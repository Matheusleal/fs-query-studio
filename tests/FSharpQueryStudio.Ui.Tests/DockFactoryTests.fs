module DockFactoryTests

open Xunit
open Dock.Model.Core
open FSharpQueryStudio.Ui

let private scriptDocuments (factory: DockFactory) : ScriptDocument list =
    factory.DocumentDock.VisibleDockables
    |> Seq.choose (function
        | :? ScriptDocument as sd -> Some sd
        | _ -> None)
    |> List.ofSeq

[<Fact>]
let ``CreateLayout produces one ScriptDocument for the shell's initial ActiveDocument`` () =
    let shell = ShellViewModel()
    let factory = DockFactory(shell, (fun () -> obj ()), obj ())
    let layout = factory.CreateLayout()
    factory.InitLayout(layout)

    let docs = scriptDocuments factory
    Assert.Single(docs) |> ignore
    Assert.Equal(shell.ActiveDocument.Uri, docs.Head.Document.Uri)

[<Fact>]
let ``OpenDocument on a new uri adds a second ScriptDocument and updates ShellViewModel.Documents`` () =
    let shell = ShellViewModel()
    let factory = DockFactory(shell, (fun () -> obj ()), obj ())
    let layout = factory.CreateLayout()
    factory.InitLayout(layout)

    factory.OpenDocument("untitled:Script2.fsx")

    Assert.Equal(2, (scriptDocuments factory).Length)
    Assert.Equal(2, shell.Documents.Length)

[<Fact>]
let ``OpenDocument on an already-open uri does not duplicate the tab`` () =
    let shell = ShellViewModel()
    let factory = DockFactory(shell, (fun () -> obj ()), obj ())
    let layout = factory.CreateLayout()
    factory.InitLayout(layout)

    factory.OpenDocument("untitled:Script2.fsx")
    factory.OpenDocument("untitled:Script2.fsx")

    Assert.Equal(2, (scriptDocuments factory).Length)

[<Fact>]
let ``DocumentActivated fires the newly active document when SetActiveDockable switches tabs`` () =
    let shell = ShellViewModel()
    let factory = DockFactory(shell, (fun () -> obj ()), obj ())
    let layout = factory.CreateLayout()
    factory.InitLayout(layout)
    factory.OpenDocument("untitled:Script2.fsx")

    let firstDoc = (scriptDocuments factory).Head
    let mutable activated: ScriptDocument option = None
    factory.DocumentActivated.Add(fun sd -> activated <- Some sd)

    factory.SetActiveDockable(firstDoc) |> ignore

    Assert.Equal(Some firstDoc.Document.Uri, activated |> Option.map (fun sd -> sd.Document.Uri))
    Assert.Equal(firstDoc.Document.Uri, shell.ActiveDocument.Uri)

// A tab added straight to `VisibleDockables` keeps a null `Owner`, and
// `CloseDockable` resolves the dock to remove a dockable from *through*
// `Owner` - so such a tab could never be closed again, the X button just
// did nothing. Only the single tab `CreateLayout`/`InitLayout` builds was
// ever wired, which is why every File > New / File > Open tab was silently
// un-closeable. `DockFactory` now routes both through `AddDockable`.

[<Fact>]
let ``a tab opened via OpenDocument is wired with an Owner and a Factory`` () =
    let shell = ShellViewModel()
    let factory = DockFactory(shell, (fun () -> obj ()), obj ())
    let layout = factory.CreateLayout()
    factory.InitLayout(layout)

    factory.OpenDocument("untitled:Script2.fsx")

    let opened = scriptDocuments factory |> List.find (fun sd -> sd.Document.Uri = "untitled:Script2.fsx")
    Assert.NotNull(opened.Owner)
    Assert.Equal(factory.DocumentDock.Id, opened.Owner.Id)
    Assert.NotNull(opened.Factory)

[<Fact>]
let ``a tab opened via OpenDocument can actually be closed again`` () =
    let shell = ShellViewModel()
    let factory = DockFactory(shell, (fun () -> obj ()), obj ())
    let layout = factory.CreateLayout()
    factory.InitLayout(layout)
    factory.OpenDocument("untitled:Script2.fsx")

    let opened = scriptDocuments factory |> List.find (fun sd -> sd.Document.Uri = "untitled:Script2.fsx")
    (factory :> IFactory).CloseDockable(opened)

    Assert.Equal<string list>(
        [ "untitled:Script1.fsx" ],
        scriptDocuments factory |> List.map (fun sd -> sd.Document.Uri)
    )

// `ShellViewModel.CloseDocument` replaces the document list with a fresh
// `untitled:Script1.fsx` when the last tab closes, so `ActiveDocument`
// always points at something. Dock knows nothing about that replacement,
// so without the factory's `DockableClosed` repair the document area went
// empty while `ActiveDocument` pointed at a document with no tab at all -
// and Run would have silently executed that invisible document.

[<Fact>]
let ``closing the last document tab leaves a tab for the shell's replacement document`` () =
    let shell = ShellViewModel()
    let factory = DockFactory(shell, (fun () -> obj ()), obj ())
    let layout = factory.CreateLayout()
    factory.InitLayout(layout)

    let onlyTab = (scriptDocuments factory).Head
    (factory :> IFactory).CloseDockable(onlyTab)

    let tabs = scriptDocuments factory
    Assert.Single(tabs) |> ignore
    // The tab must wrap the *replacement* document the shell created, not
    // the one that was just closed.
    Assert.NotSame(onlyTab, tabs.Head)
    Assert.Same(shell.ActiveDocument, tabs.Head.Document)

