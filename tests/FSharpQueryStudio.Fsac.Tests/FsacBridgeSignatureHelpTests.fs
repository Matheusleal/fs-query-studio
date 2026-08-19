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
let ``signatureHelp on a higher-order function doesn't fragment a function-typed parameter`` () =
    task {
        let uri = $"file:///sighelp-test-{Guid.NewGuid()}.fsx"

        // `apply`'s real fsautocomplete 0.83.0 signature-help label is
        // `"val apply: f: (int -> int) -> x: int -> int"` - a naive split
        // on every literal "->" tears the parenthesized `f: (int -> int)`
        // parameter in two (`["f: (int"; "int)"; "x: int"; "int"]`
        // instead of `["f: (int -> int)"; "x: int"; "int"]`), producing
        // `Parameters.Length = 3` instead of the correct 2 and a mangled
        // first parameter's text. Position here is right after `apply `
        // on line 2 (before the `(fun ...)` argument).
        let source = "let apply (f: int -> int) (x: int) = f x\nlet r = apply (fun n -> n + 1) 5"
        let! result = FsacBridge.signatureHelp uri 1 source 2 14

        match result with
        | None -> failwith "expected signature help"
        | Some info ->
            Assert.Equal("apply", info.DisplayName)
            Assert.Equal(2, info.Parameters.Length)
            Assert.Equal("f: (int -> int)", info.Parameters.[0])
            Assert.Equal("x: int", info.Parameters.[1])
            Assert.Equal("int", info.ReturnType)
    }

[<Fact>]
let ``signatureHelp on a non-callable symbol returns None`` () =
    task {
        let uri = $"file:///sighelp-test-{Guid.NewGuid()}.fsx"
        // "total" ends at column 9 (0-based): "let total = 42".
        let! result = FsacBridge.signatureHelp uri 1 "let total = 42" 1 9
        Assert.Equal(None, result)
    }
