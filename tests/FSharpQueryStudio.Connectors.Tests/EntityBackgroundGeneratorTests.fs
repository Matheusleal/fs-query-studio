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

    // Mixed text fallback to string
    Assert.Equal("string", EntityBackgroundGenerator.inferType [ "355272835.53"; "N/A" ])

[<Fact>]
let ``sanitizeColumns removes empty column names and deduplicates identical headers`` () =
    let raw = [ ""; "Valor"; "Valor"; "  "; "Data" ]
    let sanitized = EntityBackgroundGenerator.sanitizeColumns raw
    Assert.Equal<string list>([ "Column1"; "Valor"; "Valor_2"; "Column4"; "Data" ], sanitized)

[<Fact>]
let ``generateForConnectionsAsync handles multiple CSV data sources without omitting asEntities`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), "fqs-multicsv-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tempDir) |> ignore

    try
        let csv1Path = Path.Combine(tempDir, "sales.csv")
        File.WriteAllText(csv1Path, "Id,TotalAmount,Category\n1,355272835.53,Hardware\n2,120.00,Books\n")

        let csv2Path = Path.Combine(tempDir, "customers.csv")
        File.WriteAllText(csv2Path, "Id,Name,Balance\n1,Acme Corp,500000.75\n2,Beta LLC,1200.50\n")

        let conn1 =
            { Id = Guid.NewGuid()
              Name = "SalesFeed"
              Kind = ConnectorKind.Csv
              Configuration = JsonSerializer.SerializeToElement({| FilePath = csv1Path; Delimiter = Some "," |})
              SecretReference = None
              IsTrusted = true }

        let conn2 =
            { Id = Guid.NewGuid()
              Name = "CustomersFeed"
              Kind = ConnectorKind.Csv
              Configuration = JsonSerializer.SerializeToElement({| FilePath = csv2Path; Delimiter = Some "," |})
              SecretReference = None
              IsTrusted = true }

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

        // asEntities generated and handles multiple CSVs
        Assert.Contains("type FSharpQueryStudio.Connectors.Connections.CsvConnection with", code)
        Assert.Contains("member this.SalesFeed = SalesFeedCsv.Data(this)", code)
        Assert.Contains("member this.CustomersFeed = CustomersFeedCsv.Data(this)", code)
        Assert.Contains("member this.asEntities", code)
        Assert.Contains("\"SalesFeed\" -> box (SalesFeedCsv.Data(this))", code)
        Assert.Contains("\"CustomersFeed\" -> box (CustomersFeedCsv.Data(this))", code)
    finally
        try Directory.Delete(tempDir, true) with _ -> ()

[<Fact>]
let ``generateForConnectionsAsync handles multiple SQLite and multiple CSV data sources simultaneously`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), "fqs-multisource-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tempDir) |> ignore

    try
        let db1Path = Path.Combine(tempDir, "sample_store.db")
        do
            use conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db1Path}")
            conn.Open()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "CREATE TABLE Customers (Id INTEGER PRIMARY KEY, Name TEXT); INSERT INTO Customers VALUES (1, 'Alice');"
            cmd.ExecuteNonQuery() |> ignore

        let db2Path = Path.Combine(tempDir, "inventory.db")
        do
            use conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db2Path}")
            conn.Open()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "CREATE TABLE Warehouses (Id INTEGER PRIMARY KEY, Location TEXT); INSERT INTO Warehouses VALUES (1, 'North');"
            cmd.ExecuteNonQuery() |> ignore

        let csv1Path = Path.Combine(tempDir, "sales.csv")
        File.WriteAllText(csv1Path, "Id,Total\n1,100.50\n")

        let csv2Path = Path.Combine(tempDir, "customers.csv")
        File.WriteAllText(csv2Path, "Id,Name\n1,Alice\n")

        let connSqlite1 =
            { Id = Guid.NewGuid()
              Name = "SampleStore"
              Kind = ConnectorKind.Sqlite
              Configuration = JsonSerializer.SerializeToElement({| FilePath = db1Path |})
              SecretReference = None
              IsTrusted = true }

        let connSqlite2 =
            { Id = Guid.NewGuid()
              Name = "InventoryStore"
              Kind = ConnectorKind.Sqlite
              Configuration = JsonSerializer.SerializeToElement({| FilePath = db2Path |})
              SecretReference = None
              IsTrusted = true }

        let connCsv1 =
            { Id = Guid.NewGuid()
              Name = "SalesFeed"
              Kind = ConnectorKind.Csv
              Configuration = JsonSerializer.SerializeToElement({| FilePath = csv1Path; Delimiter = Some "," |})
              SecretReference = None
              IsTrusted = true }

        let connCsv2 =
            { Id = Guid.NewGuid()
              Name = "CustomersFeed"
              Kind = ConnectorKind.Csv
              Configuration = JsonSerializer.SerializeToElement({| FilePath = csv2Path; Delimiter = Some "," |})
              SecretReference = None
              IsTrusted = true }

        let secretStore = InMemorySecretStore()
        let result =
            (EntityBackgroundGenerator.generateForConnectionsAsync tempDir [ connSqlite1; connSqlite2; connCsv1; connCsv2 ] secretStore)
                .GetAwaiter()
                .GetResult()

        Assert.True(result)
        let generatedFile = EntityBackgroundGenerator.generatedFilePath tempDir
        Assert.True(File.Exists generatedFile)

        let code = File.ReadAllText(generatedFile)

        // SQLite modules and DatabaseConnection extension
        Assert.Contains("module SampleStoreDb =", code)
        Assert.Contains("module InventoryStoreDb =", code)
        Assert.Contains("type FSharpQueryStudio.Connectors.Connections.DatabaseConnection with", code)
        Assert.Contains("member this.SampleStore = SampleStoreDb.Database(this)", code)
        Assert.Contains("member this.InventoryStore = InventoryStoreDb.Database(this)", code)
        Assert.Contains("\"SampleStore\" -> box (SampleStoreDb.Database(this))", code)
        Assert.Contains("\"InventoryStore\" -> box (InventoryStoreDb.Database(this))", code)

        // CSV modules and CsvConnection extension
        Assert.Contains("module SalesFeedCsv =", code)
        Assert.Contains("module CustomersFeedCsv =", code)
        Assert.Contains("type FSharpQueryStudio.Connectors.Connections.CsvConnection with", code)
        Assert.Contains("member this.SalesFeed = SalesFeedCsv.Data(this)", code)
        Assert.Contains("member this.CustomersFeed = CustomersFeedCsv.Data(this)", code)
        Assert.Contains("\"SalesFeed\" -> box (SalesFeedCsv.Data(this))", code)
        Assert.Contains("\"CustomersFeed\" -> box (CustomersFeedCsv.Data(this))", code)
    finally
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
        try Directory.Delete(tempDir, true) with _ -> ()

