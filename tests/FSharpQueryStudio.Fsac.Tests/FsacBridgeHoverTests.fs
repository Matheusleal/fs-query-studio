module FsacBridgeHoverTests

open System
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
        | Some info -> Assert.Equal("sql", info.DisplayName)
    }

[<Fact>]
let ``hover at a position with no resolvable symbol returns None`` () =
    task {
        let uri = $"file:///hover-test-{Guid.NewGuid()}.fsx"
        let! result = FsacBridge.hover uri 1 "()" 1 0
        Assert.Equal(None, result)
    }
