module HandlersTests

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Execution
open FSharpQueryStudio.Storage
open FSharpQueryStudio.Worker

let private emptyParamsRequest (id: string) (method_: string) : WorkerRequestFrame =
    { Id = id
      Method = method_
      Params = JsonDocument.Parse("{}").RootElement }

let private evaluateRequest (id: string) (source: string) : WorkerRequestFrame =
    { Id = id
      Method = "execution/evaluate"
      Params =
        JsonSerializer.SerializeToElement<EvaluateParams>(
            { Source = source; SessionId = None; Mode = Isolated },
            Serialization.options
        ) }

let private persistentEvaluateRequest (id: string) (source: string) (sessionId: string) : WorkerRequestFrame =
    { Id = id
      Method = "execution/evaluate"
      Params =
        JsonSerializer.SerializeToElement<EvaluateParams>(
            { Source = source
              SessionId = Some sessionId
              Mode = Persistent },
            Serialization.options
        ) }

let private analysisCheckRequest (id: string) (source: string) : WorkerRequestFrame =
    { Id = id
      Method = "analysis/check"
      Params =
        JsonSerializer.SerializeToElement<AnalysisCheckParams>(
            { DocumentUri = "file:///workspace/sales.fsx"
              Version = 1
              Source = source },
            Serialization.options
        ) }

let private resultsPageRequest (id: string) (sessionId: string) (token: string) : WorkerRequestFrame =
    { Id = id
      Method = "results/page"
      Params =
        JsonSerializer.SerializeToElement<ResultPageRequest>(
            { SessionId = sessionId; ContinuationToken = token },
            Serialization.options
        ) }

let private analysisHoverRequest (id: string) (source: string) (line: int) (column: int) : WorkerRequestFrame =
    { Id = id
      Method = "analysis/hover"
      Params =
        JsonSerializer.SerializeToElement<AnalysisHoverParams>(
            ({ DocumentUri = "file:///workspace/sales.fsx"
               Version = 1
               Source = source
               Line = line
               Column = column }: AnalysisHoverParams),
            Serialization.options
        ) }

let private analysisDefinitionRequest (id: string) (source: string) (line: int) (column: int) : WorkerRequestFrame =
    { Id = id
      Method = "analysis/definition"
      Params =
        JsonSerializer.SerializeToElement<AnalysisDefinitionParams>(
            ({ DocumentUri = "file:///workspace/sales.fsx"
               Version = 1
               Source = source
               Line = line
               Column = column }: AnalysisDefinitionParams),
            Serialization.options
        ) }

let private analysisSignatureHelpRequest (id: string) (source: string) (line: int) (column: int) : WorkerRequestFrame =
    { Id = id
      Method = "analysis/signatureHelp"
      Params =
        JsonSerializer.SerializeToElement<AnalysisSignatureHelpParams>(
            ({ DocumentUri = "file:///workspace/sales.fsx"
               Version = 1
               Source = source
               Line = line
               Column = column }: AnalysisSignatureHelpParams),
            Serialization.options
        ) }

let private completionRequest (id: string) (source: string) (line: int) (column: int) : WorkerRequestFrame =
    { Id = id
      Method = "analysis/complete"
      Params =
        JsonSerializer.SerializeToElement<AnalysisCompletionParams>(
            { DocumentUri = "untitled:Script1.fsx"
              Version = 1
              Source = source
              Line = line
              Column = column },
            Serialization.options
        ) }

[<Fact>]
let ``dispatch on worker/health returns a Result with no Error`` () =
    task {
        let request = emptyParamsRequest "h1" "worker/health"

        let! response = Handlers.dispatch request

        Assert.Equal("h1", response.Id)
        Assert.True(response.Result.IsSome)
        Assert.True(response.Error.IsNone)
    }

[<Fact>]
let ``dispatch on worker/health returns a non-empty version`` () =
    task {
        let request = emptyParamsRequest "h2" "worker/health"

        let! response = Handlers.dispatch request
        let health = JsonSerializer.Deserialize<HealthInfo>(response.Result.Value, Serialization.options)

        Assert.False(System.String.IsNullOrWhiteSpace(health.Version))
    }

