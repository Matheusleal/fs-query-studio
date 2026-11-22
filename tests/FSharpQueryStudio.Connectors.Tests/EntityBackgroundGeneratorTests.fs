module EntityBackgroundGeneratorTests

open System
open System.IO
open System.Text.Json
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors
open FSharpQueryStudio.Security

[<Fact>]
let ``inferType infers decimal for values with dot or comma decimal parts and handles quoted strings`` () =
    // Dot decimal
    Assert.Equal("decimal", EntityBackgroundGenerator.inferType [ "355272835.53"; "100.50"; "0.01" ])

    // Comma decimal
    Assert.Equal("decimal", EntityBackgroundGenerator.inferType [ "355272835,53"; "100,50"; "0,01" ])

    // Quoted decimal strings
    Assert.Equal("decimal", EntityBackgroundGenerator.inferType [ "\"355272835.53\""; "\"100.50\"" ])

    // Int32
    Assert.Equal("int", EntityBackgroundGenerator.inferType [ "1"; "2"; "42" ])

    // Int64
    Assert.Equal("int64", EntityBackgroundGenerator.inferType [ "3000000000"; "4000000000" ])

    // Bool
    Assert.Equal("bool", EntityBackgroundGenerator.inferType [ "true"; "false"; "TRUE" ])

    // DateTime
    Assert.Equal("System.DateTime", EntityBackgroundGenerator.inferType [ "2026-10-27"; "2026-10-28T12:00:00Z" ])

    // Sentinel values N/A, -, null trigger option types
    Assert.Equal("decimal option", EntityBackgroundGenerator.inferType [ "355272835.53"; "N/A" ])
    Assert.Equal("decimal option", EntityBackgroundGenerator.inferType [ "100.50"; "-" ])
    Assert.Equal("int option", EntityBackgroundGenerator.inferType [ "1"; "null"; "42" ])
    Assert.Equal("string option", EntityBackgroundGenerator.inferType [ "Alice"; "N/A"; "Bob" ])
    Assert.Equal("string option", EntityBackgroundGenerator.inferType [ "Alice"; "-"; "Bob" ])
    Assert.Equal("string option", EntityBackgroundGenerator.inferType [ "Alice"; "null"; "Bob" ])

    // Non-sentinel mixed text fallback to regular string
    Assert.Equal("string", EntityBackgroundGenerator.inferType [ "355272835.53"; "TextoInvalido" ])

    // 200 rows sampling: sentinel at row 150 triggers option
    let rows200WithSentinel = [ for i in 1..200 -> if i = 150 then "N/A" else "100.50" ]
    Assert.Equal("decimal option", EntityBackgroundGenerator.inferType rows200WithSentinel)

    // 200 rows sampling: clean 200 rows has no option
    let rows200Clean = [ for _ in 1..200 -> "100.50" ]
    Assert.Equal("decimal", EntityBackgroundGenerator.inferType rows200Clean)

[<Fact>]
let ``sanitizeColumns removes empty column names and deduplicates identical headers`` () =
    let raw = [ ""; "Valor"; "Valor"; "  "; "Data" ]
    let sanitized = EntityBackgroundGenerator.sanitizeColumns raw
    Assert.Equal<string list>([ "Column1"; "Valor"; "Valor_2"; "Column4"; "Data" ], sanitized)

