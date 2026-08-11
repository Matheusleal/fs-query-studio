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

// A bridge `textChanged` names the document it came from. Applying it to
// `ActiveDocument` instead was harmless while there was only ever one tab;
// with several open, an edit still in flight when the user switches tabs
// would land on the wrong document.

[<Fact>]
let ``RouteTextChanged applies the change to the document the message names`` () =
    let vm = ShellViewModel()
    let firstDoc = vm.ActiveDocument
    let secondDoc = vm.OpenDocument("untitled:Script2.fsx")

    let untouchedSeedText = secondDoc.Text
    vm.RouteTextChanged(firstDoc.Uri, 1, "typed into the first tab")

    Assert.Equal("typed into the first tab", firstDoc.Text)
    Assert.Equal(untouchedSeedText, secondDoc.Text)

[<Fact>]
let ``RouteTextChanged does not touch the active tab when the message names a background one`` () =
    let vm = ShellViewModel()
    let backgroundDoc = vm.ActiveDocument
    let activeDoc = vm.OpenDocument("untitled:Script2.fsx")

    // The exact race the URI routing exists for: an edit for the tab the
    // user just left arrives after the switch. The active tab's version
    // counter is its own, so a plain `newVersion > version` guard would
    // happily accept this into the wrong document.
    Assert.Same(activeDoc, vm.ActiveDocument)
    let untouchedSeedText = activeDoc.Text
    vm.RouteTextChanged(backgroundDoc.Uri, 1, "belongs to the background tab")

    Assert.Equal("belongs to the background tab", backgroundDoc.Text)
    Assert.Equal(untouchedSeedText, activeDoc.Text)
    Assert.False(activeDoc.IsDirty)

[<Fact>]
let ``RouteTextChanged drops a message for a uri that is not open`` () =
    let vm = ShellViewModel()
    let onlyDoc = vm.ActiveDocument
    let untouchedSeedText = onlyDoc.Text

    vm.RouteTextChanged("untitled:ClosedWhileTyping.fsx", 1, "orphaned") // Must not throw.

    Assert.Equal(untouchedSeedText, onlyDoc.Text)

// Saving an `untitled:` tab retags its `Uri`, but the editor page keeps its
// own copy of that URI and stamps every `textChanged` with it. Since
// `RouteTextChanged` above matches strictly on the URI, a retag the page
// never hears about turns every later keystroke into an unroutable message.

[<Fact>]
let ``MarkDocumentSaved re-keys the editor page when the save retagged the document uri`` () =
    let vm = ShellViewModel()
    let doc = vm.ActiveDocument
    doc.ApplyTextChanged(1, "let x = 1")

    let sent = ResizeArray<EditorEnvelope>()
    vm.SendToEditor <- Some(fun envelope -> sent.Add envelope)

    vm.MarkDocumentSaved(doc, Some "C:/scripts/saved.fsx")

    Assert.Equal("C:/scripts/saved.fsx", doc.Uri)
    Assert.False(doc.IsDirty)
    Assert.Equal(1, sent.Count)
    Assert.Equal("documentLoaded", sent.[0].Kind)

    let payload =
        JsonSerializer.Deserialize<DocumentLoadedPayload>(sent.[0].Payload, Serialization.options)

    Assert.Equal("C:/scripts/saved.fsx", payload.DocumentUri)
    Assert.Equal(1, payload.Version)
    Assert.Equal("let x = 1", payload.Text)

[<Fact>]
let ``edits typed after saving an untitled tab still reach the document`` () =
    // The whole point of the push: the page re-keys onto the saved path, so
    // the next keystroke is stamped with a URI `RouteTextChanged` can find.
    // Without it the message carried `untitled:Script1.fsx`, matched nothing,
    // and the edit vanished - the following Save then wrote the pre-save text.
    let vm = ShellViewModel()
    let doc = vm.ActiveDocument
    doc.ApplyTextChanged(1, "before the save")

    let mutable pageUri = doc.Uri

    vm.SendToEditor <-
        Some(fun envelope ->
            if envelope.Kind = "documentLoaded" then
                pageUri <-
                    JsonSerializer
                        .Deserialize<DocumentLoadedPayload>(envelope.Payload, Serialization.options)
                        .DocumentUri)

    vm.MarkDocumentSaved(doc, Some "C:/scripts/saved.fsx")
    vm.RouteTextChanged(pageUri, 2, "typed after the save")

    Assert.Equal("typed after the save", doc.Text)
    Assert.True(doc.IsDirty)

