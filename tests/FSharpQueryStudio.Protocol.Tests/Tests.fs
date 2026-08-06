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
let ``ConnectionDefinition round-trips through JSON, including a None SecretReference`` () =
    let original: ConnectionDefinition =
        { Id = System.Guid.Parse("11111111-1111-1111-1111-111111111111")
          Name = "Sales"
          Kind = ConnectorKind.Sqlite
          Configuration = JsonDocument.Parse("""{"filePath":"C:/data/sales.db"}""").RootElement
          SecretReference = None
          IsTrusted = true }

    let json = JsonSerializer.Serialize(original, Serialization.options)
    let roundTripped = JsonSerializer.Deserialize<ConnectionDefinition>(json, Serialization.options)

    Assert.Equal(original.Id, roundTripped.Id)
    Assert.Equal(original.Name, roundTripped.Name)
    Assert.Equal(original.Kind, roundTripped.Kind)
    Assert.Equal(
        original.Configuration.GetProperty("filePath").GetString(),
        roundTripped.Configuration.GetProperty("filePath").GetString()
    )
    Assert.Equal(original.SecretReference, roundTripped.SecretReference)
    Assert.Equal(original.IsTrusted, roundTripped.IsTrusted)

[<Fact>]
let ``ConnectionDefinition round-trips a Some SecretReference and every ConnectorKind`` () =
    let kinds =
        [ ConnectorKind.SqlServer
          ConnectorKind.PostgreSql
          ConnectorKind.Sqlite
          ConnectorKind.Csv
          ConnectorKind.Excel
          ConnectorKind.RestJson ]

    for kind in kinds do
        let original: ConnectionDefinition =
            { Id = System.Guid.NewGuid()
              Name = "Conn"
              Kind = kind
              Configuration = JsonDocument.Parse("{}").RootElement
              SecretReference = Some "vault://sales-db"
              IsTrusted = false }

        let json = JsonSerializer.Serialize(original, Serialization.options)
        let roundTripped = JsonSerializer.Deserialize<ConnectionDefinition>(json, Serialization.options)

        Assert.Equal(kind, roundTripped.Kind)
        Assert.Equal(Some "vault://sales-db", roundTripped.SecretReference)
        Assert.Equal(false, roundTripped.IsTrusted)

[<Fact>]
let ``ResultPageRequest and ResultPage round-trip through JSON`` () =
    let request: ResultPageRequest =
        { SessionId = "sess-1"; ContinuationToken = "token-1" }

    let requestJson = JsonSerializer.Serialize(request, Serialization.options)
    let roundTrippedRequest = JsonSerializer.Deserialize<ResultPageRequest>(requestJson, Serialization.options)
    Assert.Equal(request, roundTrippedRequest)

    let page: ResultPage =
        { Rows = [| [| box "a"; box 1 |] |]
          IsTruncated = true
          ContinuationToken = Some "token-2" }

    let pageJson = JsonSerializer.Serialize(page, Serialization.options)
    let roundTrippedPage = JsonSerializer.Deserialize<ResultPage>(pageJson, Serialization.options)
    Assert.Equal(page.IsTruncated, roundTrippedPage.IsTruncated)
    Assert.Equal(page.ContinuationToken, roundTrippedPage.ContinuationToken)
    Assert.Equal("a", roundTrippedPage.Rows.[0].[0].ToString())

[<Fact>]
let ``deserializing a WorkerRequestFrame with an unknown extra field does not throw`` () =
    let json =
        """{"id":"x1","method":"worker/health","params":{},"futureField":"from-a-newer-client"}"""

    let frame = JsonSerializer.Deserialize<WorkerRequestFrame>(json, Serialization.options)

    Assert.Equal("x1", frame.Id)
    Assert.Equal("worker/health", frame.Method)