[<Fact>]
let ``generateForConnectionsAsync handles multiple CSV data sources without omitting asEntities`` () =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "fqs-multicsv-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(tempDir) |> ignore

    try
        let csv1Path = Path.Combine(tempDir, "sales.csv")
        File.WriteAllText(csv1Path, "Id,TotalAmount,Category\n1,355272835.53,Hardware\n2,120.00,Books\n")

        let csv2Path = Path.Combine(tempDir, "customers.csv")
        File.WriteAllText(csv2Path, "Id,Name,Balance\n1,Acme Corp,500000.75\n2,Beta LLC,1200.50\n")

        let conn1 =
            {
                Id = Guid.NewGuid()
                Name = "SalesFeed"
                Kind = ConnectorKind.Csv
                Configuration =
                    JsonSerializer.SerializeToElement(
                        {|
                            FilePath = csv1Path
                            Delimiter = Some ","
                        |}
                    )
                SecretReference = None
                IsTrusted = true
            }

        let conn2 =
            {
                Id = Guid.NewGuid()
                Name = "CustomersFeed"
                Kind = ConnectorKind.Csv
                Configuration =
                    JsonSerializer.SerializeToElement(
                        {|
                            FilePath = csv2Path
                            Delimiter = Some ","
                        |}
                    )
                SecretReference = None
                IsTrusted = true
            }

        let secretStore = InMemorySecretStore()

        let result =
            (EntityBackgroundGenerator.generateForConnectionsAsync tempDir [ conn1; conn2 ] secretStore)
                .GetAwaiter()
                .GetResult()

        Assert.True(result)
        let generatedFile = EntityBackgroundGenerator.generatedFilePath tempDir
        Assert.True(File.Exists generatedFile)

        let code = File.ReadAllText(generatedFile)

        // Both modules generated
        Assert.Contains("module SalesFeedCsv =", code)
        Assert.Contains("module CustomersFeedCsv =", code)

        // Decimal inference on TotalAmount and Balance
        Assert.Contains("TotalAmount: decimal", code)
        Assert.Contains("Balance: decimal", code)

        // CsvCategory extensions generated for both CSVs without asEntities
        Assert.Contains("type FSharpQueryStudio.Connectors.Connections.CsvCategory with", code)
        Assert.Contains("member _.SalesFeed : SalesFeedCsv.Data = SalesFeedCsv.Data(\"SalesFeed\")", code)

        Assert.Contains(
            "member _.CustomersFeed : CustomersFeedCsv.Data = CustomersFeedCsv.Data(\"CustomersFeed\")",
            code
        )

        Assert.DoesNotContain("asEntities", code)
        Assert.DoesNotContain("AllCsvEntities", code)
    finally
        try
            Directory.Delete(tempDir, true)
        with _ ->
            ()

[<Fact>]
let ``generateForConnectionsAsync handles multiple SQLite and multiple CSV data sources simultaneously`` () =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "fqs-multisource-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(tempDir) |> ignore

    try
        let db1Path = Path.Combine(tempDir, "sample_store.db")

        do
            use conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db1Path}")
            conn.Open()
            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                "CREATE TABLE Customers (Id INTEGER PRIMARY KEY, Name TEXT); INSERT INTO Customers VALUES (1, 'Alice');"

            cmd.ExecuteNonQuery() |> ignore

        let db2Path = Path.Combine(tempDir, "inventory.db")

        do
            use conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db2Path}")
            conn.Open()
            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                "CREATE TABLE Warehouses (Id INTEGER PRIMARY KEY, Location TEXT); INSERT INTO Warehouses VALUES (1, 'North');"

            cmd.ExecuteNonQuery() |> ignore

        let csv1Path = Path.Combine(tempDir, "sales.csv")
        File.WriteAllText(csv1Path, "Id,Total\n1,100.50\n")

        let csv2Path = Path.Combine(tempDir, "customers.csv")
        File.WriteAllText(csv2Path, "Id,Name\n1,Alice\n")

        let connSqlite1 =
            {
                Id = Guid.NewGuid()
                Name = "SampleStore"
                Kind = ConnectorKind.Sqlite
                Configuration = JsonSerializer.SerializeToElement({| FilePath = db1Path |})
                SecretReference = None
                IsTrusted = true
            }

        let connSqlite2 =
            {
                Id = Guid.NewGuid()
                Name = "InventoryStore"
                Kind = ConnectorKind.Sqlite
                Configuration = JsonSerializer.SerializeToElement({| FilePath = db2Path |})
                SecretReference = None
                IsTrusted = true
            }

        let connCsv1 =
            {
                Id = Guid.NewGuid()
                Name = "SalesFeed"
                Kind = ConnectorKind.Csv
                Configuration =
                    JsonSerializer.SerializeToElement(
                        {|
                            FilePath = csv1Path
                            Delimiter = Some ","
                        |}
                    )
                SecretReference = None
                IsTrusted = true
            }

        let connCsv2 =
            {
                Id = Guid.NewGuid()
                Name = "CustomersFeed"
                Kind = ConnectorKind.Csv
                Configuration =
                    JsonSerializer.SerializeToElement(
                        {|
                            FilePath = csv2Path
                            Delimiter = Some ","
                        |}
                    )
                SecretReference = None
                IsTrusted = true
            }

        let secretStore = InMemorySecretStore()

        let result =
            (EntityBackgroundGenerator.generateForConnectionsAsync
                tempDir
                [ connSqlite1; connSqlite2; connCsv1; connCsv2 ]
                secretStore)
                .GetAwaiter()
                .GetResult()

        Assert.True(result)
        let generatedFile = EntityBackgroundGenerator.generatedFilePath tempDir
        Assert.True(File.Exists generatedFile)

        let code = File.ReadAllText(generatedFile)

        // SQLite modules and SqlCategory extension
        Assert.Contains("module SampleStoreDb =", code)
        Assert.Contains("module InventoryStoreDb =", code)
        Assert.Contains("SqlPushdownQueryable<", code)
        Assert.Contains("type FSharpQueryStudio.Connectors.Connections.SqlCategory with", code)
        Assert.Contains("member _.SampleStore : SampleStoreDb.Database = SampleStoreDb.Database(\"SampleStore\")", code)

        Assert.Contains(
            "member _.InventoryStore : InventoryStoreDb.Database = InventoryStoreDb.Database(\"InventoryStore\")",
            code
        )

        // CSV modules and CsvCategory extension
        Assert.Contains("module SalesFeedCsv =", code)
        Assert.Contains("module CustomersFeedCsv =", code)
        Assert.Contains("type FSharpQueryStudio.Connectors.Connections.CsvCategory with", code)
        Assert.Contains("member _.SalesFeed : SalesFeedCsv.Data = SalesFeedCsv.Data(\"SalesFeed\")", code)

        Assert.Contains(
            "member _.CustomersFeed : CustomersFeedCsv.Data = CustomersFeedCsv.Data(\"CustomersFeed\")",
            code
        )

        // No asEntities or cross-connection aggregate types
        Assert.DoesNotContain("asEntities", code)
        Assert.DoesNotContain("AllDatabaseEntities", code)
        Assert.DoesNotContain("AllCsvEntities", code)
    finally
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()

        try
            Directory.Delete(tempDir, true)
        with _ ->
            ()

