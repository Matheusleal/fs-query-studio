module CsvConnectorTests

open System
open System.IO
open System.Text
open System.Threading
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors

let private tempCsvFile (encoding: Encoding) (content: string) : string =
    let path = Path.Combine(Path.GetTempPath(), "fqs-csv-" + Guid.NewGuid().ToString("N") + ".csv")
    File.WriteAllText(path, content, encoding)
    path

[<Fact>]
let ``ReadRows parses a header row and each data row as columns/rows`` () =
    let path = tempCsvFile Encoding.UTF8 "Id,Name,Total\n1,Sales,100.0\n2,Inventory,50.0\n"
    let connector = CsvConnector(path)

    let result = connector.ReadRows()

    Assert.Equal<string list>([ "Id"; "Name"; "Total" ], result.Columns)
    let rows = result.Rows |> Seq.toList
    Assert.Equal<obj list>([ box "1"; box "Sales"; box "100.0" ], rows.[0] |> Array.toList)
    Assert.Equal<obj list>([ box "2"; box "Inventory"; box "50.0" ], rows.[1] |> Array.toList)

[<Fact>]
let ``ReadRows keeps a quoted field's comma inside a single field, not splitting it`` () =
    let path = tempCsvFile Encoding.UTF8 "Id,Description\n1,\"Sales, EMEA\"\n"
    let connector = CsvConnector(path)

    let rows = connector.ReadRows().Rows |> Seq.toList

    Assert.Equal<obj list>([ box "1"; box "Sales, EMEA" ], rows.[0] |> Array.toList)

[<Fact>]
let ``ReadRows keeps a quoted field's embedded newline inside a single field, not ending the row`` () =
    let path = tempCsvFile Encoding.UTF8 "Id,Notes\n1,\"line one\nline two\"\n2,plain\n"
    let connector = CsvConnector(path)

    let rows = connector.ReadRows().Rows |> Seq.toList

    Assert.Equal(2, rows.Length)
    Assert.Equal<obj list>([ box "1"; box "line one\nline two" ], rows.[0] |> Array.toList)
    Assert.Equal<obj list>([ box "2"; box "plain" ], rows.[1] |> Array.toList)

[<Fact>]
let ``ReadRows unescapes a doubled quote inside a quoted field`` () =
    let path = tempCsvFile Encoding.UTF8 "Id,Quote\n1,\"She said \"\"hi\"\"\"\n"
    let connector = CsvConnector(path)

    let rows = connector.ReadRows().Rows |> Seq.toList

    Assert.Equal<obj list>([ box "1"; box "She said \"hi\"" ], rows.[0] |> Array.toList)

[<Fact>]
let ``ReadRows reads a non-UTF8 encoded file correctly when Encoding is set`` () =
    let latin1 = Encoding.GetEncoding("iso-8859-1")
    let path = tempCsvFile latin1 "Id,City\n1,São Paulo\n"
    let connector = CsvConnector(path, { CsvOptions.Default with Encoding = latin1 })

    let rows = connector.ReadRows().Rows |> Seq.toList

    Assert.Equal<obj list>([ box "1"; box "São Paulo" ], rows.[0] |> Array.toList)

[<Fact>]
let ``ReadRows generates Column1/Column2 names and includes the first row as data when HasHeader is false`` () =
    let path = tempCsvFile Encoding.UTF8 "1,Sales\n2,Inventory\n"
    let connector = CsvConnector(path, { CsvOptions.Default with HasHeader = false })

    let result = connector.ReadRows()

    Assert.Equal<string list>([ "Column1"; "Column2" ], result.Columns)
    let rows = result.Rows |> Seq.toList
    Assert.Equal(2, rows.Length)
    Assert.Equal<obj list>([ box "1"; box "Sales" ], rows.[0] |> Array.toList)

[<Fact>]
let ``ReadRows splits on a custom delimiter`` () =
    let path = tempCsvFile Encoding.UTF8 "Id;Name\n1;Sales\n"
    let connector = CsvConnector(path, { CsvOptions.Default with Delimiter = ';' })

    let result = connector.ReadRows()

    Assert.Equal<string list>([ "Id"; "Name" ], result.Columns)
    Assert.Equal<obj list>([ box "1"; box "Sales" ], (result.Rows |> Seq.exactlyOne) |> Array.toList)

[<Fact>]
let ``TestConnectionAsync returns true for an existing file and false for a missing one`` () =
    task {
        let path = tempCsvFile Encoding.UTF8 "Id\n1\n"
        let existing = CsvConnector(path) :> IConnector
        let missing = CsvConnector(path + ".does-not-exist") :> IConnector

        let! existingResult = existing.TestConnectionAsync(CancellationToken.None)
        let! missingResult = missing.TestConnectionAsync(CancellationToken.None)

        Assert.True(existingResult)
        Assert.False(missingResult)
    }

[<Fact>]
let ``DiscoverSchemaAsync reports one table named after the file, with header-derived columns`` () =
    task {
        let path = tempCsvFile Encoding.UTF8 "Id,Name\n1,Sales\n"
        let connector = CsvConnector(path) :> IConnector

        let! tables = connector.DiscoverSchemaAsync(CancellationToken.None)

        let table = tables |> List.exactlyOne
        Assert.Equal(Path.GetFileNameWithoutExtension(path), table.Name)
        Assert.Equal<string list>([ "Id"; "Name" ], table.Columns |> List.map (fun c -> c.Name))
    }

[<Fact>]
let ``CsvConnector reports its Kind as Csv`` () =
    let path = tempCsvFile Encoding.UTF8 "Id\n1\n"
    let connector = CsvConnector(path) :> IConnector

    Assert.Equal(ConnectorKind.Csv, connector.Kind)

[<Fact>]
let ``typed conversion helpers parse valid text and reject invalid text`` () =
    Assert.Equal(Some 42, CsvConversions.tryParseInt "42")
    Assert.Equal(None, CsvConversions.tryParseInt "not-a-number")

    Assert.Equal(Some 3.5, CsvConversions.tryParseFloat "3.5")
    Assert.Equal(None, CsvConversions.tryParseFloat "not-a-float")

    Assert.Equal(Some true, CsvConversions.tryParseBool "true")
    Assert.Equal(None, CsvConversions.tryParseBool "not-a-bool")

    Assert.Equal(Some(DateTime(2026, 1, 15)), CsvConversions.tryParseDateTime "2026-01-15")
    Assert.Equal(None, CsvConversions.tryParseDateTime "not-a-date")
