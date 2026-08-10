namespace FSharpQueryStudio.Protocol

open System.Text.Json

/// App-level UI state — UI-Foundation's persisted theme choice, under
/// `%LOCALAPPDATA%/FSharpQueryStudio/`. Not workspace data (a window
/// arrangement isn't project-scoped), so it lives outside
/// `workspace.json`/`trust.json`. `DockLayout` is an opaque blob from an
/// earlier, since-removed dockable shell — the current fixed layout
/// never writes it, only carries an old value through unchanged so a
/// pre-existing file isn't silently truncated.
type UiState =
    { Theme: string
      DockLayout: JsonElement option }