[<Fact>]
let ``generateForConnectionsAsync with empty connections overwrites existing GeneratedEntities with empty scaffold``
    ()
    =
    let tempDir = Path.Combine(Path.GetTempPath(), $"fqs-empty-gen-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tempDir) |> ignore
    let genDir = Path.Combine(tempDir, "generated")
    Directory.CreateDirectory(genDir) |> ignore
    let targetPath = Path.Combine(genDir, "GeneratedEntities.fs")
    File.WriteAllText(targetPath, "// OLD STALE CODE\nmodule Old = let x = 1")

    let secretStore =
        { new ISecretStore with
            member _.TryRead _ = None

            member _.TryReadAsync(_, _) =
                System.Threading.Tasks.Task.FromResult None

            member _.Save _ _ = ()
            member _.Delete _ = ()
        }

    try
        let result =
            (EntityBackgroundGenerator.generateForConnectionsAsync tempDir [] secretStore).GetAwaiter().GetResult()

        Assert.True(result)
        let newContent = File.ReadAllText targetPath
        Assert.DoesNotContain("OLD STALE CODE", newContent)
        Assert.Contains("namespace FSharpQueryStudio.Generated", newContent)
        Assert.Contains("// No connections registered.", newContent)
    finally
        try
            Directory.Delete(tempDir, true)
        with _ ->
            ()

[<Fact>]
let ``generateForConnectionsAsync emits Excel connection as schema container without root IQueryable or Rows`` () =
    BuiltinConnectors.registerDefaults ()
    let tempDir = Path.Combine(Path.GetTempPath(), $"fqs-excel-gen-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tempDir) |> ignore

    try
        let xlsxPath = Path.Combine(tempDir, "teste.xlsx")

        do
            use wb = new ClosedXML.Excel.XLWorkbook()
            let ws1 = wb.Worksheets.Add("calendar")
            ws1.Cell(1, 1).Value <- "Id"
            ws1.Cell(1, 2).Value <- "EventName"
            ws1.Cell(2, 1).Value <- 1
            ws1.Cell(2, 2).Value <- "Kickoff"

            let ws2 = wb.Worksheets.Add("names")
            ws2.Cell(1, 1).Value <- "Id"
            ws2.Cell(1, 2).Value <- "FullName"
            ws2.Cell(2, 1).Value <- 2
            ws2.Cell(2, 2).Value <- "Ana"

            let ws3 = wb.Worksheets.Add("courses")
            ws3.Cell(1, 1).Value <- "Id"
            ws3.Cell(1, 2).Value <- "Title"
            ws3.Cell(2, 1).Value <- 3
            ws3.Cell(2, 2).Value <- "Databases"
            wb.SaveAs(xlsxPath)

        let connExcel =
            {
                Id = Guid.NewGuid()
                Name = "teste"
                Kind = ConnectorKind.Excel
                Configuration =
                    JsonSerializer.SerializeToElement(
                        {|
                            filePath = xlsxPath
                            hasHeader = true
                        |}
                    )
                SecretReference = None
                IsTrusted = true
            }

        let secretStore = InMemorySecretStore()

        let result =
            (EntityBackgroundGenerator.generateForConnectionsAsync tempDir [ connExcel ] secretStore)
                .GetAwaiter()
                .GetResult()

        Assert.True(result)
        let generatedFile = EntityBackgroundGenerator.generatedFilePath tempDir
        let code = File.ReadAllText(generatedFile)

        // Schema container module and worksheet properties
        Assert.Contains("module testeExcel =", code)
        Assert.Contains("module calendar =", code)
        Assert.Contains("module names =", code)
        Assert.Contains("module courses =", code)

        Assert.Contains(
            "member _.calendar : FSharpQueryStudio.Connectors.SqlPushdownQueryable<calendar.Row> = conn.SheetPushdownQueryable<calendar.Row>(\"calendar\")",
            code
        )

        Assert.Contains(
            "member _.names : FSharpQueryStudio.Connectors.SqlPushdownQueryable<names.Row> = conn.SheetPushdownQueryable<names.Row>(\"names\")",
            code
        )

        Assert.Contains(
            "member _.courses : FSharpQueryStudio.Connectors.SqlPushdownQueryable<courses.Row> = conn.SheetPushdownQueryable<courses.Row>(\"courses\")",
            code
        )

        // Root Data type must NOT act as a table of the first sheet
        Assert.DoesNotContain("defaultQueryable", code)
        Assert.DoesNotContain("member _.Rows", code)
        Assert.DoesNotContain("interface System.Linq.IQueryable", code)
    finally
        try
            Directory.Delete(tempDir, true)
        with _ ->
            ()

[<Fact>]
let ``generateForConnectionsAsync emits Json and Xml connections as schema containers with sub-properties`` () =
    BuiltinConnectors.registerDefaults ()

    let tempDir =
        Path.Combine(Path.GetTempPath(), $"fqs-jsonxml-gen-{Guid.NewGuid():N}")

    Directory.CreateDirectory(tempDir) |> ignore

    try
        let jsonPath = Path.Combine(tempDir, "catalog.json")
        SampleDataGenerator.createSampleJsonFile jsonPath

        let xmlPath = Path.Combine(tempDir, "logistics.xml")
        SampleDataGenerator.createSampleXmlFile xmlPath

        let connJson =
            {
                Id = Guid.NewGuid()
                Name = "ProductCatalog"
                Kind = ConnectorKind.Json
                Configuration =
                    JsonSerializer.SerializeToElement(
                        {|
                            filePath = jsonPath
                            flattenNested = true
                        |}
                    )
                SecretReference = None
                IsTrusted = true
            }

        let connXml =
            {
                Id = Guid.NewGuid()
                Name = "LogisticsHub"
                Kind = ConnectorKind.Xml
                Configuration =
                    JsonSerializer.SerializeToElement(
                        {|
                            filePath = xmlPath
                            includeAttributes = true
                            flattenNested = true
                        |}
                    )
                SecretReference = None
                IsTrusted = true
            }

        let secretStore = InMemorySecretStore()

        let result =
            (EntityBackgroundGenerator.generateForConnectionsAsync tempDir [ connJson; connXml ] secretStore)
                .GetAwaiter()
                .GetResult()

        Assert.True(result)
        let generatedFile = EntityBackgroundGenerator.generatedFilePath tempDir
        let code = File.ReadAllText(generatedFile)

        // JSON schema container and sub-properties
        Assert.Contains("module ProductCatalogJson =", code)

        Assert.Contains(
            "member _.Products : FSharpQueryStudio.Connectors.SqlPushdownQueryable<Products.Row> = conn.TablePushdownQueryable<Products.Row>(\"Products\")",
            code
        )

        Assert.Contains(
            "member _.Reviews : FSharpQueryStudio.Connectors.SqlPushdownQueryable<Reviews.Row> = conn.TablePushdownQueryable<Reviews.Row>(\"Reviews\")",
            code
        )

        Assert.Contains("type FSharpQueryStudio.Connectors.Connections.JsonCategory with", code)

        Assert.Contains(
            "member _.ProductCatalog : ProductCatalogJson.Data = ProductCatalogJson.Data(\"ProductCatalog\")",
            code
        )

        // XML schema container and sub-properties
        Assert.Contains("module LogisticsHubXml =", code)

        Assert.Contains(
            "member _.Carriers : FSharpQueryStudio.Connectors.SqlPushdownQueryable<Carriers.Row> = conn.TablePushdownQueryable<Carriers.Row>(\"Carriers\")",
            code
        )

        Assert.Contains(
            "member _.Shipments : FSharpQueryStudio.Connectors.SqlPushdownQueryable<Shipments.Row> = conn.TablePushdownQueryable<Shipments.Row>(\"Shipments\")",
            code
        )

        Assert.Contains("type FSharpQueryStudio.Connectors.Connections.XmlCategory with", code)
        Assert.Contains("member _.LogisticsHub : LogisticsHubXml.Data = LogisticsHubXml.Data(\"LogisticsHub\")", code)
    finally
        try
            Directory.Delete(tempDir, true)
        with _ ->
            ()

[<Fact>]
let ``tryGetKnownSampleTables returns expected schemas for all 5 sample databases and None for unknown`` () =
    let knownDbs =
        [
            "PostgreSQL_Advanced"
            "SqlServer_Advanced"
            "MySQL_Advanced"
            "MariaDB_Advanced"
            "Oracle_Advanced"
        ]

    for db in knownDbs do
        match AdvancedSampleGenerator.tryGetKnownSampleTables db with
        | None -> Assert.Fail($"Expected known sample tables for {db}")
        | Some tables ->
            Assert.Equal(5, tables.Length)

            let tableNames =
                tables
                |> List.map (fun t -> (t.Name.Split('.') |> Array.last).ToLowerInvariant().Replace("_", ""))

            Assert.Contains("categories", tableNames)
            Assert.Contains("products", tableNames)
            Assert.Contains("customers", tableNames)
            Assert.Contains("orders", tableNames)
            Assert.Contains("orderitems", tableNames)

    Assert.True((AdvancedSampleGenerator.tryGetKnownSampleTables "UnknownDb").IsNone)

[<Fact>]
let ``offline sample database connections generate typed table accessors and case tolerant properties`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), $"fqs-offline-db-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tempDir) |> ignore

    try
        let connPg =
            {
                Id = Guid.NewGuid()
                Name = "PostgreSQL_Advanced"
                Kind = ConnectorKind.PostgreSql
                Configuration =
                    JsonSerializer.SerializeToElement(
                        {|
                            connectionString =
                                "Host=127.0.0.1;Port=54329;Database=fsqs_test;Username=postgres;Password=invalid"
                        |}
                    )
                SecretReference = None
                IsTrusted = true
            }

        let secretStore = InMemorySecretStore()

        let result =
            (EntityBackgroundGenerator.generateForConnectionsAsync tempDir [ connPg ] secretStore)
                .GetAwaiter()
                .GetResult()

        Assert.True(result)
        let generatedFile = EntityBackgroundGenerator.generatedFilePath tempDir
        let code = File.ReadAllText(generatedFile)

        // Database module generated with tables even when offline
        Assert.Contains("module PostgreSQL_AdvancedDb =", code)
        Assert.Contains("member _.products : FSharpQueryStudio.Connectors.SqlPushdownQueryable<", code)
        Assert.Contains("member _.categories : FSharpQueryStudio.Connectors.SqlPushdownQueryable<", code)
        // Case-tolerant top-level accessors
        Assert.Contains("member _.Products : FSharpQueryStudio.Connectors.SqlPushdownQueryable<", code)
        Assert.Contains("member _.PRODUCTS : FSharpQueryStudio.Connectors.SqlPushdownQueryable<", code)
    finally
        try
            Directory.Delete(tempDir, true)
        with _ ->
            ()

[<Fact>]
let ``isModuleIncomplete and needsSynchronousGeneration detect empty database stubs and trigger regeneration`` () =
    let emptyStub =
        "module PostgreSQL_AdvancedDb =\n"
        + "    type Database(conn: FSharpQueryStudio.Connectors.IConnector) =\n"
        + "        class end\n"

    Assert.True(EntityBackgroundGenerator.isModuleIncomplete emptyStub "PostgreSQL_Advanced")

    let completeModule =
        "module PostgreSQL_AdvancedDb =\n"
        + "    module categories =\n"
        + "        type Row = { id: int }\n"
        + "    type Database(conn: FSharpQueryStudio.Connectors.IConnector) =\n"
        + "        member _.categories = ()\n"

    Assert.False(EntityBackgroundGenerator.isModuleIncomplete completeModule "PostgreSQL_Advanced")

[<Fact>]
let ``generatedDir and generatedFilePath handle null, empty, whitespace, and special leaf dirs scenario 1`` () =
    Assert.Equal("generated", EntityBackgroundGenerator.generatedDir "")
    Assert.Equal("generated", EntityBackgroundGenerator.generatedDir "   ")
    Assert.Equal("generated", EntityBackgroundGenerator.generatedDir null)
    
    let fsqsDir = Path.Combine("C:", "Project", ".fsqs")
    Assert.Equal(Path.Combine(fsqsDir, "generated"), EntityBackgroundGenerator.generatedDir fsqsDir)

    let fqsDir = Path.Combine("C:", "Project", "FSharpQueryStudio")
    Assert.Equal(Path.Combine(fqsDir, "generated"), EntityBackgroundGenerator.generatedDir fqsDir)

[<Fact>]
let ``generatedDir detects existing dot-fsqs or generated directories scenario 2`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), $"fqs-test-dir-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tempDir) |> ignore
    try
        let genSub = Path.Combine(tempDir, "generated")
        Directory.CreateDirectory(genSub) |> ignore
        Assert.Equal(Path.Combine(tempDir, "generated"), EntityBackgroundGenerator.generatedDir tempDir)
    finally
        try Directory.Delete(tempDir, true) with _ -> ()

[<Fact>]
let ``generateForConnectionsAsync handles empty connections and reports zero extensions scenario 1`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), $"fqs-empty-gen-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tempDir) |> ignore
    let prevStatus = EntityBackgroundGenerator.onStatusChanged
    try
        let mutable reportedStatus: EntityBackgroundGenerator.EntityMappingStatus option = None
        EntityBackgroundGenerator.onStatusChanged <- Some (fun status -> reportedStatus <- Some status)
        let secretStore = InMemorySecretStore()
        let result =
            (EntityBackgroundGenerator.generateForConnectionsAsync tempDir [] secretStore)
                .GetAwaiter()
                .GetResult()
        Assert.True(result)
        Assert.Equal(Some(EntityBackgroundGenerator.EntityMappingStatus.Completed 0), reportedStatus)
    finally
        EntityBackgroundGenerator.onStatusChanged <- prevStatus
        try Directory.Delete(tempDir, true) with _ -> ()

[<Fact>]
let ``generateForConnectionsAsync handles connection generator failure gracefully scenario 2`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), $"fqs-err-gen-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tempDir) |> ignore
    try
        // Malformed CSV configuration that throws during schema/header discovery
        let badConn =
            {
                Id = Guid.NewGuid()
                Name = "BadCsv"
                Kind = ConnectorKind.Csv
                Configuration = JsonSerializer.SerializeToElement({| FilePath = "non_existent_file_xyz_123.csv" |})
                SecretReference = None
                IsTrusted = true
            }
        let secretStore = InMemorySecretStore()
        let result =
            (EntityBackgroundGenerator.generateForConnectionsAsync tempDir [ badConn ] secretStore)
                .GetAwaiter()
                .GetResult()
        // Gracefully handled without throwing
        Assert.False(result)
    finally
        try Directory.Delete(tempDir, true) with _ -> ()

