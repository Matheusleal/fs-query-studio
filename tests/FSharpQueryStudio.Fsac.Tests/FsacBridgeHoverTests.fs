module FsacBridgeHoverTests

open System
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Fsac

[<Fact>]
let ``hover on a typed let binding returns its display name and inferred type`` () =
    task {
        let uri = $"file:///hover-test-{Guid.NewGuid()}.fsx"
        // "total" ends at column 9 (0-based) on line 1: "let total = 42".
        let! result = FsacBridge.hover uri 1 "let total = 42" 1 9

        match result with
        | None -> failwith "expected hover info"
        | Some info ->
            Assert.Equal("total", info.DisplayName)
            Assert.Equal("int", info.Type)
    }

[<Fact>]
let ``hover on Connections.sql resolves with no #r or open of its own`` () =
    task {
        let uri = $"file:///hover-test-{Guid.NewGuid()}.fsx"
        let! result = FsacBridge.hover uri 1 "Connections.sql" 1 15

        match result with
        | None -> failwith "expected hover info"
        | Some info ->
            Assert.Equal("sql", info.DisplayName)
            // Real FSAC's `contents` array puts a "**Description**"
            // element directly adjacent to the signature's last line,
            // with no blank-line separator between them. A parser that
            // re-derives the signature/doc boundary from whitespace
            // (instead of the array's own element boundaries) swallows
            // "**Description**" into `Type`, corrupting it - this is
            // exactly that regression check.
            Assert.Contains("SqlCategory", info.Type)
            Assert.DoesNotContain("**Description**", info.Type)
    }

[<Fact>]
let ``hover at a position with no resolvable symbol returns None`` () =
    task {
        let uri = $"file:///hover-test-{Guid.NewGuid()}.fsx"
        let! result = FsacBridge.hover uri 1 "()" 1 0
        Assert.Equal(None, result)
    }

[<Fact>]
let ``check and hover racing for the same documentUri and version both resolve correctly`` () =
    task {
        let uri = $"file:///hover-test-{Guid.NewGuid()}.fsx"
        // A real type error, so `check`'s result is verifiably correct
        // only if the genuine diagnostics come back - not a false
        // positive from the `[]` fallback a lost waiter would produce.
        let source = "let x : int = \"not an int\""

        // Both started (and racing for the same `diagnosticsPending`
        // key) before either is awaited - `Worker.Program.fs` backgrounds
        // `analysis/check`/`analysis/hover` independently with no
        // serialization between them, so this reproduces a real scenario.
        let checkTask = FsacBridge.check uri 1 source
        let hoverTask = FsacBridge.hover uri 1 source 1 5

        let! diagnostics = checkTask
        let! _ = hoverTask

        Assert.NotEmpty(diagnostics)
    }
