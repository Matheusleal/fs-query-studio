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

