module Tests

open System.Text.Json
open Xunit
open FSharpQueryStudio.Protocol

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
          Kind = ConnectorKind("Sqlite")
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
        [ ConnectorKind("SqlServer")
          ConnectorKind("PostgreSql")
          ConnectorKind("Sqlite")
          ConnectorKind("Csv")
          ConnectorKind("Excel")
          ConnectorKind("RestJson") ]

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
let ``EvaluateParams round-trips through JSON for both Isolated and Persistent modes`` () =
    let isolated: EvaluateParams =
        { Source = "1 + 1"
          SessionId = None
          Mode = Isolated
          DocumentUri = "untitled:Script1.fsx"
          IsNotebookCell = false }

    let isolatedJson = JsonSerializer.Serialize(isolated, Serialization.options)
    let roundTrippedIsolated = JsonSerializer.Deserialize<EvaluateParams>(isolatedJson, Serialization.options)
    Assert.Equal(isolated, roundTrippedIsolated)

    let persistent: EvaluateParams =
        { Source = "x + 1"
          SessionId = Some "sess-1"
          Mode = Persistent
          DocumentUri = "file:///workspace/sales.fsx"
          IsNotebookCell = true }

    let persistentJson = JsonSerializer.Serialize(persistent, Serialization.options)
    let roundTrippedPersistent = JsonSerializer.Deserialize<EvaluateParams>(persistentJson, Serialization.options)
    Assert.Equal(persistent, roundTrippedPersistent)

[<Fact>]
let ``deserializing a WorkerRequestFrame with an unknown extra field does not throw`` () =
    let json =
        """{"id":"x1","method":"worker/health","params":{},"futureField":"from-a-newer-client"}"""

    let frame = JsonSerializer.Deserialize<WorkerRequestFrame>(json, Serialization.options)

    Assert.Equal("x1", frame.Id)
    Assert.Equal("worker/health", frame.Method)

[<Fact>]
let ``ConnectorKind Custom round-trips through JSON`` () =
    let custom = ConnectorKind.Custom "DuckDb"
    let json = JsonSerializer.Serialize(custom, Serialization.options)
    let roundTripped = JsonSerializer.Deserialize<ConnectorKind>(json, Serialization.options)
    Assert.Equal(custom, roundTripped)
    Assert.Contains("\"DuckDb\"", json)

[<Fact>]
let ``ConnectorKind deserializes from case object or raw string`` () =
    let objJson = """{"Case":"MyCustomStore"}"""
    let fromObj = JsonSerializer.Deserialize<ConnectorKind>(objJson, Serialization.options)
    Assert.Equal(ConnectorKind.Custom "MyCustomStore", fromObj)

    let strJson = "\"MyCustomStore\""
    let fromStr = JsonSerializer.Deserialize<ConnectorKind>(strJson, Serialization.options)
    Assert.Equal(ConnectorKind.Custom "MyCustomStore", fromStr)

[<Fact>]
let ``ConnectorListResult round-trips through JSON`` () =
    let dto: ConnectorListResult =
        { Connectors =
            [ { KindId = "Sqlite"
                DisplayName = "SQLite"
                Category = "sql"
                Icon = "database"
                FormFields =
                    [ { Name = "filePath"
                        Label = "Database Path"
                        Type = "file"
                        Required = true
                        Placeholder = Some "/path/to/db.sqlite"
                        DefaultValue = None
                        Options = None
                        SecretHandling = "none" } ] } ] }

    let json = JsonSerializer.Serialize(dto, Serialization.options)
    let roundTripped = JsonSerializer.Deserialize<ConnectorListResult>(json, Serialization.options)
    Assert.Equal(1, roundTripped.Connectors.Length)
    Assert.Equal("Sqlite", roundTripped.Connectors.[0].KindId)
    Assert.Equal("SQLite", roundTripped.Connectors.[0].DisplayName)
    Assert.Equal("file", roundTripped.Connectors.[0].FormFields.[0].Type)
