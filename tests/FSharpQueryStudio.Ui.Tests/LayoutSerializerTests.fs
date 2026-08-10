module LayoutSerializerTests

open Xunit
open Dock.Model.Core
open Dock.Model.Controls
open FSharpQueryStudio.Ui

/// Regression coverage for the Task 12 code-review findings:
/// - A plain `DockSerializer()` throws `JsonException: A possible object
///   cycle was detected` on any real, live-built `DockFactory` layout
///   (the installed v12.1.0 `Dock.Serializer.SystemTextJson` package's
///   own `JsonConverterList<T>.Write` serializes each list element via a
///   nested `JsonSerializer.Serialize` call, which breaks
///   `ReferenceHandler.Preserve`'s cycle tracking across that boundary -
///   see `LayoutSerializer`'s doc comment).
/// - The same defect duplicates `ActiveDockable`/`DefaultDockable`/
///   `FocusedDockable` instead of referencing the real
///   `VisibleDockables` entry (`LayoutSerializer.normalize` fixes this).
/// - Document tabs and tool-pane live content (`Context`) never survive
///   the round-trip and need grafting back (`DockFactory.RestoreDocumentTabs`/
///   `RestoreToolPanes`).
///
/// These tests build a layout the same way `DockFactoryTests.fs` does
/// and exercise the real restore path end to end, so a future change
/// that reintroduces any of these fails a test instead of only failing
/// silently (or visibly, for the tool-pane/duplication bugs) at app
/// startup.

let private rebuild () =
    let shell = ShellViewModel()
    let factory = DockFactory(shell, (fun () -> obj ()), obj ())
    let layout = factory.CreateLayout()
    factory.InitLayout(layout)
    shell, factory, layout

/// `ResultsTool` content lives in `ContentTool.Content`, not
/// `IDockable.Context` — see `ContentTool`'s doc comment. `null` for
/// anything that isn't a `ContentTool` (e.g. the restored tree's own
/// tool node before `RestoreToolPanes` has grafted the live one in).
let private resultsContentOf (tool: IDockable) : obj =
    match tool with
    | :? ContentTool as ct -> ct.Content
    | _ -> null

let rec private find (id: string) (node: IDockable) : IDockable option =
    if node.Id = id then
        Some node
    else
        match node with
        | :? IDock as dock when not (isNull (box dock.VisibleDockables)) -> dock.VisibleDockables |> Seq.tryPick (find id)
        | _ -> None

/// Mirrors the restore sequence `App.axaml.fs` runs: normalize -> InitLayout
/// -> graft document tabs -> graft tool panes -> InitLayout again.
let private restoreLayout (factory: DockFactory) (serializer: Dock.Serializer.SystemTextJson.DockSerializer) (json: string) =
    let restored = serializer.Deserialize<IRootDock>(json)
    LayoutSerializer.normalize (restored :> IDockable)
    factory.InitLayout(restored)
    let documentsOk = factory.RestoreDocumentTabs(restored :> IDockable)
    let toolPanesOk = factory.RestoreToolPanes(restored :> IDockable)
    factory.InitLayout(restored)
    restored, documentsOk, toolPanesOk

[<Fact>]
let ``SerializeLayout then Deserialize round-trips a real layout without throwing`` () =
    let _, factory, layout = rebuild ()
    let serializer = LayoutSerializer.create ()

    let json = factory.SerializeLayout(serializer, layout :> IDock)
    let restored = serializer.Deserialize<IRootDock>(json)

    Assert.NotNull(restored)

[<Fact>]
let ``A Proportion change on a tool pane survives the round-trip`` () =
    let _, factory, layout = rebuild ()
    factory.ResultsDock.Proportion <- 0.42
    factory.WorkspaceDock.Proportion <- 0.17

    let serializer = LayoutSerializer.create ()
    let json = factory.SerializeLayout(serializer, layout :> IDock)
    let restored = serializer.Deserialize<IRootDock>(json)
    factory.InitLayout(restored)

    let restoredResults = find "ResultsPane" (restored :> IDockable)
    let restoredWorkspace = find "WorkspacePane" (restored :> IDockable)

    Assert.True(restoredResults.IsSome)
    Assert.True(restoredWorkspace.IsSome)
    Assert.Equal(0.42, restoredResults.Value.Proportion)
    Assert.Equal(0.17, restoredWorkspace.Value.Proportion)

