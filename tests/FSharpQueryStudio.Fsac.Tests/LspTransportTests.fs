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
