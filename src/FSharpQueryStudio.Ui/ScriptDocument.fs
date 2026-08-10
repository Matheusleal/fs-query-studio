namespace FSharpQueryStudio.Ui

open System.ComponentModel
open Dock.Model.Controls
open Dock.Model.Mvvm.Controls

/// Wraps one `DocumentViewModel` as a Dock.Avalonia document tab.
///
/// `editorContent` (this tab's own Monaco `NativeWebView` — one per open
/// script tab, not shared; see `DockFactory`'s doc comment for why a
/// single shared instance doesn't work) is exposed two ways:
/// `EditorControl`, a plain member the composition root (`MainWindow`)
/// reads directly, and the `IDocumentContent.Content` interface
/// implementation below, which is the one Dock's own rendering actually
/// looks for.
///
/// That second one is not optional decoration — it is the fix for a real,
/// confirmed-via-reflection bug. `Dock.Model.Mvvm.Controls.Document`'s
/// settable `Context: obj` property looks like the obvious place to put a
/// tab's visual content, and earlier work in this project did exactly
/// that. But Dock's real default content rendering
/// (`Dock.Avalonia.Internal.DockDataTemplateHelper.
/// CreateDefaultDataTemplates`, on the installed `Dock.Avalonia` 12.1.0 —
/// confirmed by loading it and calling it directly, not by reading docs)
/// resolves a document's on-screen content by checking whether the
/// dockable implements `Dock.Model.Controls.IDocumentContent`, and does
/// not inspect `Context` at all. A plain `Document` with only `Context`
/// set matches none of Dock's built-in templates and renders as empty
/// background — which is exactly the "Monaco just doesn't appear" bug
/// this fixes. `Context` itself is left unused now; `IDocumentContent`
/// is the real contract.
///
/// `OnClose` routes entirely through `ShellViewModel.CloseDocument` so
/// the existing dirty-tab confirmation flow
/// (`ShellViewModel.ConfirmDiscardDirtyDocument`) stays the single source
/// of truth for "can this tab actually close" — Dock's own tab removal
/// only proceeds when that call returns true.
type ScriptDocument(document: DocumentViewModel, shell: ShellViewModel, editorContent: obj) as this =
    inherit Document()

    let updateTitle () =
        this.Title <- document.Uri + (if document.IsDirty then " *" else "")

    do
        this.Id <- document.Uri
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

    interface IDocumentContent with
        member _.Content
            with get () = editorContent
            and set _ = () // Dock never sets this; the WebView is ours to own.

    member _.Document = document

    /// This tab's own `NativeWebView`, for code that needs the control
    /// itself rather than going through the `IDocumentContent` interface
    /// (`MainWindow`'s bridge-push call sites) — the same object
    /// `IDocumentContent.Content` returns, just without the cast.
    member _.EditorControl = editorContent

    override _.OnClose() = shell.CloseDocument(document.Uri)