[<Fact>]
let ``normalize repairs the ActiveDockable/VisibleDockables duplication a real round-trip introduces`` () =
    let _, factory, layout = rebuild ()
    let serializer = LayoutSerializer.create ()
    let json = factory.SerializeLayout(serializer, layout :> IDock)
    let restored = serializer.Deserialize<IRootDock>(json)

    // Reproduces the reviewer's finding directly: before normalize, the
    // nested-Serialize-per-list-boundary defect leaves root.ActiveDockable
    // as a structurally-identical but reference-distinct duplicate of
    // root.VisibleDockables.[0], not the same object.
    Assert.False(obj.ReferenceEquals(restored.ActiveDockable, restored.VisibleDockables.[0]))

    LayoutSerializer.normalize (restored :> IDockable)

    Assert.True(obj.ReferenceEquals(restored.ActiveDockable, restored.VisibleDockables.[0]))

    // The same duplication recurs at every IDock level, not just the root -
    // confirm a nested ToolDock is fixed too.
    match find "ResultsPane" (restored :> IDockable) with
    | Some(:? IDock as resultsPane) ->
        Assert.True(obj.ReferenceEquals(resultsPane.ActiveDockable, resultsPane.VisibleDockables.[0]))
    | _ -> Assert.Fail("expected to find the restored ResultsPane")

[<Fact>]
let ``RestoreDocumentTabs grafts the live DocumentDock back, restoring the real open tab`` () =
    let shell, factory, layout = rebuild ()
    let originalUri = shell.ActiveDocument.Uri

    let serializer = LayoutSerializer.create ()
    let json = factory.SerializeLayout(serializer, layout :> IDock)
    let restored = serializer.Deserialize<IRootDock>(json)
    factory.InitLayout(restored)

    let grafted = factory.RestoreDocumentTabs(restored :> IDockable)
    factory.InitLayout(restored)

    Assert.True(grafted)
    Assert.Single(factory.DocumentDock.VisibleDockables) |> ignore

    match factory.DocumentDock.VisibleDockables.[0] with
    | :? ScriptDocument as sd -> Assert.Equal(originalUri, sd.Document.Uri)
    | other -> Assert.Fail($"expected a ScriptDocument, got {other.GetType().FullName}")

[<Fact>]
let ``RestoreToolPanes repairs ResultsTool.Content and repoints DockFactory's cached fields`` () =
    let _, factory, layout = rebuild ()
    let liveResultsContent = resultsContentOf factory.ResultsTool
    let preRestoreResultsDock = factory.ResultsDock
    Assert.NotNull(liveResultsContent) // sanity: CreateLayout really did set it

    let serializer = LayoutSerializer.create ()
    let json = factory.SerializeLayout(serializer, layout :> IDock)
    let restored = serializer.Deserialize<IRootDock>(json)
    LayoutSerializer.normalize (restored :> IDockable)
    factory.InitLayout(restored)

    // Before the graft: the *restored* tree's own Results tool has Content
    // null (IgnoreDataMember, never on the wire) - it's a distinct object
    // from factory.ResultsTool at this point, since RestoreToolPanes
    // hasn't repointed the cached field yet.
    let restoredResultsDock =
        match find factory.ResultsDock.Id (restored :> IDockable) with
        | Some(:? IDock as d) -> d
        | _ -> failwith "expected to find the restored ResultsPane"

    match find factory.ResultsTool.Id (restored :> IDockable) with
    | Some restoredResultsToolBeforeGraft -> Assert.Null(resultsContentOf restoredResultsToolBeforeGraft)
    | None -> Assert.Fail("expected to find the restored Results tool")

    let ok = factory.RestoreToolPanes(restored :> IDockable)

    Assert.True(ok)
    Assert.Same(liveResultsContent, resultsContentOf factory.ResultsTool)
    // DockFactory's cached ResultsDock now points at the *restored*
    // instance (proven by reference, not by value - Proportion alone
    // wouldn't distinguish "repointed" from "coincidentally already
    // correct", since the pre-restore live ToolDock's own Proportion is
    // never touched by SerializeLayout in the first place), not the
    // stale pre-restore object.
    Assert.Same(restoredResultsDock, factory.ResultsDock :> IDockable)
    Assert.NotSame(preRestoreResultsDock, factory.ResultsDock)

[<Fact>]
let ``RestoreToolPanes returns false without mutating state when a pane Id is missing`` () =
    let _, factory, _ = rebuild ()
    let bareRoot = factory.CreateRootDock()
    bareRoot.VisibleDockables <- factory.CreateList<IDockable>()

    let beforeResultsDock = factory.ResultsDock
    let ok = factory.RestoreToolPanes(bareRoot :> IDockable)

    Assert.False(ok)
    Assert.Same(beforeResultsDock, factory.ResultsDock)

