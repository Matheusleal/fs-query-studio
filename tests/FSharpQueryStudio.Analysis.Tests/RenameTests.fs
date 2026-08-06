module RenameTests

open System.IO
open Xunit
open FSharpQueryStudio.Analysis

let private source = "let total = 1\nlet a = total + 1\nlet b = total + 2\n"

let private referencesAt (source: string) (line: int) (column: int) =
    let dir = TestHelpers.tempWorkingDirectory ()

    try
        let context = AnalysisContext.build dir
        Rename.findReferences context source line column |> Async.RunSynchronously
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``findReferences on a binding's declaration returns an edit for every use, not just the declaration`` () =
    // "total" ends at column 9 (0-based) on line 1: "let total = 1".
    match referencesAt source 1 9 with
    | None -> failwith "expected a list of references"
    | Some edits ->
        Assert.Equal(3, edits.Length)
        Assert.Equal<int list>([ 0; 1; 2 ], edits |> List.map (fun e -> e.StartLine) |> List.sort)

[<Fact>]
let ``findReferences on a use site returns the same edit set as clicking the declaration`` () =
    // "total" ends at column 13 (0-based) on line 2: "let a = total + 1".
    match referencesAt source 2 13 with
    | None -> failwith "expected a list of references"
    | Some edits ->
        Assert.Equal(3, edits.Length)
        Assert.Equal<int list>([ 0; 1; 2 ], edits |> List.map (fun e -> e.StartLine) |> List.sort)

[<Fact>]
let ``findReferences on a binding used exactly once returns a single edit for the declaration alone`` () =
    let onceSource = "let onlyHere = 1\n"
    // "onlyHere" ends at column 12 (0-based) on line 1.
    match referencesAt onceSource 1 12 with
    | None -> failwith "expected a list of references"
    | Some edits -> Assert.Equal(1, edits.Length)

[<Fact>]
let ``findReferences at a position with no resolvable symbol returns None`` () =
    // Line 2 of "\n\n" is blank.
    Assert.Equal(None, referencesAt "\n\n" 2 0)
