namespace FSharpQueryStudio.Ui

open System
open System.ComponentModel
open System.IO
open Avalonia.Controls
open Avalonia.Data
open Avalonia.Interactivity
open Avalonia.Markup.Xaml
open Avalonia.Platform.Storage
open Avalonia.Styling
open Dock.Avalonia.Controls
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Editor

type MainWindow() as this =
    inherit Window()

    let mutable assetHost: AssetHost.Host option = None

    let shellViewModel = ShellViewModel()
    let themeService = ThemeService()

    // The single shared Monaco editor and the single Results panel: both
    // are built here rather than declared in `MainWindow.axaml`, because
    // `DockFactory.CreateLayout` needs live control instances to assign to
    // each dockable's `Context` (see `ScriptDocument`'s doc comment).
    let webView = NativeWebView()
    let resultsView = ResultsView(DataContext = shellViewModel)
    let dockFactory = DockFactory(shellViewModel, webView, resultsView)

    /// Host -> editor channel. The installed `Avalonia.Controls.WebView`
    /// (12.0.1) `NativeWebView` exposes no `PostWebMessage*` method at all
    /// — its only outbound member is `InvokeScript` — so an envelope is
    /// delivered by dispatching a synthetic `message` event on
    /// `window.chrome.webview`, which is exactly the object
    /// `editor-host.html`'s listener is attached to. Failures are
    /// swallowed — both a synchronous throw and a faulted task, since
    /// `InvokeScript` can fail either way — because a message sent before
    /// the page finished loading must never take the window down.
    let postToEditor (envelope: EditorEnvelope) =
        let json =
            System.Text.Json.JsonSerializer.Serialize(envelope, Serialization.options)

        // Serializing the JSON *again* yields a correctly escaped JS
        // string literal to embed in the script below.
        let literal = System.Text.Json.JsonSerializer.Serialize(json)

        let script =
            "if (window.chrome && window.chrome.webview) { window.chrome.webview.dispatchEvent(new MessageEvent('message', { data: "
            + literal
            + " })); }"

        try
            webView
                .InvokeScript(script)
                .ContinueWith((fun (t: System.Threading.Tasks.Task<string>) -> t.Exception |> ignore),
                              System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted)
            |> ignore
        with _ ->
            ()

    do
        AvaloniaXamlLoader.Load(this)
        this.DataContext <- shellViewModel

        let dockControl = this.FindControl<DockControl>("MainDock")
        let layout = dockFactory.CreateLayout()
        // `InitLayout` wires every dockable's `Factory`/`Owner` and
        // activates the default dockable — Dock.Avalonia's own init
        // sequence requires it before the layout is handed to the control.
        dockFactory.InitLayout(layout)
        dockControl.Factory <- dockFactory
        dockControl.Layout <- layout

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

        // Gives `ProblemViewModel.Navigate` (the Problems tab's
        // double-click) somewhere to send its caret-move envelope — the
        // WebView is private to this window, so this is the only place
        // that hook can be wired.
        shellViewModel.SendToEditor <- Some postToEditor

        // Pushes the newly active tab's text into the shared editor —
        // see ScriptDocument/DockFactory's doc comments (Tasks 5-6) and
        // the DocumentLoaded notification (Task 7).
        dockFactory.DocumentActivated.Add(fun doc ->
            postToEditor (EditorBridge.encodeNotification (DocumentLoaded(doc.Uri, doc.Version, doc.Text))))

        let resultsGrid = resultsView.FindControl<DataGrid>("ResultsGrid")

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

        let resetSessionButton = this.FindControl<Button>("ResetSessionButton")
        resetSessionButton.Click.Add(fun (_: RoutedEventArgs) -> shellViewModel.ResetSessionAsync() |> ignore)

        let runMenuItem = this.FindControl<MenuItem>("RunMenuItem")
        runMenuItem.Click.Add(fun (_: RoutedEventArgs) -> shellViewModel.RunAsync() |> ignore)

        let cancelMenuItem = this.FindControl<MenuItem>("CancelMenuItem")
        cancelMenuItem.Click.Add(fun (_: RoutedEventArgs) -> shellViewModel.Cancel() |> ignore)

        let resetSessionMenuItem = this.FindControl<MenuItem>("ResetSessionMenuItem")
        resetSessionMenuItem.Click.Add(fun (_: RoutedEventArgs) -> shellViewModel.ResetSessionAsync() |> ignore)

        let toggleThemeMenuItem = this.FindControl<MenuItem>("ToggleThemeMenuItem")

        toggleThemeMenuItem.Click.Add(fun (_: RoutedEventArgs) ->
            themeService.Toggle()

            Avalonia.Application.Current.RequestedThemeVariant <-
                match themeService.Current with
                | Dark -> ThemeVariant.Dark
                | Light -> ThemeVariant.Light)

        let openFsxFilter = FilePickerFileType("F# Script", Patterns = [ "*.fsx" ])

        let newDocument () =
            let uri = $"untitled:Script{shellViewModel.Documents.Length + 1}.fsx"
            dockFactory.OpenDocument(uri)

        // Loads the picked file's real text into the `DocumentViewModel`
        // *before* handing the URI to `dockFactory.OpenDocument` — that
        // call synchronously activates the new tab, which fires
        // `DocumentActivated` (Task 9's wiring above) and pushes a
        // `DocumentLoaded` bridge notification with whatever the
        // document's `Text`/`Version` are *at that instant*. Populating
        // the document first (via `shellViewModel.OpenDocument`, the same
        // shell-only half `DockFactory.OpenDocument` calls internally —
        // see its doc comment) means that notification carries the real
        // file content instead of the empty string every freshly-created
        // `DocumentViewModel` starts with.
        let openDocument () =
            task {
                let options = FilePickerOpenOptions(AllowMultiple = false, FileTypeFilter = [ openFsxFilter ])
                let! files = this.StorageProvider.OpenFilePickerAsync(options)

                match files |> Seq.tryHead with
                | None -> ()
                | Some file ->
                    let path = file.Path.LocalPath
                    let text = File.ReadAllText(path)
                    let doc = shellViewModel.OpenDocument(path)
                    doc.ApplyTextChanged(doc.Version + 1, text)
                    dockFactory.OpenDocument(path)
            }
            :> System.Threading.Tasks.Task

        let saveActiveDocument () =
            let doc = shellViewModel.ActiveDocument

            if doc.Uri.StartsWith("untitled:") then
                task {
                    let options =
                        FilePickerSaveOptions(FileTypeChoices = [ openFsxFilter ], SuggestedFileName = "Script1.fsx")

                    let! file = this.StorageProvider.SaveFilePickerAsync(options)

                    if not (isNull file) then
                        File.WriteAllText(file.Path.LocalPath, doc.Text)
                }
                :> System.Threading.Tasks.Task
            else
                File.WriteAllText(doc.Uri, doc.Text)
                System.Threading.Tasks.Task.CompletedTask

        this.FindControl<MenuItem>("NewMenuItem").Click.Add(fun _ -> newDocument ())
        this.FindControl<Button>("NewButton").Click.Add(fun _ -> newDocument ())

        this.FindControl<MenuItem>("OpenMenuItem").Click.Add(fun _ -> openDocument () |> ignore)
        this.FindControl<Button>("OpenButton").Click.Add(fun _ -> openDocument () |> ignore)

        this.FindControl<MenuItem>("SaveMenuItem").Click.Add(fun _ -> saveActiveDocument () |> ignore)
        this.FindControl<Button>("SaveButton").Click.Add(fun _ -> saveActiveDocument () |> ignore)

        this.FindControl<MenuItem>("SaveAsMenuItem").Click.Add(fun _ ->
            task {
                let doc = shellViewModel.ActiveDocument
                let options = FilePickerSaveOptions(FileTypeChoices = [ openFsxFilter ])
                let! file = this.StorageProvider.SaveFilePickerAsync(options)

                if not (isNull file) then
                    File.WriteAllText(file.Path.LocalPath, doc.Text)
            }
            |> ignore)

        // `Dock.Model.Core.IFactory` (v12.1.0) has no `IsVisible`-style
        // flag on `IDockable` — the real hide/show primitives are
        // `HideDockable`/`RestoreDockable`, confirmed by a throwaway xunit
        // probe (see `DockFactoryTests.fs`) that found `RestoreDockable`
        // must be handed the *same* `IDockable` reference that was hidden:
        // a fresh `RestoreDockable(id: string)` lookup after the hide left
        // the pane hidden, because once removed the tool's `Owner` becomes
        // the root dock and it drops out of its `ToolDock`'s
        // `VisibleDockables` entirely — so `DockFactory.WorkspaceTool`/
        // `ResultsTool` (added alongside `WorkspaceDock`/`ResultsDock`)
        // exist to keep that reference alive across the round-trip.
        let toggleToolVisibility (dock: Dock.Model.Core.IDock) (tool: Dock.Model.Core.IDockable) =
            let isVisible =
                dock.VisibleDockables
                |> Seq.exists (fun d -> obj.ReferenceEquals(d, tool))

            if isVisible then
                (dockFactory :> Dock.Model.Core.IFactory).HideDockable(tool) |> ignore
            else
                (dockFactory :> Dock.Model.Core.IFactory).RestoreDockable(tool) |> ignore

        this.FindControl<MenuItem>("ToggleWorkspaceMenuItem").Click.Add(fun _ ->
            toggleToolVisibility dockFactory.WorkspaceDock dockFactory.WorkspaceTool)

        this.FindControl<MenuItem>("ToggleResultsMenuItem").Click.Add(fun _ ->
            toggleToolVisibility dockFactory.ResultsDock dockFactory.ResultsTool)

        this.FindControl<MenuItem>("ExitMenuItem").Click.Add(fun _ -> this.Close())

        this.Closed.Add(fun _ ->
            match assetHost with
            | Some host -> (host :> IDisposable).Dispose()
            | None -> ())

    /// Exposed so the composition root (`App.axaml.fs`) can wire further
    /// hooks (`CheckAsync`, etc.) after construction, alongside
    /// `WireExecution` below.
    member _.ShellViewModel = shellViewModel

    /// Exposed so `App.axaml.fs` can persist/restore the theme choice.
    member _.ThemeService = themeService

    /// Exposed so `App.axaml.fs` (and File > New/Open) can open documents
    /// as real Dock tabs, and so layout persistence can reach the layout.
    member _.DockFactory = dockFactory

    /// Composition root: wires `shellViewModel`'s `ExecuteAsync`/
    /// `CancelAsync`/`ResetSession` to a real worker process — set once,
    /// from `App.axaml.fs`. Kept out of the constructor so headless/
    /// `Ui.Tests` construction of `MainWindow` never spawns a real worker
    /// process as a side effect.
    member _.WireExecution
        (
            executeAsync: EvaluateParams -> System.Threading.Tasks.Task<ResultEnvelope>,
            cancelAsync: unit -> System.Threading.Tasks.Task<unit>,
            resetSessionAsync: string -> System.Threading.Tasks.Task<unit>
        ) =
        shellViewModel.ExecuteAsync <- Some executeAsync
        shellViewModel.CancelAsync <- Some cancelAsync
        shellViewModel.ResetSession <- Some resetSessionAsync
        shellViewModel.WorkerState.TryTransition(WorkerState.Ready) |> ignore
