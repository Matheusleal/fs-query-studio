namespace FSharpQueryStudio.Connectors

open System
open System.Data
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open FSharpQueryStudio.Protocol

[<AutoOpen>]
module SqliteConnectorExtensions =
    type ConnectorKind with
        static member Sqlite = ConnectorKind("Sqlite")

    type ConnectorCategory with
        static member Sql = ConnectorCategory("sql", "Db", "FSharpQueryStudio.Connectors.Connections.SqlCategory")

/// SQLite connector — Section 10. `filePath` names a local `.db` file;
/// SQLite creates it automatically on first connection if it doesn't
/// exist yet.
type SqliteConnector(filePath: string) =
    let connectionString =
        let csb = SqliteConnectionStringBuilder(DataSource = filePath, Pooling = false)
        csb.ToString()

    /// Binds every `(name, value)` pair as a real `SqliteParameter` —
    /// never string-concatenated into `sql`. The structural guarantee
    /// C.11's cross-connector regression test checks.
    static member BindParameters(command: SqliteCommand, parameters: (string * obj) list) =
        for name, value in parameters do
            command.Parameters.AddWithValue(name, (if isNull value then box DBNull.Value else value))
            |> ignore

    /// Runs `sql` with `parameters` bound, returning columns immediately
    /// and rows as a re-enumerable lazy sequence that manages connection/reader
    /// per enumerator lifecycle.
    member _.ExecuteQuery (sql: string) (parameters: (string * obj) list) : ConnectorQueryResult =
        let initialConn = new SqliteConnection(connectionString)
        initialConn.Open()
        let initialCmd = initialConn.CreateCommand()
        initialCmd.CommandText <- sql
        SqliteConnector.BindParameters(initialCmd, parameters)
        let initialReader = initialCmd.ExecuteReader(CommandBehavior.CloseConnection)
        let columns = [ for i in 0 .. initialReader.FieldCount - 1 -> initialReader.GetName(i) ]
        let fieldCount = initialReader.FieldCount
        initialReader.Dispose()
        initialCmd.Dispose()
        initialConn.Dispose()

        let rows =
            if fieldCount = 0 then
                Seq.empty
            else
                Seq.delay (fun () ->
                    let conn = new SqliteConnection(connectionString)
                    conn.Open()
                    let cmd = conn.CreateCommand()
                    cmd.CommandText <- sql
                    SqliteConnector.BindParameters(cmd, parameters)
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
            let connection = new SqliteConnection(connectionString)
            connection.Open()
            let command = connection.CreateCommand()
            command.CommandText <- sql
            SqliteConnector.BindParameters(command, parameters)
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

    /// Every table SQLite tracks in `sqlite_master`, plus each table's
    /// columns via `PRAGMA table_info` — Section 10's "schema discovery".
    /// Internal SQLite tables (`sqlite_%`) are excluded.
    member _.DiscoverSchemaAsync(ct: CancellationToken) : Task<ConnectorTable list> =
        task {
            use connection = new SqliteConnection(connectionString)
            do! connection.OpenAsync(ct)

            use listTables = connection.CreateCommand()

            listTables.CommandText <-
                "select name from sqlite_master where type in ('table', 'view') and name not like 'sqlite\\_%' escape '\\'"

            let tableNames = ResizeArray<string>()
            use! tableReader = listTables.ExecuteReaderAsync(ct)

            let mutable hasTableRow = true

            while hasTableRow do
                let! more = tableReader.ReadAsync(ct)

                if more then
                    tableNames.Add(tableReader.GetString(0))
                else
                    hasTableRow <- false

            let tables = ResizeArray<ConnectorTable>()

            for tableName in tableNames do
                use pragma = connection.CreateCommand()
                pragma.CommandText <- $"PRAGMA table_info('{tableName}')"
                use! pragmaReader = pragma.ExecuteReaderAsync(ct)
                let columns = ResizeArray<ConnectorColumn>()
                let mutable hasColumnRow = true

                while hasColumnRow do
                    let! more = pragmaReader.ReadAsync(ct)

                    if more then
                        columns.Add(
                            { Name = pragmaReader.GetString(1)
                              DataType = pragmaReader.GetString(2) }
                        )
                    else
                        hasColumnRow <- false

                tables.Add({ Name = tableName; Columns = columns |> List.ofSeq })

            return tables |> List.ofSeq
        }

    interface IConnector with
        member _.Kind = ConnectorKind.Sqlite

        member _.TestConnectionAsync(ct: CancellationToken) : Task<bool> =
            task {
                try
                    use connection = new SqliteConnection(connectionString)
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

