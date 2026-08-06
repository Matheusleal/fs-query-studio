namespace FSharpQueryStudio.Ui

open System.ComponentModel

/// UI-Foundation's dark/light theme choice — see the UI-Foundation spec's
/// theme section. Pure state, no Avalonia dependency: the composition
/// root (`App.axaml.fs`) is the one place that translates `Current` into
/// a real `ThemeVariant` on `Application.Current`.
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
