namespace FSharpQueryStudio.Ui

open System
open System.ComponentModel
open FSharpQueryStudio.Protocol

/// See Section 5's core view models. Cursor location and execution state
/// join later, alongside the tasks that actually need them.
///
/// `scheduler` defaults to a real timer; tests inject a fake/virtual one
/// to drive `DebouncedTextChanged` deterministically.
///
/// `initialText`, when given, seeds `Text` directly — bypassing
/// `ApplyTextChanged` — so a freshly created document can start with
/// starter content (see `ShellViewModel.createDocument`) without being
/// marked dirty for text nobody actually typed. `Version` stays `0`,
/// matching the page's own `currentVersion` on a fresh Monaco load, so
/// the guard in `ApplyTextChanged` still accepts the user's first real
/// edit normally.
type DocumentViewModel(uri: string, ?scheduler: IDebounceScheduler, ?initialText: string) as self =
    // Mutable because `MarkSaved` can retag an `untitled:` document to the
    // real path it was just written to — see its doc comment.
    let mutable currentUri = uri
    let mutable text = defaultArg initialText ""
    let mutable version = 0
    let mutable isDirty = false
    let mutable diagnostics: Diagnostic list = []
    let mutable sessionMode = Isolated
    let mutable hasActiveSession = false
    let propertyChanged = Event<PropertyChangedEventHandler, PropertyChangedEventArgs>()
    let debouncedTextChanged = Event<int * string>()

    let debouncer =
        Debouncer(TimeSpan.FromMilliseconds(400.0), defaultArg scheduler (RealDebounceScheduler()))

    interface INotifyPropertyChanged with
        [<CLIEvent>]
        member _.PropertyChanged = propertyChanged.Publish

    member _.Uri = currentUri
    member _.Text = text
    member _.Version = version
    member _.IsDirty = isDirty
    member _.Diagnostics = diagnostics

    /// Isolated (Milestone A's default) or opt-in per-tab Persistent
    /// (E.1) — `Uri` doubles as the `SessionId` a Persistent-mode
    /// evaluation is keyed by, since it's already stable for this tab's
    /// lifetime and unique among open tabs. Switching modes clears
    /// `HasActiveSession`: whatever "active" meant under the old mode no
    /// longer applies once the mode itself has changed.
    member _.SessionMode
        with get () = sessionMode
        and set value =
            if value <> sessionMode then
                sessionMode <- value
                hasActiveSession <- false
                propertyChanged.Trigger(self, PropertyChangedEventArgs(nameof self.SessionMode))
                propertyChanged.Trigger(self, PropertyChangedEventArgs(nameof self.HasActiveSession))
                propertyChanged.Trigger(self, PropertyChangedEventArgs(nameof self.SessionStateDisplay))

    /// True once this tab has completed at least one Persistent-mode
    /// execution since the last mode switch or `ResetSession` — the E.2
    /// "session-state display" signal for whether bindings are currently
    /// live worker-side. Always false in Isolated mode.
    member _.HasActiveSession = hasActiveSession

    /// Short human-readable session state for E.2's UI display.
    member this.SessionStateDisplay =
        match this.SessionMode, hasActiveSession with
        | Isolated, _ -> "Isolated"
        | Persistent, true -> "Persistent · active"
        | Persistent, false -> "Persistent · reset"

    /// Records that a Persistent-mode execution just completed — called
    /// by `ShellViewModel.RunAsync` after a successful run. A no-op
    /// signal-wise in Isolated mode (nothing meaningful to mark active).
    member _.MarkSessionActive() =
        if sessionMode = Persistent && not hasActiveSession then
            hasActiveSession <- true
            propertyChanged.Trigger(self, PropertyChangedEventArgs(nameof self.HasActiveSession))
            propertyChanged.Trigger(self, PropertyChangedEventArgs(nameof self.SessionStateDisplay))

    /// Clears this tab's local "active" display state — called by
    /// `ShellViewModel.ResetSessionAsync` after the worker-side
    /// `session/reset` call completes. Idempotent: resetting an already-
    /// inactive session is a no-op, not an error.
    member _.MarkSessionReset() =
        if hasActiveSession then
            hasActiveSession <- false
            propertyChanged.Trigger(self, PropertyChangedEventArgs(nameof self.HasActiveSession))
            propertyChanged.Trigger(self, PropertyChangedEventArgs(nameof self.SessionStateDisplay))

    /// Fires once, debounced 300-500ms per Section 8's analysis pipeline,
    /// after the last of a burst of `ApplyTextChanged` calls — the trigger
    /// a later task uses to send `analysis/check` with the settled
    /// version/text rather than every intermediate keystroke.
    [<CLIEvent>]
    member _.DebouncedTextChanged = debouncedTextChanged.Publish

    /// Applies a bridge `TextChanged` message. Ignores anything whose
    /// version isn't strictly greater than the current one — the same
    /// stale-response defense Section 7 describes for analysis results,
    /// applied here to the document's own version counter so an
    /// out-of-order message can never regress it.
    member _.ApplyTextChanged(newVersion: int, source: string) =
        if newVersion > version then
            version <- newVersion
            text <- source
            isDirty <- true
            propertyChanged.Trigger(self, PropertyChangedEventArgs(nameof self.Version))
            propertyChanged.Trigger(self, PropertyChangedEventArgs(nameof self.Text))
            propertyChanged.Trigger(self, PropertyChangedEventArgs(nameof self.IsDirty))
            debouncer.Notify(fun () -> debouncedTextChanged.Trigger(version, text))

    /// Marks this document clean after a *successful* write to disk, and
    /// — when `newUri` is `Some` — retags `Uri` to the path it was
    /// actually written to. Reached from `MainWindow`'s save handlers via
    /// `ShellViewModel.MarkDocumentSaved`, which wraps this call so a retag
    /// also re-keys the editor page's own URI tracking; prefer that wrapper
    /// over calling this directly from a save path.
    ///
    /// The retag is what makes "save an `untitled:` tab through the file
    /// picker, then hit Save again" write straight through instead of
    /// re-prompting forever: `Uri` is the only thing the save handler
    /// distinguishes a scratch tab from a file-backed one by. `Uri` also
    /// doubles as this tab's Persistent-mode `SessionId` and as the tab
    /// strip's label, so both `Uri` and `IsDirty` raise `PropertyChanged`
    /// — `MainWindow`'s `rebuildTabs` listens for both to keep the strip
    /// and its dirty marker (`*`) in sync.
    ///
    /// Pass `None` for a document that already had a real path (a plain
    /// re-save): there's nothing to retag, only the dirty flag to clear.
    /// This deliberately does *not* touch `Version` — the version counter
    /// tracks edits for the analysis pipeline's stale-response guard, and
    /// saving is not an edit.
    member _.MarkSaved(newUri: string option) =
        match newUri with
        | Some path when path <> currentUri ->
            currentUri <- path
            propertyChanged.Trigger(self, PropertyChangedEventArgs(nameof self.Uri))
        | _ -> ()

        if isDirty then
            isDirty <- false
            propertyChanged.Trigger(self, PropertyChangedEventArgs(nameof self.IsDirty))

    /// Applies an `analysis/check` response only if its version exactly
    /// matches the current document version — Section 7's stale-response
    /// guard. A response for an older version arriving after a newer
    /// one already landed (out-of-order delivery) is silently discarded,
    /// never regressing what's shown.
    member _.ApplyAnalysisResult(responseVersion: int, newDiagnostics: Diagnostic list) =
        if responseVersion = version then
            diagnostics <- newDiagnostics
            propertyChanged.Trigger(self, PropertyChangedEventArgs(nameof self.Diagnostics))
