module SignatureHelpTests

open System.IO
open Xunit
open FSharpQueryStudio.Analysis

let private source = "let add (x: int) (y: int) : int = x + y\n\nlet total = 42\n"

let private signatureHelpAt (source: string) (line: int) (column: int) =
    let dir = TestHelpers.tempWorkingDirectory ()

    try
        let context = AnalysisContext.build dir
        SignatureHelp.signatureHelp context source line column |> Async.RunSynchronously
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``signature help on a function returns its display name, parameters, and return type`` () =
    // "add" ends at column 6 (0-based) on line 1: "let add (x: int) (y: int) : int = x + y".
    match signatureHelpAt source 1 6 with
    | None -> failwith "expected signature help"
    | Some info ->
        Assert.Equal("add", info.DisplayName)
        Assert.Equal<string list>([ "x: int"; "y: int" ], info.Parameters)
        Assert.Equal("int", info.ReturnType)

[<Fact>]
let ``signature help on a plain value (no parameters) returns None`` () =
    // "total" ends at column 9 (0-based) on line 3: "let total = 42".
    Assert.Equal(None, signatureHelpAt source 3 9)

[<Fact>]
let ``signature help at a position with no resolvable symbol returns None`` () =
    // Line 2 is blank.
    Assert.Equal(None, signatureHelpAt source 2 0)

[<Fact>]
let ``signature help on Connections.sql resolves with no #r or open of its own`` () =
    // "Connections.sql" ends at column 15 (0-based). Same rationale as
    // HoverTests' matching case: live analysis must see what real
    // execution's own auto-reference already gives a running script.
    match signatureHelpAt "Connections.sql" 1 15 with
    | None -> failwith "expected signature help"
    | Some info -> Assert.Equal("sql", info.DisplayName)
