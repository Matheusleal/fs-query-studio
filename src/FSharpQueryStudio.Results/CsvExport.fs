namespace FSharpQueryStudio.Results

open System.IO

/// CSV export — Section 11. A worker-side streamed operation: `rows` is
/// enumerated exactly once, one row written per pull, so exporting never
/// requires materializing the full result in memory first and is never
/// limited to whatever the UI's bounded preview already fetched — a
/// connector's live-reader-backed sequence streams straight through.
module CsvExport =
    let private needsQuoting (field: string) =
        field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r')

    let private escape (field: string) =
        if needsQuoting field then
            "\"" + field.Replace("\"", "\"\"") + "\""
        else
            field

    let private writeRow (writer: TextWriter) (fields: string seq) =
        writer.Write(fields |> Seq.map escape |> String.concat ",")
        writer.Write("\r\n")

    /// Writes a header row from `columns`, then one CSV row per item in
    /// `rows`. `writer` is left open — the caller owns its lifetime.
    let write (writer: TextWriter) (columns: string list) (rows: obj array seq) : unit =
        writeRow writer columns

        for row in rows do
            writeRow writer (row |> Array.map (fun cell -> if isNull cell then "" else string cell))
