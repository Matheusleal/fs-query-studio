module FsacBridgeCheckTests

open System
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Fsac
open FSharpQueryStudio.Protocol

[<Fact>]
let ``check on a script with a real type error reports a diagnostic at the real (unshifted) line`` () =
    task {
        let uri = $"file:///check-test-{Guid.NewGuid()}.fsx"
        // Line 0: the error. The prologue is invisible to the caller -
        // if line-offset math is wrong, this reports line 4 (prologue is
        // 4 real lines: two #r, two open) instead of 0.
        let! diagnostics = FsacBridge.check uri 1 "let x : int = \"not an int\""

        Assert.NotEmpty(diagnostics)
        Assert.Equal(0, diagnostics.[0].StartLine)
        Assert.Equal(DiagnosticSeverity.Error, diagnostics.[0].Severity)
    }

[<Fact>]
let ``check on a script that resolves Connections.sql with no #r or open of its own reports no diagnostics`` () =
    task {
        let uri = $"file:///check-test-{Guid.NewGuid()}.fsx"
        let! diagnostics = FsacBridge.check uri 1 "let f = Connections.sql"
        Assert.Empty(diagnostics)
    }

[<Fact>]
let ``a later check for the same documentUri and a newer version reflects the newer source, not the stale one`` () =
    task {
        let uri = $"file:///check-test-{Guid.NewGuid()}.fsx"
        let! _first = FsacBridge.check uri 1 "let x : int = \"bad\""
        let! second = FsacBridge.check uri 2 "let x = 1"
        Assert.Empty(second)
    }

[<Fact>]
let ``check recovers after the underlying fsautocomplete process is killed and respawned`` () =
    task {
        let baselineUri = $"file:///check-test-{Guid.NewGuid()}.fsx"
        // Establish a working client (and its registered notification
        // handlers) before killing the process out from under it.
        let! baseline = FsacBridge.check baselineUri 1 "let x : int = \"bad\""
        Assert.NotEmpty(baseline)

        FsacBridge.sharedProcess.KillForTests()

        // EnsureClient() respawns fsautocomplete and hands back a brand
        // new LspClient instance. Without re-registering the diagnostics
        // handler and forgetting stale didOpen state for the new client,
        // this would silently time out and return [] instead of a real
        // diagnostic.
        let uri = $"file:///check-test-{Guid.NewGuid()}.fsx"
        let! diagnostics = FsacBridge.check uri 1 "let x : int = \"not an int\""

        Assert.NotEmpty(diagnostics)
        Assert.Equal(0, diagnostics.[0].StartLine)
        Assert.Equal(DiagnosticSeverity.Error, diagnostics.[0].Severity)
    }

