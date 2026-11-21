namespace FSharpQueryStudio.Fsac

open System
open System.IO
open System.Runtime.CompilerServices
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

    [<Sealed>]
    type private LspReader(stream: Stream) =
        // 4KB buffer for reading headers and messages in contiguous blocks instead of byte-by-byte
        let buffer = Array.zeroCreate<byte> 4096
        let mutable bufferStart = 0
        let mutable bufferEnd = 0
        let mutable lastStreamPosition = if stream.CanSeek then stream.Position else 0L

        let checkSeekableReset () =
            if stream.CanSeek && stream.Position <> lastStreamPosition then
                bufferStart <- 0
                bufferEnd <- 0
                lastStreamPosition <- stream.Position

        member _.ReadHeaderLineAsync() : Task<string option> =
            task {
                checkSeekableReset ()
                let lineBytes = ResizeArray<byte>()
                let mutable finished = false
                let mutable sawAnyByte = false

                while not finished do
                    if bufferStart >= bufferEnd then
                        bufferStart <- 0
                        bufferEnd <- 0
                        let! read = stream.ReadAsync(buffer, 0, buffer.Length)

                        if stream.CanSeek then
                            lastStreamPosition <- stream.Position

                        bufferEnd <- read

                        if read = 0 then
                            finished <- true

                    if not finished then
                        sawAnyByte <- true
                        let mutable scanIdx = bufferStart
                        let mutable foundNewline = false

                        while scanIdx < bufferEnd && not foundNewline do
                            if buffer.[scanIdx] = byte '\n' then
                                foundNewline <- true

                            scanIdx <- scanIdx + 1

                        if foundNewline then
                            for i = bufferStart to scanIdx - 1 do
                                let b = buffer.[i]

                                if b <> byte '\r' && b <> byte '\n' then
                                    lineBytes.Add(b)

                            bufferStart <- scanIdx
                            finished <- true
                        else
                            for i = bufferStart to bufferEnd - 1 do
                                let b = buffer.[i]

                                if b <> byte '\r' then
                                    lineBytes.Add(b)

                            bufferStart <- bufferEnd

                if not sawAnyByte then
                    return None
                else
                    return Some(Encoding.ASCII.GetString(lineBytes.ToArray()))
            }

        member _.ReadBodyAsync(contentLength: int) : Task<string> =
            task {
                checkSeekableReset ()
                let body = Array.zeroCreate<byte> contentLength
                let mutable totalRead = 0

                // 1. Drain bytes already buffered from earlier reads
                let availableInBuffer = bufferEnd - bufferStart

                if availableInBuffer > 0 then
                    let toCopy = min availableInBuffer (contentLength - totalRead)
                    Buffer.BlockCopy(buffer, bufferStart, body, totalRead, toCopy)
                    bufferStart <- bufferStart + toCopy
                    totalRead <- totalRead + toCopy

                // 2. Read any remainder directly from the underlying stream
                let mutable bodyEnded = false

                while totalRead < contentLength && not bodyEnded do
                    let needed = contentLength - totalRead
                    let! read = stream.ReadAsync(body, totalRead, needed)

                    if stream.CanSeek then
                        lastStreamPosition <- stream.Position

                    if read = 0 then
                        bodyEnded <- true
                    else
                        totalRead <- totalRead + read

                return Encoding.UTF8.GetString(body, 0, totalRead)
            }

    let private readerTable = ConditionalWeakTable<Stream, LspReader>()

    let private getReader (stream: Stream) : LspReader =
        match readerTable.TryGetValue(stream) with
        | true, reader -> reader
        | false, _ ->
            let reader = LspReader(stream)
            readerTable.AddOrUpdate(stream, reader)
            reader

    let readMessage (stream: Stream) : Task<string option> =
        task {
            let reader = getReader stream
            let mutable contentLength = -1
            let mutable reachedEnd = false
            let mutable sawBlankLine = false

            while not sawBlankLine && not reachedEnd do
                let! line = reader.ReadHeaderLineAsync()

                match line with
                | None -> reachedEnd <- true
                | Some "" -> sawBlankLine <- true
                | Some line ->
                    let prefix = "Content-Length:"

                    if line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then
                        let lenStr = line.Substring(prefix.Length).Trim()

                        match Int32.TryParse(lenStr) with
                        | true, len -> contentLength <- len
                        | false, _ -> ()

            if reachedEnd || contentLength < 0 then
                return None
            else
                let! body = reader.ReadBodyAsync(contentLength)
                return Some body
        }
