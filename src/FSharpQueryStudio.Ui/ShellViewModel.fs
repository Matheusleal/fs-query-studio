namespace FSharpQueryStudio.Ui

open System.Threading.Tasks
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Editor

/// Root view model — see Section 5. Owns the workspace's open document
/// tabs and which one is active. Global commands join alongside whatever
/// task first needs them.
///
/// `scheduler` is forwarded to every `DocumentViewModel` this workspace
/// creates (the initial tab and any later `OpenDocument` call), so tests
/// can drive `CheckAsync`'s debounced trigger with a virtual clock the
/// same way `DocumentViewModelTests` does directly.
type ShellViewModel(?scheduler: IDebounceScheduler) =
    let documents = ResizeArray<DocumentViewModel>()

    let mutable checkAsync: ((int * string) -> Task<Diagnostic list>) option = None
    let mutable activeDocument = Unchecked.defaultof<DocumentViewModel>

    /// Wires a document's debounced text change to `checkAsync` — every
    /// open tab gets its own analysis pipeline, not just the first one.
    let wireDocument (doc: DocumentViewModel) =
        doc.DebouncedTextChanged.Add(fun (version, source) ->
            match checkAsync with
            | None -> ()
            | Some check ->
                (task {
                    let! diagnostics = check (version, source)
                    doc.ApplyAnalysisResult(version, diagnostics)
                }
                :> Task)
                |> ignore)

    let createDocument (uri: string) =
        let doc = DocumentViewModel(uri, ?scheduler = scheduler)
        wireDocument doc
        documents.Add(doc)
        doc

    do activeDocument <- createDocument "untitled:Script1.fsx"

    member val WorkerState = WorkerStateViewModel()
    member _.ActiveDocument = activeDocument
    member _.Documents = documents |> List.ofSeq
    member val Result = ResultViewModel()

    /// Prompts before discarding a dirty tab's unsaved changes — set by
    /// the composition root to show a real confirmation dialog. `Close`
    /// treats an unconfigured hook as "don't discard", the same
    /// no-op-when-unconfigured rationale as `ExecuteAsync` below.
    member val ConfirmDiscardDirtyDocument: (DocumentViewModel -> bool) option = None with get, set

    /// Opens `uri` as the active tab. Activates the existing tab instead
    /// of creating a duplicate if it's already open, leaving every other
    /// open tab's state untouched either way.
    member this.OpenDocument(uri: string) : DocumentViewModel =
        let doc =
            documents
            |> Seq.tryFind (fun d -> d.Uri = uri)
            |> Option.defaultWith (fun () -> createDocument uri)

        activeDocument <- doc
        doc

    /// Closes the tab for `uri`, prompting via `ConfirmDiscardDirtyDocument`
    /// first if it's dirty. Returns whether the tab was actually closed.
    /// Closing the last remaining tab opens a fresh blank one in its
    /// place, so `ActiveDocument` always has something to point at.
    member this.CloseDocument(uri: string) : bool =
        match documents |> Seq.tryFind (fun d -> d.Uri = uri) with
        | None -> false
        | Some doc ->
            let canDiscard =
                if not doc.IsDirty then
                    true
                else
                    match this.ConfirmDiscardDirtyDocument with
                    | None -> false
                    | Some confirm -> confirm doc

            if canDiscard then
                documents.Remove(doc) |> ignore

                if documents.Count = 0 then
                    activeDocument <- createDocument "untitled:Script1.fsx"
                elif System.Object.ReferenceEquals(activeDocument, doc) then
                    activeDocument <- documents.[documents.Count - 1]

            canDiscard

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

    /// `analysis/check` — set by the composition root. Invoked whenever
    /// `ActiveDocument.DebouncedTextChanged` fires (Section 8's debounced
    /// analysis pipeline); a no-op if never configured, same rationale as
    /// `ExecuteAsync`.
    member _.CheckAsync
        with get () = checkAsync
        and set value = checkAsync <- value

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
