module ExcelConnectorTests

open System
open System.IO
open System.Threading
open ClosedXML.Excel
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors

/// A temp `.xlsx` fixture with two sheets: "Orders" (header row, three
/// data rows) and "NoHeader" (no header row, just raw data). Deleted
/// when the caller's `use` scope ends.
type private TempWorkbook() =
    let path = Path.Combine(Path.GetTempPath(), "fqs-xlsx-" + Guid.NewGuid().ToString("N") + ".xlsx")

    do
        use workbook = new XLWorkbook()
        let orders = workbook.Worksheets.Add("Orders")
        orders.Cell(1, 1).Value <- "Id"
        orders.Cell(1, 2).Value <- "CustomerId"
        orders.Cell(1, 3).Value <- "Total"
        orders.Cell(2, 1).Value <- 1
        orders.Cell(2, 2).Value <- 42
        orders.Cell(2, 3).Value <- 100.0
        orders.Cell(3, 1).Value <- 2
        orders.Cell(3, 2).Value <- 43
        orders.Cell(3, 3).Value <- 50.0
        orders.Cell(4, 1).Value <- 3
        orders.Cell(4, 2).Value <- 44
        orders.Cell(4, 3).Value <- 25.0

        let noHeader = workbook.Worksheets.Add("NoHeader")
        noHeader.Cell(1, 1).Value <- "a"
        noHeader.Cell(1, 2).Value <- "b"
        noHeader.Cell(2, 1).Value <- "c"
        noHeader.Cell(2, 2).Value <- "d"

        workbook.SaveAs(path)

    member _.Path = path

    interface IDisposable with
        member _.Dispose() =
            if File.Exists path then
                File.Delete path

[<Fact>]
let ``ReadRows reads the first sheet's header and data rows by default`` () =
    use workbook = new TempWorkbook()
    let connector = ExcelConnector(workbook.Path)

    let result = connector.ReadRows()

    Assert.Equal<string list>([ "Id"; "CustomerId"; "Total" ], result.Columns)
    let rows = result.Rows |> Seq.toList
    Assert.Equal(3, rows.Length)
    Assert.Equal(box 1.0, rows.[0].[0])

[<Fact>]
let ``ReadRows selects the named sheet when multiple sheets exist`` () =
    use workbook = new TempWorkbook()

    let connector =
        ExcelConnector(workbook.Path, { ExcelOptions.Default with SheetName = Some "NoHeader" })

    let result = connector.ReadRows()

    // NoHeader defaults HasHeader = true (ExcelOptions.Default), so its
    // first row ("a","b") is treated as the header here.
    Assert.Equal<string list>([ "a"; "b" ], result.Columns)
    Assert.Equal<obj list>([ box "c"; box "d" ], result.Rows |> Seq.exactlyOne |> Array.toList)

[<Fact>]
let ``ReadRows generates Column1/Column2 names and treats every row as data when HasHeader is false`` () =
    use workbook = new TempWorkbook()

    let connector =
        ExcelConnector(
            workbook.Path,
            { ExcelOptions.Default with
                SheetName = Some "NoHeader"
                HasHeader = false }
        )

    let result = connector.ReadRows()

    Assert.Equal<string list>([ "Column1"; "Column2" ], result.Columns)
    let rows = result.Rows |> Seq.toList
    Assert.Equal(2, rows.Length)
    Assert.Equal<obj list>([ box "a"; box "b" ], rows.[0] |> Array.toList)
    Assert.Equal<obj list>([ box "c"; box "d" ], rows.[1] |> Array.toList)

[<Fact>]
let ``ReadRows bounds the data rows returned to maxRows`` () =
    use workbook = new TempWorkbook()
    let connector = ExcelConnector(workbook.Path)

    let result = connector.ReadRows(maxRows = 2)

    Assert.Equal(2, result.Rows |> Seq.length)

[<Fact>]
let ``TestConnectionAsync returns true for an existing file and false for a missing one`` () =
    task {
        use workbook = new TempWorkbook()
        let existing = ExcelConnector(workbook.Path) :> IConnector
        let missing = ExcelConnector(workbook.Path + ".does-not-exist") :> IConnector

        let! existingResult = existing.TestConnectionAsync(CancellationToken.None)
        let! missingResult = missing.TestConnectionAsync(CancellationToken.None)

        Assert.True(existingResult)
        Assert.False(missingResult)
    }

[<Fact>]
let ``DiscoverSchemaAsync reports every sheet as a table with its header-derived columns`` () =
    task {
        use workbook = new TempWorkbook()
        let connector = ExcelConnector(workbook.Path) :> IConnector

        let! tables = connector.DiscoverSchemaAsync(CancellationToken.None)

        let tableNames = tables |> List.map (fun t -> t.Name) |> List.sort
        Assert.Equal<string list>([ "NoHeader"; "Orders" ], tableNames)

        let orders = tables |> List.find (fun t -> t.Name = "Orders")
        Assert.Equal<string list>([ "Id"; "CustomerId"; "Total" ], orders.Columns |> List.map (fun c -> c.Name))
    }

[<Fact>]
let ``ExcelConnector reports its Kind as Excel`` () =
    use workbook = new TempWorkbook()
    let connector = ExcelConnector(workbook.Path) :> IConnector

    Assert.Equal(ConnectorKind.Excel, connector.Kind)
