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
let ``SignatureHelpRequested round-trips through encode/decode`` () =
    roundTrips (SignatureHelpRequested("file:///a.fsx", 3, 10, 4))

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

[<Fact>]
let ``encode then decode round-trips a RenameRequested request`` () =
    let request = RenameRequested("untitled:Script1.fsx", 3, 1, 4, "newName")
    let envelope = EditorBridge.encode request

    Assert.Equal("renameRequested", envelope.Kind)
    Assert.Equal(Ok request, EditorBridge.decode envelope)

[<Fact>]
let ``encodeNotification produces a well-formed renameResolved envelope`` () =
    let edits =
        Some
            [ { StartLine = 0
                StartColumn = 4
                EndLine = 0
                EndColumn = 5 } ]

    let envelope =
        EditorBridge.encodeNotification (RenameResolved("req1", "untitled:Script1.fsx", edits, "newName"))

    Assert.Equal("req1", envelope.Id)
    Assert.Equal("renameResolved", envelope.Kind)

    let payload =
        System.Text.Json.JsonSerializer.Deserialize<RenameResolvedPayload>(envelope.Payload, Serialization.options)

    Assert.Equal(edits, payload.Edits)
    Assert.Equal("newName", payload.NewName)

[<Fact>]
let ``encodeNotification for DocumentLoaded produces kind documentLoaded with uri version and text`` () =
    let envelope = EditorBridge.encodeNotification (DocumentLoaded("untitled:Script2.fsx", 3, "let x = 1"))

    Assert.Equal("documentLoaded", envelope.Kind)

    let payload =
        System.Text.Json.JsonSerializer.Deserialize<DocumentLoadedPayload>(envelope.Payload, Serialization.options)

    Assert.Equal("untitled:Script2.fsx", payload.DocumentUri)
    Assert.Equal(3, payload.Version)
    Assert.Equal("let x = 1", payload.Text)
