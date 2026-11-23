module Tests

open System.Text.Json
open Xunit
open FSharpQueryStudio.Protocol

[<Fact>]
let ``WorkerRequestFrame round-trips through JSON`` () =
    let original =
        {
            Id = "30c714ec"
            Method = "analysis/check"
            Params = JsonDocument.Parse("""{"sessionId":"a8eebdab","version":42}""").RootElement
        }

    let json = JsonSerializer.Serialize(original, Serialization.options)

    let roundTripped =
        JsonSerializer.Deserialize<WorkerRequestFrame>(json, Serialization.options)

    Assert.Equal(original.Id, roundTripped.Id)
    Assert.Equal(original.Method, roundTripped.Method)
    Assert.Equal(42, roundTripped.Params.GetProperty("version").GetInt32())

[<Fact>]
let ``WorkerResponseFrame round-trips with a Result and no Error`` () =
    let original =
        {
            Id = "30c714ec"
            Result = Some(JsonDocument.Parse("""{"ok":true}""").RootElement)
            Error = None
        }

    let json = JsonSerializer.Serialize(original, Serialization.options)

    let roundTripped =
        JsonSerializer.Deserialize<WorkerResponseFrame>(json, Serialization.options)

    Assert.Equal(original.Id, roundTripped.Id)
    Assert.True(roundTripped.Result.IsSome)
    Assert.True(roundTripped.Error.IsNone)
    Assert.True(roundTripped.Result.Value.GetProperty("ok").GetBoolean())

[<Fact>]
let ``WorkerResponseFrame round-trips with an Error and no Result`` () =
    let original =
        {
            Id = "30c714ec"
            Result = None
            Error =
                Some
                    {
                        Code = "execution/faulted"
                        Message = "worker crashed"
                    }
        }

    let json = JsonSerializer.Serialize(original, Serialization.options)

    let roundTripped =
        JsonSerializer.Deserialize<WorkerResponseFrame>(json, Serialization.options)

    Assert.True(roundTripped.Result.IsNone)
    Assert.Equal(Some "execution/faulted", roundTripped.Error |> Option.map (fun e -> e.Code))
    Assert.Equal(Some "worker crashed", roundTripped.Error |> Option.map (fun e -> e.Message))

[<Fact>]
let ``ConnectionDefinition round-trips through JSON, including a None SecretReference`` () =
    let original: ConnectionDefinition =
        {
            Id = System.Guid.Parse("11111111-1111-1111-1111-111111111111")
            Name = "Sales"
            Kind = ConnectorKind("Sqlite")
            Configuration = JsonDocument.Parse("""{"filePath":"C:/data/sales.db"}""").RootElement
            SecretReference = None
            IsTrusted = true
        }

    let json = JsonSerializer.Serialize(original, Serialization.options)

    let roundTripped =
        JsonSerializer.Deserialize<ConnectionDefinition>(json, Serialization.options)

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
        [
            ConnectorKind("SqlServer")
            ConnectorKind("PostgreSql")
            ConnectorKind("Sqlite")
            ConnectorKind("Csv")
            ConnectorKind("Excel")
            ConnectorKind("RestJson")
        ]

    for kind in kinds do
        let original: ConnectionDefinition =
            {
                Id = System.Guid.NewGuid()
                Name = "Conn"
                Kind = kind
                Configuration = JsonDocument.Parse("{}").RootElement
                SecretReference = Some "vault://sales-db"
                IsTrusted = false
            }

        let json = JsonSerializer.Serialize(original, Serialization.options)

        let roundTripped =
            JsonSerializer.Deserialize<ConnectionDefinition>(json, Serialization.options)

        Assert.Equal(kind, roundTripped.Kind)
        Assert.Equal(Some "vault://sales-db", roundTripped.SecretReference)
        Assert.Equal(false, roundTripped.IsTrusted)

[<Fact>]
let ``ResultPageRequest and ResultPage round-trip through JSON`` () =
    let request: ResultPageRequest =
        {
            SessionId = "sess-1"
            ContinuationToken = "token-1"
        }

    let requestJson = JsonSerializer.Serialize(request, Serialization.options)

    let roundTrippedRequest =
        JsonSerializer.Deserialize<ResultPageRequest>(requestJson, Serialization.options)

    Assert.Equal(request, roundTrippedRequest)

    let page: ResultPage =
        {
            Rows = [| [| box "a"; box 1 |] |]
            IsTruncated = true
            ContinuationToken = Some "token-2"
        }

    let pageJson = JsonSerializer.Serialize(page, Serialization.options)

    let roundTrippedPage =
        JsonSerializer.Deserialize<ResultPage>(pageJson, Serialization.options)

    Assert.Equal(page.IsTruncated, roundTrippedPage.IsTruncated)
    Assert.Equal(page.ContinuationToken, roundTrippedPage.ContinuationToken)
    Assert.Equal("a", roundTrippedPage.Rows.[0].[0].ToString())

