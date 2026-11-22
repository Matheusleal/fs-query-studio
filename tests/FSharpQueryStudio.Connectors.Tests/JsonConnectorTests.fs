module JsonConnectorTests

open System
open System.IO
open System.Threading
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors

type private TempJsonFile(content: string) =
    let path =
        Path.Combine(Path.GetTempPath(), "fqs-json-" + Guid.NewGuid().ToString("N") + ".json")

    do File.WriteAllText(path, content)
    member _.Path = path

    interface IDisposable with
        member _.Dispose() =
            if File.Exists path then
                File.Delete path

[<Fact>]
let ``DiscoverSchemaAsync discovers multiple top-level arrays and infers column types`` () =
    task {
        let json =
            """{
  "Products": [
    { "ProductId": 101, "Title": "Neural Stick", "Price": 249.90, "InStock": true },
    { "ProductId": 102, "Title": "Smart Switch", "Price": 389.50, "InStock": false }
  ],
  "Reviews": [
    { "ReviewId": 1, "ProductId": 101, "Stars": 5, "Comment": "Great" },
    { "ReviewId": 2, "ProductId": 102, "Stars": 4, "Comment": "Solid" }
  ]
}"""

        use f = new TempJsonFile(json)
        let connector = JsonConnector(f.Path) :> IConnector

        let! tables = connector.DiscoverSchemaAsync(CancellationToken.None)
        let tableNames = tables |> List.map (fun t -> t.Name) |> List.sort
        Assert.Equal<string list>([ "Products"; "Reviews" ], tableNames)

        let products = tables |> List.find (fun t -> t.Name = "Products")

        let colMap =
            products.Columns |> List.map (fun c -> c.Name, c.DataType) |> Map.ofList

        Assert.Equal("int", colMap.["ProductId"])
        Assert.Equal("string", colMap.["Title"])
        Assert.Equal("decimal", colMap.["Price"])
        Assert.Equal("bool", colMap.["InStock"])
    }

[<Fact>]
let ``ReadRows flattens nested objects with underscore separator when FlattenNested is true`` () =
    let json =
        """[
  { "Id": 1, "Customer": { "Name": "Alice", "City": "London" }, "Tags": ["vip", "uk"] },
  { "Id": 2, "Customer": { "Name": "Bob", "City": "Austin" }, "Tags": ["us"] }
]"""

    use f = new TempJsonFile(json)

    let connector =
        JsonConnector(
            f.Path,
            { JsonOptions.Default with
                FlattenNested = true
            }
        )

    let result = connector.ReadRows()
    Assert.Equal<string list>([ "Id"; "Customer_Name"; "Customer_City"; "Tags" ], result.Columns)
    let rows = result.Rows |> Seq.toList
    Assert.Equal(2, rows.Length)
    Assert.Equal(box 1, rows.[0].[0])
    Assert.Equal(box "Alice", rows.[0].[1])
    Assert.Equal(box "London", rows.[0].[2])
    Assert.Contains("vip", string rows.[0].[3])

[<Fact>]
let ``ReadRows navigates JsonPath when specified`` () =
    let json =
        """{
  "response": {
    "payload": {
      "items": [
        { "Code": "A1", "Qty": 10 },
        { "Code": "B2", "Qty": 25 }
      ]
    }
  }
}"""

    use f = new TempJsonFile(json)

    let connector =
        JsonConnector(
            f.Path,
            { JsonOptions.Default with
                JsonPath = Some "response.payload.items"
            }
        )

    let result = connector.ReadRows()
    Assert.Equal<string list>([ "Code"; "Qty" ], result.Columns)
    let rows = result.Rows |> Seq.toList
    Assert.Equal(2, rows.Length)
    Assert.Equal(box "A1", rows.[0].[0])
    Assert.Equal(box 10, rows.[0].[1])

[<Fact>]
let ``JsonConnector reports its Kind as Json`` () =
    use f = new TempJsonFile("[]")
    let connector = JsonConnector(f.Path) :> IConnector
    Assert.Equal(ConnectorKind.Json, connector.Kind)

[<Fact>]
let ``ReadRows respects maxRows limit`` () =
    let json = """[ { "Id": 1 }, { "Id": 2 }, { "Id": 3 } ]"""
    use f = new TempJsonFile(json)
    let connector = JsonConnector(f.Path)
    let result = connector.ReadRows(maxRows = 2)
    Assert.Equal(2, result.Rows |> Seq.length)

[<Fact>]
let ``ReadRows projects primitive array into Value column`` () =
    let json = """[ "alpha", "beta", "gamma" ]"""
    use f = new TempJsonFile(json)
    let connector = JsonConnector(f.Path)
    let result = connector.ReadRows()
    Assert.Equal<string list>([ "Value" ], result.Columns)
    let rows = result.Rows |> Seq.toList
    Assert.Equal(3, rows.Length)
    Assert.Equal(box "alpha", rows.[0].[0])

[<Fact>]
let ``ReadRows returns empty columns and rows for empty array`` () =
    let json = "[]"
    use f = new TempJsonFile(json)
    let connector = JsonConnector(f.Path)
    let result = connector.ReadRows()
    Assert.Empty(result.Columns)
    Assert.Empty(result.Rows)

[<Fact>]
let ``ReadRows discovers nested container holding array`` () =
    let json =
        """{
  "wrapper": {
    "records": [
      { "Key": "k1", "Val": 100 },
      { "Key": "k2", "Val": 200 }
    ]
  }
}"""

    use f = new TempJsonFile(json)
    let connector = JsonConnector(f.Path)
    let result = connector.ReadRows()
    Assert.Equal<string list>([ "Key"; "Val" ], result.Columns)
    Assert.Equal(2, result.Rows |> Seq.length)

