module ShellViewModelTests

open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Ui

[<Fact>]
let ``exposes a WorkerState starting in the Starting state`` () =
    let vm = ShellViewModel()
    Assert.Equal(WorkerState.Starting, vm.WorkerState.Current)

[<Fact>]
let ``exposes an ActiveDocument for the vertical slice's single editor tab`` () =
    let vm = ShellViewModel()
    Assert.Equal(0, vm.ActiveDocument.Version)
    Assert.False(vm.ActiveDocument.IsDirty)

let private canned (kind: ResultKind) (displayText: string) : ResultEnvelope =
    { Kind = kind
      DisplayText = Some displayText
      Schema = []
      Rows = [||]
      IsTruncated = false
      ContinuationToken = None
      ElapsedMs = 3L
      Output = [] }

[<Fact>]
let ``RunAsync calls ExecuteAsync with the active document's text and applies the result`` () =
    task {
        let vm = ShellViewModel()
        vm.ActiveDocument.ApplyTextChanged(1, "1 + 1")
        let mutable receivedSource = None
        vm.ExecuteAsync <- Some(fun source ->
            receivedSource <- Some source
            Task.FromResult(canned ResultKind.Scalar "2"))

        do! vm.RunAsync()

        Assert.Equal(Some "1 + 1", receivedSource)
        Assert.Equal(ResultKind.Scalar, vm.Result.Kind)
        Assert.Equal(Some "2", vm.Result.DisplayText)
    }

[<Fact>]
let ``RunAsync awaits a slow ExecuteAsync instead of blocking synchronously on it`` () =
    task {
        let vm = ShellViewModel()
        let executeSignal = TaskCompletionSource<ResultEnvelope>()
        vm.ExecuteAsync <- Some(fun _source -> executeSignal.Task)

        // A blocking implementation would never return control here until
        // ExecuteAsync's task completed - proving the call returns while
        // ExecuteAsync is still pending demonstrates the await doesn't
        // stall whatever thread called RunAsync (the UI thread, in the
        // real app), regardless of how slow/large the eventual result is.
        let runTask = vm.RunAsync()
        Assert.False(runTask.IsCompleted)

        executeSignal.SetResult(canned ResultKind.Table "done")
        do! runTask

        Assert.Equal(ResultKind.Table, vm.Result.Kind)
    }

[<Fact>]
let ``RunAsync is a no-op when ExecuteAsync has not been configured`` () =
    task {
        let vm = ShellViewModel()

        do! vm.RunAsync()

        Assert.Equal(ResultKind.NoValue, vm.Result.Kind)
    }
