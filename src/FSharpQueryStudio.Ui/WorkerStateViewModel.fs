namespace FSharpQueryStudio.Ui

open System.ComponentModel

/// See Section 5's core view models.
type WorkerState =
    | Starting
    | Ready
    | Running
    | Cancelling
    | Stopped
    | Faulted

module WorkerState =

    /// Stopped is terminal. Faulted only recovers via a fresh worker
    /// (Starting) — matching WorkerClient's crash-then-restart policy
    /// from Section 9. Every non-terminal state can also shut down.
    let private allowed =
        dict
            [ Starting, set [ Ready; Faulted; Stopped ]
              Ready, set [ Running; Stopped ]
              Running, set [ Cancelling; Ready; Faulted; Stopped ]
              Cancelling, set [ Ready; Faulted; Stopped ]
              Faulted, set [ Starting; Stopped ]
              Stopped, set [] ]

    let canTransition (from: WorkerState) (toState: WorkerState) : bool =
        allowed.[from] |> Set.contains toState

type WorkerStateViewModel() as self =
    let mutable current = WorkerState.Starting
    let propertyChanged = Event<PropertyChangedEventHandler, PropertyChangedEventArgs>()

    interface INotifyPropertyChanged with
        [<CLIEvent>]
        member _.PropertyChanged = propertyChanged.Publish

    member _.Current = current

    /// Returns false and leaves `Current` unchanged for a transition
    /// `WorkerState.canTransition` doesn't allow.
    member _.TryTransition(next: WorkerState) : bool =
        if WorkerState.canTransition current next then
            current <- next
            propertyChanged.Trigger(self, PropertyChangedEventArgs(nameof self.Current))
            true
        else
            false
