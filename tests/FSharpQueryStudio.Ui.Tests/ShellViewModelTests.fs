module ShellViewModelTests

open Xunit
open FSharpQueryStudio.Ui

[<Fact>]
let ``exposes a WorkerState starting in the Starting state`` () =
    let vm = ShellViewModel()
    Assert.Equal(WorkerState.Starting, vm.WorkerState.Current)
