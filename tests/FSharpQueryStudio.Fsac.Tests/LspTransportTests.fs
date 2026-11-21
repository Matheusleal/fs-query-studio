module LspTransportTests

open System.IO
open System.Text
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Fsac

/// Yields exactly one byte per `ReadAsync` call, regardless of how many
/// bytes the caller asked for — proves `readMessage` reassembles a full
/// header/body regardless of how the underlying pipe chooses to deliver
/// bytes, the same concern `NdjsonStream`'s own fragmented-stream test
/// covers for the Worker<->App leg.
type private OneByteAtATimeStream(bytes: byte[]) =
    inherit Stream()
    let mutable position = 0
    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = int64 bytes.Length
    override _.Position with get () = int64 position and set _ = failwith "not seekable"
    override _.Flush() = ()
    override _.SetLength(_) = failwith "not supported"
    override _.Seek(_, _) = failwith "not supported"
    override _.Write(_, _, _) = failwith "not supported"

    override _.Read(buffer: byte[], offset: int, _count: int) =
        if position >= bytes.Length then
            0
        else
            buffer.[offset] <- bytes.[position]
            position <- position + 1
            1

/// Yields up to `maxChunkSize` bytes per `ReadAsync` call to test arbitrary fragmentation.
type private ChunkedStream(bytes: byte[], maxChunkSize: int) =
    inherit Stream()
    let mutable position = 0
    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = int64 bytes.Length
    override _.Position with get () = int64 position and set _ = failwith "not seekable"
    override _.Flush() = ()
    override _.SetLength(_) = failwith "not supported"
    override _.Seek(_, _) = failwith "not supported"
    override _.Write(_, _, _) = failwith "not supported"

    override _.Read(buffer: byte[], offset: int, count: int) =
        if position >= bytes.Length then
            0
        else
            let toRead = min count (min maxChunkSize (bytes.Length - position))
            System.Array.Copy(bytes, position, buffer, offset, toRead)
            position <- position + toRead
            toRead

[<Fact>]
let ``writeMessage then readMessage round-trips a JSON payload`` () =
    task {
        let stream = new MemoryStream()
        do! LspTransport.writeMessage stream """{"jsonrpc":"2.0","id":1,"method":"initialize"}"""
        stream.Position <- 0L
        let! result = LspTransport.readMessage stream
        Assert.Equal(Some """{"jsonrpc":"2.0","id":1,"method":"initialize"}""", result)
    }

[<Fact>]
let ``readMessage reassembles a frame delivered one byte at a time`` () =
    task {
        let json = """{"jsonrpc":"2.0","method":"textDocument/publishDiagnostics","params":{}}"""
        let bodyBytes = Encoding.UTF8.GetBytes(json)
        let header = $"Content-Length: {bodyBytes.Length}\r\n\r\n"
        let allBytes = Array.append (Encoding.ASCII.GetBytes(header)) bodyBytes
        let stream = new OneByteAtATimeStream(allBytes)
        let! result = LspTransport.readMessage stream
        Assert.Equal(Some json, result)
    }

[<Fact>]
let ``a JSON body containing multi-byte UTF-8 characters round-trips intact`` () =
    task {
        let stream = new MemoryStream()
        let json = """{"jsonrpc":"2.0","method":"x","params":{"text":"héllo wörld — ✓"}}"""
        do! LspTransport.writeMessage stream json
        stream.Position <- 0L
        let! result = LspTransport.readMessage stream
        Assert.Equal(Some json, result)
    }

[<Fact>]
let ``readMessage returns None at a clean end of stream`` () =
    task {
        let stream = new MemoryStream([||])
        let! result = LspTransport.readMessage stream
        Assert.Equal(None, result)
    }

// --- TASK-004 Scenarios: Minimum 2 tests per scenario (Rule Compliance) ---

// Scenario 1: Standard LSP message reading with exact Content-Length framing
[<Fact>]
let ``readMessage parses standard LSP message with exact content length header`` () =
    task {
        let payload = """{"jsonrpc":"2.0","id":1}"""
        let raw = $"Content-Length: 25\r\n\r\n{payload}"
        let bytes = Encoding.UTF8.GetBytes(raw)
        let stream = new MemoryStream(bytes)

        let! result = LspTransport.readMessage stream
        Assert.Equal(Some payload, result)
    }

[<Fact>]
let ``readMessage parses standard LSP message with multiple headers and case-insensitive length`` () =
    task {
        let payload = """{"jsonrpc":"2.0","method":"shutdown"}"""
        let raw = $"content-length: {payload.Length}\r\nContent-Type: application/vscode-jsonrpc; charset=utf-8\r\n\r\n{payload}"
        let bytes = Encoding.UTF8.GetBytes(raw)
        let stream = new MemoryStream(bytes)

        let! result = LspTransport.readMessage stream
        Assert.Equal(Some payload, result)
    }