[<Fact>]
let ``EvaluateParams round-trips through JSON for both Isolated and Persistent modes`` () =
    let isolated: EvaluateParams =
        {
            Source = "1 + 1"
            SessionId = None
            Mode = Isolated
            DocumentUri = "untitled:Script1.fsx"
            IsNotebookCell = false
        }

    let isolatedJson = JsonSerializer.Serialize(isolated, Serialization.options)

    let roundTrippedIsolated =
        JsonSerializer.Deserialize<EvaluateParams>(isolatedJson, Serialization.options)

    Assert.Equal(isolated, roundTrippedIsolated)

    let persistent: EvaluateParams =
        {
            Source = "x + 1"
            SessionId = Some "sess-1"
            Mode = Persistent
            DocumentUri = "file:///workspace/sales.fsx"
            IsNotebookCell = true
        }

    let persistentJson = JsonSerializer.Serialize(persistent, Serialization.options)

    let roundTrippedPersistent =
        JsonSerializer.Deserialize<EvaluateParams>(persistentJson, Serialization.options)

    Assert.Equal(persistent, roundTrippedPersistent)

[<Fact>]
let ``deserializing a WorkerRequestFrame with an unknown extra field does not throw`` () =
    let json =
        """{"id":"x1","method":"worker/health","params":{},"futureField":"from-a-newer-client"}"""

    let frame =
        JsonSerializer.Deserialize<WorkerRequestFrame>(json, Serialization.options)

    Assert.Equal("x1", frame.Id)
    Assert.Equal("worker/health", frame.Method)

[<Fact>]
let ``ConnectorKind Custom round-trips through JSON`` () =
    let custom = ConnectorKind.Custom "DuckDb"
    let json = JsonSerializer.Serialize(custom, Serialization.options)

    let roundTripped =
        JsonSerializer.Deserialize<ConnectorKind>(json, Serialization.options)

    Assert.Equal(custom, roundTripped)
    Assert.Contains("\"DuckDb\"", json)

[<Fact>]
let ``ConnectorKind deserializes from case object or raw string`` () =
    let objJson = """{"Case":"MyCustomStore"}"""

    let fromObj =
        JsonSerializer.Deserialize<ConnectorKind>(objJson, Serialization.options)

    Assert.Equal(ConnectorKind.Custom "MyCustomStore", fromObj)

    let strJson = "\"MyCustomStore\""

    let fromStr =
        JsonSerializer.Deserialize<ConnectorKind>(strJson, Serialization.options)

    Assert.Equal(ConnectorKind.Custom "MyCustomStore", fromStr)

[<Fact>]
let ``ConnectorListResult round-trips through JSON`` () =
    let dto: ConnectorListResult =
        {
            Connectors =
                [
                    {
                        KindId = "Sqlite"
                        DisplayName = "SQLite"
                        Category = "sql"
                        Icon = "database"
                        FormFields =
                            [
                                {
                                    Name = "filePath"
                                    Label = "Database Path"
                                    Type = "file"
                                    Required = true
                                    Placeholder = Some "/path/to/db.sqlite"
                                    DefaultValue = None
                                    Options = None
                                    SecretHandling = "none"
                                }
                            ]
                    }
                ]
        }

    let json = JsonSerializer.Serialize(dto, Serialization.options)

    let roundTripped =
        JsonSerializer.Deserialize<ConnectorListResult>(json, Serialization.options)

    Assert.Equal(1, roundTripped.Connectors.Length)
    Assert.Equal("Sqlite", roundTripped.Connectors.[0].KindId)
    Assert.Equal("SQLite", roundTripped.Connectors.[0].DisplayName)
    Assert.Equal("file", roundTripped.Connectors.[0].FormFields.[0].Type)

type QueryWithToRawSql() =
    member _.ToRawSql() = "SELECT * FROM Users"

type QueryWithFailingToRawSql() =
    member _.ToRawSql() = failwith "error in ToRawSql"
    override _.ToString() = "fallback sql"

type QueryPlain() =
    override _.ToString() = "SELECT 42"

