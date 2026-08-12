# Welcome Page Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give a fresh workspace real orientation instead of opening straight to a blank script. A `Welcome.fsx`-style tab, seeded with a real guide to the app's actual features (written as inert `//` comments), opens active at every startup alongside the usual blank `Script1.fsx`, and is reopenable any time from a new `_Welcome…` item under the `_Help` menu.

**Architecture:** Reuses the existing tab-strip/`DocumentViewModel` machinery entirely — no new window, no new content-rendering path. `ShellViewModel.createDocument` gains a URI-keyed seed-content branch: opening the well-known `WelcomeUri` (`"untitled:Welcome.fsx"`) seeds real guide text instead of the existing blank-script seed (`newScriptSeed`). The composition root (`App.axaml.fs`) calls `OpenDocument(WelcomeUri)` once at startup, right after `SeedConnections` — `OpenDocument`'s existing open-or-activate logic does the rest (creates it, adds it to `Documents`, makes it `ActiveDocument`), so the "Welcome active, Script1 open behind it" startup state falls out of code that already exists, unmodified. `MainWindow`'s new `_Welcome…` menu item calls the same `OpenDocument(WelcomeUri)` to reopen it. Full design rationale: `docs/superpowers/specs/2026-08-12-welcome-page-design.md`.

**Tech Stack:** F#, Avalonia 12.1.1, xUnit, existing `FSharpQueryStudio.{Ui,App}` projects. No new project, no new package.

## Global Constraints

- TDD throughout for `ShellViewModel.fs`'s change: a failing test before any production code. The `MainWindow`/`App.axaml.fs` wiring task has no TDD cycle (XAML/composition-root wiring isn't unit-testable in this codebase — no headless rendering harness available), verified instead by a clean `dotnet build`.
- `WelcomeUri` is `"untitled:Welcome.fsx"` — reuses the existing `"untitled:"` scheme deliberately, so nothing else that already special-cases that prefix needs to change. It must NOT collide with `NextUntitledUri`'s own `untitled:ScriptN.fsx` probing pattern — `Welcome.fsx` doesn't match `Script\d+\.fsx`, so no collision; do not rename it to something that does.
- The guide content is inert: every example is inside a `//` comment. Pressing F5 on the Welcome tab as shipped must be a harmless no-op (an all-comment `.fsx` file evaluates to nothing) — never write a real, uncommented call to `Connections.*`/anything else into the seed text.
- The guide content must describe only features that actually exist in the running UI today — verified during design, not assumed. In particular: session mode is **Isolated only** from the UI's perspective (no control anywhere switches a document to `Persistent`, only a read-only `SessionStateDisplay` and a Reset Session command exist) — do not describe a session-mode switch that isn't reachable.
- `dotnet build` clean (0 warnings/errors) and `dotnet test` green (excluding the pre-existing `RequiresDocker` `Integration.Tests` failures, unrelated to this work) after every task.
- Update `docs/DEVLOG.md` with an entry once the feature is end-to-end working (Task 2), per this repo's established convention.

---

### Task 1: `ShellViewModel` — `WelcomeUri`, guide content, URI-keyed seed selection

**Files:**
- Modify: `src/FSharpQueryStudio.Ui/ShellViewModel.fs`
- Test: Modify `tests/FSharpQueryStudio.Ui.Tests/ShellViewModelTests.fs`

**Interfaces:**
- Consumes: `ShellViewModel.OpenDocument`/`createDocument` (existing, unmodified logic — only the seed-text source changes).
- Produces: `ShellViewModel.WelcomeUri: string` (a `member`, read-only). Task 2 (`App.axaml.fs`, `MainWindow.axaml.fs`) consumes this member directly — both call sites must reference `shellViewModel.WelcomeUri`, never re-type the literal `"untitled:Welcome.fsx"` string themselves, so the two can never drift apart.

- [ ] **Step 1: Write the failing tests**

Append to `tests/FSharpQueryStudio.Ui.Tests/ShellViewModelTests.fs` (anywhere after the existing `testConnection` helper and other `ShellViewModel()`-constructing tests — end of file is fine):

```fsharp
[<Fact>]
let ``OpenDocument with WelcomeUri seeds real guide content and activates it`` () =
    let vm = ShellViewModel()

    let welcomeDoc = vm.OpenDocument(vm.WelcomeUri)

    Assert.Equal(vm.WelcomeUri, vm.ActiveDocument.Uri)
    Assert.Same(welcomeDoc, vm.ActiveDocument)
    Assert.Contains("RUNNING SCRIPTS", welcomeDoc.Text)

[<Fact>]
let ``OpenDocument with an ordinary script URI still seeds the blank script content`` () =
    let vm = ShellViewModel()

    let doc = vm.OpenDocument("untitled:Script2.fsx")

    Assert.Contains("Hello from F# Query Studio!", doc.Text)
    Assert.DoesNotContain("RUNNING SCRIPTS", doc.Text)

[<Fact>]
let ``WelcomeUri is the same stable constant across separate ShellViewModel instances`` () =
    let first = ShellViewModel()
    let second = ShellViewModel()

    Assert.Equal(first.WelcomeUri, second.WelcomeUri)
    Assert.Equal("untitled:Welcome.fsx", first.WelcomeUri)
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FSharpQueryStudio.Ui.Tests --filter "FullyQualifiedName~WelcomeUri|FullyQualifiedName~ordinary script URI"`
Expected: compile error — `ShellViewModel` has no `WelcomeUri` member yet.