[<Fact>]
let ``MarkDocumentSaved does not re-push when the save left the uri unchanged`` () =
    // `documentLoaded` calls Monaco's `setValue`, which throws away the undo
    // stack - so an ordinary re-save of a file-backed tab must stay silent.
    let vm = ShellViewModel()
    let doc = vm.OpenDocument("C:/scripts/saved.fsx")
    doc.ApplyTextChanged(1, "let x = 1")

    let sent = ResizeArray<EditorEnvelope>()
    vm.SendToEditor <- Some(fun envelope -> sent.Add envelope)

    vm.MarkDocumentSaved(doc, None)
    vm.MarkDocumentSaved(doc, Some "C:/scripts/saved.fsx")

    Assert.False(doc.IsDirty)
    Assert.Empty(sent)

[<Fact>]
let ``MarkDocumentSaved does not re-key the editor page for a tab that is no longer active`` () =
    // The save handlers await a file picker, so the user can switch tabs
    // before the write lands. Re-keying then would point the page at a
    // document it isn't showing and overwrite the visible tab's text.
    let vm = ShellViewModel()
    let saved = vm.ActiveDocument
    saved.ApplyTextChanged(1, "belongs to the first tab")
    let other = vm.OpenDocument("untitled:Script2.fsx")
    Assert.Same(other, vm.ActiveDocument)

    let sent = ResizeArray<EditorEnvelope>()
    vm.SendToEditor <- Some(fun envelope -> sent.Add envelope)

    vm.MarkDocumentSaved(saved, Some "C:/scripts/saved.fsx")

    Assert.Equal("C:/scripts/saved.fsx", saved.Uri)
    Assert.False(saved.IsDirty)
    Assert.Empty(sent)

[<Fact>]
let ``MarkDocumentSaved still marks the document clean with no editor hook configured`` () =
    let vm = ShellViewModel()
    let doc = vm.ActiveDocument
    doc.ApplyTextChanged(1, "let x = 1")

    vm.MarkDocumentSaved(doc, Some "C:/scripts/saved.fsx") // Must not throw.

    Assert.Equal("C:/scripts/saved.fsx", doc.Uri)
    Assert.False(doc.IsDirty)

[<Fact>]
let ``NextUntitledUri starts at Script2 when only the initial Script1 is open`` () =
    let vm = ShellViewModel()

    Assert.Equal("untitled:Script2.fsx", vm.NextUntitledUri())

[<Fact>]
let ``NextUntitledUri fills a gap left by a closed tab instead of colliding with a live one`` () =
    let vm = ShellViewModel()
    vm.OpenDocument("untitled:Script2.fsx") |> ignore
    vm.OpenDocument("untitled:Script3.fsx") |> ignore

    // Three open, close the middle one: a `Documents.Length + 1` scheme
    // would now propose `Script3`, which is still open - and the caller's
    // open-or-activate dedupe would silently re-activate it rather than
    // creating anything.
    Assert.True(vm.CloseDocument("untitled:Script2.fsx"))

    Assert.Equal("untitled:Script2.fsx", vm.NextUntitledUri())

[<Fact>]
let ``NextUntitledUri never proposes a uri that is already open`` () =
    let vm = ShellViewModel()
    vm.OpenDocument("untitled:Script2.fsx") |> ignore
    vm.OpenDocument("untitled:Script3.fsx") |> ignore

    let proposed = vm.NextUntitledUri()

    Assert.DoesNotContain(proposed, vm.Documents |> List.map (fun d -> d.Uri))

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
    let raisedFor = ResizeArray<string>()
    (vm :> INotifyPropertyChanged).PropertyChanged.Add(fun args -> raisedFor.Add(args.PropertyName))

    vm.OpenDocument("untitled:Script2.fsx") |> ignore

    Assert.Contains("ActiveDocument", raisedFor)
    Assert.NotEqual<string>(firstUri, vm.ActiveDocument.Uri)

[<Fact>]
let ``PropertyChanged does not fire for ActiveDocument when OpenDocument re-opens the already-active tab`` () =
    let vm = ShellViewModel()
    let mutable raised = false
    (vm :> INotifyPropertyChanged).PropertyChanged.Add(fun _ -> raised <- true)

    vm.OpenDocument(vm.ActiveDocument.Uri) |> ignore

    Assert.False(raised)

