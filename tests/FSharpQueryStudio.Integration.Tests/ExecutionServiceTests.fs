module ExecutionServiceTests

open Xunit
open FSharpQueryStudio.App
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Ui
open TestHelpers

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
