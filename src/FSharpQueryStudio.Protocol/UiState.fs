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