// `Problems` is derived from the active document's `Diagnostics`, so a
// binding on it (the Problems pane, from UI-Foundation.9 onward) only ever
// updates if `ShellViewModel` itself re-raises for both of the reasons the
// derived value can change: a different tab became active, or the active
// tab's own diagnostics changed.

[<Fact>]
let ``PropertyChanged fires for Problems when the active document's diagnostics change`` () =
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

    let raisedFor = ResizeArray<string>()
    (vm :> INotifyPropertyChanged).PropertyChanged.Add(fun args -> raisedFor.Add(args.PropertyName))

    vm.ActiveDocument.ApplyAnalysisResult(1, [ diagnostic ])

    Assert.Contains("Problems", raisedFor)
    Assert.Equal<Diagnostic list>([ diagnostic ], vm.Problems |> List.map (fun p -> p.Diagnostic))

[<Fact>]
let ``PropertyChanged does not fire for Problems when a background document's diagnostics change`` () =
    let vm = ShellViewModel()
    let backgroundUri = vm.ActiveDocument.Uri
    vm.OpenDocument("untitled:Script2.fsx") |> ignore

    let background = vm.Documents |> List.find (fun d -> d.Uri = backgroundUri)
    background.ApplyTextChanged(1, "let x : int = \"oops\"")

    let diagnostic: Diagnostic =
        { Message = "type error"
          Severity = DiagnosticSeverity.Error
          StartLine = 0
          StartColumn = 14
          EndLine = 0
          EndColumn = 20
          Code = Some "FS0001" }

    let raisedFor = ResizeArray<string>()
    (vm :> INotifyPropertyChanged).PropertyChanged.Add(fun args -> raisedFor.Add(args.PropertyName))

    background.ApplyAnalysisResult(1, [ diagnostic ])

    Assert.DoesNotContain("Problems", raisedFor)

[<Fact>]
let ``PropertyChanged fires for Problems when OpenDocument switches to a different tab`` () =
    let vm = ShellViewModel()
    let raisedFor = ResizeArray<string>()
    (vm :> INotifyPropertyChanged).PropertyChanged.Add(fun args -> raisedFor.Add(args.PropertyName))

    vm.OpenDocument("untitled:Script2.fsx") |> ignore

    Assert.Contains("Problems", raisedFor)

[<Fact>]
let ``PropertyChanged fires for Problems when CloseDocument changes which tab is active`` () =
    let vm = ShellViewModel()
    vm.OpenDocument("untitled:Script2.fsx") |> ignore

    let raisedFor = ResizeArray<string>()
    (vm :> INotifyPropertyChanged).PropertyChanged.Add(fun args -> raisedFor.Add(args.PropertyName))

    Assert.True(vm.CloseDocument("untitled:Script2.fsx"))

    Assert.Contains("Problems", raisedFor)

let private testConnection (name: string) : ConnectionDefinition =
    { Id = Guid.NewGuid()
      Name = name
      Kind = Sqlite
      Configuration = JsonDocument.Parse("""{"FilePath":"C:\\data.db"}""").RootElement
      SecretReference = None
      IsTrusted = false }

[<Fact>]
let ``HoverAsync is settable and reachable as a plain hook, no-op when unconfigured`` () =
    task {
        let vm = ShellViewModel()
        Assert.Equal(None, vm.HoverAsync)

        let mutable received = None

        vm.HoverAsync <-
            Some(fun (uri, version, source, line, column) ->
                received <- Some(uri, version, source, line, column)
                Task.FromResult(Some { DisplayName = "x"; Type = "int"; Signature = None; XmlDoc = None }))

        let! result = vm.HoverAsync.Value ("untitled:Script1.fsx", 1, "let x = 1", 1, 5)

        Assert.Equal(Some("untitled:Script1.fsx", 1, "let x = 1", 1, 5), received)
        Assert.Equal("x", result.Value.DisplayName)
    }