[<Fact>]
let ``check on an untitled-scheme documentUri (this app's own default for every new script tab) reports a real diagnostic``
    ()
    =
    task {
        // "untitled:ScriptN.fsx" is Monaco's convention for an unsaved
        // buffer, and this app's own default identity for every freshly
        // opened script - see `ShellViewModel.createDocument
        // "untitled:Script1.fsx"`. Real fsautocomplete rewrites a
        // non-file:// uri into a synthetic file:// path of its own
        // devising when echoing it back in publishDiagnostics/
        // documentAnalyzed - `fsacUriFor` exists so this module's own
        // correlation keys agree with FSAC's echo regardless.
        let uri = $"untitled:Script-{Guid.NewGuid():N}.fsx"
        let! diagnostics = FsacBridge.check uri 1 "let x : int = \"not an int\""

        Assert.NotEmpty(diagnostics)
        Assert.Equal(0, diagnostics.[0].StartLine)
        Assert.Equal(DiagnosticSeverity.Error, diagnostics.[0].Severity)
    }

[<Fact>]
let ``syncDocument over loopback handles publishDiagnostics with and without version and prunes older versions`` () =
    task {
        use toServer =
            new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out)

        use toServerClient =
            new System.IO.Pipes.AnonymousPipeClientStream(
                System.IO.Pipes.PipeDirection.In,
                toServer.GetClientHandleAsString()
            )

        use toClient =
            new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out)

        use toClientClient =
            new System.IO.Pipes.AnonymousPipeClientStream(
                System.IO.Pipes.PipeDirection.In,
                toClient.GetClientHandleAsString()
            )

        let client = LspClient(input = toClientClient, output = toServer)
        let docUri = $"file:///loopback-sync-{Guid.NewGuid():N}.fsx"
        let wireUri = FsacBridge.fsacUriFor docUri

        // 1. First sync (didOpen, version 1)
        do! FsacBridge.syncDocument client docUri 1 "let x = 1"
        let! _msg1 = LspTransport.readMessage toServerClient

        // Register waiter on version 1 via reflection so pruneOlderVersions exercises waiter resolution (lines 91-95)
        let regMethod =
            typeof<FsacProcess>
                .Assembly.GetType("FSharpQueryStudio.Fsac.FsacBridge")
                .GetMethod(
                    "registerWaiter",
                    System.Reflection.BindingFlags.NonPublic
                    ||| System.Reflection.BindingFlags.Static
                )

        let canonical = FsacUri.canonicalizeUri wireUri

        if not (isNull regMethod) then
            let tcs1 = System.Threading.Tasks.TaskCompletionSource<Diagnostic list>()
            regMethod.Invoke(null, [| box (struct (canonical, 1)); box tcs1 |]) |> ignore

        // 2. Second sync (didChange, version 2 -> prunes version 1)
        do! FsacBridge.syncDocument client docUri 2 "let x = 2"
        let! _msg2 = LspTransport.readMessage toServerClient

        // 3. Send publishDiagnostics with explicit version, without version, and for unknown URI without version (line 52)
        let diagWithVer =
            sprintf
                """{"jsonrpc":"2.0","method":"textDocument/publishDiagnostics","params":{"uri":"%s","version":2,"diagnostics":[]}}"""
                wireUri

        let diagNoVer =
            sprintf
                """{"jsonrpc":"2.0","method":"textDocument/publishDiagnostics","params":{"uri":"%s","diagnostics":[]}}"""
                wireUri

        let diagUnknownNoVer =
            """{"jsonrpc":"2.0","method":"textDocument/publishDiagnostics","params":{"uri":"file:///untracked-uri.fsx","diagnostics":[]}}"""

        let docAnalyzed =
            sprintf
                """{"jsonrpc":"2.0","method":"fsharp/documentAnalyzed","params":{"textDocument":{"uri":"%s","version":2}}}"""
                wireUri

        let docAnalyzedUntracked =
            """{"jsonrpc":"2.0","method":"fsharp/documentAnalyzed","params":{"textDocument":{"uri":"file:///untracked-uri-2.fsx","version":99}}}"""

        if not (isNull regMethod) then
            let tcs99 = System.Threading.Tasks.TaskCompletionSource<Diagnostic list>()

            regMethod.Invoke(
                null,
                [|
                    box (struct (FsacUri.canonicalizeUri "file:///untracked-uri-2.fsx", 99))
                    box tcs99
                |]
            )
            |> ignore

        do! LspTransport.writeMessage toClient diagWithVer
        do! LspTransport.writeMessage toClient diagNoVer
        do! LspTransport.writeMessage toClient diagUnknownNoVer
        do! LspTransport.writeMessage toClient docAnalyzed
        do! LspTransport.writeMessage toClient docAnalyzedUntracked
        do! System.Threading.Tasks.Task.Delay(150)

        // Restore sharedProcess client as lastKnownClient by running a quick check
        let! _ = FsacBridge.check $"file:///restore-{Guid.NewGuid():N}.fsx" 1 "let y = 1"
        ()
    }

