module SqliteConnectorTests

open System
open System.IO
open System.Threading
open Microsoft.Data.Sqlite
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors
open FSharpQueryStudio.Security
open System.Text.Json

[<Fact>]
let ``BindParameters adds every parameter as a real ADO.NET parameter, never touching the SQL text`` () =
    use command = new SqliteCommand()

    let originalSql =
        "select * from Orders where CustomerId = @customerId and Total > @minTotal"

    command.CommandText <- originalSql

    SqliteConnector.BindParameters(command, [ "@customerId", box 42; "@minTotal", box 100.0 ])

    Assert.Equal(2, command.Parameters.Count)
    Assert.Equal(box 42, command.Parameters.["@customerId"].Value)
    Assert.Equal(box 100.0, command.Parameters.["@minTotal"].Value)
    Assert.Equal(originalSql, command.CommandText)

[<Fact>]
let ``BindParameters binds a null value as DBNull rather than a literal null`` () =
    use command = new SqliteCommand()

    SqliteConnector.BindParameters(command, [ "@note", null ])

    Assert.Equal(box DBNull.Value, command.Parameters.["@note"].Value)

/// A fresh temp SQLite file with one `Orders` table and two rows, deleted
/// when the caller's `use` scope ends.
type private TempSqliteFile() =
    let path =
        Path.Combine(Path.GetTempPath(), "fqs-sqlite-" + Guid.NewGuid().ToString("N") + ".db")

    do
        use connection =
            new SqliteConnection(SqliteConnectionStringBuilder(DataSource = path).ToString())

        connection.Open()
        use create = connection.CreateCommand()
        create.CommandText <- "create table Orders (Id integer primary key, CustomerId integer, Total real)"
        create.ExecuteNonQuery() |> ignore
        use insert = connection.CreateCommand()
        insert.CommandText <- "insert into Orders (Id, CustomerId, Total) values (1, 42, 100.0), (2, 43, 50.0)"
        insert.ExecuteNonQuery() |> ignore

    member _.Path = path

    interface IDisposable with
        member _.Dispose() =
            // Microsoft.Data.Sqlite pools connections by default, which
            // keeps a native handle open on `path` past `connection`'s
            // own Dispose - clear the pool first or deleting the file
            // races a still-open handle.
            SqliteConnection.ClearAllPools()

            try
                if File.Exists path then
                    File.Delete path
            with _ ->
                ()

[<Fact>]
let ``ExecuteQuery returns only the rows matching a bound parameter, reading through a live SqliteDataReader`` () =
    use file = new TempSqliteFile()
    let connector = SqliteConnector(file.Path)

    let result =
        connector.ExecuteQuery
            "select Id, CustomerId, Total from Orders where CustomerId = @customerId"
            [ "@customerId", box 42 ]

    Assert.Equal<string list>([ "Id"; "CustomerId"; "Total" ], result.Columns)
    let rows = result.Rows |> Seq.toList
    Assert.Equal<obj list>([ box 1L; box 42L; box 100.0 ], rows |> List.exactlyOne |> Array.toList)

[<Fact>]
let ``TestConnectionAsync returns true for a reachable SQLite file`` () =
    task {
        use file = new TempSqliteFile()
        let connector = SqliteConnector(file.Path) :> IConnector

        let! connected = connector.TestConnectionAsync(CancellationToken.None)

        Assert.True(connected)
    }

[<Fact>]
let ``DiscoverSchemaAsync reports the Orders table and its columns`` () =
    task {
        use file = new TempSqliteFile()
        let connector = SqliteConnector(file.Path) :> IConnector

        let! tables = connector.DiscoverSchemaAsync(CancellationToken.None)

        let orders = tables |> List.find (fun t -> t.Name = "Orders")
        let columnNames = orders.Columns |> List.map (fun c -> c.Name) |> List.sort
        Assert.Equal<string list>([ "CustomerId"; "Id"; "Total" ], columnNames)
    }

[<Fact>]
let ``SqliteConnector reports its Kind as Sqlite`` () =
    use file = new TempSqliteFile()
    let connector = SqliteConnector(file.Path) :> IConnector

    Assert.Equal(ConnectorKind.Sqlite, connector.Kind)

