namespace FSharpQueryStudio.Ui

open System
open System.ComponentModel
open System.IO
open Avalonia.Controls
open Avalonia.Data
open Avalonia.Interactivity
open Avalonia.Markup.Xaml
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Editor

type MainWindow() as this =
    inherit Window()

    let mutable assetHost: AssetHost.Host option = None

    let shellViewModel = ShellViewModel()

    do
        AvaloniaXamlLoader.Load(this)
        this.DataContext <- shellViewModel

        let webView = this.FindControl<NativeWebView>("MonacoWebView")
        let assetsRoot = Path.Combine(AppContext.BaseDirectory, "EditorAssets")
        let host = AssetHost.start assetsRoot
        assetHost <- Some host
        webView.Source <- Uri(host.BaseUri, "editor-host.html")

        webView.WebMessageReceived.Add(fun args ->
            let envelope =
                System.Text.Json.JsonSerializer.Deserialize<EditorEnvelope>(args.Body, Serialization.options)

            match EditorBridge.decode envelope with
            | Ok(TextChanged(_documentUri, version, source)) ->
                shellViewModel.ActiveDocument.ApplyTextChanged(version, source)
            | Ok _otherRequest -> () // Wired as each lands in its own later task.
            | Result.Error _err -> ()) // Malformed message from the page - never crashes the host.

        let resultsGrid = this.FindControl<DataGrid>("ResultsGrid")

        let rebuildResultColumns () =
            resultsGrid.Columns.Clear()

            for column in ResultGridColumns.build shellViewModel.Result.Schema do
                resultsGrid.Columns.Add(
                    DataGridTextColumn(Header = column.Header, Binding = Binding(column.BindingPath))
                )

        (shellViewModel.Result :> INotifyPropertyChanged).PropertyChanged.Add(fun args ->
            if args.PropertyName = "Schema" then
                rebuildResultColumns ())

        let runButton = this.FindControl<Button>("RunButton")
        runButton.Click.Add(fun (_: RoutedEventArgs) -> shellViewModel.RunAsync() |> ignore)

        let cancelButton = this.FindControl<Button>("CancelButton")
        cancelButton.Click.Add(fun (_: RoutedEventArgs) -> shellViewModel.Cancel() |> ignore)

        let copyResultButton = this.FindControl<Button>("CopyResultButton")
        copyResultButton.Click.Add(fun (_: RoutedEventArgs) -> shellViewModel.CopyResultAsync() |> ignore)

        this.Closed.Add(fun _ ->
            match assetHost with
            | Some host -> (host :> IDisposable).Dispose()
            | None -> ())

    /// Exposed so the composition root (`App.axaml.fs`) can wire further
    /// hooks (`CheckAsync`, etc.) after construction, alongside
    /// `WireExecution` below.
    member _.ShellViewModel = shellViewModel

    /// Composition root: wires `shellViewModel`'s `ExecuteAsync`/`CancelAsync`
    /// to a real worker process — set once, from `App.axaml.fs`. Kept out of
    /// the constructor so headless/`Ui.Tests` construction of `MainWindow`
    /// never spawns a real worker process as a side effect.
    member _.WireExecution(executeAsync: string -> System.Threading.Tasks.Task<ResultEnvelope>, cancelAsync: unit -> System.Threading.Tasks.Task<unit>) =
        shellViewModel.ExecuteAsync <- Some executeAsync
        shellViewModel.CancelAsync <- Some cancelAsync
        shellViewModel.WorkerState.TryTransition(WorkerState.Ready) |> ignore