- [ ] **Step 3: Add `WelcomeUri`, the guide content, and the seed-selection branch**

In `src/FSharpQueryStudio.Ui/ShellViewModel.fs`, find the existing:

```fsharp
    let newScriptSeed =
        "// New F# script — write your code below\nprintfn \"Hello from F# Query Studio!\"\n"

    let createDocument (uri: string) =
        let doc = DocumentViewModel(uri, ?scheduler = scheduler, initialText = newScriptSeed)
        wireDocument doc
        documents.Add(doc)
        doc
```

Replace it with:

```fsharp
    let newScriptSeed =
        "// New F# script — write your code below\nprintfn \"Hello from F# Query Studio!\"\n"

    /// The one, stable URI the Welcome tab is always opened/reopened
    /// under — reachable as `ShellViewModel.WelcomeUri` so the
    /// composition root's startup call and `MainWindow`'s Help-menu
    /// handler both reference the exact same constant, never a
    /// re-typed literal that could drift. Reuses the existing
    /// `"untitled:"` scheme deliberately; `Welcome.fsx` doesn't match
    /// `NextUntitledUri`'s own `ScriptN.fsx` probing pattern, so the two
    /// coexist without collision.
    let welcomeUri = "untitled:Welcome.fsx"

    /// A quick guide to the app's actual features, seeded into the
    /// Welcome tab instead of `newScriptSeed` — see
    /// `docs/superpowers/specs/2026-08-12-welcome-page-design.md`. Every
    /// example stays inside a `//` comment deliberately: this file is
    /// meant to be run (F5) harmlessly as a no-op, not to hit a real
    /// "connection not found" error on a user's very first run.
    let welcomeGuideSeed =
        """// Welcome to F# Query Studio!
//
// This tab is a quick guide to what the app can do. It's just comments —
// press F5 if you like, it won't do anything (there's no live code here).
// Close this tab any time; reopen it later from Help > Welcome...
//
// RUNNING SCRIPTS
//   Write F# in a script tab, then press F5 (or Run in the toolbar/menu)
//   to execute it. Cancel stops a running script. Every run starts fresh
//   (Isolated mode) - nothing from a previous run carries over. The
//   status bar at the bottom shows the current session state.
//
// CONNECTING TO DATA
//   Connections menu > New Connection... registers a database, CSV file,
//   Excel workbook, or REST/JSON endpoint. Once registered, expand it in
//   the Workspace panel on the left to browse its tables and columns.
//
//   Supported kinds: SQLite, SQL Server, PostgreSQL, CSV, Excel, REST/JSON.
//
// QUERYING FROM A SCRIPT
//   Once a connection is registered, call it by name - no #r or open
//   needed, these are already available in every script:
//
//     let orders = Connections.sql "Sales" "select * from Orders where Total > @min" [ "min", box 100 ]
//     let rows    = Connections.csv "MyCsvConnection"
//     let sheet   = Connections.excel "MyWorkbook" None
//     // let data = Connections.rest "MyApi" options ct
//
//   Parameters are always bound, never string-concatenated into the SQL -
//   the `[ "min", box 100 ]` list above is how you pass them safely.
//
// CONTROLLING WHAT SHOWS IN RESULTS
//   By default the script's last value shows in the Results pane. To
//   show something specific instead:
//
//     Results.table orders   // shows orders' own real columns as a table
//     Results.text  "done"   // shows a plain text/string value
//
// READING RESULTS
//   Results, Output, Problems, and Schema each have their own tab at the
//   bottom - and each open script tab has its own: switching tabs never
//   loses another tab's last run, output log, or diagnostics.
//
// WATCHING WHAT THE APP IS DOING
//   Help > Logs... opens a live view of the app's own log files - useful
//   when something doesn't behave the way you expect.
"""

    let seedFor (uri: string) = if uri = welcomeUri then welcomeGuideSeed else newScriptSeed

    let createDocument (uri: string) =
        let doc = DocumentViewModel(uri, ?scheduler = scheduler, initialText = seedFor uri)
        wireDocument doc
        documents.Add(doc)
        doc
