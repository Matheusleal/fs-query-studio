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
    let path =
        Path.Combine(Path.GetTempPath(), "fqs-xlsx-" + Guid.NewGuid().ToString("N") + ".xlsx")

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
        ExcelConnector(
            workbook.Path,
            { ExcelOptions.Default with
                SheetName = Some "NoHeader"
            }
        )

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
                HasHeader = false
            }
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
        Assert.True(
            colMap.["Total"] = "int"
            || colMap.["Total"] = "decimal"
            || colMap.["Total"] = "double"
        )
    }

[<Fact>]
let ``DiscoverSchemaAsync respects explicit ColumnTypes override`` () =
    task {
        use workbook = new TempWorkbook()

        let opts =
            { ExcelOptions.Default with
                ColumnTypes = Map.ofList [ "Total", "decimal"; "CustomerId", "int64" ]
            }

        let connector = ExcelConnector(workbook.Path, opts) :> IConnector

        let! tables = connector.DiscoverSchemaAsync(CancellationToken.None)
        let orders = tables |> List.find (fun t -> t.Name = "Orders")

        let colMap = orders.Columns |> List.map (fun c -> c.Name, c.DataType) |> Map.ofList
        Assert.Equal("decimal", colMap.["Total"])
        Assert.Equal("int64", colMap.["CustomerId"])
    }

[<Fact>]
let ``ExcelConnector applies column transformations such as replace and trim`` () =
    let path =
        Path.Combine(Path.GetTempPath(), "fqs-transform-" + Guid.NewGuid().ToString("N") + ".xlsx")

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
                ColumnTransforms = Map.ofList [ "RawPrice", "replace('.', '').replace(',', '.')"; "RawCode", "trim" ]
                ColumnTypes = Map.ofList [ "RawPrice", "decimal" ]
            }

        let connector = ExcelConnector(path, opts)
        let qr = connector.ReadRows()
        let rows = qr.Rows |> Seq.toList
        Assert.Single(rows) |> ignore
        Assert.Equal(box "1234.56", rows.[0].[0])
        Assert.Equal(box "ABC-123", rows.[0].[1])
    finally
        if File.Exists path then
            File.Delete path

[<Fact>]
let ``FileSqliteEngine preserves long numbers with many decimal places without rounding to int`` () =
    let dbPath =
        Path.Combine(Path.GetTempPath(), "fqs-decimal-" + Guid.NewGuid().ToString("N") + ".db")

    let csvPath =
        Path.Combine(Path.GetTempPath(), "fqs-decimal-" + Guid.NewGuid().ToString("N") + ".csv")

    try
        File.WriteAllLines(csvPath, [| "Id,HighPrecision"; "1,1234567.890123456"; "2,9876543.210987654" |])

        let engine = FileSqliteEngine(dbPath)
        let connector = CsvConnector(csvPath)

        let stagedTable =
            engine.EnsureStaged("PrecisionData", csvPath, (fun () -> connector.ReadRows()))

        Assert.Equal("PrecisionData", stagedTable)

        let res =
            engine.ExecuteQuery("SELECT HighPrecision FROM PrecisionData ORDER BY Id;")

        let rows = res.Rows |> Seq.toList
        Assert.Equal(2, rows.Length)

        let val1 =
            Convert.ToDouble(rows.[0].[0], System.Globalization.CultureInfo.InvariantCulture)

        let val2 =
            Convert.ToDouble(rows.[1].[0], System.Globalization.CultureInfo.InvariantCulture)

        // Must preserve the decimals, NOT rounded to 1234568 or 9876543
        Assert.True(Math.Abs(val1 - 1234567.890123456) < 0.000001)
        Assert.True(Math.Abs(val2 - 9876543.210987654) < 0.000001)
    finally
        try
            if File.Exists dbPath then
                File.Delete dbPath
        with _ ->
            ()

        try
            if File.Exists csvPath then
                File.Delete csvPath
        with _ ->
            ()

[<Fact>]
let ``ExcelConnector reports its Kind as Excel`` () =
    use workbook = new TempWorkbook()
    let connector = ExcelConnector(workbook.Path) :> IConnector

    Assert.Equal(ConnectorKind.Excel, connector.Kind)

[<Fact>]
let ``ExcelDescriptor and ExcelConnectorFactory coverage`` () =
    let descriptor = ExcelDescriptor.Instance :> IConnectorDescriptor
    Assert.Equal("Excel", descriptor.KindId)
    Assert.Equal("Excel", descriptor.DisplayName)
    Assert.Equal("table", descriptor.Icon)
    Assert.Equal(ConnectorCategory.Excel, descriptor.Category)
    Assert.Equal(4, descriptor.FormFields.Length)
    Assert.True(descriptor.EntityGenerator.IsNone)

    let validJson =
        System.Text.Json.JsonDocument.Parse("""{"filePath": "test.xlsx"}""").RootElement

    Assert.True(descriptor.ValidateConfiguration(validJson).IsOk)
    let invalidJson = System.Text.Json.JsonDocument.Parse("""{}""").RootElement
    Assert.True(descriptor.ValidateConfiguration(invalidJson).IsError)

    let factory = descriptor.Factory

    let dummyStore =
        { new FSharpQueryStudio.Security.ISecretStore with
            member _.TryRead(_) = None

            member _.TryReadAsync(_, _) =
                System.Threading.Tasks.Task.FromResult None

            member _.Save _ _ = ()
            member _.Delete _ = ()
        }

    let strDoc = System.Text.Json.JsonDocument.Parse("\"test.xlsx\"")
    let conn1 = factory.Create(strDoc.RootElement, dummyStore, None) :?> ExcelConnector
    Assert.Equal("test.xlsx", conn1.FilePath)

    let objDoc =
        System.Text.Json.JsonDocument.Parse(
            """{"filePath": "data.xlsx", "sheetName": "Sheet2", "hasHeader": false, "columnTypes": {"Col1": "int"}, "columnTransforms": {"Col1": "trim"}}"""
        )

    let conn2 = factory.Create(objDoc.RootElement, dummyStore, None) :?> ExcelConnector
    Assert.Equal(Some "Sheet2", conn2.Options.SheetName)
    Assert.False(conn2.Options.HasHeader)
    Assert.Equal(Some "int", conn2.Options.ColumnTypes.TryFind "Col1")
    Assert.Equal(Some "trim", conn2.Options.ColumnTransforms.TryFind "Col1")

