module DocumentViewModelTests

open System
open System.ComponentModel
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Ui
open FSharpQueryStudio.Ui.Tests

let private diagnostic (message: string) : Diagnostic =
    { Message = message
      Severity = DiagnosticSeverity.Error
      StartLine = 0
      StartColumn = 0
      EndLine = 0
      EndColumn = 1
      Code = None }

[<Fact>]
let ``starts with empty text, version 0, and not dirty`` () =
    let vm = DocumentViewModel("file:///a.fsx")

    Assert.Equal("file:///a.fsx", vm.Uri)
    Assert.Equal("", vm.Text)
    Assert.Equal(0, vm.Version)
    Assert.False(vm.IsDirty)

[<Fact>]
let ``starts in Isolated mode with no active session`` () =
    let vm = DocumentViewModel("file:///a.fsx")

    Assert.Equal(Isolated, vm.SessionMode)
    Assert.False(vm.HasActiveSession)
    Assert.Equal("Isolated", vm.SessionStateDisplay)

[<Fact>]
let ``MarkSessionActive only takes effect in Persistent mode`` () =
    let isolated = DocumentViewModel("file:///a.fsx")
    isolated.MarkSessionActive()
    Assert.False(isolated.HasActiveSession)

    let persistent = DocumentViewModel("file:///b.fsx")
    persistent.SessionMode <- Persistent
    persistent.MarkSessionActive()

    Assert.True(persistent.HasActiveSession)
    Assert.Equal("Persistent · active", persistent.SessionStateDisplay)

[<Fact>]
let ``MarkSessionReset clears HasActiveSession`` () =
    let vm = DocumentViewModel("file:///a.fsx")
    vm.SessionMode <- Persistent
    vm.MarkSessionActive()

    vm.MarkSessionReset()

    Assert.False(vm.HasActiveSession)
    Assert.Equal("Persistent · reset", vm.SessionStateDisplay)

[<Fact>]
let ``switching SessionMode clears HasActiveSession, even back to the same mode it started from`` () =
    let vm = DocumentViewModel("file:///a.fsx")
    vm.SessionMode <- Persistent
    vm.MarkSessionActive()

    vm.SessionMode <- Isolated

    Assert.False(vm.HasActiveSession)
    Assert.Equal(Isolated, vm.SessionMode)

[<Fact>]
let ``PropertyChanged fires for SessionMode, HasActiveSession, and SessionStateDisplay on a mode switch`` () =
    let vm = DocumentViewModel("file:///a.fsx")
    let mutable raisedNames: string list = []
    (vm :> INotifyPropertyChanged).PropertyChanged.Add(fun args -> raisedNames <- args.PropertyName :: raisedNames)

    vm.SessionMode <- Persistent

    Assert.Contains("SessionMode", raisedNames)
    Assert.Contains("HasActiveSession", raisedNames)
    Assert.Contains("SessionStateDisplay", raisedNames)

[<Fact>]
let ``ApplyTextChanged with a newer version updates Text and Version and marks dirty`` () =
    let vm = DocumentViewModel("file:///a.fsx")

    vm.ApplyTextChanged(1, "let x = 1")

    Assert.Equal("let x = 1", vm.Text)
    Assert.Equal(1, vm.Version)
    Assert.True(vm.IsDirty)

[<Fact>]
let ``ApplyTextChanged keeps advancing the version monotonically across several edits`` () =
    let vm = DocumentViewModel("file:///a.fsx")

    vm.ApplyTextChanged(1, "let x = 1")
    vm.ApplyTextChanged(2, "let x = 1\nlet y = 2")

    Assert.Equal(2, vm.Version)
    Assert.Equal("let x = 1\nlet y = 2", vm.Text)

[<Fact>]
let ``ApplyTextChanged with a version that is not greater than the current one is ignored`` () =
    let vm = DocumentViewModel("file:///a.fsx")
    vm.ApplyTextChanged(5, "let x = 1")

    vm.ApplyTextChanged(3, "some stale out-of-order text")

    Assert.Equal(5, vm.Version)
    Assert.Equal("let x = 1", vm.Text)