[<Fact>]
let ``ExecuteQuery rows sequence can be enumerated multiple times without throwing ObjectDisposedException`` () =
    use file = new TempSqliteFile()
    let connector = SqliteConnector(file.Path)

    let result = connector.ExecuteQuery "select Id, CustomerId, Total from Orders" []

    // Primeira enumeração: contar elementos
    let count = result.Rows |> Seq.length
    Assert.Equal(2, count)

    // Segunda enumeração: iterar e mapear
    let ids = result.Rows |> Seq.map (fun row -> Convert.ToInt32(row.[0])) |> Seq.toList
    Assert.Equal<int list>([ 1; 2 ], ids)

    // Terceira enumeração com ExecuteReaderStream
    let streaming = connector :> IStreamingQueryableConnector

    let streamResult =
        streaming.ExecuteReaderStream "select Id, CustomerId, Total from Orders order by Id" [] (fun record ->
            record.GetInt32(0))
        |> Seq.toList

    Assert.Equal<int list>([ 1; 2 ], streamResult)

[<Fact>]
let ``ExecuteQuery executes query only once on first enumeration without duplicate execution`` () =
    use file = new TempSqliteFile()

    use connection =
        new SqliteConnection(SqliteConnectionStringBuilder(DataSource = file.Path).ToString())

    connection.Open()
    use create = connection.CreateCommand()
    create.CommandText <- "create table ExecAudit (Id integer primary key autoincrement, Val text)"
    create.ExecuteNonQuery() |> ignore
    SqliteConnection.ClearAllPools()

    let connector = SqliteConnector(file.Path)

    let result =
        connector.ExecuteQuery "insert into ExecAudit (Val) values ('hit') returning Id, Val" []

    Assert.Equal<string list>([ "Id"; "Val" ], result.Columns)

    // Primeira enumeração dos rows deve consumir o leitor inicial já aberto sem re-executar a query
    let rowsFirst = result.Rows |> Seq.toArray
    Assert.Equal(1, rowsFirst.Length)
    Assert.Equal(box 1L, rowsFirst.[0].[0])
    Assert.Equal(box "hit", rowsFirst.[0].[1])

    // Verifica no banco que a query rodou exatamente 1 vez
    use verifyConn =
        new SqliteConnection(SqliteConnectionStringBuilder(DataSource = file.Path).ToString())

    verifyConn.Open()
    use countCmd = verifyConn.CreateCommand()
    countCmd.CommandText <- "select count(*) from ExecAudit"
    let totalExecutions = Convert.ToInt32(countCmd.ExecuteScalar())
    Assert.Equal(1, totalExecutions)

[<Fact>]
let ``ExecuteQuery supports multiple re-enumerations of Rows returning identical data without error`` () =
    use file = new TempSqliteFile()
    let connector = SqliteConnector(file.Path)

    let result = connector.ExecuteQuery "select 1 as A, 2 as B union all select 3, 4" []

    Assert.Equal<string list>([ "A"; "B" ], result.Columns)

    // Iteração 1 (usa firstRun)
    let firstPass = result.Rows |> Seq.toArray
    Assert.Equal(2, firstPass.Length)
    Assert.Equal<obj list>([ box 1L; box 2L ], firstPass.[0] |> Array.toList)
    Assert.Equal<obj list>([ box 3L; box 4L ], firstPass.[1] |> Array.toList)

    // Iteração 2 (re-executa nova conexão em None)
    let secondPass = result.Rows |> Seq.toArray
    Assert.Equal(2, secondPass.Length)
    Assert.Equal<obj list>([ box 1L; box 2L ], secondPass.[0] |> Array.toList)
    Assert.Equal<obj list>([ box 3L; box 4L ], secondPass.[1] |> Array.toList)

    // Iteração 3 (garante estabilidade em múltiplas re-enumerações)
    let thirdPass = result.Rows |> Seq.map (fun r -> r.[0]) |> Seq.toList
    Assert.Equal<obj list>([ box 1L; box 3L ], thirdPass)

[<Fact>]
let ``ExecuteQuery with zero fieldCount immediately disposes and returns empty rows`` () =
    use file = new TempSqliteFile()
    let connector = SqliteConnector(file.Path)
    let result = connector.ExecuteQuery "create table EmptyTest (Id int)" []
    Assert.Empty(result.Columns)
    Assert.Empty(result.Rows)

