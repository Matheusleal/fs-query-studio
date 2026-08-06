# UI-Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `MainWindow.axaml`'s fixed `Grid` shell with a `Dock.Avalonia`-based dockable, VS2026-styled shell — wiring the already-built `ShellViewModel`/`DocumentViewModel`/`ResultViewModel`/`ProblemViewModel`/`WorkerStateViewModel` into real, restyled, dockable panes instead of the current single-tab stub.

**Architecture:** `Dock.Model.Mvvm` types (`Factory`, `Document`, `Tool`, `DocumentDock`, `ToolDock`, `ProportionalDock`) have no Avalonia-rendering dependency, so the new `ThemeService`/`ScriptDocument`/`DockFactory` live in `FSharpQueryStudio.Ui` and are fully unit-testable with plain xUnit — only the actual `DockControl` XAML wiring in `MainWindow.axaml`/`.axaml.fs` is manual-verify, matching this codebase's established pattern for every prior XAML/WebView task (A.10, A.13, A.16, B.6, B.9, E.6, E.7). One Monaco `NativeWebView` instance is shared across every open script tab (WebView2 instances are expensive; VS itself doesn't spin up N editor engines either) — each `ScriptDocument`'s `Content` points at that same shared control, and a new `DocumentLoaded` host→editor bridge notification swaps its text/model when the active tab changes.

**Tech Stack:** Avalonia 12.1.1 (existing), Dock.Avalonia + Dock.Model.Mvvm + Dock.Serializer.SystemTextJson + Dock.Avalonia.Themes.Fluent (new), Material.Icons.Avalonia (new), F#/.NET (existing), xUnit (existing).

## Global Constraints

- Every new/modified `.fs` file lands in the project the codebase's existing Section 4 split already assigns it to: `Ui` for view models and Dock-model glue, `Storage` for file I/O, `Protocol` for wire/shared types, `Editor` for the bridge, `App` for composition-root wiring.
- No hand-picked NuGet version numbers — install packages via `dotnet add package <name>` so the resolved version is real, then leave whatever version lands in the `.fsproj`.
- `ShellViewModel.CheckAsync`/`ExecuteAsync`/`CancelAsync`/etc. keep their existing "no-op when unconfigured" contract — nothing in this plan changes that contract, only adds to what's wired.
- Every automated test follows this codebase's existing naming convention: `[<Fact>] let `` a plain-English sentence describing the behavior `` () = ...`.
- After every task: update `docs/DEVLOG.md` with what was actually implemented/decided (not a restatement of the task), then commit. Do this without asking — established project convention.
- `dotnet build` and `dotnet test` (solution-wide) must be green before each task's commit.

---

### Task 1: `ThemeService`

**Files:**
- Create: `src/FSharpQueryStudio.Ui/ThemeService.fs`
- Modify: `src/FSharpQueryStudio.Ui/FSharpQueryStudio.Ui.fsproj` (add compile item, right after `<Compile Include="WorkerStateViewModel.fs" />`)
- Test: `tests/FSharpQueryStudio.Ui.Tests/ThemeServiceTests.fs`
- Modify: `tests/FSharpQueryStudio.Ui.Tests/FSharpQueryStudio.Ui.Tests.fsproj` (add compile item, right after `<Compile Include="WorkerStateViewModel.fs" />`... i.e. after the corresponding new source's sibling — after `<Compile Include="ThemeServiceTests.fs" />` position doesn't matter for tests, xUnit has no ordering dependency; place it right after `WorkerStateViewModelTests.fs`)

**Interfaces:**
- Produces: `type Theme = Dark | Light`; `ThemeService()` with `member _.Current: Theme` (starts `Dark`), `member _.Toggle() : unit`, `interface INotifyPropertyChanged`.

- [ ] **Step 1: Write the failing tests**

```fsharp
// tests/FSharpQueryStudio.Ui.Tests/ThemeServiceTests.fs
module ThemeServiceTests

open System.ComponentModel
open Xunit
open FSharpQueryStudio.Ui

[<Fact>]
let ``starts in the Dark theme`` () =
    let service = ThemeService()
    Assert.Equal(Theme.Dark, service.Current)

[<Fact>]
let ``Toggle flips Dark to Light`` () =
    let service = ThemeService()
    service.Toggle()
    Assert.Equal(Theme.Light, service.Current)

[<Fact>]
let ``Toggle flips Light back to Dark`` () =
    let service = ThemeService()
    service.Toggle()
    service.Toggle()
    Assert.Equal(Theme.Dark, service.Current)

[<Fact>]
let ``PropertyChanged fires exactly once per Toggle, for Current`` () =
    let service = ThemeService()
    let mutable raisedCount = 0
    let mutable lastProperty = None
    (service :> INotifyPropertyChanged).PropertyChanged.Add(fun args ->
        raisedCount <- raisedCount + 1
        lastProperty <- Some args.PropertyName)

    service.Toggle()

    Assert.Equal(1, raisedCount)
    Assert.Equal(Some "Current", lastProperty)
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FSharpQueryStudio.Ui.Tests --filter FullyQualifiedName~ThemeServiceTests`
Expected: FAIL to compile — `ThemeService`/`Theme` don't exist yet.

- [ ] **Step 3: Write the implementation**

```fsharp
// src/FSharpQueryStudio.Ui/ThemeService.fs
namespace FSharpQueryStudio.Ui

open System.ComponentModel

/// UI-Foundation's dark/light theme choice — see the UI-Foundation spec's
/// theme section. Pure state, no Avalonia dependency: the composition
/// root (`App.axaml.fs`) is the one place that translates `Current` into
/// a real `ThemeVariant` on `Application.Current`.
type Theme =
    | Dark
    | Light

type ThemeService() as self =
    let mutable current = Theme.Dark
    let propertyChanged = Event<PropertyChangedEventHandler, PropertyChangedEventArgs>()

    interface INotifyPropertyChanged with
        [<CLIEvent>]
        member _.PropertyChanged = propertyChanged.Publish

    member _.Current = current

    member _.Toggle() =
        current <-
            match current with
            | Theme.Dark -> Theme.Light
            | Theme.Light -> Theme.Dark

        propertyChanged.Trigger(self, PropertyChangedEventArgs(nameof self.Current))
```

- [ ] **Step 4: Add both files to their `.fsproj`s and run the tests**

Run: `dotnet test tests/FSharpQueryStudio.Ui.Tests --filter FullyQualifiedName~ThemeServiceTests`
Expected: PASS, 4/4.

- [ ] **Step 5: Update DEVLOG.md and commit**

```bash
git add src/FSharpQueryStudio.Ui/ThemeService.fs src/FSharpQueryStudio.Ui/FSharpQueryStudio.Ui.fsproj tests/FSharpQueryStudio.Ui.Tests/ThemeServiceTests.fs tests/FSharpQueryStudio.Ui.Tests/FSharpQueryStudio.Ui.Tests.fsproj docs/DEVLOG.md
git commit -m "UI-Foundation.1: ThemeService"
```

---

### Task 2: `UiState` persistence

**Files:**
- Create: `src/FSharpQueryStudio.Protocol/UiState.fs`
- Modify: `src/FSharpQueryStudio.Protocol/FSharpQueryStudio.Protocol.fsproj` (add `<Compile Include="UiState.fs" />` after `<Compile Include="Trust.fs" />`)
- Create: `src/FSharpQueryStudio.Storage/UiState.fs`
- Modify: `src/FSharpQueryStudio.Storage/FSharpQueryStudio.Storage.fsproj` (add `<Compile Include="UiState.fs" />` after `<Compile Include="Trust.fs" />`)
- Test: `tests/FSharpQueryStudio.Storage.Tests/UiStateTests.fs`
- Modify: `tests/FSharpQueryStudio.Storage.Tests/FSharpQueryStudio.Storage.Tests.fsproj` (add compile item)

**Interfaces:**
- Produces: `FSharpQueryStudio.Protocol.UiState = { Theme: string; DockLayout: System.Text.Json.JsonElement option }`; `FSharpQueryStudio.Storage.UiState.defaultState: UiState`, `.read (appDataDir: string) : UiState`, `.write (appDataDir: string) (state: UiState) : unit`.
- Consumes: `FSharpQueryStudio.Protocol.Serialization.options` (existing, from `Serialization.fs`) for JSON round-tripping, same as `Trust.fs`.

- [ ] **Step 1: Write the failing tests**

```fsharp
// tests/FSharpQueryStudio.Storage.Tests/UiStateTests.fs
module UiStateTests

open System
open System.IO
open System.Text.Json
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Storage

let private tempAppDataDir () =
    let dir = Path.Combine(Path.GetTempPath(), "fqs-uistate-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    dir

[<Fact>]
let ``read returns defaultState when ui-state.json does not exist`` () =
    let dir = tempAppDataDir ()
    Assert.Equal(UiState.defaultState, UiState.read dir)

[<Fact>]
let ``write then read round-trips Theme and a DockLayout blob`` () =
    let dir = tempAppDataDir ()
    let layout = JsonSerializer.SerializeToElement({| tabs = [ "a"; "b" ] |})
    let state: FSharpQueryStudio.Protocol.UiState = { Theme = "Light"; DockLayout = Some layout }

    UiState.write dir state
    let read = UiState.read dir

    Assert.Equal("Light", read.Theme)
    Assert.True(read.DockLayout.IsSome)
    Assert.Equal(JsonSerializer.Serialize(layout), JsonSerializer.Serialize(read.DockLayout.Value))

[<Fact>]
let ``write then read round-trips a None DockLayout`` () =
    let dir = tempAppDataDir ()
    let state: FSharpQueryStudio.Protocol.UiState = { Theme = "Dark"; DockLayout = None }

    UiState.write dir state

    Assert.Equal(state, UiState.read dir)

[<Fact>]
let ``read returns defaultState when ui-state.json is malformed`` () =
    let dir = tempAppDataDir ()
    File.WriteAllText(Path.Combine(dir, "ui-state.json"), "{ not valid json ")

    Assert.Equal(UiState.defaultState, UiState.read dir)
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FSharpQueryStudio.Storage.Tests --filter FullyQualifiedName~UiStateTests`
Expected: FAIL to compile — `FSharpQueryStudio.Protocol.UiState`/`FSharpQueryStudio.Storage.UiState` don't exist yet.

