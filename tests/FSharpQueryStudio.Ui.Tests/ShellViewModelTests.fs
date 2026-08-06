module ShellViewModelTests

open System
open System.ComponentModel
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
        vm.ExecuteAsync <- Some(fun evalParams ->
            receivedSource <- Some evalParams.Source
            Task.FromResult(canned ResultKind.Scalar "2"))

        do! vm.RunAsync()

        Assert.Equal(Some "1 + 1", receivedSource)
        Assert.Equal(ResultKind.Scalar, vm.Result.Kind)
        Assert.Equal(Some "2", vm.Result.DisplayText)
    }

[<Fact>]
let ``RunAsync sends Mode=Isolated and no SessionId when the active document's SessionMode is Isolated`` () =
    task {
        let vm = ShellViewModel()
        vm.ActiveDocument.ApplyTextChanged(1, "1 + 1")
        let mutable received = None
        vm.ExecuteAsync <- Some(fun evalParams ->
            received <- Some evalParams
            Task.FromResult(canned ResultKind.Scalar "2"))

        do! vm.RunAsync()

        match received with
        | Some p ->
            Assert.Equal(Isolated, p.Mode)
            Assert.Equal(None, p.SessionId)
        | None -> failwith "expected ExecuteAsync to have been called"
    }

[<Fact>]
let ``RunAsync sends Mode=Persistent and the active document's Uri as SessionId when SessionMode is Persistent`` () =
    task {
        let vm = ShellViewModel()
        vm.ActiveDocument.SessionMode <- Persistent
        vm.ActiveDocument.ApplyTextChanged(1, "1 + 1")
        let mutable received = None
        vm.ExecuteAsync <- Some(fun evalParams ->
            received <- Some evalParams
            Task.FromResult(canned ResultKind.Scalar "2"))

        do! vm.RunAsync()

        match received with
        | Some p ->
            Assert.Equal(Persistent, p.Mode)
            Assert.Equal(Some vm.ActiveDocument.Uri, p.SessionId)
        | None -> failwith "expected ExecuteAsync to have been called"
    }

[<Fact>]
let ``a successful RunAsync in Persistent mode marks the active document's session active`` () =
    task {
        let vm = ShellViewModel()
        vm.ActiveDocument.SessionMode <- Persistent
        vm.ActiveDocument.ApplyTextChanged(1, "1 + 1")
        vm.ExecuteAsync <- Some(fun _evalParams -> Task.FromResult(canned ResultKind.Scalar "2"))

        Assert.False(vm.ActiveDocument.HasActiveSession)
        do! vm.RunAsync()

        Assert.True(vm.ActiveDocument.HasActiveSession)
    }

[<Fact>]
let ``a successful RunAsync in Isolated mode never marks a session active`` () =
    task {
        let vm = ShellViewModel()
        vm.ActiveDocument.ApplyTextChanged(1, "1 + 1")
        vm.ExecuteAsync <- Some(fun _evalParams -> Task.FromResult(canned ResultKind.Scalar "2"))

        do! vm.RunAsync()

        Assert.False(vm.ActiveDocument.HasActiveSession)
    }

[<Fact>]
let ``ResetSessionAsync invokes ResetSession with the active document's Uri and clears HasActiveSession`` () =
    task {
        let vm = ShellViewModel()
        vm.ActiveDocument.SessionMode <- Persistent
        vm.ActiveDocument.MarkSessionActive()
        let mutable receivedSessionId = None
        vm.ResetSession <- Some(fun sessionId ->
            receivedSessionId <- Some sessionId
            Task.FromResult(()))

        do! vm.ResetSessionAsync()

        Assert.Equal(Some vm.ActiveDocument.Uri, receivedSessionId)
        Assert.False(vm.ActiveDocument.HasActiveSession)
    }

[<Fact>]
let ``ResetSessionAsync is a no-op when ResetSession has not been configured`` () =
    task {
        let vm = ShellViewModel()
        vm.ActiveDocument.SessionMode <- Persistent
        vm.ActiveDocument.MarkSessionActive()

        do! vm.ResetSessionAsync() // Must not throw.

        Assert.True(vm.ActiveDocument.HasActiveSession) // Unconfigured hook changes nothing.
    }

