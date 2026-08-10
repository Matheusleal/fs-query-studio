namespace FSharpQueryStudio.Ui

open System.Collections.Generic
open Dock.Model.Core
open Dock.Model.Controls
open Dock.Model.Mvvm
open Dock.Model.Mvvm.Controls
open Dock.Serializer.SystemTextJson

/// Builds and owns the VS-shaped default dock layout (Section 5's
/// workspace/document/results panes) and keeps it in sync with
/// `ShellViewModel`'s canonical document list. `createEditorControl`
/// builds a fresh Monaco `NativeWebView` control per document tab (see
/// `ScriptDocument`'s doc comment for why one *per tab*, not shared —
/// Avalonia rejects the same `Control` instance being parented under two
/// `ContentControl`s at once, which a shared instance hits the moment a
/// second tab exists); `resultsContent` is the existing Results/Output/
/// Problems/Schema view — safe to share, since exactly one "Results" tool
/// ever exists. Both `createEditorControl`'s return value and
/// `resultsContent` arrive as `obj`, built by the caller (`MainWindow`) —
/// `Ui` has no other Avalonia-control dependency anywhere, and this keeps
/// it that way.
///
/// The real installed `Dock.Model`/`Dock.Model.Mvvm` (v12.1.0) place
/// `IRootDock`/`IDocumentDock` in `Dock.Model.Controls` (not
/// `Dock.Model.Core`, which only has `IDockable`/`Alignment`/
/// `Orientation`/`IFactory`), and the concrete `DocumentDock`/`ToolDock`/
/// `ProportionalDock`/`ProportionalDockSplitter` classes live in
/// `Dock.Model.Mvvm.Controls` alongside `Document`/`Tool` (not in a
/// separate `Dock.Model.Controls` implementation namespace — those are
/// interfaces only) — confirmed via reflection over the built
/// `Dock.Model.dll`/`Dock.Model.Mvvm.dll`. The opens above already
/// reflect that.
type DockFactory(shell: ShellViewModel, createEditorControl: unit -> obj, resultsContent: obj) as self =
    inherit Factory()

    let documentActivated = Event<ScriptDocument>()
    let mutable documentDock: IDocumentDock = Unchecked.defaultof<_>
    let mutable workspaceDock: IToolDock = Unchecked.defaultof<_>
    let mutable resultsDock: IToolDock = Unchecked.defaultof<_>
    let mutable workspaceTool: IDockable = Unchecked.defaultof<_>
    let mutable resultsTool: IDockable = Unchecked.defaultof<_>

    /// The Dock-side half of `ShellViewModel.OpenDocument`'s dedupe —
    /// `ShellViewModel.Documents` carries no change notification Dock
    /// could observe directly, so `DockFactory.OpenDocument` (below) is
    /// the one path every "open a script" caller (File > New/Open, a
    /// later workspace-explorer double-click) goes through instead.
    let findScriptDocument (uri: string) : ScriptDocument option =
        documentDock.VisibleDockables
        |> Seq.choose (function
            | :? ScriptDocument as sd -> Some sd
            | _ -> None)
        |> Seq.tryFind (fun sd -> sd.Document.Uri = uri)

    /// Adds `document` as a new Dock tab and activates it. Shared by
    /// `OpenDocument` and the last-tab-closed repair below; both need the
    /// same steps, and neither may go through `shell.OpenDocument` again
    /// (the caller has already done the shell-side half).
    ///
    /// `AddDockable` rather than `documentDock.VisibleDockables.Add`: the
    /// raw list insert leaves the new tab's `Owner`/`Factory` null, and a
    /// dockable with no `Owner` can never be closed again — `CloseDockable`
    /// resolves the dock to remove it from *through* `Owner`, so it just
    /// silently does nothing. Verified empirically against the installed
    /// v12.1.0: a tab added the raw way reports `Owner = null`, and
    /// clicking its close button (or calling `CloseDockable` directly)
    /// leaves it in place. `AddDockable` runs `InitDockable` for us, which
    /// is what wires both fields. `InitLayout` happens to do the same
    /// thing, which is why the one tab `CreateLayout` builds was always
    /// closeable and this never showed up before now.
    let addTab (document: DocumentViewModel) : ScriptDocument =
        let scriptDoc = ScriptDocument(document, shell, createEditorControl ())
        self.AddDockable(documentDock, scriptDoc)
        self.SetActiveDockable(scriptDoc) |> ignore
        scriptDoc

    do
        self.ActiveDockableChanged.Add(fun args ->
            match args.Dockable with
            | :? ScriptDocument as sd ->
                shell.OpenDocument(sd.Document.Uri) |> ignore
                documentActivated.Trigger(sd)
            | _ -> ())

        // `ShellViewModel.CloseDocument` guarantees `ActiveDocument` always
        // points at *something*: closing the last remaining tab replaces the
        // document list with a fresh `untitled:Script1.fsx` rather than
        // leaving the shell with no active document at all. Dock knows
        // nothing about that replacement, though — it just removed the tab
        // the user clicked — so without this the document area goes visually
        // empty while `ActiveDocument` points at a document with no tab, and
        // Run would silently execute that invisible document.
        //
        // `DockableClosed` (not `DockableClosing`) is the right hook:
        // verified empirically against the installed v12.1.0 that it fires
        // *after* the dockable has been removed from `VisibleDockables` and
        // after `ScriptDocument.OnClose` has already run
        // `shell.CloseDocument` — so both the "no tabs left" test and
        // `shell.ActiveDocument` are already settled by the time this runs.
        // It also only fires when the close actually went through: a dirty
        // tab whose `OnClose` returned false never reaches here (also
        // verified empirically), so a declined close can't spawn a tab.
        self.DockableClosed.Add(fun args ->
            match args.Dockable with
            | :? ScriptDocument ->
                let anyTabsLeft =
                    documentDock.VisibleDockables
                    |> Seq.exists (fun d -> d :? ScriptDocument)

                if not anyTabsLeft then
                    addTab shell.ActiveDocument |> ignore
            | _ -> ())

    /// The Dock tab strip for open scripts — exposed so tests can assert
    /// on its `VisibleDockables` without reaching through `CreateLayout`'s
    /// return value.
    member _.DocumentDock = documentDock

    /// The left-side tool pane hosting the "Workspace" `Tool` — exposed
    /// (same rationale as `DocumentDock`) so `MainWindow`'s View menu
    /// wiring (Task 11) and tests can find the `Tool` by reference rather
    /// than re-deriving it from `CreateLayout`'s return value.
    member _.WorkspaceDock = workspaceDock

    /// The bottom tool pane hosting the "Results" `Tool` — see
    /// `WorkspaceDock`'s doc comment.
    member _.ResultsDock = resultsDock

    /// The "Workspace" `Tool` dockable itself — exposed separately from
    /// `WorkspaceDock` because `IFactory.HideDockable`/`RestoreDockable`
    /// (the real installed v12.1.0 API for toggling a tool pane's
    /// visibility — confirmed via a throwaway xunit probe, since neither
    /// `IDockable` nor `IFactory` exposes a simpler `IsVisible`-style
    /// flag) need the *same* `IDockable` reference across a hide/restore
    /// round-trip: once hidden, the tool's `Owner` becomes the root dock
    /// and it drops out of `WorkspaceDock.VisibleDockables` entirely, so
    /// `RestoreDockable` only finds its way back to the original
    /// `ToolDock` when handed the exact object that was hidden — a fresh
    /// `RestoreDockable(id: string)` lookup after the hide does *not*
    /// work (verified empirically: it left the pane hidden).
    member _.WorkspaceTool = workspaceTool

    /// The "Results" `Tool` dockable itself — see `WorkspaceTool`'s doc
    /// comment.
    member _.ResultsTool = resultsTool

    /// Fires the newly active tab's `ScriptDocument` — not just its
    /// `DocumentViewModel` — whenever the active dockable changes,
    /// because the caller (`MainWindow`) needs to reach that specific
    /// tab's own `Context` (its dedicated `NativeWebView`) to push the
    /// `DocumentLoaded` notification into the right control, not
    /// whichever one used to be shared.
    [<CLIEvent>]
    member _.DocumentActivated = documentActivated.Publish

    override this.CreateLayout() : IRootDock =
        let initialDoc = ScriptDocument(shell.ActiveDocument, shell, createEditorControl ())

        workspaceTool <- Tool(Id = "Workspace", Title = "Workspace", CanClose = false)

        // `ContentTool`, not a plain `Tool` — see its doc comment. A bare
        // `Tool` with `Context` set (what this used to be) matches none
        // of Dock's own default content templates, so the Results pane
        // rendered as empty background for the same reason the document
        // pane did.
        resultsTool <- ContentTool(Id = "Results", Title = "Results", CanClose = false, Content = resultsContent)

        documentDock <-
            DocumentDock(
                Id = "Documents",
                IsCollapsable = false,
                CanCreateDocument = false,
                ActiveDockable = initialDoc,
                VisibleDockables = this.CreateList<IDockable>(initialDoc)
            )

        workspaceDock <-
            ToolDock(
                Id = "WorkspacePane",
                Alignment = Alignment.Left,
                Proportion = 0.2,
                ActiveDockable = workspaceTool,
                VisibleDockables = this.CreateList<IDockable>(workspaceTool)
            )

        resultsDock <-
            ToolDock(
                Id = "ResultsPane",
                Alignment = Alignment.Bottom,
                Proportion = 0.3,
                ActiveDockable = resultsTool,
                VisibleDockables = this.CreateList<IDockable>(resultsTool)
            )

        let centerColumn =
            ProportionalDock(
                Orientation = Orientation.Vertical,
                VisibleDockables = this.CreateList<IDockable>(documentDock, ProportionalDockSplitter(), resultsDock)
            )

        let mainLayout =
            ProportionalDock(
                Orientation = Orientation.Horizontal,
                VisibleDockables =
                    this.CreateList<IDockable>(workspaceDock, ProportionalDockSplitter(), centerColumn)
            )

        let root = this.CreateRootDock()
        root.IsCollapsable <- false
        root.DefaultDockable <- mainLayout
        root.ActiveDockable <- mainLayout
        root.VisibleDockables <- this.CreateList<IDockable>(mainLayout)
        root.LeftPinnedDockables <- this.CreateList<IDockable>()
        root.RightPinnedDockables <- this.CreateList<IDockable>()
        root.TopPinnedDockables <- this.CreateList<IDockable>()
        root.BottomPinnedDockables <- this.CreateList<IDockable>()
        root

    /// Opens `uri` as a Dock tab, activating the existing one instead of
    /// duplicating it if it's already open — see `findScriptDocument`'s
    /// comment for why this, not `ShellViewModel.OpenDocument` directly,
    /// is the entry point every "open a script" caller uses.
    member this.OpenDocument(uri: string) : unit =
        match findScriptDocument uri with
        | Some existing -> this.SetActiveDockable(existing) |> ignore
        | None -> addTab (shell.OpenDocument(uri)) |> ignore

    /// Every `IDockable` list a node might keep children in — not just
    /// `VisibleDockables`. `IFactory.HideDockable` (Task 11's View-menu
    /// toggle) moves a tool out of `VisibleDockables` into
    /// `IRootDock.HiddenDockables`, and that list round-trips through
    /// JSON correctly (confirmed empirically) - a `FindById` that only
    /// looked at `VisibleDockables` would never find a hidden pane on
    /// restore. The four `*PinnedDockables` lists are included for the
    /// same reason (a pinned/auto-hide dockable lives there, not in
    /// `VisibleDockables`, on the real `Dock.Avalonia` pinning gesture),
    /// even though nothing in this app currently pins anything.
    static member private ChildLists(dock: IDock) : IList<IDockable> list =
        [ dock.VisibleDockables ]
        @ (match dock with
           | :? IRootDock as r ->
               [ r.HiddenDockables; r.LeftPinnedDockables; r.RightPinnedDockables; r.TopPinnedDockables; r.BottomPinnedDockables ]
           | _ -> [])
        |> List.filter (fun l -> not (isNull (box l)))

    /// Finds the first `IDockable` in `root`'s tree (searched via
    /// `ChildLists`, the containment relationship, not `Owner`) whose
    /// `Id` equals `id`. Shared by every restore-time graft below.
    static member private FindById(id: string, root: IDockable) : IDockable option =
        let rec walk (node: IDockable) : IDockable option =
            if node.Id = id then
                Some node
            else
                match node with
                | :? IDock as dock ->
                    DockFactory.ChildLists(dock) |> List.tryPick (fun l -> l |> Seq.tryPick walk)
                | _ -> None

        walk root

    /// Serializes `root` for persistence (Task 12), working around the
    /// installed serializer's inability to round-trip document tabs:
    /// `ScriptDocument` has only a 3-argument constructor (it needs a
    /// live `DocumentViewModel`/`ShellViewModel`/editor control), so
    /// System.Text.Json can never deserialize one back — confirmed
    /// empirically (`InvalidOperationException` on the deserialization
    /// constructor, "must bind to an object property or field"). Tab
    /// *content* was never going to survive a restart anyway, the same
    /// reason `ScriptDocument.Context` (the shared WebView) is already
    /// `[IgnoreDataMember]` — what should survive is the structural
    /// layout around it (pane sizes/alignment/visibility). So
    /// `documentDock`'s tab content is emptied for the duration of this
    /// one `Serialize` call and restored immediately after, live tabs
    /// completely unaffected. `RestoreDocumentTabs` below is the other
    /// half of this round-trip.
    ///
    /// Restoring `documentDock.ActiveDockable` in the `finally` re-fires
    /// `ActiveDockableChanged` -> `DocumentActivated` -> a
    /// `DocumentLoaded` bridge push into the WebView, on every window
    /// close. Harmless today only because `postToEditor` (in
    /// `MainWindow.axaml.fs`) already swallows a closing/closed WebView
    /// throwing - if that guard ever moves, this call site would need
    /// its own.
    member _.SerializeLayout(serializer: DockSerializer, root: IDock) : string =
        let originalVisible = documentDock.VisibleDockables
        let originalActive = documentDock.ActiveDockable
        let originalDefault = documentDock.DefaultDockable

        try
            documentDock.VisibleDockables <- ResizeArray<IDockable>() :> IList<IDockable>
            documentDock.ActiveDockable <- Unchecked.defaultof<IDockable>
            documentDock.DefaultDockable <- Unchecked.defaultof<IDockable>
            serializer.Serialize<IDock>(root)
        finally
            documentDock.VisibleDockables <- originalVisible
            documentDock.ActiveDockable <- originalActive
            documentDock.DefaultDockable <- originalDefault

    /// Dry-run check for whether `root` contains every pane
    /// `RestoreDocumentTabs`/`RestoreToolPanes` need — the document dock
    /// and both tool docks/tools, searched the same way `FindById`
    /// searches (so a hidden pane still counts as present). Mutates
    /// nothing. Callers MUST check this before calling either mutating
    /// method: both graft/repoint live state as they run (splicing the
    /// live `documentDock` in, repointing `DockFactory`'s cached
    /// tool-pane fields), and if the restore as a whole is going to be
    /// rejected because just one of the two panes is missing (an
    /// old/hand-edited `ui-state.json`, for instance), running the
    /// mutating half that *did* find its pane first would leave
    /// `DockFactory`'s live state pointing partway into a tree that's
    /// about to be discarded in favor of the default layout — the same
    /// "View-menu toggle is a silent no-op" symptom `RestoreToolPanes`
    /// exists to fix, just reached a different way.
    member _.CanRestore(root: IDockable) : bool =
        [ documentDock.Id; workspaceDock.Id; resultsDock.Id; workspaceTool.Id; resultsTool.Id ]
        |> List.forall (fun id -> (DockFactory.FindById(id, root)).IsSome)

    /// The other half of `SerializeLayout`: finds the empty `documentDock`
    /// placeholder inside a freshly restored tree — matched by `Id`,
    /// which (unlike `Owner`) is a plain `[DataMember]` string and does
    /// round-trip — and replaces it with the live `documentDock`
    /// instance, so the restored layout shows the real, still-open
    /// tab(s) instead of an empty document pane. The placeholder's own
    /// `Proportion` (correctly restored - only its tab *content* was
    /// excluded from the wire) is copied onto the live `documentDock`
    /// first, so grafting the live object back doesn't silently discard
    /// the document pane's last-saved size. Returns `false` (a no-op,
    /// not an error) if `root` doesn't contain a node with
    /// `documentDock`'s `Id` at all — but this still mutates
    /// `documentDock.Proportion` even on that path if a same-named node
    /// merely can't be spliced in (can't currently happen given
    /// `FindById`/`replace` search identically, but callers should still
    /// gate this on `CanRestore(root)` first, same as `RestoreToolPanes`,
    /// rather than rely on that invariant holding forever).
    member _.RestoreDocumentTabs(root: IDockable) : bool =
        match DockFactory.FindById(documentDock.Id, root) with
        | None -> false
        | Some placeholder ->
            documentDock.Proportion <- placeholder.Proportion

            let rec replace (node: IDockable) : bool =
                match node with
                | :? IDock as dock ->
                    // Null-guarded for the same reason `ChildLists` and
                    // `LayoutSerializer.normalize` are: only a hand-edited/
                    // truncated `ui-state.json` can produce a `null` list
                    // here (the real serializer always emits `[]`), and
                    // `App.axaml.fs`'s try/with would catch the resulting
                    // `ArgumentNullException` - but this walk shouldn't be
                    // the one place that relies on that outer net.
                    let index =
                        if isNull (box dock.VisibleDockables) then
                            None
                        else
                            dock.VisibleDockables |> Seq.tryFindIndex (fun d -> d.Id = documentDock.Id)

                    match index with
                    | Some i ->
                        dock.VisibleDockables.[i] <- (documentDock :> IDockable)
                        true
                    | None -> DockFactory.ChildLists(dock) |> List.exists (Seq.exists replace)
                | _ -> false

            replace root

    /// The tool-pane counterpart of `RestoreDocumentTabs`: `resultsTool`'s
    /// `Context` (the real `ResultsView` control) is `[IgnoreDataMember]`
    /// for the same reason `ScriptDocument.Context` is, so it always
    /// deserializes as `null` - and, separate from that, this
    /// `DockFactory`'s own cached `workspaceDock`/`resultsDock`/
    /// `workspaceTool`/`resultsTool` fields still point at the pre-restore
    /// objects that (after this call site swaps `dockControl.Layout` to
    /// the restored tree) are no longer actually in the tree at all -
    /// `MainWindow`'s View-menu hide/show toggles (Task 11) read those
    /// fields, so leaving them stale would make the toggles silent
    /// no-ops post-restore.
    ///
    /// Unlike `RestoreDocumentTabs`, this does *not* graft the live
    /// `ToolDock` objects wholesale - the restored `ToolDock`/`Tool`
    /// nodes correctly carry the persisted `Proportion`/`Alignment` (that
    /// survives the round-trip cleanly, unlike document content), so
    /// overwriting them with the pre-restore live objects would throw
    /// that away. Instead this finds the restored nodes by `Id` (via
    /// `FindById`, which also searches `HiddenDockables` - a pane hidden
    /// through Task 11's View-menu toggle before closing is still found
    /// here, not treated as missing), repairs just `resultsTool`'s
    /// `Context`, and repoints the cached fields at the restored
    /// instances. Returns `false` (a no-op, no fields repointed) if
    /// either tool pane isn't found in `root` at all. Callers MUST check
    /// `CanRestore(root)` before calling this (see its doc comment) -
    /// this mutates `DockFactory`'s live cached fields the moment it
    /// finds a match, with no way to undo that if a *sibling* restore
    /// step (`RestoreDocumentTabs`) then fails and the whole restore gets
    /// rejected.
    ///
    /// This also re-establishes each restored tool's `OriginalOwner`.
    /// `IFactory.RestoreDockable` (the un-hide half of Task 11's View-menu
    /// toggle) reads `OriginalOwner` to know which `ToolDock` to put a
    /// hidden dockable back into - but `OriginalOwner` is not part of the
    /// serialized wire format, so a pane that was hidden at save time
    /// comes back with it `null`, and `RestoreDockable` then removes the
    /// tool from `IRootDock.HiddenDockables` and puts it nowhere at all:
    /// unreachable from anywhere in the tree for the rest of the session
    /// (verified empirically - the pane never reappears, and the next save
    /// writes a layout missing that tool entirely, which the launch after
    /// that rejects wholesale via `CanRestore`, discarding all persisted
    /// layout state). Setting it unconditionally is safe: for a pane that
    /// was *visible* at save time, `HideDockable` overwrites it on the next
    /// hide anyway.
    member _.RestoreToolPanes(root: IDockable) : bool =
        match
            DockFactory.FindById(workspaceDock.Id, root),
            DockFactory.FindById(resultsDock.Id, root),
            DockFactory.FindById(workspaceTool.Id, root),
            DockFactory.FindById(resultsTool.Id, root)
        with
        // `rrt` must come back as a real `ContentTool` — Dock's
        // polymorphic serializer preserves the concrete type via its
        // `$type` discriminator, so a genuine round-trip always matches
        // here; a hand-edited `ui-state.json` that doesn't is exactly
        // the "restore rejected, fall back to the default layout" case
        // this whole match is already built to degrade into.
        | Some(:? IToolDock as rw), Some(:? IToolDock as rr), Some rwt, Some(:? ContentTool as rrt) ->
            rrt.Content <- resultsContent
            rwt.OriginalOwner <- (rw :> IDockable)
            (rrt :> IDockable).OriginalOwner <- (rr :> IDockable)
            workspaceDock <- rw
            resultsDock <- rr
            workspaceTool <- rwt
            resultsTool <- rrt
            true
        | _ -> false
