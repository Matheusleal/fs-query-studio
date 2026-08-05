namespace FSharpQueryStudio.Ui

open System.ComponentModel

/// See Section 5's core view models. Cursor location and execution state
/// join later, alongside the tasks that actually need them.
type DocumentViewModel(uri: string) as self =
    let mutable text = ""
    let mutable version = 0
    let mutable isDirty = false
    let propertyChanged = Event<PropertyChangedEventHandler, PropertyChangedEventArgs>()

    interface INotifyPropertyChanged with
        [<CLIEvent>]
        member _.PropertyChanged = propertyChanged.Publish

    member _.Uri = uri
    member _.Text = text
    member _.Version = version
    member _.IsDirty = isDirty

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