[<Fact>]
let ``PropertyChanged fires for Version on a successful apply`` () =
    let vm = DocumentViewModel("file:///a.fsx")
    let mutable raisedNames: string list = []
    (vm :> INotifyPropertyChanged).PropertyChanged.Add(fun args -> raisedNames <- args.PropertyName :: raisedNames)

    vm.ApplyTextChanged(1, "let x = 1")

    Assert.Contains("Version", raisedNames)

[<Fact>]
let ``PropertyChanged does not fire for a stale ApplyTextChanged`` () =
    let vm = DocumentViewModel("file:///a.fsx")
    vm.ApplyTextChanged(5, "let x = 1")
    let mutable raised = false
    (vm :> INotifyPropertyChanged).PropertyChanged.Add(fun _ -> raised <- true)

    vm.ApplyTextChanged(3, "stale")

    Assert.False(raised)

[<Fact>]
let ``rapid successive text changes raise DebouncedTextChanged exactly once, carrying the latest edit`` () =
    let scheduler = FakeDebounceScheduler()
    let vm = DocumentViewModel("file:///a.fsx", scheduler)
    let mutable fireCount = 0
    let mutable lastVersion = 0
    let mutable lastText = ""

    vm.DebouncedTextChanged.Add(fun (version, text) ->
        fireCount <- fireCount + 1
        lastVersion <- version
        lastText <- text)

    vm.ApplyTextChanged(1, "let x = 1")
    scheduler.Advance(TimeSpan.FromMilliseconds(100.0))
    vm.ApplyTextChanged(2, "let x = 1\nlet y = 2")
    scheduler.Advance(TimeSpan.FromMilliseconds(500.0))

    Assert.Equal(1, fireCount)
    Assert.Equal(2, lastVersion)
    Assert.Equal("let x = 1\nlet y = 2", lastText)

[<Fact>]
let ``a text change that never reaches the debounce window never raises DebouncedTextChanged`` () =
    let scheduler = FakeDebounceScheduler()
    let vm = DocumentViewModel("file:///a.fsx", scheduler)
    let mutable fireCount = 0
    vm.DebouncedTextChanged.Add(fun _ -> fireCount <- fireCount + 1)

    vm.ApplyTextChanged(1, "let x = 1")
    scheduler.Advance(TimeSpan.FromMilliseconds(399.0))

    Assert.Equal(0, fireCount)

[<Fact>]
let ``ApplyAnalysisResult with the current document version updates Diagnostics`` () =
    let vm = DocumentViewModel("file:///a.fsx")
    vm.ApplyTextChanged(1, "let x : int = \"oops\"")

    let diagnostics = [ diagnostic "type error" ]
    vm.ApplyAnalysisResult(1, diagnostics)

    Assert.Equal<Diagnostic list>(diagnostics, vm.Diagnostics)

[<Fact>]
let ``a stale analysis response arriving after a newer one is discarded, not applied`` () =
    let vm = DocumentViewModel("file:///a.fsx")
    vm.ApplyTextChanged(1, "let x = 1")
    vm.ApplyTextChanged(2, "let x : int = \"oops\"")

    let currentDiagnostics = [ diagnostic "type error" ]
    vm.ApplyAnalysisResult(2, currentDiagnostics)

    // The response for version 1 arrives late, after version 2's already landed.
    vm.ApplyAnalysisResult(1, [ diagnostic "stale" ])

    Assert.Equal<Diagnostic list>(currentDiagnostics, vm.Diagnostics)

[<Fact>]
let ``PropertyChanged fires for IsDirty when ApplyTextChanged marks the document dirty`` () =
    let doc = DocumentViewModel("untitled:Script1.fsx")
    let mutable raisedFor: string option = None
    (doc :> INotifyPropertyChanged).PropertyChanged.Add(fun args -> raisedFor <- Some args.PropertyName)

    doc.ApplyTextChanged(1, "1 + 1")

    Assert.Equal(Some "IsDirty", raisedFor)

