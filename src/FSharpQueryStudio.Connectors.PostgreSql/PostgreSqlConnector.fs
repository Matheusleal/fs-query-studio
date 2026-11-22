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
        static member Sql =
            ConnectorCategory("sql", "Db", "FSharpQueryStudio.Connectors.Connections.SqlCategory")

/// PostgreSQL connector — Section 10, same capability shape as
/// `SqliteConnector`: connection, parameterized commands, reader-based
/// paging, schema discovery.
type PostgreSqlConnector(connectionString: string) =

    static let tryRewriteCrossDbSql (connStr: string) (sql: string) : (string * string) option =
        if String.IsNullOrWhiteSpace sql then
            None
        else
            let re3 =
                System.Text.RegularExpressions.Regex(
                    @"(?i)\b(FROM|JOIN)\s+([a-zA-Z_][a-zA-Z0-9_]*)\.([a-zA-Z_][a-zA-Z0-9_]*)\.([a-zA-Z_][a-zA-Z0-9_]*)\b"
                )

            let m3 = re3.Match(sql)

            if m3.Success then
                let dbName = m3.Groups.[2].Value

                let targetConnStr =
                    try
                        let csb = NpgsqlConnectionStringBuilder(connStr)
                        csb.Database <- dbName
                        csb.ToString()
                    with _ ->
                        connStr

                let rewritten =
                    re3.Replace(
                        sql,
                        System.Text.RegularExpressions.MatchEvaluator(fun m ->
                            let kw = m.Groups.[1].Value
                            let d = m.Groups.[2].Value
                            let s = m.Groups.[3].Value
                            let t = m.Groups.[4].Value

                            if String.Equals(d, dbName, StringComparison.OrdinalIgnoreCase) then
                                if String.Equals(s, "public", StringComparison.OrdinalIgnoreCase) then
                                    $"{kw} {t}"
                                else
                                    $"{kw} {s}.{t}"
                            else
                                m.Value)
                    )

                Some(targetConnStr, rewritten)
            else
                let re2 =
                    System.Text.RegularExpressions.Regex(
                        @"(?i)\b(FROM|JOIN)\s+([a-zA-Z_][a-zA-Z0-9_]*)\.([a-zA-Z_][a-zA-Z0-9_]*)\b"
                    )

                let m2 = re2.Match(sql)

                if m2.Success then
                    let dbName = m2.Groups.[2].Value

                    let targetConnStr =
                        try
                            let csb = NpgsqlConnectionStringBuilder(connStr)
                            csb.Database <- dbName
                            csb.ToString()
                        with _ ->
                            connStr

                    let rewritten =
                        re2.Replace(
                            sql,
                            System.Text.RegularExpressions.MatchEvaluator(fun m ->
                                let kw = m.Groups.[1].Value
                                let d = m.Groups.[2].Value
                                let t = m.Groups.[3].Value

                                if String.Equals(d, dbName, StringComparison.OrdinalIgnoreCase) then
                                    $"{kw} {t}"
                                else
                                    m.Value)
                        )

                    Some(targetConnStr, rewritten)
                else
                    None

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
        let mutable effectiveConnStr = connectionString
        let mutable effectiveSql = sql

        let initialConn, initialCmd, initialReader =
            let conn = new NpgsqlConnection(effectiveConnStr)
            conn.Open()
            let cmd = conn.CreateCommand()
            cmd.CommandText <- effectiveSql
            PostgreSqlConnector.BindParameters(cmd, parameters)

            try
                let r = cmd.ExecuteReader(CommandBehavior.CloseConnection)
                conn, cmd, r
            with
            | :? PostgresException as pex when
                pex.SqlState = "3F000" || pex.SqlState = "42P01" || pex.SqlState = "0A000"
                ->
                cmd.Dispose()
                conn.Dispose()

                match tryRewriteCrossDbSql connectionString sql with
                | Some(altConnStr, altSql) ->
                    effectiveConnStr <- altConnStr
                    effectiveSql <- altSql
                    let retryConn = new NpgsqlConnection(effectiveConnStr)
                    retryConn.Open()
                    let retryCmd = retryConn.CreateCommand()
                    retryCmd.CommandText <- effectiveSql
                    PostgreSqlConnector.BindParameters(retryCmd, parameters)
                    let retryReader = retryCmd.ExecuteReader(CommandBehavior.CloseConnection)
                    retryConn, retryCmd, retryReader
                | None -> reraise ()
            | _ ->
                cmd.Dispose()
                conn.Dispose()
                reraise ()

        let columns =
            [ for i in 0 .. initialReader.FieldCount - 1 -> initialReader.GetName(i) ]

        let mutable firstRun = Some(initialConn, initialCmd, initialReader)

        let rows =
            Seq.delay (fun () ->
                match firstRun with
                | Some(conn, cmd, reader) ->
                    firstRun <- None

                    seq {
                        use _c = conn
                        use _cmd = cmd
                        use _r = reader

                        while reader.Read() do
                            yield [| for i in 0 .. reader.FieldCount - 1 -> reader.GetValue(i) |]
                    }
                | None ->
                    let conn = new NpgsqlConnection(effectiveConnStr)
                    conn.Open()
                    let cmd = conn.CreateCommand()
                    cmd.CommandText <- effectiveSql
                    PostgreSqlConnector.BindParameters(cmd, parameters)
                    let reader = cmd.ExecuteReader(CommandBehavior.CloseConnection)

                    seq {
                        use _c = conn
                        use _cmd = cmd
                        use _r = reader

                        while reader.Read() do
                            yield [| for i in 0 .. reader.FieldCount - 1 -> reader.GetValue(i) |]
                    })

        { Columns = columns; Rows = rows }

    /// Executes `sql` and streams rows directly into a typed F# record without creating intermediate obj[] arrays per row.
    member this.ExecuteReaderStreamWithColumns<'T>
        (sql: string)
        (parameters: (string * obj) list)
        (mapperFactory: string list -> (System.Data.IDataRecord -> 'T))
        : 'T seq =
        Seq.delay (fun () ->
            let connection, command, reader =
                let conn = new NpgsqlConnection(connectionString)
                conn.Open()
                let cmd = conn.CreateCommand()
                cmd.CommandText <- sql
                PostgreSqlConnector.BindParameters(cmd, parameters)

                try
                    let r = cmd.ExecuteReader(CommandBehavior.CloseConnection)
                    conn, cmd, r
                with
                | :? PostgresException as pex when
                    pex.SqlState = "3F000" || pex.SqlState = "42P01" || pex.SqlState = "0A000"
                    ->
                    cmd.Dispose()
                    conn.Dispose()

                    match tryRewriteCrossDbSql connectionString sql with
                    | Some(altConnStr, altSql) ->
                        let retryConn = new NpgsqlConnection(altConnStr)
                        retryConn.Open()
                        let retryCmd = retryConn.CreateCommand()
                        retryCmd.CommandText <- altSql
                        PostgreSqlConnector.BindParameters(retryCmd, parameters)
                        let retryReader = retryCmd.ExecuteReader(CommandBehavior.CloseConnection)
                        retryConn, retryCmd, retryReader
                    | None -> reraise ()
                | _ ->
                    cmd.Dispose()
                    conn.Dispose()
                    reraise ()

            seq {
                use _conn = connection
                use _cmd = command
                use _reader = reader
                let columns = [ for i in 0 .. reader.FieldCount - 1 -> reader.GetName(i) ]
                let mapper = mapperFactory columns
                let record = reader :> System.Data.IDataRecord

                while reader.Read() do
                    yield mapper record
            })

    /// Executes `sql` and streams rows directly into a typed F# record using a pre-configured mapper.
    member this.ExecuteReaderStream<'T>
        (sql: string)
        (parameters: (string * obj) list)
        (mapper: System.Data.IDataRecord -> 'T)
        : 'T seq =
        this.ExecuteReaderStreamWithColumns sql parameters (fun _ -> mapper)

    /// Every base table, view and column across all accessible databases and user schemas.
    member _.DiscoverSchemaAsync(ct: CancellationToken) : Task<ConnectorTable list> =
        task {
            use connection = new NpgsqlConnection(connectionString)
            do! connection.OpenAsync(ct)

            let dbNames = ResizeArray<string>()

            try
                use dbCmd = connection.CreateCommand()

                dbCmd.CommandText <-
                    "SELECT datname FROM pg_database WHERE datistemplate = false AND datallowconn = true ORDER BY datname"

                use! dbReader = dbCmd.ExecuteReaderAsync(ct)
                let mutable moreDbs = true

                while moreDbs do
                    let! hasNext = dbReader.ReadAsync(ct)

                    if hasNext then
                        dbNames.Add(dbReader.GetString(0))
                    else
                        moreDbs <- false
            with _ ->
                ()

            if dbNames.Count = 0 && not (String.IsNullOrWhiteSpace connection.Database) then
                dbNames.Add(connection.Database)

            let rows = ResizeArray<string * string * string * string * string>()

            let discoverForConnection (dbName: string) (conn: NpgsqlConnection) =
                task {
                    use command = conn.CreateCommand()

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
                    let mutable hasRow = true

                    while hasRow do
                        let! more = reader.ReadAsync(ct)

                        if more then
                            rows.Add(
                                (dbName,
                                 reader.GetString(0),
                                 reader.GetString(1),
                                 reader.GetString(2),
                                 reader.GetString(3))
                            )
                        else
                            hasRow <- false
                }

            for dbName in dbNames do
                try
                    if String.Equals(dbName, connection.Database, StringComparison.OrdinalIgnoreCase) then
                        do! discoverForConnection dbName connection
                    else
                        let csb = NpgsqlConnectionStringBuilder(connectionString)
                        csb.Database <- dbName
                        use dbConn = new NpgsqlConnection(csb.ToString())
                        do! dbConn.OpenAsync(ct)
                        do! discoverForConnection dbName dbConn
                with _ ->
                    ()

            return
                rows
                |> Seq.groupBy (fun (dbName, schema, tableName, _, _) ->
                    if schema = "public" then
                        $"{dbName}.{tableName}"
                    else
                        $"{dbName}.{schema}.{tableName}")
                |> Seq.map (fun (displayName, tableRows) ->
                    {
                        Name = displayName
                        Columns =
                            tableRows
                            |> Seq.map (fun (_, _, _, columnName, dataType) ->
                                ({
                                    Name = columnName
                                    DataType = dataType
                                }
                                : ConnectorColumn))
                            |> List.ofSeq
                    }
                    : ConnectorTable)
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
        member this.ExecuteReaderStream
            (sql: string)
            (parameters: (string * obj) list)
            (mapper: System.Data.IDataRecord -> 'T)
            : 'T seq =
            this.ExecuteReaderStream sql parameters mapper

        member this.ExecuteReaderStreamWithColumns
            (sql: string)
            (parameters: (string * obj) list)
            (mapperFactory: string list -> (System.Data.IDataRecord -> 'T))
            : 'T seq =
            this.ExecuteReaderStreamWithColumns sql parameters mapperFactory
