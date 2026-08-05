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
