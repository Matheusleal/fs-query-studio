module GoToDefinitionTests

open System.IO
open Xunit
open FSharpQueryStudio.Analysis

let private source = "let helper (x: int) : int = x + 1\n\nlet total = helper 41\n"

let private definitionAt (source: string) (line: int) (column: int) =
    let dir = TestHelpers.tempWorkingDirectory ()

    try
        let context = AnalysisContext.build dir
        GoToDefinition.findDefinition context source line column |> Async.RunSynchronously
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``go-to-definition on a use site resolves to the binding's own declaration`` () =
    // "helper" ends at column 18 (0-based) on line 3: "let total = helper 41".
    match definitionAt source 3 18 with
    | None -> failwith "expected a definition location"
    | Some location ->
        Assert.Equal(0, location.StartLine)
        Assert.Equal(4, location.StartColumn)
        Assert.Equal(10, location.EndColumn)

[<Fact>]
let ``go-to-definition at a position with no resolvable symbol returns None`` () =
    // Line 2 is blank.
    Assert.Equal(None, definitionAt source 2 0)

/// The real point of go-to-definition across an E.5 multi-file project:
/// resolving to a *different file's* real path, not just a position
/// within the file being checked.
[<Fact>]
let ``go-to-definition on a reference to another file in a multi-file project resolves to that file's path`` () =
    let dir = TestHelpers.tempWorkingDirectory ()

    try
        let pathA = Path.Combine(dir, "A.fs")
        let pathB = Path.Combine(dir, "B.fs")
        File.WriteAllText(pathA, "module A\nlet value = 42")
        let sourceB = "module B\nlet result = A.value + 1"
        File.WriteAllText(pathB, sourceB)

        // `findDefinition` checks whichever file `context.ScriptPath`
        // names - point it at B.fs so the lookup happens against the
        // file actually referencing A's `value`.
        let context =
            { AnalysisContext.buildForProject dir [ pathA; pathB ] with
                ScriptPath = pathB }

        // "value" in "A.value" ends at column 20 (0-based) on line 2:
        // "let result = A.value + 1".
        match GoToDefinition.findDefinition context sourceB 2 20 |> Async.RunSynchronously with
        | None -> failwith "expected a definition location"
        | Some location ->
            Assert.Equal(pathA, location.FilePath)
            Assert.Equal(1, location.StartLine)
    finally
        Directory.Delete(dir, true)
