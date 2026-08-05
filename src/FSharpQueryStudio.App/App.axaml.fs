namespace FSharpQueryStudio.App

open System
open System.IO
open Avalonia
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Markup.Xaml
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

            let executeAsync (source: string) =
                task {
                    let id = Guid.NewGuid().ToString()
                    currentRequestId.Value <- Some id
                    let! result = ExecutionService.evaluateWithId client id source
                    currentRequestId.Value <- None
                    return result
                }

            let cancelAsync () =
                task {
                    match currentRequestId.Value with
                    | Some id -> do! client.Cancel(id, TimeSpan.FromSeconds(15.0))
                    | None -> ()
                }

            mainWindow.WireExecution(executeAsync, cancelAsync)
            desktop.MainWindow <- mainWindow
        | _ -> ()

        base.OnFrameworkInitializationCompleted()
