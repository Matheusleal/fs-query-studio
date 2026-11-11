namespace FSharpQueryStudio.Connectors

open System
open System.Globalization
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open FSharpQueryStudio.Protocol

[<AutoOpen>]
module CsvConnectorExtensions =
    type ConnectorKind with
        static member Csv = ConnectorKind("Csv")

    type ConnectorCategory with
        static member TabularFile = ConnectorCategory("csv", "Csv", "FSharpQueryStudio.Connectors.Connections.CsvCategory")

/// Delimiter/encoding/header options — Section 10's CSV connector
/// capability shape.
type CsvOptions =
    { Delimiter: char
      Encoding: Encoding
      HasHeader: bool
      ColumnTypes: Map<string, string>
      ColumnTransforms: Map<string, string> }

    static member Default =
        { Delimiter = ','
          Encoding = Encoding.UTF8
          HasHeader = true
          ColumnTypes = Map.empty
          ColumnTransforms = Map.empty }

/// A small RFC4180 streaming parser. `StreamReader.ReadLine` can't be
/// used directly for this: a quoted field may itself contain a raw
/// newline, which is not a record boundary.
module private CsvParsing =
    /// Reads one record's raw string fields starting at the reader's
    /// current position. `None` only at true end-of-stream, with nothing
    /// left to read.
    let readRecord (reader: TextReader) (delimiter: char) : string list option =
        if reader.Peek() = -1 then
            None
        else
            let fields = ResizeArray<string>()
            let field = StringBuilder()
            let mutable inQuotes = false
            let mutable finished = false

            let flush () =
                fields.Add(field.ToString())
                field.Clear() |> ignore

            while not finished do
                let code = reader.Read()

                if code = -1 then
                    flush ()
                    finished <- true
                else
                    let ch = char code

                    if inQuotes then
                        if ch = '"' then
                            if reader.Peek() = int '"' then
                                reader.Read() |> ignore
                                field.Append('"') |> ignore
                            else
                                inQuotes <- false
                        else
                            field.Append(ch) |> ignore
                    elif ch = '"' && field.Length = 0 then
                        inQuotes <- true
                    elif ch = delimiter then
                        flush ()
                    elif ch = '\r' then
                        if reader.Peek() = int '\n' then
                            reader.Read() |> ignore

                        flush ()
                        finished <- true
                    elif ch = '\n' then
                        flush ()
                        finished <- true
                    else
                        field.Append(ch) |> ignore

            Some(fields |> List.ofSeq)

    /// Fast streaming parser that reuses `field` and directly returns `obj array`
    /// without intermediate `string list` -> `obj list` -> `obj array` allocations per row.
    let readRecordAsObjArray (reader: TextReader) (delimiter: char) (field: StringBuilder) (expectedCols: int) : obj array option =
        if reader.Peek() = -1 then
            None
        else
            let fields = ResizeArray<obj>(max 4 expectedCols)
            field.Clear() |> ignore
            let mutable inQuotes = false
            let mutable finished = false

            let flush () =
                fields.Add(box (field.ToString()))
                field.Clear() |> ignore

            while not finished do
                let code = reader.Read()

                if code = -1 then
                    flush ()
                    finished <- true
                else
                    let ch = char code

                    if inQuotes then
                        if ch = '"' then
                            if reader.Peek() = int '"' then
                                reader.Read() |> ignore
                                field.Append('"') |> ignore
                            else
                                inQuotes <- false
                        else
                            field.Append(ch) |> ignore
                    elif ch = '"' && field.Length = 0 then
                        inQuotes <- true
                    elif ch = delimiter then
                        flush ()
                    elif ch = '\r' then
                        if reader.Peek() = int '\n' then
                            reader.Read() |> ignore

                        flush ()
                        finished <- true
                    elif ch = '\n' then
                        flush ()
                        finished <- true
                    else
                        field.Append(ch) |> ignore

            Some(fields.ToArray())