[<Fact>]
let ``TestConnectionAsync returns false for invalid connection string scenario 1`` () =
    task {
        let badConn = SqliteConnector("Data Source=http://invalid-sqlite-path/nonexistent.db;Mode=ReadOnly") :> IConnector
        let! ok = badConn.TestConnectionAsync(CancellationToken.None)
        Assert.False(ok)
    }

[<Fact>]
let ``TestConnectionAsync returns false for invalid directory file scenario 2`` () =
    task {
        let invalidPath = Path.Combine(Path.GetTempPath(), $"fqs-bad-sqlite-{Guid.NewGuid():N}", "nested", "file.db")
        let badConn = SqliteConnector($"Data Source={invalidPath};Mode=ReadOnly") :> IConnector
        let! ok = badConn.TestConnectionAsync(CancellationToken.None)
        Assert.False(ok)
    }

[<Fact>]
let ``StagedEngineDatabasePathResolver property get and set scenario 1`` () =
    let prev = SqliteConnector.StagedEngineDatabasePathResolver
    try
        let dummyResolver = Some (fun () -> Some "custom_staged.db")
        SqliteConnector.StagedEngineDatabasePathResolver <- dummyResolver
        Assert.True(SqliteConnector.StagedEngineDatabasePathResolver.IsSome)
    finally
        SqliteConnector.StagedEngineDatabasePathResolver <- prev

[<Fact>]
let ``StagedEngineDatabasePathResolver property reset to None scenario 2`` () =
    let prev = SqliteConnector.StagedEngineDatabasePathResolver
    try
        SqliteConnector.StagedEngineDatabasePathResolver <- None
        Assert.True(SqliteConnector.StagedEngineDatabasePathResolver.IsNone)
    finally
        SqliteConnector.StagedEngineDatabasePathResolver <- prev

[<Fact>]
let ``ExecuteQuery throws and disposes on invalid SQL syntax scenario 1`` () =
    use file = new TempSqliteFile()
    let connector = SqliteConnector(file.Path)
    Assert.ThrowsAny<Exception>(fun () -> connector.ExecuteQuery "SELECT FROM INVALID SYNTAX" [] |> ignore) |> ignore

[<Fact>]
let ``ExecuteQuery throws and disposes on missing table without staging engine scenario 2`` () =
    use file = new TempSqliteFile()
    let connector = SqliteConnector(file.Path)
    Assert.ThrowsAny<Exception>(fun () -> connector.ExecuteQuery "SELECT * FROM NoSuchTableXYZ" [] |> ignore) |> ignore

[<Fact>]
let ``ConnectorCategory Sql property returns expected id and display name scenario 1`` () =
    let cat = ConnectorCategory.Sql
    Assert.Equal("sql", cat.Identifier)
    Assert.Equal("Db", cat.ModuleSuffix)

[<Fact>]
let ``ConnectorCategory Sql property matches constant definition scenario 2`` () =
    let cat = ConnectorCategory.Sql
    Assert.Equal("FSharpQueryStudio.Connectors.Connections.SqlCategory", cat.CategoryTypeName)

[<Fact>]
let ``SqliteConnector tryEnsureAndAttachStagedEngine when same database path scenario 1`` () =
    use file = new TempSqliteFile()
    let prev = SqliteConnector.StagedEngineDatabasePathResolver
    try
        SqliteConnector.StagedEngineDatabasePathResolver <- Some (fun () -> Some file.Path)
        let connector = SqliteConnector(file.Path)
        let result = connector.ExecuteQuery "SELECT 42 AS Answer" []
        let rows = result.Rows |> Seq.toArray
        Assert.Single(rows) |> ignore
        Assert.Equal(box 42L, rows.[0].[0])
    finally
        SqliteConnector.StagedEngineDatabasePathResolver <- prev