[<Fact>]
let ``ExcelOptions Default record values scenario 1`` () =
    let def = ExcelOptions.Default
    Assert.True(def.SheetName.IsNone)
    Assert.True(def.HasHeader)
    Assert.True(def.ColumnTypes.IsEmpty)
    Assert.True(def.ColumnTransforms.IsEmpty)

[<Fact>]
let ``ExcelOptions Default copy with modified options scenario 2`` () =
    let custom =
        { ExcelOptions.Default with
            SheetName = Some "CustomSheet"
            HasHeader = false
        }

    Assert.Equal(Some "CustomSheet", custom.SheetName)
    Assert.False(custom.HasHeader)
    Assert.True(custom.ColumnTypes.IsEmpty)

[<Fact>]
let ``ExcelConnector reads boolean, datetime, timespan and blank cell values scenario 1`` () =
    let path =
        Path.Combine(Path.GetTempPath(), $"fqs-excel-types-{Guid.NewGuid():N}.xlsx")

    try
        do
            use wb = new ClosedXML.Excel.XLWorkbook()
            let ws = wb.Worksheets.Add("Types")
            ws.Cell(1, 1).Value <- "IsActive"
            ws.Cell(1, 2).Value <- "CreatedDate"
            ws.Cell(1, 3).Value <- "Duration"
            ws.Cell(1, 4).Value <- "BlankCol"

            ws.Cell(2, 1).Value <- true
            ws.Cell(2, 2).Value <- DateTime(2026, 1, 1, 10, 30, 0)
            ws.Cell(2, 3).Value <- TimeSpan.FromHours(2.5)
            ws.Cell(2, 4).Value <- ClosedXML.Excel.Blank.Value

            wb.SaveAs(path)

        let conn = ExcelConnector(path)
        let qr = conn.ReadRows()
        let rows = qr.Rows |> Seq.toArray
        Assert.Single(rows) |> ignore
        Assert.Equal(box true, rows.[0].[0])
        Assert.Equal(box (DateTime(2026, 1, 1, 10, 30, 0)), rows.[0].[1])
        Assert.Equal(box (TimeSpan.FromHours(2.5).ToString()), rows.[0].[2])
        Assert.Null(rows.[0].[3])
    finally
        if File.Exists path then
            File.Delete path

[<Fact>]
let ``ExcelConnector reads false boolean and custom datetime scenario 2`` () =
    let path =
        Path.Combine(Path.GetTempPath(), $"fqs-excel-types2-{Guid.NewGuid():N}.xlsx")

    try
        do
            use wb = new ClosedXML.Excel.XLWorkbook()
            let ws = wb.Worksheets.Add("Types")
            ws.Cell(1, 1).Value <- "Flag"
            ws.Cell(1, 2).Value <- "Date"
            ws.Cell(2, 1).Value <- false
            ws.Cell(2, 2).Value <- DateTime(2025, 12, 31, 23, 59, 59)
            wb.SaveAs(path)

        let conn = ExcelConnector(path)
        let qr = conn.ReadRows()
        let rows = qr.Rows |> Seq.toArray
        Assert.Single(rows) |> ignore
        Assert.Equal(box false, rows.[0].[0])
        Assert.Equal(box (DateTime(2025, 12, 31, 23, 59, 59)), rows.[0].[1])
    finally
        if File.Exists path then
            File.Delete path

[<Fact>]
let ``ExcelConnector ReadRaw and StagePrimaryTable scenario 1`` () =
    use wb = new TempWorkbook()
    let conn = ExcelConnector(wb.Path) :> IStagableConnector
    let rawLimited = conn.ReadRaw(Some 1)
    Assert.Equal(1, rawLimited.Rows |> Seq.length)
    let rawAll = conn.ReadRaw(None)
    Assert.True(rawAll.Rows |> Seq.length >= 2)

[<Fact>]
let ``ExcelConnector StagePrimaryTable with specific and default sheet scenario 2`` () =
    use wb = new TempWorkbook()

    let dbPath =
        Path.Combine(Path.GetTempPath(), $"fqs-excel-stage-{Guid.NewGuid():N}.db")

    try
        let engine = FileSqliteEngine(dbPath) :> IStagingEngine
        let conn1 = ExcelConnector(wb.Path) :> IStagableConnector
        let tbl1 = conn1.StagePrimaryTable(engine, "conn1")
        Assert.False(String.IsNullOrWhiteSpace tbl1)

        let conn2 =
            ExcelConnector(
                wb.Path,
                { ExcelOptions.Default with
                    SheetName = Some "Orders"
                }
            )
            :> IStagableConnector

        let tbl2 = conn2.StagePrimaryTable(engine, "conn2")
        Assert.False(String.IsNullOrWhiteSpace tbl2)
    finally
        if File.Exists dbPath then
            File.Delete dbPath
