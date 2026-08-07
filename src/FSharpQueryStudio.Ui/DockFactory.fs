namespace FSharpQueryStudio.Ui

open System.Collections.Generic
open Dock.Model.Core
open Dock.Model.Controls
open Dock.Model.Mvvm
open Dock.Model.Mvvm.Controls
open Dock.Serializer.SystemTextJson

/// Builds and owns the VS-shaped default dock layout (Section 5's
/// workspace/document/results panes) and keeps it in sync with
/// `ShellViewModel`'s canonical document list. `editorContent` is the
/// single shared Monaco `NativeWebView` control (see `ScriptDocument`'s
/// doc comment); `resultsContent` is the existing Results/Output/
/// Problems/Schema view, built by the caller (`MainWindow`) — `Ui` has
/// no other Avalonia-control dependency anywhere, and this keeps it
/// that way (both arrive as `obj`).
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
type DockFactory(shell: ShellViewModel, editorContent: obj, resultsContent: obj) as self =
    inherit Factory()

    let documentActivated = Event<DocumentViewModel>()
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

    do
        self.ActiveDockableChanged.Add(fun args ->
            match args.Dockable with
            | :? ScriptDocument as sd ->
                shell.OpenDocument(sd.Document.Uri) |> ignore
                documentActivated.Trigger(sd.Document)
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

    [<CLIEvent>]
    member _.DocumentActivated = documentActivated.Publish

    override this.CreateLayout() : IRootDock =
        let initialDoc = ScriptDocument(shell.ActiveDocument, shell, editorContent)

        workspaceTool <- Tool(Id = "Workspace", Title = "Workspace", CanClose = false)
        resultsTool <- Tool(Id = "Results", Title = "Results", CanClose = false, Context = resultsContent)

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
        | None ->
            let doc = shell.OpenDocument(uri)
            let scriptDoc = ScriptDocument(doc, shell, editorContent)
            documentDock.VisibleDockables.Add(scriptDoc)
            this.SetActiveDockable(scriptDoc) |> ignore

    /// Finds the first `IDockable` in `root`'s tree (searched via
    /// `VisibleDockables`, the containment relationship, not `Owner`)
    /// whose `Id` equals `id`. Shared by every restore-time graft below.
    static member private FindById(id: string, root: IDockable) : IDockable option =
        let rec walk (node: IDockable) : IDockable option =
            if node.Id = id then
                Some node
            else
                match node with
                | :? IDock as dock when not (isNull (box dock.VisibleDockables)) ->
                    dock.VisibleDockables |> Seq.tryPick walk
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
    /// `documentDock`'s `Id` at all.
    member _.RestoreDocumentTabs(root: IDockable) : bool =
        match DockFactory.FindById(documentDock.Id, root) with
        | None -> false
        | Some placeholder ->
            documentDock.Proportion <- placeholder.Proportion

            let rec replace (node: IDockable) : bool =
                match node with
                | :? IDock as dock when not (isNull (box dock.VisibleDockables)) ->
                    match dock.VisibleDockables |> Seq.tryFindIndex (fun d -> d.Id = documentDock.Id) with
                    | Some i ->
                        dock.VisibleDockables.[i] <- (documentDock :> IDockable)
                        true
                    | None -> dock.VisibleDockables |> Seq.exists replace
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
    /// that away. Instead this finds the restored nodes by `Id`, repairs
    /// just `resultsTool`'s `Context`, and repoints the cached fields at
    /// the restored instances. Returns `false` (a no-op) if either tool
    /// pane isn't found in `root` at all.
    member _.RestoreToolPanes(root: IDockable) : bool =
        match
            DockFactory.FindById(workspaceDock.Id, root),
            DockFactory.FindById(resultsDock.Id, root),
            DockFactory.FindById(workspaceTool.Id, root),
            DockFactory.FindById(resultsTool.Id, root)
        with
        | Some(:? IToolDock as rw), Some(:? IToolDock as rr), Some rwt, Some rrt ->
            rrt.Context <- resultsContent
            workspaceDock <- rw
            resultsDock <- rr
            workspaceTool <- rwt
            resultsTool <- rrt
            true
        | _ -> false
