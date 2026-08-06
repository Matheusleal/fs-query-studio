namespace FSharpQueryStudio.Ui

open System
open System.ComponentModel
open FSharpQueryStudio.Protocol

/// See Section 5's core view models. Cursor location and execution state
/// join later, alongside the tasks that actually need them.
///
/// `scheduler` defaults to a real timer; tests inject a fake/virtual one
/// to drive `DebouncedTextChanged` deterministically.
type DocumentViewModel(uri: string, ?scheduler: IDebounceScheduler) as self =
    let mutable text = ""
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

    member _.Uri = uri
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

    /// Applies an `analysis/check` response only if its version exactly
    /// matches the current document version — Section 7's stale-response
    /// guard. A response for an older version arriving after a newer
    /// one already landed (out-of-order delivery) is silently discarded,
    /// never regressing what's shown.
    member _.ApplyAnalysisResult(responseVersion: int, newDiagnostics: Diagnostic list) =
        if responseVersion = version then
            diagnostics <- newDiagnostics
            propertyChanged.Trigger(self, PropertyChangedEventArgs(nameof self.Diagnostics))
