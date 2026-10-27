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
