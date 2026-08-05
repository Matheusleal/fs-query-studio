namespace FSharpQueryStudio.Worker

open System.IO
open System.Text.Json
open FSharpQueryStudio.Protocol

/// Reads/writes NDJSON frames over stdio — see Section 7: one JSON document
/// per line. A JSON string value containing a newline is always escaped by
/// the serializer, so the raw newline byte on the wire is never ambiguous
/// with a real frame boundary; `TextReader.ReadLineAsync` reassembles a full
/// line regardless of how the underlying stream chooses to deliver bytes.
module NdjsonStream =

    let writeFrame<'T> (writer: TextWriter) (frame: 'T) : System.Threading.Tasks.Task =
        task {
            let json = JsonSerializer.Serialize<'T>(frame, Serialization.options)
            do! writer.WriteLineAsync(json)
            do! writer.FlushAsync()
        }

    let readFrame<'T> (reader: TextReader) : System.Threading.Tasks.Task<'T option> =
        task {
            let! line = reader.ReadLineAsync()

            match line with
            | null -> return None
            | line -> return Some(JsonSerializer.Deserialize<'T>(line, Serialization.options))
        }
