namespace FSharpQueryStudio.Connectors

open System
open System.Data
open System.Threading
open System.Threading.Tasks
open Npgsql
open FSharpQueryStudio.Protocol

[<AutoOpen>]
module PostgreSqlConnectorExtensions =
    type ConnectorKind with
        static member PostgreSql = ConnectorKind("PostgreSql")

    type ConnectorCategory with
        static member Sql = ConnectorCategory("sql", "Db", "FSharpQueryStudio.Connectors.Connections.SqlCategory")

/// PostgreSQL connector — Section 10, same capability shape as
/// `SqliteConnector`: connection, parameterized commands, reader-based
/// paging, schema discovery.
type PostgreSqlConnector(connectionString: string) =

    /// Binds every `(name, value)` pair as a real `NpgsqlParameter` —
    /// never string-concatenated into `sql`. The structural guarantee
    /// C.11's cross-connector regression test checks.
    static member BindParameters(command: NpgsqlCommand, parameters: (string * obj) list) =
        for name, value in parameters do
            command.Parameters.AddWithValue(name, (if isNull value then box DBNull.Value else value))
            |> ignore

    /// Runs `sql` with `parameters` bound, returning columns immediately
    /// and rows as a re-enumerable lazy sequence that manages connection/reader
    /// per enumerator lifecycle.
    member _.ExecuteQuery (sql: string) (parameters: (string * obj) list) : ConnectorQueryResult =
        let initialConn = new NpgsqlConnection(connectionString)
        initialConn.Open()
        let initialCmd = initialConn.CreateCommand()
        initialCmd.CommandText <- sql
        PostgreSqlConnector.BindParameters(initialCmd, parameters)
        let initialReader = initialCmd.ExecuteReader(CommandBehavior.CloseConnection)
        let columns = [ for i in 0 .. initialReader.FieldCount - 1 -> initialReader.GetName(i) ]
        let mutable firstRun = Some (initialConn, initialCmd, initialReader)

        let rows =
            Seq.delay (fun () ->
                match firstRun with
                | Some (conn, cmd, reader) ->
                    firstRun <- None
                    seq {
                        use _c = conn
                        use _cmd = cmd
                        use _r = reader
                        while reader.Read() do
                            yield [| for i in 0 .. reader.FieldCount - 1 -> reader.GetValue(i) |]
                    }
                | None ->
                    let conn = new NpgsqlConnection(connectionString)
                    conn.Open()
                    let cmd = conn.CreateCommand()
                    cmd.CommandText <- sql
                    PostgreSqlConnector.BindParameters(cmd, parameters)
                    let reader = cmd.ExecuteReader(CommandBehavior.CloseConnection)
                    seq {
                        use _c = conn
                        use _cmd = cmd
                        use _r = reader
                        while reader.Read() do
                            yield [| for i in 0 .. reader.FieldCount - 1 -> reader.GetValue(i) |]
                    }
            )

        { Columns = columns; Rows = rows }

    /// Executes `sql` and streams rows directly into a typed F# record without creating intermediate obj[] arrays per row.
    member this.ExecuteReaderStreamWithColumns<'T> (sql: string) (parameters: (string * obj) list) (mapperFactory: string list -> (System.Data.IDataRecord -> 'T)) : 'T seq =
        Seq.delay (fun () ->
            let connection = new NpgsqlConnection(connectionString)
            connection.Open()
            let command = connection.CreateCommand()
            command.CommandText <- sql
            PostgreSqlConnector.BindParameters(command, parameters)
            let reader = command.ExecuteReader(CommandBehavior.CloseConnection)
            seq {
                use _conn = connection
                use _cmd = command
                use _reader = reader
                let columns = [ for i in 0 .. reader.FieldCount - 1 -> reader.GetName(i) ]
                let mapper = mapperFactory columns
                let record = reader :> System.Data.IDataRecord

                while reader.Read() do
                    yield mapper record
            }
        )

    /// Executes `sql` and streams rows directly into a typed F# record using a pre-configured mapper.
    member this.ExecuteReaderStream<'T> (sql: string) (parameters: (string * obj) list) (mapper: System.Data.IDataRecord -> 'T) : 'T seq =
        this.ExecuteReaderStreamWithColumns sql parameters (fun _ -> mapper)

    /// Every base table, view and column across user schemas, via
    /// `information_schema` — Section 10's "schema discovery".
    member _.DiscoverSchemaAsync(ct: CancellationToken) : Task<ConnectorTable list> =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync(ct)
            use command = connection.CreateCommand()

            command.CommandText <-
                "select t.table_schema, t.table_name, c.column_name, c.data_type \
                 from information_schema.tables t \
                 join information_schema.columns c \
                   on c.table_name = t.table_name and c.table_schema = t.table_schema \
                 where t.table_type in ('BASE TABLE', 'VIEW') \
                   and t.table_schema not in ('information_schema', 'pg_catalog') \
                   and t.table_schema not like 'pg_%' \
                 order by t.table_schema, t.table_name, c.ordinal_position"

            use! reader = command.ExecuteReaderAsync(ct)
            let rows = ResizeArray<string * string * string * string>()
            let mutable hasRow = true

            while hasRow do
                let! more = reader.ReadAsync(ct)

                if more then
                    rows.Add(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))
                else
                    hasRow <- false

            return
                rows
                |> Seq.groupBy (fun (schema, tableName, _, _) ->
                    if schema = "public" then tableName
                    else $"{schema}.{tableName}")
                |> Seq.map (fun (displayName, tableRows) ->
                    { Name = displayName
                      Columns =
                        tableRows
                        |> Seq.map (fun (_, _, columnName, dataType) -> ({ Name = columnName; DataType = dataType }: ConnectorColumn))
                        |> List.ofSeq }: ConnectorTable)
                |> List.ofSeq
        }

    interface IConnector with
        member _.Kind = ConnectorKind.PostgreSql

        member _.TestConnectionAsync(ct: CancellationToken) : Task<bool> =
            task {
                try
                    use connection = new NpgsqlConnection(connectionString)
                    do! connection.OpenAsync(ct)
                    return true
                with _ ->
                    return false
            }

        member this.DiscoverSchemaAsync(ct: CancellationToken) = this.DiscoverSchemaAsync(ct)

    member _.ConnectionString = connectionString

    interface IQueryableConnector with
        member _.ConnectionString = connectionString
        member this.ExecuteQuery (sql: string) (parameters: (string * obj) list) = this.ExecuteQuery sql parameters

    interface IStreamingQueryableConnector with
        member this.ExecuteReaderStream (sql: string) (parameters: (string * obj) list) (mapper: System.Data.IDataRecord -> 'T) : 'T seq =
            this.ExecuteReaderStream sql parameters mapper
        member this.ExecuteReaderStreamWithColumns (sql: string) (parameters: (string * obj) list) (mapperFactory: string list -> (System.Data.IDataRecord -> 'T)) : 'T seq =
            this.ExecuteReaderStreamWithColumns sql parameters mapperFactory

