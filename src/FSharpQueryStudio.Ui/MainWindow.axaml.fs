namespace FSharpQueryStudio.Ui

open Avalonia.Controls
open Avalonia.Markup.Xaml

type MainWindow() as this =
    inherit Window()

    do
        AvaloniaXamlLoader.Load(this)
        this.DataContext <- ShellViewModel()
