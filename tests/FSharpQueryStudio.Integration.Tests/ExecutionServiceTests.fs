module ExecutionServiceTests

open System
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.App
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Ui
open TestHelpers

/// Wires `ExecuteAsync`/`CancelAsync` the way the App composition root
/// does: `CancelAsync` closes over whatever request id the most recent
/// `ExecuteAsync` call sent, so `shell.Cancel()` cancels the evaluation
/// actually in flight without `ShellViewModel` itself knowing request ids
/// exist.
let private wireShell (gracePeriod: TimeSpan) (client: WorkerClient) (shell: ShellViewModel) =
    let currentRequestId = ref None

    shell.ExecuteAsync <-
        Some(fun source ->
            task {
                let id = Guid.NewGuid().ToString()
                currentRequestId.Value <- Some id
                let! result = ExecutionService.evaluateWithId client id source
                currentRequestId.Value <- None
                return result
            })

    shell.CancelAsync <-
        Some(fun () ->
            task {
                match currentRequestId.Value with
                | Some id -> do! client.Cancel(id, gracePeriod)
                | None -> ()
            })

[<Fact>]
let ``Run (F5) end to end: ShellViewModel -> ExecutionService -> real worker -> ResultViewModel`` () =
    task {
        let client = WorkerClient(workerDllPath ())
        let shell = ShellViewModel()
        shell.ExecuteAsync <- Some(ExecutionService.evaluate client)
        shell.ActiveDocument.ApplyTextChanged(1, "1 + 1")

        do! shell.RunAsync() |> withTimeout 15.0

        Assert.Equal(ResultKind.Scalar, shell.Result.Kind)
        Assert.Equal(Some "2", shell.Result.DisplayText)
    }

[<Fact>]
let ``Run end to end surfaces a worker-level error through ResultViewModel too`` () =
    task {
        let client = WorkerClient(workerDllPath ())
        let shell = ShellViewModel()
        shell.ExecuteAsync <- Some(ExecutionService.evaluate client)
        // A concretely-typed throw - see EvaluationTests' characterization
        // of the bare-failwith value-restriction trap in Execution.Tests.
        shell.ActiveDocument.ApplyTextChanged(1, "(failwith \"boom\" : int)")

        do! shell.RunAsync() |> withTimeout 15.0

        Assert.Equal(ResultKind.Error, shell.Result.Kind)
        Assert.Equal(Some "boom", shell.Result.DisplayText)
    }

[<Fact>]
let ``Cancel (through the UI command) lets a cooperative script stop early, worker still Ready afterward`` () =
    task {
        let client = WorkerClient(workerDllPath ())
        let shell = ShellViewModel()
        wireShell (TimeSpan.FromSeconds(15.0)) client shell
        shell.WorkerState.TryTransition(WorkerState.Ready) |> ignore

        let script =
            "let mutable i = 0 in (while i < 2_000_000_000 && not (System.IO.File.Exists(\".cancel-requested\")) do i <- i + 1); i"

        shell.ActiveDocument.ApplyTextChanged(1, script)

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
    }

[<Fact>]
let ``Cancel (through the UI command) forces termination of a non-cooperative script`` () =
    task {
        let client = WorkerClient(workerDllPath ())
        let shell = ShellViewModel()
        // A short grace period so the test doesn't wait 15s for the kill.
        wireShell (TimeSpan.FromMilliseconds(500.0)) client shell
        shell.WorkerState.TryTransition(WorkerState.Ready) |> ignore

        shell.ActiveDocument.ApplyTextChanged(1, "while true do ()")

        let runTask = shell.RunAsync()
        do! Task.Delay(200)
        do! shell.Cancel() |> withTimeout 20.0
        do! runTask |> withTimeout 20.0

        Assert.Equal(ResultKind.Error, shell.Result.Kind)
        Assert.Equal(WorkerState.Ready, shell.WorkerState.Current)
    }
