module XmlConnectorTests

open System
open System.IO
open System.Threading
open System.Xml
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors

type private TempXmlFile(content: string) =
    let path =
        Path.Combine(Path.GetTempPath(), "fqs-xml-" + Guid.NewGuid().ToString("N") + ".xml")

    do File.WriteAllText(path, content)
    member _.Path = path

    interface IDisposable with
        member _.Dispose() =
            if File.Exists path then
                File.Delete path

[<Fact>]
let ``DiscoverSchemaAsync discovers multiple repeating element collections and extracts attributes plus elements`` () =
    task {
        let xml =
            """<?xml version="1.0" encoding="utf-8"?>
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

        let colMap =
            shipments.Columns |> List.map (fun c -> c.Name, c.DataType) |> Map.ofList

        Assert.Equal("int", colMap.["ShipmentId"])
        Assert.Equal("int", colMap.["CarrierId"])
        Assert.Equal("string", colMap.["Destination"])
        Assert.Equal("decimal", colMap.["ShippingCost"])
    }

[<Fact>]
let ``XmlConnector prohibits DTD processing to prevent XXE attacks`` () =
    let xxeXml =
        """<?xml version="1.0" encoding="utf-8"?>
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
    let xml =
        """<Store><Inventory><Gadget Name="Drone" Price="500"/><Gadget Name="Cam" Price="300"/></Inventory></Store>"""

    use f = new TempXmlFile(xml)

    let opts =
        { XmlOptions.Default with
            RowElement = Some "Gadget"
        }

    let connector = XmlConnector(f.Path, opts)
    let result = connector.ReadRows()
    Assert.Equal<string list>([ "Name"; "Price" ], result.Columns)
    Assert.Equal(2, result.Rows |> Seq.length)

[<Fact>]
let ``ReadRows flattens nested elements with underscore`` () =
    let xml =
        """<Catalog><Item><Id>1</Id><Details><Origin>BR</Origin></Details></Item></Catalog>"""

    use f = new TempXmlFile(xml)

    let opts =
        { XmlOptions.Default with
            FlattenNested = true
        }

    let connector = XmlConnector(f.Path, opts)
    let result = connector.ReadRows()
    Assert.Contains("Details_Origin", result.Columns)

[<Fact>]
let ``ReadRows applies column transforms on XML columns`` () =
    let xml = """<Catalog><Item><Name>banana</Name></Item></Catalog>"""
    use f = new TempXmlFile(xml)

    let opts =
        { XmlOptions.Default with
            ColumnTransforms = Map.ofList [ "Name", "replace('banana', 'fruit')" ]
        }

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

    let xml =
        """<Root><Item Code="A1"><Nested><Child>Val1</Child></Nested><Leaf Attr="X">TextVal</Leaf></Item><Item Code="A2"><Nested><Child>Val2</Child></Nested><Leaf Attr="Y">TextVal2</Leaf></Item></Root>"""

    use f = new TempXmlFile(xml)

    let opts =
        { XmlOptions.Default with
            FlattenNested = false
            IncludeAttributes = false
            ColumnTypes = Map.ofList [ "Leaf", "string" ]
        }

    let conn = XmlConnector(f.Path, opts)
    let qr = conn.ReadRows()
    Assert.Contains("Nested", qr.Columns)
    Assert.Contains("Leaf", qr.Columns)

    let tables = (conn :> IConnector).DiscoverSchemaAsync(CancellationToken.None).Result
    let leafCol = tables.[0].Columns |> List.find (fun c -> c.Name = "Leaf")
    Assert.Equal("string", leafCol.DataType)

[<Fact>]
let ``XmlOptions Default record values scenario 1`` () =
    let def = XmlOptions.Default
    Assert.True(def.RowElement.IsNone)
    Assert.True(def.IncludeAttributes)
    Assert.True(def.FlattenNested)
    Assert.True(def.ColumnTypes.IsEmpty)
    Assert.True(def.ColumnTransforms.IsEmpty)

[<Fact>]
let ``XmlOptions Default copy with modification scenario 2`` () =
    let custom =
        { XmlOptions.Default with
            RowElement = Some "CustomRow"
            IncludeAttributes = false
        }

    Assert.Equal(Some "CustomRow", custom.RowElement)
    Assert.False(custom.IncludeAttributes)
    Assert.True(custom.FlattenNested)

[<Fact>]
let ``XmlConnector parses element with both attributes and text content scenario 1`` () =
    let xml =
        """<Root><Entry id="101" category="general">Important Note</Entry><Entry id="102" category="urgent">Action Required</Entry></Root>"""

    use f = new TempXmlFile(xml)
    let conn = XmlConnector(f.Path)
    let result = conn.ReadRows()
    Assert.Contains("id", result.Columns)
    Assert.Contains("category", result.Columns)
    Assert.Contains("Entry_Value", result.Columns)
    let rows = result.Rows |> Seq.toArray
    Assert.Equal(2, rows.Length)
    Assert.Equal(box "Important Note", rows.[0].[2])

[<Fact>]
let ``XmlConnector parses element with both attributes and text content scenario 2`` () =
    let xml = """<Catalog><Product code="P1">Widget</Product></Catalog>"""
    use f = new TempXmlFile(xml)
    let conn = XmlConnector(f.Path)
    let result = conn.ReadRows()
    Assert.Contains("code", result.Columns)
    Assert.Contains("Product_Value", result.Columns)
    let rows = result.Rows |> Seq.toArray
    Assert.Single(rows) |> ignore
    Assert.Equal(box "Widget", rows.[0].[1])

[<Fact>]
let ``XmlConnector handles root element without child elements scenario 1`` () =
    let xml = """<SingleRecord>Sole Content</SingleRecord>"""
    use f = new TempXmlFile(xml)
    let conn = XmlConnector(f.Path)
    let qr = conn.ReadRows()
    Assert.Contains("Value", qr.Columns)
    let rows = qr.Rows |> Seq.toArray
    Assert.Single(rows) |> ignore
    Assert.Equal(box "Sole Content", rows.[0].[0])

[<Fact>]
let ``XmlConnector handles root element with empty RowElement and target tag matching root scenario 2`` () =
    let xml = """<Item id="1">Isolated</Item>"""
    use f = new TempXmlFile(xml)

    let opts =
        { XmlOptions.Default with
            RowElement = Some "Item"
        }

    let conn = XmlConnector(f.Path, opts)
    let qr = conn.ReadRows()
    Assert.Contains("id", qr.Columns)
    Assert.Contains("Item_Value", qr.Columns)

[<Fact>]
let ``XmlConnector ReadRaw with row limit and without row limit scenario 1`` () =
    let xml =
        """<Data><Item><Val>1</Val></Item><Item><Val>2</Val></Item><Item><Val>3</Val></Item></Data>"""

    use f = new TempXmlFile(xml)
    let conn = XmlConnector(f.Path) :> IStagableConnector
    let rawLimited = conn.ReadRaw(Some 2)
    Assert.Equal(2, rawLimited.Rows |> Seq.length)
    let rawAll = conn.ReadRaw(None)
    Assert.Equal(3, rawAll.Rows |> Seq.length)

[<Fact>]
let ``XmlConnector StagePrimaryTable with row element None and Some scenario 2`` () =
    let xml = """<Data><Item><Val>10</Val></Item></Data>"""
    use f = new TempXmlFile(xml)

    let dbPath =
        Path.Combine(Path.GetTempPath(), $"fqs-xml-stage-{Guid.NewGuid():N}.db")

    try
        let engine = FileSqliteEngine(dbPath) :> IStagingEngine
        let conn1 = XmlConnector(f.Path) :> IStagableConnector
        let tbl1 = conn1.StagePrimaryTable(engine, "conn1")
        Assert.False(String.IsNullOrWhiteSpace tbl1)

        let conn2 =
            XmlConnector(
                f.Path,
                { XmlOptions.Default with
                    RowElement = Some "Item"
                }
            )
            :> IStagableConnector

        let tbl2 = conn2.StagePrimaryTable(engine, "conn2")
        Assert.False(String.IsNullOrWhiteSpace tbl2)
    finally
        if File.Exists dbPath then
            File.Delete dbPath

[<Fact>]
let ``XmlDescriptor properties and validation scenario 1`` () =
    let desc = XmlDescriptor.Instance :> IConnectorDescriptor
    Assert.Equal("Xml", desc.KindId)
    Assert.Equal("XML File", desc.DisplayName)
    Assert.Equal(ConnectorCategory.Xml, desc.Category)
    Assert.Equal("code", desc.Icon)
    Assert.True(Option.isNone desc.EntityGenerator)
    Assert.NotEmpty(desc.FormFields)

    use doc =
        System.Text.Json.JsonDocument.Parse("""{"filePath": "/path/to/data.xml"}""")

    let validRes = desc.ValidateConfiguration doc.RootElement
    Assert.True(Result.isOk validRes)

[<Fact>]
let ``XmlDescriptor properties and validation scenario 2`` () =
    let desc = XmlDescriptor.Instance :> IConnectorDescriptor
    use docInvalid = System.Text.Json.JsonDocument.Parse("""{}""")
    let invalidRes = desc.ValidateConfiguration docInvalid.RootElement
    Assert.True(Result.isError invalidRes)

[<Fact>]
let ``XmlConnectorFactory creates connector from string or raw text scenario 1`` () =
    let desc = XmlDescriptor.Instance :> IConnectorDescriptor
    let factory = desc.Factory

    let secretStore =
        FSharpQueryStudio.Security.InMemorySecretStore() :> FSharpQueryStudio.Security.ISecretStore

    use docStr = System.Text.Json.JsonDocument.Parse("\"/my/file.xml\"")
    let conn = factory.Create(docStr.RootElement, secretStore, None)
    Assert.NotNull(conn)
    Assert.Equal(ConnectorKind.Xml, conn.Kind)

[<Fact>]
let ``XmlConnectorFactory creates connector from empty or object configuration scenario 2`` () =
    let factory = XmlConnectorFactory() :> IConnectorFactory

    let secretStore =
        FSharpQueryStudio.Security.InMemorySecretStore() :> FSharpQueryStudio.Security.ISecretStore

    use docEmptyStr = System.Text.Json.JsonDocument.Parse("\"\"")
    let conn1 = factory.Create(docEmptyStr.RootElement, secretStore, None)
    Assert.NotNull(conn1)

    use docObj =
        System.Text.Json.JsonDocument.Parse(
            "{\"filePath\": \"/data/test.xml\", \"rowElement\": \"Item\", \"includeAttributes\": false}"
        )

    let conn2 = factory.Create(docObj.RootElement, secretStore, None)
    Assert.NotNull(conn2)
