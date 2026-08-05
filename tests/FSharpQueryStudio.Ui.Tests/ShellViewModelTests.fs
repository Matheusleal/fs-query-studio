module ShellViewModelTests

open Xunit
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
