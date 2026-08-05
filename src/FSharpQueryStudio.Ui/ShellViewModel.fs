namespace FSharpQueryStudio.Ui

/// Root view model — see Section 5. Workspace, active document, and
/// global commands are added alongside the tasks that need them
/// (Milestone A's script explorer/tabs work); worker status is here from
/// the start since the vertical slice's acceptance criterion depends on it.
type ShellViewModel() =
    member val WorkerState = WorkerStateViewModel()
