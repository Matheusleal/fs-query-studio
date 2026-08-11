namespace FSharpQueryStudio.Connectors

open System
open System.Globalization
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open FSharpQueryStudio.Protocol

/// Delimiter/encoding/header options — Section 10's CSV connector
/// capability shape.
type CsvOptions =
    { Delimiter: char
      Encoding: Encoding
      HasHeader: bool }

    static member Default =
        { Delimiter = ','
          Encoding = Encoding.UTF8
          HasHeader = true }

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
        | false, _ -> None

    let tryParseBool (s: string) : bool option =
        match Boolean.TryParse(s) with
        | true, v -> Some v
        | false, _ -> None

    let tryParseDateTime (s: string) : DateTime option =
        match DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | true, v -> Some v
        | false, _ -> None

/// CSV connector — Section 10. `filePath` names a local `.csv` file;
/// `options` controls delimiter, encoding, and whether the first record
/// is a header row.
type CsvConnector(filePath: string, ?options: CsvOptions) =
    let options = defaultArg options CsvOptions.Default

    member _.Options = options

    /// Streams rows as a lazy sequence — Section 10's streaming-read
    /// requirement. `Rows` is genuinely re-enumerable: each enumeration
    /// opens its own file handle from scratch (disposed when that
    /// enumeration ends or is abandoned), rather than sharing one handle
    /// across every `GetEnumerator()` call — a caller that materializes
    /// `Rows` once (e.g. `Seq.toArray`) and then hands the same
    /// `ConnectorQueryResult` to something else that also reads `Rows`
    /// (e.g. `Results.table`) must see the same data again, not a
    /// "closed TextReader" failure from a stream only ever good for one
    /// pass. Columns come from the header row when `options.HasHeader`,
    /// else synthesized as `Column1`, `Column2`, ... with the first
    /// record treated as data — peeked once, eagerly, via its own
    /// throwaway reader closed before this method returns.
    member _.ReadRows() : ConnectorQueryResult =
        let columns =
            use headerReader = new StreamReader(filePath, options.Encoding)

            match CsvParsing.readRecord headerReader options.Delimiter with
            | None -> []
            | Some record ->
                if options.HasHeader then
                    record
                else
                    [ for i in 1 .. record.Length -> $"Column{i}" ]

        let rows =
            seq {
                use streamReader = new StreamReader(filePath, options.Encoding)
                let first = CsvParsing.readRecord streamReader options.Delimiter

                match first, options.HasHeader with
                | Some record, false -> yield record |> List.map box |> Array.ofList
                | _ -> () // Some when HasHeader (the header itself) or None (empty file) - nothing to yield yet.

                let mutable more = true

                while more do
                    match CsvParsing.readRecord streamReader options.Delimiter with
                    | Some record -> yield record |> List.map box |> Array.ofList
                    | None -> more <- false
            }

        { Columns = columns; Rows = rows }

    interface IConnector with
        member _.Kind = ConnectorKind.Csv

        member _.TestConnectionAsync(_ct: CancellationToken) : Task<bool> = Task.FromResult(File.Exists filePath)

        /// A CSV file is one table named after the file itself, with
        /// header-derived column names and a `string` `DataType` for
        /// every column - the file's raw cell values are always text
        /// until a script applies `CsvConversions`.
        member this.DiscoverSchemaAsync(_ct: CancellationToken) : Task<ConnectorTable list> =
            let columns = this.ReadRows().Columns

            let table: ConnectorTable =
                { Name = Path.GetFileNameWithoutExtension(filePath)
                  Columns = columns |> List.map (fun name -> ({ Name = name; DataType = "string" }: ConnectorColumn)) }

            Task.FromResult [ table ]