[<Fact>]
let ``triggerBackgroundGeneration and waitForPendingGeneration execute asynchronously scenario 1`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), $"fqs-async-gen-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tempDir) |> ignore
    try
        let secretStore = InMemorySecretStore()
        EntityBackgroundGenerator.triggerBackgroundGeneration tempDir [] secretStore
        EntityBackgroundGenerator.waitForPendingGeneration ()
        Assert.True(true)
    finally
        try Directory.Delete(tempDir, true) with _ -> ()

[<Fact>]
let ``triggerBackgroundGeneration and waitForPendingGeneration with valid connection scenario 2`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), $"fqs-async-gen2-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tempDir) |> ignore
    try
        let csvPath = Path.Combine(tempDir, "data.csv")
        File.WriteAllText(csvPath, "ColA,ColB\n1,Hello\n2,World\n")
        let conn =
            {
                Id = Guid.NewGuid()
                Name = "AsyncFeed"
                Kind = ConnectorKind.Csv
                Configuration = JsonSerializer.SerializeToElement({| FilePath = csvPath |})
                SecretReference = None
                IsTrusted = true
            }
        let secretStore = InMemorySecretStore()
        EntityBackgroundGenerator.triggerBackgroundGeneration tempDir [ conn ] secretStore
        EntityBackgroundGenerator.waitForPendingGeneration ()
        let genFile = EntityBackgroundGenerator.generatedFilePath tempDir
        Assert.True(File.Exists genFile)
    finally
        try Directory.Delete(tempDir, true) with _ -> ()

