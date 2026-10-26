module NotebookExecutionTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Execution
open TestHelpers

/// `FsiSession.create` calls Directory.SetCurrentDirectory, which on
/// Windows locks that directory against deletion for as long as it's the
/// process's current directory - matches Execution.Tests/TestHelpers.fs's
/// own helper, duplicated here rather than shared across test projects.
let private tempWorkingDirectory () =
    let dir = Path.Combine(Path.GetTempPath(), "fqs-notebook-tests-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    dir

let private cleanupWorkingDirectory (dir: string) =
    Directory.SetCurrentDirectory(Path.GetTempPath())
    Directory.Delete(dir, true)

/// Cell 1 defines `x`; cell 2 references it without redeclaring it - only
/// resolvable if the cell that runs it shares a live session with cell 1.
let private notebookScript = "let x = 41\n// %%\nx + 1"

/// E.4's acceptance shape, integrating `NotebookCells.parse`
/// with `SessionStore.appendFragment`: real cell-boundary
/// parsing feeding real ordered, incremental FSI execution - not a stand-
/// in for either piece tested alone (see `NotebookCellsTests.fs` for the
/// pure parser and `SessionStoreTests.fs` for the pure store behavior).
[<Fact>]
let ``notebook/persistent session: later cells see earlier cells' bindings`` () =
    task {
        let workDir = tempWorkingDirectory ()
        let key = "notebook-" + Guid.NewGuid().ToString("N")
        let cells = NotebookCells.parse notebookScript
        Assert.Equal(2, cells.Length)

        try
            let mutable lastOutcome = Unchecked.defaultof<Evaluation.EvaluationOutcome>

            for cell in cells do
                let run =
                    task {
                        let session = SessionStore.appendFragment key workDir cell.Text
                        return Evaluation.evaluate session cell.Text
                    }

                let! outcome = run |> withTimeout 45.0
                lastOutcome <- outcome

            Assert.True(lastOutcome.Exception.IsNone)

            match lastOutcome.Value with
            | Some fsiValue -> Assert.Equal(box 42, fsiValue.ReflectionValue)
            | None -> failwith "expected a value, got none"
        finally
            SessionStore.reset key
            cleanupWorkingDirectory workDir
    }

/// Paired with the case above: same script, same cell-by-cell execution
/// shape, but each cell gets its own fresh session (Isolated) instead of
/// sharing one via `SessionStore` - proving "later cells see earlier
/// cells' bindings" is actually conditioned on the persistent/notebook
/// session, not an accident of how the script happens to be written.
[<Fact>]
let ``isolated per-cell execution: later cells do not see earlier cells' bindings`` () =
    task {
        let workDir = tempWorkingDirectory ()
        let cells = NotebookCells.parse notebookScript
        Assert.Equal(2, cells.Length)

        try
            let mutable lastOutcome = Unchecked.defaultof<Evaluation.EvaluationOutcome>

            for cell in cells do
                let run =
                    task {
                        let session = FsiSession.create workDir
                        return Evaluation.evaluate session cell.Text
                    }

                let! outcome = run |> withTimeout 45.0
                lastOutcome <- outcome

            Assert.True(lastOutcome.Exception.IsSome, "expected 'x' to be undefined in a fresh, unrelated session")
            Assert.NotEmpty(lastOutcome.Diagnostics)
        finally
            cleanupWorkingDirectory workDir
    }
