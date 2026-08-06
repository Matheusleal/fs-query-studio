namespace FSharpQueryStudio.App

open System
open System.IO
open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Input.Platform
open Avalonia.Markup.Xaml
open Serilog
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Ui

type App() =
    inherit Application()

    override this.Initialize() = AvaloniaXamlLoader.Load(this)

    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as desktop ->
            let mainWindow = MainWindow()

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

            desktop.MainWindow <- mainWindow
        | _ -> ()

        base.OnFrameworkInitializationCompleted()
