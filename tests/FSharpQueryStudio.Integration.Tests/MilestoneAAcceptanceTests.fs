module MilestoneAAcceptanceTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.App
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Ui
open TestHelpers

/// Milestone A's acceptance criterion (architecture spec): "A user can
/// open a standalone .fsx script, execute it in isolation, read
/// output/results, cancel it, and recover from a worker crash without
/// restarting the UI." One continuous `ShellViewModel`/`WorkerClient` pair
/// for the whole test - restarting either between steps would be exactly
/// the "restarting the UI" the criterion rules out.
[<Fact>]
let ``open, run, read output/results, cancel, and recover from a crash - all without restarting the UI`` () =
    task {
        let client = WorkerClient(workerDllPath ())
        let shell = ShellViewModel()
        wireShell (TimeSpan.FromSeconds(15.0)) client shell
        shell.WorkerState.TryTransition(WorkerState.Ready) |> ignore

        // 1. Open a standalone .fsx script - a real file on disk, read the
        // same way the App's eventual "File > Open" command would.
        let scriptPath = Path.Combine(Path.GetTempPath(), $"fqs-acceptance-{Guid.NewGuid():N}.fsx")
        File.WriteAllText(scriptPath, "printfn \"hello from the acceptance script\"\n21 * 2")

        try
            let scriptText = File.ReadAllText(scriptPath)
            shell.ActiveDocument.ApplyTextChanged(1, scriptText)

            // 2 & 3. Execute in isolation; read output/results.
            do! shell.RunAsync() |> withTimeout 15.0

            Assert.Equal(ResultKind.Scalar, shell.Result.Kind)
            Assert.Equal(Some "42", shell.Result.DisplayText)

            Assert.Contains(
                shell.Result.Output,
                fun (line: OutputLine) -> line.Text.Contains("hello from the acceptance script")
            )

            Assert.Equal(WorkerState.Ready, shell.WorkerState.Current)

            // 4. Cancel it - a cooperative long-running script, stopped
            // early through the same UI command as A.17.
            let cooperativeScript =
                "let mutable i = 0 in (while i < 2_000_000_000 && not (System.IO.File.Exists(\".cancel-requested\")) do i <- i + 1); i"

            shell.ActiveDocument.ApplyTextChanged(2, cooperativeScript)

            let runTask = shell.RunAsync()
            do! Task.Delay(200)
            Assert.Equal(WorkerState.Running, shell.WorkerState.Current)

            do! shell.Cancel() |> withTimeout 20.0
            do! runTask |> withTimeout 20.0

            Assert.Equal(ResultKind.Scalar, shell.Result.Kind)

            match shell.Result.DisplayText with
            | Some text -> Assert.True(int text < 2_000_000_000, $"expected the loop to stop early, got {text}")
            | None -> failwith "expected a display value"

            Assert.Equal(WorkerState.Ready, shell.WorkerState.Current)

            // 5. Recover from a worker crash - kill the process out from
            // under an in-flight request, then keep using the *same*
            // shell/client: the next run must still work, against a
            // freshly restarted worker (A.8's host-side ownership).
            shell.ActiveDocument.ApplyTextChanged(3, "while true do ()")
            let crashedRunTask = shell.RunAsync()
            do! Task.Delay(200)

            match client.CurrentProcess with
            | Some p -> p.Kill()
            | None -> failwith "expected a running worker process to kill"

            do! crashedRunTask |> withTimeout 15.0
            Assert.Equal(ResultKind.Error, shell.Result.Kind)

            shell.ActiveDocument.ApplyTextChanged(4, "1 + 1")
            do! shell.RunAsync() |> withTimeout 15.0

            Assert.Equal(ResultKind.Scalar, shell.Result.Kind)
            Assert.Equal(Some "2", shell.Result.DisplayText)
        finally
            File.Delete(scriptPath)
    }