- [ ] **Step 3: Write the Protocol type**

```fsharp
// src/FSharpQueryStudio.Protocol/UiState.fs
namespace FSharpQueryStudio.Protocol

open System.Text.Json

/// App-level UI state — UI-Foundation's persisted theme choice and
/// Dock.Avalonia layout, under `%LOCALAPPDATA%/FSharpQueryStudio/`. Not
/// workspace data (a window arrangement isn't project-scoped), so it
/// lives outside `workspace.json`/`trust.json`. `DockLayout` is an
/// opaque blob — Dock.Avalonia's own serializer owns its shape, this
/// type just carries it alongside the theme choice.
type UiState =
    { Theme: string
      DockLayout: JsonElement option }
```

- [ ] **Step 4: Write the Storage module**

```fsharp
// src/FSharpQueryStudio.Storage/UiState.fs
namespace FSharpQueryStudio.Storage

open System.IO
open System.Text.Json
open FSharpQueryStudio.Protocol

/// Reads and writes `ui-state.json` under a given app-data directory.
/// The composition root passes the real `%LOCALAPPDATA%/FSharpQueryStudio`
/// path in production; tests point it at a temp directory.
module UiState =

    let private fileName = "ui-state.json"

    let private statePath (appDataDir: string) = Path.Combine(appDataDir, fileName)

    let defaultState: FSharpQueryStudio.Protocol.UiState = { Theme = "Dark"; DockLayout = None }

    /// `defaultState` when the file is missing or fails to parse — a
    /// corrupt or foreign `ui-state.json` must never block startup.
    let read (appDataDir: string) : FSharpQueryStudio.Protocol.UiState =
        let path = statePath appDataDir

        if File.Exists path then
            try
                let json = File.ReadAllText path
                JsonSerializer.Deserialize<FSharpQueryStudio.Protocol.UiState>(json, Serialization.options)
            with _ ->
                defaultState
        else
            defaultState

    let write (appDataDir: string) (state: FSharpQueryStudio.Protocol.UiState) : unit =
        Directory.CreateDirectory appDataDir |> ignore
        let json = JsonSerializer.Serialize(state, Serialization.options)
        File.WriteAllText(statePath appDataDir, json)
```

- [ ] **Step 5: Add all files to their `.fsproj`s and run the tests**

Run: `dotnet test tests/FSharpQueryStudio.Storage.Tests --filter FullyQualifiedName~UiStateTests`
Expected: PASS, 4/4.

- [ ] **Step 6: Update DEVLOG.md and commit**

```bash
git add src/FSharpQueryStudio.Protocol/UiState.fs src/FSharpQueryStudio.Protocol/FSharpQueryStudio.Protocol.fsproj src/FSharpQueryStudio.Storage/UiState.fs src/FSharpQueryStudio.Storage/FSharpQueryStudio.Storage.fsproj tests/FSharpQueryStudio.Storage.Tests/UiStateTests.fs tests/FSharpQueryStudio.Storage.Tests/FSharpQueryStudio.Storage.Tests.fsproj docs/DEVLOG.md
git commit -m "UI-Foundation.2: UiState persistence (theme + dock layout)"
```

---

### Task 3: Add Dock.Avalonia and Material.Icons.Avalonia packages

**Files:**
- Modify: `src/FSharpQueryStudio.Ui/FSharpQueryStudio.Ui.fsproj`
- Modify: `src/FSharpQueryStudio.App/FSharpQueryStudio.App.fsproj`

**Interfaces:** None — package references only, no code yet.

- [ ] **Step 1: Add packages to `Ui`**

Run, from the repo root:
```bash
dotnet add src/FSharpQueryStudio.Ui/FSharpQueryStudio.Ui.fsproj package Dock.Avalonia
dotnet add src/FSharpQueryStudio.Ui/FSharpQueryStudio.Ui.fsproj package Dock.Model.Mvvm
dotnet add src/FSharpQueryStudio.Ui/FSharpQueryStudio.Ui.fsproj package Dock.Serializer.SystemTextJson
dotnet add src/FSharpQueryStudio.Ui/FSharpQueryStudio.Ui.fsproj package Material.Icons.Avalonia
```

`Dock.Model.Mvvm`/`Dock.Avalonia`/`Dock.Serializer.SystemTextJson` are what `DockFactory`/`ScriptDocument` (Tasks 5-6) and the layout-persistence wiring (Task 12) need; `Dock.Avalonia` itself carries the `DockControl` used in `MainWindow.axaml` (Task 9) — it's added here rather than only in `App` because `MainWindow.axaml` lives in `Ui`, not `App`. `Material.Icons.Avalonia` is for the toolbar icons in the same file.

- [ ] **Step 2: Add the Dock theme package to `App`**

Run:
```bash
dotnet add src/FSharpQueryStudio.App/FSharpQueryStudio.App.fsproj package Dock.Avalonia.Themes.Fluent
```

`App.axaml` (Task 8) is what registers `DockFluentTheme`, and `App.axaml` lives in `FSharpQueryStudio.App`.

- [ ] **Step 3: Build the solution**

Run: `dotnet build`
Expected: succeeds. If `Dock.Serializer.SystemTextJson` doesn't exist under that exact package id, use `dotnet nuget search Dock.Serializer` to find the current System.Text.Json-based serializer package name and substitute it — the requirement is "a Dock.Avalonia layout serializer that isn't the Newtonsoft one," not that literal id.

- [ ] **Step 4: Update DEVLOG.md and commit**

```bash
git add src/FSharpQueryStudio.Ui/FSharpQueryStudio.Ui.fsproj src/FSharpQueryStudio.App/FSharpQueryStudio.App.fsproj docs/DEVLOG.md
git commit -m "UI-Foundation.3: add Dock.Avalonia and Material.Icons.Avalonia packages"
```

---

### Task 4: `DocumentViewModel`/`ShellViewModel` expose `PropertyChanged` for `IsDirty`/`ActiveDocument`

Neither type currently implements `INotifyPropertyChanged` (`WorkerStateViewModel`/`ResultViewModel` do; these two were missed). Without it, nothing — including the Dock tab title (Task 5) and the status bar's `ActiveDocument`-rooted bindings (Task 9) — can react when the dirty flag or the active tab changes, which UI-Foundation's tab-switching needs to actually refresh on screen.

**Files:**
- Modify: `src/FSharpQueryStudio.Ui/DocumentViewModel.fs`
- Modify: `src/FSharpQueryStudio.Ui/ShellViewModel.fs`
- Test: `tests/FSharpQueryStudio.Ui.Tests/DocumentViewModelTests.fs` (add a case)
- Test: `tests/FSharpQueryStudio.Ui.Tests/ShellViewModelTests.fs` (add cases)

**Interfaces:**
- Produces: `DocumentViewModel :> INotifyPropertyChanged` (already had the `propertyChanged` event internally — just exposing it); `ShellViewModel :> INotifyPropertyChanged`, firing `"ActiveDocument"` whenever `OpenDocument`/`CloseDocument` actually change which document is active (not on every call — only on an actual reference change).

- [ ] **Step 1: Write the failing tests**

