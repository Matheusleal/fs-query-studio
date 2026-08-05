module Tests

open System.Text.Json
open Xunit
open FSharpQueryStudio.Protocol

[<Fact>]
let ``EditorEnvelope round-trips through JSON`` () =
    let original =
        { Id = "e1"
          Kind = "textChanged"
          Payload = JsonDocument.Parse("""{"documentUri":"file:///a.fsx"}""").RootElement }

    let json = JsonSerializer.Serialize(original, Serialization.options)
    let roundTripped = JsonSerializer.Deserialize<EditorEnvelope>(json, Serialization.options)

    Assert.Equal(original.Id, roundTripped.Id)
    Assert.Equal(original.Kind, roundTripped.Kind)
    Assert.Equal(
        original.Payload.GetProperty("documentUri").GetString(),
        roundTripped.Payload.GetProperty("documentUri").GetString()
    )

[<Fact>]
let ``WorkerRequestFrame round-trips through JSON`` () =
    let original =
        { Id = "30c714ec"
          Method = "analysis/check"
          Params = JsonDocument.Parse("""{"sessionId":"a8eebdab","version":42}""").RootElement }

    let json = JsonSerializer.Serialize(original, Serialization.options)
    let roundTripped = JsonSerializer.Deserialize<WorkerRequestFrame>(json, Serialization.options)

    Assert.Equal(original.Id, roundTripped.Id)
    Assert.Equal(original.Method, roundTripped.Method)
    Assert.Equal(42, roundTripped.Params.GetProperty("version").GetInt32())

[<Fact>]
let ``WorkerResponseFrame round-trips with a Result and no Error`` () =
    let original =
        { Id = "30c714ec"
          Result = Some(JsonDocument.Parse("""{"ok":true}""").RootElement)
          Error = None }

    let json = JsonSerializer.Serialize(original, Serialization.options)
    let roundTripped = JsonSerializer.Deserialize<WorkerResponseFrame>(json, Serialization.options)

    Assert.Equal(original.Id, roundTripped.Id)
    Assert.True(roundTripped.Result.IsSome)
    Assert.True(roundTripped.Error.IsNone)
    Assert.True(roundTripped.Result.Value.GetProperty("ok").GetBoolean())

[<Fact>]
let ``WorkerResponseFrame round-trips with an Error and no Result`` () =
    let original =
        { Id = "30c714ec"
          Result = None
          Error = Some { Code = "execution/faulted"; Message = "worker crashed" } }

    let json = JsonSerializer.Serialize(original, Serialization.options)
    let roundTripped = JsonSerializer.Deserialize<WorkerResponseFrame>(json, Serialization.options)

    Assert.True(roundTripped.Result.IsNone)
    Assert.Equal(Some "execution/faulted", roundTripped.Error |> Option.map (fun e -> e.Code))
    Assert.Equal(Some "worker crashed", roundTripped.Error |> Option.map (fun e -> e.Message))

[<Fact>]
let ``deserializing a WorkerRequestFrame with an unknown extra field does not throw`` () =
    let json =
        """{"id":"x1","method":"worker/health","params":{},"futureField":"from-a-newer-client"}"""

    let frame = JsonSerializer.Deserialize<WorkerRequestFrame>(json, Serialization.options)

    Assert.Equal("x1", frame.Id)
    Assert.Equal("worker/health", frame.Method)
