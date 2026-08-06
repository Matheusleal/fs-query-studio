module ShellViewModelTests

open System
open System.Text.Json
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Ui
open FSharpQueryStudio.Ui.Tests

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

[<Fact>]
let ``RunAsync moves WorkerState Ready -> Running -> Ready around a successful execution`` () =
    task {
        let vm = ShellViewModel()
        vm.WorkerState.TryTransition(WorkerState.Ready) |> ignore
        let executeSignal = TaskCompletionSource<ResultEnvelope>()
        vm.ExecuteAsync <- Some(fun _source -> executeSignal.Task)

        let runTask = vm.RunAsync()
        Assert.Equal(WorkerState.Running, vm.WorkerState.Current)

        executeSignal.SetResult(canned ResultKind.Scalar "2")
        do! runTask

        Assert.Equal(WorkerState.Ready, vm.WorkerState.Current)
    }

[<Fact>]
let ``Cancel invokes CancelAsync and moves WorkerState through Cancelling while Running`` () =
    task {
        let vm = ShellViewModel()
        vm.WorkerState.TryTransition(WorkerState.Ready) |> ignore
        let executeSignal = TaskCompletionSource<ResultEnvelope>()
        vm.ExecuteAsync <- Some(fun _source -> executeSignal.Task)
        let mutable cancelInvoked = false
        vm.CancelAsync <- Some(fun () ->
            cancelInvoked <- true
            Task.FromResult(()))

        let runTask = vm.RunAsync()
        do! vm.Cancel()

        Assert.True(cancelInvoked)
        Assert.Equal(WorkerState.Cancelling, vm.WorkerState.Current)

        executeSignal.SetResult(canned ResultKind.Error "cancelled")
        do! runTask
    }

[<Fact>]
let ``Cancel is a no-op when the worker is not Running`` () =
    task {
        let vm = ShellViewModel()
        let mutable cancelInvoked = false
        vm.CancelAsync <- Some(fun () ->
            cancelInvoked <- true
            Task.FromResult(()))

        do! vm.Cancel()

        Assert.False(cancelInvoked)
        Assert.Equal(WorkerState.Starting, vm.WorkerState.Current)
    }

[<Fact>]
let ``Cancel is a no-op when CancelAsync has not been configured`` () =
    task {
        let vm = ShellViewModel()
        vm.WorkerState.TryTransition(WorkerState.Ready) |> ignore
        let executeSignal = TaskCompletionSource<ResultEnvelope>()
        vm.ExecuteAsync <- Some(fun _source -> executeSignal.Task)

        let runTask = vm.RunAsync()
        do! vm.Cancel() // No CancelAsync configured - must not throw or change state.

        Assert.Equal(WorkerState.Running, vm.WorkerState.Current)

        executeSignal.SetResult(canned ResultKind.Scalar "2")
        do! runTask
    }

[<Fact>]
let ``Problems reflects the active document's current diagnostics`` () =
    let vm = ShellViewModel()
    vm.ActiveDocument.ApplyTextChanged(1, "let x : int = \"oops\"")

    let diagnostic: Diagnostic =
        { Message = "type error"
          Severity = DiagnosticSeverity.Error
          StartLine = 0
          StartColumn = 14
          EndLine = 0
          EndColumn = 20
          Code = Some "FS0001" }

    vm.ActiveDocument.ApplyAnalysisResult(1, [ diagnostic ])

    Assert.Equal<Diagnostic list>([ diagnostic ], vm.Problems |> List.map (fun p -> p.Diagnostic))

[<Fact>]
let ``invoking a problem's Navigate command sends the diagnostic's start position as a caret-move bridge message`` () =
    let vm = ShellViewModel()
    vm.ActiveDocument.ApplyTextChanged(1, "let x : int = \"oops\"")

    let diagnostic: Diagnostic =
        { Message = "type error"
          Severity = DiagnosticSeverity.Error
          StartLine = 0
          StartColumn = 14
          EndLine = 0
          EndColumn = 20
          Code = Some "FS0001" }

    vm.ActiveDocument.ApplyAnalysisResult(1, [ diagnostic ])

    let mutable sentEnvelope: EditorEnvelope option = None
    vm.SendToEditor <- Some(fun envelope -> sentEnvelope <- Some envelope)

    (List.head vm.Problems).Navigate()

    match sentEnvelope with
    | None -> failwith "expected a bridge message to be sent"
    | Some envelope ->
        Assert.Equal("caretMoved", envelope.Kind)
        let payload = JsonSerializer.Deserialize<CaretMovedPayload>(envelope.Payload, Serialization.options)
        Assert.Equal(vm.ActiveDocument.Uri, payload.DocumentUri)
        Assert.Equal(0, payload.Line)
        Assert.Equal(14, payload.Column)

[<Fact>]
let ``Navigate is a no-op when SendToEditor has not been configured`` () =
    let vm = ShellViewModel()
    vm.ActiveDocument.ApplyTextChanged(1, "let x : int = \"oops\"")

    let diagnostic: Diagnostic =
        { Message = "type error"
          Severity = DiagnosticSeverity.Error
          StartLine = 0
          StartColumn = 14
          EndLine = 0
          EndColumn = 20
          Code = None }

    vm.ActiveDocument.ApplyAnalysisResult(1, [ diagnostic ])

    (List.head vm.Problems).Navigate() // Must not throw.

[<Fact>]
let ``a debounced text change calls CheckAsync and applies the result through the version guard`` () =
    let scheduler = FakeDebounceScheduler()
    let vm = ShellViewModel(scheduler)

    let diagnostic: Diagnostic =
        { Message = "type error"
          Severity = DiagnosticSeverity.Error
          StartLine = 0
          StartColumn = 14
          EndLine = 0
          EndColumn = 20
          Code = None }

    let mutable receivedVersion = 0
    let mutable receivedSource = ""

    vm.CheckAsync <-
        Some(fun (version, source) ->
            receivedVersion <- version
            receivedSource <- source
            Task.FromResult([ diagnostic ]))

    vm.ActiveDocument.ApplyTextChanged(1, "let x : int = \"oops\"")
    scheduler.Advance(TimeSpan.FromMilliseconds(500.0))

    Assert.Equal(1, receivedVersion)
    Assert.Equal("let x : int = \"oops\"", receivedSource)
    Assert.Equal<Diagnostic list>([ diagnostic ], vm.ActiveDocument.Diagnostics)