[<Fact>]
let ``ReadRows applies column transforms`` () =
    let json = """[ { "Name": "apple" }, { "Name": "banana" } ]"""

    let opts =
        { JsonOptions.Default with
            ColumnTransforms = Map.ofList [ "Name", "replace('apple', 'pear')" ]
        }

    use f = new TempJsonFile(json)
    let connector = JsonConnector(f.Path, opts)
    let result = connector.ReadRows()
    let rows = result.Rows |> Seq.toList
    Assert.Equal(box "pear", rows.[0].[0])

[<Fact>]
let ``TestConnectionAsync returns true when file exists and false when missing`` () =
    task {
        use f = new TempJsonFile("[]")
        let connector = JsonConnector(f.Path) :> IConnector
        let! ok = connector.TestConnectionAsync(CancellationToken.None)
        Assert.True(ok)

        let missing = JsonConnector("C:/non_existent_folder/missing.json") :> IConnector
        let! notOk = missing.TestConnectionAsync(CancellationToken.None)
        Assert.False(notOk)
    }

[<Fact>]
let ``GetColumnNames returns columns for discovered table`` () =
    let json = """[ { "ColA": 1, "ColB": "text" } ]"""
    use f = new TempJsonFile(json)
    let connector = JsonConnector(f.Path)
    let cols = connector.ReadRows().Columns
    Assert.Equal<string list>([ "ColA"; "ColB" ], cols)

[<Fact>]
let ``JsonConnector handles missing files, primitive arrays with transforms, JsonPath edge cases and explicit ColumnTypes``
    ()
    =
    let missing = JsonConnector("C:/non_existent_folder_12345/missing.json")
    Assert.Empty(missing.ReadRows().Columns)
    Assert.Empty((missing :> IConnector).DiscoverSchemaAsync(CancellationToken.None).Result)

    use fPrim = new TempJsonFile("[ 10, 20, 30 ]")

    let optsPrim =
        { JsonOptions.Default with
            JsonPath = Some "."
            ColumnTransforms = Map.ofList [ "Value", "multiply(10)" ]
            ColumnTypes = Map.ofList [ "Value", "int" ]
        }

    let connPrim = JsonConnector(fPrim.Path, optsPrim)
    let qrPrim = connPrim.ReadRows()
    let rowsPrim = qrPrim.Rows |> Seq.toList
    Assert.Equal(box "100", rowsPrim.[0].[0])

    let tablesPrim =
        (connPrim :> IConnector).DiscoverSchemaAsync(CancellationToken.None).Result

    Assert.Equal("int", tablesPrim.[0].Columns.[0].DataType)

    let connMissingPath =
        JsonConnector(
            fPrim.Path,
            { JsonOptions.Default with
                JsonPath = Some "nonexistent.path"
            }
        )

    Assert.Empty(connMissingPath.ReadRows().Columns)

    use fScalar = new TempJsonFile("42")
    let connScalar = JsonConnector(fScalar.Path)
    Assert.Equal(1, connScalar.ReadRows().Rows |> Seq.length)

[<Fact>]
let ``JsonDescriptor properties and validation scenario 1`` () =
    let desc = JsonDescriptor.Instance :> IConnectorDescriptor
    Assert.Equal("Json", desc.KindId)
    Assert.Equal("JSON File", desc.DisplayName)
    Assert.Equal(ConnectorCategory.Json, desc.Category)
    Assert.Equal("json", desc.Icon)
    Assert.True(Option.isNone desc.EntityGenerator)
    Assert.NotEmpty(desc.FormFields)

    use doc = System.Text.Json.JsonDocument.Parse("""{"filePath": "/path/to/data.json"}""")
    let validRes = desc.ValidateConfiguration doc.RootElement
    Assert.True(Result.isOk validRes)

[<Fact>]
let ``JsonDescriptor properties and validation scenario 2`` () =
    let desc = JsonDescriptor.Instance :> IConnectorDescriptor
    use docInvalid = System.Text.Json.JsonDocument.Parse("""{}""")
    let invalidRes = desc.ValidateConfiguration docInvalid.RootElement
    Assert.True(Result.isError invalidRes)

[<Fact>]
let ``JsonConnectorFactory creates connector from string or raw text scenario 1`` () =
    let desc = JsonDescriptor.Instance :> IConnectorDescriptor
    let factory = desc.Factory
    let secretStore = FSharpQueryStudio.Security.InMemorySecretStore() :> FSharpQueryStudio.Security.ISecretStore

    use docStr = System.Text.Json.JsonDocument.Parse("\"/my/file.json\"")
    let conn = factory.Create(docStr.RootElement, secretStore, None)
    Assert.NotNull(conn)
    Assert.Equal(ConnectorKind.Json, conn.Kind)

[<Fact>]
let ``JsonConnectorFactory creates connector from empty or object configuration scenario 2`` () =
    let factory = JsonConnectorFactory() :> IConnectorFactory
    let secretStore = FSharpQueryStudio.Security.InMemorySecretStore() :> FSharpQueryStudio.Security.ISecretStore

    use docEmptyStr = System.Text.Json.JsonDocument.Parse("\"\"")
    let conn1 = factory.Create(docEmptyStr.RootElement, secretStore, None)
    Assert.NotNull(conn1)

    use docObj = System.Text.Json.JsonDocument.Parse("{\"filePath\": \"/data/test.json\", \"flattenNested\": false}")
    let conn2 = factory.Create(docObj.RootElement, secretStore, None)
    Assert.NotNull(conn2)
