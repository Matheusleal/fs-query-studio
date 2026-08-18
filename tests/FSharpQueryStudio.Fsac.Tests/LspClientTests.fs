module LspClientTests

open System.IO.Pipes
open System.Text.Json
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Fsac

type private Loopback =
    { OurClient: LspClient
      ServerRead: AnonymousPipeClientStream
      ServerWrite: AnonymousPipeServerStream }

    interface System.IDisposable with
        member this.Dispose() =
            this.ServerRead.Dispose()
            this.ServerWrite.Dispose()

/// Two pipe pairs, wired so `LspClient` writes onto one and a hand-written
/// "fake server" (this test file) reads from it, and vice versa — a real
/// in-process JSON-RPC round trip with no dependency on the real
/// `fsautocomplete` binary.
let private makeLoopback () =
    let toServer = new AnonymousPipeServerStream(PipeDirection.Out)
    let toServerClient = new AnonymousPipeClientStream(PipeDirection.In, toServer.GetClientHandleAsString())
    let toClient = new AnonymousPipeServerStream(PipeDirection.Out)
    let toClientClient = new AnonymousPipeClientStream(PipeDirection.In, toClient.GetClientHandleAsString())

    let ourClient = LspClient(input = toClientClient, output = toServer)

    { OurClient = ourClient
      ServerRead = toServerClient
      ServerWrite = toClient }

[<Fact>]
let ``SendRequest returns the matching response's result`` () =
    task {
        use loopback = makeLoopback ()

        let serverTask =
            task {
                let! raw = LspTransport.readMessage loopback.ServerRead
                let requestId = JsonDocument.Parse(raw.Value).RootElement.GetProperty("id").GetInt32()
                let response = $"""{{"jsonrpc":"2.0","id":{requestId},"result":{{"ok":true}}}}"""
                do! LspTransport.writeMessage loopback.ServerWrite response
            }

        let! result = loopback.OurClient.SendRequest("workspace/echo", {| ping = "hi" |})
        do! serverTask

        match result with
        | LspOk element -> Assert.True(element.GetProperty("ok").GetBoolean())
        | LspFail err -> failwith $"expected success, got error: {err.Message}"
    }

[<Fact>]
let ``SendRequest surfaces a JSON-RPC error response`` () =
    task {
        use loopback = makeLoopback ()

        let serverTask =
            task {
                let! raw = LspTransport.readMessage loopback.ServerRead
                let requestId = JsonDocument.Parse(raw.Value).RootElement.GetProperty("id").GetInt32()
                let response = $"""{{"jsonrpc":"2.0","id":{requestId},"error":{{"code":-32601,"message":"boom"}}}}"""
                do! LspTransport.writeMessage loopback.ServerWrite response
            }

        let! result = loopback.OurClient.SendRequest("workspace/echo", {| ping = "hi" |})
        do! serverTask

        match result with
        | LspOk _ -> failwith "expected an error"
        | LspFail err ->
            Assert.Equal(-32601, err.Code)
            Assert.Equal("boom", err.Message)
    }

[<Fact>]
let ``OnNotification fires for a matching server-initiated notification`` () =
    task {
        use loopback = makeLoopback ()
        let received = TaskCompletionSource<string>()

        loopback.OurClient.OnNotification(
            "textDocument/publishDiagnostics",
            fun paramsEl -> received.TrySetResult(paramsEl.GetProperty("uri").GetString()) |> ignore
        )

        let notification =
            """{"jsonrpc":"2.0","method":"textDocument/publishDiagnostics","params":{"uri":"file:///a.fsx","diagnostics":[]}}"""

        do! LspTransport.writeMessage loopback.ServerWrite notification
        let! uri = received.Task
        Assert.Equal("file:///a.fsx", uri)
    }

[<Fact>]
let ``a throwing notification handler does not stop the reader loop from dispatching later notifications`` () =
    task {
        use loopback = makeLoopback ()
        let received = TaskCompletionSource<string>()

        loopback.OurClient.OnNotification("test/throws", (fun _ -> failwith "boom"))

        loopback.OurClient.OnNotification(
            "test/succeeds",
            fun paramsEl -> received.TrySetResult(paramsEl.GetProperty("value").GetString()) |> ignore
        )

        let throwingNotification = """{"jsonrpc":"2.0","method":"test/throws","params":{}}"""

        let succeedingNotification =
            """{"jsonrpc":"2.0","method":"test/succeeds","params":{"value":"still alive"}}"""

        do! LspTransport.writeMessage loopback.ServerWrite throwingNotification
        do! LspTransport.writeMessage loopback.ServerWrite succeedingNotification

        let! value = received.Task
        Assert.Equal("still alive", value)
    }
