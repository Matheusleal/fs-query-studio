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
open FSharpQueryStudio.Security
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

            let hoverAsync (documentUri: string, version: int, source: string, line: int, column: int) =
                AnalysisService.hover client documentUri version source line column

            mainWindow.ShellViewModel.HoverAsync <- Some hoverAsync

            let definitionAsync (documentUri: string, version: int, source: string, line: int, column: int) =
                AnalysisService.definition client documentUri version source line column

            mainWindow.ShellViewModel.DefinitionAsync <- Some definitionAsync

            let signatureHelpAsync (documentUri: string, version: int, source: string, line: int, column: int) =
                AnalysisService.signatureHelp client documentUri version source line column

            mainWindow.ShellViewModel.SignatureHelpAsync <- Some signatureHelpAsync

            let completeAsync (documentUri: string, version: int, source: string, line: int, column: int) =
                AnalysisService.complete client documentUri version source line column

            mainWindow.ShellViewModel.CompleteAsync <- Some completeAsync

            let renameAsync (documentUri: string, version: int, source: string, line: int, column: int) =
                AnalysisService.rename client documentUri version source line column

            mainWindow.ShellViewModel.RenameAsync <- Some renameAsync

            mainWindow.ShellViewModel.CopyResultToClipboard <-
                Some(fun text -> task { do! mainWindow.Clipboard.SetTextAsync(text) })

            let secretStore = WindowsCredentialStore() :> ISecretStore

            mainWindow.ShellViewModel.RegisterConnectionAsync <-
                Some(fun (definition, secret) -> ConnectionService.register client secretStore definition secret)

            mainWindow.ShellViewModel.RemoveConnectionAsync <-
                Some(fun definition -> ConnectionService.remove client secretStore definition)

            mainWindow.ShellViewModel.LoadConnectionSchemaAsync <- Some(fun id -> ConnectionService.schema client id)

            mainWindow.ShellViewModel.ExportCsvToFile <-
                Some(fun (documentUri, filePath) -> ExportService.exportCsv client documentUri filePath)

            mainWindow.ShellViewModel.DiscardResultsAsync <-
                Some(fun documentUri -> ExportService.discardResults client documentUri)

            // Populate `Connections` from whatever's already registered —
            // reads the same `workspace.json` the worker itself reads
            // fresh before every `execution/evaluate` (see
            // `Handlers.runEvaluate`), so what the UI shows at startup and
            // what a script can actually resolve always agree.
            let workspaceDir =
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FSharpQueryStudio"
                )

            mainWindow.ShellViewModel.SeedConnections(FSharpQueryStudio.Storage.Workspace.readConnections workspaceDir)

            // Every fresh session opens with the Welcome tab active (and
            // the usual blank Script1.fsx open behind it, from
            // ShellViewModel's own constructor) — see
            // docs/superpowers/specs/2026-08-12-welcome-page-design.md.
            // `OpenDocument`'s existing open-or-activate logic does all
            // the real work here; this is just the one call that
            // triggers it at the right point in startup.
            mainWindow.ShellViewModel.OpenDocument(mainWindow.ShellViewModel.WelcomeUri) |> ignore

            // D.7's packaged-artifact test launches the real published exe
            // as a black box - it has no way to assert on rendered window
            // content, so this log line is the signal it waits on to know
            // the shell actually reached a usable state rather than just
            // "the process didn't crash yet".
            mainWindow.Opened.Add(fun _ -> Log.Information("Host window ready"))

            // Persists the theme choice on close. `uiState.DockLayout` is
            // passed through unchanged — this fixed shell has no layout
            // of its own to save, but an older saved value is left alone
            // rather than cleared.
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
