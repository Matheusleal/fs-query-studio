namespace FSharpQueryStudio.Ui

open System.Threading.Tasks
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Editor

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

    /// Requests cancellation of whatever `ExecuteAsync` call is currently
    /// in flight — set by the composition root alongside `ExecuteAsync`,
    /// closing over the same `WorkerClient` and the request id it sent.
    /// `Cancel` is a no-op if it's never configured, same rationale as
    /// `ExecuteAsync`.
    member val CancelAsync: (unit -> Task<unit>) option = None with get, set

    /// The composition root's hook into the real WebView bridge — set
    /// alongside `ExecuteAsync`/`CancelAsync`. Passed to every
    /// `ProblemViewModel` so its `Navigate` command has somewhere to send
    /// the caret-move message; a no-op there if this is never configured.
    member val SendToEditor: (EditorEnvelope -> unit) option = None with get, set

    /// Problems pane rows for the active document's current diagnostics —
    /// see Section 5. Recomputed on every access rather than cached/
    /// observable, since nothing binds to it yet.
    member this.Problems: ProblemViewModel list =
        this.ActiveDocument.Diagnostics
        |> List.map (fun d -> ProblemViewModel(this.ActiveDocument.Uri, d, this.SendToEditor))

    member this.RunAsync() : Task<unit> =
        task {
            match this.ExecuteAsync with
            | None -> ()
            | Some execute ->
                this.WorkerState.TryTransition(WorkerState.Running) |> ignore
                let! envelope = execute this.ActiveDocument.Text
                this.Result.Apply(envelope)
                this.WorkerState.TryTransition(WorkerState.Ready) |> ignore
        }

    /// A no-op unless the worker is actually `Running` — nothing to
    /// cancel from `Ready`/`Starting`/etc, and `RunAsync` above is the
    /// only path that reaches `Running` to begin with.
    member this.Cancel() : Task<unit> =
        task {
            if this.WorkerState.Current = WorkerState.Running then
                match this.CancelAsync with
                | None -> ()
                | Some cancel ->
                    this.WorkerState.TryTransition(WorkerState.Cancelling) |> ignore
                    do! cancel ()
        }
