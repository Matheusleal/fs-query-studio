module FsacBridgeDefinitionTests

open System
open Xunit
open FSharpQueryStudio.Fsac

[<Fact>]
let ``definition on a same-file use resolves to the declaration, with the caller's own DocumentUri`` () =
    task {
        let uri = $"file:///definition-test-{Guid.NewGuid()}.fsx"
        let source = "let add x y = x + y\nlet total = add 1 2"
        // "add" on line 2 ends at column 15 (0-based): "let total = add 1 2".
        let! result = FsacBridge.definition uri 1 source 2 15

        match result with
        | None -> failwith "expected a resolved definition"
        | Some loc ->
            Assert.Equal(uri, loc.FilePath)
            Assert.Equal(0, loc.StartLine) // the declaration is on the real (unshifted) line 0
    }

[<Fact>]
let ``definition at a position with nothing resolvable returns None`` () =
    task {
        let uri = $"file:///definition-test-{Guid.NewGuid()}.fsx"
        let! result = FsacBridge.definition uri 1 "()" 1 0
        Assert.Equal(None, result)
    }