/// Typed conversion helpers a script uses to interpret a CSV cell's raw
/// string value — Section 10's CSV connector capability shape.
module CsvConversions =
    let tryParseInt (s: string) : int option =
        match Int32.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture) with
        | true, v -> Some v
        | false, _ -> None

    let tryParseFloat (s: string) : float option =
        match Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, v -> Some v
        | false, _ ->
            match Double.TryParse(s.Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, v -> Some v
            | false, _ -> None

    let tryParseDecimal (s: string) : decimal option =
        let style = NumberStyles.AllowDecimalPoint ||| NumberStyles.AllowLeadingSign
        match Decimal.TryParse(s, style, CultureInfo.InvariantCulture) with
        | true, v -> Some v
        | false, _ ->
            match Decimal.TryParse(s.Replace(",", "."), style, CultureInfo.InvariantCulture) with
            | true, v -> Some v
            | false, _ -> None

    let tryParseBool (s: string) : bool option =
        match Boolean.TryParse(s) with
        | true, v -> Some v
        | false, _ -> None

    let tryParseDateTime (s: string) : DateTime option =
        match DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | true, v -> Some v
        | false, _ -> None

module CsvDelimiter =
    let detect (path: string) (encoding: Encoding option) : char =
        let enc = defaultArg encoding Encoding.UTF8
        let clean =
            if String.IsNullOrWhiteSpace path then ""
            else path.Trim().Trim('"', '\'')
        try
            if not (File.Exists clean) then ','
            else
                use reader = new StreamReader(clean, enc)
                let firstLine = reader.ReadLine()
                if String.IsNullOrEmpty firstLine then ','
                else
                    let mutable commas = 0
                    let mutable semicolons = 0
                    let mutable tabs = 0
                    let mutable pipes = 0
                    let mutable inQuotes = false

                    for ch in firstLine do
                        if ch = '"' then inQuotes <- not inQuotes
                        elif not inQuotes then
                            match ch with
                            | ',' -> commas <- commas + 1
                            | ';' -> semicolons <- semicolons + 1
                            | '\t' -> tabs <- tabs + 1
                            | '|' -> pipes <- pipes + 1
                            | _ -> ()

                    let counts = [ (semicolons, ';'); (commas, ','); (tabs, '\t'); (pipes, '|') ]
                    let (maxCount, bestDelim) = counts |> List.maxBy fst
                    if maxCount > 0 then bestDelim else ','
        with _ ->
            ','

/// Type inference helpers for CSV data sources.
module CsvTypeInference =
    let cleanSample (s: string) : string =
        if isNull s then ""
        else s.Trim().Trim('"', '\'')

    let isSentinel (s: string) : bool =
        if isNull s then false
        else
            let t = s.Trim().Trim('"', '\'')
            String.Equals(t, "n/a", StringComparison.OrdinalIgnoreCase)
            || t = "-"
            || String.Equals(t, "null", StringComparison.OrdinalIgnoreCase)

    let isEmpty (s: string) : bool =
        if isNull s then true
        else
            let t = s.Trim().Trim('"', '\'')
            String.IsNullOrEmpty t

    let isMissingOrSentinel (s: string) : bool =
        isEmpty s || isSentinel s

    /// Infers an F# type from up to 200 sample string values.
    /// When sentinel values ("N/A", "-", "null") are encountered,
    /// or when a numeric/date/bool column contains empty values,
    /// the type is converted to an option type (e.g. `decimal option`, `int option`, `string option`).
    let inferType (samples: string seq) : string =
        let sampleList = samples |> Seq.truncate 200 |> Seq.toList
        let hasSentinel = sampleList |> List.exists isSentinel
        let hasEmpty = sampleList |> List.exists isEmpty

        let validValues =
            sampleList
            |> List.map cleanSample
            |> List.filter (fun s -> not (isMissingOrSentinel s))

        if List.isEmpty validValues then
            if hasSentinel then "string option" else "string"
        else
            let baseType =
                if validValues |> List.forall (fun s ->
                    match Int32.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                    | true, _ -> true | false, _ -> false) then
                    "int"
                elif validValues |> List.forall (fun s ->
                    match Int64.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                    | true, _ -> true | false, _ -> false) then
                    "int64"
                elif validValues |> List.forall (fun s ->
                    let style = NumberStyles.Number ||| NumberStyles.AllowLeadingSign
                    match Decimal.TryParse(s, style, CultureInfo.InvariantCulture) with
                    | true, _ -> true
                    | false, _ ->
                        match Decimal.TryParse(s.Replace(",", "."), style, CultureInfo.InvariantCulture) with
                        | true, _ -> true
                        | false, _ -> false) then
                    "decimal"
                elif validValues |> List.forall (fun s ->
                    let style = NumberStyles.Float ||| NumberStyles.AllowThousands
                    match Double.TryParse(s, style, CultureInfo.InvariantCulture) with
                    | true, _ -> true
                    | false, _ ->
                        match Double.TryParse(s.Replace(",", "."), style, CultureInfo.InvariantCulture) with
                        | true, _ -> true
                        | false, _ -> false) then
                    "double"
                elif validValues |> List.forall (fun s ->
                    match Boolean.TryParse(s) with
                    | true, _ -> true
                    | false, _ ->
                        let l = s.ToLowerInvariant()
                        l = "true" || l = "false" || l = "1" || l = "0") then
                    "bool"
                elif validValues |> List.forall (fun s ->
                    match DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None) with
                    | true, _ -> true
                    | false, _ ->
                        match DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None) with
                        | true, _ -> true
                        | false, _ -> false) then
                    "System.DateTime"
                else
                    "string"

            if hasSentinel || (hasEmpty && baseType <> "string") then
                $"{baseType} option"
            else
                baseType

