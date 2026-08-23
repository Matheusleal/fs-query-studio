module FsacBridgeRenameTests

open System
open Xunit
open FSharpQueryStudio.Fsac

[<Fact>]
let ``rename on a let binding returns every use, including the declaration itself`` () =
    task {
        let uri = $"file:///rename-test-{Guid.NewGuid()}.fsx"
        let source = "let total = 1\nlet doubled = total + total"
        // "total" ends at column 9 (0-based) on line 1: "let total = 1".
        let! result = FsacBridge.rename uri 1 source 1 9

        match result with
        | None -> failwith "expected reference edits"
        | Some edits ->
            // Declaration (line 0) + two uses (line 1) = 3 spans.
            Assert.Equal(3, edits.Length)
            Assert.Contains(edits, fun e -> e.StartLine = 0)
    }

[<Fact>]
let ``rename at a position with nothing resolvable returns None`` () =
    task {
        let uri = $"file:///rename-test-{Guid.NewGuid()}.fsx"
        let! result = FsacBridge.rename uri 1 "()" 1 0
        Assert.Equal(None, result)
    }