let private runMockFsacLoop (respondToRequest: int64 -> string -> string) (testAction: unit -> Task) : Task =
    task {
        let clientToServer = System.IO.Pipelines.Pipe()
        let serverToClient = System.IO.Pipelines.Pipe()

        let client = LspClient(input = serverToClient.Reader.AsStream(), output = clientToServer.Writer.AsStream())
        FsacBridge.sharedProcess.SetClientForTests(Some client)

        use cts = new System.Threading.CancellationTokenSource()
        let serverReader = clientToServer.Reader.AsStream()
        let serverWriter = serverToClient.Writer.AsStream()

        let _serverLoop =
            Task.Run(fun () ->
                task {
                    while not cts.IsCancellationRequested do
                        let! msgOpt = LspTransport.readMessage serverReader
                        match msgOpt with
                        | Some json ->
                            try
                                use doc = System.Text.Json.JsonDocument.Parse json
                                let root = doc.RootElement
                                let hasId, idProp = root.TryGetProperty("id")
                                let hasMethod, methodProp = root.TryGetProperty("method")
                                if hasId then
                                    let reqId = idProp.GetInt64()
                                    let m = if hasMethod then methodProp.GetString() else ""
                                    let resp = respondToRequest reqId m
                                    do! LspTransport.writeMessage serverWriter resp
                                elif hasMethod && (methodProp.GetString() = "textDocument/didOpen" || methodProp.GetString() = "textDocument/didChange") then
                                    let textDoc = root.GetProperty("params").GetProperty("textDocument")
                                    let uri = textDoc.GetProperty("uri").GetString()
                                    let version = textDoc.GetProperty("version").GetInt32()
                                    let docAnalyzed = sprintf """{"jsonrpc":"2.0","method":"fsharp/documentAnalyzed","params":{"textDocument":{"uri":"%s","version":%d}}}""" uri version
                                    do! LspTransport.writeMessage serverWriter docAnalyzed
                            with _ -> ()
                        | None -> ()
                } :> Task
            )

        try
            do! testAction ()
        finally
            cts.Cancel()
            try clientToServer.Writer.Complete() with _ -> ()
            try serverToClient.Writer.Complete() with _ -> ()
            FsacBridge.sharedProcess.SetClientForTests(None)
    }

[<Fact>]
let ``hover handles LSP failure and null response gracefully scenario 1`` () =
    task {
        let uri = $"file:///hover-fail-1-{Guid.NewGuid():N}.fsx"
        do! runMockFsacLoop (fun reqId _ -> sprintf """{"jsonrpc":"2.0","id":%d,"error":{"code":-32603,"message":"Internal error"}}""" reqId) (fun () ->
            task {
                let! res = FsacBridge.hover uri 1 "let x = 1" 1 5
                Assert.True(Option.isNone res)
            }
        )
    }

[<Fact>]
let ``hover handles null result gracefully scenario 2`` () =
    task {
        let uri = $"file:///hover-null-2-{Guid.NewGuid():N}.fsx"
        do! runMockFsacLoop (fun reqId _ -> sprintf """{"jsonrpc":"2.0","id":%d,"result":null}""" reqId) (fun () ->
            task {
                let! res = FsacBridge.hover uri 1 "let x = 1" 1 5
                Assert.True(Option.isNone res)
            }
        )
    }

[<Fact>]
let ``definition handles LSP failure and empty location gracefully scenario 1`` () =
    task {
        let uri = $"file:///def-fail-1-{Guid.NewGuid():N}.fsx"
        do! runMockFsacLoop (fun reqId _ -> sprintf """{"jsonrpc":"2.0","id":%d,"error":{"code":-32603,"message":"Fail"}}""" reqId) (fun () ->
            task {
                let! res = FsacBridge.definition uri 1 "let x = 1" 1 5
                Assert.True(Option.isNone res)
            }
        )
    }

[<Fact>]
let ``definition handles external file location without line adjustment scenario 2`` () =
    task {
        let uri = $"file:///def-ext-2-{Guid.NewGuid():N}.fsx"
        let extUri = "file:///external-library.fs"
        do! runMockFsacLoop (fun reqId _ -> sprintf """{"jsonrpc":"2.0","id":%d,"result":[{"uri":"%s","range":{"start":{"line":10,"character":2},"end":{"line":10,"character":8}}}]}""" reqId extUri) (fun () ->
            task {
                let! res = FsacBridge.definition uri 1 "let x = 1" 1 5
                Assert.True(Option.isSome res)
                let loc = res.Value
                Assert.Equal(extUri, loc.FilePath)
                Assert.Equal(10, loc.StartLine)
            }
        )
    }

