namespace FSharpQueryStudio.Ui

open System
open System.ComponentModel
open System.IO
open Avalonia
open Avalonia.Controls
open Avalonia.Data
open Avalonia.Input
open Avalonia.Interactivity
open Avalonia.Layout
open Avalonia.Markup.Xaml
open Avalonia.Media
open Avalonia.Platform.Storage
open Avalonia.Styling
open Avalonia.Threading
open Serilog
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Editor

/// Minimal `ICommand` adapter. Every command in this window is wired as a
/// plain `Click` handler, but an Avalonia `KeyBinding` can only invoke an
/// `ICommand` and Avalonia ships no relay-command type of its own — so
/// this exists purely so the F5 gesture advertised in the Run menu and the
/// Run button's tooltip is a real binding rather than a decorative label.
type internal RelayCommand(execute: unit -> unit) =
    let canExecuteChanged = Event<EventHandler, EventArgs>()

    interface System.Windows.Input.ICommand with
        [<CLIEvent>]
        member _.CanExecuteChanged = canExecuteChanged.Publish

        member _.CanExecute(_parameter: obj) = true
        member _.Execute(_parameter: obj) = execute ()

type MainWindow() as this =
    inherit Window()

    let mutable assetHost: AssetHost.Host option = None

    let shellViewModel = ShellViewModel()
    let themeService = ThemeService()

    // The Results panel: one instance, shared, because exactly one
    // "Results" tool ever exists — nothing to reparent-conflict with.
    let resultsView = ResultsView(DataContext = shellViewModel)

    // TEMP DIAGNOSTIC: Dock.Avalonia removed for this test — see
    // MainWindow.axaml's `FixedShellGrid` comment and docs/DEVLOG.md.
    // `webView` is the single, fixed-position editor control (was one
    // per tab via `DockFactory.createEditorControl`; this test has only
    // one editor slot, no tabs).
    let mutable webView: NativeWebView = Unchecked.defaultof<_>

    /// Host -> editor channel for the fixed editor control. The installed
    /// `Avalonia.Controls.WebView` (12.0.1) `NativeWebView` exposes no
    /// `PostWebMessage*` method at all — its only outbound member is
    /// `InvokeScript` — so an envelope is delivered by dispatching a
    /// synthetic `message` event on `window.chrome.webview`, which is
    /// exactly the object `editor-host.html`'s listener is attached to.
    /// Failures are swallowed — both a synchronous throw and a faulted
    /// task, since `InvokeScript` can fail either way — because a message
    /// sent before the page finished loading must never take the window
    /// down.
    let postToEditor (webView: NativeWebView) (envelope: EditorEnvelope) =
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

        let assetsRoot = Path.Combine(AppContext.BaseDirectory, "EditorAssets")
        let host = AssetHost.start assetsRoot
        assetHost <- Some host

        // TEMP DIAGNOSTIC: single fixed `NativeWebView`, wired the same
        // way `DockFactory.createEditorControl` used to build one per
        // tab — just one instance now, since `FixedShellGrid` has one
        // editor slot instead of a `DocumentDock`.
        webView <- NativeWebView()
        webView.Source <- Uri(host.BaseUri, "editor-host.html")

        webView.WebMessageReceived.Add(fun args ->
            let envelope =
                System.Text.Json.JsonSerializer.Deserialize<EditorEnvelope>(args.Body, Serialization.options)

            match EditorBridge.decode envelope with
            | Ok(TextChanged(documentUri, version, source)) ->
                shellViewModel.RouteTextChanged(documentUri, version, source)
            | Ok _otherRequest -> () // Wired as each lands in its own later task.
            | Result.Error _err -> ()) // Malformed message from the page - never crashes the host.

        // TEMP DIAGNOSTIC: logging the native adapter's own lifecycle
        // events, to see whether/when `NativeWebView` actually creates
        // its platform adapter for this instance.
        webView.AdapterCreated.Add(fun _ ->
            Log.Information(
                "NativeWebView.AdapterCreated Bounds={Bounds} IsVisible={IsVisible} IsEffectivelyVisible={EffVisible}",
                webView.Bounds,
                webView.IsVisible,
                webView.IsEffectivelyVisible
            ))

        webView.AdapterDestroyed.Add(fun _ -> Log.Information("NativeWebView.AdapterDestroyed"))
        webView.NavigationStarted.Add(fun args ->
            Log.Information("NativeWebView.NavigationStarted {Uri}", args.Request))

        webView.NavigationCompleted.Add(fun args ->
            Log.Information(
                "NativeWebView.NavigationCompleted IsSuccess={IsSuccess} Request={Request} Bounds={Bounds}",
                args.IsSuccess,
                args.Request,
                webView.Bounds
            ))

        let editorHost = this.FindControl<ContentControl>("EditorHost")
        let resultsHostControl = this.FindControl<ContentControl>("ResultsHost")
        resultsHostControl.Content <- resultsView

        // TEMP DIAGNOSTIC: assigning `Content` here, during the
        // constructor (before the window is ever shown), is suspected of
        // being too early for `NativeWebView`'s native adapter — deferred
        // to `Opened` instead, a documented workaround for this class of
        // Avalonia native-control-host timing issue.
        this.Opened.Add(fun _ -> editorHost.Content <- webView)

        // Gives `ProblemViewModel.Navigate` (the Problems tab's
        // double-click) and `ShellViewModel.MarkDocumentSaved` (the
        // post-save editor re-key) somewhere to send an envelope —
        // resolved to whichever tab is active *at send time*, since both
        // callers only ever act on the active document and each tab now
        // owns its own `NativeWebView` rather than there being one to
        // reach directly.
        shellViewModel.SendToEditor <- Some(fun envelope -> postToEditor webView envelope)

        // Avalonia ships no message-box control, so this *is* the whole
        // confirmation dialog: a small owned `Window` with two buttons.
        //
        // The contract is synchronous — Dock calls `ScriptDocument.OnClose()`
        // and needs a `bool` back before it decides whether to remove the
        // tab — while `ShowDialog` is asynchronous, and blocking the UI
        // thread on its task would deadlock. Pumping a nested dispatcher
        // frame (`DispatcherFrame` + `Dispatcher.PushFrame`, both confirmed
        // present in the installed Avalonia 12.1.1 by reflection over
        // `Avalonia.Base`) is the supported way to hold a UI-thread caller
        // while still servicing the dialog's own input.
        //
        // Cancel is `IsDefault` so Enter/Escape both keep the tab: the
        // safe answer is the one that doesn't throw away work. If showing
        // the dialog fails at all, the answer is "don't discard" for the
        // same reason.
        let confirmDiscardDirty (doc: DocumentViewModel) : bool =
            let mutable discard = false

            let message =
                TextBlock(
                    Text = $"{doc.Uri} has unsaved changes.\n\nDiscard them and close the tab?",
                    TextWrapping = TextWrapping.Wrap
                )

            let discardButton = Button(Content = "Discard", Width = 96.0)
            let cancelButton = Button(Content = "Cancel", Width = 96.0, IsDefault = true, IsCancel = true)

            let buttons =
                StackPanel(
                    Orientation = Orientation.Horizontal,
                    Spacing = 8.0,
                    HorizontalAlignment = HorizontalAlignment.Right
                )

            buttons.Children.Add(cancelButton)
            buttons.Children.Add(discardButton)

            let panel = StackPanel(Margin = Thickness(16.0), Spacing = 16.0)
            panel.Children.Add(message)
            panel.Children.Add(buttons)

            let dialog =
                Window(
                    Title = "Unsaved changes",
                    Width = 400.0,
                    SizeToContent = SizeToContent.Height,
                    CanResize = false,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = panel
                )

            discardButton.Click.Add(fun _ ->
                discard <- true
                dialog.Close())

            cancelButton.Click.Add(fun _ -> dialog.Close())

            let frame = DispatcherFrame()
            dialog.Closed.Add(fun _ -> frame.Continue <- false)

            try
                let showing = dialog.ShowDialog(this)

                // `ShowDialog` throws synchronously for the likely failure
                // (an owner that isn't visible), which the `with` below
                // catches — but a task that faults *after* returning would
                // leave the nested frame pumping forever with no window
                // left to close it, hanging the whole UI. Same
                // swallow-and-continue shape as `postToEditor` above.
                showing.ContinueWith(
                    (fun (t: System.Threading.Tasks.Task) ->
                        Log.Warning(t.Exception, "The discard-changes confirmation dialog faulted; keeping the tab open")
                        Dispatcher.UIThread.Post(fun () -> frame.Continue <- false)),
                    System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted
                )
                |> ignore

                Dispatcher.UIThread.PushFrame(frame)
                discard
            with ex ->
                Log.Warning(ex, "Failed to show the discard-changes confirmation; keeping the tab open")
                false

        // Without this, `ShellViewModel.CloseDocument` sees an unconfigured
        // hook and (by its own documented convention) answers "don't
        // discard" — so closing a dirty tab was a completely silent no-op:
        // no prompt, no message, the tab just refused to close.
        shellViewModel.ConfirmDiscardDirtyDocument <- Some confirmDiscardDirty

        // TEMP DIAGNOSTIC: no Dock, no tabs — `ActiveDocument` changing
        // (via `OpenDocument`, File > New/Open) is the one signal this
        // fixed layout has for "push this document into the editor",
        // replacing `DockFactory.DocumentActivated`.
        (shellViewModel :> INotifyPropertyChanged)
            .PropertyChanged.Add(fun args ->
                if args.PropertyName = nameof shellViewModel.ActiveDocument then
                    let doc = shellViewModel.ActiveDocument

                    postToEditor
                        webView
                        (EditorBridge.encodeNotification (DocumentLoaded(doc.Uri, doc.Version, doc.Text))))

        // Initial push for the workspace's very first document — nothing
        // else fires an `ActiveDocument` change for it.
        postToEditor
            webView
            (EditorBridge.encodeNotification (
                DocumentLoaded(
                    shellViewModel.ActiveDocument.Uri,
                    shellViewModel.ActiveDocument.Version,
                    shellViewModel.ActiveDocument.Text
                )
            ))

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

        // The Run menu item and the Run button's tooltip both advertise F5;
        // this is the binding that makes that true. `InputGesture` on the
        // menu item only *renders* the gesture text — it does not handle the
        // key — so the window-level `KeyBinding` is what actually fires.
        this.KeyBindings.Add(
            KeyBinding(
                Gesture = KeyGesture(Key.F5),
                Command = RelayCommand(fun () -> shellViewModel.RunAsync() |> ignore)
            )
        )

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

        // `NextUntitledUri` rather than `$"...{Documents.Length + 1}"` —
        // see its doc comment for the collision that made File > New a
        // silent no-op after a tab had been closed.
        let newDocument () =
            shellViewModel.OpenDocument(shellViewModel.NextUntitledUri()) |> ignore

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

                    // TEMP DIAGNOSTIC: the `ActiveDocument` PropertyChanged
                    // push above already fired once, before `text` landed
                    // (activation happens on `OpenDocument`, content is
                    // applied after) — push again now with the real
                    // content so the fixed editor slot shows the opened
                    // file, not an empty buffer.
                    postToEditor
                        webView
                        (EditorBridge.encodeNotification (DocumentLoaded(doc.Uri, doc.Version, doc.Text)))
            }
            :> System.Threading.Tasks.Task

        /// Writes `text` to `path`, reporting whether it actually landed.
        /// Every save path runs inside a fire-and-forget `task` that the
        /// click handlers `|> ignore`, so an `IOException` (read-only
        /// target, denied permissions, full disk) would otherwise become an
        /// unobserved faulted task: no dialog, no log line, the user's work
        /// simply gone. A warning is the minimum bar; a user-facing error
        /// dialog is follow-up work. The boolean matters just as much as
        /// the log line — `MarkSaved` must only run on a write that
        /// actually succeeded, or a failed save would clear the dirty
        /// marker and tell the user their work is safe when it isn't.
        let tryWriteFile (path: string) (text: string) : bool =
            try
                File.WriteAllText(path, text)
                true
            with ex ->
                Log.Warning(ex, "Failed to save document to {Path}", path)
                false

        let saveActiveDocument () =
            let doc = shellViewModel.ActiveDocument

            if doc.Uri.StartsWith("untitled:") then
                task {
                    let options =
                        FilePickerSaveOptions(FileTypeChoices = [ openFsxFilter ], SuggestedFileName = "Script1.fsx")

                    let! file = this.StorageProvider.SaveFilePickerAsync(options)

                    if not (isNull file) then
                        let path = file.Path.LocalPath

                        // Retagging the Uri is what stops the *next* Save on
                        // this tab from re-prompting: `untitled:` is the only
                        // thing this handler distinguishes a scratch tab by.
                        // Routed through `MarkDocumentSaved` (rather than
                        // `doc.MarkSaved` directly) so the retag also re-keys
                        // Monaco's own `documentUri` — see that member's doc
                        // comment for the edits silently dropped otherwise.
                        if tryWriteFile path doc.Text then
                            shellViewModel.MarkDocumentSaved(doc, Some path)
                }
                :> System.Threading.Tasks.Task
            else
                if tryWriteFile doc.Uri doc.Text then
                    shellViewModel.MarkDocumentSaved(doc, None)

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
                    let path = file.Path.LocalPath

                    // Save As retags even a document that already had a real
                    // path — after "Save As", the tab edits the new file, so
                    // the editor page has to be re-keyed onto the new URI
                    // too (`MarkDocumentSaved` does that when it changed).
                    if tryWriteFile path doc.Text then
                        shellViewModel.MarkDocumentSaved(doc, Some path)
            }
            |> ignore)

        // TEMP DIAGNOSTIC: no Dock, so no `HideDockable`/`RestoreDockable`
        // — the fixed panels just toggle `IsVisible` directly.
        let workspacePanel = this.FindControl<Border>("WorkspacePanel")
        let resultsHostToggle = this.FindControl<ContentControl>("ResultsHost")

        this.FindControl<MenuItem>("ToggleWorkspaceMenuItem").Click.Add(fun _ ->
            workspacePanel.IsVisible <- not workspacePanel.IsVisible)

        this.FindControl<MenuItem>("ToggleResultsMenuItem").Click.Add(fun _ ->
            resultsHostToggle.IsVisible <- not resultsHostToggle.IsVisible)

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

    // TEMP DIAGNOSTIC: `DockFactory`/`MainDockLayout`/`RestoreLayout`
    // removed along with `DockControl` — see `FixedShellGrid`'s comment.
    // `App.axaml.fs`'s layout persistence block is disabled for this test
    // rather than reaching for these.

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