```

Then add the public member — place it near `NextUntitledUri` (another URI-related member), anywhere in the `member` section is fine:

```fsharp
    /// The stable URI the Welcome tab always opens/reopens under — see
    /// `welcomeUri`'s own doc comment above for why both the composition
    /// root's startup call and `MainWindow`'s Help-menu handler must go
    /// through this member rather than a re-typed literal.
    member _.WelcomeUri = welcomeUri
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FSharpQueryStudio.Ui.Tests --filter "FullyQualifiedName~WelcomeUri|FullyQualifiedName~ordinary script URI"`
Expected: PASS (3/3).

- [ ] **Step 5: Run the full `Ui.Tests` suite**

Run: `dotnet test tests/FSharpQueryStudio.Ui.Tests`
Expected: every existing case still green (157 prior + 3 new = 160) — `seedFor` only changes behavior for the one new literal URI, every other call site is unaffected.

- [ ] **Step 6: Commit**

```bash
git add src/FSharpQueryStudio.Ui/ShellViewModel.fs tests/FSharpQueryStudio.Ui.Tests/ShellViewModelTests.fs
git commit -m "Feature: WelcomeUri and real guide content seeded into a Welcome tab"
```

---

### Task 2: Open at startup, reopen from a new Help menu item, DEVLOG entry

**Files:**
- Modify: `src/FSharpQueryStudio.App/App.axaml.fs`
- Modify: `src/FSharpQueryStudio.Ui/MainWindow.axaml`
- Modify: `src/FSharpQueryStudio.Ui/MainWindow.axaml.fs`
- Modify: `docs/DEVLOG.md`

**Interfaces:**
- Consumes: `ShellViewModel.WelcomeUri`/`OpenDocument` (Task 1).
- Produces: nothing further downstream — this is the plan's last task.

No TDD cycle — XAML/composition-root wiring isn't unit-testable in this codebase, verified by a clean `dotnet build`.

- [ ] **Step 1: Open the Welcome tab at startup in `App.axaml.fs`**

Find the existing:

```fsharp
            mainWindow.ShellViewModel.SeedConnections(FSharpQueryStudio.Storage.Workspace.readConnections workspaceDir)
```

Add immediately after it:

```fsharp

            // Every fresh session opens with the Welcome tab active (and
            // the usual blank Script1.fsx open behind it, from
            // ShellViewModel's own constructor) — see
            // docs/superpowers/specs/2026-08-12-welcome-page-design.md.
            // `OpenDocument`'s existing open-or-activate logic does all
            // the real work here; this is just the one call that
            // triggers it at the right point in startup.
            mainWindow.ShellViewModel.OpenDocument(mainWindow.ShellViewModel.WelcomeUri) |> ignore
```

- [ ] **Step 2: Add the `_Welcome…` Help menu item to `MainWindow.axaml`**

Find the `_Help` menu (added by the Log Viewer plan):

```xml
      <MenuItem Header="_Help">
        <MenuItem Header="_Logs…" Name="LogsMenuItem" />
      </MenuItem>
```

Add a second item:

```xml
      <MenuItem Header="_Help">
        <MenuItem Header="_Logs…" Name="LogsMenuItem" />
        <MenuItem Header="_Welcome…" Name="WelcomeMenuItem" />
      </MenuItem>
```

- [ ] **Step 3: Wire the click handler in `MainWindow.axaml.fs`**

Find the existing `LogsMenuItem` wiring:

```fsharp
        this.FindControl<MenuItem>("LogsMenuItem").Click.Add(fun _ -> LogViewerWindow().Show())
```

Add right after it:

```fsharp

        this.FindControl<MenuItem>("WelcomeMenuItem").Click.Add(fun _ ->
            shellViewModel.OpenDocument(shellViewModel.WelcomeUri) |> ignore)
```

- [ ] **Step 4: Build and run the full test suite**

Run: `dotnet build` then `dotnet test tests/FSharpQueryStudio.Ui.Tests`
Expected: build clean; `Ui.Tests` unchanged from Task 1's 160 (this task adds no new tests — XAML/composition-root wiring only).

- [ ] **Step 5: Write the DEVLOG entry**

Append an entry to `docs/DEVLOG.md` covering: the user request (a welcome/user-guide page, open at every startup and reachable from the top menu), the brainstorming decision to make it a tab rather than a window (tie it to the `feedback_ui_content_placement` distinction — app content vs diagnostic tooling — if that framing reads naturally, otherwise just state the decision and why), the URI-keyed seed-selection mechanism and why it needed zero changes to `OpenDocument`/`createDocument`'s own logic, the real gap caught during design (no UI control exists to switch a document to Persistent session mode — the guide was written to match what's actually reachable, not the underlying model's full capability), what was verified (build clean, exact test counts), and files changed. Match this file's established long-form voice — read the last entry or two before writing, don't write a short bullet list.

- [ ] **Step 6: Commit**

```bash
git add src/FSharpQueryStudio.App/App.axaml.fs src/FSharpQueryStudio.Ui/MainWindow.axaml src/FSharpQueryStudio.Ui/MainWindow.axaml.fs docs/DEVLOG.md
git commit -m "Feature: open the Welcome tab at startup, reopen it from a new Help menu item"
```
