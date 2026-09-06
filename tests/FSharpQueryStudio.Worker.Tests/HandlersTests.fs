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

/// FSAC tracks each `analysis/*` document by `(documentUri, version)`
/// (Task 12's cutover from FCS's own per-request, stateless
/// `AnalysisContext`) — a strictly increasing `version` per uri, the same
/// convention every `FsacBridge.Tests` test already follows (see
/// `FsacBridgeCheckTests.fs`'s "a later check ... reflects the newer
/// source" test). Every analysis test below sends `Version = 1`, so each
/// one needs its own uri: reusing one uri with the same version across
/// tests that send genuinely different source text is untested,
/// unsupported territory (FSAC's own `fsharp/documentAnalyzed` is not
/// guaranteed to refire for a repeat version), not a real scenario the
/// Worker itself ever produces (the App always bumps `Version` on an
/// edit).
let private uniqueDocumentUri (label: string) : string =
    $"file:///workspace/{label}-{Guid.NewGuid()}.fsx"

let private emptyParamsRequest (id: string) (method_: string) : WorkerRequestFrame =
    { Id = id
      Method = method_
      Params = JsonDocument.Parse("{}").RootElement }

let private evaluateRequest (id: string) (source: string) : WorkerRequestFrame =
    { Id = id
      Method = "execution/evaluate"
      Params =
        JsonSerializer.SerializeToElement<EvaluateParams>(
            { Source = source
              SessionId = None
              Mode = Isolated
              DocumentUri = "untitled:Test.fsx" },
            Serialization.options
        ) }

let private persistentEvaluateRequest (id: string) (source: string) (sessionId: string) : WorkerRequestFrame =
    { Id = id
      Method = "execution/evaluate"
      Params =
        JsonSerializer.SerializeToElement<EvaluateParams>(
            { Source = source
              SessionId = Some sessionId
              Mode = Persistent
              DocumentUri = "untitled:Test.fsx" },
            Serialization.options
        ) }

let private evaluateRequestFor (id: string) (documentUri: string) (source: string) : WorkerRequestFrame =
    { Id = id
      Method = "execution/evaluate"
      Params =
        JsonSerializer.SerializeToElement<EvaluateParams>(
            { Source = source
              SessionId = None
              Mode = Isolated
              DocumentUri = documentUri },
            Serialization.options
        ) }

let private exportCsvRequest (id: string) (documentUri: string) (filePath: string) : WorkerRequestFrame =
    { Id = id
      Method = "results/exportCsv"
      Params =
        JsonSerializer.SerializeToElement<ExportCsvParams>(
            { DocumentUri = documentUri; FilePath = filePath },
            Serialization.options
        ) }

let private resultsDiscardRequest (id: string) (documentUri: string) : WorkerRequestFrame =
    { Id = id
      Method = "results/discard"
      Params =
        JsonSerializer.SerializeToElement<ResultsDiscardParams>(
            { DocumentUri = documentUri },
            Serialization.options
        ) }