[<Fact>]
let ``isModuleIncomplete edge cases for CR-LF and missing module scenario 1`` () =
    let missing = "module OtherDb =\n    type X = int"
    Assert.False(EntityBackgroundGenerator.isModuleIncomplete missing "Target")

[<Fact>]
let ``isModuleIncomplete edge cases for CR-LF inner types scenario 2`` () =
    let crlfStub = "module TargetDb =\r\n    class end\r\n"
    Assert.True(EntityBackgroundGenerator.isModuleIncomplete crlfStub "Target")

    let crlfComplete = "module TargetDb =\r\n    module sub =\r\n        type Row = { a: int }\r\n"
    Assert.False(EntityBackgroundGenerator.isModuleIncomplete crlfComplete "Target")

[<Fact>]
let ``needsSynchronousGeneration with cache hit and cache miss scenario 1`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), $"fqs-sync-gen-cache1-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tempDir) |> ignore
    try
        let filePath = EntityBackgroundGenerator.generatedFilePath tempDir
        Directory.CreateDirectory(Path.GetDirectoryName filePath) |> ignore
        EntityBackgroundGenerator.resetCache ()

        // Non-existent file -> true
        Assert.True(EntityBackgroundGenerator.needsSynchronousGeneration filePath [])

        // Write file with "No connections registered."
        File.WriteAllText(filePath, "// <auto-generated>\n// No connections registered.\n")

        // First call with empty connections -> computes and populates cache -> returns false because connections is empty
        let needs1 = EntityBackgroundGenerator.needsSynchronousGeneration filePath []
        Assert.False(needs1)

        // Second call with same timestamp & fingerprint -> cache hit returns false
        let needs2 = EntityBackgroundGenerator.needsSynchronousGeneration filePath []
        Assert.False(needs2)
    finally
        try Directory.Delete(tempDir, true) with _ -> ()

[<Fact>]
let ``needsSynchronousGeneration with cache hit and connection changes scenario 2`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), $"fqs-sync-gen-cache2-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tempDir) |> ignore
    try
        let filePath = EntityBackgroundGenerator.generatedFilePath tempDir
        EntityBackgroundGenerator.resetCache ()
        let csvPath = Path.Combine(tempDir, "test.csv")
        File.WriteAllText(csvPath, "A,B\n1,2\n")

        let conn =
            {
                Id = Guid.NewGuid()
                Name = "CachedConn"
                Kind = ConnectorKind.Csv
                Configuration = JsonSerializer.SerializeToElement({| FilePath = csvPath |})
                SecretReference = None
                IsTrusted = true
            }

        // File does not exist yet -> true
        Assert.True(EntityBackgroundGenerator.needsSynchronousGeneration filePath [ conn ])

        // Generate file
        let secretStore = InMemorySecretStore()
        let genSuccess = (EntityBackgroundGenerator.generateForConnectionsAsync tempDir [ conn ] secretStore).GetAwaiter().GetResult()
        Assert.True(genSuccess)

        // Reset cache to force recalculation against existing generated file
        EntityBackgroundGenerator.resetCache ()
        let needsAfterGen = EntityBackgroundGenerator.needsSynchronousGeneration filePath [ conn ]
        Assert.False(needsAfterGen)

        // Now with same file and same conn -> cache hit returns false
        let needsHit = EntityBackgroundGenerator.needsSynchronousGeneration filePath [ conn ]
        Assert.False(needsHit)

        // With new connection -> fingerprint changes -> cache miss -> true
        let conn2 = { conn with Id = Guid.NewGuid(); Name = "BrandNewConn" }
        let needsMiss = EntityBackgroundGenerator.needsSynchronousGeneration filePath [ conn; conn2 ]
        Assert.True(needsMiss)
    finally
        try Directory.Delete(tempDir, true) with _ -> ()