[<Fact>]
let ``dispatch on an unknown method returns a structured error and no Result`` () =
    task {
        let request = emptyParamsRequest "u1" "not/a-real-method"

        let! response = Handlers.dispatch request

        Assert.Equal("u1", response.Id)
        Assert.True(response.Result.IsNone)

        match response.Error with
        | Some err ->
            Assert.Equal("method/unknown", err.Code)
            Assert.Contains("not/a-real-method", err.Message)
        | None -> failwith "expected an Error"
    }

[<Fact>]
let ``dispatch on execution/cancel acknowledges even when no evaluation is running`` () =
    task {
        let! response = Handlers.dispatch (emptyParamsRequest "c1" "execution/cancel")

        Assert.Equal("c1", response.Id)
        Assert.True(response.Result.IsSome)
        Assert.True(response.Error.IsNone)
    }

[<Fact>]
let ``handleEvaluate captures printed stdout into the envelope's Output lines`` () =
    let response = Handlers.handleEvaluate (evaluateRequest "p1" "printfn \"hello from fsi\"")

    Assert.True(response.Result.IsSome)
    let envelope = JsonSerializer.Deserialize<ResultEnvelope>(response.Result.Value, Serialization.options)

    Assert.Contains(envelope.Output, (fun (line: OutputLine) -> line.Text.Contains("hello from fsi")))

[<Fact>]
let ``handleEvaluate rejects a second concurrent evaluate with a structured busy error`` () =
    task {
        // A plain file check, not a call back into a host assembly - see
        // Execution.Tests/EvaluationTests.fs for why the earlier
        // static-field-plus-self-reference design was unreliable.
        let longRunningScript =
            "let mutable i = 0 in (while i < 2_000_000_000 && not (System.IO.File.Exists(\".cancel-requested\")) do i <- i + 1); i"

        // Reserve the slot synchronously (mirrors Program.fs's own loop)
        // rather than backgrounding the whole handleEvaluate call and
        // hoping a Task.Delay outlasts the threadpool picking it up - a
        // freshly started process's threadpool can take a while to inject
        // worker threads under load, which made this test flaky before.
        match Handlers.beginEvaluate (evaluateRequest "e1" longRunningScript) with
        | Choice2Of2 _ -> failwith "expected to reserve the evaluation slot"
        | Choice1Of2 workDir ->
            let firstTask =
                Task.Run(fun () -> Handlers.runEvaluate (evaluateRequest "e1" longRunningScript) workDir)

            let! secondResponse = Handlers.dispatch (evaluateRequest "e2" "1")

            Assert.True(secondResponse.Result.IsNone)

            match secondResponse.Error with
            | Some err -> Assert.Equal("execution/busy", err.Code)
            | None -> failwith "expected a busy error"

            let! _ = Handlers.dispatch (emptyParamsRequest "c2" "execution/cancel")
            ()

            let! _ = firstTask
            ()
    }

[<Fact>]
let ``handleEvaluate issues a continuation token for a truncated result, and results/page returns the rest`` () =
    task {
        let response = Handlers.handleEvaluate (evaluateRequest "pg1" "[1 .. 510]")
        let envelope = JsonSerializer.Deserialize<ResultEnvelope>(response.Result.Value, Serialization.options)

        Assert.True(envelope.IsTruncated)
        Assert.Equal(500, envelope.Rows.Length)

        let token =
            match envelope.ContinuationToken with
            | Some t -> t
            | None -> failwith "expected a continuation token"

        let! pageResponse = Handlers.dispatch (resultsPageRequest "pg1-page1" "s1" token)

        Assert.True(pageResponse.Result.IsSome)
        let page = JsonSerializer.Deserialize<ResultPage>(pageResponse.Result.Value, Serialization.options)

        Assert.Equal(10, page.Rows.Length)
        Assert.False(page.IsTruncated)
        Assert.True(page.ContinuationToken.IsNone)
    }

[<Fact>]
let ``results/page returns a structured error for an unknown or expired token`` () =
    task {
        let! response = Handlers.dispatch (resultsPageRequest "bad1" "s1" "not-a-real-token")

        Assert.True(response.Result.IsNone)

        match response.Error with
        | Some err -> Assert.Equal("results/unknownToken", err.Code)
        | None -> failwith "expected an error"
    }