Append to `tests/FSharpQueryStudio.Ui.Tests/DocumentViewModelTests.fs`:
```fsharp
[<Fact>]
let ``PropertyChanged fires for IsDirty when ApplyTextChanged marks the document dirty`` () =
    let doc = DocumentViewModel("untitled:Script1.fsx")
    let mutable raisedFor: string option = None
    (doc :> INotifyPropertyChanged).PropertyChanged.Add(fun args -> raisedFor <- Some args.PropertyName)

    doc.ApplyTextChanged(1, "1 + 1")

    Assert.Equal(Some "IsDirty", raisedFor)
```
(Add `open System.ComponentModel` to the file's opens if not already present.)

Append to `tests/FSharpQueryStudio.Ui.Tests/ShellViewModelTests.fs`:
```fsharp
[<Fact>]
let ``PropertyChanged fires for ActiveDocument when OpenDocument switches to a different tab`` () =
    let vm = ShellViewModel()
    let firstUri = vm.ActiveDocument.Uri
    let mutable raisedFor: string option = None
    (vm :> INotifyPropertyChanged).PropertyChanged.Add(fun args -> raisedFor <- Some args.PropertyName)

    vm.OpenDocument("untitled:Script2.fsx") |> ignore

    Assert.Equal(Some "ActiveDocument", raisedFor)
    Assert.NotEqual(firstUri, vm.ActiveDocument.Uri)

[<Fact>]
let ``PropertyChanged does not fire for ActiveDocument when OpenDocument re-opens the already-active tab`` () =
    let vm = ShellViewModel()
    let mutable raised = false
    (vm :> INotifyPropertyChanged).PropertyChanged.Add(fun _ -> raised <- true)

    vm.OpenDocument(vm.ActiveDocument.Uri) |> ignore

    Assert.False(raised)
```
(Add `open System.ComponentModel` to the file's opens if not already present.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FSharpQueryStudio.Ui.Tests --filter "FullyQualifiedName~PropertyChanged"`
Expected: FAIL — `DocumentViewModel`/`ShellViewModel` don't implement `INotifyPropertyChanged` as a castable interface from test code yet (compile error on `:> INotifyPropertyChanged`... actually `DocumentViewModel` already privately triggers a `propertyChanged` event but doesn't expose the interface, so the `:>` cast itself fails to compile).

- [ ] **Step 3: Expose it on `DocumentViewModel`**

In `src/FSharpQueryStudio.Ui/DocumentViewModel.fs`, immediately after the existing `let debouncer = ...` line and before `member _.Uri = uri`, add:
```fsharp
    interface INotifyPropertyChanged with
        [<CLIEvent>]
        member _.PropertyChanged = propertyChanged.Publish
```

- [ ] **Step 4: Expose it on `ShellViewModel` and fire on real `ActiveDocument` changes**

In `src/FSharpQueryStudio.Ui/ShellViewModel.fs`:

1. Add `open System.ComponentModel` to the opens.
2. Change the type declaration line from
   ```fsharp
   type ShellViewModel(?scheduler: IDebounceScheduler) =
   ```
   to
   ```fsharp
   type ShellViewModel(?scheduler: IDebounceScheduler) as self =
   ```
3. Right after `let mutable activeDocument = Unchecked.defaultof<DocumentViewModel>`, add:
   ```fsharp
       let propertyChanged = Event<PropertyChangedEventHandler, PropertyChangedEventArgs>()
   ```
4. Right after the `do activeDocument <- createDocument "untitled:Script1.fsx"` line, add:
   ```fsharp
       interface INotifyPropertyChanged with
           [<CLIEvent>]
           member _.PropertyChanged = propertyChanged.Publish
   ```
5. Replace `OpenDocument`'s body:
   ```fsharp
       member this.OpenDocument(uri: string) : DocumentViewModel =
           let doc =
               documents
               |> Seq.tryFind (fun d -> d.Uri = uri)
               |> Option.defaultWith (fun () -> createDocument uri)

           if not (System.Object.ReferenceEquals(activeDocument, doc)) then
               activeDocument <- doc
               propertyChanged.Trigger(self, PropertyChangedEventArgs(nameof this.ActiveDocument))

           doc
   ```
6. In `CloseDocument`, replace the two `activeDocument <- ...` reassignment lines so each fires the same event:
   ```fsharp
               if documents.Count = 0 then
                   activeDocument <- createDocument "untitled:Script1.fsx"
                   propertyChanged.Trigger(self, PropertyChangedEventArgs(nameof this.ActiveDocument))
               elif System.Object.ReferenceEquals(activeDocument, doc) then
                   activeDocument <- documents.[documents.Count - 1]
                   propertyChanged.Trigger(self, PropertyChangedEventArgs(nameof this.ActiveDocument))
   ```
   (`this.ActiveDocument` requires `CloseDocument` to already be a `member this.CloseDocument` — it already is.)

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/FSharpQueryStudio.Ui.Tests`
Expected: PASS, full `Ui.Tests` suite green (existing tests unaffected — this only adds an interface implementation and narrows when a mutation already happening fires an event).

- [ ] **Step 6: Update DEVLOG.md and commit**

```bash
git add src/FSharpQueryStudio.Ui/DocumentViewModel.fs src/FSharpQueryStudio.Ui/ShellViewModel.fs tests/FSharpQueryStudio.Ui.Tests/DocumentViewModelTests.fs tests/FSharpQueryStudio.Ui.Tests/ShellViewModelTests.fs docs/DEVLOG.md
git commit -m "UI-Foundation.4: DocumentViewModel/ShellViewModel expose PropertyChanged"
```

---

### Task 5: `ScriptDocument` (Dock tab wrapper)

**Files:**
- Create: `src/FSharpQueryStudio.Ui/ScriptDocument.fs`
- Modify: `src/FSharpQueryStudio.Ui/FSharpQueryStudio.Ui.fsproj` (add `<Compile Include="ScriptDocument.fs" />` immediately after `<Compile Include="ShellViewModel.fs" />`, before `MainWindow.axaml.fs`)
- Test: `tests/FSharpQueryStudio.Ui.Tests/ScriptDocumentTests.fs`
- Modify: `tests/FSharpQueryStudio.Ui.Tests/FSharpQueryStudio.Ui.Tests.fsproj` (add compile item after `ShellViewModelTests.fs`)

**Interfaces:**
- Consumes: `ShellViewModel.CloseDocument(uri: string) : bool` (Task 4's `DocumentViewModel`/`ShellViewModel` `PropertyChanged`, existing `CloseDocument`); `Dock.Model.Mvvm.Controls.Document` base class (`Id`/`Title`/`Content: obj`/`OnClose() : bool`, per Dock.Avalonia's documented shape).
- Produces: `ScriptDocument(document: DocumentViewModel, shell: ShellViewModel, editorContent: obj)` — `member _.Document: DocumentViewModel`; `Title` is `"<uri>"`, or `"<uri> *"` while `document.IsDirty`; `OnClose()` delegates to and returns `shell.CloseDocument(document.Uri)`.

- [ ] **Step 1: Write the failing tests**

```fsharp
// tests/FSharpQueryStudio.Ui.Tests/ScriptDocumentTests.fs
module ScriptDocumentTests

open Xunit
open FSharpQueryStudio.Ui

[<Fact>]
let ``Title is the document's Uri when not dirty`` () =
    let shell = ShellViewModel()
    let doc = shell.ActiveDocument
    let scriptDoc = ScriptDocument(doc, shell, obj ())

    Assert.Equal(doc.Uri, scriptDoc.Title)

[<Fact>]
let ``Title gains a trailing marker once the document becomes dirty`` () =
    let shell = ShellViewModel()
    let doc = shell.ActiveDocument
    let scriptDoc = ScriptDocument(doc, shell, obj ())

    doc.ApplyTextChanged(1, "1 + 1")

    Assert.Equal(doc.Uri + " *", scriptDoc.Title)

[<Fact>]
let ``Id is the document's Uri`` () =
    let shell = ShellViewModel()
    let doc = shell.ActiveDocument
    let scriptDoc = ScriptDocument(doc, shell, obj ())

    Assert.Equal(doc.Uri, scriptDoc.Id)

[<Fact>]
let ``Content is the shared editor control instance passed to the constructor`` () =
    let shell = ShellViewModel()
    let editorControl = obj ()
    let scriptDoc = ScriptDocument(shell.ActiveDocument, shell, editorControl)

    Assert.Same(editorControl, scriptDoc.Content)

[<Fact>]
let ``OnClose returns ShellViewModel.CloseDocument's result and does not open the confirm hook when the document is clean`` () =
    let shell = ShellViewModel()
    let scriptDoc = ScriptDocument(shell.ActiveDocument, shell, obj ())

    Assert.True(scriptDoc.OnClose())

[<Fact>]
let ``OnClose returns false and keeps the tab when the document is dirty and the confirm hook denies`` () =
    let shell = ShellViewModel()
    shell.ActiveDocument.ApplyTextChanged(1, "1 + 1")
    shell.ConfirmDiscardDirtyDocument <- Some(fun _ -> false)
    let scriptDoc = ScriptDocument(shell.ActiveDocument, shell, obj ())

    Assert.False(scriptDoc.OnClose())
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FSharpQueryStudio.Ui.Tests --filter FullyQualifiedName~ScriptDocumentTests`
Expected: FAIL to compile — `ScriptDocument` doesn't exist yet.

- [ ] **Step 3: Write the implementation**

```fsharp
// src/FSharpQueryStudio.Ui/ScriptDocument.fs
namespace FSharpQueryStudio.Ui

open System.ComponentModel
open Dock.Model.Mvvm.Controls

/// Wraps one `DocumentViewModel` as a Dock.Avalonia document tab. `Content`
/// is always the same shared Monaco `NativeWebView` instance — see the
/// UI-Foundation spec's rationale for one editor control reused across
/// every open script tab, with the active tab's text pushed into it via
/// a `DocumentLoaded` bridge notification (`DockFactory` raises that).
///
/// `OnClose` routes entirely through `ShellViewModel.CloseDocument` so
/// the existing dirty-tab confirmation flow
/// (`ShellViewModel.ConfirmDiscardDirtyDocument`) stays the single source
/// of truth for "can this tab actually close" — Dock's own tab removal
/// only proceeds when that call returns true.
type ScriptDocument(document: DocumentViewModel, shell: ShellViewModel, editorContent: obj) as this =
    inherit Document()

    let updateTitle () =
        this.Title <- document.Uri + (if document.IsDirty then " *" else "")

    do
        this.Id <- document.Uri
        this.Content <- editorContent
        updateTitle ()

        (document :> INotifyPropertyChanged).PropertyChanged.Add(fun args ->
            if args.PropertyName = nameof document.IsDirty then
                updateTitle ())

    member _.Document = document

    override _.OnClose() = shell.CloseDocument(document.Uri)
```

- [ ] **Step 4: Add both files to their `.fsproj`s and run the tests**

Run: `dotnet test tests/FSharpQueryStudio.Ui.Tests --filter FullyQualifiedName~ScriptDocumentTests`
Expected: PASS, 6/6.

- [ ] **Step 5: Update DEVLOG.md and commit**

```bash
git add src/FSharpQueryStudio.Ui/ScriptDocument.fs src/FSharpQueryStudio.Ui/FSharpQueryStudio.Ui.fsproj tests/FSharpQueryStudio.Ui.Tests/ScriptDocumentTests.fs tests/FSharpQueryStudio.Ui.Tests/FSharpQueryStudio.Ui.Tests.fsproj docs/DEVLOG.md
git commit -m "UI-Foundation.5: ScriptDocument Dock tab wrapper"
```

---

### Task 6: `DockFactory`

**Files:**
- Create: `src/FSharpQueryStudio.Ui/DockFactory.fs`
- Modify: `src/FSharpQueryStudio.Ui/FSharpQueryStudio.Ui.fsproj` (add `<Compile Include="DockFactory.fs" />` immediately after `<Compile Include="ScriptDocument.fs" />`)
- Test: `tests/FSharpQueryStudio.Ui.Tests/DockFactoryTests.fs`
- Modify: `tests/FSharpQueryStudio.Ui.Tests/FSharpQueryStudio.Ui.Tests.fsproj` (add compile item after `ScriptDocumentTests.fs`)

**Interfaces:**
- Consumes: `ScriptDocument` (Task 5); `ShellViewModel.OpenDocument`/`.Documents`/`.ActiveDocument` (existing); `Dock.Model.Mvvm.Factory`, `Dock.Model.Mvvm.Controls.Tool`, `Dock.Model.Controls.{DocumentDock,ToolDock,ProportionalDock,ProportionalDockSplitter}`, `Dock.Model.Core.{IRootDock,IDocumentDock,IDockable,Alignment,Orientation}` (Dock.Avalonia's documented API).
- Produces: `DockFactory(shell: ShellViewModel, editorContent: obj, resultsContent: obj)`, `member _.OpenDocument(uri: string) : unit` (activates an existing Dock tab or opens+adds a new one — the Dock-layer half of `ShellViewModel.OpenDocument`'s dedupe), `[<CLIEvent>] member _.DocumentActivated : IEvent<DocumentViewModel>` (fires the newly active document whenever the Dock tab selection changes — `MainWindow` subscribes in Task 9 to push its text into the shared editor).

- [ ] **Step 1: Write the failing tests**

```fsharp
// tests/FSharpQueryStudio.Ui.Tests/DockFactoryTests.fs
module DockFactoryTests

open Xunit
open Dock.Model.Core
open FSharpQueryStudio.Ui

let private scriptDocuments (factory: DockFactory) : ScriptDocument list =
    factory.DocumentDock.VisibleDockables
    |> Seq.choose (function
        | :? ScriptDocument as sd -> Some sd
        | _ -> None)
    |> List.ofSeq

[<Fact>]
let ``CreateLayout produces one ScriptDocument for the shell's initial ActiveDocument`` () =
    let shell = ShellViewModel()
    let factory = DockFactory(shell, obj (), obj ())
    let layout = factory.CreateLayout()
    factory.InitLayout(layout)

    let docs = scriptDocuments factory
    Assert.Single(docs) |> ignore
    Assert.Equal(shell.ActiveDocument.Uri, docs.Head.Document.Uri)

[<Fact>]
let ``OpenDocument on a new uri adds a second ScriptDocument and updates ShellViewModel.Documents`` () =
    let shell = ShellViewModel()
    let factory = DockFactory(shell, obj (), obj ())
    let layout = factory.CreateLayout()
    factory.InitLayout(layout)

    factory.OpenDocument("untitled:Script2.fsx")

    Assert.Equal(2, (scriptDocuments factory).Length)
    Assert.Equal(2, shell.Documents.Length)

[<Fact>]
let ``OpenDocument on an already-open uri does not duplicate the tab`` () =
    let shell = ShellViewModel()
    let factory = DockFactory(shell, obj (), obj ())
    let layout = factory.CreateLayout()
    factory.InitLayout(layout)

    factory.OpenDocument("untitled:Script2.fsx")
    factory.OpenDocument("untitled:Script2.fsx")

    Assert.Equal(2, (scriptDocuments factory).Length)

[<Fact>]
let ``DocumentActivated fires the newly active document when SetActiveDockable switches tabs`` () =
    let shell = ShellViewModel()
    let factory = DockFactory(shell, obj (), obj ())
    let layout = factory.CreateLayout()
    factory.InitLayout(layout)
    factory.OpenDocument("untitled:Script2.fsx")

    let firstDoc = (scriptDocuments factory).Head
    let mutable activated: DocumentViewModel option = None
    factory.DocumentActivated.Add(fun doc -> activated <- Some doc)

    factory.SetActiveDockable(firstDoc) |> ignore

    Assert.Equal(Some firstDoc.Document.Uri, activated |> Option.map (fun d -> d.Uri))
    Assert.Equal(firstDoc.Document.Uri, shell.ActiveDocument.Uri)
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FSharpQueryStudio.Ui.Tests --filter FullyQualifiedName~DockFactoryTests`
Expected: FAIL to compile — `DockFactory` doesn't exist yet.

- [ ] **Step 3: Write the implementation**

```fsharp
// src/FSharpQueryStudio.Ui/DockFactory.fs
namespace FSharpQueryStudio.Ui

open Dock.Model.Core
open Dock.Model.Controls
open Dock.Model.Mvvm
open Dock.Model.Mvvm.Controls

/// Builds and owns the VS-shaped default dock layout (Section 5's
/// workspace/document/results panes) and keeps it in sync with
/// `ShellViewModel`'s canonical document list. `editorContent` is the
/// single shared Monaco `NativeWebView` control (see `ScriptDocument`'s
/// doc comment); `resultsContent` is the existing Results/Output/
/// Problems/Schema view, built by the caller (`MainWindow`) — `Ui` has
/// no other Avalonia-control dependency anywhere, and this keeps it
/// that way (both arrive as `obj`).
type DockFactory(shell: ShellViewModel, editorContent: obj, resultsContent: obj) as self =
    inherit Factory()

    let documentActivated = Event<DocumentViewModel>()
    let mutable documentDock: IDocumentDock = Unchecked.defaultof<_>

    /// The Dock-side half of `ShellViewModel.OpenDocument`'s dedupe —
    /// `ShellViewModel.Documents` carries no change notification Dock
    /// could observe directly, so `DockFactory.OpenDocument` (below) is
    /// the one path every "open a script" caller (File > New/Open, a
    /// later workspace-explorer double-click) goes through instead.
    let findScriptDocument (uri: string) : ScriptDocument option =
        documentDock.VisibleDockables
        |> Seq.choose (function
            | :? ScriptDocument as sd -> Some sd
            | _ -> None)
        |> Seq.tryFind (fun sd -> sd.Document.Uri = uri)

    do
        self.ActiveDockableChanged.Add(fun args ->
            match args.Dockable with
            | :? ScriptDocument as sd ->
                shell.OpenDocument(sd.Document.Uri) |> ignore
                documentActivated.Trigger(sd.Document)
            | _ -> ())

    /// The Dock tab strip for open scripts — exposed so tests can assert
    /// on its `VisibleDockables` without reaching through `CreateLayout`'s
    /// return value.
    member _.DocumentDock = documentDock

    [<CLIEvent>]
    member _.DocumentActivated = documentActivated.Publish

    override this.CreateLayout() : IRootDock =
        let initialDoc = ScriptDocument(shell.ActiveDocument, shell, editorContent)

        let workspaceTool = Tool(Id = "Workspace", Title = "Workspace", CanClose = false)
        let resultsTool = Tool(Id = "Results", Title = "Results", CanClose = false, Content = resultsContent)

        documentDock <-
            DocumentDock(
                Id = "Documents",
                IsCollapsable = false,
                CanCreateDocument = false,
                ActiveDockable = initialDoc,
                VisibleDockables = this.CreateList<IDockable>(initialDoc)
            )

        let workspaceDock =
            ToolDock(
                Id = "WorkspacePane",
                Alignment = Alignment.Left,
                Proportion = 0.2,
                ActiveDockable = workspaceTool,
                VisibleDockables = this.CreateList<IDockable>(workspaceTool)
            )

        let resultsDock =
            ToolDock(
                Id = "ResultsPane",
                Alignment = Alignment.Bottom,
                Proportion = 0.3,
                ActiveDockable = resultsTool,
                VisibleDockables = this.CreateList<IDockable>(resultsTool)
            )

        let centerColumn =
            ProportionalDock(
                Orientation = Orientation.Vertical,
                VisibleDockables = this.CreateList<IDockable>(documentDock, ProportionalDockSplitter(), resultsDock)
            )

        let mainLayout =
            ProportionalDock(
                Orientation = Orientation.Horizontal,
                VisibleDockables =
                    this.CreateList<IDockable>(workspaceDock, ProportionalDockSplitter(), centerColumn)
            )

        let root = this.CreateRootDock()
        root.IsCollapsable <- false
        root.DefaultDockable <- mainLayout
        root.ActiveDockable <- mainLayout
        root.VisibleDockables <- this.CreateList<IDockable>(mainLayout)
        root.LeftPinnedDockables <- this.CreateList<IDockable>()
        root.RightPinnedDockables <- this.CreateList<IDockable>()
        root.TopPinnedDockables <- this.CreateList<IDockable>()
        root.BottomPinnedDockables <- this.CreateList<IDockable>()
        root

    /// Opens `uri` as a Dock tab, activating the existing one instead of
    /// duplicating it if it's already open — see `findScriptDocument`'s
    /// comment for why this, not `ShellViewModel.OpenDocument` directly,
    /// is the entry point every "open a script" caller uses.
    member this.OpenDocument(uri: string) : unit =
        match findScriptDocument uri with
        | Some existing -> this.SetActiveDockable(existing) |> ignore
        | None ->
            let doc = shell.OpenDocument(uri)
            let scriptDoc = ScriptDocument(doc, shell, editorContent)
            documentDock.VisibleDockables.Add(scriptDoc)
            this.SetActiveDockable(scriptDoc) |> ignore
```

- [ ] **Step 4: Add both files to their `.fsproj`s and run the tests**

Run: `dotnet test tests/FSharpQueryStudio.Ui.Tests --filter FullyQualifiedName~DockFactoryTests`
Expected: PASS, 4/4. If `SetActiveDockable` doesn't raise `ActiveDockableChanged` in the installed Dock.Avalonia version (the fourth test would fail with `activated = None` rather than a compile error), that's real information the test caught — call `self.SetActiveDockable` from inside `OpenDocument`/wherever needed and additionally raise `documentActivated` directly at that call site so the event contract holds regardless of the library's own event timing.

- [ ] **Step 5: Update DEVLOG.md and commit**

```bash
git add src/FSharpQueryStudio.Ui/DockFactory.fs src/FSharpQueryStudio.Ui/FSharpQueryStudio.Ui.fsproj tests/FSharpQueryStudio.Ui.Tests/DockFactoryTests.fs tests/FSharpQueryStudio.Ui.Tests/FSharpQueryStudio.Ui.Tests.fsproj docs/DEVLOG.md
git commit -m "UI-Foundation.6: DockFactory"
```

---

### Task 7: `DocumentLoaded` host→editor bridge notification

Swaps the shared Monaco instance's content when `DockFactory.DocumentActivated` fires (wired in Task 9). Same shape/pattern as the existing `CaretMoved`/`DiagnosticsUpdated` notifications from Milestone B/E.

**Files:**
- Modify: `src/FSharpQueryStudio.Protocol/HostNotification.fs`
- Modify: `src/FSharpQueryStudio.Editor/EditorBridge.fs`
- Modify: `src/FSharpQueryStudio.Editor/EditorAssets/editor-host.html`
- Test: `tests/FSharpQueryStudio.Editor.Tests/EditorBridgeTests.fs` (add cases)

**Interfaces:**
- Produces: `HostNotification.DocumentLoaded of uri: string * version: int * text: string`; `EditorBridge.encodeNotification` handles it, `Kind = "documentLoaded"`, payload `{ documentUri, version, text }`.

- [ ] **Step 1: Read the existing payload/notification shapes**

Read `src/FSharpQueryStudio.Protocol/HostNotification.fs` and `src/FSharpQueryStudio.Protocol/Payloads.fs` in full before editing — `CaretMoved`'s case and `CaretMovedPayload` are the exact pattern to mirror (this step has no code of its own; it's what makes step 3 match the existing DU-case/payload-record naming convention instead of guessing it).

- [ ] **Step 2: Write the failing test**

Append to `tests/FSharpQueryStudio.Editor.Tests/EditorBridgeTests.fs` (mirroring whatever existing `CaretMoved`-encoding test is already there):
```fsharp
[<Fact>]
let ``encodeNotification for DocumentLoaded produces kind documentLoaded with uri version and text`` () =
    let envelope = EditorBridge.encodeNotification (DocumentLoaded("untitled:Script2.fsx", 3, "let x = 1"))

    Assert.Equal("documentLoaded", envelope.Kind)

    let payload =
        System.Text.Json.JsonSerializer.Deserialize<DocumentLoadedPayload>(envelope.Payload, Serialization.options)

    Assert.Equal("untitled:Script2.fsx", payload.DocumentUri)
    Assert.Equal(3, payload.Version)
    Assert.Equal("let x = 1", payload.Text)
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/FSharpQueryStudio.Editor.Tests --filter FullyQualifiedName~DocumentLoaded`
Expected: FAIL to compile — `DocumentLoaded`/`DocumentLoadedPayload` don't exist yet.

- [ ] **Step 4: Add the DU case and payload record**

In `src/FSharpQueryStudio.Protocol/HostNotification.fs`, add a case to the `HostNotification` DU alongside `CaretMoved`:
```fsharp
    | DocumentLoaded of uri: string * version: int * text: string
```

In `src/FSharpQueryStudio.Protocol/Payloads.fs`, add a record alongside `CaretMovedPayload`:
```fsharp
type DocumentLoadedPayload =
    { DocumentUri: string
      Version: int
      Text: string }
```

- [ ] **Step 5: Handle the case in `EditorBridge.encodeNotification`**

In `src/FSharpQueryStudio.Editor/EditorBridge.fs`, add a branch to `encodeNotification`'s `match`, alongside `CaretMoved`:
```fsharp
        | DocumentLoaded(uri, version, text) ->
            { Id = Guid.NewGuid().ToString()
              Kind = "documentLoaded"
              Payload =
                JsonSerializer.SerializeToElement<DocumentLoadedPayload>(
                    { DocumentUri = uri
                      Version = version
                      Text = text },
                    Serialization.options
                ) }
```

- [ ] **Step 6: Handle `documentLoaded` in `editor-host.html`**

In `src/FSharpQueryStudio.Editor/EditorAssets/editor-host.html`, two changes:

1. Add a `suppressChangeEvent` guard so loading a document programmatically doesn't fire a spurious `textChanged` (which would immediately re-mark the freshly-opened tab dirty). Change:
```javascript
        var documentUri = "untitled:Script1.fsx";
        var currentVersion = 0;

        editor.onDidChangeModelContent(function () {
          currentVersion += 1;
          postToHost("textChanged", {
            documentUri: documentUri,
            version: currentVersion,
            source: editor.getValue()
          });
        });
```
to:
```javascript
        var documentUri = "untitled:Script1.fsx";
        var currentVersion = 0;
        var suppressChangeEvent = false;

        editor.onDidChangeModelContent(function () {
          if (suppressChangeEvent) {
            return;
          }
          currentVersion += 1;
          postToHost("textChanged", {
            documentUri: documentUri,
            version: currentVersion,
            source: editor.getValue()
          });
        });
```

2. Add a `case "documentLoaded":` branch to the `switch (envelope.kind)` block, alongside `"caretMoved"`/`"diagnosticsUpdated"`:
```javascript
              case "documentLoaded": {
                suppressChangeEvent = true;
                documentUri = envelope.payload.documentUri;
                currentVersion = envelope.payload.version;
                editor.setValue(envelope.payload.text);
                suppressChangeEvent = false;
                break;
              }
```

- [ ] **Step 7: Run the Editor.Tests test**

Run: `dotnet test tests/FSharpQueryStudio.Editor.Tests --filter FullyQualifiedName~DocumentLoaded`
Expected: PASS. Run `dotnet test` for the full solution too — the new DU case must not break any existing exhaustive `match` on `HostNotification` (the compiler will flag any that need a new branch; add one matching the existing style at each site).

- [ ] **Step 8: Manual verification (deferred to Task 14)**

The actual "switching Dock tabs swaps Monaco's visible content without flashing a phantom dirty marker" behavior isn't headlessly testable — same rationale as every prior editor-host.html change in this codebase (B.6, B.9, E.6, E.7). Flagged here, checked for real in Task 14's manual pass alongside the rest of the docking shell.

- [ ] **Step 9: Update DEVLOG.md and commit**

```bash
git add src/FSharpQueryStudio.Protocol/HostNotification.fs src/FSharpQueryStudio.Protocol/Payloads.fs src/FSharpQueryStudio.Editor/EditorBridge.fs src/FSharpQueryStudio.Editor/EditorAssets/editor-host.html tests/FSharpQueryStudio.Editor.Tests/EditorBridgeTests.fs docs/DEVLOG.md
git commit -m "UI-Foundation.7: DocumentLoaded host-to-editor bridge notification"
```

---

### Task 8: VS2026 theme resource dictionaries

**Files:**
- Create: `src/FSharpQueryStudio.App/Themes/VsDark.axaml`
- Create: `src/FSharpQueryStudio.App/Themes/VsLight.axaml`
- Modify: `src/FSharpQueryStudio.App/App.axaml`

**Interfaces:** None new at the code level — pure XAML resources, consumed by whatever Task 9's `MainWindow.axaml` binds against (`{DynamicResource ...}` keys defined here).

- [ ] **Step 1: Write `VsDark.axaml`**

```xaml
<!-- src/FSharpQueryStudio.App/Themes/VsDark.axaml -->
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Color x:Key="WindowBackgroundColor">#1E1E1E</Color>
  <Color x:Key="PanelBackgroundColor">#252526</Color>
  <Color x:Key="ChromeBackgroundColor">#2D2D30</Color>
  <Color x:Key="AccentColor">#007ACC</Color>
  <Color x:Key="PrimaryTextColor">#CCCCCC</Color>
  <Color x:Key="BorderColor">#3F3F46</Color>

  <SolidColorBrush x:Key="WindowBackgroundBrush" Color="{StaticResource WindowBackgroundColor}" />
  <SolidColorBrush x:Key="PanelBackgroundBrush" Color="{StaticResource PanelBackgroundColor}" />
  <SolidColorBrush x:Key="ChromeBackgroundBrush" Color="{StaticResource ChromeBackgroundColor}" />
  <SolidColorBrush x:Key="AccentBrush" Color="{StaticResource AccentColor}" />
  <SolidColorBrush x:Key="PrimaryTextBrush" Color="{StaticResource PrimaryTextColor}" />
  <SolidColorBrush x:Key="BorderBrush" Color="{StaticResource BorderColor}" />
</ResourceDictionary>
```

- [ ] **Step 2: Write `VsLight.axaml`**

```xaml
<!-- src/FSharpQueryStudio.App/Themes/VsLight.axaml -->
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Color x:Key="WindowBackgroundColor">#F3F3F3</Color>
  <Color x:Key="PanelBackgroundColor">#FFFFFF</Color>
  <Color x:Key="ChromeBackgroundColor">#F0F0F0</Color>
  <Color x:Key="AccentColor">#007ACC</Color>
  <Color x:Key="PrimaryTextColor">#1E1E1E</Color>
  <Color x:Key="BorderColor">#D6D6D6</Color>

  <SolidColorBrush x:Key="WindowBackgroundBrush" Color="{StaticResource WindowBackgroundColor}" />
  <SolidColorBrush x:Key="PanelBackgroundBrush" Color="{StaticResource PanelBackgroundColor}" />
  <SolidColorBrush x:Key="ChromeBackgroundBrush" Color="{StaticResource ChromeBackgroundColor}" />
  <SolidColorBrush x:Key="AccentBrush" Color="{StaticResource AccentColor}" />
  <SolidColorBrush x:Key="PrimaryTextBrush" Color="{StaticResource PrimaryTextColor}" />
  <SolidColorBrush x:Key="BorderBrush" Color="{StaticResource BorderColor}" />
</ResourceDictionary>
```

- [ ] **Step 3: Wire both into `App.axaml` behind `ThemeVariant`, plus the Dock Fluent theme**

Replace `src/FSharpQueryStudio.App/App.axaml`'s contents:
```xaml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="FSharpQueryStudio.App.App"
             RequestedThemeVariant="Dark">
  <Application.Resources>
    <ResourceDictionary>
      <ResourceDictionary.ThemeDictionaries>
        <ResourceInclude x:Key="Dark" Source="avares://FSharpQueryStudio.App/Themes/VsDark.axaml" />
        <ResourceInclude x:Key="Light" Source="avares://FSharpQueryStudio.App/Themes/VsLight.axaml" />
      </ResourceDictionary.ThemeDictionaries>
    </ResourceDictionary>
  </Application.Resources>
  <Application.Styles>
    <FluentTheme />
    <StyleInclude Source="avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml" />
    <StyleInclude Source="avares://Dock.Avalonia.Themes.Fluent/DockFluentTheme.axaml" />
  </Application.Styles>
</Application>
```
`RequestedThemeVariant="Dark"` is the startup default (matches `ThemeService`'s own `Theme.Dark` default from Task 1) — Task 12 overrides it at startup from `UiState`, and Task 11's `View > Theme` toggle changes it at runtime by setting `Application.Current.RequestedThemeVariant` directly (not by touching this file again).

- [ ] **Step 4: Build the solution**

Run: `dotnet build`
Expected: succeeds — this task adds resources nothing yet consumes, so nothing should regress. If `avares://FSharpQueryStudio.App/Themes/VsDark.axaml` 404s at runtime later (Task 14's manual pass), check that both `.axaml` files are picked up as `AvaloniaResource` — per A.10's devlog entry, the SDK auto-globs `**/*.axaml`, so no explicit `<AvaloniaResource Include>` should be needed or added.

- [ ] **Step 5: Update DEVLOG.md and commit**

```bash
git add src/FSharpQueryStudio.App/Themes/VsDark.axaml src/FSharpQueryStudio.App/Themes/VsLight.axaml src/FSharpQueryStudio.App/App.axaml docs/DEVLOG.md
git commit -m "UI-Foundation.8: VS2026 dark/light theme resource dictionaries"
```

---

### Task 9: `MainWindow.axaml` — `DockControl` shell, icon toolbar, status bar

The largest task in this plan and the one place XAML markup itself changes wholesale. No new automated test surface — same manual-verify rationale as every prior XAML-rendering task in this codebase (A.10, A.13, A.16).

**Files:**
- Modify: `src/FSharpQueryStudio.Ui/MainWindow.axaml`
- Modify: `src/FSharpQueryStudio.Ui/MainWindow.axaml.fs`

**Interfaces:**
- Consumes: `DockFactory` (Task 6), `ScriptDocument` (Task 5), `Theme`/`ThemeService` (Task 1), `Material.Icons.Avalonia`'s `MaterialIcon` control, existing `ShellViewModel` members (`RunAsync`/`Cancel`/`CopyResultAsync`/`ResetSessionAsync`/`WorkerState`/`Result`/`Problems`).
- Produces: `MainWindow.ThemeService: ThemeService` (new public member, so `App.axaml.fs` can wire persistence in Task 12); existing `member _.ShellViewModel` and `member _.WireExecution` are unchanged.

- [ ] **Step 1: Move the Results/Output/Problems/Schema panel into its own `UserControl`**

Create `src/FSharpQueryStudio.Ui/ResultsView.axaml`, containing exactly today's Results `TabControl` (moved verbatim from `MainWindow.axaml`, now also binding the previously-empty `Problems` tab and adding a `Schema` list bound to `Result.Schema` — the data's existed since Milestone A/B, this is the first time it's displayed):
```xaml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="FSharpQueryStudio.Ui.ResultsView"
             x:CompileBindings="False">
  <TabControl Name="ResultsTabs">
    <TabItem Header="Results">
      <Grid>
        <TextBlock Text="{Binding Result.DisplayText}"
                   IsVisible="{Binding Result.IsScalarOrText}"
                   Margin="8"
                   TextWrapping="Wrap" />
        <DataGrid Name="ResultsGrid"
                  ItemsSource="{Binding Result.Rows}"
                  IsVisible="{Binding Result.IsTable}"
                  AutoGenerateColumns="False"
                  IsReadOnly="True" />
      </Grid>
    </TabItem>
    <TabItem Header="Output">
      <ListBox ItemsSource="{Binding Result.Output}" Name="OutputList">
        <ListBox.ItemTemplate>
          <DataTemplate>
            <TextBlock Text="{Binding Text}" FontFamily="Consolas,Cascadia Mono,monospace" />
          </DataTemplate>
        </ListBox.ItemTemplate>
      </ListBox>
    </TabItem>
    <TabItem Header="Problems">
      <ListBox ItemsSource="{Binding Problems}" Name="ProblemsList">
        <ListBox.ItemTemplate>
          <DataTemplate>
            <TextBlock Text="{Binding Diagnostic.Message}" DoubleTapped="ProblemDoubleTapped" />
          </DataTemplate>
        </ListBox.ItemTemplate>
      </ListBox>
    </TabItem>
    <TabItem Header="Schema">
      <ListBox ItemsSource="{Binding Result.Schema}" Name="SchemaList">
        <ListBox.ItemTemplate>
          <DataTemplate>
            <TextBlock Text="{Binding Name}" />
          </DataTemplate>
        </ListBox.ItemTemplate>
      </ListBox>
    </TabItem>
  </TabControl>
</UserControl>
```

Create `src/FSharpQueryStudio.Ui/ResultsView.axaml.fs`:
```fsharp
namespace FSharpQueryStudio.Ui

open Avalonia.Controls
open Avalonia.Input
open Avalonia.Markup.Xaml

type ResultsView() =
    inherit UserControl()

    do AvaloniaXamlLoader.Load(this)

    member private this.ProblemDoubleTapped(sender: obj, _args: TappedEventArgs) =
        match (sender :?> Control).DataContext with
        | :? ProblemViewModel as p -> p.Navigate()
        | _ -> ()
```
Add both to `FSharpQueryStudio.Ui.fsproj`: `<Compile Include="ResultsView.axaml.fs" />` right after `<Compile Include="DockFactory.fs" />` (XAML files themselves need no `<Compile>` entry — same as `MainWindow.axaml`'s existing convention, they're picked up as `AvaloniaResource` automatically).

- [ ] **Step 2: Rewrite `MainWindow.axaml`**

```xaml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:dock="using:Dock.Avalonia.Controls"
        xmlns:mi="using:Material.Icons.Avalonia"
        x:Class="FSharpQueryStudio.Ui.MainWindow"
        x:CompileBindings="False"
        Title="F# Query Studio"
        Width="1400"
        Height="900"
        Background="{DynamicResource WindowBackgroundBrush}">
  <DockPanel>
    <Menu DockPanel.Dock="Top" Background="{DynamicResource ChromeBackgroundBrush}">
      <MenuItem Header="_File">
        <MenuItem Header="_New" Name="NewMenuItem" />
        <MenuItem Header="_Open..." Name="OpenMenuItem" />
        <MenuItem Header="_Save" Name="SaveMenuItem" />
        <MenuItem Header="Save _As..." Name="SaveAsMenuItem" />
        <Separator />
        <MenuItem Header="E_xit" Name="ExitMenuItem" />
      </MenuItem>
      <MenuItem Header="_Edit" IsEnabled="False" />
      <MenuItem Header="_View">
        <MenuItem Header="Toggle _Theme" Name="ToggleThemeMenuItem" />
        <Separator />
        <MenuItem Header="_Workspace" Name="ToggleWorkspaceMenuItem" />
        <MenuItem Header="_Results" Name="ToggleResultsMenuItem" />
      </MenuItem>
      <MenuItem Header="_Connections" IsEnabled="False" />
      <MenuItem Header="_Run">
        <MenuItem Header="_Run (F5)" Name="RunMenuItem" />
        <MenuItem Header="_Cancel" Name="CancelMenuItem" />
        <MenuItem Header="_Reset Session" Name="ResetSessionMenuItem" />
      </MenuItem>
    </Menu>

    <Border DockPanel.Dock="Top" Background="{DynamicResource ChromeBackgroundBrush}" Padding="4">
      <StackPanel Orientation="Horizontal">
        <Button Name="NewButton" ToolTip.Tip="New">
          <mi:MaterialIcon Kind="FilePlus" Width="16" Height="16" />
        </Button>
        <Button Name="OpenButton" ToolTip.Tip="Open" Margin="4,0,0,0">
          <mi:MaterialIcon Kind="FolderOpen" Width="16" Height="16" />
        </Button>
        <Button Name="SaveButton" ToolTip.Tip="Save" Margin="4,0,0,0">
          <mi:MaterialIcon Kind="ContentSave" Width="16" Height="16" />
        </Button>
        <Separator Margin="8,0" />
        <Button Name="RunButton" ToolTip.Tip="Run (F5)">
          <mi:MaterialIcon Kind="Play" Width="16" Height="16" />
        </Button>
        <Button Name="CancelButton" ToolTip.Tip="Cancel" Margin="4,0,0,0">
          <mi:MaterialIcon Kind="Stop" Width="16" Height="16" />
        </Button>
        <Button Name="CopyResultButton" ToolTip.Tip="Copy Result" Margin="4,0,0,0">
          <mi:MaterialIcon Kind="ContentCopy" Width="16" Height="16" />
        </Button>
        <Button Name="ResetSessionButton" ToolTip.Tip="Reset Session" Margin="4,0,0,0">
          <mi:MaterialIcon Kind="Restore" Width="16" Height="16" />
        </Button>
      </StackPanel>
    </Border>

    <Border DockPanel.Dock="Bottom" Background="{DynamicResource ChromeBackgroundBrush}" Padding="8,2">
      <StackPanel Orientation="Horizontal" Spacing="16">
        <TextBlock Text="{Binding WorkerState.Current}" Foreground="{DynamicResource PrimaryTextBrush}" />
        <TextBlock Text="{Binding ActiveDocument.SessionStateDisplay}" Foreground="{DynamicResource PrimaryTextBrush}" />
      </StackPanel>
    </Border>

    <dock:DockControl Name="MainDock" />
  </DockPanel>
</Window>
```
`InitializeFactory`/`InitializeLayout` are deliberately left off — Step 3 assigns `Factory`/`Layout` and calls `InitLayout` explicitly in code-behind (the plain-C# init sequence Dock.Avalonia's own docs show), so relying on the XAML auto-init flags too would be a redundant second init path with unclear ordering against the manual one.
Note this drops `NativeWebView`/`ResultsTabs`/`RunButton` etc. as directly-nested markup — the editor and results content are now built in code-behind (Step 3) and handed to `DockFactory` as `Content`, since `DockFactory.CreateLayout()` (Task 6) needs live control instances, not XAML-declared ones, to assign to `ScriptDocument.Content`/`resultsTool.Content`. `RunButton`/`CancelButton`/etc. names are kept identical to today's so `MainWindow.axaml.fs`'s existing `FindControl` calls need no renaming.

- [ ] **Step 3: Rewrite `MainWindow.axaml.fs`**

```fsharp
namespace FSharpQueryStudio.Ui

open System
open System.ComponentModel
open System.IO
open Avalonia.Controls
open Avalonia.Data
open Avalonia.Interactivity
open Avalonia.Markup.Xaml
open Avalonia.Styling
open Dock.Avalonia.Controls
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Editor

type MainWindow() as this =
    inherit Window()

    let mutable assetHost: AssetHost.Host option = None

    let shellViewModel = ShellViewModel()
    let themeService = ThemeService()

    let webView = NativeWebView()
    let resultsView = ResultsView(DataContext = shellViewModel)
    let dockFactory = DockFactory(shellViewModel, webView, resultsView)

    do
        AvaloniaXamlLoader.Load(this)
        this.DataContext <- shellViewModel

        let dockControl = this.FindControl<DockControl>("MainDock")
        let layout = dockFactory.CreateLayout()
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
            | Ok _otherRequest -> ()
            | Result.Error _err -> ())

        // Pushes the newly active tab's text into the shared editor —
        // see ScriptDocument/DockFactory's doc comments (Tasks 5-6) and
        // the DocumentLoaded notification (Task 7).
        dockFactory.DocumentActivated.Add(fun doc ->
            let envelope =
                EditorBridge.encodeNotification (DocumentLoaded(doc.Uri, doc.Version, doc.Text))

            webView.PostWebMessage(System.Text.Json.JsonSerializer.Serialize(envelope, Serialization.options)))

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

        this.Closed.Add(fun _ ->
            match assetHost with
            | Some host -> (host :> IDisposable).Dispose()
            | None -> ())

    member _.ShellViewModel = shellViewModel
    member _.ThemeService = themeService
    member _.DockFactory = dockFactory

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
```
`webView.PostWebMessage(...)` matches `NativeWebView`'s existing outbound-message shape from Avalonia.Controls.WebView — if the exact method name differs in the installed `Avalonia.Controls.WebView` version (`PostWebMessage` vs `PostWebMessageAsJson`/`PostWebMessageAsString`), the compiler error names the real one; use whichever it reports.

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: succeeds. Fix any XAML-name/compile mismatches the same way A.16's devlog entry describes handling them (e.g. `AVLN3000`-style property-name errors) — this step's job is "it compiles," not "it renders correctly," which Task 14 checks manually.

- [ ] **Step 5: Update DEVLOG.md and commit**

```bash
git add src/FSharpQueryStudio.Ui/ResultsView.axaml src/FSharpQueryStudio.Ui/ResultsView.axaml.fs src/FSharpQueryStudio.Ui/MainWindow.axaml src/FSharpQueryStudio.Ui/MainWindow.axaml.fs src/FSharpQueryStudio.Ui/FSharpQueryStudio.Ui.fsproj docs/DEVLOG.md
git commit -m "UI-Foundation.9: DockControl shell, icon toolbar, status bar"
```

---

### Task 10: File New/Open/Save

**Files:**
- Modify: `src/FSharpQueryStudio.Ui/MainWindow.axaml.fs`

**Interfaces:**
- Consumes: `Avalonia.Platform.Storage.IStorageProvider` (via `this.StorageProvider`, standard Avalonia 11+/12 file-dialog API), `DockFactory.OpenDocument` (Task 6), `ShellViewModel.ActiveDocument.Text`.

- [ ] **Step 1: Wire the three menu items and toolbar buttons**

In `MainWindow.axaml.fs`'s `do` block, after the theme-toggle wiring from Task 9, add:
```fsharp
        let openFsxFilter =
            FilePickerFileType("F# Script") |> fun t -> t.Patterns <- [ "*.fsx" ]; t

        let newDocument () =
            let uri = $"untitled:Script{shellViewModel.Documents.Length + 1}.fsx"
            dockFactory.OpenDocument(uri)

        let openDocument () =
            task {
                let options = FilePickerOpenOptions(AllowMultiple = false, FileTypeFilter = [ openFsxFilter ])
                let! files = this.StorageProvider.OpenFilePickerAsync(options)

                match files |> Seq.tryHead with
                | None -> ()
                | Some file ->
                    let path = file.Path.LocalPath
                    let text = File.ReadAllText(path)
                    dockFactory.OpenDocument(path)
                    let doc = shellViewModel.Documents |> List.find (fun d -> d.Uri = path)
                    doc.ApplyTextChanged(doc.Version + 1, text)
            }
            :> System.Threading.Tasks.Task

        let saveActiveDocument () =
            let doc = shellViewModel.ActiveDocument

            if doc.Uri.StartsWith("untitled:") then
                task {
                    let options = FilePickerSaveOptions(FileTypeChoices = [ openFsxFilter ], SuggestedFileName = "Script1.fsx")
                    let! file = this.StorageProvider.SaveFilePickerAsync(options)

                    match file with
                    | None -> ()
                    | Some f -> File.WriteAllText(f.Path.LocalPath, doc.Text)
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

                match file with
                | None -> ()
                | Some f -> File.WriteAllText(f.Path.LocalPath, doc.Text)
            }
            |> ignore)

        this.FindControl<MenuItem>("ExitMenuItem").Click.Add(fun _ ->
            (this.VisualRoot :?> Window).Close())
```
Add `open Avalonia.Platform.Storage` to the file's opens. `ApplyTextChanged` after `dockFactory.OpenDocument` in `openDocument` is what actually loads the file's real text into the new tab's `DocumentViewModel` — `DockFactory.OpenDocument`/`ShellViewModel.OpenDocument` only create an empty tab keyed by URI, they don't read any file themselves (matching this codebase's existing split: `Ui`/`Editor` own document state, `Storage`/plain `System.IO` own file bytes).

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: succeeds.

- [ ] **Step 3: Update DEVLOG.md and commit**

```bash
git add src/FSharpQueryStudio.Ui/MainWindow.axaml.fs docs/DEVLOG.md
git commit -m "UI-Foundation.10: File New/Open/Save/Save As"
```

---

### Task 11: View menu — theme toggle already wired (Task 9); tool-window visibility

Adds the two `View` menu items left unwired in Task 9 (`ToggleWorkspaceMenuItem`/`ToggleResultsMenuItem`).

**Files:**
- Modify: `src/FSharpQueryStudio.Ui/MainWindow.axaml.fs`

**Interfaces:**
- Consumes: `Dock.Model.Core.IFactory`'s dockable visibility — `Factory` inherited members for hiding/showing a `Tool` (`Factory.PinDockable`/hide via `RemoveDockable`+`AddDockable`, or a simpler `IsVisible`-style toggle if the installed version exposes one directly).

- [ ] **Step 1: Wire visibility toggles**

Append, inside the same `do` block:
```fsharp
        let toggleToolVisibility (toolId: string) =
            match dockFactory.DocumentDock.Owner with
            | _ ->
                // Dock.Avalonia's own hide/show API for a named tool —
                // confirm the exact member against the installed
                // version's IntelliSense (this plan's research found
                // Factory.RemoveDockable/AddDockable as the underlying
                // primitives; a newer version may expose IsVisible or
                // HideTool/ShowTool directly, which is simpler if
                // present — prefer that if it exists).
                ()

        this.FindControl<MenuItem>("ToggleWorkspaceMenuItem").Click.Add(fun _ -> toggleToolVisibility "Workspace")
        this.FindControl<MenuItem>("ToggleResultsMenuItem").Click.Add(fun _ -> toggleToolVisibility "Results")
```

- [ ] **Step 2: Replace the placeholder with the real API**

Before writing final code, check the installed package's actual surface:
```bash
dotnet build src/FSharpQueryStudio.Ui/FSharpQueryStudio.Ui.fsproj -v:normal 2>&1 | grep -i "Dock.Model"
```
then inspect `Dock.Model.Core.IFactory`'s members (e.g. via `ildasm`/IDE IntelliSense/decompiling the installed package under `~/.nuget/packages/dock.model.mvvm/`). Replace `toggleToolVisibility`'s body with the real hide/show call against the `Tool` instance found via `dockFactory.DocumentDock`'s sibling `workspaceDock`/`resultsDock` (exposed as two more `member _.WorkspaceDock`/`member _.ResultsDock` properties on `DockFactory`, added the same way `DocumentDock` already is in Task 6, if the chosen API needs the `IToolDock` rather than just the `Tool`'s id).

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: succeeds.

- [ ] **Step 4: Update DEVLOG.md and commit**

```bash
git add src/FSharpQueryStudio.Ui/MainWindow.axaml.fs src/FSharpQueryStudio.Ui/DockFactory.fs docs/DEVLOG.md
git commit -m "UI-Foundation.11: View menu tool-window visibility toggles"
```

---

### Task 12: Layout + theme persistence wiring

**Files:**
- Modify: `src/FSharpQueryStudio.App/App.axaml.fs`

**Interfaces:**
- Consumes: `FSharpQueryStudio.Storage.UiState.read`/`.write` (Task 2), `FSharpQueryStudio.Logging.LoggerSetup.defaultLogsDirectory`-style app-data path pattern, `Dock.Serializer.SystemTextJson`'s serializer type (whatever `dotnet add package` actually resolved in Task 3 — check its real type name in `~/.nuget/packages/` once installed; the Context7 research for this plan found the general shape as `new DockSerializer(typeof<ObservableCollection<_>>)` for the Newtonsoft variant, and a same-shaped `SystemTextJsonDockSerializer`-or-similarly-named type for the System.Text.Json one — confirm the exact class name against the installed package before writing this task's code, the same way Task 11 confirms `IFactory`'s hide/show members).
- Produces: `App.axaml.fs` loads `UiState` before constructing `MainWindow`, applies the saved theme, and — once `DockFactory.CreateLayout()` has run — restores a saved layout if present; saves both on `MainWindow.Closing`.

- [ ] **Step 1: Add the app-data directory helper and load on startup**

In `App.axaml.fs`, before `let mainWindow = MainWindow()`, add:
```fsharp
            let appDataDir =
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FSharpQueryStudio")

            let uiState = FSharpQueryStudio.Storage.UiState.read appDataDir

            let startupTheme =
                match uiState.Theme with
                | "Light" -> Theme.Light
                | _ -> Theme.Dark
```

- [ ] **Step 2: Apply the loaded theme once `mainWindow` exists**

Right after `let mainWindow = MainWindow()`:
```fsharp
            if startupTheme = Theme.Light then
                mainWindow.ThemeService.Toggle()
                this.RequestedThemeVariant <- ThemeVariant.Light
```
(`ThemeService` starts `Dark`; `Toggle()` is the only mutator it exposes, matching Task 1's design — calling it once when the saved state says `Light` is the correct, minimal way to reach that state without adding a second setter just for this one call site.)

- [ ] **Step 3: Restore the saved dock layout, if any**

After `mainWindow.WireExecution(...)`, before `desktop.MainWindow <- mainWindow`:
```fsharp
            match uiState.DockLayout with
            | None -> ()
            | Some layoutJson ->
                try
                    let json = layoutJson.GetRawText()
                    let restored = <SerializerTypeFromTask3>().Deserialize<IRootDock>(json)

                    match restored with
                    | null -> ()
                    | restored ->
                        mainWindow.DockFactory.InitLayout(restored)
                        // Assign onto the real DockControl the same way
                        // MainWindow.axaml.fs's constructor does for the
                        // freshly-built layout (Task 9, Step 3) — expose
                        // a `member _.RestoreLayout(root: IRootDock)` on
                        // MainWindow that does `dockControl.Layout <- root`
                        // if DockControl isn't otherwise reachable here.
                        mainWindow.RestoreLayout(restored)
                with ex ->
                    Log.Warning(ex, "Failed to restore saved dock layout; using the default layout")
```
Add a `member _.RestoreLayout(root: IRootDock) = dockControl.Layout <- root` to `MainWindow` in `MainWindow.axaml.fs` (capture `dockControl` in a `let` outside the `do` block, alongside `webView`/`resultsView`, so this member can reach it) — `<SerializerTypeFromTask3>` is a placeholder in this plan only because the exact class name depends on which package `dotnet add package Dock.Serializer.SystemTextJson` actually resolved in Task 3; replace it with that real type the moment this task starts, before writing any other line of this step.

- [ ] **Step 4: Save on close**

Add, right after `mainWindow.Opened.Add(...)`:
```fsharp
            mainWindow.Closing.Add(fun _ ->
                let layoutJson =
                    try
                        let serializer = <SerializerTypeFromTask3>()
                        let json = serializer.Serialize(mainWindow.MainDockLayout)
                        Some(System.Text.Json.JsonDocument.Parse(json).RootElement)
                    with ex ->
                        Log.Warning(ex, "Failed to serialize dock layout for persistence")
                        None

                let theme = match mainWindow.ThemeService.Current with | Theme.Light -> "Light" | Theme.Dark -> "Dark"

                FSharpQueryStudio.Storage.UiState.write appDataDir { Theme = theme; DockLayout = layoutJson })
```
Add `member _.MainDockLayout = dockControl.Layout` to `MainWindow`, same capture as `RestoreLayout`.

- [ ] **Step 5: Build and manually verify persistence**

Run: `dotnet build`. Then, per this task's own manual-verify nature (no automated test touches real serialization against a real `DockControl`): launch the built app, move a panel, close it, relaunch, confirm the moved layout comes back — recorded as part of Task 14's consolidated manual pass, not repeated here.

- [ ] **Step 6: Update DEVLOG.md and commit**

```bash
git add src/FSharpQueryStudio.App/App.axaml.fs src/FSharpQueryStudio.Ui/MainWindow.axaml.fs docs/DEVLOG.md
git commit -m "UI-Foundation.12: persist dock layout and theme across restarts"
```

---

### Task 13: Update `fs-studio-plan.md` Section 5

**Files:**
- Modify: `docs/fs-studio-plan.md`

- [ ] **Step 1: Replace Section 5's layout description**

Replace the `## 5. Avalonia UX and Layout` section's opening paragraph and ASCII diagram (the "Use a straightforward `Grid`... Avoid a complex docking framework" text and the fixed-box diagram) with:
```markdown
## 5. Avalonia UX and Layout

Uses `Dock.Avalonia` (`Dock.Model.Mvvm` + `Dock.Avalonia.Themes.Fluent`) for a real dockable/floatable tool-window shell — VS-familiar drag-to-dock, float, and re-tab behavior, superseding this section's original fixed-`Grid`-only MVP decision (see the UI-Foundation spec and DEVLOG for the full rationale). `DockFactory` (in `Ui`) builds the default layout in code; `MainWindow.axaml` hosts a single `DockControl` bound to it.

```text
┌─────────────────────────────────────────────────────────────────┐
│ File · Edit · View · Connections · Run  | New Save Run Cancel    │
├────────────────┬────────────────────────────────────────────────┤
│ Workspace       │ Script tabs (dockable document dock)           │
│ (dockable tool  ├────────────────────────────────────────────────┤
│  window)        │ Monaco F# editor (shared across tabs)          │
│                 ├────────────────────────────────────────────────┤
│                 │ Results | Output | Problems | Schema (1 tool)  │
└─────────────────┴────────────────────────────────────────────────┘
```

Dock layout (panel positions/sizes/floating state) and the dark/light theme choice persist across restarts to `%LOCALAPPDATA%/FSharpQueryStudio/ui-state.json`.
```

- [ ] **Step 2: Update DEVLOG.md and commit**

```bash
git add docs/fs-studio-plan.md docs/DEVLOG.md
git commit -m "UI-Foundation.13: update Section 5 for the Dock.Avalonia shell"
```

---

### Task 14: UI-Foundation acceptance pass

**Files:** None new — verification only.

- [ ] **Step 1: Full solution build and test**

Run: `dotnet build` then `dotnet test`.
Expected: both succeed, zero failures, across every project touched by Tasks 1-13.

- [ ] **Step 2: Manual verification checklist**

Launch the real published/run app (`dotnet run --project src/FSharpQueryStudio.App` or the built `.exe`, matching this codebase's existing manual-verify convention from A.13/A.19) and confirm each of the following, checking off as verified:

- [ ] Window opens in the dark VS2026-style palette by default.
- [ ] `View > Toggle Theme` switches to the light palette and back.
- [ ] Workspace, Documents, and Results tool windows are all visible in the default layout.
- [ ] Dragging the Results tool window to float it, then re-docking it, both work without a crash.
- [ ] `File > New` opens a second script tab; typing in it doesn't affect the first tab's content.
- [ ] Clicking between the two tabs shows each tab's own text in the editor (the `DocumentLoaded`/shared-WebView mechanism from Task 7 — this is the one behavior most likely to need a real fix if Monaco's `setValue` timing doesn't match what Task 7's code assumed).
- [ ] `File > Open` on a real `.fsx` file loads its actual contents into a new tab.
- [ ] `File > Save` on an `untitled:` tab prompts for a location; on a file-backed tab, saves directly without prompting.
- [ ] Run (toolbar button, `Run` menu item, and F5 if already bound) all execute the active tab's script and show a result.
- [ ] The status bar's worker-state and session-mode text update when running/cancelling and when switching tabs.
- [ ] Closing a dirty tab prompts before discarding (if `ConfirmDiscardDirtyDocument` is wired anywhere yet — if not, note that as a pre-existing gap, not a regression from this plan).
- [ ] Closing and relaunching the app restores the previous session's dock layout and theme.

- [ ] **Step 3: Write the milestone-style DEVLOG entry**

Add a final `docs/DEVLOG.md` entry summarizing UI-Foundation as a whole: what shipped, what manual verification found (including anything from Step 2's checklist that needed a real fix, same as this codebase's existing acceptance-test entries for Milestones A-E), and explicitly note UI-Workspace/UI-Connections/UI-Trust-Packages as the three still-pending sub-projects from the original spec's decomposition.

- [ ] **Step 4: Commit**

```bash
git add docs/DEVLOG.md
git commit -m "UI-Foundation.14: acceptance pass"
```

---

## Self-review notes

- **Spec coverage:** Dock shell (Tasks 5-6, 9), theme + toggle (Tasks 1, 8, 9), persistence (Tasks 2, 12), chrome/commands/status bar (Tasks 9-11), icons (Task 9), testing split (every task), Section 5 doc update (Task 13) — all five of the spec's numbered sections have a task. The spec's Non-goals (`Edit` menu, Connections UI, workspace explorer content) are explicitly left stubbed in Task 9, not silently dropped.
- **Real gap found during planning, folded in:** `DocumentViewModel`/`ShellViewModel` never exposed `INotifyPropertyChanged` despite `WorkerStateViewModel`/`ResultViewModel` already doing so — without Task 4, tab titles and the status bar simply wouldn't refresh on tab switch, silently breaking the feature the rest of the plan builds. Folded in as its own small TDD task rather than a footnote.
- **Known placeholders, deliberately left as such and flagged inline (not silent):** the exact `Dock.Serializer.SystemTextJson` class name (Task 12) and the exact `IFactory` tool-visibility API (Task 11) depend on package versions this plan can't pin without running `dotnet add package` for real first — each is called out at its exact use site with a concrete instruction for resolving it (check the installed package), not a vague "handle this later."
