module Tests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Execution

let private withTimeout (seconds: float) (work: Task<'T>) : Task<'T> =
    task {
        let! winner = Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds seconds))

        if winner = (work :> Task) then
            return! work
        else
            return failwith $"timed out after {seconds}s"
    }

let private tempWorkingDirectory () =
    let dir = Path.Combine(Path.GetTempPath(), "fqs-fsi-tests-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    dir

/// `FsiSession.create` calls Directory.SetCurrentDirectory, which on
/// Windows locks that directory against deletion for as long as it's the
/// process's current directory. Tests must point the current directory
/// elsewhere before cleaning up their temp directory.
let private cleanupWorkingDirectory (dir: string) =
    Directory.SetCurrentDirectory(Path.GetTempPath())
    Directory.Delete(dir, true)

[<Fact>]
let ``create sets the current directory to the one requested`` () =
    let workDir = tempWorkingDirectory ()

    try
        FsiSession.create workDir |> ignore
        Assert.Equal(Path.GetFullPath(workDir), Path.GetFullPath(Environment.CurrentDirectory))
    finally
        cleanupWorkingDirectory workDir

[<Fact>]
let ``create returns a session that evaluates a trivial expression without hanging on stdin`` () =
    task {
        let workDir = tempWorkingDirectory ()

        try
            let work =
                task {
                    let session = FsiSession.create workDir
                    return session.Fsi.EvalInteractionNonThrowing("1 + 1")
                }

            let! (result, diagnostics) = work |> withTimeout 30.0

            match result with
            | Choice1Of2(Some _) -> ()
            | Choice1Of2 None -> failwith "expected a value, got none"
            | Choice2Of2 ex -> failwithf "evaluation failed: %O" ex

            Assert.Empty(diagnostics)
        finally
            cleanupWorkingDirectory workDir
    }
