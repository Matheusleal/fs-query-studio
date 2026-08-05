namespace FSharpQueryStudio.Ui

/// Root view model — see Section 5. One `ActiveDocument` for the vertical
/// slice's single editor tab; a workspace/multi-tab collection replaces
/// this once Milestone C's script explorer lands. Global commands join
/// alongside whatever task first needs them.
type ShellViewModel() =
    member val WorkerState = WorkerStateViewModel()
    member val ActiveDocument = DocumentViewModel("untitled:Script1.fsx")
