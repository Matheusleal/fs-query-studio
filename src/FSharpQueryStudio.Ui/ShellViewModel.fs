namespace FSharpQueryStudio.Ui

open System.ComponentModel
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
type ShellViewModel(?scheduler: IDebounceScheduler) as self =
    let documents = ResizeArray<DocumentViewModel>()
    let mutable connections: ConnectionDefinition list = []
    let mutable workspaceItems: WorkspaceConnectionViewModel list = []
    let mutable loadConnectionSchemaAsync: (System.Guid -> Task<Result<SchemaTable list, string>>) option = None

    let mutable checkAsync: ((int * string) -> Task<Diagnostic list>) option = None
    let mutable activeDocument = Unchecked.defaultof<DocumentViewModel>
    let propertyChanged = Event<PropertyChangedEventHandler, PropertyChangedEventArgs>()

    /// `Problems` below is derived from the *active* document's
    /// `Diagnostics`, and a derived CLR property notifies nothing on its
    /// own. Anything bound to it (the Problems pane) would therefore read
    /// it exactly once at bind time and never again, so every place the
    /// derived value can change has to re-raise here by hand.
    let raiseProblemsChanged () =
        propertyChanged.Trigger(self, PropertyChangedEventArgs("Problems"))

    /// `Result` below is derived from the *active* document's own
    /// `Result` — same shape/rationale as `raiseProblemsChanged` above,
    /// so every place `ActiveDocument` changes has to re-raise here by
    /// hand too, or a `{Binding Result.Xxx}` (Results/Output/Schema
    /// panes) would bind once and never notice a tab switch.
    let raiseResultChanged () =
        propertyChanged.Trigger(self, PropertyChangedEventArgs("Result"))

    /// `Connections` is a mutable list that updates whenever a connection
    /// is successfully registered. A derived CLR property notifies nothing
    /// on its own, so every place the derived value changes has to re-raise
    /// here by hand.
    let raiseConnectionsChanged () =
        propertyChanged.Trigger(self, PropertyChangedEventArgs("Connections"))

    /// `WorkspaceItems` mirrors `Connections` as real navigation objects
    /// (Connection -> Tables -> Columns) — see `WorkspaceConnectionViewModel`.
    /// Reused by Id across refreshes so an already-expanded/loaded
    /// connection's `WorkspaceConnectionViewModel` survives a
    /// register/remove elsewhere in the list, rather than being thrown
    /// away and rebuilt (which would silently re-collapse it and discard
    /// its cached schema).
    let raiseWorkspaceItemsChanged () =
        propertyChanged.Trigger(self, PropertyChangedEventArgs("WorkspaceItems"))

    let syncWorkspaceItems () =
        let loadSchema (id: System.Guid) =
            match loadConnectionSchemaAsync with
            | Some load -> load id
            | None -> Task.FromResult(Ok [])

        let existingById =
            workspaceItems |> List.map (fun item -> item.Id, item) |> dict

        workspaceItems <-
            connections
            |> List.map (fun definition ->
                match existingById.TryGetValue definition.Id with
                | true, existing ->
                    existing.UpdateDefinition definition
                    existing
                | false, _ -> WorkspaceConnectionViewModel(definition, loadSchema))

        raiseWorkspaceItemsChanged ()

    /// Wires a document's debounced text change to `checkAsync` — every
    /// open tab gets its own analysis pipeline, not just the first one —
    /// and re-raises `Problems` when this document's diagnostics land,
    /// but only while it's the active tab (a background tab's analysis
    /// completing changes nothing the Problems pane is showing).
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

        (doc :> INotifyPropertyChanged)
            .PropertyChanged.Add(fun args ->
                if
                    args.PropertyName = nameof doc.Diagnostics
                    && System.Object.ReferenceEquals(activeDocument, doc)
                then
                    raiseProblemsChanged ())

        (doc.Result :> INotifyPropertyChanged)
            .PropertyChanged.Add(fun args ->
                if
                    args.PropertyName = "Schema"
                    && System.Object.ReferenceEquals(activeDocument, doc)
                then
                    raiseResultChanged ())

    /// Starter content for every freshly created document — including
    /// the workspace's very first tab, not just later `File > New`
    /// documents — so a document with real, visibly-syntax-highlighted
    /// text is on screen the moment a tab opens, rather than an empty
    /// buffer indistinguishable from the editor having failed to render
    /// at all.
    let newScriptSeed =
        "// New F# script — write your code below\nprintfn \"Hello from F# Query Studio!\"\n"

    /// The one, stable URI the Welcome tab is always opened/reopened
    /// under — reachable as `ShellViewModel.WelcomeUri` so the
    /// composition root's startup call and `MainWindow`'s Help-menu
    /// handler both reference the exact same constant, never a
    /// re-typed literal that could drift. Reuses the existing
    /// `"untitled:"` scheme deliberately; `Welcome.fsx` doesn't match
    /// `NextUntitledUri`'s own `ScriptN.fsx` probing pattern, so the two
    /// coexist without collision.
    let welcomeUri = "untitled:Welcome.fsx"

    /// A quick guide to the app's actual features, seeded into the
    /// Welcome tab instead of `newScriptSeed` — see
    /// `docs/superpowers/specs/2026-08-12-welcome-page-design.md`. Every
    /// example stays inside a `//` comment deliberately: this file is
    /// meant to be run (F5) harmlessly as a no-op, not to hit a real
    /// "connection not found" error on a user's very first run.
    let welcomeGuideSeed =
        """// Welcome to F# Query Studio!
//
// This tab is a quick guide to what the app can do. It's just comments —
// press F5 if you like, it won't do anything (there's no live code here).
// Close this tab any time; reopen it later from Help > Welcome...
//
// RUNNING SCRIPTS
//   Write F# in a script tab, then press F5 (or Run in the toolbar/menu)
//   to execute it. Cancel stops a running script. Every run starts fresh
//   (Isolated mode) - nothing from a previous run carries over. The
//   status bar at the bottom shows the current session state.
//
// CONNECTING TO DATA
//   Connections menu > New Connection... registers a database, CSV file,
//   Excel workbook, or REST/JSON endpoint. Once registered, expand it in
//   the Workspace panel on the left to browse its tables and columns.
//
//   Supported kinds: SQLite, SQL Server, PostgreSQL, CSV, Excel, REST/JSON.
//
// QUERYING FROM A SCRIPT
//   Once a connection is registered, call it by name - no #r or open
//   needed, these are already available in every script:
//
//     let orders = Connections.sql "Sales" "select * from Orders where Total > @min" [ "min", box 100 ]
//     let rows    = Connections.csv "MyCsvConnection"
//     let sheet   = Connections.excel "MyWorkbook" None
//     // let data = Connections.rest "MyApi" options ct
//
//   Parameters are always bound, never string-concatenated into the SQL -
//   the `[ "min", box 100 ]` list above is how you pass them safely.
//
// CONTROLLING WHAT SHOWS IN RESULTS
//   By default the script's last value shows in the Results pane. To
//   show something specific instead:
//
//     Results.table orders   // shows orders' own real columns as a table
//     Results.text  "done"   // shows a plain text/string value
//
// READING RESULTS
//   Results, Output, Problems, and Schema each have their own tab at the
//   bottom - and each open script tab has its own: switching tabs never
//   loses another tab's last run, output log, or diagnostics.
//
// WATCHING WHAT THE APP IS DOING
//   Help > Logs... opens a live view of the app's own log files - useful
//   when something doesn't behave the way you expect.
"""

    let seedFor (uri: string) = if uri = welcomeUri then welcomeGuideSeed else newScriptSeed

    let createDocument (uri: string) =
        let doc = DocumentViewModel(uri, ?scheduler = scheduler, initialText = seedFor uri)
        wireDocument doc
        documents.Add(doc)
        doc

    do activeDocument <- createDocument "untitled:Script1.fsx"

    interface INotifyPropertyChanged with
        [<CLIEvent>]
        member _.PropertyChanged = propertyChanged.Publish

    member val WorkerState = WorkerStateViewModel()
    member _.ActiveDocument = activeDocument
    member _.Documents = documents |> List.ofSeq

    /// The active document's own Results/Output/Schema — see
    /// `DocumentViewModel.Result`. Recomputed on every access rather than
    /// cached (same pattern as `Problems`), with `raiseResultChanged`
    /// covering every place `ActiveDocument` itself changes.
    member this.Result = this.ActiveDocument.Result

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

        if not (System.Object.ReferenceEquals(activeDocument, doc)) then
            activeDocument <- doc
            propertyChanged.Trigger(self, PropertyChangedEventArgs(nameof this.ActiveDocument))
            raiseProblemsChanged ()
            raiseResultChanged ()

        doc

    /// Routes a bridge `textChanged` message to the document it actually
    /// names, ignoring one for a URI no longer open.
    ///
    /// Deliberately *not* applied to `ActiveDocument`: while there was one
    /// tab those were the same document, but with several open they aren't.
    /// Type in tab A and immediately click tab B, and an in-flight
    /// `textChanged` for A can land after the switch — applying it to
    /// whichever tab is active *now* would write A's text into B.
    /// `DocumentViewModel.ApplyTextChanged`'s `newVersion > version` guard
    /// is no defence there, because that counter is per-document and B's
    /// was just reset by the `documentLoaded` push the activation itself
    /// triggered.
    member _.RouteTextChanged(documentUri: string, version: int, source: string) : unit =
        documents
        |> Seq.tryFind (fun d -> d.Uri = documentUri)
        |> Option.iter (fun d -> d.ApplyTextChanged(version, source))

    /// Marks `doc` clean after a *successful* write to disk (delegating to
    /// `DocumentViewModel.MarkSaved`) and — when that actually retagged the
    /// document's `Uri` — re-keys the editor page onto the new URI with a
    /// `DocumentLoaded` push. Called by `MainWindow`'s save handlers, which
    /// wire `SendToEditor` to the real WebView.
    ///
    /// That push is not cosmetic. `editor-host.html` stamps every outgoing
    /// `textChanged` with a `documentUri` variable it updates *only* on an
    /// incoming `documentLoaded`, and `RouteTextChanged` above routes
    /// strictly by that URI. Saving an `untitled:` tab through the picker
    /// retags the document host-side, but nothing else tells the page — so
    /// without this every keystroke after that first save still carries the
    /// dead pre-save URI, matches no open document, and is dropped in
    /// silence: the next Save writes the pre-save text, and the tab never
    /// even re-marks dirty to hint that anything was lost.
    ///
    /// Pushed only when the URI actually changed, and only while `doc` is
    /// still the active tab. Both guards matter: `documentLoaded` calls
    /// Monaco's `setValue`, which discards the editor's undo stack, so
    /// re-pushing on an ordinary same-path re-save would cost the user
    /// their undo history for no gain — and the save handlers `await` a
    /// file picker, so the user can switch tabs before the write lands.
    /// Pushing then would re-key the page onto a document it isn't showing
    /// *and* overwrite the visible tab's text with the saved one's. The
    /// background case needs no push anyway: switching back to that tab
    /// changes `ActiveDocument`, which pushes the (by then retagged) URI
    /// itself.
    member this.MarkDocumentSaved(doc: DocumentViewModel, newUri: string option) : unit =
        let previousUri = doc.Uri
        doc.MarkSaved(newUri)

        if doc.Uri <> previousUri then
            // Retention is keyed by DocumentUri at run time (see
            // `ResultRetention`). A retag off `previousUri` would otherwise
            // strand that key's entry forever — exactly the memory leak
            // this plan set out to stop — and leave the new URI with
            // nothing retained, breaking that tab's export going forward.
            // Same fire-and-forget hook `CloseDocument` already uses.
            match this.DiscardResultsAsync with
            | None -> ()
            | Some discard -> discard previousUri |> ignore

            if System.Object.ReferenceEquals(activeDocument, doc) then
                match this.SendToEditor with
                | None -> ()
                | Some send -> send (EditorBridge.encodeNotification (DocumentLoaded(doc.Uri, doc.Version, doc.Text)))

    /// The first `untitled:ScriptN.fsx` (probing upward from 1) that no
    /// open tab is already using — what File > New names its document.
    ///
    /// Probing rather than counting matters: `Documents.Length + 1` collides
    /// as soon as a tab has been closed. Open three, close the second, and
    /// `Length + 1` is `Script3`, which is still open — the caller's
    /// open-or-activate dedupe would then quietly re-activate that existing
    /// tab, making File > New look like it did nothing.
    member _.NextUntitledUri() : string =
        let inUse = documents |> Seq.map (fun d -> d.Uri) |> Set.ofSeq

        Seq.initInfinite (fun i -> $"untitled:Script{i + 1}.fsx")
        |> Seq.find (fun uri -> not (inUse.Contains uri))

    /// The stable URI the Welcome tab always opens/reopens under — see
    /// `welcomeUri`'s own doc comment above for why both the composition
    /// root's startup call and `MainWindow`'s Help-menu handler must go
    /// through this member rather than a re-typed literal.
    member _.WelcomeUri = welcomeUri

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

                match this.DiscardResultsAsync with
                | None -> ()
                | Some discard -> discard doc.Uri |> ignore

                if documents.Count = 0 then
                    activeDocument <- createDocument "untitled:Script1.fsx"
                    propertyChanged.Trigger(self, PropertyChangedEventArgs(nameof this.ActiveDocument))
                    raiseProblemsChanged ()
                    raiseResultChanged ()
                elif System.Object.ReferenceEquals(activeDocument, doc) then
                    activeDocument <- documents.[documents.Count - 1]
                    propertyChanged.Trigger(self, PropertyChangedEventArgs(nameof this.ActiveDocument))
                    raiseProblemsChanged ()
                    raiseResultChanged ()

            canDiscard

    /// The "host service" (Section 4's App responsibility) that actually
    /// runs a script and returns its projected result — set by the
    /// composition root. `RunAsync` is a no-op if it's never configured,
    /// which keeps this view model constructible and testable without a
    /// worker process behind it. Takes the full `EvaluateParams` (E.1) so
    /// the active document's `SessionMode`/`SessionId` reach the worker,
    /// not just its source text.
    member val ExecuteAsync: (EvaluateParams -> Task<ResultEnvelope>) option = None with get, set

    /// Requests cancellation of whatever `ExecuteAsync` call is currently
    /// in flight — set by the composition root alongside `ExecuteAsync`,
    /// closing over the same `WorkerClient` and the request id it sent.
    /// `Cancel` is a no-op if it's never configured, same rationale as
    /// `ExecuteAsync`.
    member val CancelAsync: (unit -> Task<unit>) option = None with get, set

    /// The composition root's hook into the real `session/reset` worker
    /// call — set alongside `ExecuteAsync`/`CancelAsync`.
    member val ResetSession: (string -> Task<unit>) option = None with get, set

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

    /// `analysis/hover` — set by the composition root, same
    /// no-op-when-unconfigured shape as `ExecuteAsync`/`CheckAsync`.
    /// Takes `(documentUri, version, source, line, column)`; `MainWindow`
    /// calls it in response to a bridge `hoverRequested` message and
    /// posts the result back as `HoverResolved`.
    member val HoverAsync: ((string * int * string * int * int) -> Task<HoverInfo option>) option =
        None with get, set

    /// `analysis/definition` — same shape/rationale as `HoverAsync`.
    member val DefinitionAsync: ((string * int * string * int * int) -> Task<DefinitionLocation option>) option =
        None with get, set

    /// `analysis/signatureHelp` — same shape/rationale as `HoverAsync`.
    member val SignatureHelpAsync: ((string * int * string * int * int) -> Task<SignatureHelpInfo option>) option =
        None with get, set

    /// `analysis/complete` — same shape/rationale as `HoverAsync`.
    member val CompleteAsync: ((string * int * string * int * int) -> Task<string list>) option =
        None with get, set

    /// `analysis/rename` — same shape/rationale as `HoverAsync`. The
    /// requested `newName` isn't part of this hook's own signature — the
    /// bridge case in `MainWindow` carries it separately, since Monaco's
    /// own rename UI collects it and this hook only ever needs to locate
    /// the edits; applying the actual rewrite with `newName` substituted
    /// is `editor-host.html`'s job once `RenameResolved` comes back.
    member val RenameAsync: ((string * int * string * int * int) -> Task<RenameEdit list option>) option =
        None with get, set

    /// The composition root's hook into the real OS clipboard — see
    /// C.15. A no-op if never configured, same rationale as
    /// `ExecuteAsync`.
    member val CopyResultToClipboard: (string -> Task<unit>) option = None with get, set

    /// The composition root's hook into `results/exportCsv` — see
    /// `ExportService.exportCsv`. Takes `(documentUri, filePath)`; a no-op
    /// (`Error`) if never configured, so `ExportCsvAsync` stays callable
    /// without a worker process behind it, same rationale as every other
    /// hook here.
    member val ExportCsvToFile: ((string * string) -> Task<Result<unit, string>>) option = None with get, set

    /// The composition root's hook into `results/discard` — see
    /// `ExportService.discardResults`. Called fire-and-forget from
    /// `CloseDocument` right after a tab is actually removed, so a
    /// closed tab's retained result doesn't leak in the Worker's memory.
    /// A no-op if never configured, same rationale as every other hook.
    member val DiscardResultsAsync: (string -> Task<unit>) option = None with get, set

    /// The list of registered database connections for this workspace.
    member _.Connections = connections

    /// The composition root's hook into `connection/register` — set
    /// alongside `ExecuteAsync`/`ResetSession`. Takes the definition and
    /// an optional plaintext secret (saved to the OS credential store
    /// before the definition itself is ever sent anywhere) — see
    /// `ConnectionService.register`. A no-op if never configured, same
    /// rationale as every other hook here.
    member val RegisterConnectionAsync: ((ConnectionDefinition * string option) -> Task<ConnectionDefinition list>) option =
        None with get, set

    /// Registers `definition` (with `secret`, if any) and updates
    /// `Connections` to the worker's returned, up-to-date list on
    /// success. A no-op, not an error, when `RegisterConnectionAsync`
    /// isn't configured.
    member this.RegisterConnection (definition: ConnectionDefinition) (secret: string option) : Task<unit> =
        task {
            match this.RegisterConnectionAsync with
            | None -> ()
            | Some register ->
                let! updated = register (definition, secret)
                connections <- updated
                raiseConnectionsChanged ()
                syncWorkspaceItems ()
        }

    /// The composition root's hook into `connection/remove` — set
    /// alongside `RegisterConnectionAsync`. See `ConnectionService.remove`
    /// for why it takes the full definition (needs `SecretReference` to
    /// know whether there's a credential to delete too), not just an Id.
    /// A no-op if never configured, same rationale as every other hook
    /// here.
    member val RemoveConnectionAsync: (ConnectionDefinition -> Task<ConnectionDefinition list>) option =
        None with get, set

    /// Removes `definition` and updates `Connections` to the worker's
    /// returned, up-to-date list on success. A no-op, not an error, when
    /// `RemoveConnectionAsync` isn't configured.
    member this.RemoveConnection(definition: ConnectionDefinition) : Task<unit> =
        task {
            match this.RemoveConnectionAsync with
            | None -> ()
            | Some remove ->
                let! updated = remove definition
                connections <- updated
                raiseConnectionsChanged ()
                syncWorkspaceItems ()
        }

    /// Seeds `Connections` at startup from whatever's already in
    /// `workspace.json` — distinct from `RegisterConnection`, which is
    /// for adding one new connection through the configured hook.
    /// Raises `PropertyChanged` the same as every other place `Connections`
    /// changes: `MainWindow`'s constructor sets `DataContext` (activating
    /// every `{Binding ...}` in the XAML, including `WorkspacePanel`'s
    /// `ItemsSource="{Binding Connections}"`) *before* the composition root
    /// calls `SeedConnections`, so by the time this runs the binding is
    /// already live and needs the same nudge `RegisterConnection` gives it.
    member _.SeedConnections(initial: ConnectionDefinition list) =
        connections <- initial
        raiseConnectionsChanged ()
        syncWorkspaceItems ()

    /// Real object navigation (Connection -> Tables -> Columns) over
    /// `Connections` — see `WorkspaceConnectionViewModel`. Kept in sync
    /// with `Connections` at every point that already raises
    /// `raiseConnectionsChanged`.
    member _.WorkspaceItems = workspaceItems

    /// The composition root's hook into `connection/schema` — see
    /// `ConnectionService.schema`. `syncWorkspaceItems`'s `loadSchema`
    /// closure reads this by name at call time, so it's safe to set
    /// after `WorkspaceConnectionViewModel`s already exist.
    member _.LoadConnectionSchemaAsync
        with get () = loadConnectionSchemaAsync
        and set value = loadConnectionSchemaAsync <- value

    /// Section 13's `trust.json` metadata for the active workspace,
    /// loaded by the composition root — a freshly-created workspace
    /// defaults to locally-authored/unknown-trust with nothing requested,
    /// so `RunAsync` never blocks on it until real trust data is wired in.
    member val Trust: TrustMetadata =
        { Origin = ScriptOrigin.Local
          TrustState = TrustState.Unknown
          RequestedCapabilities = []
          ReviewedAt = None
          ReviewedBy = None }
        with get, set

    /// Shows requested capabilities and blocks until the user approves
    /// or denies — set by the composition root (D.3). Approving is
    /// treated as "unblocked for this run"; `RunAsync` only re-prompts on
    /// the next run if the caller resets `Trust.TrustState` itself. No
    /// hook configured denies by default, the opposite of every other
    /// no-op-when-unconfigured hook on this type, since silently running
    /// an unapproved script would defeat the whole point of D.3.
    member val RequestCapabilityApproval: (TrustMetadata -> bool) option = None with get, set

    /// Problems pane rows for the active document's current diagnostics —
    /// see Section 5. Recomputed on every access rather than cached; the
    /// Problems pane binds to this (UI-Foundation.9), so `PropertyChanged`
    /// is raised for it by hand — see `raiseProblemsChanged` above — both
    /// when the active document changes and when the active document's own
    /// `Diagnostics` do. A derived property notifies nothing on its own,
    /// so without those raises the pane would bind once and never update.
    member this.Problems: ProblemViewModel list =
        this.ActiveDocument.Diagnostics
        |> List.map (fun d -> ProblemViewModel(this.ActiveDocument.Uri, d, this.SendToEditor))

    /// Approves the current run against `Trust` — `Trusted` always
    /// proceeds, `Blocked` never does, and anything else only prompts
    /// (via `RequestCapabilityApproval`) when capabilities are actually
    /// requested; a script requesting none has nothing to approve.
    /// Approving updates `Trust.TrustState` to `Trusted` so the same
    /// script doesn't re-prompt on its next run within this session.
    member private this.TryApproveRun() : bool =
        match this.Trust.TrustState with
        | TrustState.Trusted -> true
        | TrustState.Blocked -> false
        | _ ->
            if List.isEmpty this.Trust.RequestedCapabilities then
                true
            else
                match this.RequestCapabilityApproval with
                | None -> false
                | Some approve ->
                    let approved = approve this.Trust

                    if approved then
                        this.Trust <- { this.Trust with TrustState = TrustState.Trusted }

                    approved

    member this.RunAsync() : Task<unit> =
        task {
            match this.ExecuteAsync with
            | None -> ()
            | Some execute ->
                if this.TryApproveRun() then
                    this.WorkerState.TryTransition(WorkerState.Running) |> ignore

                    let doc = this.ActiveDocument

                    let evalParams: EvaluateParams =
                        { Source = doc.Text
                          SessionId = (match doc.SessionMode with
                                       | Persistent -> Some doc.Uri
                                       | Isolated -> None)
                          Mode = doc.SessionMode
                          DocumentUri = doc.Uri }

                    let! envelope = execute evalParams

                    // `doc`, not `this.ActiveDocument` re-read here — the
                    // user can switch tabs while `execute` is in flight,
                    // and the result belongs to the document that was
                    // actually run, not whatever tab happens to be active
                    // when the response lands (see `RouteTextChanged`'s
                    // doc comment for the same race, applied there to
                    // text instead of results).
                    doc.Result.Apply(envelope)

                    if doc.SessionMode = Persistent then
                        doc.MarkSessionActive()

                    this.WorkerState.TryTransition(WorkerState.Ready) |> ignore
        }

    /// Drops the active document's worker-side Persistent-mode session
    /// via `ResetSession` (E.2's `session/reset`, set by the composition
    /// root) and clears its local `HasActiveSession` display state — a
    /// no-op if the hook isn't configured, same rationale as `RunAsync`/
    /// `ExecuteAsync`. Safe to call regardless of the document's current
    /// `SessionMode`: resetting a session that was never Persistent, or
    /// already reset, is a no-op both worker-side (`SessionStore.reset`)
    /// and locally (`MarkSessionReset`).
    member this.ResetSessionAsync() : Task<unit> =
        task {
            match this.ResetSession with
            | None -> ()
            | Some reset ->
                do! reset this.ActiveDocument.Uri
                this.ActiveDocument.MarkSessionReset()
        }

    /// Copies the active result to the clipboard — C.15. A no-op if
    /// `CopyResultToClipboard` was never configured, same rationale as
    /// `RunAsync`/`ExecuteAsync`.
    member this.CopyResultAsync() : Task<unit> =
        task {
            match this.CopyResultToClipboard with
            | None -> ()
            | Some copy -> do! copy this.Result.ClipboardText
        }

    /// Exports the active document's retained full result to `filePath` —
    /// see docs/superpowers/specs/2026-08-12-results-export-design.md.
    /// Returns an `Error` (not a thrown exception) both for a real worker-
    /// side failure and for `ExportCsvToFile` never having been configured
    /// — the caller (`MainWindow`'s click handler) surfaces either the same
    /// way.
    member this.ExportCsvAsync(filePath: string) : Task<Result<unit, string>> =
        task {
            match this.ExportCsvToFile with
            | None -> return Result.Error "Export is not available."
            | Some export -> return! export (this.ActiveDocument.Uri, filePath)
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
