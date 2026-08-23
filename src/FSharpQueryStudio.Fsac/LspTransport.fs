namespace FSharpQueryStudio.Fsac

open System.IO
open System.Text
open System.Threading.Tasks

/// Reads/writes LSP's `Content-Length:`-framed JSON-RPC messages over a
/// stdio pipe — the Worker<->fsautocomplete leg's own wire format,
/// distinct from `Worker.NdjsonStream`'s newline-delimited framing (which
/// stays exactly as-is for the App<->Worker leg). Per the LSP base
/// protocol: a `Content-Length: <n>\r\n\r\n` ASCII header followed by
/// exactly `n` UTF-8 bytes of JSON body, no trailing newline.
module LspTransport =

    let writeMessage (stream: Stream) (json: string) : Task =
        task {
            let bodyBytes = Encoding.UTF8.GetBytes(json)
            let headerBytes = Encoding.ASCII.GetBytes($"Content-Length: {bodyBytes.Length}\r\n\r\n")
            do! stream.WriteAsync(headerBytes, 0, headerBytes.Length)
            do! stream.WriteAsync(bodyBytes, 0, bodyBytes.Length)
            do! stream.FlushAsync()
        }

    /// One ASCII, `\n`-terminated header line (a bare `\r`, if present
    /// right before it, is dropped). `None` only at a genuine end of
    /// stream with nothing at all read for this line — the same "no more
    /// frames" convention `NdjsonStream.readFrame` uses.
    let private readHeaderLine (stream: Stream) : Task<string option> =
        task {
            let bytes = ResizeArray<byte>()
            let buffer = Array.zeroCreate<byte> 1
            let mutable finished = false
            let mutable sawAnyByte = false

            while not finished do
                let! read = stream.ReadAsync(buffer, 0, 1)

                if read = 0 then
                    finished <- true
                else
                    sawAnyByte <- true

                    if buffer.[0] = byte '\n' then
                        finished <- true
                    elif buffer.[0] <> byte '\r' then
                        bytes.Add(buffer.[0])

            if not sawAnyByte then
                return None
            else
                return Some(Encoding.ASCII.GetString(bytes.ToArray()))
        }

    let readMessage (stream: Stream) : Task<string option> =
        task {
            let mutable contentLength = -1
            let mutable reachedEnd = false
            let mutable sawBlankLine = false

            while not sawBlankLine && not reachedEnd do
                let! line = readHeaderLine stream

                match line with
                | None -> reachedEnd <- true
                | Some "" -> sawBlankLine <- true
                | Some line ->
                    let prefix = "Content-Length:"

                    if line.StartsWith(prefix) then
                        contentLength <- int (line.Substring(prefix.Length).Trim())

            if reachedEnd || contentLength < 0 then
                return None
            else
                let body = Array.zeroCreate<byte> contentLength
                let mutable totalRead = 0
                let mutable bodyEnded = false

                while totalRead < contentLength && not bodyEnded do
                    let! read = stream.ReadAsync(body, totalRead, contentLength - totalRead)

                    if read = 0 then
                        bodyEnded <- true // stream closed mid-body - return whatever arrived rather than hang
                    else
                        totalRead <- totalRead + read

                return Some(Encoding.UTF8.GetString(body, 0, totalRead))
        }