[<Fact>]
let ``ensureGenerated executes for empty connections when file exists scenario 1`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), $"fqs-ensure-gen1-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tempDir) |> ignore
    try
        let filePath = EntityBackgroundGenerator.generatedFilePath tempDir
        let targetDir = Path.GetDirectoryName filePath
        Directory.CreateDirectory targetDir |> ignore
        File.WriteAllText(filePath, "// Old generated entities")

        let secretStore = InMemorySecretStore()
        EntityBackgroundGenerator.ensureGenerated tempDir [] secretStore
        EntityBackgroundGenerator.waitForPendingGeneration ()

        // File should be updated to "No connections registered."
        let content = File.ReadAllText(filePath)
        Assert.Contains("No connections registered.", content)
    finally
        try Directory.Delete(tempDir, true) with _ -> ()

[<Fact>]
let ``ensureGenerated executes synchronously when generation is needed scenario 2`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), $"fqs-ensure-gen2-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tempDir) |> ignore
    try
        let csvPath = Path.Combine(tempDir, "sample.csv")
        File.WriteAllText(csvPath, "ColX,ColY\n10,20\n")
        let conn =
            {
                Id = Guid.NewGuid()
                Name = "EnsureSyncConn"
                Kind = ConnectorKind.Csv
                Configuration = JsonSerializer.SerializeToElement({| FilePath = csvPath |})
                SecretReference = None
                IsTrusted = true
            }
        let secretStore = InMemorySecretStore()
        EntityBackgroundGenerator.resetCache ()
        EntityBackgroundGenerator.ensureGenerated tempDir [ conn ] secretStore

        let filePath = EntityBackgroundGenerator.generatedFilePath tempDir
        Assert.True(File.Exists filePath)
        let text = File.ReadAllText filePath
        Assert.Contains("EnsureSyncConn", text)
    finally
        try Directory.Delete(tempDir, true) with _ -> ()

