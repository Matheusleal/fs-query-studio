namespace FSharpQueryStudio.Ui

open System.ComponentModel
open Dock.Model.Mvvm.Controls

/// Wraps one `DocumentViewModel` as a Dock.Avalonia document tab. `Context`
/// holds this tab's own Monaco `NativeWebView` instance — one per open
/// script tab, not shared (a single shared instance was tried first and
/// threw `InvalidOperationException: ... already has a visual parent ...`
/// the moment a second tab existed, since Dock builds a distinct
/// `ContentControl` per dockable and an Avalonia `Control` can only ever
/// have one visual parent at a time). Each tab's `NativeWebView` gets its
/// content set once, right after `DockFactory` raises `DocumentActivated`
/// for it, via a `DocumentLoaded` bridge notification.
///
/// `OnClose` routes entirely through `ShellViewModel.CloseDocument` so
/// the existing dirty-tab confirmation flow
/// (`ShellViewModel.ConfirmDiscardDirtyDocument`) stays the single source
/// of truth for "can this tab actually close" — Dock's own tab removal
/// only proceeds when that call returns true.
///
/// The real installed `Dock.Model.Mvvm.Controls.Document` (v12.1.0) has no
/// `Content` property — `Id`/`Title`/`OnClose()` are inherited from
/// `Dock.Model.Mvvm.Core.DockableBase` exactly as expected, but the
/// settable `obj` payload slot on a dockable is called `Context` (also
/// inherited from `DockableBase`/`IDockable`), so `editorContent` is
/// assigned there instead.
type ScriptDocument(document: DocumentViewModel, shell: ShellViewModel, editorContent: obj) as this =
    inherit Document()

    let updateTitle () =
        this.Title <- document.Uri + (if document.IsDirty then " *" else "")

    do
        this.Id <- document.Uri
        this.Context <- editorContent
        updateTitle ()

        (document :> INotifyPropertyChanged).PropertyChanged.Add(fun args ->
            if args.PropertyName = nameof document.IsDirty then
                updateTitle ()
            // `DocumentViewModel.MarkSaved` retags an `untitled:` document
            // to its real path once it's been written to disk. `Id` was
            // snapshotted from `Uri` at construction, and
            // `DockFactory.OpenDocument`'s dedupe compares
            // `sd.Document.Uri` against the *live* value — so leaving `Id`
            // stale would make a re-open of the saved path miss this tab
            // and duplicate it. Re-key both here.
            elif args.PropertyName = nameof document.Uri then
                this.Id <- document.Uri
                updateTitle ())

    member _.Document = document

    override _.OnClose() = shell.CloseDocument(document.Uri)
