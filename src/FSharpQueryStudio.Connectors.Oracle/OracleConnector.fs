namespace FSharpQueryStudio.Connectors

open System
open System.Data
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Oracle.ManagedDataAccess.Client
open FSharpQueryStudio.Protocol

[<AutoOpen>]
module OracleConnectorExtensions =
    type ConnectorKind with
        static member Oracle = ConnectorKind("Oracle")

    type ConnectorCategory with
        static member Sql = ConnectorCategory("sql", "Db", "FSharpQueryStudio.Connectors.Connections.SqlCategory")

/// Oracle connector — connection, parameterized commands, reader-based paging, schema discovery.
type OracleConnector(connectionString: string) =

    /// Adapts `@param` syntax to Oracle `:param` bind variable syntax if needed.
    static member PrepareSql(sql: string) =
        if String.IsNullOrEmpty sql || not (sql.Contains("@")) then sql
        else Regex.Replace(sql, @"@([a-zA-Z0-9_]+)", ":$1")

    /// Binds every `(name, value)` pair as a real `OracleParameter` — never string-concatenated into `sql`.
    static member BindParameters(command: OracleCommand, parameters: (string * obj) list) =
        command.BindByName <- true
        for name, value in parameters do
            let cleanName = name.TrimStart('@', ':')
            let paramValue = if isNull value then box DBNull.Value else value
            let p = new OracleParameter(cleanName, paramValue)
            command.Parameters.Add(p) |> ignore

    /// Runs `sql` with `parameters` bound, returning columns immediately
    /// and rows as a re-enumerable lazy sequence that manages connection/reader
    /// per enumerator lifecycle.
    member _.ExecuteQuery (sql: string) (parameters: (string * obj) list) : ConnectorQueryResult =
        let targetSql = OracleConnector.PrepareSql sql
        let initialConn = new OracleConnection(connectionString)
        initialConn.Open()
        let initialCmd = initialConn.CreateCommand()
        initialCmd.CommandText <- targetSql
        OracleConnector.BindParameters(initialCmd, parameters)
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
                    let conn = new OracleConnection(connectionString)
                    conn.Open()
                    let cmd = conn.CreateCommand()
                    cmd.CommandText <- targetSql
                    OracleConnector.BindParameters(cmd, parameters)
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
        let targetSql = OracleConnector.PrepareSql sql
        Seq.delay (fun () ->
            let connection = new OracleConnection(connectionString)
            connection.Open()
            let command = connection.CreateCommand()
            command.CommandText <- targetSql
            OracleConnector.BindParameters(command, parameters)
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

    /// Every base table, view and column across user schemas, via data dictionary views.
    member _.DiscoverSchemaAsync(ct: CancellationToken) : Task<ConnectorTable list> =
        task {
            use connection = new OracleConnection(connectionString)
            do! connection.OpenAsync(ct)
            use command = connection.CreateCommand()

            command.CommandText <-
                "select t.owner, t.table_name, c.column_name, \
                        case \
                          when c.data_type = 'NUMBER' and nvl(c.data_scale, 0) = 0 and nvl(c.data_precision, 38) <= 9 then 'int' \
                          when c.data_type = 'NUMBER' and nvl(c.data_scale, 0) = 0 and nvl(c.data_precision, 38) <= 18 then 'bigint' \
                          when c.data_type = 'NUMBER' and nvl(c.data_scale, 0) = 0 then 'bigint' \
                          when c.data_type = 'NUMBER' then 'decimal' \
                          else c.data_type \
                        end as resolved_type \
                 from ( \
                     select owner, table_name from all_tables where owner = USER and table_name not like '%$%' and table_name not like 'BIN$%' and table_name not like 'MVIEW$%' \
                     union all \
                     select owner, view_name as table_name from all_views where owner = USER and view_name not like '%$%' and view_name not like 'BIN$%' and view_name not like 'MVIEW$%' \
                 ) t \
                 join all_tab_columns c on c.table_name = t.table_name and c.owner = t.owner \
                 where c.column_name not like '%$%' and c.column_name not like '%#%' \
                 order by t.table_name, c.column_id"

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
                |> Seq.groupBy (fun (_owner, tableName, _, _) -> tableName)
                |> Seq.map (fun (displayName, tableRows) ->
                    { Name = displayName
                      Columns =
                        tableRows
                        |> Seq.map (fun (_, _, columnName, dataType) -> ({ Name = columnName; DataType = dataType }: ConnectorColumn))
                        |> List.ofSeq }: ConnectorTable)
                |> List.ofSeq
        }

    interface IConnector with
        member _.Kind = ConnectorKind.Oracle

        member _.TestConnectionAsync(ct: CancellationToken) : Task<bool> =
            task {
                try
                    use connection = new OracleConnection(connectionString)
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
