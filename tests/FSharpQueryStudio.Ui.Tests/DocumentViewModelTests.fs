module DocumentViewModelTests

open System.ComponentModel
open Xunit
open FSharpQueryStudio.Ui

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