[<Fact>]
let ``the document area is never left without a tab for the active document`` () =
    let shell = ShellViewModel()
    let factory = DockFactory(shell, (fun () -> obj ()), obj ())
    let layout = factory.CreateLayout()
    factory.InitLayout(layout)
    factory.OpenDocument("untitled:Script2.fsx")

    // Close both tabs one after the other: the first close leaves a
    // sibling behind and must not fabricate anything, the second is the
    // last-tab case.
    for tab in scriptDocuments factory do
        (factory :> IFactory).CloseDockable(tab)

    let tabs = scriptDocuments factory
    Assert.Single(tabs) |> ignore
    Assert.Same(shell.ActiveDocument, tabs.Head.Document)
    Assert.Single(shell.Documents) |> ignore

[<Fact>]
let ``closing a non-last document tab does not fabricate a replacement tab`` () =
    let shell = ShellViewModel()
    let factory = DockFactory(shell, (fun () -> obj ()), obj ())
    let layout = factory.CreateLayout()
    factory.InitLayout(layout)
    factory.OpenDocument("untitled:Script2.fsx")

    let firstTab = (scriptDocuments factory) |> List.find (fun sd -> sd.Document.Uri = "untitled:Script1.fsx")
    (factory :> IFactory).CloseDockable(firstTab)

    let tabs = scriptDocuments factory
    Assert.Single(tabs) |> ignore
    Assert.Equal("untitled:Script2.fsx", tabs.Head.Document.Uri)

[<Fact>]
let ``a declined close on a dirty last tab keeps the original tab and adds nothing`` () =
    let shell = ShellViewModel()
    let factory = DockFactory(shell, (fun () -> obj ()), obj ())
    let layout = factory.CreateLayout()
    factory.InitLayout(layout)
    shell.ActiveDocument.ApplyTextChanged(1, "unsaved")
    shell.ConfirmDiscardDirtyDocument <- Some(fun _ -> false)

    let onlyTab = (scriptDocuments factory).Head
    (factory :> IFactory).CloseDockable(onlyTab)

    // `OnClose` returned false, so Dock never removed the tab and the
    // repair must not have run either - a declined close that spawned a
    // second tab would be worse than the bug it fixes.
    let tabs = scriptDocuments factory
    Assert.Single(tabs) |> ignore
    Assert.Same(onlyTab, tabs.Head)

[<Fact>]
let ``the tab created for a replacement document can itself be closed again`` () =
    let shell = ShellViewModel()
    let factory = DockFactory(shell, (fun () -> obj ()), obj ())
    let layout = factory.CreateLayout()
    factory.InitLayout(layout)

    // Guards against the repair producing a tab that Dock can't manage
    // (missing Owner/Factory wiring would surface as a throw here).
    (factory :> IFactory).CloseDockable((scriptDocuments factory).Head)
    (factory :> IFactory).CloseDockable((scriptDocuments factory).Head)

    Assert.Single(scriptDocuments factory) |> ignore
    Assert.Same(shell.ActiveDocument, (scriptDocuments factory).Head.Document)

// A single shared `NativeWebView` was tried first (Task 5-9) and threw
// `InvalidOperationException: ... already has a visual parent ...` the
// moment a second tab existed - Dock builds a distinct `ContentControl`
// per dockable, and an Avalonia `Control` can only ever be parented under
// one of them at a time. `DockFactory` now calls `createEditorControl`
// fresh for every tab it creates, so each `ScriptDocument.EditorControl`
// is its own, never-shared object.

[<Fact>]
let ``every open document tab gets its own editor control instance, never shared`` () =
    let shell = ShellViewModel()
    let mutable created = 0

    let createEditorControl () =
        created <- created + 1
        box created

    let factory = DockFactory(shell, createEditorControl, obj ())
    let layout = factory.CreateLayout()
    factory.InitLayout(layout)

    factory.OpenDocument("untitled:Script2.fsx")
    factory.OpenDocument("untitled:Script3.fsx")

    let editorControls = scriptDocuments factory |> List.map (fun sd -> sd.EditorControl)
    Assert.Equal(3, created)
    Assert.Equal<int list>([ 1; 2; 3 ], editorControls |> List.map (fun c -> c :?> int) |> List.sort)
    Assert.Equal(3, editorControls |> List.distinct |> List.length)

[<Fact>]
let ``HideDockable then RestoreDockable round-trips the Results tool back into ResultsDock`` () =
    let shell = ShellViewModel()
    let factory = DockFactory(shell, (fun () -> obj ()), obj ())
    let layout = factory.CreateLayout()
    factory.InitLayout(layout)

    let isVisible () =
        factory.ResultsDock.VisibleDockables
        |> Seq.exists (fun d -> System.Object.ReferenceEquals(d, factory.ResultsTool))

    Assert.True(isVisible ())

    (factory :> IFactory).HideDockable(factory.ResultsTool) |> ignore
    Assert.False(isVisible ())

    (factory :> IFactory).RestoreDockable(factory.ResultsTool) |> ignore
    Assert.True(isVisible ())

[<Fact>]
let ``HideDockable then RestoreDockable round-trips the Workspace tool back into WorkspaceDock`` () =
    let shell = ShellViewModel()
    let factory = DockFactory(shell, (fun () -> obj ()), obj ())
    let layout = factory.CreateLayout()
    factory.InitLayout(layout)

    let isVisible () =
        factory.WorkspaceDock.VisibleDockables
        |> Seq.exists (fun d -> System.Object.ReferenceEquals(d, factory.WorkspaceTool))

    Assert.True(isVisible ())

    (factory :> IFactory).HideDockable(factory.WorkspaceTool) |> ignore
    Assert.False(isVisible ())

    (factory :> IFactory).RestoreDockable(factory.WorkspaceTool) |> ignore
    Assert.True(isVisible ())