[<Fact>]
let ``a new evaluate invalidates a previous result's continuation token`` () =
    task {
        let firstResponse = Handlers.handleEvaluate (evaluateRequest "pg2" "[1 .. 510]")

        let firstEnvelope =
            JsonSerializer.Deserialize<ResultEnvelope>(firstResponse.Result.Value, Serialization.options)

        let oldToken = firstEnvelope.ContinuationToken |> Option.get

        Handlers.handleEvaluate (evaluateRequest "pg3" "1") |> ignore

        let! pageResponse = Handlers.dispatch (resultsPageRequest "pg2-page" "s1" oldToken)

        Assert.True(pageResponse.Result.IsNone)

        match pageResponse.Error with
        | Some err -> Assert.Equal("results/unknownToken", err.Code)
        | None -> failwith "expected an error"
    }

[<Fact>]
let ``handleEvaluate reuses a Persistent-mode session's bindings across two calls under the same SessionId`` () =
    let sessionId = "sess-" + System.Guid.NewGuid().ToString("N")

    try
        let firstResponse = Handlers.handleEvaluate (persistentEvaluateRequest "pers1" "let x = 41" sessionId)
        Assert.True(firstResponse.Result.IsSome)

        // A pure append of round 1's text - E.3's invalidation rule
        // requires this to keep the session (and x's binding) alive.
        let secondResponse =
            Handlers.handleEvaluate (persistentEvaluateRequest "pers2" "let x = 41\nx + 1" sessionId)
        Assert.True(secondResponse.Result.IsSome)

        let envelope =
            JsonSerializer.Deserialize<ResultEnvelope>(secondResponse.Result.Value, Serialization.options)

        Assert.Equal(ResultKind.Scalar, envelope.Kind)
        Assert.Equal(Some "42", envelope.DisplayText)
    finally
        SessionStore.reset sessionId

let private sessionResetRequest (id: string) (sessionId: string) : WorkerRequestFrame =
    { Id = id
      Method = "session/reset"
      Params =
        JsonSerializer.SerializeToElement<SessionResetParams>({ SessionId = sessionId }, Serialization.options) }

[<Fact>]
let ``dispatch on session/reset acknowledges even when nothing is tracked for that SessionId`` () =
    task {
        let! response = Handlers.dispatch (sessionResetRequest "rst1" "never-started")

        Assert.Equal("rst1", response.Id)
        Assert.True(response.Result.IsSome)
        Assert.True(response.Error.IsNone)
    }

[<Fact>]
let ``session/reset actually drops a Persistent-mode session's bindings`` () =
    task {
        let sessionId = "sess-" + System.Guid.NewGuid().ToString("N")

        try
            let firstResponse = Handlers.handleEvaluate (persistentEvaluateRequest "rst2" "let x = 41" sessionId)
            Assert.True(firstResponse.Result.IsSome)

            let! resetResponse = Handlers.dispatch (sessionResetRequest "rst3" sessionId)
            Assert.True(resetResponse.Result.IsSome)

            let secondResponse = Handlers.handleEvaluate (persistentEvaluateRequest "rst4" "x + 1" sessionId)

            let envelope =
                JsonSerializer.Deserialize<ResultEnvelope>(secondResponse.Result.Value, Serialization.options)

            Assert.Equal(ResultKind.Error, envelope.Kind)
        finally
            SessionStore.reset sessionId
    }

[<Fact>]
let ``dispatch on analysis/check returns at least one diagnostic for a script with a type error`` () =
    task {
        let! response = Handlers.handleAnalysisCheck (analysisCheckRequest "a1" "let x : int = \"not an int\"")

        Assert.True(response.Result.IsSome)
        let result = JsonSerializer.Deserialize<AnalysisCheckResult>(response.Result.Value, Serialization.options)

        Assert.NotEmpty(result.Diagnostics)
    }

