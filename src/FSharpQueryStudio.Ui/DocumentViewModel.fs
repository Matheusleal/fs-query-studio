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