[<Fact>]
let ``generateForConnectionsAsync collision handling for multiple connections producing identical clean identifier scenario 1`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), $"fqs-collision1-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tempDir) |> ignore
    try
        let csv1 = Path.Combine(tempDir, "c1.csv")
        let csv2 = Path.Combine(tempDir, "c2.csv")
        File.WriteAllText(csv1, "Id,Name\n1,Alpha\n")
        File.WriteAllText(csv2, "Id,Score\n1,100\n")

        // Two connections with distinct names that yield the same clean identifier: "my-conn" and "my_conn"
        let conn1 =
            {
                Id = Guid.NewGuid()
                Name = "my-conn"
                Kind = ConnectorKind.Csv
                Configuration = JsonSerializer.SerializeToElement({| FilePath = csv1 |})
                SecretReference = None
                IsTrusted = true
            }
        let conn2 =
            {
                Id = Guid.NewGuid()
                Name = "my_conn"
                Kind = ConnectorKind.Csv
                Configuration = JsonSerializer.SerializeToElement({| FilePath = csv2 |})
                SecretReference = None
                IsTrusted = true
            }

        let secretStore = InMemorySecretStore()
        let result =
            (EntityBackgroundGenerator.generateForConnectionsAsync tempDir [ conn1; conn2 ] secretStore)
                .GetAwaiter()
                .GetResult()
        Assert.True(result)

        let filePath = EntityBackgroundGenerator.generatedFilePath tempDir
        let text = File.ReadAllText filePath
        Assert.Contains("my_conn", text)
        Assert.Contains("my_conn_2", text)
    finally
        try Directory.Delete(tempDir, true) with _ -> ()

[<Fact>]
let ``generateForConnectionsAsync collision handling with three collisions scenario 2`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), $"fqs-collision2-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tempDir) |> ignore
    try
        let csv1 = Path.Combine(tempDir, "d1.csv")
        let csv2 = Path.Combine(tempDir, "d2.csv")
        let csv3 = Path.Combine(tempDir, "d3.csv")
        File.WriteAllText(csv1, "Id\n1\n")
        File.WriteAllText(csv2, "Id\n2\n")
        File.WriteAllText(csv3, "Id\n3\n")

        let conn1 =
            {
                Id = Guid.NewGuid()
                Name = "test-feed"
                Kind = ConnectorKind.Csv
                Configuration = JsonSerializer.SerializeToElement({| FilePath = csv1 |})
                SecretReference = None
                IsTrusted = true
            }
        let conn2 =
            {
                Id = Guid.NewGuid()
                Name = "test_feed"
                Kind = ConnectorKind.Csv
                Configuration = JsonSerializer.SerializeToElement({| FilePath = csv2 |})
                SecretReference = None
                IsTrusted = true
            }
        let conn3 =
            {
                Id = Guid.NewGuid()
                Name = "test feed"
                Kind = ConnectorKind.Csv
                Configuration = JsonSerializer.SerializeToElement({| FilePath = csv3 |})
                SecretReference = None
                IsTrusted = true
            }

        let secretStore = InMemorySecretStore()
        let result =
            (EntityBackgroundGenerator.generateForConnectionsAsync tempDir [ conn1; conn2; conn3 ] secretStore)
                .GetAwaiter()
                .GetResult()
        Assert.True(result)

        let filePath = EntityBackgroundGenerator.generatedFilePath tempDir
        let text = File.ReadAllText filePath
        Assert.Contains("test_feed", text)
        Assert.Contains("test_feed_2", text)
        Assert.Contains("test_feed_3", text)
    finally
        try Directory.Delete(tempDir, true) with _ -> ()
