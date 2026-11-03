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
let ``DiscoverSchemaAsync reports inferred types instead of hardcoded string`` () =
    task {
        use workbook = new TempWorkbook()
        let connector = ExcelConnector(workbook.Path) :> IConnector

        let! tables = connector.DiscoverSchemaAsync(CancellationToken.None)
        let orders = tables |> List.find (fun t -> t.Name = "Orders")

        let colMap = orders.Columns |> List.map (fun c -> c.Name, c.DataType) |> Map.ofList
        Assert.Equal("int", colMap.["Id"])
        Assert.Equal("int", colMap.["CustomerId"])
        // Total has floating point 100.0, 50.0, 25.0
        Assert.True(colMap.["Total"] = "int" || colMap.["Total"] = "decimal" || colMap.["Total"] = "double")
    }

[<Fact>]
let ``DiscoverSchemaAsync respects explicit ColumnTypes override`` () =
    task {
        use workbook = new TempWorkbook()
        let opts =
            { ExcelOptions.Default with
                ColumnTypes = Map.ofList [ "Total", "decimal"; "CustomerId", "int64" ] }
        let connector = ExcelConnector(workbook.Path, opts) :> IConnector

        let! tables = connector.DiscoverSchemaAsync(CancellationToken.None)
        let orders = tables |> List.find (fun t -> t.Name = "Orders")

        let colMap = orders.Columns |> List.map (fun c -> c.Name, c.DataType) |> Map.ofList
        Assert.Equal("decimal", colMap.["Total"])
        Assert.Equal("int64", colMap.["CustomerId"])
    }

[<Fact>]
let ``ExcelConnector applies column transformations such as replace and trim`` () =
    let path = Path.Combine(Path.GetTempPath(), "fqs-transform-" + Guid.NewGuid().ToString("N") + ".xlsx")
    try
        do
            use wb = new XLWorkbook()
            let ws = wb.Worksheets.Add("Data")
            ws.Cell(1, 1).Value <- "RawPrice"
            ws.Cell(1, 2).Value <- "RawCode"
            ws.Cell(2, 1).Value <- "1.234,56"
            ws.Cell(2, 2).Value <- "  ABC-123  "
            wb.SaveAs(path)

        let opts =
            { ExcelOptions.Default with
                ColumnTransforms =
                    Map.ofList [
                        "RawPrice", "replace('.', '').replace(',', '.')"
                        "RawCode", "trim"
                    ]
                ColumnTypes = Map.ofList [ "RawPrice", "decimal" ] }

        let connector = ExcelConnector(path, opts)
        let qr = connector.ReadRows()
        let rows = qr.Rows |> Seq.toList
        Assert.Single(rows) |> ignore
        Assert.Equal(box "1234.56", rows.[0].[0])
        Assert.Equal(box "ABC-123", rows.[0].[1])
    finally
        if File.Exists path then File.Delete path

[<Fact>]
let ``FileSqliteEngine preserves long numbers with many decimal places without rounding to int`` () =
    let dbPath = Path.Combine(Path.GetTempPath(), "fqs-decimal-" + Guid.NewGuid().ToString("N") + ".db")
    let csvPath = Path.Combine(Path.GetTempPath(), "fqs-decimal-" + Guid.NewGuid().ToString("N") + ".csv")
    try
        File.WriteAllLines(csvPath, [|
            "Id,HighPrecision"
            "1,1234567.890123456"
            "2,9876543.210987654"
        |])

        let engine = FileSqliteEngine(dbPath)
        let connector = CsvConnector(csvPath)

        let stagedTable = engine.EnsureStaged("PrecisionData", csvPath, (fun () -> connector.ReadRows()))
        Assert.Equal("PrecisionData", stagedTable)

        let res = engine.ExecuteQuery("SELECT HighPrecision FROM PrecisionData ORDER BY Id;")
        let rows = res.Rows |> Seq.toList
        Assert.Equal(2, rows.Length)

        let val1 = Convert.ToDouble(rows.[0].[0], System.Globalization.CultureInfo.InvariantCulture)
        let val2 = Convert.ToDouble(rows.[1].[0], System.Globalization.CultureInfo.InvariantCulture)

        // Must preserve the decimals, NOT rounded to 1234568 or 9876543
        Assert.True(Math.Abs(val1 - 1234567.890123456) < 0.000001)
        Assert.True(Math.Abs(val2 - 9876543.210987654) < 0.000001)
    finally
        try if File.Exists dbPath then File.Delete dbPath with _ -> ()
        try if File.Exists csvPath then File.Delete csvPath with _ -> ()

[<Fact>]
let ``ExcelConnector reports its Kind as Excel`` () =
    use workbook = new TempWorkbook()
    let connector = ExcelConnector(workbook.Path) :> IConnector

    Assert.Equal(ConnectorKind.Excel, connector.Kind)