[<Fact>]
let ``dispatch on analysis/hover returns the display name and type for a local binding`` () =
    task {
        // "total" ends at column 9 (0-based) on line 1: "let total = 42".
        let! response = Handlers.handleAnalysisHover (analysisHoverRequest "h1" "let total = 42" 1 9)

        Assert.True(response.Result.IsSome)
        let result = JsonSerializer.Deserialize<AnalysisHoverResult>(response.Result.Value, Serialization.options)

        match result.Hover with
        | None -> failwith "expected hover info"
        | Some hover ->
            Assert.Equal("total", hover.DisplayName)
            Assert.Equal("int", hover.Type)
    }

[<Fact>]
let ``dispatch on analysis/definition resolves a use site back to its own declaration`` () =
    task {
        let source = "let helper (x: int) : int = x + 1\n\nlet total = helper 41\n"
        // "helper" ends at column 18 (0-based) on line 3: "let total = helper 41".
        let! response = Handlers.dispatch (analysisDefinitionRequest "d1" source 3 18)

        Assert.True(response.Result.IsSome)

        let result =
            JsonSerializer.Deserialize<AnalysisDefinitionResult>(response.Result.Value, Serialization.options)

        match result.Location with
        | None -> failwith "expected a definition location"
        | Some location -> Assert.Equal(0, location.StartLine)
    }

[<Fact>]
let ``dispatch on analysis/definition at a position with no resolvable symbol returns a Result with no location`` () =
    task {
        let source = "let helper (x: int) : int = x + 1\n\nlet total = helper 41\n"
        let! response = Handlers.dispatch (analysisDefinitionRequest "d2" source 2 0)

        Assert.True(response.Result.IsSome)

        let result =
            JsonSerializer.Deserialize<AnalysisDefinitionResult>(response.Result.Value, Serialization.options)

        Assert.Equal(None, result.Location)
    }

[<Fact>]
let ``dispatch on analysis/signatureHelp returns the parameter and return types for a function`` () =
    task {
        let source = "let add (x: int) (y: int) : int = x + y\n"
        // "add" ends at column 6 (0-based) on line 1.
        let! response = Handlers.dispatch (analysisSignatureHelpRequest "s1" source 1 6)

        Assert.True(response.Result.IsSome)

        let result =
            JsonSerializer.Deserialize<AnalysisSignatureHelpResult>(response.Result.Value, Serialization.options)

        match result.SignatureHelp with
        | None -> failwith "expected signature help"
        | Some info ->
            Assert.Equal("add", info.DisplayName)
            Assert.Equal<string list>([ "x: int"; "y: int" ], info.Parameters)
            Assert.Equal("int", info.ReturnType)
    }

[<Fact>]
let ``dispatch on analysis/signatureHelp on a non-callable symbol returns a Result with no signature`` () =
    task {
        let source = "let total = 42\n"
        // "total" ends at column 9 (0-based) on line 1.
        let! response = Handlers.dispatch (analysisSignatureHelpRequest "s2" source 1 9)

        Assert.True(response.Result.IsSome)

        let result =
            JsonSerializer.Deserialize<AnalysisSignatureHelpResult>(response.Result.Value, Serialization.options)

        Assert.Equal(None, result.SignatureHelp)
    }

[<Fact>]
let ``dispatch on analysis/complete returns tier-1 keyword and connector-helper names regardless of cursor position`` () =
    task {
        let! response = Handlers.dispatch (completionRequest "cmp1" "" 1 0)

        Assert.True(response.Error.IsNone)
        let result = JsonSerializer.Deserialize<AnalysisCompletionResult>(response.Result.Value, Serialization.options)
        Assert.Contains("let", result.Items)
        Assert.Contains("Connections.sql", result.Items)
    }

[<Fact>]
let ``dispatch on analysis/complete includes tier-2 semantic completions at the cursor position`` () =
    task {
        let source = "let myLocalValue = 1\nmyLocal"
        let! response = Handlers.dispatch (completionRequest "cmp2" source 2 8)

        Assert.True(response.Error.IsNone)
        let result = JsonSerializer.Deserialize<AnalysisCompletionResult>(response.Result.Value, Serialization.options)
        Assert.Contains("myLocalValue", result.Items)
    }

let private registerConnectionRequest (id: string) (connection: ConnectionDefinition) : WorkerRequestFrame =
    { Id = id
      Method = "connection/register"
      Params = JsonSerializer.SerializeToElement(connection, Serialization.options) }

