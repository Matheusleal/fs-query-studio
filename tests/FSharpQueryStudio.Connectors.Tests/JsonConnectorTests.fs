module JsonConnectorTests

open System
open System.IO
open System.Threading
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors

type private TempJsonFile(content: string) =
    let path = Path.Combine(Path.GetTempPath(), "fqs-json-" + Guid.NewGuid().ToString("N") + ".json")
    do File.WriteAllText(path, content)
    member _.Path = path
    interface IDisposable with
        member _.Dispose() =
            if File.Exists path then
                File.Delete path

[<Fact>]
let ``DiscoverSchemaAsync discovers multiple top-level arrays and infers column types`` () =
    task {
        let json = """{
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
        let colMap = products.Columns |> List.map (fun c -> c.Name, c.DataType) |> Map.ofList
        Assert.Equal("int", colMap.["ProductId"])
        Assert.Equal("string", colMap.["Title"])
        Assert.Equal("decimal", colMap.["Price"])
        Assert.Equal("bool", colMap.["InStock"])
    }

[<Fact>]
let ``ReadRows flattens nested objects with underscore separator when FlattenNested is true`` () =
    let json = """[
  { "Id": 1, "Customer": { "Name": "Alice", "City": "London" }, "Tags": ["vip", "uk"] },
  { "Id": 2, "Customer": { "Name": "Bob", "City": "Austin" }, "Tags": ["us"] }
]"""
    use f = new TempJsonFile(json)
    let connector = JsonConnector(f.Path, { JsonOptions.Default with FlattenNested = true })

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
    let json = """{
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
    let connector = JsonConnector(f.Path, { JsonOptions.Default with JsonPath = Some "response.payload.items" })

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

