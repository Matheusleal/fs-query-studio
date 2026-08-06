namespace FSharpQueryStudio.Ui

open System.ComponentModel

/// UI-Foundation's dark/light theme choice — see the UI-Foundation spec's
/// theme section. Pure state, no Avalonia dependency: translating
/// `Current` into a real `ThemeVariant` on `Application.Current` happens
/// in the View layer (`MainWindow`'s View > Toggle Theme handler, as of
/// UI-Foundation.9), never here.
type Theme =
    | Dark
    | Light

type ThemeService() as self =
    let mutable current = Theme.Dark
    let propertyChanged = Event<PropertyChangedEventHandler, PropertyChangedEventArgs>()

    interface INotifyPropertyChanged with
        [<CLIEvent>]
        member _.PropertyChanged = propertyChanged.Publish

    member _.Current = current

    member _.Toggle() =
        current <-
            match current with
            | Theme.Dark -> Theme.Light
            | Theme.Light -> Theme.Dark

        propertyChanged.Trigger(self, PropertyChangedEventArgs(nameof self.Current))
