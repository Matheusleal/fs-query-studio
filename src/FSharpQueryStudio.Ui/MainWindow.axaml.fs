namespace FSharpQueryStudio.Ui

open System
open System.IO
open Avalonia.Controls
open Avalonia.Markup.Xaml
open FSharpQueryStudio.Editor

type MainWindow() as this =
    inherit Window()

    let mutable assetHost: AssetHost.Host option = None

    do
        AvaloniaXamlLoader.Load(this)
        this.DataContext <- ShellViewModel()

        let webView = this.FindControl<NativeWebView>("MonacoWebView")
        let assetsRoot = Path.Combine(AppContext.BaseDirectory, "EditorAssets")
        let host = AssetHost.start assetsRoot
        assetHost <- Some host
        webView.Source <- Uri(host.BaseUri, "editor-host.html")

        webView.WebMessageReceived.Add(fun args ->
            let envelope =
                System.Text.Json.JsonSerializer.Deserialize<FSharpQueryStudio.Protocol.EditorEnvelope>(
                    args.Body,
                    FSharpQueryStudio.Protocol.Serialization.options
                )

            match EditorBridge.decode envelope with
            | Ok _request -> () // Consumed into ViewModels starting with A.14 (TextChanged -> DocumentViewModel).
            | Error _err -> ()) // Malformed message from the page - never crashes the host.

        this.Closed.Add(fun _ ->
            match assetHost with
            | Some host -> (host :> IDisposable).Dispose()
            | None -> ())