[<Fact>]
let ``SqliteConnector tryEnsureAndAttachStagedEngine when resolver returns None or invalid path scenario 2`` () =
    use file = new TempSqliteFile()
    let prev = SqliteConnector.StagedEngineDatabasePathResolver
    try
        // Resolver returns None inside closure
        SqliteConnector.StagedEngineDatabasePathResolver <- Some (fun () -> None)
        let connector = SqliteConnector(file.Path)
        let result1 = connector.ExecuteQuery "SELECT 1 AS One" []
        Assert.Single(result1.Rows |> Seq.toArray) |> ignore

        // Resolver returns invalid path
        SqliteConnector.StagedEngineDatabasePathResolver <- Some (fun () -> Some "invalid_db_path::???")
        let result2 = connector.ExecuteQuery "SELECT 2 AS Two" []
        Assert.Single(result2.Rows |> Seq.toArray) |> ignore
    finally
        SqliteConnector.StagedEngineDatabasePathResolver <- prev

[<Fact>]
let ``SqliteConnector Dialect property returns SqliteDialect instance scenario 1`` () =
    use file = new TempSqliteFile()
    let connector = SqliteConnector(file.Path)
    Assert.NotNull(connector.Dialect)
    Assert.Equal("Sqlite", connector.Dialect.Name)

[<Fact>]
let ``SqliteConnector Dialect property can quote identifiers scenario 2`` () =
    use file = new TempSqliteFile()
    let connector = SqliteConnector(file.Path)
    Assert.Equal("\"order\"", connector.Dialect.QuoteIdentifier "order")

[<Fact>]
let ``SqliteConnector ExecuteReaderStreamWithColumns throws and disposes on invalid SQL scenario 1`` () =
    use file = new TempSqliteFile()
    let connector = SqliteConnector(file.Path) :> IStreamingQueryableConnector
    Assert.ThrowsAny<Exception>(fun () ->
        connector.ExecuteReaderStreamWithColumns "SELECT FROM INVALID" [] (fun _ r -> r.GetInt32(0))
        |> Seq.toArray
        |> ignore)
    |> ignore

[<Fact>]
let ``SqliteConnector ExecuteReaderStream throws on missing table without staging scenario 2`` () =
    use file = new TempSqliteFile()
    let connector = SqliteConnector(file.Path) :> IStreamingQueryableConnector
    Assert.ThrowsAny<Exception>(fun () ->
        connector.ExecuteReaderStream "SELECT * FROM MissingTableXYZ" [] (fun r -> r.GetInt32(0))
        |> Seq.toArray
        |> ignore)
    |> ignore

[<Fact>]
let ``SqliteConnector attaches staged engine dynamically on no such table and re-enumerates successfully scenario 1`` () =
    let mainPath = Path.Combine(Path.GetTempPath(), $"fqs-main-stage-{Guid.NewGuid():N}.db")
    use stagedFile = new TempSqliteFile()
    let prev = SqliteConnector.StagedEngineDatabasePathResolver
    try
        SqliteConnector.StagedEngineDatabasePathResolver <- Some (fun () -> Some stagedFile.Path)
        let connector = SqliteConnector(mainPath)
        // Table Orders is inside stagedFile, query triggers attach and retry
        let qr = connector.ExecuteQuery "SELECT Id, CustomerId FROM _fqs_staged.Orders" []
        // First enumeration
        let rows1 = qr.Rows |> Seq.toArray
        Assert.Equal(2, rows1.Length)
        // Second enumeration via None branch
        let rows2 = qr.Rows |> Seq.toArray
        Assert.Equal(2, rows2.Length)
    finally
        SqliteConnector.StagedEngineDatabasePathResolver <- prev
        SqliteConnection.ClearAllPools()
        if File.Exists mainPath then try File.Delete mainPath with _ -> ()

[<Fact>]
let ``SqliteConnector attaches staged engine dynamically in ExecuteReaderStreamWithColumns scenario 2`` () =
    let mainPath = Path.Combine(Path.GetTempPath(), $"fqs-main-stream-{Guid.NewGuid():N}.db")
    use stagedFile = new TempSqliteFile()
    let prev = SqliteConnector.StagedEngineDatabasePathResolver
    try
        SqliteConnector.StagedEngineDatabasePathResolver <- Some (fun () -> Some stagedFile.Path)
        let connector = SqliteConnector(mainPath) :> IStreamingQueryableConnector
        let ids =
            connector.ExecuteReaderStreamWithColumns "SELECT Id FROM _fqs_staged.Orders" [] (fun _ r -> r.GetInt64(0))
            |> Seq.toArray
        Assert.Equal(2, ids.Length)
    finally
        SqliteConnector.StagedEngineDatabasePathResolver <- prev
        SqliteConnection.ClearAllPools()
        if File.Exists mainPath then try File.Delete mainPath with _ -> ()

