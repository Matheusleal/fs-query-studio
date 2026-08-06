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
    let factory = DockFactory(shell, obj (), obj ())
    let layout = factory.CreateLayout()
    factory.InitLayout(layout)

    let docs = scriptDocuments factory
    Assert.Single(docs) |> ignore
    Assert.Equal(shell.ActiveDocument.Uri, docs.Head.Document.Uri)

[<Fact>]
let ``OpenDocument on a new uri adds a second ScriptDocument and updates ShellViewModel.Documents`` () =
    let shell = ShellViewModel()
    let factory = DockFactory(shell, obj (), obj ())
    let layout = factory.CreateLayout()
    factory.InitLayout(layout)

    factory.OpenDocument("untitled:Script2.fsx")

    Assert.Equal(2, (scriptDocuments factory).Length)
    Assert.Equal(2, shell.Documents.Length)

[<Fact>]
let ``OpenDocument on an already-open uri does not duplicate the tab`` () =
    let shell = ShellViewModel()
    let factory = DockFactory(shell, obj (), obj ())
    let layout = factory.CreateLayout()
    factory.InitLayout(layout)

    factory.OpenDocument("untitled:Script2.fsx")
    factory.OpenDocument("untitled:Script2.fsx")

    Assert.Equal(2, (scriptDocuments factory).Length)

[<Fact>]
let ``DocumentActivated fires the newly active document when SetActiveDockable switches tabs`` () =
    let shell = ShellViewModel()
    let factory = DockFactory(shell, obj (), obj ())
    let layout = factory.CreateLayout()
    factory.InitLayout(layout)
    factory.OpenDocument("untitled:Script2.fsx")

    let firstDoc = (scriptDocuments factory).Head
    let mutable activated: DocumentViewModel option = None
    factory.DocumentActivated.Add(fun doc -> activated <- Some doc)

    factory.SetActiveDockable(firstDoc) |> ignore

    Assert.Equal(Some firstDoc.Document.Uri, activated |> Option.map (fun d -> d.Uri))
    Assert.Equal(firstDoc.Document.Uri, shell.ActiveDocument.Uri)

[<Fact>]
let ``HideDockable then RestoreDockable round-trips the Results tool back into ResultsDock`` () =
    let shell = ShellViewModel()
    let factory = DockFactory(shell, obj (), obj ())
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
    let factory = DockFactory(shell, obj (), obj ())
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
