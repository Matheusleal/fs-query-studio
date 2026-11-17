module XmlConnectorTests

open System
open System.IO
open System.Threading
open System.Xml
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors

type private TempXmlFile(content: string) =
    let path = Path.Combine(Path.GetTempPath(), "fqs-xml-" + Guid.NewGuid().ToString("N") + ".xml")
    do File.WriteAllText(path, content)
    member _.Path = path
    interface IDisposable with
        member _.Dispose() =
            if File.Exists path then
                File.Delete path

[<Fact>]
let ``DiscoverSchemaAsync discovers multiple repeating element collections and extracts attributes plus elements`` () =
    task {
        let xml = """<?xml version="1.0" encoding="utf-8"?>
<LogisticsHub>
  <Carriers>
    <Carrier CarrierId="1" Code="DHL-INT" Name="DHL Global Express" OnTimeRate="98.4" />
    <Carrier CarrierId="2" Code="FDX-PRI" Name="FedEx Priority Air" OnTimeRate="97.8" />
  </Carriers>
  <Shipments>
    <Shipment ShipmentId="9001" CarrierId="1">
      <Destination>Sao Paulo</Destination>
      <ShippingCost>128.90</ShippingCost>
    </Shipment>
    <Shipment ShipmentId="9002" CarrierId="2">
      <Destination>London</Destination>
      <ShippingCost>94.50</ShippingCost>
    </Shipment>
  </Shipments>
</LogisticsHub>"""
        use f = new TempXmlFile(xml)
        let connector = XmlConnector(f.Path) :> IConnector

        let! tables = connector.DiscoverSchemaAsync(CancellationToken.None)
        let tableNames = tables |> List.map (fun t -> t.Name) |> List.sort
        Assert.Equal<string list>([ "Carriers"; "Shipments" ], tableNames)

        let shipments = tables |> List.find (fun t -> t.Name = "Shipments")
        let colMap = shipments.Columns |> List.map (fun c -> c.Name, c.DataType) |> Map.ofList
        Assert.Equal("int", colMap.["ShipmentId"])
        Assert.Equal("int", colMap.["CarrierId"])
        Assert.Equal("string", colMap.["Destination"])
        Assert.Equal("decimal", colMap.["ShippingCost"])
    }

[<Fact>]
let ``XmlConnector prohibits DTD processing to prevent XXE attacks`` () =
    let xxeXml = """<?xml version="1.0" encoding="utf-8"?>
<!DOCTYPE foo [
  <!ELEMENT foo ANY >
  <!ENTITY xxe SYSTEM "file:///etc/passwd" >
]>
<Root>
  <Item><Name>&xxe;</Name></Item>
  <Item><Name>Safe</Name></Item>
</Root>"""
    use f = new TempXmlFile(xxeXml)
    let connector = XmlConnector(f.Path)

    Assert.Throws<XmlException>(fun () -> connector.ReadRows() |> ignore) |> ignore

[<Fact>]
let ``XmlConnector reports its Kind as Xml`` () =
    use f = new TempXmlFile("<Root><Item Id=\"1\"/><Item Id=\"2\"/></Root>")
    let connector = XmlConnector(f.Path) :> IConnector
    Assert.Equal(ConnectorKind.Xml, connector.Kind)

[<Fact>]
let ``ReadRows respects maxRows limit in XML`` () =
    let xml = """<Catalog><Item Id="1"/><Item Id="2"/><Item Id="3"/></Catalog>"""
    use f = new TempXmlFile(xml)
    let connector = XmlConnector(f.Path)
    let result = connector.ReadRows(maxRows = 2)
    Assert.Equal(2, result.Rows |> Seq.length)

[<Fact>]
let ``ReadRows respects RowElement option path`` () =
    let xml = """<Store><Inventory><Gadget Name="Drone" Price="500"/><Gadget Name="Cam" Price="300"/></Inventory></Store>"""
    use f = new TempXmlFile(xml)
    let opts = { XmlOptions.Default with RowElement = Some "Gadget" }
    let connector = XmlConnector(f.Path, opts)
    let result = connector.ReadRows()
    Assert.Equal<string list>([ "Name"; "Price" ], result.Columns)
    Assert.Equal(2, result.Rows |> Seq.length)

[<Fact>]
let ``ReadRows flattens nested elements with underscore`` () =
    let xml = """<Catalog><Item><Id>1</Id><Details><Origin>BR</Origin></Details></Item></Catalog>"""
    use f = new TempXmlFile(xml)
    let opts = { XmlOptions.Default with FlattenNested = true }
    let connector = XmlConnector(f.Path, opts)
    let result = connector.ReadRows()
    Assert.Contains("Details_Origin", result.Columns)

[<Fact>]
let ``ReadRows applies column transforms on XML columns`` () =
    let xml = """<Catalog><Item><Name>banana</Name></Item></Catalog>"""
    use f = new TempXmlFile(xml)
    let opts = { XmlOptions.Default with ColumnTransforms = Map.ofList [ "Name", "replace('banana', 'fruit')" ] }
    let connector = XmlConnector(f.Path, opts)
    let result = connector.ReadRows()
    let rows = result.Rows |> Seq.toList
    Assert.Equal(box "fruit", rows.[0].[0])

[<Fact>]
let ``TestConnectionAsync returns true when XML exists and false when missing`` () =
    task {
        use f = new TempXmlFile("<Root><Item/></Root>")
        let connector = XmlConnector(f.Path) :> IConnector
        let! ok = connector.TestConnectionAsync(CancellationToken.None)
        Assert.True(ok)

        let missing = XmlConnector("C:/non_existent_folder/missing.xml") :> IConnector
        let! notOk = missing.TestConnectionAsync(CancellationToken.None)
        Assert.False(notOk)
    }

[<Fact>]
let ``GetColumnNames returns columns for discovered XML table`` () =
    let xml = """<Root><Record Id="10" Description="Widget"/></Root>"""
    use f = new TempXmlFile(xml)
    let connector = XmlConnector(f.Path)
    let cols = connector.ReadRows().Columns
    Assert.Equal<string list>([ "Id"; "Description" ], cols)

[<Fact>]
let ``XmlConnector handles missing files, FlattenNested false, IncludeAttributes false, and explicit ColumnTypes`` () =
    let missing = XmlConnector("C:/non_existent_folder_12345/missing.xml")
    Assert.Empty(missing.ReadRows().Columns)
    Assert.Empty((missing :> IConnector).DiscoverSchemaAsync(CancellationToken.None).Result)

    let xml = """<Root><Item Code="A1"><Nested><Child>Val1</Child></Nested><Leaf Attr="X">TextVal</Leaf></Item><Item Code="A2"><Nested><Child>Val2</Child></Nested><Leaf Attr="Y">TextVal2</Leaf></Item></Root>"""
    use f = new TempXmlFile(xml)
    let opts =
        { XmlOptions.Default with
            FlattenNested = false
            IncludeAttributes = false
            ColumnTypes = Map.ofList [ "Leaf", "string" ] }
    let conn = XmlConnector(f.Path, opts)
    let qr = conn.ReadRows()
    Assert.Contains("Nested", qr.Columns)
    Assert.Contains("Leaf", qr.Columns)

    let tables = (conn :> IConnector).DiscoverSchemaAsync(CancellationToken.None).Result
    let leafCol = tables.[0].Columns |> List.find (fun c -> c.Name = "Leaf")
    Assert.Equal("string", leafCol.DataType)