// `MarkSaved` is what the save handlers call after a write that actually
// landed on disk. Before it existed a saved tab kept its dirty marker
// forever, and an `untitled:` tab re-prompted for a path on every Save
// instead of writing through to the file it had already been saved to.

[<Fact>]
let ``MarkSaved clears IsDirty`` () =
    let doc = DocumentViewModel("file:///a.fsx")
    doc.ApplyTextChanged(1, "1 + 1")
    Assert.True(doc.IsDirty)

    doc.MarkSaved(None)

    Assert.False(doc.IsDirty)

[<Fact>]
let ``MarkSaved with None leaves the Uri alone`` () =
    let doc = DocumentViewModel("file:///a.fsx")
    doc.ApplyTextChanged(1, "1 + 1")

    doc.MarkSaved(None)

    Assert.Equal("file:///a.fsx", doc.Uri)

[<Fact>]
let ``MarkSaved retags an untitled document to the path it was written to`` () =
    let doc = DocumentViewModel("untitled:Script1.fsx")
    doc.ApplyTextChanged(1, "1 + 1")

    doc.MarkSaved(Some @"C:\scripts\real.fsx")

    // The retag is what makes the *next* Save write straight through
    // instead of re-opening the picker: `untitled:` is the only thing the
    // save handler distinguishes a scratch tab by.
    Assert.Equal(@"C:\scripts\real.fsx", doc.Uri)
    Assert.False(doc.Uri.StartsWith("untitled:"))
    Assert.False(doc.IsDirty)

[<Fact>]
let ``MarkSaved preserves Text and Version - saving is not an edit`` () =
    let doc = DocumentViewModel("untitled:Script1.fsx")
    doc.ApplyTextChanged(7, "let x = 1")

    doc.MarkSaved(Some @"C:\scripts\real.fsx")

    Assert.Equal("let x = 1", doc.Text)
    Assert.Equal(7, doc.Version)

[<Fact>]
let ``MarkSaved raises PropertyChanged for both Uri and IsDirty`` () =
    let doc = DocumentViewModel("untitled:Script1.fsx")
    doc.ApplyTextChanged(1, "1 + 1")
    let raisedFor = ResizeArray<string>()
    (doc :> INotifyPropertyChanged).PropertyChanged.Add(fun args -> raisedFor.Add(args.PropertyName))

    doc.MarkSaved(Some @"C:\scripts\real.fsx")

    Assert.Contains("Uri", raisedFor)
    Assert.Contains("IsDirty", raisedFor)

[<Fact>]
let ``MarkSaved does not raise for Uri when the path has not actually changed`` () =
    let doc = DocumentViewModel(@"C:\scripts\real.fsx")
    doc.ApplyTextChanged(1, "1 + 1")
    let raisedFor = ResizeArray<string>()
    (doc :> INotifyPropertyChanged).PropertyChanged.Add(fun args -> raisedFor.Add(args.PropertyName))

    doc.MarkSaved(Some @"C:\scripts\real.fsx")

    Assert.DoesNotContain("Uri", raisedFor)
    Assert.Contains("IsDirty", raisedFor)

[<Fact>]
let ``MarkSaved on an already-clean document raises nothing`` () =
    let doc = DocumentViewModel("file:///a.fsx")
    let raisedFor = ResizeArray<string>()
    (doc :> INotifyPropertyChanged).PropertyChanged.Add(fun args -> raisedFor.Add(args.PropertyName))

    doc.MarkSaved(None)

    Assert.False(doc.IsDirty)
    Assert.Empty(raisedFor)

[<Fact>]
let ``a freshly-constructed DocumentViewModel owns its own Result, starting at NoValue`` () =
    let doc = DocumentViewModel("file:///a.fsx")

    Assert.Equal(ResultKind.NoValue, doc.Result.Kind)

[<Fact>]
let ``two different DocumentViewModels each own a distinct Result instance`` () =
    let first = DocumentViewModel("file:///a.fsx")
    let second = DocumentViewModel("file:///b.fsx")

    Assert.NotSame(first.Result, second.Result)