[<Fact>]
let ``Full restore sequence leaves ActiveDockable reachable from VisibleDockables and shows the real tab and Results content`` () =
    let shell, factory, layout = rebuild ()
    let originalUri = shell.ActiveDocument.Uri
    let liveResultsContent = resultsContentOf factory.ResultsTool

    let serializer = LayoutSerializer.create ()
    let json = factory.SerializeLayout(serializer, layout :> IDock)
    let restored, documentsOk, toolPanesOk = restoreLayout factory serializer json

    Assert.True(documentsOk)
    Assert.True(toolPanesOk)

    // Root-level duplication (the reviewer's Important finding) is fixed:
    // ActiveDockable is the *same* object as VisibleDockables.[0], not a
    // structurally-identical duplicate branch a real RootDockControl
    // would show instead of the correctly-grafted tree.
    Assert.True(obj.ReferenceEquals(restored.ActiveDockable, restored.VisibleDockables.[0]))

    // The restored DocumentDock's own ActiveDockable is the real, live
    // ScriptDocument that was grafted in - not an empty placeholder or a
    // duplicate - and it's reachable via VisibleDockables too.
    match find factory.DocumentDock.Id (restored :> IDockable) with
    | Some(:? IDock as restoredDocumentDock) ->
        Assert.True(obj.ReferenceEquals(restoredDocumentDock.ActiveDockable, restoredDocumentDock.VisibleDockables.[0]))

        match restoredDocumentDock.ActiveDockable with
        | :? ScriptDocument as sd -> Assert.Equal(originalUri, sd.Document.Uri)
        | other -> Assert.Fail($"expected the restored DocumentDock's ActiveDockable to be a ScriptDocument, got {other}")
    | _ -> Assert.Fail("expected to find the restored DocumentDock")

    Assert.Same(liveResultsContent, resultsContentOf factory.ResultsTool)

[<Fact>]
let ``A hidden tool pane still round-trips - CanRestore and RestoreToolPanes both find it`` () =
    // IFactory.HideDockable moves a tool out of VisibleDockables into
    // IRootDock.HiddenDockables - a FindById that only searched
    // VisibleDockables (the original round-3 bug) would treat this as
    // "missing" on every subsequent restore, discarding the ENTIRE saved
    // layout, not just the hidden pane's own state.
    let _, factory, layout = rebuild ()
    (factory :> IFactory).HideDockable(factory.WorkspaceTool) |> ignore

    let isHiddenBeforeSave =
        not (factory.WorkspaceDock.VisibleDockables |> Seq.exists (fun d -> obj.ReferenceEquals(d, factory.WorkspaceTool)))

    Assert.True(isHiddenBeforeSave) // sanity: HideDockable really did move it out

    let serializer = LayoutSerializer.create ()
    let json = factory.SerializeLayout(serializer, layout :> IDock)
    let restored = serializer.Deserialize<IRootDock>(json)
    LayoutSerializer.normalize (restored :> IDockable)
    factory.InitLayout(restored)

    Assert.True(factory.CanRestore(restored :> IDockable))

    let documentsOk = factory.RestoreDocumentTabs(restored :> IDockable)
    let toolPanesOk = factory.RestoreToolPanes(restored :> IDockable)

    Assert.True(documentsOk)
    Assert.True(toolPanesOk)

/// Is `target` still reachable from `root` through *any* child list a node
/// can hold dockables in - `VisibleDockables`, `HiddenDockables` or the four
/// pinned lists? (The test-side mirror of `DockFactory.ChildLists`; the
/// module-level `find` above deliberately only walks `VisibleDockables`.)
let private reachableAnywhere (target: IDockable) (root: IDockable) : bool =
    let childLists (dock: IDock) =
        [ dock.VisibleDockables ]
        @ (match dock with
           | :? IRootDock as r ->
               [ r.HiddenDockables
                 r.LeftPinnedDockables
                 r.RightPinnedDockables
                 r.TopPinnedDockables
                 r.BottomPinnedDockables ]
           | _ -> [])
        |> List.filter (fun l -> not (isNull (box l)))

    let rec walk (node: IDockable) =
        if obj.ReferenceEquals(node, target) then
            true
        else
            match node with
            | :? IDock as dock -> childLists dock |> List.exists (Seq.exists walk)
            | _ -> false

    walk root

/// Mirrors `MainWindow`'s View-menu toggle exactly (see
/// `toggleToolVisibility` there): visibility is a reference-identity check
/// against the `ToolDock`'s own `VisibleDockables`, and the un-hide side
/// calls `IFactory.RestoreDockable` with that same reference.
let private toggleToolVisibility (factory: DockFactory) (dock: IDock) (tool: IDockable) =
    let isVisible = dock.VisibleDockables |> Seq.exists (fun d -> obj.ReferenceEquals(d, tool))

    if isVisible then
        (factory :> IFactory).HideDockable(tool) |> ignore
    else
        (factory :> IFactory).RestoreDockable(tool) |> ignore