[<Fact>]
let ``SqliteConnector TestConnectionAsync returns false on canceled token scenario 1`` () =
    task {
        use cts = new CancellationTokenSource()
        cts.Cancel()
        use file = new TempSqliteFile()
        let connector = SqliteConnector(file.Path) :> IConnector
        let! ok = connector.TestConnectionAsync(cts.Token)
        Assert.False(ok)
    }

[<Fact>]
let ``SqliteConnector TestConnectionAsync returns false on canceled token scenario 2`` () =
    task {
        use file = new TempSqliteFile()
        let connector = SqliteConnector(file.Path) :> IConnector
        let! ok = connector.TestConnectionAsync(CancellationToken(true))
        Assert.False(ok)
    }

[<Fact>]
let ``SqliteConnector ExecuteQuery returns empty sequence for 0-column commands scenario 1`` () =
    use file = new TempSqliteFile()
    let connector = SqliteConnector(file.Path)
    let res = connector.ExecuteQuery "PRAGMA synchronous = OFF;" []
    Assert.Empty(res.Columns)
    Assert.Empty(res.Rows)

[<Fact>]
let ``SqliteConnector ExecuteQuery handles DDL statement with 0 columns scenario 2`` () =
    use file = new TempSqliteFile()
    let connector = SqliteConnector(file.Path)
    let res = connector.ExecuteQuery "CREATE TABLE TempTest (Id int);" []
    Assert.Empty(res.Columns)
    Assert.Empty(res.Rows)

[<Fact>]
let ``SqliteConnector tryEnsureAndAttachStagedEngine handles illegal characters in path scenario 1`` () =
    let prev = SqliteConnector.StagedEngineDatabasePathResolver
    try
        let invalidPath = "invalid:\0path.db"
        SqliteConnector.StagedEngineDatabasePathResolver <- Some (fun () -> Some invalidPath)
        let connector = SqliteConnector(invalidPath)
        Assert.ThrowsAny<Exception>(fun () ->
            connector.ExecuteQuery "SELECT * FROM MissingTable" [] |> ignore) |> ignore
    finally
        SqliteConnector.StagedEngineDatabasePathResolver <- prev

[<Fact>]
let ``SqliteConnector tryEnsureAndAttachStagedEngine handles non-database file attachment failure scenario 2`` () =
    let txtFile = Path.Combine(Path.GetTempPath(), $"non-db-{Guid.NewGuid():N}.txt")
    File.WriteAllText(txtFile, "Not a SQLite file")
    use file = new TempSqliteFile()
    let prev = SqliteConnector.StagedEngineDatabasePathResolver
    try
        SqliteConnector.StagedEngineDatabasePathResolver <- Some (fun () -> Some txtFile)
        let connector = SqliteConnector(file.Path)
        Assert.ThrowsAny<Exception>(fun () ->
            connector.ExecuteQuery "SELECT * FROM _fqs_staged.MissingTable" [] |> ignore) |> ignore
    finally
        SqliteConnector.StagedEngineDatabasePathResolver <- prev
        if File.Exists txtFile then try File.Delete txtFile with _ -> ()

[<Fact>]
let ``SqliteConnector ExecuteReaderStreamWithColumns throws when staging fails scenario 1`` () =
    let txtFile = Path.Combine(Path.GetTempPath(), $"non-db-stream-{Guid.NewGuid():N}.txt")
    File.WriteAllText(txtFile, "Not a SQLite file")
    use file = new TempSqliteFile()
    let prev = SqliteConnector.StagedEngineDatabasePathResolver
    try
        SqliteConnector.StagedEngineDatabasePathResolver <- Some (fun () -> Some txtFile)
        let connector = SqliteConnector(file.Path) :> IStreamingQueryableConnector
        Assert.ThrowsAny<Exception>(fun () ->
            connector.ExecuteReaderStreamWithColumns "SELECT * FROM _fqs_staged.MissingTable" [] (fun _ r -> r.GetInt32(0))
            |> Seq.toArray
            |> ignore) |> ignore
    finally
        SqliteConnector.StagedEngineDatabasePathResolver <- prev
        if File.Exists txtFile then try File.Delete txtFile with _ -> ()

