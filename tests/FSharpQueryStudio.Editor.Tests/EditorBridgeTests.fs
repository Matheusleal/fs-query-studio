module EditorBridgeTests

open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Editor

let private roundTrips (request: EditorRequest) =
    let envelope = EditorBridge.encode request

    match EditorBridge.decode envelope with
    | Ok decoded -> Assert.Equal(request, decoded)
    | Result.Error err -> failwithf "expected a successful decode, got %A" err

[<Fact>]
let ``TextChanged round-trips through encode/decode`` () =
    roundTrips (TextChanged("file:///a.fsx", 3, "let x = 1"))

[<Fact>]
let ``CompletionRequested round-trips through encode/decode`` () =
    roundTrips (CompletionRequested("file:///a.fsx", 3, 10, 4))

[<Fact>]
let ``HoverRequested round-trips through encode/decode`` () =
    roundTrips (HoverRequested("file:///a.fsx", 3, 10, 4))

[<Fact>]
let ``GoToDefinitionRequested round-trips through encode/decode`` () =
    roundTrips (GoToDefinitionRequested("file:///a.fsx", 3, 10, 4))

[<Fact>]
let ``ExecuteRequested with a selection round-trips through encode/decode`` () =
    roundTrips (ExecuteRequested("file:///a.fsx", 3, Some "1 + 1"))

[<Fact>]
let ``ExecuteRequested with no selection round-trips through encode/decode`` () =
    roundTrips (ExecuteRequested("file:///a.fsx", 3, None))

[<Fact>]
let ``CancelRequested round-trips through encode/decode`` () =
    roundTrips CancelRequested

[<Fact>]
let ``decoding an envelope with an unknown Kind produces a typed error, not an exception`` () =
    let envelope: EditorEnvelope =
        { Id = "x1"
          Kind = "not-a-real-kind"
          Payload = System.Text.Json.JsonDocument.Parse("{}").RootElement }

    match EditorBridge.decode envelope with
    | Result.Error _ -> ()
    | Ok decoded -> failwithf "expected an error, got %A" decoded

[<Fact>]
let ``decoding an envelope with a payload missing required fields produces a typed error, not an exception`` () =
    let envelope: EditorEnvelope =
        { Id = "x2"
          Kind = "textChanged"
          Payload = System.Text.Json.JsonDocument.Parse("""{"documentUri":"file:///a.fsx"}""").RootElement }

    match EditorBridge.decode envelope with
    | Result.Error _ -> ()
    | Ok decoded -> failwithf "expected an error, got %A" decoded
