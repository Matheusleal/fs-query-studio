namespace FSharpQueryStudio.App

open System
open System.IO
open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Input.Platform
open Avalonia.Markup.Xaml
open Avalonia.Styling
open Dock.Model.Controls
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

            // Built once, used for both directions below. See
            // `LayoutSerializer`'s doc comment for why a plain
            // `DockSerializer()` can't be used as-is: it throws on any
            // real dock tree (a genuine reference-cycle bug in the
            // installed v12.1.0 package, reproduced empirically, not
            // merely suspected).
            let layoutSerializer = LayoutSerializer.create ()

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

            // UI-Foundation.12: restore a saved dock layout on top of the
            // default one `MainWindow`'s constructor already built. A
            // missing `DockLayout` (fresh install) or a corrupt/
            // incompatible one (older app version, hand-edited file) must
            // never block startup - the `try`/`with` falls back to the
            // default layout that's already in place either way.
            match uiState.DockLayout with
            | None -> ()
            | Some layoutJson ->
                try
                    let json = layoutJson.GetRawText()
                    let restored = layoutSerializer.Deserialize<IRootDock>(json)

                    match restored with
                    | null -> ()
                    | restored ->
                        mainWindow.DockFactory.InitLayout(restored)
                        // Document tabs never survive the JSON round-trip
                        // (see `DockFactory.SerializeLayout`'s doc
                        // comment) - graft the live `DocumentDock` (the
                        // real, still-open tab(s)) back into the restored
                        // structural tree before it becomes visible, then
                        // re-run InitLayout so the grafted node's
                        // Owner/Factory point into its new restored
                        // parent correctly.
                        mainWindow.DockFactory.RestoreDocumentTabs(restored) |> ignore
                        mainWindow.DockFactory.InitLayout(restored)
                        mainWindow.RestoreLayout(restored)
                with ex ->
                    Log.Warning(ex, "Failed to restore saved dock layout; using the default layout")

            // UI-Foundation.12: persist both the current theme and dock
            // layout on close, so the next launch can restore them via
            // `uiState` above. Layout serialization failure (e.g. a
            // future incompatible Dock.Avalonia upgrade) must not stop
            // the window from closing or lose the theme choice, so it's
            // isolated in its own `try`/`with` and downgrades to `None`
            // rather than propagating.
            mainWindow.Closing.Add(fun _ ->
                let layoutJson =
                    try
                        let json =
                            mainWindow.DockFactory.SerializeLayout(layoutSerializer, mainWindow.MainDockLayout)

                        Some(System.Text.Json.JsonDocument.Parse(json).RootElement)
                    with ex ->
                        Log.Warning(ex, "Failed to serialize dock layout for persistence")
                        None

                let theme =
                    match mainWindow.ThemeService.Current with
                    | Theme.Light -> "Light"
                    | Theme.Dark -> "Dark"

                FSharpQueryStudio.Storage.UiState.write appDataDir { Theme = theme; DockLayout = layoutJson })

            desktop.MainWindow <- mainWindow
        | _ -> ()

        base.OnFrameworkInitializationCompleted()