let private analysisCheckRequest (id: string) (documentUri: string) (source: string) : WorkerRequestFrame =
    { Id = id
      Method = "analysis/check"
      Params =
        JsonSerializer.SerializeToElement<AnalysisCheckParams>(
            { DocumentUri = documentUri
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

let private analysisHoverRequest
    (id: string)
    (documentUri: string)
    (source: string)
    (line: int)
    (column: int)
    : WorkerRequestFrame =
    { Id = id
      Method = "analysis/hover"
      Params =
        JsonSerializer.SerializeToElement<AnalysisHoverParams>(
            ({ DocumentUri = documentUri
               Version = 1
               Source = source
               Line = line
               Column = column }: AnalysisHoverParams),
            Serialization.options
        ) }

let private analysisDefinitionRequest
    (id: string)
    (documentUri: string)
    (source: string)
    (line: int)
    (column: int)
    : WorkerRequestFrame =
    { Id = id
      Method = "analysis/definition"
      Params =
        JsonSerializer.SerializeToElement<AnalysisDefinitionParams>(
            ({ DocumentUri = documentUri
               Version = 1
               Source = source
               Line = line
               Column = column }: AnalysisDefinitionParams),
            Serialization.options
        ) }

let private analysisSignatureHelpRequest
    (id: string)
    (documentUri: string)
    (source: string)
    (line: int)
    (column: int)
    : WorkerRequestFrame =
    { Id = id
      Method = "analysis/signatureHelp"
      Params =
        JsonSerializer.SerializeToElement<AnalysisSignatureHelpParams>(
            ({ DocumentUri = documentUri
               Version = 1
               Source = source
               Line = line
               Column = column }: AnalysisSignatureHelpParams),
            Serialization.options
        ) }

let private completionRequest
    (id: string)
    (documentUri: string)
    (source: string)
    (line: int)
    (column: int)
    : WorkerRequestFrame =
    { Id = id
      Method = "analysis/complete"
      Params =
        JsonSerializer.SerializeToElement<AnalysisCompletionParams>(
            ({ DocumentUri = documentUri
               Version = 1
               Source = source
               Line = line
               Column = column }: AnalysisCompletionParams),
            Serialization.options
        ) }

let private renameRequest
    (id: string)
    (documentUri: string)
    (source: string)
    (line: int)
    (column: int)
    : WorkerRequestFrame =
    { Id = id
      Method = "analysis/rename"
      Params =
        JsonSerializer.SerializeToElement<AnalysisRenameParams>(
            ({ DocumentUri = documentUri
               Version = 1
               Source = source
               Line = line
               Column = column }: AnalysisRenameParams),
            Serialization.options
        ) }

[<Fact>]
let ``dispatch on analysis/rename finds every reference to the symbol at the given position`` () =
    task {
        let documentUri = uniqueDocumentUri "rename"
        let source = "let value = 1\nlet total = value + value"
        // "value" ends at column 9 (0-based) on line 1: "let value = 1" -
        // same end-of-identifier convention as the hover/definition/
        // signatureHelp requests above.
        let! response = Handlers.dispatch (renameRequest "rn1" documentUri source 1 9)

        Assert.True(response.Error.IsNone)
        let result = JsonSerializer.Deserialize<AnalysisRenameResult>(response.Result.Value, Serialization.options)
        Assert.True(result.Edits.IsSome)
        Assert.Equal(3, result.Edits.Value.Length) // the declaration plus two uses
    }

[<Fact>]
let ``dispatch on analysis/rename returns no edits for a position with no resolvable symbol`` () =
    task {
        let documentUri = uniqueDocumentUri "rename"
        let! response = Handlers.dispatch (renameRequest "rn2" documentUri "// just a comment" 1 5)

        Assert.True(response.Error.IsNone)
        let result = JsonSerializer.Deserialize<AnalysisRenameResult>(response.Result.Value, Serialization.options)
        Assert.True(result.Edits.IsNone)
    }

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
        let documentUri = uniqueDocumentUri "check"
        let! response = Handlers.handleAnalysisCheck (analysisCheckRequest "a1" documentUri "let x : int = \"not an int\"")

        Assert.True(response.Result.IsSome)
        let result = JsonSerializer.Deserialize<AnalysisCheckResult>(response.Result.Value, Serialization.options)

        Assert.NotEmpty(result.Diagnostics)
    }

[<Fact>]
let ``dispatch on analysis/hover returns the display name and type for a local binding`` () =
    task {
        let documentUri = uniqueDocumentUri "hover"
        // "total" ends at column 9 (0-based) on line 1: "let total = 42".
        let! response = Handlers.handleAnalysisHover (analysisHoverRequest "h1" documentUri "let total = 42" 1 9)

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
        let documentUri = uniqueDocumentUri "definition"
        let source = "let helper (x: int) : int = x + 1\n\nlet total = helper 41\n"
        // "helper" ends at column 18 (0-based) on line 3: "let total = helper 41".
        let! response = Handlers.dispatch (analysisDefinitionRequest "d1" documentUri source 3 18)

        Assert.True(response.Result.IsSome)

        let result =
            JsonSerializer.Deserialize<AnalysisDefinitionResult>(response.Result.Value, Serialization.options)

        match result.Location with
        | None -> failwith "expected a definition location"
        | Some location -> Assert.Equal(0, location.StartLine)
    }

[<Fact>]
let ``dispatch on analysis/definition reports a same-file declaration against the requesting document, not FSAC's own internal identity`` () =
    task {
        let documentUri = uniqueDocumentUri "definition"
        let source = "let helper (x: int) : int = x + 1\n\nlet total = helper 41\n"
        let! response = Handlers.dispatch (analysisDefinitionRequest "d3" documentUri source 3 18)

        let result =
            JsonSerializer.Deserialize<AnalysisDefinitionResult>(response.Result.Value, Serialization.options)

        match result.Location with
        | None -> failwith "expected a definition location"
        | Some location ->
            // FSAC echoes back its own (drive-letter-qualified,
            // percent-encoded) identity for the uri it was `didOpen`'d
            // under, which is meaningless to the editor and can never
            // equal the document URI the request came from. FsacBridge
            // rewrites a same-file result to the caller's own
            // `documentUri` so the editor's same-document check can
            // actually succeed - this is that rewrite, not a workaround
            // this handler does itself.
            Assert.Equal(documentUri, location.FilePath)
    }

[<Fact>]
let ``dispatch on analysis/definition at a position with no resolvable symbol returns a Result with no location`` () =
    task {
        let documentUri = uniqueDocumentUri "definition"
        let source = "let helper (x: int) : int = x + 1\n\nlet total = helper 41\n"
        let! response = Handlers.dispatch (analysisDefinitionRequest "d2" documentUri source 2 0)

        Assert.True(response.Result.IsSome)

        let result =
            JsonSerializer.Deserialize<AnalysisDefinitionResult>(response.Result.Value, Serialization.options)

        Assert.Equal(None, result.Location)
    }

[<Fact>]
let ``dispatch on analysis/signatureHelp returns the parameter and return types for a function`` () =
    task {
        let documentUri = uniqueDocumentUri "signatureHelp"
        let source = "let add (x: int) (y: int) : int = x + y\nlet result = add 1 2"
        // Real fsautocomplete 0.83.0 only returns a non-null
        // `textDocument/signatureHelp` result when the cursor sits inside
        // a call's argument list - confirmed during this task: querying
        // `add`'s own name (the definition site, or a call site's name
        // before any argument) always came back null. Column 17 on line 2
        // is on the first argument's own "1" in "let result = add 1 2".
        let! response = Handlers.dispatch (analysisSignatureHelpRequest "s1" documentUri source 2 17)

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
let ``dispatch on analysis/signatureHelp resolves only from inside a call's argument list, not merely at the callee's own name`` () =
    task {
        let documentUri = uniqueDocumentUri "signatureHelp"
        let source = "let add (x: int) (y: int) : int = x + y\nlet result = add 1 2"
        // Column 17 on line 2 sits on the first argument's own "1" -
        // genuinely inside the call's argument list. Column 14 sits right
        // after "add" itself, before any argument - real fsautocomplete
        // 0.83.0 returns null there (confirmed during this task), the same
        // "callee's own name" null Fsac.Tests' own signatureHelp tests
        // document. Both requests share one uri/version deliberately -
        // it's the same unchanged document queried at two positions, not
        // an edit.
        let! insideArgs = Handlers.dispatch (analysisSignatureHelpRequest "s3" documentUri source 2 17)
        let! atCalleeName = Handlers.dispatch (analysisSignatureHelpRequest "s4" documentUri source 2 14)

        let parse (r: WorkerResponseFrame) =
            JsonSerializer.Deserialize<AnalysisSignatureHelpResult>(r.Result.Value, Serialization.options)

        match (parse insideArgs).SignatureHelp with
        | None -> failwith "expected signature help inside the call's argument list"
        | Some info -> Assert.Equal("add", info.DisplayName)

        Assert.Equal(None, (parse atCalleeName).SignatureHelp)
    }

[<Fact>]
let ``dispatch on analysis/signatureHelp on a non-callable symbol returns a Result with no signature`` () =
    task {
        let documentUri = uniqueDocumentUri "signatureHelp"
        let source = "let total = 42\n"
        // "total" ends at column 9 (0-based) on line 1.
        let! response = Handlers.dispatch (analysisSignatureHelpRequest "s2" documentUri source 1 9)

        Assert.True(response.Result.IsSome)

        let result =
            JsonSerializer.Deserialize<AnalysisSignatureHelpResult>(response.Result.Value, Serialization.options)

        Assert.Equal(None, result.SignatureHelp)
    }

[<Fact>]
let ``dispatch on analysis/complete on an empty document returns the connector-helper names in scope from the prologue`` () =
    task {
        let documentUri = uniqueDocumentUri "complete"
        let! response = Handlers.dispatch (completionRequest "cmp1" documentUri "" 1 0)

        Assert.True(response.Error.IsNone)
        let result = JsonSerializer.Deserialize<AnalysisCompletionResult>(response.Result.Value, Serialization.options)
        // FSAC's own top-level-scope completion offers "Connections" (the
        // type itself, brought into scope by the prologue's `open
        // FSharpQueryStudio.Connectors`) - not the dotted "Connections.sql"
        // the old FCS-direct handler's static tier-1 list used to include
        // unconditionally. A member like "sql" only shows up once the
        // caller is actually completing after "Connections." - see the
        // dotted-position tests below.
        Assert.Contains("Connections", result.Items)
    }

[<Fact>]
let ``dispatch on analysis/complete includes semantic completions at the cursor position`` () =
    task {
        let documentUri = uniqueDocumentUri "complete"
        let source = "let myLocalValue = 1\nmyLocal"
        // Line 2 is "myLocal", 7 characters — column 7 is the end-of-line
        // caret a completion is actually requested from, and the largest
        // in-range value for this line. (The earlier 8 was past the end.)
        let! response = Handlers.dispatch (completionRequest "cmp2" documentUri source 2 7)

        Assert.True(response.Error.IsNone)
        let result = JsonSerializer.Deserialize<AnalysisCompletionResult>(response.Result.Value, Serialization.options)
        Assert.Contains("myLocalValue", result.Items)
    }

[<Fact>]
let ``dispatch on analysis/complete returns the members of the qualifier at a dotted position, not the whole top-level scope`` () =
    task {
        // The caret sits at the end of "s.Sub", the shape Monaco actually
        // asks about while a member name is being typed. FSAC never
        // prefix-filters (the editor does), so the useful assertion is
        // that the list is String's *members* rather than everything in
        // top-level scope - the failure mode when the qualifier is lost.
        let documentUri = uniqueDocumentUri "complete"
        let source = "let s = \"abc\"\ns.Sub"
        let! response = Handlers.dispatch (completionRequest "cmp3" documentUri source 2 5)

        Assert.True(response.Error.IsNone)
        let result = JsonSerializer.Deserialize<AnalysisCompletionResult>(response.Result.Value, Serialization.options)

        Assert.Contains("Substring", result.Items)
        Assert.Contains("Length", result.Items)
        Assert.Contains("ToUpper", result.Items)

        // A top-level-scope dump would also carry names like these, and a
        // real member list must not.
        Assert.DoesNotContain("Choice1Of2", result.Items)
        Assert.DoesNotContain("printfn", result.Items)
    }

[<Fact>]
let ``dispatch on analysis/complete at a bare trailing dot returns the qualifier's members`` () =
    task {
        let documentUri = uniqueDocumentUri "complete"
        let source = "let s = \"abc\"\ns."
        let! response = Handlers.dispatch (completionRequest "cmp4" documentUri source 2 2)

        Assert.True(response.Error.IsNone)
        let result = JsonSerializer.Deserialize<AnalysisCompletionResult>(response.Result.Value, Serialization.options)

        Assert.Contains("Substring", result.Items)
        Assert.DoesNotContain("Choice1Of2", result.Items)
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

let private removeConnectionRequest (id: string) (connectionId: Guid) : WorkerRequestFrame =
    { Id = id
      Method = "connection/remove"
      Params =
        JsonSerializer.SerializeToElement<ConnectionRemoveParams>(
            ({ Id = connectionId }: ConnectionRemoveParams),
            Serialization.options
        ) }

[<Fact>]
let ``dispatch on connection/remove drops the connection from workspace.json`` () =
    task {
        let tempDir = Path.Combine(Path.GetTempPath(), "fqs-workspace-" + Guid.NewGuid().ToString("N"))
        Handlers.setWorkspaceDirForTests (Some tempDir)

        try
            let connection = testConnection "TestConn"
            let! _ = Handlers.dispatch (registerConnectionRequest "reg1" connection)

            let! response = Handlers.dispatch (removeConnectionRequest "rem1" connection.Id)

            Assert.Equal("rem1", response.Id)
            Assert.True(response.Error.IsNone)
            Assert.Empty(Workspace.readConnections tempDir)
        finally
            Handlers.setWorkspaceDirForTests None

            if Directory.Exists tempDir then
                Directory.Delete(tempDir, true)
    }

[<Fact>]
let ``dispatch on connection/remove preserves every other registered connection`` () =
    task {
        let tempDir = Path.Combine(Path.GetTempPath(), "fqs-workspace-" + Guid.NewGuid().ToString("N"))
        Handlers.setWorkspaceDirForTests (Some tempDir)

        try
            let keep = testConnection "Keep"
            let drop = testConnection "Drop"
            let! _ = Handlers.dispatch (registerConnectionRequest "reg1" keep)
            let! _ = Handlers.dispatch (registerConnectionRequest "reg2" drop)

            let! _ = Handlers.dispatch (removeConnectionRequest "rem1" drop.Id)

            let persisted = Workspace.readConnections tempDir
            Assert.Single(persisted) |> ignore
            Assert.Equal("Keep", persisted.[0].Name)
        finally
            Handlers.setWorkspaceDirForTests None

            if Directory.Exists tempDir then
                Directory.Delete(tempDir, true)
    }

let private schemaRequest (id: string) (connectionId: Guid) : WorkerRequestFrame =
    { Id = id
      Method = "connection/schema"
      Params =
        JsonSerializer.SerializeToElement<ConnectionSchemaParams>(
            ({ Id = connectionId }: ConnectionSchemaParams),
            Serialization.options
        ) }

[<Fact>]
let ``dispatch on connection/schema discovers real tables and columns for a registered SQLite connection`` () =
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
            connection.Close()
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()

            let registered =
                { Id = Guid.NewGuid()
                  Name = "Sales"
                  Kind = Sqlite
                  Configuration = JsonSerializer.SerializeToElement({| FilePath = dbPath |}, Serialization.options)
                  SecretReference = None
                  IsTrusted = false }

            let! _ = Handlers.dispatch (registerConnectionRequest "reg1" registered)
            let! response = Handlers.dispatch (schemaRequest "schema1" registered.Id)

            Assert.True(response.Error.IsNone)
            let result = JsonSerializer.Deserialize<ConnectionSchemaResult>(response.Result.Value, Serialization.options)
            Assert.Equal<string list>([ "Orders" ], result.Tables |> List.map (fun t -> t.Name))
            let columns = result.Tables.[0].Columns |> List.map (fun c -> c.Name)
            Assert.Equal<string list>([ "Id"; "Total" ], columns)
        finally
            Handlers.setWorkspaceDirForTests None
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()

            if Directory.Exists tempDir then
                Directory.Delete(tempDir, true)
    }

[<Fact>]
let ``dispatch on connection/schema returns a structured error for an unregistered Id`` () =
    task {
        let tempDir = Path.Combine(Path.GetTempPath(), "fqs-workspace-" + Guid.NewGuid().ToString("N"))
        Handlers.setWorkspaceDirForTests (Some tempDir)

        try
            let! response = Handlers.dispatch (schemaRequest "schema1" (Guid.NewGuid()))

            Assert.True(response.Result.IsNone)
            Assert.True(response.Error.IsSome)
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

[<Fact>]
let ``execution/evaluate shows Results.table's push even when the script's own last statement is a printfn`` () =
    task {
        let tempDir = Path.Combine(Path.GetTempPath(), "fqs-workspace-" + Guid.NewGuid().ToString("N"))
        let csvPath = Path.Combine(tempDir, "test1.csv")
        Handlers.setWorkspaceDirForTests (Some tempDir)

        try
            Directory.CreateDirectory tempDir |> ignore
            File.WriteAllText(csvPath, "id,name\n1,Ada\n2,Alan\n")

            let registerRequest =
                registerConnectionRequest
                    "reg1"
                    { Id = Guid.NewGuid()
                      Name = "test1"
                      Kind = Csv
                      Configuration =
                        JsonSerializer.SerializeToElement({| FilePath = csvPath |}, Serialization.options)
                      SecretReference = None
                      IsTrusted = false }

            let! _ = Handlers.dispatch registerRequest

            let script =
                "let result = Connections.csv \"test1\"\n"
                + "let rows = result.Rows |> Seq.toArray\n"
                + "Results.raw result |> ignore\n"
                + "printfn \"%A\" rows.[1]"

            let! evalResponse = Handlers.dispatch (evaluateRequest "eval1" script)

            Assert.True(evalResponse.Error.IsNone)
            let envelope = JsonSerializer.Deserialize<ResultEnvelope>(evalResponse.Result.Value, Serialization.options)
            Assert.Equal(ResultKind.Table, envelope.Kind)
            Assert.Equal<string list>([ "id"; "name" ], envelope.Schema |> List.map (fun c -> c.Name))
            Assert.Equal(2, envelope.Rows.Length)
        finally
            Handlers.setWorkspaceDirForTests None

            if Directory.Exists tempDir then
                Directory.Delete(tempDir, true)
    }

[<Fact>]
let ``execution/evaluate committing a Results.table push, then results/exportCsv, writes the full retained CSV`` () =
    task {
        let tempDir = Path.Combine(Path.GetTempPath(), "fqs-workspace-" + Guid.NewGuid().ToString("N"))
        let csvPath = Path.Combine(tempDir, "test1.csv")
        let exportPath = Path.Combine(tempDir, "exported.csv")
        Handlers.setWorkspaceDirForTests (Some tempDir)

        try
            Directory.CreateDirectory tempDir |> ignore
            File.WriteAllText(csvPath, "id,name\n1,Ada\n2,Alan\n")

            let registerRequest =
                registerConnectionRequest
                    "reg1"
                    { Id = Guid.NewGuid()
                      Name = "test1"
                      Kind = Csv
                      Configuration =
                        JsonSerializer.SerializeToElement({| FilePath = csvPath |}, Serialization.options)
                      SecretReference = None
                      IsTrusted = false }

            let! _ = Handlers.dispatch registerRequest

            let script = "Connections.csv \"test1\" |> Results.raw |> ignore"

            let! evalResponse = Handlers.dispatch (evaluateRequestFor "eval1" "untitled:Export1.fsx" script)
            Assert.True(evalResponse.Error.IsNone)

            let! exportResponse = Handlers.dispatch (exportCsvRequest "export1" "untitled:Export1.fsx" exportPath)
            Assert.True(exportResponse.Error.IsNone)

            let written = File.ReadAllText(exportPath)
            Assert.Equal("id,name\r\n1,Ada\r\n2,Alan\r\n", written)
        finally
            Handlers.setWorkspaceDirForTests None

            if Directory.Exists tempDir then
                Directory.Delete(tempDir, true)
    }

[<Fact>]
let ``a second execution/evaluate under the same DocumentUri overwrites the tab's retained result`` () =
    task {
        let tempDir = Path.Combine(Path.GetTempPath(), "fqs-workspace-" + Guid.NewGuid().ToString("N"))
        let exportPath = Path.Combine(tempDir, "exported.csv")
        Handlers.setWorkspaceDirForTests (Some tempDir)

        try
            Directory.CreateDirectory tempDir |> ignore

            let firstScript =
                "({ Columns = [ \"n\" ]; Rows = seq { yield [| box 1 |] } }: ConnectorQueryResult) |> Results.raw |> ignore"

            let secondScript =
                "({ Columns = [ \"n\" ]; Rows = seq { yield [| box 2 |]; yield [| box 3 |] } }: ConnectorQueryResult) |> Results.raw |> ignore"

            let! _ = Handlers.dispatch (evaluateRequestFor "eval1" "untitled:Overwrite1.fsx" firstScript)
            let! _ = Handlers.dispatch (evaluateRequestFor "eval2" "untitled:Overwrite1.fsx" secondScript)

            let! exportResponse = Handlers.dispatch (exportCsvRequest "export1" "untitled:Overwrite1.fsx" exportPath)
            Assert.True(exportResponse.Error.IsNone)

            let written = File.ReadAllText(exportPath)
            Assert.Equal("n\r\n2\r\n3\r\n", written)
        finally
            Handlers.setWorkspaceDirForTests None

            if Directory.Exists tempDir then
                Directory.Delete(tempDir, true)
    }

[<Fact>]
let ``an evaluate that doesn't push a table leaves the tab's existing retained result untouched`` () =
    task {
        let tempDir = Path.Combine(Path.GetTempPath(), "fqs-workspace-" + Guid.NewGuid().ToString("N"))
        let exportPath = Path.Combine(tempDir, "exported.csv")
        Handlers.setWorkspaceDirForTests (Some tempDir)

        try
            Directory.CreateDirectory tempDir |> ignore

            let tableScript =
                "({ Columns = [ \"n\" ]; Rows = seq { yield [| box 1 |] } }: ConnectorQueryResult) |> Results.raw |> ignore"

            let! _ = Handlers.dispatch (evaluateRequestFor "eval1" "untitled:Untouched1.fsx" tableScript)
            let! _ = Handlers.dispatch (evaluateRequestFor "eval2" "untitled:Untouched1.fsx" "printfn \"no table here\"")

            let! exportResponse = Handlers.dispatch (exportCsvRequest "export1" "untitled:Untouched1.fsx" exportPath)
            Assert.True(exportResponse.Error.IsNone)

            let written = File.ReadAllText(exportPath)
            Assert.Equal("n\r\n1\r\n", written)
        finally
            Handlers.setWorkspaceDirForTests None

            if Directory.Exists tempDir then
                Directory.Delete(tempDir, true)
    }

[<Fact>]
let ``results/exportCsv against a DocumentUri with nothing retained returns a nothingToExport error`` () =
    task {
        let! response = Handlers.dispatch (exportCsvRequest "export1" "untitled:NeverRun.fsx" "irrelevant.csv")

        Assert.True(response.Result.IsNone)

        match response.Error with
        | Some err -> Assert.Equal("results/nothingToExport", err.Code)
        | None -> failwith "expected a nothingToExport error"
    }

[<Fact>]
let ``results/discard removes a retained result, and a repeat call is a no-op`` () =
    task {
        let tempDir = Path.Combine(Path.GetTempPath(), "fqs-workspace-" + Guid.NewGuid().ToString("N"))
        Handlers.setWorkspaceDirForTests (Some tempDir)

        try
            Directory.CreateDirectory tempDir |> ignore

            let tableScript =
                "({ Columns = [ \"n\" ]; Rows = seq { yield [| box 1 |] } }: ConnectorQueryResult) |> Results.raw |> ignore"

            let! _ = Handlers.dispatch (evaluateRequestFor "eval1" "untitled:Discard1.fsx" tableScript)

            let! discardResponse = Handlers.dispatch (resultsDiscardRequest "discard1" "untitled:Discard1.fsx")
            Assert.True(discardResponse.Error.IsNone)

            let! exportResponse =
                Handlers.dispatch (exportCsvRequest "export1" "untitled:Discard1.fsx" (Path.Combine(tempDir, "x.csv")))

            match exportResponse.Error with
            | Some err -> Assert.Equal("results/nothingToExport", err.Code)
            | None -> failwith "expected a nothingToExport error after discard"

            // Repeating the discard must not throw.
            let! secondDiscardResponse = Handlers.dispatch (resultsDiscardRequest "discard2" "untitled:Discard1.fsx")
            Assert.True(secondDiscardResponse.Error.IsNone)
        finally
            Handlers.setWorkspaceDirForTests None

            if Directory.Exists tempDir then
                Directory.Delete(tempDir, true)
    }

[<Fact>]
let ``a later generic-projection Table run under the same DocumentUri discards the tab's earlier retained result`` () =
    task {
        let tempDir = Path.Combine(Path.GetTempPath(), "fqs-workspace-" + Guid.NewGuid().ToString("N"))
        let exportPath = Path.Combine(tempDir, "exported.csv")
        Handlers.setWorkspaceDirForTests (Some tempDir)

        try
            Directory.CreateDirectory tempDir |> ignore

            let tableScript =
                "({ Columns = [ \"n\" ]; Rows = seq { yield [| box 1 |] } }: ConnectorQueryResult) |> Results.raw |> ignore"

            // A generic FSI-tail-value script - no `Results.table` push -
            // whose last expression is still an `IEnumerable`, which
            // `Projection.projectWithCursor` turns into a `ResultKind.Table`
            // envelope without ever touching `ResultRetention`.
            let genericTableScript = "[ [| box 1 |] ]"

            let! _ = Handlers.dispatch (evaluateRequestFor "eval1" "untitled:StaleAfterGeneric1.fsx" tableScript)

            let! genericResponse =
                Handlers.dispatch (evaluateRequestFor "eval2" "untitled:StaleAfterGeneric1.fsx" genericTableScript)

            Assert.True(genericResponse.Error.IsNone)

            let genericEnvelope =
                JsonSerializer.Deserialize<ResultEnvelope>(genericResponse.Result.Value, Serialization.options)

            Assert.Equal(ResultKind.Table, genericEnvelope.Kind)

            let! exportResponse =
                Handlers.dispatch (exportCsvRequest "export1" "untitled:StaleAfterGeneric1.fsx" exportPath)

            match exportResponse.Error with
            | Some err -> Assert.Equal("results/nothingToExport", err.Code)
            | None -> failwith "expected a nothingToExport error, not the earlier retained data"
        finally
            Handlers.setWorkspaceDirForTests None

            if Directory.Exists tempDir then
                Directory.Delete(tempDir, true)
    }

[<Fact>]
let ``execution/evaluate shows NoValue, not a literal "null", when the script's last statement produces nothing`` () =
    task {
        let! evalResponse = Handlers.dispatch (evaluateRequest "eval1" "let x = 1\nprintfn \"%d\" x")

        Assert.True(evalResponse.Error.IsNone)
        let envelope = JsonSerializer.Deserialize<ResultEnvelope>(evalResponse.Result.Value, Serialization.options)
        Assert.Equal(ResultKind.NoValue, envelope.Kind)
        Assert.Equal(None, envelope.DisplayText)
    }

[<Fact>]
let ``dispatch on connection/list returns the connections persisted in workspace.json`` () =
    task {
        let tmp = Path.Combine(Path.GetTempPath(), "fqs-list-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory tmp |> ignore
        try
            Handlers.setWorkspaceDirForTests (Some tmp)
            let seeded: ConnectionDefinition =
                { Id = Guid.NewGuid()
                  Name = "Sales"
                  Kind = ConnectorKind.Sqlite
                  Configuration = JsonDocument.Parse("""{"filePath":"C:/data/sales.db"}""").RootElement
                  SecretReference = None
                  IsTrusted = true }
            Workspace.writeConnections tmp [ seeded ]

            let! response = Handlers.dispatch (emptyParamsRequest "l1" "connection/list")

            Assert.True response.Error.IsNone
            let payload =
                JsonSerializer.Deserialize<{| connections: ConnectionDefinition list |}>(
                    response.Result.Value, Serialization.options)
            Assert.Equal(1, payload.connections.Length)
            Assert.Equal("Sales", payload.connections.Head.Name)
        finally
            Handlers.setWorkspaceDirForTests None
            Directory.Delete(tmp, true)
    }

[<Fact>]
let ``dispatch on connection/list returns an empty list for a fresh workspace`` () =
    task {
        let tmp = Path.Combine(Path.GetTempPath(), "fqs-list-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory tmp |> ignore
        try
            Handlers.setWorkspaceDirForTests (Some tmp)
            let! response = Handlers.dispatch (emptyParamsRequest "l2" "connection/list")
            let payload =
                JsonSerializer.Deserialize<{| connections: ConnectionDefinition list |}>(
                    response.Result.Value, Serialization.options)
            Assert.Empty payload.connections
        finally
            Handlers.setWorkspaceDirForTests None
            Directory.Delete(tmp, true)
    }