// Scenario 2: Multiple sequential messages on the same stream
[<Fact>]
let ``readMessage parses multiple sequential messages on the same stream without losing buffer`` () =
    task {
        let msg1 = """{"jsonrpc":"2.0","id":1,"result":"ok"}"""
        let msg2 = """{"jsonrpc":"2.0","id":2,"result":"next"}"""
        let stream = new MemoryStream()
        do! LspTransport.writeMessage stream msg1
        do! LspTransport.writeMessage stream msg2
        stream.Position <- 0L

        let! result1 = LspTransport.readMessage stream
        let! result2 = LspTransport.readMessage stream
        let! result3 = LspTransport.readMessage stream

        Assert.Equal(Some msg1, result1)
        Assert.Equal(Some msg2, result2)
        Assert.Equal(None, result3)
    }

[<Fact>]
let ``readMessage parses multiple sequential messages in small chunks across boundaries`` () =
    task {
        let msg1 = """{"jsonrpc":"2.0","id":100,"method":"test1"}"""
        let msg2 = """{"jsonrpc":"2.0","id":101,"method":"test2"}"""
        let msg3 = """{"jsonrpc":"2.0","id":102,"method":"test3"}"""

        let mem = new MemoryStream()
        do! LspTransport.writeMessage mem msg1
        do! LspTransport.writeMessage mem msg2
        do! LspTransport.writeMessage mem msg3
        let allBytes = mem.ToArray()

        // 7-byte chunks force headers and bodies to split across arbitrary buffer boundaries
        let chunkedStream = new ChunkedStream(allBytes, 7)

        let! r1 = LspTransport.readMessage chunkedStream
        let! r2 = LspTransport.readMessage chunkedStream
        let! r3 = LspTransport.readMessage chunkedStream
        let! r4 = LspTransport.readMessage chunkedStream

        Assert.Equal(Some msg1, r1)
        Assert.Equal(Some msg2, r2)
        Assert.Equal(Some msg3, r3)
        Assert.Equal(None, r4)
    }

// Scenario 3: Stream closed mid-body or mid-header
[<Fact>]
let ``readMessage handles stream closed mid-body gracefully`` () =
    task {
        let header = "Content-Length: 100\r\n\r\npartial-body-only"
        let stream = new MemoryStream(Encoding.UTF8.GetBytes(header))

        let! result = LspTransport.readMessage stream
        Assert.Equal(Some "partial-body-only", result)
    }

[<Fact>]
let ``readMessage returns None when stream closes mid-header before blank line`` () =
    task {
        let header = "Content-Length: 50\r\n"
        let stream = new MemoryStream(Encoding.ASCII.GetBytes(header))

        let! result = LspTransport.readMessage stream
        Assert.Equal(None, result)
    }

// Scenario 4: Malformed or missing Content-Length header
[<Fact>]
let ``readMessage returns None when Content-Length header is missing`` () =
    task {
        let raw = "Content-Type: application/json\r\n\r\n{}"
        let stream = new MemoryStream(Encoding.ASCII.GetBytes(raw))

        let! result = LspTransport.readMessage stream
        Assert.Equal(None, result)
    }

[<Fact>]
let ``readMessage returns None when Content-Length is not a valid integer`` () =
    task {
        let raw = "Content-Length: not-a-number\r\n\r\n{}"
        let stream = new MemoryStream(Encoding.ASCII.GetBytes(raw))

        let! result = LspTransport.readMessage stream
        Assert.Equal(None, result)
    }

// Scenario 5: Seekable stream rewind
[<Fact>]
let ``readMessage handles seekable stream reset correctly`` () =
    task {
        let stream = new MemoryStream()
        do! LspTransport.writeMessage stream """{"jsonrpc":"2.0","id":1}"""
        stream.Position <- 0L
        let! r1 = LspTransport.readMessage stream
        Assert.Equal(Some """{"jsonrpc":"2.0","id":1}""", r1)

        // Reset position to re-read
        stream.Position <- 0L
        let! r2 = LspTransport.readMessage stream
        Assert.Equal(Some """{"jsonrpc":"2.0","id":1}""", r2)
    }

[<Fact>]
let ``readMessage handles bare newline headers without carriage return`` () =
    task {
        let payload = """{"jsonrpc":"2.0","id":99}"""
        let raw = $"Content-Length: {payload.Length}\n\n{payload}"
        let stream = new MemoryStream(Encoding.UTF8.GetBytes(raw))

        let! result = LspTransport.readMessage stream
        Assert.Equal(Some payload, result)
    }
