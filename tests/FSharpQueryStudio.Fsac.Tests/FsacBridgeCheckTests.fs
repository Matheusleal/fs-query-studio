module FsacBridgeCheckTests

open System
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
let ``check on an untitled-scheme documentUri (this app's own default for every new script tab) reports a real diagnostic`` () =
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
