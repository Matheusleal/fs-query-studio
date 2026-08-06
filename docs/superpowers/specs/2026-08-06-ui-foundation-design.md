# UI-Foundation — Design

First of four planned sub-projects toward a full UI implementation pass (UI-Foundation → UI-Workspace → UI-Connections → UI-Trust-Packages). This one replaces the fixed-`Grid` shell built in Milestone A with a dockable, VS2026-styled shell. It touches only the *view* layer — every existing `ShellViewModel`/`DocumentViewModel`/`ResultViewModel`/`ProblemViewModel`/`WorkerStateViewModel` stays as-is; this spec wires them into real, restyled XAML instead of the current stub.

## Background

All five original milestones (A–E) are complete and functionally sound, but `MainWindow.axaml` never caught up to what the view models already support:

- No binding to `ShellViewModel.Documents` — only the single initial tab is ever shown; multi-document tab support already exists in the view model (`OpenDocument`/`CloseDocument`) but nothing in the UI calls it.
- `Problems` and `Schema` tabs are empty placeholders; `ShellViewModel.Problems` (backed by `ProblemViewModel`, including its navigate-to-diagnostic command) is never bound.
- Menu items (`File`/`Edit`/`View`/`Connections`/`Run`) have no click handlers except the ad-hoc `RunButton`/`CancelButton`/`ResetSessionButton` added directly to the toolbar in A.19/E.2.
- Layout is `Section 5`'s fixed `Grid` + `GridSplitter`s, chosen deliberately at the time ("avoid a complex docking framework until users need it," per [`fs-studio-plan.md` Section 5](../../fs-studio-plan.md#5-avalonia-ux-and-layout)). The user now wants a VS2026-familiar, dockable/floatable tool-window experience — this spec revisits that decision and supersedes it.
- Theming is bare `FluentTheme` with no custom palette.

## Goals

- Replace the fixed `Grid` shell with a `Dock.Avalonia`-based dockable layout: draggable, floatable, re-arrangeable tool windows, matching VS's tool-window model.
- VS2026-like dark/light theme with a `View` menu toggle, persisted across restarts.
- Real menu/toolbar commands for the functionality that already exists on `ShellViewModel` (Run/Cancel/Reset Session/New/Open/Save) plus a status bar surfacing worker state and session mode.
- Persist the user's dock layout and theme choice across app restarts.
- Material.Icons.Avalonia for toolbar/menu iconography.

## Non-goals (deferred to later sub-projects)

- Workspace explorer content (Scripts/History tree) — UI-Workspace.
- Connection management UI, `ConnectionViewModel`, schema browser content — UI-Connections.
- Package approval / capability-approval dialog polish — UI-Trust-Packages.
- `Edit` menu commands (Monaco already handles standard editing internally via its own keybindings) — no host-side Edit command exists to wire, so the menu stays present but stubbed.
- Multi-monitor floating-window persistence edge cases — best-effort only, not exhaustively tested.

## Architecture

### Docking

Add `Dock.Avalonia` and `Dock.Model.Mvvm` to `FSharpQueryStudio.Ui` (view-model side) and `FSharpQueryStudio.App` (composition root, same split the codebase already uses for `WorkerClient` wiring). A new `DockFactory : Dock.Model.Mvvm.Factory` builds the default layout:

```
┌─────────────────────────────────────────────────────────────────┐
│ File  Edit  View  Connections  Run  |  [icons] New Open Save     │
│                                        Run  Cancel  Reset         │
├────────────────┬────────────────────────────────────────────────┤
│ Workspace       │ Script tabs (IDocument per open DocumentVM)    │
│ (placeholder    ├────────────────────────────────────────────────┤
│  tool window)   │ Monaco editor                                 │
│                 ├────────────────────────────────────────────────┤
│                 │ Results | Output | Problems | Schema (1 tool)  │
├─────────────────┴────────────────────────────────────────────────┤
│ Worker: Ready   Session: Isolated                    [status bar]│
└─────────────────────────────────────────────────────────────────┘
```

Three dockable units:

1. **Document dock** (center) — one `IDocument` wrapping each `ShellViewModel.Documents` entry; opening/closing a Dock.Avalonia document tab calls `ShellViewModel.OpenDocument`/`CloseDocument` so the existing dirty-tab confirmation flow (`ConfirmDiscardDirtyDocument`) keeps working unchanged. The active `IDocument`'s underlying `DocumentViewModel` becomes `ShellViewModel.ActiveDocument`'s source — closing the gap where only the first tab was ever reachable.
2. **`WorkspaceTool`** (left, docked) — placeholder content only (a static "Workspace" label, same as today), proving the panel docks/floats/resizes correctly. Real content is UI-Workspace's job.
3. **`ResultsTool`** (bottom, docked) — the existing `TabControl` (Results/Output/Problems/Schema) moves inside this tool window verbatim; internal tab binding logic doesn't change, only its container. `Problems` tab gets bound to `ShellViewModel.Problems` for the first time (previously an empty placeholder) since the data's been available since Milestone B/D and there's no reason to leave it disconnected while touching this file anyway.

