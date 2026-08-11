module MilestoneEAcceptanceTests

open System
open System.IO
open Xunit
open FSharpQueryStudio.Analysis
open FSharpQueryStudio.App
open FSharpQueryStudio.Execution
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Storage
open FSharpQueryStudio.Ui
open TestHelpers

let private tempDir (prefix: string) =
    let dir = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    dir

/// `FsiSession.create` locks its working directory (Windows) for as long
/// as it's the process's current directory - matches Execution.Tests/
/// NotebookExecutionTests.fs's own helper.
let private cleanupWorkingDirectory (dir: string) =
    Directory.SetCurrentDirectory(Path.GetTempPath())
    Directory.Delete(dir, true)

/// Milestone E's synthesized acceptance criterion (no verbatim spec
/// wording exists for this milestone - see the development plan's E.10
/// row) has four parts. Three are genuinely behavioral and covered here,
/// each through the real mechanism its own milestone task built rather
/// than a fresh one invented for this test - same reuse-only-what-exists
/// approach as D.9's own acceptance test. The fourth ("the UI runs on at
/// least one non-Windows platform in CI") isn't a unit-testable claim:
/// it's E.9's Linux CI matrix addition (`Ui.Tests`/`Editor.Tests` now run
/// there), verified against a real GitHub Actions run, not a test here.
[<Fact>]
let ``a persistent-mode session retains bindings across executions until Reset Session clears them`` () =
    task {
        let client = WorkerClient(workerDllPath ())
        let shell = ShellViewModel()
        wireShell (TimeSpan.FromSeconds(15.0)) client shell
        shell.WorkerState.TryTransition(WorkerState.Ready) |> ignore
        shell.ActiveDocument.SessionMode <- Persistent

        // 1. First execution defines a binding - a bare `let` produces no
        // expression value (Text/"null", not Scalar; see EvaluationTests
        // .fs's own characterization of this), so the meaningful
        // assertion here is that it didn't error, not its Kind.
        shell.ActiveDocument.ApplyTextChanged(1, "let x = 41")
        do! shell.RunAsync() |> withTimeout 45.0
        Assert.NotEqual(ResultKind.Error, shell.Result.Kind)
        Assert.True(shell.ActiveDocument.HasActiveSession)

        // 2. A second execution - the growing document, per E.3's
        // append-only invalidation rule - sees the earlier binding
        // without redeclaring it.
        shell.ActiveDocument.ApplyTextChanged(2, "let x = 41\nx + 1")
        do! shell.RunAsync() |> withTimeout 45.0
        Assert.Equal(ResultKind.Scalar, shell.Result.Kind)
        Assert.Equal("42", shell.Result.DisplayText)

        // 3. Reset Session drops the worker-side session and the local
        // "active" display state.
        do! shell.ResetSessionAsync() |> withTimeout 45.0
        Assert.False(shell.ActiveDocument.HasActiveSession)

        // 4. The binding is really gone, not just hidden - referencing it
        // alone (no longer a mere append of what was reset) fails against
        // the fresh session Reset started.
        shell.ActiveDocument.ApplyTextChanged(3, "x")
        do! shell.RunAsync() |> withTimeout 45.0
        Assert.Equal(ResultKind.Error, shell.Result.Kind)
    }

/// Reuses E.4's `NotebookCells.parse` and `SessionStore.appendFragment`
/// exactly as `NotebookExecutionTests.fs` does - E.4 deliberately never
/// wired cell-by-cell execution into the worker/protocol layer (only
/// `Handlers.runEvaluate`'s whole-document `getOrCreate` policy exists
/// there; sending each cell as its own `execution/evaluate` call would
/// hit E.3's append-only invalidation rule and wrongly drop earlier
/// cells' bindings), so this stays at the same in-process
/// `SessionStore`/`Evaluation` layer E.4 actually built and proved.
[<Fact>]
let ``a notebook script runs cell-by-cell, later cells seeing earlier cells' bindings`` () =
    task {
        let workDir = tempDir "fqs-milestone-e-notebook"
        let key = "milestone-e-notebook-" + Guid.NewGuid().ToString("N")
        let script = "let x = 41\n// %%\nx + 1\n// %%\nx * 2"
        let cells = NotebookCells.parse script
        Assert.Equal(3, cells.Length)

        try
            let mutable lastOutcome = Unchecked.defaultof<Evaluation.EvaluationOutcome>

            for cell in cells do
                let run =
                    task {
                        let session = SessionStore.appendFragment key workDir cell.Text
                        return Evaluation.evaluate session cell.Text
                    }

                let! outcome = run |> withTimeout 60.0
                lastOutcome <- outcome

            // The last cell ("x * 2") only resolves because it shares a
            // session with the first cell's "let x = 41" - a fresh
            // session would leave `x` undefined.
            Assert.True(lastOutcome.Exception.IsNone)

            match lastOutcome.Value with
            | Some fsiValue -> Assert.Equal(box 82, fsiValue.ReflectionValue)
            | None -> failwith "expected a value, got none"
        finally
            SessionStore.reset key
            cleanupWorkingDirectory workDir
    }

/// E.5's `Storage.FsProj`/`Analysis.AnalysisContext.buildForProject`, run
/// against real files on disk end to end: a project file listing two
/// real `.fs` files, one referencing the other, opened and type-checked
/// successfully purely because of `.fsproj` compile order.
[<Fact>]
let ``a multi-file fsproj workspace opens and analyzes correctly`` () =
    let dir = tempDir "fqs-milestone-e-multifile"

    try
        let pathA = Path.Combine(dir, "A.fs")
        let pathB = Path.Combine(dir, "B.fs")
        File.WriteAllText(pathA, "module A\nlet value = 42")
        let sourceB = "module B\nlet result = A.value + 1"
        File.WriteAllText(pathB, sourceB)

        let fsprojPath = Path.Combine(dir, "Project.fsproj")

        File.WriteAllText(
            fsprojPath,
            """<Project Sdk="Microsoft.NET.Sdk">
                 <ItemGroup>
                   <Compile Include="A.fs" />
                   <Compile Include="B.fs" />
                 </ItemGroup>
               </Project>"""
        )

        let project = FsProj.load fsprojPath
        Assert.Equal<string list>([ "A.fs"; "B.fs" ], project.Files)

        let context =
            { AnalysisContext.buildForProject dir (FsProj.resolvedFilePaths project) with
                ScriptPath = pathB }

        let _parseResults, checkAnswer =
            AnalysisContext.checker.ParseAndCheckFileInProject(
                pathB,
                0,
                FSharp.Compiler.Text.SourceText.ofString sourceB,
                context.ProjectOptions
            )
            |> Async.RunSynchronously

        match checkAnswer with
        | FSharp.Compiler.CodeAnalysis.FSharpCheckFileAnswer.Aborted -> failwith "expected the check to complete"
        | FSharp.Compiler.CodeAnalysis.FSharpCheckFileAnswer.Succeeded results ->
            Assert.Empty(results.Diagnostics)
    finally
        Directory.Delete(dir, true)
