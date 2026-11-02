namespace FSharpQueryStudio.Connectors

open System.IO
open System.Threading
open System.Threading.Tasks
open ClosedXML.Excel
open FSharpQueryStudio.Protocol

[<AutoOpen>]
module ExcelConnectorExtensions =
    type ConnectorKind with
        static member Excel = ConnectorKind("Excel")

    type ConnectorCategory with
        static member Excel = ConnectorCategory("excel", "Excel", "FSharpQueryStudio.Connectors.Connections.ExcelCategory")

/// Workbook/sheet selection and header options — Section 10's Excel
/// connector capability shape. `SheetName = None` targets the workbook's
/// first sheet.
type ExcelOptions =
    { SheetName: string option
      HasHeader: bool
      ColumnTypes: Map<string, string>
      ColumnTransforms: Map<string, string> }

    static member Default =
        { SheetName = None
          HasHeader = true
          ColumnTypes = Map.empty
          ColumnTransforms = Map.empty }

/// Reads an `IXLCell`'s value as its natural .NET type (number, boolean,
/// date, or text) rather than always flattening to a display string.
module private ExcelCells =
    let value (cell: IXLCell) : obj =
        if isNull cell then
            box null
        else
            let v = cell.Value

            if v.IsBlank || v.IsError then
                box null
            elif v.IsNumber then
                let n = v.GetNumber()
                if System.Double.IsFinite n then box n else box null
            elif v.IsBoolean then
                box (v.GetBoolean())
            elif v.IsDateTime then
                box (v.GetDateTime())
            elif v.IsTimeSpan then
                box (v.GetTimeSpan().ToString())
            else
                box (v.ToString())

/// Excel connector — Section 10. `filePath` names a local `.xlsx` file.
type ExcelConnector(filePath: string, ?options: ExcelOptions) =
    let cleanPath =
        if System.String.IsNullOrWhiteSpace filePath then ""
        else filePath.Trim().Trim('"', '\'')

    let options = defaultArg options ExcelOptions.Default

    let selectSheet (workbook: XLWorkbook) : IXLWorksheet =
        match options.SheetName with
        | Some name -> workbook.Worksheet(name)
        | None -> workbook.Worksheets |> Seq.head

    member _.FilePath = cleanPath
    member _.Options = options

    /// Reads data rows from the target sheet. When `maxRows` is provided,
    /// stops enumerating worksheet rows as soon as `maxRows` data rows have
    /// been read so large workbooks never materialize unneeded rows.
    member _.ReadRows(?sheetName: string, ?maxRows: int) : ConnectorQueryResult =
        use fs = new FileStream(cleanPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536)
        use workbook = new XLWorkbook(fs)
        let sheet =
            match sheetName with
            | Some name -> workbook.Worksheet(name)
            | None -> selectSheet workbook

        use rowEnum = sheet.RowsUsed().GetEnumerator()

        if not (rowEnum.MoveNext()) then
            { Columns = []; Rows = Seq.empty }
        else
            let firstRow = rowEnum.Current
            let firstCell = firstRow.FirstCellUsed()
            let lastCell = firstRow.LastCellUsed()

            if isNull firstCell || isNull lastCell then
                { Columns = []; Rows = Seq.empty }
            else
                let firstCol = firstCell.Address.ColumnNumber
                let lastCol = lastCell.Address.ColumnNumber
                let colCount = lastCol - firstCol + 1

                let columns =
                    if options.HasHeader then
                        [ for c in firstCol .. lastCol -> firstRow.Cell(c).Value.ToString() ]
                    else
                        [ for i in 1 .. colCount -> $"Column{i}" ]

                let hasTransforms = not options.ColumnTransforms.IsEmpty
                let applyColTransform (colName: string) (valObj: obj) : obj =
                    match options.ColumnTransforms.TryFind colName with
                    | Some expr -> ColumnTransformation.applyTransform expr valObj
                    | None -> valObj

                let readRowArray (row: IXLRow) : obj array =
                    let arr = Array.zeroCreate<obj> colCount
                    for idx = 0 to colCount - 1 do
                        let raw = ExcelCells.value (row.Cell(firstCol + idx))
                        if hasTransforms && idx < columns.Length then
                            arr.[idx] <- applyColTransform columns.[idx] raw
                        else
                            arr.[idx] <- raw
                    arr

                let limit = defaultArg maxRows System.Int32.MaxValue
                let collected = ResizeArray<obj array>()

                if not options.HasHeader && collected.Count < limit then
                    collected.Add(readRowArray firstRow)

                while collected.Count < limit && rowEnum.MoveNext() do
                    collected.Add(readRowArray rowEnum.Current)

                { Columns = columns; Rows = collected.ToArray() :> seq<obj array> }

    interface IConnector with
        member _.Kind = ConnectorKind.Excel

        member _.TestConnectionAsync(_ct: CancellationToken) : Task<bool> = Task.FromResult(File.Exists cleanPath)

        /// Every worksheet is one table, named after the sheet, with
        /// header-derived columns and inferred or explicitly configured
        /// data types per `options.ColumnTypes` and `options.ColumnTransforms`.
        member _.DiscoverSchemaAsync(_ct: CancellationToken) : Task<ConnectorTable list> =
            use fs = new FileStream(cleanPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536)
            use workbook = new XLWorkbook(fs)

            let tables =
                workbook.Worksheets
                |> Seq.map (fun sheet ->
                    let rowsEnum = sheet.RowsUsed() |> Seq.truncate 200 |> Seq.toList
                    match rowsEnum with
                    | [] ->
                        { Name = sheet.Name; Columns = [] }: ConnectorTable
                    | firstRow :: dataRows ->
                        let firstCell = firstRow.FirstCellUsed()
                        let lastCell = firstRow.LastCellUsed()
                        if isNull firstCell || isNull lastCell then
                            { Name = sheet.Name; Columns = [] }: ConnectorTable
                        else
                            let firstCol = firstCell.Address.ColumnNumber
                            let lastCol = lastCell.Address.ColumnNumber
                            let colCount = lastCol - firstCol + 1

                            let columnNames =
                                if options.HasHeader then
                                    [ for c in firstCol .. lastCol -> firstRow.Cell(c).Value.ToString() ]
                                else
                                    [ for i in 1 .. colCount -> $"Column{i}" ]

                            let effectiveDataRows =
                                if options.HasHeader then dataRows
                                else firstRow :: dataRows

                            let colsWithTypes =
                                columnNames
                                |> List.mapi (fun idx colName ->
                                    let explicitType = options.ColumnTypes.TryFind colName
                                    let colType =
                                        match explicitType with
                                        | Some t -> t
                                        | None ->
                                            let transform = options.ColumnTransforms.TryFind colName
                                            let samples =
                                                effectiveDataRows
                                                |> Seq.map (fun r ->
                                                    let cellVal = ExcelCells.value (r.Cell(firstCol + idx))
                                                    let transformed =
                                                        match transform with
                                                        | Some expr -> ColumnTransformation.applyTransform expr cellVal
                                                        | None -> cellVal
                                                    if isNull transformed then ""
                                                    else System.Convert.ToString(transformed, System.Globalization.CultureInfo.InvariantCulture)
                                                )
                                            TabularTypeInference.inferType samples
                                    { Name = colName; DataType = colType }: ConnectorColumn
                                )

                            { Name = sheet.Name; Columns = colsWithTypes }: ConnectorTable)
                |> List.ofSeq

            Task.FromResult tables
