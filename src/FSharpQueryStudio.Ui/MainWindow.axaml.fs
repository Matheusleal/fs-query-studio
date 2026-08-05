namespace FSharpQueryStudio.Ui

open System
open System.IO
open Avalonia.Controls
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
            | Error _err -> ()) // Malformed message from the page - never crashes the host.

        this.Closed.Add(fun _ ->
            match assetHost with
            | Some host -> (host :> IDisposable).Dispose()
            | None -> ())
