module MultiFileProjectTests

open System.IO
open Xunit
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FSharpQueryStudio.Analysis

[<Fact>]
let ``buildForProject exposes SourceFiles in the given order`` () =
    let dir = TestHelpers.tempWorkingDirectory ()

    try
        let files = [ Path.Combine(dir, "A.fs"); Path.Combine(dir, "B.fs"); Path.Combine(dir, "C.fs") ]

        let context = AnalysisContext.buildForProject dir files

        Assert.Equal<string list>(files, context.ProjectOptions.SourceFiles |> List.ofArray)
    finally
        Directory.Delete(dir, true)

/// The real point of "resolving a correct compile order": a later file
/// can see an earlier file's definitions purely because of where it sits
/// in `SourceFiles`, proven against real on-disk files and a real FCS
/// project-mode check — not just that the list itself came out in order.
[<Fact>]
let ``a file checked against a multi-file project sees definitions from files compiled before it`` () =
    let dir = TestHelpers.tempWorkingDirectory ()

    try
        let pathA = Path.Combine(dir, "A.fs")
        let pathB = Path.Combine(dir, "B.fs")
        File.WriteAllText(pathA, "module A\nlet value = 42")
        let sourceB = "module B\nlet result = A.value + 1"
        File.WriteAllText(pathB, sourceB)

        let context = AnalysisContext.buildForProject dir [ pathA; pathB ]

        let _parseResults, checkAnswer =
            AnalysisContext.checker.ParseAndCheckFileInProject(
                pathB,
                0,
                SourceText.ofString sourceB,
                context.ProjectOptions
            )
            |> Async.RunSynchronously

        match checkAnswer with
        | FSharpCheckFileAnswer.Succeeded results -> Assert.Empty(results.Diagnostics)
        | FSharpCheckFileAnswer.Aborted -> failwith "expected the check to complete"
    finally
        Directory.Delete(dir, true)

/// Paired with the case above: same two files, same content shape, but
/// the file referencing the other one now comes *first* in compile
/// order - proving the earlier test's success is actually conditioned on
/// order, not an artifact of the files' content.
[<Fact>]
let ``a file checked against a multi-file project cannot see definitions from files compiled after it`` () =
    let dir = TestHelpers.tempWorkingDirectory ()

    try
        let pathA = Path.Combine(dir, "A.fs")
        let pathB = Path.Combine(dir, "B.fs")
        let sourceA = "module A\nlet result = B.value + 1"
        File.WriteAllText(pathA, sourceA)
        File.WriteAllText(pathB, "module B\nlet value = 42")

        // B comes after A in compile order, so A can't see B's `value`.
        let context = AnalysisContext.buildForProject dir [ pathA; pathB ]

        let _parseResults, checkAnswer =
            AnalysisContext.checker.ParseAndCheckFileInProject(
                pathA,
                0,
                SourceText.ofString sourceA,
                context.ProjectOptions
            )
            |> Async.RunSynchronously

        match checkAnswer with
        | FSharpCheckFileAnswer.Succeeded results -> Assert.NotEmpty(results.Diagnostics)
        | FSharpCheckFileAnswer.Aborted -> failwith "expected the check to complete"
    finally
        Directory.Delete(dir, true)
