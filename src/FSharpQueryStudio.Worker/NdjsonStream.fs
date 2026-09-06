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

    /// The raw next line, or `None` at EOF. The reverse channel (F5) needs
    /// to peek at whether an inbound line is a request (`method` present)
    /// or a response to a worker-initiated request *before* choosing which
    /// type to deserialise it as — `readFrame<'T>` commits to one shape.
    let readLine (reader: TextReader) : System.Threading.Tasks.Task<string option> =
        task {
            let! line = reader.ReadLineAsync()
            return (match line with | null -> None | l -> Some l)
        }

    let readFrame<'T> (reader: TextReader) : System.Threading.Tasks.Task<'T option> =
        task {
            let! line = reader.ReadLineAsync()

            match line with
            | null -> return None
            | line -> return Some(JsonSerializer.Deserialize<'T>(line, Serialization.options))
        }