`ShellViewModel` gains no new members for docking itself — `DockFactory` observes `ShellViewModel.Documents`/`ActiveDocument` and calls its existing `OpenDocument`/`CloseDocument`/property setters. No new coupling from `Ui` to `Dock.Avalonia` types leaks into `ShellViewModel`.

### Theme

Two `ResourceDictionary` files, `Themes/VsDark.axaml` and `Themes/VsLight.axaml`, merged into `App.axaml` under `Application.Styles`, keyed to `ThemeVariant.Dark`/`ThemeVariant.Light`. Palette (VS2026 defaults, not invented):

| Token | Dark | Light |
|---|---|---|
| Window background | `#1E1E1E` | `#F3F3F3` |
| Panel/tool-window background | `#252526` | `#FFFFFF` |
| Toolbar/menu background | `#2D2D30` | `#F0F0F0` |
| Accent (Run, active tab) | `#007ACC` | `#007ACC` |
| Primary text | `#CCCCCC` | `#1E1E1E` |
| Border/splitter | `#3F3F46` | `#D6D6D6` |

A new `ThemeService` in `Ui` (plain class, no Avalonia dependency — testable the same way `WorkerStateViewModel` is): holds `Current: ThemeVariant` (`Dark`/`Light`), exposes `Toggle()`, raises `PropertyChanged`. The composition root (`App.axaml.fs`) subscribes and sets `Application.Current.RequestedThemeVariant` on change — the one place `Ui` would otherwise need an Avalonia dependency, kept out of it entirely.

### Chrome

- **File**: New (blank `.fsx` tab via `ShellViewModel.OpenDocument` with a fresh `untitled:` URI), Open (native file dialog → `OpenDocument`), Save / Save As (write `ActiveDocument.Text` via `Storage`), Exit.
- **Run**: Run (F5), Cancel, Reset Session — already exist as `ShellViewModel.RunAsync`/`Cancel`/`ResetSessionAsync`; this pass just adds real `MenuItem`/toolbar bindings alongside the existing ad-hoc buttons (which stay, now icon+text styled).
- **View**: theme toggle (calls `ThemeService.Toggle`), show/hide each tool window (calls into `DockFactory`).
- **Edit**, **Connections**: present, disabled/empty — explicitly out of scope per Non-goals.
- **Toolbar**: existing `RunButton`/`CancelButton`/`CopyResultButton`/`ResetSessionButton` restyled with Material.Icons.Avalonia glyphs (`Play`, `Stop`, `ContentCopy`, `Restart`) plus new `New`/`Save` icon buttons.
- **Status bar** (new `DockPanel.Dock="Bottom"` row below the `DockControl`): `WorkerState.Current` (already exists), `ActiveDocument.SessionStateDisplay` (already exists) — both currently shown inline in the toolbar per the pre-existing `MainWindow.axaml`; this pass relocates them to a proper status bar, which is where users expect that information in a VS-like shell.

### Persistence

One file, `%LOCALAPPDATA%/FSharpQueryStudio/ui-state.json`, holding:

```json
{ "theme": "Dark", "dockLayout": { /* Dock.Avalonia's own serialized layout */ } }
```

This is app-level state (a window arrangement), not workspace-level — it does not belong in `workspace.json` alongside connections/scripts. A new `UiState` module in `Storage` (matching where `Workspace.fs`/`Trust.fs` already live) handles read/write; missing or corrupt file falls back to `ThemeService`'s default (`Dark`) and `DockFactory`'s in-code default layout — never a startup failure.

## Testing

Matching this codebase's established split for visual work (see DEVLOG entries for A.10/A.13/A.16 — Avalonia rendering isn't headlessly automatable, view-model logic is):

- `ThemeService` (`Ui.Tests`): `Toggle()` flips `Dark`↔`Light`, raises `PropertyChanged` exactly once per toggle.
- `UiState` read/write (`Storage.Tests`): round-trips theme + an opaque layout blob through a temp directory; a missing or malformed file returns the documented defaults rather than throwing.
- `DockFactory`'s document-open/close calling into `ShellViewModel.OpenDocument`/`CloseDocument` (`Ui.Tests`, no real `Dock.Avalonia` control needed — `Factory` methods are plain calls): opening a second document tab produces two `ShellViewModel.Documents` entries; closing a dirty one respects `ConfirmDiscardDirtyDocument` exactly as it does today.
- Actual docking/drag/float behavior, theme rendering, and icon appearance: manual verification, same rationale and same caveat as every prior XAML-rendering task in this project (no display available in this environment; confirmed by running the compiled app).

## Documentation impact

[`fs-studio-plan.md` Section 5](../../fs-studio-plan.md#5-avalonia-ux-and-layout) currently states the fixed-`Grid`, no-docking-framework decision as the architecture of record. The implementation plan for this spec must update that section to describe the `Dock.Avalonia` shell instead, so the architecture doc doesn't drift out of sync with what's actually built — matching this project's existing practice of keeping `fs-studio-plan.md` authoritative (e.g. B.1's `Logging` project addition updated Section 4 the same way).
