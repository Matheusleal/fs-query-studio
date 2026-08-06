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