[<Fact>]
let ``ResultKind toRawSql and toRawSqlWithParameters cover default extractors and edge cases`` () =
    // null query
    Assert.Equal("", ResultKind.toRawSql null)
    let nullSql, nullParams = ResultKind.toRawSqlWithParameters null
    Assert.Equal("", nullSql)
    Assert.Empty(nullParams)

    // string query
    Assert.Equal("SELECT 1", ResultKind.toRawSql "SELECT 1")

    // object with ToRawSql method
    let q1 = QueryWithToRawSql()
    Assert.Equal("SELECT * FROM Users", ResultKind.toRawSql q1)

    // object without ToRawSql method
    let q2 = QueryPlain()
    Assert.Equal("SELECT 42", ResultKind.toRawSql q2)

    // object whose ToRawSql throws
    let q3 = QueryWithFailingToRawSql()
    Assert.Equal("fallback sql", ResultKind.toRawSql q3)

[<Fact>]
let ``ResultKind RegisterRawSqlExtractor allows customizing SQL extraction`` () =
    try
        ResultKind.RegisterRawSqlExtractor((fun _ -> "CUSTOM_SQL"))
        Assert.Equal("CUSTOM_SQL", ResultKind.toRawSql 123)

        ResultKind.RegisterRawSqlExtractor((fun _ -> "PARAM_SQL"), (fun _ -> ("PARAM_SQL", [ ("@id", box 1) ])))
        let sql, p = ResultKind.toRawSqlWithParameters 456
        Assert.Equal("PARAM_SQL", sql)
        Assert.Single(p) |> ignore
    finally
        // restore default
        ResultKind.RegisterRawSqlExtractor(fun q ->
            match q with
            | null -> ""
            | :? string as s -> s
            | other -> string other)

[<Fact>]
let ``ConnectorKind methods, equality, and JSON variations`` () =
    let k = ConnectorKind "PostgreSql"
    Assert.Equal("PostgreSql", k.ToString())
    Assert.Equal(k.GetHashCode(), (ConnectorKind "postgresql").GetHashCode())
    Assert.True(k.Equals(box (ConnectorKind "POSTGRESQL")))
    Assert.False(k.Equals(box "not-a-connector-kind"))
    Assert.False(k.Equals(null))

    let nullKind = ConnectorKind(null)
    Assert.Equal("", nullKind.Id)

    // Deserialization with lowercase "case" property
    let jsonCaseLower = """{"case": "DuckDb"}"""

    let fromLower =
        JsonSerializer.Deserialize<ConnectorKind>(jsonCaseLower, Serialization.options)

    Assert.Equal("DuckDb", fromLower.Id)

    // Deserialization with object lacking case/Case property
    let jsonNoCase = """{"other": 123}"""

    let fromNoCase =
        JsonSerializer.Deserialize<ConnectorKind>(jsonNoCase, Serialization.options)

    Assert.Contains("other", fromNoCase.Id)

[<Fact>]
let ``ResultEnvelope, Column, and OutputLine types can be constructed and accessed`` () =
    let col: Column = { Name = "Col1"; DisplayType = "int" }
    Assert.Equal("Col1", col.Name)
    Assert.Equal("int", col.DisplayType)

    let outLine: OutputLine =
        {
            Stream = OutputStream.Stdout
            Text = "Hello"
        }

    Assert.Equal(OutputStream.Stdout, outLine.Stream)
    Assert.Equal("Hello", outLine.Text)

    let env: ResultEnvelope =
        {
            Kind = ResultKind.Scalar
            DisplayText = Some "42"
            Schema = [ col ]
            Rows = [| [| box 42 |] |]
            IsTruncated = false
            ContinuationToken = None
            ElapsedMs = 10L
            Output = [ outLine ]
            RawQuery = None
            Parameters = []
        }

    Assert.Equal(ResultKind.Scalar, env.Kind)
    Assert.Equal(Some "42", env.DisplayText)
    Assert.Null(env.RawQuery |> Option.toObj)
    Assert.Empty(env.Parameters)

    // Test QueryParameter and LastQueryTracker
    LastQueryTracker.Reset()
    Assert.True(LastQueryTracker.Current.IsNone)
    LastQueryTracker.Record("SELECT 1", [ "@p", box 10 ])
    Assert.True(LastQueryTracker.Current.IsSome)
    let sql, parms = LastQueryTracker.Current.Value
    Assert.Equal("SELECT 1", sql)
    Assert.Equal(1, parms.Length)
    LastQueryTracker.Reset()
    Assert.True(LastQueryTracker.Current.IsNone)