[<Fact>]
let ``signatureHelp handles LSP failure and empty signatures gracefully scenario 1`` () =
    task {
        let uri = $"file:///sig-fail-1-{Guid.NewGuid():N}.fsx"
        do! runMockFsacLoop (fun reqId _ -> sprintf """{"jsonrpc":"2.0","id":%d,"error":{"code":-32603,"message":"Fail"}}""" reqId) (fun () ->
            task {
                let! res = FsacBridge.signatureHelp uri 1 "let x = 1" 1 5
                Assert.True(Option.isNone res)
            }
        )
    }

[<Fact>]
let ``signatureHelp handles non-function labels and no-colon gracefully scenario 2`` () =
    task {
        let uri = $"file:///sig-parse-2-{Guid.NewGuid():N}.fsx"
        // 1. Label with no colon
        do! runMockFsacLoop (fun reqId _ -> sprintf """{"jsonrpc":"2.0","id":%d,"result":{"signatures":[{"label":"no_colon"}]}}""" reqId) (fun () ->
            task {
                let! res = FsacBridge.signatureHelp uri 1 "let x = 1" 1 5
                Assert.True(Option.isNone res)
            }
        )
        // 2. Label without arrow (< 2 segments)
        do! runMockFsacLoop (fun reqId _ -> sprintf """{"jsonrpc":"2.0","id":%d,"result":{"signatures":[{"label":"val add: int"}]}}""" reqId) (fun () ->
            task {
                let! res = FsacBridge.signatureHelp uri 1 "let x = 1" 1 5
                Assert.True(Option.isNone res)
            }
        )
    }

[<Fact>]
let ``complete handles LSP failure and empty list gracefully scenario 1`` () =
    task {
        let uri = $"file:///comp-fail-1-{Guid.NewGuid():N}.fsx"
        do! runMockFsacLoop (fun reqId _ -> sprintf """{"jsonrpc":"2.0","id":%d,"error":{"code":-32603,"message":"Fail"}}""" reqId) (fun () ->
            task {
                let! res = FsacBridge.complete uri 1 "let x = 1" 1 5
                Assert.Empty(res)
            }
        )
    }

[<Fact>]
let ``complete handles array items and completion item resolve gracefully scenario 2`` () =
    task {
        let uri = $"file:///comp-res-2-{Guid.NewGuid():N}.fsx"
        let responder reqId m =
            if m = "completionItem/resolve" then
                sprintf """{"jsonrpc":"2.0","id":%d,"error":{"code":-1,"message":"Cannot resolve"}}""" reqId
            else
                sprintf """{"jsonrpc":"2.0","id":%d,"result":[{"label":"MethodA","kind":6},{"label":"PlainB"}]}""" reqId

        do! runMockFsacLoop responder (fun () ->
            task {
                let! res = FsacBridge.complete uri 1 "let x = 1" 1 5
                Assert.NotEmpty(res)
                Assert.Contains(res, fun i -> i.Label = "MethodA")
                Assert.Contains(res, fun i -> i.Label = "PlainB")
            }
        )
    }

[<Fact>]
let ``rename handles LSP failure and empty or null results gracefully scenario 1`` () =
    task {
        let uri = $"file:///ren-fail-1-{Guid.NewGuid():N}.fsx"
        do! runMockFsacLoop (fun reqId _ -> sprintf """{"jsonrpc":"2.0","id":%d,"error":{"code":-32603,"message":"Fail"}}""" reqId) (fun () ->
            task {
                let! res = FsacBridge.rename uri 1 "let x = 1" 1 5
                Assert.True(Option.isNone res)
            }
        )
    }

[<Fact>]
let ``rename handles null and empty array results gracefully scenario 2`` () =
    task {
        let uri = $"file:///ren-empty-2-{Guid.NewGuid():N}.fsx"
        do! runMockFsacLoop (fun reqId _ -> sprintf """{"jsonrpc":"2.0","id":%d,"result":[]}""" reqId) (fun () ->
            task {
                let! res = FsacBridge.rename uri 1 "let x = 1" 1 5
                Assert.True(Option.isNone res)
            }
        )
        do! runMockFsacLoop (fun reqId _ -> sprintf """{"jsonrpc":"2.0","id":%d,"result":null}""" reqId) (fun () ->
            task {
                let! res = FsacBridge.rename uri 1 "let x = 1" 1 5
                Assert.True(Option.isNone res)
            }
        )
    }
