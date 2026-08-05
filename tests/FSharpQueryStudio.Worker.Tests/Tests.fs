module Tests

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Worker

/// Simulates a slow/fragmented pipe: returns at most one byte per read,
/// regardless of how large a buffer the caller offers.
type private OneByteAtATimeStream(data: byte[]) =
    inherit Stream()
    let mutable pos = 0
    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = int64 data.Length
    override _.Position
        with get () = int64 pos
        and set _ = failwith "not supported"
    override _.Flush() = ()
    override _.Seek(_, _) = failwith "not supported"
    override _.SetLength(_) = failwith "not supported"
    override _.Write(_: byte[], _: int, _: int) = failwith "not supported"

    override _.Read(buffer: byte[], offset: int, _count: int) : int =
        if pos >= data.Length then
            0
        else
            buffer.[offset] <- data.[pos]
            pos <- pos + 1
            1

    override _.ReadAsync
        (buffer: Memory<byte>, _cancellationToken: CancellationToken)
        : ValueTask<int> =
        if pos >= data.Length then
            ValueTask<int>(0)
        else
            buffer.Span.[0] <- data.[pos]
            pos <- pos + 1
            ValueTask<int>(1)

let private sampleRequest (paramsJson: string) =
    { Id = "req-1"
      Method = "worker/health"
      Params = Text.Json.JsonDocument.Parse(paramsJson).RootElement }

[<Fact>]
let ``readFrame reassembles a full line from a stream that delivers one byte at a time`` () =
    task {
        let frame = sampleRequest """{"note":"hello"}"""
        let json = Text.Json.JsonSerializer.Serialize(frame, Serialization.options)
        let bytes = Encoding.UTF8.GetBytes(json + "\n")
        use reader = new StreamReader(new OneByteAtATimeStream(bytes), Encoding.UTF8)

        let! result = NdjsonStream.readFrame<WorkerRequestFrame> reader

        match result with
        | Some got ->
            Assert.Equal(frame.Id, got.Id)
            Assert.Equal(frame.Method, got.Method)
        | None -> failwith "expected a frame, got end of stream"
    }

[<Fact>]
let ``a string value containing a newline round-trips as exactly one frame`` () =
    task {
        let frame = sampleRequest """{"note":"line one"}"""
        let framWithNewline =
            { frame with
                Params = Text.Json.JsonDocument.Parse("""{"note":"line one\nline two"}""").RootElement }

        use stream = new MemoryStream()
        use writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen = true)
        do! NdjsonStream.writeFrame writer framWithNewline
        stream.Position <- 0L

        use reader = new StreamReader(stream, Encoding.UTF8)
        let! first = NdjsonStream.readFrame<WorkerRequestFrame> reader
        let! second = NdjsonStream.readFrame<WorkerRequestFrame> reader

        match first with
        | Some got -> Assert.Equal("line one\nline two", got.Params.GetProperty("note").GetString())
        | None -> failwith "expected a frame, got end of stream"

        Assert.True(second.IsNone)
    }

[<Fact>]
let ``a Unicode value round-trips exactly`` () =
    task {
        let frame = sampleRequest """{"note":"café 😀 日本語"}"""

        use stream = new MemoryStream()
        use writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen = true)
        do! NdjsonStream.writeFrame writer frame
        stream.Position <- 0L

        use reader = new StreamReader(stream, Encoding.UTF8)
        let! result = NdjsonStream.readFrame<WorkerRequestFrame> reader

        match result with
        | Some got -> Assert.Equal("café 😀 日本語", got.Params.GetProperty("note").GetString())
        | None -> failwith "expected a frame, got end of stream"
    }

[<Fact>]
let ``readFrame returns None at end of stream`` () =
    task {
        use reader = new StreamReader(new MemoryStream([||]), Encoding.UTF8)
        let! result = NdjsonStream.readFrame<WorkerRequestFrame> reader
        Assert.True(result.IsNone)
    }
