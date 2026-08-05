namespace FSharpQueryStudio.Ui

open System.Threading.Tasks
open FSharpQueryStudio.Protocol

/// Root view model — see Section 5. One `ActiveDocument` for the vertical
/// slice's single editor tab; a workspace/multi-tab collection replaces
/// this once Milestone C's script explorer lands. Global commands join
/// alongside whatever task first needs them.
type ShellViewModel() =
    member val WorkerState = WorkerStateViewModel()
    member val ActiveDocument = DocumentViewModel("untitled:Script1.fsx")
    member val Result = ResultViewModel()

    /// The "host service" (Section 4's App responsibility) that actually
    /// runs a script and returns its projected result — set by the
    /// composition root. `RunAsync` is a no-op if it's never configured,
    /// which keeps this view model constructible and testable without a
    /// worker process behind it.
    member val ExecuteAsync: (string -> Task<ResultEnvelope>) option = None with get, set

    member this.RunAsync() : Task<unit> =
        task {
            match this.ExecuteAsync with
            | None -> ()
            | Some execute ->
                let! envelope = execute this.ActiveDocument.Text
                this.Result.Apply(envelope)
        }