[<Fact>]
let ``A tool pane hidden at save time can actually be un-hidden again after a restore`` () =
    // `IFactory.RestoreDockable` puts a hidden dockable back by reading
    // `IDockable.OriginalOwner` - which is NOT part of the serialized wire
    // format, so a restored hidden tool comes back with it null. Without
    // `RestoreToolPanes` re-establishing it, the View-menu un-hide is
    // destructive: `RestoreDockable` removes the tool from
    // `HiddenDockables` and puts it nowhere, making the pane unreachable
    // from anywhere in the tree for the rest of the session (and the next
    // save then writes a layout missing the tool entirely, which the launch
    // after that rejects wholesale via `CanRestore`).
    let _, factory, layout = rebuild ()
    (factory :> IFactory).HideDockable(factory.WorkspaceTool) |> ignore

    let serializer = LayoutSerializer.create ()
    let json = factory.SerializeLayout(serializer, layout :> IDock)
    let restored, documentsOk, toolPanesOk = restoreLayout factory serializer json

    Assert.True(documentsOk)
    Assert.True(toolPanesOk)

    // It restored as hidden (round 3's fix) - not visible in its ToolDock,
    // but still present in the root's HiddenDockables.
    Assert.DoesNotContain(factory.WorkspaceDock.VisibleDockables, fun d -> obj.ReferenceEquals(d, factory.WorkspaceTool))
    Assert.Contains(restored.HiddenDockables, fun d -> obj.ReferenceEquals(d, factory.WorkspaceTool))

    // The actual regression: OriginalOwner must point at the *restored*
    // WorkspacePane, so RestoreDockable knows where to put the tool back.
    Assert.Same(factory.WorkspaceDock :> IDockable, factory.WorkspaceTool.OriginalOwner)
    // Set unconditionally, so the pane that was *visible* at save time is
    // equally un-hideable if the user hides it later this session.
    Assert.Same(factory.ResultsDock :> IDockable, factory.ResultsTool.OriginalOwner)

    // Drive the real un-hide path (same call MainWindow's View menu makes).
    toggleToolVisibility factory factory.WorkspaceDock factory.WorkspaceTool

    Assert.Contains(factory.WorkspaceDock.VisibleDockables, fun d -> obj.ReferenceEquals(d, factory.WorkspaceTool))
    Assert.DoesNotContain(restored.HiddenDockables, fun d -> obj.ReferenceEquals(d, factory.WorkspaceTool))

    // ...and toggling a second time hides it again rather than losing it,
    // so the pane is still reachable from the tree either way.
    toggleToolVisibility factory factory.WorkspaceDock factory.WorkspaceTool

    Assert.Contains(restored.HiddenDockables, fun d -> obj.ReferenceEquals(d, factory.WorkspaceTool))
    Assert.True(reachableAnywhere factory.WorkspaceTool (restored :> IDockable))

[<Fact>]
let ``CanRestore false leaves DockFactory's cached fields and documentDock.Proportion untouched`` () =
    // Reproduces the ordering bug directly: if RestoreDocumentTabs/
    // RestoreToolPanes ran unconditionally before checking whether the
    // OTHER one succeeded, a saved layout missing just one pane's Id
    // (an old/hand-edited ui-state.json - simulated here by renaming
    // "Documents" in the saved JSON) would still mutate DockFactory's
    // live state for the pane it DID find, even though the restore as a
    // whole gets rejected and the default layout stays on screen.
    // CanRestore, checked before either mutating call, must prevent that
    // entirely.
    let _, factory, layout = rebuild ()
    let preRestoreResultsDock = factory.ResultsDock
    let preRestoreWorkspaceDock = factory.WorkspaceDock
    let preRestoreResultsToolContent = resultsContentOf factory.ResultsTool
    let preRestoreDocumentProportion = factory.DocumentDock.Proportion

    let serializer = LayoutSerializer.create ()
    let json = factory.SerializeLayout(serializer, layout :> IDock)
    let corruptedJson = json.Replace("\"Documents\"", "\"DocumentsOld\"")

    let restored = serializer.Deserialize<IRootDock>(corruptedJson)
    LayoutSerializer.normalize (restored :> IDockable)
    factory.InitLayout(restored)

    Assert.False(factory.CanRestore(restored :> IDockable))

    // The real assertion: DockFactory's live state is exactly as it was
    // before any restore attempt - RestoreDocumentTabs/RestoreToolPanes
    // were never called at all, because App.axaml.fs's real flow (and
    // this test, mirroring it) gates them on CanRestore first.
    Assert.Same(preRestoreResultsDock, factory.ResultsDock)
    Assert.Same(preRestoreWorkspaceDock, factory.WorkspaceDock)
    Assert.Same(preRestoreResultsToolContent, resultsContentOf factory.ResultsTool)
    Assert.Equal(preRestoreDocumentProportion, factory.DocumentDock.Proportion)
