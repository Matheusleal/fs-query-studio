# Welcome Page — Design

Second of four planned sub-projects toward better app observability and polish for real usage (Log Viewer → Welcome Page → Results Export → UI Polish). Log Viewer shipped; this one adds a startup guide/welcome tab. No dependency on Log Viewer's own code, only on the `_Help` menu it created (this sub-project's menu item is the second item under it, as that entry's DEVLOG already anticipated).

## Background

The user asked for "a welcome page with the user guide to this app what should open every time the app starts and also should be available to open by the top menu." First-run/first-session context for this app currently doesn't exist at all — a fresh workspace opens straight to a blank script with no orientation toward what the app can actually do (register a connection, call `Connections.sql`, switch session modes, read the per-tab Results/Output/Problems/Schema panel, open the Log Viewer).

Clarified during brainstorming: this is a **tab in the existing tab strip**, not a separate window (rejecting the Log Viewer's own window pattern for this — see `feedback_ui_content_placement` memory: app *content* the user reads as part of normal workflow belongs in the tab strip; separate windows are for diagnostic tools). It's closable but active at startup, reopenable from the Help menu. Its content renders as plain F#-comment text through the same Monaco editor every script tab already uses — not a rich native view — trading visual polish for near-zero new UI-plumbing code, since this app has no rendering path today that swaps the shared editor pane for something else per-tab.

## Goals

- A `Welcome.fsx`-style tab, seeded with real guide content as `//` comments, open and active every time the app starts (alongside the usual blank `Script1.fsx`, which stays open behind it).
- A `_Welcome…` item under the existing `_Help` menu (added after `_Logs…`) that reopens it — recreating it with fresh, unedited content if the user closed or edited it away.
- Closable exactly like any other tab — no special pinning, no "are you sure" prompt (it starts non-dirty, matching a fresh script's own close behavior).
- Guide content accurate to what the app can actually do today — no describing a feature that doesn't exist, no describing one that exists differently than written.

## Non-goals (deferred or out of scope)

- A rich, formatted native view (headings, styled sections, clickable quick-actions) — rejected during brainstorming in favor of the much simpler Monaco-comment rendering. Could be revisited as a future "Welcome v2" if the plain-text version feels too thin once it's actually used.
- A "don't show this again" checkbox/setting — not requested; the user explicitly wants it every time, and adding a persisted suppress-flag is unrequested scope.
- Runnable example code (real, uncommented calls to `Connections.sql`/etc. the user could press F5 on directly) — a first-time user pressing F5 on the Welcome tab would hit a real error if no connection is registered yet, which is a bad first impression for the file that's supposed to be a soft landing. All example code stays inside comments; running the Welcome tab as-is is a harmless no-op (an all-comment `.fsx` file evaluates to nothing).
- Any change to `DocumentViewModel`, the tab strip's rendering, or the Monaco bridge — this reuses 100% existing machinery (`createDocument`, `OpenDocument`, the existing seed-text mechanism `newScriptSeed` already uses for blank scripts). The only production change is making the seed content depend on which URI is being opened.

## Architecture

### Seeding mechanism (`Ui`)

`ShellViewModel.createDocument` currently always seeds new documents with the same `newScriptSeed` constant. It gains a second known constant, `welcomeUri = "untitled:Welcome.fsx"` (matching this codebase's existing `"untitled:"` scheme, so it needs no special-casing anywhere else that already handles that prefix — `NextUntitledUri`'s own `ScriptN` probing pattern doesn't match `Welcome.fsx`, so the two coexist without collision) and `welcomeGuideSeed: string` (the real content, see below). `createDocument (uri: string)` picks the seed based on whether `uri = welcomeUri`:

```fsharp
let seedFor (uri: string) =
    if uri = welcomeUri then welcomeGuideSeed else newScriptSeed

let createDocument (uri: string) =
    let doc = DocumentViewModel(uri, ?scheduler = scheduler, initialText = seedFor uri)
    wireDocument doc
    documents.Add(doc)
    doc
```

`ShellViewModel.WelcomeUri` is exposed as a `member` (read-only) so the composition root (`App.axaml.fs`) and `MainWindow`'s Help-menu click handler both reach the exact same constant — no risk of the two drifting to different literal strings.

### Startup wiring (`App.axaml.fs`)

After `ShellViewModel`'s own constructor already created and activated the initial `Script1.fsx` (unchanged), the composition root calls `mainWindow.ShellViewModel.OpenDocument(mainWindow.ShellViewModel.WelcomeUri) |> ignore` once, at the same point in startup sequencing `SeedConnections` already runs. `OpenDocument`'s existing open-or-activate logic (unchanged) does the rest: creates the Welcome document (seeded via the mechanism above, since it doesn't already exist), adds it to `Documents`, and sets it as `ActiveDocument` — which is exactly "Welcome active, Script1 open behind it," the requested startup state, with zero new logic in `OpenDocument` itself.

### Reopening (`MainWindow`)

`MainWindow.axaml` gains a second item under the `_Help` menu, right after `_Logs…`:

```xml
<MenuItem Header="_Welcome…" Name="WelcomeMenuItem" />
```

`MainWindow.axaml.fs` wires it identically in spirit to `LogsMenuItem`, but calling back into the existing `ShellViewModel`, not constructing a new window:

```fsharp
this.FindControl<MenuItem>("WelcomeMenuItem").Click.Add(fun _ ->
    shellViewModel.OpenDocument(shellViewModel.WelcomeUri) |> ignore)
```

If the tab is still open, this just activates it (switches to it, no content change — matches `OpenDocument`'s existing dedup behavior for any URI). If the user closed it (or it was never reopened after a fresh install), this recreates it with the seed content — fresh, not whatever the user last edited it to, since `CloseDocument` drops the `DocumentViewModel` entirely and a new one is constructed from `seedFor` on the next open.

### Guide content

Written as `//`-commented F# (all example code also commented out, so the file is inert if run), covering exactly what exists today — verified against the real module signatures before writing, not assumed:
- Running a script: F5 / the Run button; Cancel.
- Session state: every run is Isolated by default (nothing persists between runs) — verified against the actual UI before writing this, since there's a `Persistent` mode in the underlying model (`DocumentViewModel.SessionMode`) but **no UI control anywhere exposes switching to it** today, only a read-only status display (`SessionStateDisplay` in the status bar) and a Reset Session command. The guide describes what's actually reachable, not the underlying model's full capability.
- Connecting to data: the `_Connections` menu's `New Connection…` dialog; the Workspace panel's per-connection expand-to-browse-schema (Tables → Columns); the six supported kinds (SQLite, SQL Server, PostgreSQL, CSV, Excel, REST/JSON).
- The script API surface actually exported by `FSharpQueryStudio.Connectors.Connections` and `FSharpQueryStudio.Results`: `Connections.sql name sqlText parameters`, `Connections.csv name`, `Connections.excel name maxRowsOption`, `Connections.rest name options ct`, `Results.table query`, `Results.text value` — real signatures, not paraphrased ones.
- The Results panel: Results/Output/Problems/Schema tabs, each scoped per open document tab (switching tabs never loses another tab's last run — a real, deliberately-built behavior worth mentioning since it's non-obvious).
- The Log Viewer: reachable from `_Help > Logs…`, for watching what the app itself is doing.

## Error handling

None needed beyond what already exists — this reuses `OpenDocument`/`CloseDocument`/`createDocument` verbatim, all of which already handle their own edge cases (duplicate opens, closing the last tab, etc.) from prior work. No new failure mode is introduced.

## Testing

`ShellViewModelTests`:
- A fresh `ShellViewModel` (simulating the composition root's own startup call to `OpenDocument(WelcomeUri)`) ends up with `ActiveDocument.Uri = WelcomeUri` and its text containing real guide content (assert on a distinctive substring, not the whole text verbatim) — proving the seed-selection branch actually fires for the welcome URI.
- A document opened via `OpenDocument` with an ordinary `untitled:ScriptN.fsx`-style URI still gets the existing blank-script seed, unaffected by the new branch — a regression guard for every existing script-tab test.
- `WelcomeUri` is stable/public — `ShellViewModel().WelcomeUri` returns the same value across two separate instances (guards against ever making it instance-specific by accident, since `MainWindow`'s click handler and the composition root both depend on it being the one true constant).

No XAML-level test — same standing limitation as every other UI-only change in this codebase; verified by a clean `dotnet build` (`WelcomeMenuItem`'s `Click` handler and `Name` resolve against real members at build time) plus the `ShellViewModelTests` above covering all the real logic the menu item delegates to.
