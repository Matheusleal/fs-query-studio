namespace FSharpQueryStudio.Ui

open System
open Avalonia.Controls
open Avalonia.Markup.Xaml
open Avalonia.Threading
open FSharpQueryStudio.Logging

/// Hosts a `LogViewerViewModel`, driving its `Poll()` with a real
/// `DispatcherTimer` while the window is open — see
/// `docs/superpowers/specs/2026-08-12-log-viewer-design.md`. Everything
/// else (merging, filtering, live/static switching) lives in the view
/// model itself, already unit-tested (Task 3); this file's only real
/// logic is starting/stopping the timer.
type LogViewerWindow() as this =
    inherit Window()

    let viewModel = LogViewerViewModel(LoggerSetup.defaultLogsDirectory (), LogFileReader.readEntriesSince)

    do
        this.DataContext <- viewModel
        AvaloniaXamlLoader.Load(this)

        let timer = DispatcherTimer(Interval = TimeSpan.FromSeconds(1.0))
        timer.Tick.Add(fun _ -> viewModel.Poll())
        timer.Start()
        this.Closed.Add(fun _ -> timer.Stop())
