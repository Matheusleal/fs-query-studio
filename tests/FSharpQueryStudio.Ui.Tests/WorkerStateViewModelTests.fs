module WorkerStateViewModelTests

open System.ComponentModel
open Xunit
open FSharpQueryStudio.Ui

[<Fact>]
let ``starts in the Starting state`` () =
    let vm = WorkerStateViewModel()
    Assert.Equal(WorkerState.Starting, vm.Current)

[<Fact>]
let ``a valid transition succeeds and updates Current`` () =
    let vm = WorkerStateViewModel()
    let succeeded = vm.TryTransition(WorkerState.Ready)

    Assert.True(succeeded)
    Assert.Equal(WorkerState.Ready, vm.Current)

[<Fact>]
let ``an invalid transition is rejected and Current is unchanged`` () =
    let vm = WorkerStateViewModel()
    // Starting -> Cancelling skips Ready/Running entirely.
    let succeeded = vm.TryTransition(WorkerState.Cancelling)

    Assert.False(succeeded)
    Assert.Equal(WorkerState.Starting, vm.Current)

[<Fact>]
let ``a full lifecycle of valid transitions all succeed in order`` () =
    let vm = WorkerStateViewModel()

    Assert.True(vm.TryTransition(WorkerState.Ready))
    Assert.True(vm.TryTransition(WorkerState.Running))
    Assert.True(vm.TryTransition(WorkerState.Cancelling))
    Assert.True(vm.TryTransition(WorkerState.Faulted))
    Assert.True(vm.TryTransition(WorkerState.Starting))
    Assert.Equal(WorkerState.Starting, vm.Current)

[<Fact>]
let ``PropertyChanged fires for Current on a successful transition`` () =
    let vm = WorkerStateViewModel()
    let mutable raisedFor: string option = None
    (vm :> INotifyPropertyChanged).PropertyChanged.Add(fun args -> raisedFor <- Some args.PropertyName)

    vm.TryTransition(WorkerState.Ready) |> ignore

    Assert.Equal(Some "Current", raisedFor)

[<Fact>]
let ``PropertyChanged does not fire on a rejected transition`` () =
    let vm = WorkerStateViewModel()
    let mutable raised = false
    (vm :> INotifyPropertyChanged).PropertyChanged.Add(fun _ -> raised <- true)

    vm.TryTransition(WorkerState.Cancelling) |> ignore

    Assert.False(raised)

[<Fact>]
let ``Stopped is terminal - no transition out of it succeeds`` () =
    let vm = WorkerStateViewModel()
    vm.TryTransition(WorkerState.Stopped) |> ignore

    for candidate in [ WorkerState.Starting; WorkerState.Ready; WorkerState.Running; WorkerState.Cancelling; WorkerState.Faulted ] do
        Assert.False(vm.TryTransition(candidate))
        Assert.Equal(WorkerState.Stopped, vm.Current)
