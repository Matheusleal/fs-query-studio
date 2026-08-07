module LayoutSerializerTests

open Xunit
open Dock.Model.Core
open Dock.Model.Controls
open FSharpQueryStudio.Ui

/// Regression coverage for the Task 12 code-review finding: a plain
/// `DockSerializer()` throws `JsonException: A possible object cycle was
/// detected` on any real, live-built `DockFactory` layout (the installed
/// v12.1.0 `Dock.Serializer.SystemTextJson` package's own
/// `JsonConverterList<T>.Write` serializes each list element via a
/// nested `JsonSerializer.Serialize` call, which breaks
/// `ReferenceHandler.Preserve`'s cycle tracking across that boundary -
/// see `LayoutSerializer`'s doc comment). These tests build a layout the
/// same way `DockFactoryTests.fs` does and exercise the real
/// `LayoutSerializer.create()` / `DockFactory.SerializeLayout` /
/// `RestoreDocumentTabs` path end to end, so a future change that
/// reintroduces the cycle (or breaks the document-tab graft) fails a
/// test instead of only failing silently at app startup.

[<Fact>]
let ``SerializeLayout then Deserialize round-trips a real layout without throwing`` () =
    let shell = ShellViewModel()
    let factory = DockFactory(shell, obj (), obj ())
    let layout = factory.CreateLayout()
    factory.InitLayout(layout)

    let serializer = LayoutSerializer.create ()

    let json = factory.SerializeLayout(serializer, layout :> IDock)
    let restored = serializer.Deserialize<IRootDock>(json)

    Assert.NotNull(restored)

[<Fact>]
let ``A Proportion change on a tool pane survives the round-trip`` () =
    let shell = ShellViewModel()
    let factory = DockFactory(shell, obj (), obj ())
    let layout = factory.CreateLayout()
    factory.InitLayout(layout)

    factory.ResultsDock.Proportion <- 0.42
    factory.WorkspaceDock.Proportion <- 0.17

    let serializer = LayoutSerializer.create ()
    let json = factory.SerializeLayout(serializer, layout :> IDock)
    let restored = serializer.Deserialize<IRootDock>(json)
    factory.InitLayout(restored)

    let rec find (id: string) (node: IDockable) : IDockable option =
        if node.Id = id then
            Some node
        else
            match node with
            | :? IDock as dock when not (isNull (box dock.VisibleDockables)) ->
                dock.VisibleDockables |> Seq.tryPick (find id)
            | _ -> None

    let restoredResults = find "ResultsPane" (restored :> IDockable)
    let restoredWorkspace = find "WorkspacePane" (restored :> IDockable)

    Assert.True(restoredResults.IsSome)
    Assert.True(restoredWorkspace.IsSome)
    Assert.Equal(0.42, restoredResults.Value.Proportion)
    Assert.Equal(0.17, restoredWorkspace.Value.Proportion)

[<Fact>]
let ``RestoreDocumentTabs grafts the live DocumentDock back, restoring the real open tab`` () =
    let shell = ShellViewModel()
    let factory = DockFactory(shell, obj (), obj ())
    let layout = factory.CreateLayout()
    factory.InitLayout(layout)

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