let private testConnection (name: string) : ConnectionDefinition =
    { Id = Guid.NewGuid()
      Name = name
      Kind = Sqlite
      Configuration = JsonDocument.Parse("""{"FilePath":"C:\\data.db"}""").RootElement
      SecretReference = None
      IsTrusted = false }

[<Fact>]
let ``dispatch on connection/register persists the connection to workspace.json under the configured workspace directory`` () =
    task {
        let tempDir = Path.Combine(Path.GetTempPath(), "fqs-workspace-" + Guid.NewGuid().ToString("N"))
        Handlers.setWorkspaceDirForTests (Some tempDir)

        try
            let connection = testConnection "TestConn"
            let! response = Handlers.dispatch (registerConnectionRequest "reg1" connection)

            Assert.Equal("reg1", response.Id)
            Assert.True(response.Error.IsNone)
            Assert.True(response.Result.IsSome)

            let persisted = Workspace.readConnections tempDir
            Assert.Contains(persisted, fun c -> c.Name = "TestConn")
        finally
            Handlers.setWorkspaceDirForTests None

            if Directory.Exists tempDir then
                Directory.Delete(tempDir, true)
    }

[<Fact>]
let ``dispatch on connection/register replacing an existing connection keeps only one binding for that Id`` () =
    task {
        let tempDir = Path.Combine(Path.GetTempPath(), "fqs-workspace-" + Guid.NewGuid().ToString("N"))
        Handlers.setWorkspaceDirForTests (Some tempDir)

        try
            let original = testConnection "Original"
            let renamed = { original with Name = "Renamed" }

            let! _ = Handlers.dispatch (registerConnectionRequest "reg1" original)
            let! _ = Handlers.dispatch (registerConnectionRequest "reg2" renamed)

            let persisted = Workspace.readConnections tempDir
            Assert.Single(persisted) |> ignore
            Assert.Equal("Renamed", persisted.[0].Name)
        finally
            Handlers.setWorkspaceDirForTests None

            if Directory.Exists tempDir then
                Directory.Delete(tempDir, true)
    }

[<Fact>]
let ``execution/evaluate resolves Connections.sql against a connection registered via connection/register`` () =
    task {
        let tempDir = Path.Combine(Path.GetTempPath(), "fqs-workspace-" + Guid.NewGuid().ToString("N"))
        let dbPath = Path.Combine(tempDir, "orders.db")
        Handlers.setWorkspaceDirForTests (Some tempDir)

        try
            Directory.CreateDirectory tempDir |> ignore

            use connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}")
            connection.Open()
            use createCmd = connection.CreateCommand()
            createCmd.CommandText <- "create table Orders (Id integer primary key, Total real)"
            createCmd.ExecuteNonQuery() |> ignore
            use insertCmd = connection.CreateCommand()
            insertCmd.CommandText <- "insert into Orders (Id, Total) values (1, 100.0)"
            insertCmd.ExecuteNonQuery() |> ignore
            connection.Close()
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()

            let registerRequest =
                registerConnectionRequest
                    "reg1"
                    { Id = Guid.NewGuid()
                      Name = "Sales"
                      Kind = Sqlite
                      Configuration =
                        JsonSerializer.SerializeToElement({| FilePath = dbPath |}, Serialization.options)
                      SecretReference = None
                      IsTrusted = false }

            let! _ = Handlers.dispatch registerRequest

            let evaluateRequest = evaluateRequest "eval1" """Connections.sql "Sales" "select Total from Orders" [] |> fun r -> r.Rows |> Seq.head |> Array.head"""
            let! evalResponse = Handlers.dispatch evaluateRequest

            Assert.True(evalResponse.Error.IsNone)
            let result = JsonSerializer.Deserialize<ResultEnvelope>(evalResponse.Result.Value, Serialization.options)
            Assert.Equal(ResultKind.Scalar, result.Kind)
            Assert.Equal(Some "100", result.DisplayText)
        finally
            Handlers.setWorkspaceDirForTests None
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()

            if Directory.Exists tempDir then
                Directory.Delete(tempDir, true)
    }
