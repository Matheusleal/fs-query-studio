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

[<Fact>]
let ``read returns defaultState when ui-state.json holds the literal null`` () =
    let dir = tempAppDataDir ()

    // Not covered by the malformed-JSON case above: `null` is *valid* JSON,
    // so Deserialize succeeds and hands back a null record rather than
    // throwing. The composition root reads `.Theme` off this before any
    // window exists, so returning that null would NRE during startup - the
    // one thing this module promises never to do.
    File.WriteAllText(Path.Combine(dir, "ui-state.json"), "null")

    Assert.Equal(UiState.defaultState, UiState.read dir)

[<Fact>]
let ``a read of a null ui-state.json yields a state whose Theme is safe to touch`` () =
    let dir = tempAppDataDir ()
    File.WriteAllText(Path.Combine(dir, "ui-state.json"), "null")

    let state = UiState.read dir

    // The actual startup-crash reproduction: this member access is exactly
    // what `App.OnFrameworkInitializationCompleted` does first.
    Assert.Equal("Dark", state.Theme)
    Assert.True(state.DockLayout.IsNone)