[<Fact>]
let ``DefinitionAsync is settable and reachable as a plain hook, no-op when unconfigured`` () =
    task {
        let vm = ShellViewModel()
        Assert.Equal(None, vm.DefinitionAsync)

        vm.DefinitionAsync <-
            Some(fun (_uri, _version, _source, _line, _column) ->
                Task.FromResult(
                    Some
                        { FilePath = "script.fsx"
                          StartLine = 0
                          StartColumn = 4
                          EndLine = 0
                          EndColumn = 5 }
                ))

        let! result = vm.DefinitionAsync.Value ("untitled:Script1.fsx", 1, "let x = 1\nx", 2, 0)

        Assert.Equal(0, result.Value.StartLine)
    }

[<Fact>]
let ``SignatureHelpAsync is settable and reachable as a plain hook, no-op when unconfigured`` () =
    task {
        let vm = ShellViewModel()
        Assert.Equal(None, vm.SignatureHelpAsync)

        let mutable received = None

        vm.SignatureHelpAsync <-
            Some(fun (uri, version, source, line, column) ->
                received <- Some(uri, version, source, line, column)

                Task.FromResult(
                    Some
                        { DisplayName = "printfn"
                          Parameters = [ "format: Printf.TextWriterFormat<'T>" ]
                          ReturnType = "'T" }
                ))

        let! result = vm.SignatureHelpAsync.Value ("untitled:Script1.fsx", 1, "printfn \"%s\"", 1, 9)

        Assert.Equal(Some("untitled:Script1.fsx", 1, "printfn \"%s\"", 1, 9), received)
        Assert.Equal("printfn", result.Value.DisplayName)
    }

[<Fact>]
let ``CompleteAsync is settable and reachable as a plain hook, no-op when unconfigured`` () =
    task {
        let vm = ShellViewModel()
        Assert.Equal(None, vm.CompleteAsync)

        let mutable received = None

        vm.CompleteAsync <-
            Some(fun (uri, version, source, line, column) ->
                received <- Some(uri, version, source, line, column)
                Task.FromResult([ "let"; "Connections.sql" ]))

        let! result = vm.CompleteAsync.Value ("untitled:Script1.fsx", 1, "myLoc", 1, 5)

        Assert.Equal(Some("untitled:Script1.fsx", 1, "myLoc", 1, 5), received)
        Assert.Contains("Connections.sql", result)
    }

[<Fact>]
let ``a freshly-constructed ShellViewModel has no registered connections`` () =
    let vm = ShellViewModel()
    Assert.Empty(vm.Connections)

[<Fact>]
let ``RegisterConnection calls the configured hook and stores the returned list`` () =
    task {
        let vm = ShellViewModel()
        let connection = testConnection "Sales"
        let mutable received = None

        vm.RegisterConnectionAsync <-
            Some(fun (def, secret) ->
                received <- Some(def, secret)
                Task.FromResult([ def ]))

        do! vm.RegisterConnection connection (Some "s3cret")

        Assert.Equal(Some(connection, Some "s3cret"), received)
        Assert.Equal<ConnectionDefinition list>([ connection ], vm.Connections)
    }

[<Fact>]
let ``RegisterConnection is a no-op when RegisterConnectionAsync has not been configured`` () =
    task {
        let vm = ShellViewModel()
        let connection = testConnection "Sales"

        do! vm.RegisterConnection connection None // Must not throw.

        Assert.Empty(vm.Connections)
    }

[<Fact>]
let ``PropertyChanged fires for Connections after a successful RegisterConnection`` () =
    task {
        let vm = ShellViewModel()
        let connection = testConnection "Sales"
        vm.RegisterConnectionAsync <- Some(fun (def, _secret) -> Task.FromResult([ def ]))

        let raisedFor = ResizeArray<string>()
        (vm :> INotifyPropertyChanged).PropertyChanged.Add(fun args -> raisedFor.Add(args.PropertyName))

        do! vm.RegisterConnection connection None

        Assert.Contains("Connections", raisedFor)
    }

[<Fact>]
let ``SeedConnections sets Connections directly without requiring RegisterConnectionAsync to be configured, and raises PropertyChanged for Connections`` () =
    let vm = ShellViewModel()
    let connection = testConnection "Sales"
    let raisedFor = ResizeArray<string>()
    (vm :> INotifyPropertyChanged).PropertyChanged.Add(fun args -> raisedFor.Add(args.PropertyName))

    vm.SeedConnections([ connection ])

    Assert.Equal<ConnectionDefinition list>([ connection ], vm.Connections)
    Assert.Contains("Connections", raisedFor)
