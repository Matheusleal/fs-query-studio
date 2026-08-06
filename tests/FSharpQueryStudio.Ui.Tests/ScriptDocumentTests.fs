module ScriptDocumentTests

open Xunit
open FSharpQueryStudio.Ui

[<Fact>]
let ``Title is the document's Uri when not dirty`` () =
    let shell = ShellViewModel()
    let doc = shell.ActiveDocument
    let scriptDoc = ScriptDocument(doc, shell, obj ())

    Assert.Equal(doc.Uri, scriptDoc.Title)

[<Fact>]
let ``Title gains a trailing marker once the document becomes dirty`` () =
    let shell = ShellViewModel()
    let doc = shell.ActiveDocument
    let scriptDoc = ScriptDocument(doc, shell, obj ())

    doc.ApplyTextChanged(1, "1 + 1")

    Assert.Equal(doc.Uri + " *", scriptDoc.Title)

[<Fact>]
let ``Id is the document's Uri`` () =
    let shell = ShellViewModel()
    let doc = shell.ActiveDocument
    let scriptDoc = ScriptDocument(doc, shell, obj ())

    Assert.Equal(doc.Uri, scriptDoc.Id)

[<Fact>]
let ``Context is the shared editor control instance passed to the constructor`` () =
    let shell = ShellViewModel()
    let editorControl = obj ()
    let scriptDoc = ScriptDocument(shell.ActiveDocument, shell, editorControl)

    Assert.Same(editorControl, scriptDoc.Context)

[<Fact>]
let ``OnClose returns ShellViewModel.CloseDocument's result and does not open the confirm hook when the document is clean`` () =
    let shell = ShellViewModel()
    let scriptDoc = ScriptDocument(shell.ActiveDocument, shell, obj ())

    Assert.True(scriptDoc.OnClose())

[<Fact>]
let ``OnClose returns false and keeps the tab when the document is dirty and the confirm hook denies`` () =
    let shell = ShellViewModel()
    shell.ActiveDocument.ApplyTextChanged(1, "1 + 1")
    shell.ConfirmDiscardDirtyDocument <- Some(fun _ -> false)
    let scriptDoc = ScriptDocument(shell.ActiveDocument, shell, obj ())

    Assert.False(scriptDoc.OnClose())
