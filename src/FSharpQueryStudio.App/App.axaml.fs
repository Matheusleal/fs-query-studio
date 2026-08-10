namespace FSharpQueryStudio.App

open System
open System.IO
open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Input.Platform
open Avalonia.Markup.Xaml
open Avalonia.Styling
open Serilog
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Ui

type App() =
    inherit Application()

    override this.Initialize() = AvaloniaXamlLoader.Load(this)

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            // UI-Foundation.12: load the persisted theme/layout choice
            // before building the window, so the saved theme can be
            // applied to a freshly-constructed `MainWindow` (which always
            // starts Dark) rather than flashing the default theme first.
            let appDataDir =
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FSharpQueryStudio"
                )

            let uiState = FSharpQueryStudio.Storage.UiState.read appDataDir

            let startupTheme =
                match uiState.Theme with
                | "Light" -> Theme.Light
                | _ -> Theme.Dark

            let mainWindow = MainWindow()

            if startupTheme = Theme.Light then
                mainWindow.ThemeService.Toggle()
                this.RequestedThemeVariant <- ThemeVariant.Light

            // Composition root (Section 4's App responsibility): the one
            // place a real WorkerClient gets constructed and wired into
            // the view models. currentRequestId is the same pattern
            // Integration.Tests' TestHelpers.wireShell uses - CancelAsync
            // closes over whatever id ExecuteAsync most recently sent,
            // without ShellViewModel itself knowing request ids exist.
            let workerDllPath =
                Path.Combine(AppContext.BaseDirectory, "FSharpQueryStudio.Worker.dll")

            let client = WorkerClient(workerDllPath)
            let currentRequestId = ref None

            let executeAsync (evalParams: EvaluateParams) =
                task {
                    let id = Guid.NewGuid().ToString()
                    currentRequestId.Value <- Some id
                    let! result = ExecutionService.evaluateWithId client id evalParams
                    currentRequestId.Value <- None
                    return result
                }

            let cancelAsync () =
                task {
                    match currentRequestId.Value with
                    | Some id -> do! client.Cancel(id, TimeSpan.FromSeconds(15.0))
                    | None -> ()
                }

            let resetSessionAsync (sessionId: string) = ExecutionService.resetSession client sessionId

            let checkAsync (version: int, source: string) =
                AnalysisService.check client mainWindow.ShellViewModel.ActiveDocument.Uri version source

            mainWindow.WireExecution(executeAsync, cancelAsync, resetSessionAsync)
            mainWindow.ShellViewModel.CheckAsync <- Some checkAsync

            mainWindow.ShellViewModel.CopyResultToClipboard <-
                Some(fun text -> task { do! mainWindow.Clipboard.SetTextAsync(text) })

            // D.7's packaged-artifact test launches the real published exe
            // as a black box - it has no way to assert on rendered window
            // content, so this log line is the signal it waits on to know
            // the shell actually reached a usable state rather than just
            // "the process didn't crash yet".
            mainWindow.Opened.Add(fun _ -> Log.Information("Host window ready"))

            // TEMP DIAGNOSTIC: Dock layout restore/persist disabled along
            // with Dock.Avalonia itself — see MainWindow.axaml's
            // `FixedShellGrid` comment and docs/DEVLOG.md. Theme
            // persistence only; `uiState.DockLayout` is passed through
            // unchanged so a real saved layout isn't destroyed by this
            // test.
            mainWindow.Closing.Add(fun _ ->
                let theme =
                    match mainWindow.ThemeService.Current with
                    | Theme.Light -> "Light"
                    | Theme.Dark -> "Dark"

                FSharpQueryStudio.Storage.UiState.write
                    appDataDir
                    { Theme = theme
                      DockLayout = uiState.DockLayout })

            desktop.MainWindow <- mainWindow
        | _ -> ()

        base.OnFrameworkInitializationCompleted()
