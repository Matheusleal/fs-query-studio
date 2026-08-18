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
