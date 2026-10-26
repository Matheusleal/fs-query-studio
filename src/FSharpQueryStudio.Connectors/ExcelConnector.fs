namespace FSharpQueryStudio.Connectors

open System.IO
open System.Threading
open System.Threading.Tasks
open ClosedXML.Excel
open FSharpQueryStudio.Protocol

/// Workbook/sheet selection and header options — Section 10's Excel
/// connector capability shape. `SheetName = None` targets the workbook's
/// first sheet.
type ExcelOptions =
    { SheetName: string option
      HasHeader: bool }

    static member Default = { SheetName = None; HasHeader = true }

/// Reads an `IXLCell`'s value as its natural .NET type (number, boolean,
/// date, or text) rather than always flattening to a display string.
module private ExcelCells =
    let value (cell: IXLCell) : obj =
        let v = cell.Value

        if v.IsBlank then box null
        elif v.IsNumber then box (v.GetNumber())
        elif v.IsBoolean then box (v.GetBoolean())
        elif v.IsDateTime then box (v.GetDateTime())
        else box (v.ToString())

/// Excel connector — Section 10. `filePath` names a local `.xlsx` file.
type ExcelConnector(filePath: string, ?options: ExcelOptions) =
    let options = defaultArg options ExcelOptions.Default

    let selectSheet (workbook: XLWorkbook) : IXLWorksheet =
        match options.SheetName with
        | Some name -> workbook.Worksheet(name)
        | None -> workbook.Worksheets |> Seq.head

    member _.Options = options

    /// Reads at most `maxRows` data rows from the target sheet - Section
    /// 10's "bounded worksheet reads" - defaulting to the same 500-row
    /// preview bound the results `Projection` module uses elsewhere. Columns come
    /// from the sheet's first used row when `options.HasHeader`, else
    /// synthesized as `Column1`, `Column2`, ... with every used row
    /// treated as data.
    member _.ReadRows(?maxRows: int) : ConnectorQueryResult =
        let maxRows = defaultArg maxRows 500

        use workbook = new XLWorkbook(filePath)
        let sheet = selectSheet workbook
        let usedRange = sheet.RangeUsed()

        if isNull usedRange then
            { Columns = []; Rows = Seq.empty }
        else
            let usedRows = usedRange.RowsUsed() |> List.ofSeq

            let columns, dataRows =
                match usedRows with
                | [] -> [], []
                | headerRow :: rest when options.HasHeader ->
                    (headerRow.Cells() |> Seq.map (fun c -> c.GetString()) |> List.ofSeq), rest
                | allRows -> [ for i in 1 .. usedRange.ColumnCount() -> $"Column{i}" ], allRows

            let rows =
                dataRows
                |> List.truncate maxRows
                |> List.map (fun row -> row.Cells() |> Seq.map ExcelCells.value |> Array.ofSeq)

            { Columns = columns; Rows = rows }

    interface IConnector with
        member _.Kind = ConnectorKind.Excel

        member _.TestConnectionAsync(_ct: CancellationToken) : Task<bool> = Task.FromResult(File.Exists filePath)

        /// Every worksheet is one table, named after the sheet, with
        /// header-derived columns per `options.HasHeader` — Section 10's
        /// "workbook/sheet selection" extended to schema discovery.
        member _.DiscoverSchemaAsync(_ct: CancellationToken) : Task<ConnectorTable list> =
            use workbook = new XLWorkbook(filePath)

            let tables =
                workbook.Worksheets
                |> Seq.map (fun sheet ->
                    let usedRange = sheet.RangeUsed()

                    let columnNames =
                        if isNull usedRange then
                            []
                        elif options.HasHeader then
                            usedRange.FirstRow().Cells() |> Seq.map (fun c -> c.GetString()) |> List.ofSeq
                        else
                            [ for i in 1 .. usedRange.ColumnCount() -> $"Column{i}" ]

                    { Name = sheet.Name
                      Columns =
                        columnNames
                        |> List.map (fun name -> ({ Name = name; DataType = "string" }: ConnectorColumn)) }: ConnectorTable)
                |> List.ofSeq

            Task.FromResult tables
