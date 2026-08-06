namespace FSharpQueryStudio.Ui

open Dock.Model.Core
open Dock.Model.Controls
open Dock.Model.Mvvm
open Dock.Model.Mvvm.Controls

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

    [<CLIEvent>]
    member _.DocumentActivated = documentActivated.Publish

    override this.CreateLayout() : IRootDock =
        let initialDoc = ScriptDocument(shell.ActiveDocument, shell, editorContent)

        let workspaceTool = Tool(Id = "Workspace", Title = "Workspace", CanClose = false)
        let resultsTool = Tool(Id = "Results", Title = "Results", CanClose = false, Context = resultsContent)

        documentDock <-
            DocumentDock(
                Id = "Documents",
                IsCollapsable = false,
                CanCreateDocument = false,
                ActiveDockable = initialDoc,
                VisibleDockables = this.CreateList<IDockable>(initialDoc)
            )

        let workspaceDock =
            ToolDock(
                Id = "WorkspacePane",
                Alignment = Alignment.Left,
                Proportion = 0.2,
                ActiveDockable = workspaceTool,
                VisibleDockables = this.CreateList<IDockable>(workspaceTool)
            )

        let resultsDock =
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