[<Fact>]
let ``RunAsync awaits a slow ExecuteAsync instead of blocking synchronously on it`` () =
    task {
        let vm = ShellViewModel()
        let executeSignal = TaskCompletionSource<ResultEnvelope>()
        vm.ExecuteAsync <- Some(fun _evalParams -> executeSignal.Task)

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
        vm.ExecuteAsync <- Some(fun _evalParams -> executeSignal.Task)

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
        vm.ExecuteAsync <- Some(fun _evalParams -> executeSignal.Task)
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
        vm.ExecuteAsync <- Some(fun _evalParams -> executeSignal.Task)

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
let ``OpenDocument creates a second tab without disturbing the first's state`` () =
    let vm = ShellViewModel()
    vm.ActiveDocument.ApplyTextChanged(1, "1 + 1")
    let firstDoc = vm.ActiveDocument

    let secondDoc = vm.OpenDocument("untitled:Script2.fsx")

    Assert.Equal<DocumentViewModel list>([ firstDoc; secondDoc ], vm.Documents)
    Assert.Same(secondDoc, vm.ActiveDocument)
    Assert.Equal(1, firstDoc.Version)
    Assert.Equal("1 + 1", firstDoc.Text)
    Assert.True(firstDoc.IsDirty)
    Assert.Equal(0, secondDoc.Version)
    Assert.False(secondDoc.IsDirty)

[<Fact>]
let ``OpenDocument reactivates an already-open tab instead of creating a duplicate`` () =
    let vm = ShellViewModel()
    let firstDoc = vm.ActiveDocument
    let secondDoc = vm.OpenDocument("untitled:Script2.fsx")

    let reopened = vm.OpenDocument(firstDoc.Uri)

    Assert.Same(firstDoc, reopened)
    Assert.Same(firstDoc, vm.ActiveDocument)
    Assert.Equal<DocumentViewModel list>([ firstDoc; secondDoc ], vm.Documents)

[<Fact>]
let ``CloseDocument removes a clean tab without prompting`` () =
    let vm = ShellViewModel()
    let firstDoc = vm.ActiveDocument
    let secondDoc = vm.OpenDocument("untitled:Script2.fsx")
    let mutable promptInvoked = false
    vm.ConfirmDiscardDirtyDocument <- Some(fun _ ->
        promptInvoked <- true
        true)

    let closed = vm.CloseDocument(secondDoc.Uri)

    Assert.True(closed)
    Assert.False(promptInvoked)
    Assert.Equal<DocumentViewModel list>([ firstDoc ], vm.Documents)

[<Fact>]
let ``CloseDocument prompts before discarding a dirty tab`` () =
    let vm = ShellViewModel()
    let firstDoc = vm.ActiveDocument
    firstDoc.ApplyTextChanged(1, "unsaved")
    let mutable promptedDoc = None
    vm.ConfirmDiscardDirtyDocument <- Some(fun doc ->
        promptedDoc <- Some doc
        true)

    let closed = vm.CloseDocument(firstDoc.Uri)

    Assert.True(closed)
    Assert.Equal(Some firstDoc, promptedDoc)
    Assert.DoesNotContain(firstDoc, vm.Documents)

[<Fact>]
let ``CloseDocument keeps a dirty tab open when the prompt declines`` () =
    let vm = ShellViewModel()
    let firstDoc = vm.ActiveDocument
    firstDoc.ApplyTextChanged(1, "unsaved")
    vm.ConfirmDiscardDirtyDocument <- Some(fun _ -> false)

    let closed = vm.CloseDocument(firstDoc.Uri)

    Assert.False(closed)
    Assert.Contains(firstDoc, vm.Documents)

[<Fact>]
let ``CloseDocument keeps a dirty tab open when no prompt handler is configured`` () =
    let vm = ShellViewModel()
    let firstDoc = vm.ActiveDocument
    firstDoc.ApplyTextChanged(1, "unsaved")

    let closed = vm.CloseDocument(firstDoc.Uri)

    Assert.False(closed)
    Assert.Contains(firstDoc, vm.Documents)

[<Fact>]
let ``closing the last remaining document leaves a fresh document active`` () =
    let vm = ShellViewModel()
    let firstDoc = vm.ActiveDocument

    let closed = vm.CloseDocument(firstDoc.Uri)

    Assert.True(closed)
    Assert.Single(vm.Documents) |> ignore
    Assert.NotSame(firstDoc, vm.ActiveDocument)
    Assert.False(vm.ActiveDocument.IsDirty)

[<Fact>]
let ``a document opened via OpenDocument also gets debounced CheckAsync wiring`` () =
    let scheduler = FakeDebounceScheduler()
    let vm = ShellViewModel(scheduler)
    let secondDoc = vm.OpenDocument("untitled:Script2.fsx")

    let diagnostic: Diagnostic =
        { Message = "type error"
          Severity = DiagnosticSeverity.Error
          StartLine = 0
          StartColumn = 0
          EndLine = 0
          EndColumn = 3
          Code = None }

    vm.CheckAsync <- Some(fun (_version, _source) -> Task.FromResult([ diagnostic ]))

    secondDoc.ApplyTextChanged(1, "bad")
    scheduler.Advance(TimeSpan.FromMilliseconds(500.0))

    Assert.Equal<Diagnostic list>([ diagnostic ], secondDoc.Diagnostics)

[<Fact>]
let ``CopyResultAsync sends the Result's ClipboardText through the configured hook`` () =
    task {
        let vm = ShellViewModel()
        vm.Result.Apply(canned ResultKind.Scalar "2")
        let mutable copied = None
        vm.CopyResultToClipboard <- Some(fun text -> copied <- Some text; Task.FromResult(()))

        do! vm.CopyResultAsync()

        Assert.Equal(Some "2", copied)
    }

[<Fact>]
let ``CopyResultAsync is a no-op when CopyResultToClipboard has not been configured`` () =
    task {
        let vm = ShellViewModel()
        vm.Result.Apply(canned ResultKind.Scalar "2")

        do! vm.CopyResultAsync() // Must not throw.
    }

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

[<Fact>]
let ``PropertyChanged fires for ActiveDocument when OpenDocument switches to a different tab`` () =
    let vm = ShellViewModel()
    let firstUri = vm.ActiveDocument.Uri
    let mutable raisedFor: string option = None
    (vm :> INotifyPropertyChanged).PropertyChanged.Add(fun args -> raisedFor <- Some args.PropertyName)

    vm.OpenDocument("untitled:Script2.fsx") |> ignore

    Assert.Equal(Some "ActiveDocument", raisedFor)
    Assert.NotEqual<string>(firstUri, vm.ActiveDocument.Uri)

[<Fact>]
let ``PropertyChanged does not fire for ActiveDocument when OpenDocument re-opens the already-active tab`` () =
    let vm = ShellViewModel()
    let mutable raised = false
    (vm :> INotifyPropertyChanged).PropertyChanged.Add(fun _ -> raised <- true)

    vm.OpenDocument(vm.ActiveDocument.Uri) |> ignore

    Assert.False(raised)
