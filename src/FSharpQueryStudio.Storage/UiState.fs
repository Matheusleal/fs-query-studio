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
    ///
    /// The explicit null check is not redundant with the `try`/`with`: a
    /// file whose entire content is the literal JSON `null` parses
    /// *successfully* and yields a null `UiState` record, which
    /// `System.Text.Json` is perfectly happy to return. The composition
    /// root reads `uiState.Theme` before any window exists, so returning
    /// that null would `NullReferenceException` the process during
    /// startup — exactly the "must never block startup" guarantee this
    /// module documents.
    let read (appDataDir: string) : FSharpQueryStudio.Protocol.UiState =
        let path = statePath appDataDir

        if File.Exists path then
            try
                let json = File.ReadAllText path

                let state =
                    JsonSerializer.Deserialize<FSharpQueryStudio.Protocol.UiState>(json, Serialization.options)

                // `isNull (box state)` rather than a `null` pattern: F#
                // records don't admit the null literal in source even
                // though the CLR (and System.Text.Json) can hand one back.
                if isNull (box state) then defaultState else state
            with _ ->
                defaultState
        else
            defaultState

    let write (appDataDir: string) (state: FSharpQueryStudio.Protocol.UiState) : unit =
        Directory.CreateDirectory appDataDir |> ignore
        let json = JsonSerializer.Serialize(state, Serialization.options)
        File.WriteAllText(statePath appDataDir, json)
