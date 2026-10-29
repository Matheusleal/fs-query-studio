namespace FSharpQueryStudio.Connectors

open System
open System.Data
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.SqlClient
open FSharpQueryStudio.Protocol

/// SQL Server connector — Section 10, same capability shape as
/// `SqliteConnector`: connection, parameterized commands, reader-based
/// paging, schema discovery.
type SqlServerConnector(connectionString: string) =

    /// Binds every `(name, value)` pair as a real `SqlParameter` — never
    /// string-concatenated into `sql`. The structural guarantee C.11's
    /// cross-connector regression test checks.
    static member BindParameters(command: SqlCommand, parameters: (string * obj) list) =
        for name, value in parameters do
            command.Parameters.AddWithValue(name, (if isNull value then box DBNull.Value else value))
            |> ignore

    /// Runs `sql` with `parameters` bound, returning columns immediately
    /// and rows as a re-enumerable lazy sequence that manages connection/reader
    /// per enumerator lifecycle.
    member _.ExecuteQuery (sql: string) (parameters: (string * obj) list) : ConnectorQueryResult =
        let initialConn = new SqlConnection(connectionString)
        initialConn.Open()
        let initialCmd = initialConn.CreateCommand()
        initialCmd.CommandText <- sql
        SqlServerConnector.BindParameters(initialCmd, parameters)
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
                    let conn = new SqlConnection(connectionString)
                    conn.Open()
                    let cmd = conn.CreateCommand()
                    cmd.CommandText <- sql
                    SqlServerConnector.BindParameters(cmd, parameters)
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
            let connection = new SqlConnection(connectionString)
            connection.Open()
            let command = connection.CreateCommand()
            command.CommandText <- sql
            SqlServerConnector.BindParameters(command, parameters)
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

    /// Every base table, view and column via `INFORMATION_SCHEMA` — Section
    /// 10's "schema discovery".
    member _.DiscoverSchemaAsync(ct: CancellationToken) : Task<ConnectorTable list> =
        task {
            use connection = new SqlConnection(connectionString)
            do! connection.OpenAsync(ct)
            use command = connection.CreateCommand()

            command.CommandText <-
                "select t.TABLE_SCHEMA, t.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE \
                 from INFORMATION_SCHEMA.TABLES t \
                 join INFORMATION_SCHEMA.COLUMNS c \
                   on c.TABLE_NAME = t.TABLE_NAME and c.TABLE_SCHEMA = t.TABLE_SCHEMA \
                 where t.TABLE_TYPE in ('BASE TABLE', 'VIEW') \
                 order by t.TABLE_SCHEMA, t.TABLE_NAME, c.ORDINAL_POSITION"

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
                    if String.Equals(schema, "dbo", StringComparison.OrdinalIgnoreCase) then tableName
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
        member _.Kind = ConnectorKind.SqlServer

        member _.TestConnectionAsync(ct: CancellationToken) : Task<bool> =
            task {
                try
                    use connection = new SqlConnection(connectionString)
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