[<Fact>]
let ``SqliteConnector extension ConnectorCategory Sql returns expected properties scenario 1`` () =
    let sqlCat = ConnectorCategory.Sql
    Assert.Equal("sql", sqlCat.Identifier)
    Assert.Equal("Db", sqlCat.ModuleSuffix)

[<Fact>]
let ``SqliteConnector extension ConnectorCategory Sql matches expected type name scenario 2`` () =
    let sqlCat = ConnectorCategory.Sql
    Assert.Equal("FSharpQueryStudio.Connectors.Connections.SqlCategory", sqlCat.CategoryTypeName)

[<Fact>]
let ``SqliteConnector re-enumeration with temp table triggers second-enumeration missing table handler scenario 1`` () =
    use file = new TempSqliteFile()
    let connector = SqliteConnector(file.Path)
    let qr = connector.ExecuteQuery "SELECT Id, CustomerId FROM Orders" []
    let rows1 = qr.Rows |> Seq.toArray
    Assert.Equal(2, rows1.Length)
    // Overwrite the file with a fresh empty sqlite db without Orders table
    SqliteConnection.ClearAllPools()
    use emptyConn = new SqliteConnection(SqliteConnectionStringBuilder(DataSource = file.Path).ToString())
    emptyConn.Open()
    use cmd = emptyConn.CreateCommand()
    cmd.CommandText <- "DROP TABLE Orders;"
    cmd.ExecuteNonQuery() |> ignore
    SqliteConnection.ClearAllPools()
    // Second enumeration attempts to query Orders which was dropped
    Assert.ThrowsAny<Exception>(fun () -> qr.Rows |> Seq.toArray |> ignore) |> ignore

[<Fact>]
let ``SqliteConnector re-enumeration with missing table when staging returns false scenario 2`` () =
    use file = new TempSqliteFile()
    let prev = SqliteConnector.StagedEngineDatabasePathResolver
    try
        SqliteConnector.StagedEngineDatabasePathResolver <- Some (fun () -> None)
        let connector = SqliteConnector(file.Path)
        let qr = connector.ExecuteQuery "SELECT Id, CustomerId FROM Orders" []
        let rows1 = qr.Rows |> Seq.toArray
        Assert.Equal(2, rows1.Length)
        // Drop Orders table
        SqliteConnection.ClearAllPools()
        use emptyConn = new SqliteConnection(SqliteConnectionStringBuilder(DataSource = file.Path).ToString())
        emptyConn.Open()
        use cmd = emptyConn.CreateCommand()
        cmd.CommandText <- "DROP TABLE Orders;"
        cmd.ExecuteNonQuery() |> ignore
        SqliteConnection.ClearAllPools()
        Assert.ThrowsAny<Exception>(fun () -> qr.Rows |> Seq.toArray |> ignore) |> ignore
    finally
        SqliteConnector.StagedEngineDatabasePathResolver <- prev

[<Fact>]
let ``SqliteConnectorFactory handles empty and whitespace filePath scenario 1`` () =
    let factory = SqliteConnectorFactory() :> IConnectorFactory
    let json = JsonSerializer.SerializeToElement({| filePath = "   " |})
    let store = InMemorySecretStore()
    let conn = factory.Create(json, store, None)
    Assert.NotNull(conn)
    Assert.Equal("Data Source=;Pooling=False", (conn :?> SqliteConnector).ConnectionString)

[<Fact>]
let ``SqliteConnectorFactory handles string JSON element scenario 2`` () =
    let factory = SqliteConnectorFactory() :> IConnectorFactory
    let json = JsonSerializer.SerializeToElement("mydb.sqlite")
    let store = InMemorySecretStore()
    let conn = factory.Create(json, store, None)
    Assert.NotNull(conn)
    Assert.Contains("mydb.sqlite", (conn :?> SqliteConnector).ConnectionString)
