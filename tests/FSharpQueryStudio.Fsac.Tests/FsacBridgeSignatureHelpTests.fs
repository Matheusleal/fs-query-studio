module FsacBridgeSignatureHelpTests

open System
open Xunit
open FSharpQueryStudio.Fsac

[<Fact>]
let ``signatureHelp on a function symbol returns its parameters and return type`` () =
    task {
        let uri = $"file:///sighelp-test-{Guid.NewGuid()}.fsx"

        // Unlike `hover`/`definition`'s click-on-the-identifier model,
        // real fsautocomplete 0.83.0 only returns a non-null
        // `textDocument/signatureHelp` result when the requested position
        // is inside a call's argument list - confirmed during this task:
        // querying directly on `add`'s own definition-site name (the
        // brief's original test position) always came back `null`. So the
        // source here includes an actual call site, and the query targets
        // column 17 on line 2 - the space right after `add ` in
        // `let result = add 1 2`, i.e. where the first argument goes.
        let source = "let add (x: int) (y: int) : int = x + y\nlet result = add 1 2"
        let! result = FsacBridge.signatureHelp uri 1 source 2 17

        match result with
        | None -> failwith "expected signature help"
        | Some info ->
            Assert.Equal("add", info.DisplayName)
            Assert.Equal(2, info.Parameters.Length)
    }

[<Fact>]
let ``signatureHelp on a non-callable symbol returns None`` () =
    task {
        let uri = $"file:///sighelp-test-{Guid.NewGuid()}.fsx"
        // "total" ends at column 9 (0-based): "let total = 42".
        let! result = FsacBridge.signatureHelp uri 1 "let total = 42" 1 9
        Assert.Equal(None, result)
    }