/// CSV connector — Section 10. `filePath` names a local `.csv` file;
/// `options` controls delimiter, encoding, and whether the first record
/// is a header row. Automatically cleans surrounding quotes and auto-detects
/// delimiter (comma, semicolon, tab, pipe) when not explicitly specified.
type CsvConnector(filePath: string, ?options: CsvOptions) =
    let cleanPath =
        if String.IsNullOrWhiteSpace filePath then ""
        else filePath.Trim().Trim('"', '\'')

    let resolvedOptions =
        match options with
        | Some opt -> opt
        | None ->
            let autoDelim = CsvDelimiter.detect cleanPath None
            { CsvOptions.Default with Delimiter = autoDelim }

    member _.FilePath = cleanPath
    member _.Options = resolvedOptions

    static member DetectDelimiter(path: string, ?encoding: Encoding) : char =
        CsvDelimiter.detect path encoding

    /// Streams rows as a lazy sequence — Section 10's streaming-read
    /// requirement. `Rows` is genuinely re-enumerable: each enumeration
    /// opens its own file handle from scratch (disposed when that
    /// enumeration ends or is abandoned), rather than sharing one handle
    /// across every `GetEnumerator()` call — a caller that materializes
    /// `Rows` once (e.g. `Seq.toArray`) and then hands the same
    /// `ConnectorQueryResult` to something else that also reads `Rows`
    /// (e.g. the results projection materializing it for the grid, then
    /// Export CSV re-reading it) must see the same data again, not a
    /// "closed TextReader" failure from a stream only ever good for one
    /// pass. Columns come from the header row when `options.HasHeader`,
    /// else synthesized as `Column1`, `Column2`, ... with the first
    /// record treated as data — peeked once, eagerly, via its own
    /// throwaway reader closed before this method returns.
    member _.ReadRows() : ConnectorQueryResult =
        let columns =
            use fs = new FileStream(cleanPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete, 4096)
            use headerReader = new StreamReader(fs, resolvedOptions.Encoding, true, 4096)

            match CsvParsing.readRecord headerReader resolvedOptions.Delimiter with
            | None -> []
            | Some record ->
                if resolvedOptions.HasHeader then
                    record
                else
                    [ for i in 1 .. record.Length -> $"Column{i}" ]

        let expectedCols = columns.Length
        let isBlankLine (record: obj array) =
            expectedCols > 1
            && record.Length = 1
            && (match record.[0] with :? string as s -> String.IsNullOrWhiteSpace s | _ -> false)

        let hasTransforms = not resolvedOptions.ColumnTransforms.IsEmpty
        let applyRowTransforms (row: obj array) : obj array =
            if not hasTransforms then row
            else
                let mapped = Array.copy row
                for i = 0 to min (columns.Length - 1) (mapped.Length - 1) do
                    let colName = columns.[i]
                    match resolvedOptions.ColumnTransforms.TryFind colName with
                    | Some expr -> mapped.[i] <- ColumnTransformation.applyTransform expr mapped.[i]
                    | None -> ()
                mapped

        let rows =
            seq {
                use fs = new FileStream(cleanPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete, 65536)
                use streamReader = new StreamReader(fs, resolvedOptions.Encoding, true, 65536)
                let sb = StringBuilder(256)
                let first = CsvParsing.readRecordAsObjArray streamReader resolvedOptions.Delimiter sb expectedCols

                match first, resolvedOptions.HasHeader with
                | Some record, false when not (isBlankLine record) -> yield applyRowTransforms record
                | _ -> () // Some when HasHeader (the header itself) or None (empty file) - nothing to yield yet.

                let mutable more = true

                while more do
                    match CsvParsing.readRecordAsObjArray streamReader resolvedOptions.Delimiter sb expectedCols with
                    | Some record ->
                        if not (isBlankLine record) then
                            yield applyRowTransforms record
                    | None -> more <- false
            }

        { Columns = columns; Rows = rows }

    interface IConnector with
        member _.Kind = ConnectorKind.Csv

        member _.TestConnectionAsync(_ct: CancellationToken) : Task<bool> = Task.FromResult(File.Exists cleanPath)

        /// Discovers CSV schema by inspecting the header and inferring data types
        /// across the first 200 rows, honoring explicit ColumnTypes and ColumnTransforms.
        /// When "N/A", "-", or "null" sentinels are found, the column is typed as an option type.
        member this.DiscoverSchemaAsync(_ct: CancellationToken) : Task<ConnectorTable list> =
            let qr = this.ReadRows()
            let rowsSample = qr.Rows |> Seq.truncate 200 |> Seq.toList
            let columns = qr.Columns

            let colsWithTypes =
                columns
                |> List.mapi (fun idx colName ->
                    let explicitType = resolvedOptions.ColumnTypes.TryFind colName
                    let colType =
                        match explicitType with
                        | Some t -> t
                        | None ->
                            let samples = rowsSample |> Seq.map (fun r -> if idx < r.Length then string r.[idx] else "")
                            TabularTypeInference.inferType samples
                    { Name = colName; DataType = colType }: ConnectorColumn
                )

            let table: ConnectorTable =
                { Name = Path.GetFileNameWithoutExtension(cleanPath)
                  Columns = colsWithTypes }

            Task.FromResult [ table ]
