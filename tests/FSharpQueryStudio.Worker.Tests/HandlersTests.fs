module HandlersTests

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors
open FSharpQueryStudio.Execution
open FSharpQueryStudio.Storage
open FSharpQueryStudio.Worker
open TestHelpers

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
              DocumentUri = "untitled:Test.fsx"
              IsNotebookCell = false },
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
              DocumentUri = "untitled:Test.fsx"
              IsNotebookCell = false },
            Serialization.options
        ) }

/// A single notebook cell against a persistent session: the worker
/// appends `source` to `sessionId`'s session (`SessionStore.appendFragment`),
/// so a later cell sees an earlier cell's bindings even when its text is
/// not a prefix of what ran before.
let private notebookCellRequest (id: string) (source: string) (sessionId: string) : WorkerRequestFrame =
    { Id = id
      Method = "execution/evaluate"
      Params =
        JsonSerializer.SerializeToElement<EvaluateParams>(
            { Source = source
              SessionId = Some sessionId
              Mode = Persistent
              DocumentUri = sessionId
              IsNotebookCell = true },
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
              DocumentUri = documentUri
              IsNotebookCell = false },
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
        // Reserve the slot synchronously (mirrors Program.fs's own loop)
        // rather than backgrounding the whole handleEvaluate call and
        // hoping a Task.Delay outlasts the threadpool picking it up - a
        // freshly started process's threadpool can take a while to inject
        // worker threads under load, which made this test flaky before.
        match Handlers.beginEvaluate (evaluateRequest "e1" "1") with
        | Choice2Of2 _ -> failwith "expected to reserve the evaluation slot"
        | Choice1Of2 workDir ->
            try
                let sentinelPath =
                    Path.Combine(workDir, CancellationSignal.SentinelFileName).Replace("\\", "/")

                let longRunningScript =
                    $"let mutable i = 0 in (while i < 2_000_000_000 && not (System.IO.File.Exists(\"{sentinelPath}\")) do i <- i + 1); i"

                let firstTask =
                    task {
                        do! Task.Yield()
                        return Handlers.runEvaluate (evaluateRequest "e1" longRunningScript) workDir
                    }

                let! secondResponse = Handlers.dispatch (evaluateRequest "e2" "1") |> withTimeout 45.0

                Assert.True(secondResponse.Result.IsNone)

                match secondResponse.Error with
                | Some err -> Assert.Equal("execution/busy", err.Code)
                | None -> failwith "expected a busy error"

                let! _ = Handlers.dispatch (emptyParamsRequest "c2" "execution/cancel") |> withTimeout 45.0
                ()

                let! _ = firstTask |> withTimeout 45.0
                ()
            finally
                if Directory.Exists(workDir) then
                    try Directory.Delete(workDir, true) with _ -> ()
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
let ``evaluations in different documents or sessions maintain independent continuation tokens concurrently`` () =
    task {
        let docA = "untitled:DocA.fsx"
        let docB = "untitled:DocB.fsx"

        let respA = Handlers.handleEvaluate (evaluateRequestFor "evalA" docA "[1 .. 510]")
        let envA = JsonSerializer.Deserialize<ResultEnvelope>(respA.Result.Value, Serialization.options)
        let tokenA = envA.ContinuationToken |> Option.get

        let respB = Handlers.handleEvaluate (evaluateRequestFor "evalB" docB "[1001 .. 1510]")
        let envB = JsonSerializer.Deserialize<ResultEnvelope>(respB.Result.Value, Serialization.options)
        let tokenB = envB.ContinuationToken |> Option.get

        // Both tokens must remain valid and return their own distinct pages
        let! pageResponseA = Handlers.dispatch (resultsPageRequest "pageA" "sA" tokenA)
        let! pageResponseB = Handlers.dispatch (resultsPageRequest "pageB" "sB" tokenB)

        Assert.True(pageResponseA.Result.IsSome)
        Assert.True(pageResponseB.Result.IsSome)

        let pageA = JsonSerializer.Deserialize<ResultPage>(pageResponseA.Result.Value, Serialization.options)
        let pageB = JsonSerializer.Deserialize<ResultPage>(pageResponseB.Result.Value, Serialization.options)

        Assert.Equal(10, pageA.Rows.Length)
        Assert.Equal(10, pageB.Rows.Length)
        Assert.Equal("501", pageA.Rows.[0].[0].ToString())
        Assert.Equal("1501", pageB.Rows.[0].[0].ToString())
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

[<Fact>]
let ``a notebook cell sees an earlier cell's bindings even when its source is not a prefix of the earlier cell`` () =
    let sessionId = "nb-" + System.Guid.NewGuid().ToString("N")

    try
        // Cell 1: two let bindings, last line is a binding (no tail value).
        let cell1 =
            Handlers.handleEvaluate (
                notebookCellRequest "nb1" "let a = 20\nlet b = a + 1" sessionId
            )

        Assert.True(cell1.Error.IsNone)

        // Cell 2: a bare reference to cell 1's binding. This is NOT a text
        // prefix of cell 1, so `SessionStore.getOrCreate` would have
        // discarded the session and `b` would be undefined. `appendFragment`
        // keeps it.
        let cell2 = Handlers.handleEvaluate (notebookCellRequest "nb2" "b + 1" sessionId)

        Assert.True(cell2.Error.IsNone)

        let envelope =
            JsonSerializer.Deserialize<ResultEnvelope>(cell2.Result.Value, Serialization.options)

        Assert.Equal(ResultKind.Scalar, envelope.Kind)
        Assert.Equal(Some "22", envelope.DisplayText)
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
      Kind = ConnectorKind("Sqlite")
      Configuration = JsonDocument.Parse("""{"FilePath":"C:\\data.db"}""").RootElement
      SecretReference = None
      IsTrusted = false }

let private cleanTempDir (tempDir: string) =
    try
        Handlers.waitForPendingGeneration()
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
        if Directory.Exists tempDir then
            Directory.Delete(tempDir, true)
    with _ -> ()

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
            cleanTempDir tempDir
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
            cleanTempDir tempDir
    }

[<Fact>]
let ``dispatch on connection/register with workspaceDir persists to the specified local workspace directory instead of global`` () =
    task {
        let globalDir = Path.Combine(Path.GetTempPath(), "fqs-global-" + Guid.NewGuid().ToString("N"))
        let localDir = Path.Combine(Path.GetTempPath(), "fqs-local-" + Guid.NewGuid().ToString("N"))
        Handlers.setWorkspaceDirForTests (Some globalDir)

        try
            let conn = testConnection "LocalConn"
            let payload =
                JsonSerializer.SerializeToElement(
                    {| id = conn.Id
                       name = conn.Name
                       kind = conn.Kind
                       configuration = conn.Configuration
                       secretReference = conn.SecretReference
                       isTrusted = conn.IsTrusted
                       workspaceDir = localDir |},
                    Serialization.options
                )
            let req: WorkerRequestFrame =
                { Id = "local-reg"
                  Method = "connection/register"
                  Params = payload }

            let! response = Handlers.dispatch req
            Assert.True(response.Error.IsNone)

            let localPersisted = Workspace.readConnections localDir
            Assert.Contains(localPersisted, fun c -> c.Name = "LocalConn")

            let globalPersisted = Workspace.readConnections globalDir
            Assert.DoesNotContain(globalPersisted, fun c -> c.Name = "LocalConn")
        finally
            Handlers.setWorkspaceDirForTests None
            cleanTempDir globalDir
            cleanTempDir localDir
    }

[<Fact>]
let ``dispatch on workspace/initEmptyProject creates empty workspace.json and generated directory`` () =
    task {
        let localDir = Path.Combine(Path.GetTempPath(), "fqs-init-" + Guid.NewGuid().ToString("N"))

        try
            let req: WorkerRequestFrame =
                { Id = "init-proj"
                  Method = "workspace/initEmptyProject"
                  Params = JsonSerializer.SerializeToElement({| targetDir = localDir |}, Serialization.options) }

            let! response = Handlers.dispatch req
            Assert.True(response.Error.IsNone)

            let wsFile = Path.Combine(localDir, ".fsqs", "workspace.json")
            Assert.True(File.Exists wsFile)
            let config = Workspace.readConfiguration localDir
            Assert.Equal(2, config.SchemaVersion)
            Assert.Empty(config.Connections)

            let generatedDir = Path.Combine(localDir, ".fsqs", "generated")
            Assert.True(Directory.Exists generatedDir)
        finally
            cleanTempDir localDir
    }

[<Fact>]
let ``dispatch on workspace/upgrade upgrades legacy root files to .fsqs and Schema v2`` () =
    task {
        let localDir = Path.Combine(Path.GetTempPath(), "fqs-upgrade-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory localDir |> ignore
        try
            let connId = Guid.NewGuid()
            let rootWs = Path.Combine(localDir, "workspace.json")
            let sampleJson = sprintf """[{"id":"%s","name":"LegacyDb","kind":{"Case":"Sqlite"},"configuration":{"filePath":"data.db"},"secretReference":null,"isTrusted":true}]""" (connId.ToString())
            File.WriteAllText(rootWs, sampleJson)

            let rootGen = Path.Combine(localDir, "generated")
            Directory.CreateDirectory rootGen |> ignore
            File.WriteAllText(Path.Combine(rootGen, "GeneratedEntities.fs"), "// entities")

            let req: WorkerRequestFrame =
                { Id = "upgrade-1"
                  Method = "workspace/upgrade"
                  Params = JsonSerializer.SerializeToElement({| workspaceDir = localDir |}, Serialization.options) }

            let! response = Handlers.dispatch req
            Assert.True(response.Error.IsNone)
            let res = JsonSerializer.Deserialize<WorkspaceUpgradeResult>(response.Result.Value.GetRawText(), Serialization.options)
            Assert.True(res.Upgraded)
            Assert.Equal(1, res.PreviousVersion)
            Assert.Equal(2, res.CurrentVersion)
            Assert.False(File.Exists rootWs)
            Assert.False(Directory.Exists rootGen)
            Assert.True(File.Exists(Path.Combine(localDir, ".fsqs", "workspace.json")))
            Assert.True(File.Exists(Path.Combine(localDir, ".fsqs", "generated", "GeneratedEntities.fs")))
        finally
            cleanTempDir localDir
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
            cleanTempDir tempDir
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
            cleanTempDir tempDir
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
                  Kind = ConnectorKind("Sqlite")
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
            cleanTempDir tempDir
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
            cleanTempDir tempDir
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
                      Kind = ConnectorKind("Sqlite")
                      Configuration =
                        JsonSerializer.SerializeToElement({| FilePath = dbPath |}, Serialization.options)
                      SecretReference = None
                      IsTrusted = false }

            let! _ = Handlers.dispatch registerRequest

            let evaluateRequest = evaluateRequest "eval1" """Connections.sql.Sales.rawQuery("select Total from Orders") |> fun r -> r.Rows |> Seq.head |> Array.head"""
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
let ``execution/evaluate renders a record-list tail expression as a Table with real columns`` () =
    let script = "[ {| Id = 1; Name = \"Ada\" |}; {| Id = 2; Name = \"Grace\" |} ]"
    let response = Handlers.handleEvaluate (evaluateRequest "rec1" script)

    Assert.True(response.Error.IsNone)
    let env = JsonSerializer.Deserialize<ResultEnvelope>(response.Result.Value, Serialization.options)
    Assert.Equal(ResultKind.Table, env.Kind)
    Assert.Equal<string list>([ "Id"; "Name" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal(2, env.Rows.Length)

[<Fact>]
let ``execution/evaluate on a cell that ends in printfn is NoValue plus captured stdout`` () =
    let response = Handlers.handleEvaluate (evaluateRequest "nv1" "printfn \"just a side effect\"")

    Assert.True(response.Error.IsNone)
    let env = JsonSerializer.Deserialize<ResultEnvelope>(response.Result.Value, Serialization.options)
    Assert.Equal(ResultKind.NoValue, env.Kind)
    Assert.Contains(env.Output, (fun (l: OutputLine) -> l.Text.Contains("just a side effect")))

[<Fact>]
let ``execution/evaluate committing a connector-query tail expression, then results/exportCsv, writes the full retained CSV`` () =
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
                      Kind = ConnectorKind.Csv
                      Configuration =
                        JsonSerializer.SerializeToElement({| FilePath = csvPath |}, Serialization.options)
                      SecretReference = None
                      IsTrusted = false }

            let! _ = Handlers.dispatch registerRequest

            let script = "Connections.csv.test1.Raw()"

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
                "({ Columns = [ \"n\" ]; Rows = seq { yield [| box 1 |] } }: ConnectorQueryResult)"

            let secondScript =
                "({ Columns = [ \"n\" ]; Rows = seq { yield [| box 2 |]; yield [| box 3 |] } }: ConnectorQueryResult)"

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

            let tableScript = "[ {| n = 1 |} ]"

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
                "({ Columns = [ \"n\" ]; Rows = seq { yield [| box 1 |] } }: ConnectorQueryResult)"

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
    }

[<Fact>]
let ``dispatch on connection/list cleans leaked sample connections from global workspace store`` () =
    task {
        let tmp = Path.Combine(Path.GetTempPath(), "fqs-leaked-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory tmp |> ignore
        try
            Handlers.setWorkspaceDirForTests (Some tmp)
            let leakedSample: ConnectionDefinition =
                { Id = Guid.NewGuid()
                  Name = "SampleStore"
                  Kind = ConnectorKind.Sqlite
                  Configuration = JsonDocument.Parse("""{"filePath":"C:/nonexistent/sample_store.db"}""").RootElement
                  SecretReference = None
                  IsTrusted = true }
            let normalGlobal: ConnectionDefinition =
                { Id = Guid.NewGuid()
                  Name = "SharedProdDb"
                  Kind = ConnectorKind.SqlServer
                  Configuration = JsonDocument.Parse("""{"connectionString":"Server=prod;Database=app;"}""").RootElement
                  SecretReference = None
                  IsTrusted = true }
            Workspace.writeConnections tmp [ leakedSample; normalGlobal ]

            let! response = Handlers.dispatch (emptyParamsRequest "clean1" "connection/list")
            Assert.True response.Error.IsNone
            let payload =
                JsonSerializer.Deserialize<{| connections: ConnectionDefinition list |}>(
                    response.Result.Value, Serialization.options)
            Assert.Equal(1, payload.connections.Length)
            Assert.Equal("SharedProdDb", payload.connections.Head.Name)

            // Verify it was pruned from disk
            let onDisk = Workspace.readConnections tmp
            Assert.Equal(1, onDisk.Length)
            Assert.Equal("SharedProdDb", onDisk.Head.Name)
        finally
            Handlers.setWorkspaceDirForTests None
            if Directory.Exists tmp then Directory.Delete(tmp, true)
    }

[<Fact>]
let ``connection/schema reads {password} from the store configured via configureSecretStore`` () =
    task {
        let tmp = Path.Combine(Path.GetTempPath(), "fqs-seam-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory tmp |> ignore
        try
            Handlers.setWorkspaceDirForTests (Some tmp)

            // A recording spy: proves the resolver read `{password}` from *this*
            // injected store (not the module-default `InMemorySecretStore`) — an
            // environment-independent check, unlike asserting on Npgsql's
            // host-resolution error text, which varies by OS.
            let mutable askedFor: string option = None
            let spy =
                { new FSharpQueryStudio.Security.ISecretStore with
                    member _.TryRead reference =
                        askedFor <- Some reference
                        Some "s3cr3t"
                    member _.Save _ _ = ()
                    member _.Delete _ = () }
            Handlers.configureSecretStore spy

            let definition: ConnectionDefinition =
                { Id = Guid.NewGuid()
                  Name = "pg"
                  Kind = ConnectorKind.PostgreSql
                  Configuration =
                    JsonDocument.Parse("""{"connectionString":"Host={password}-sentinel;Port=5432"}""").RootElement
                  SecretReference = Some "fqs-conn-seam"
                  IsTrusted = false }
            Workspace.writeConnections tmp [ definition ]

            let req: WorkerRequestFrame =
                { Id = "sc1"
                  Method = "connection/schema"
                  Params = JsonSerializer.SerializeToElement(({ Id = definition.Id }: ConnectionSchemaParams), Serialization.options) }
            let! response = Handlers.dispatch req

            // The injected store was consulted with this connection's
            // SecretReference — the seam is wired at handleConnectionSchema.
            Assert.Equal(Some "fqs-conn-seam", askedFor)
            // And no server at "s3cr3t-sentinel" → a structured schemaFailed
            // error (not an unhandled exception down the read loop).
            Assert.Equal(Some "connection/schemaFailed", response.Error |> Option.map (fun e -> e.Code))
        finally
            Handlers.setWorkspaceDirForTests None
            Handlers.configureSecretStore (FSharpQueryStudio.Security.InMemorySecretStore())
            Directory.Delete(tmp, true)
    }

[<Fact>]
let ``workspace/createSampleData succeeds with targetDir, targetDirectory, or empty params`` () =
    task {
        let tmp = Path.Combine(Path.GetTempPath(), $"fqs-test-sample-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tmp) |> ignore
        try
            Handlers.setWorkspaceDirForTests (Some tmp)

            // Test 1: with targetDir (what VS Code extension sends)
            let escapedTmp = tmp.Replace("\\", "\\\\")
            let req1: WorkerRequestFrame =
                { Id = "s1"
                  Method = "workspace/createSampleData"
                  Params = JsonDocument.Parse($"""{{"targetDir": "{escapedTmp}"}}""").RootElement }
            let! res1 = Handlers.dispatch req1
            Assert.True(res1.Error.IsNone, (match res1.Error with Some e -> e.Message | None -> ""))
            Assert.True(res1.Result.IsSome)

            let r1 = res1.Result.Value
            let sqlite1 = r1.GetProperty("sqlitePath").GetString()
            let sqlite2 = r1.GetProperty("sqlite2Path").GetString()
            let csv1 = r1.GetProperty("csvPath").GetString()
            let csv2 = r1.GetProperty("csv2Path").GetString()
            let excel1 = r1.GetProperty("excelPath").GetString()
            let restUrl = r1.GetProperty("restUrl").GetString()
            let nb1 = r1.GetProperty("notebookPath").GetString()
            let nb2 = r1.GetProperty("notebookPtPath").GetString()
            Assert.True(File.Exists sqlite1, $"sqlitePath should exist: {sqlite1}")
            Assert.True(File.Exists sqlite2, $"sqlite2Path should exist: {sqlite2}")
            Assert.True(File.Exists csv1, $"csvPath should exist: {csv1}")
            Assert.True(File.Exists csv2, $"csv2Path should exist: {csv2}")
            Assert.True(File.Exists excel1, $"excelPath should exist: {excel1}")
            Assert.StartsWith("https://", restUrl)
            Assert.Contains("github.com", restUrl)
            Assert.True(File.Exists nb1, $"notebookPath should exist: {nb1}")
            Assert.True(File.Exists nb2, $"notebookPtPath should exist: {nb2}")

            let regConns =
                r1.GetProperty("registeredConnections").EnumerateArray()
                |> Seq.map (fun el -> el.GetString())
                |> Seq.toList
            Assert.Equal<string list>(
                [ "SampleStore"; "InventoryStore"; "SalesFeed"; "CustomersFeed"; "RegionalTargets"; "GitHubFSharp" ],
                regConns
            )

            // Test 2: with targetDirectory
            let req2: WorkerRequestFrame =
                { Id = "s2"
                  Method = "workspace/createSampleData"
                  Params = JsonDocument.Parse($"""{{"targetDirectory": "{escapedTmp}"}}""").RootElement }
            let! res2 = Handlers.dispatch req2
            Assert.True(res2.Error.IsNone, (match res2.Error with Some e -> e.Message | None -> ""))

            // Test 3: with empty params {}
            let req3: WorkerRequestFrame =
                { Id = "s3"
                  Method = "workspace/createSampleData"
                  Params = JsonDocument.Parse("{}").RootElement }
            let! res3 = Handlers.dispatch req3
            Assert.True(res3.Error.IsNone, (match res3.Error with Some e -> e.Message | None -> ""))
        finally
            Handlers.setWorkspaceDirForTests None
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
            try
                if Directory.Exists tmp then Directory.Delete(tmp, true)
            with _ -> ()
    }

[<Fact>]
let ``evaluating sample_demo notebook cells succeeds against generated samples`` () =
    task {
        let tmp = Path.Combine(Path.GetTempPath(), $"fqs-test-eval-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tmp) |> ignore
        try
            Handlers.setWorkspaceDirForTests (Some tmp)
            let escapedTmp = tmp.Replace("\\", "\\\\")
            let req1: WorkerRequestFrame =
                { Id = "s1"
                  Method = "workspace/createSampleData"
                  Params = JsonDocument.Parse($"""{{"targetDir": "{escapedTmp}"}}""").RootElement }
            let! res1 = Handlers.dispatch req1
            Assert.True(res1.Error.IsNone)

            let notebookPath = Path.Combine(tmp, "sample_demo.fsqnb")
            Assert.True(File.Exists notebookPath)
            let doc = JsonDocument.Parse(File.ReadAllText notebookPath)
            let codeCells =
                doc.RootElement.GetProperty("cells").EnumerateArray()
                |> Seq.filter (fun c -> c.GetProperty("kind").GetString() = "code")
                |> Seq.map (fun c -> c.GetProperty("value").GetString())
                |> Seq.toList

            Assert.Equal(12, codeCells.Length)

            for idx, code in List.indexed codeCells do
                let evalReq = notebookCellRequest $"cell-{idx + 1}" code "sample_demo.fsqnb"
                let! evalRes = Handlers.dispatch evalReq
                let errMsg = match evalRes.Error with Some e -> e.Message | None -> ""
                Assert.True(evalRes.Error.IsNone, $"Cell {idx + 1} error: {errMsg}")
                let env = JsonSerializer.Deserialize<ResultEnvelope>(evalRes.Result.Value.GetRawText(), Serialization.options)
                let outputText = env.Output |> List.map (fun o -> o.Text) |> String.concat "\n"
                Assert.True(
                    (env.Kind = ResultKind.Table || env.Kind = ResultKind.Scalar),
                    $"Cell {idx + 1} expected Table or Scalar outcome but got {env.Kind}. DisplayText: {env.DisplayText}. Output: {outputText}. Code:\n{code}"
                )
                if env.Kind = ResultKind.Table then
                    Assert.NotEmpty(env.Rows)

            let notebookPtPath = Path.Combine(tmp, "sample_demo_pt.fsqnb")
            Assert.True(File.Exists notebookPtPath)
            let docPt = JsonDocument.Parse(File.ReadAllText notebookPtPath)
            let codeCellsPt =
                docPt.RootElement.GetProperty("cells").EnumerateArray()
                |> Seq.filter (fun c -> c.GetProperty("kind").GetString() = "code")
                |> Seq.map (fun c -> c.GetProperty("value").GetString())
                |> Seq.toList

            Assert.Equal(12, codeCellsPt.Length)

            for idx, code in List.indexed codeCellsPt do
                let evalReq = notebookCellRequest $"cell-pt-{idx + 1}" code "sample_demo_pt.fsqnb"
                let! evalRes = Handlers.dispatch evalReq
                let errMsg = match evalRes.Error with Some e -> e.Message | None -> ""
                Assert.True(evalRes.Error.IsNone, $"PT Cell {idx + 1} error: {errMsg}")
                let env = JsonSerializer.Deserialize<ResultEnvelope>(evalRes.Result.Value.GetRawText(), Serialization.options)
                let outputText = env.Output |> List.map (fun o -> o.Text) |> String.concat "\n"
                Assert.True(
                    (env.Kind = ResultKind.Table || env.Kind = ResultKind.Scalar),
                    $"PT Cell {idx + 1} expected Table or Scalar outcome but got {env.Kind}. DisplayText: {env.DisplayText}. Output: {outputText}. Code:\n{code}"
                )
                if env.Kind = ResultKind.Table then
                    Assert.NotEmpty(env.Rows)
        finally
            Handlers.setWorkspaceDirForTests None
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
            try
                if Directory.Exists tmp then Directory.Delete(tmp, true)
            with _ -> ()
    }

[<Fact>]
let ``re-creating sample data over a workspace with a stale empty SampleStore db and existing session heals and evaluates Customers`` () =
    task {
        let tmp = Path.Combine(Path.GetTempPath(), $"fqs-test-heal-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tmp) |> ignore
        try
            Handlers.setWorkspaceDirForTests (Some tmp)

            // 1. Simulate a broken/empty SampleStore connection and stale GeneratedEntities.fs
            let brokenDbPath = Path.Combine(tmp, "broken_empty.db")
            File.WriteAllBytes(brokenDbPath, [||])
            let staleDef: ConnectionDefinition =
                { Id = Guid.NewGuid()
                  Name = "SampleStore"
                  Kind = ConnectorKind.Sqlite
                  Configuration = JsonSerializer.SerializeToElement({| filePath = brokenDbPath |}, Serialization.options)
                  SecretReference = None
                  IsTrusted = true }
            let _ = Workspace.registerConnection tmp staleDef
            let! _ = EntityBackgroundGenerator.generateForConnectionsAsync tmp [ staleDef ] (FSharpQueryStudio.Security.InMemorySecretStore())

            // Start a persistent session while SampleStore is still broken
            let docUri = Path.Combine(tmp, "sample_demo_pt.fsqnb")
            let warmupReq: WorkerRequestFrame =
                { Id = "warmup-1"
                  Method = "execution/evaluate"
                  Params =
                    JsonSerializer.SerializeToElement(
                        { Source = "1 + 1"
                          SessionId = Some docUri
                          Mode = Persistent
                          DocumentUri = docUri
                          IsNotebookCell = true },
                        Serialization.options
                    ) }
            let! _ = Handlers.dispatch warmupReq

            // 2. Now run workspace/createSampleData in the same workspace
            let escapedTmp = tmp.Replace("\\", "\\\\")
            let createReq: WorkerRequestFrame =
                { Id = "heal-create"
                  Method = "workspace/createSampleData"
                  Params = JsonDocument.Parse($"""{{"targetDir": "{escapedTmp}"}}""").RootElement }
            let! createRes = Handlers.dispatch createReq
            Assert.True(createRes.Error.IsNone)

            // Verify no duplicate SampleStore exists in workspace.json
            let currentConns = Workspace.readConnections tmp
            let sampleStoreConns = currentConns |> List.filter (fun c -> c.Name = "SampleStore")
            Assert.Single(sampleStoreConns) |> ignore

            // 3. Evaluate Cell 1 of sample_demo_pt.fsqnb on the same sessionId
            let cell1Code = """
query {
    for c in Connections.sql.SampleStore.Customers do
    where (c.Country = "Brazil")
    sortBy c.FirstName
    select {|
        Id = c.Id
        NomeCliente = c.FirstName + " " + c.LastName
        Email = c.Email
        Cidade = c.City
        Pais = c.Country
    |}
}"""
            let evalReq: WorkerRequestFrame =
                { Id = "heal-eval-1"
                  Method = "execution/evaluate"
                  Params =
                    JsonSerializer.SerializeToElement(
                        { Source = cell1Code
                          SessionId = Some docUri
                          Mode = Persistent
                          DocumentUri = docUri
                          IsNotebookCell = true },
                        Serialization.options
                    ) }
            let! evalRes = Handlers.dispatch evalReq
            Assert.True(evalRes.Error.IsNone)
            let env = JsonSerializer.Deserialize<ResultEnvelope>(evalRes.Result.Value.GetRawText(), Serialization.options)
            Assert.Equal(ResultKind.Table, env.Kind)
            Assert.Equal(2, env.Rows.Length)
        finally
            Handlers.setWorkspaceDirForTests None
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
            try
                if Directory.Exists tmp then Directory.Delete(tmp, true)
            with _ -> ()
    }

[<Fact>]
let ``connector/list returns all registered connector descriptors with form schemas`` () =
    task {
        let req = emptyParamsRequest "conn-list-1" "connector/list"
        let! res = Handlers.dispatch req
        Assert.Null(res.Error |> Option.toObj)
        Assert.True(res.Result.IsSome)

        let listResult =
            JsonSerializer.Deserialize<ConnectorListResult>(res.Result.Value.GetRawText(), Serialization.options)

        Assert.True(listResult.Connectors.Length >= 6)
        let kindIds = listResult.Connectors |> List.map (fun c -> c.KindId) |> Set.ofList
        Assert.True(Set.contains "Sqlite" kindIds)
        Assert.True(Set.contains "SqlServer" kindIds)
        Assert.True(Set.contains "PostgreSql" kindIds)
        Assert.True(Set.contains "Csv" kindIds)
        Assert.True(Set.contains "Excel" kindIds)
        Assert.True(Set.contains "RestJson" kindIds)

        for desc in listResult.Connectors do
            Assert.False(String.IsNullOrWhiteSpace desc.DisplayName)
            Assert.False(String.IsNullOrWhiteSpace desc.Category)
            Assert.False(String.IsNullOrWhiteSpace desc.Icon)
            Assert.NotEmpty(desc.FormFields)
            for field in desc.FormFields do
                Assert.False(String.IsNullOrWhiteSpace field.Name)
                Assert.False(String.IsNullOrWhiteSpace field.Label)
                Assert.False(String.IsNullOrWhiteSpace field.Type)
    }

[<Fact>]
let ``evaluating table rows with UTF-8 accented characters over UTF-8 stdio stream preserves characters without mojibake`` () =
    task {
        let utf8NoBom = System.Text.UTF8Encoding(false)
        let code =
            """[
    {| Fonte = "SalesFeed"; Tipo = "CSV (Arquivo Plano)"; TotalRegistros = 10; EntidadePrincipal = "Transações" |}
    {| Fonte = "CustomersFeed"; Tipo = "CSV (Arquivo Plano)"; TotalRegistros = 6; EntidadePrincipal = "Perfis de Fidelidade" |}
]"""
        let reqFrame = evaluateRequest "utf8-eval-1" code
        let reqJson = JsonSerializer.Serialize(reqFrame, Serialization.options) + "\n"
        let inputBytes = utf8NoBom.GetBytes(reqJson)

        use inputStream = new MemoryStream(inputBytes)
        use outputStream = new MemoryStream()

        use reader = new StreamReader(inputStream, utf8NoBom, detectEncodingFromByteOrderMarks = false, bufferSize = 4096, leaveOpen = true)
        use writer = new StreamWriter(outputStream, utf8NoBom, bufferSize = 4096, leaveOpen = true, AutoFlush = true)

        let! readReqOpt = NdjsonStream.readFrame<WorkerRequestFrame> reader
        Assert.True(readReqOpt.IsSome)
        let! dispatchResp = Handlers.dispatch readReqOpt.Value
        do! NdjsonStream.writeFrame writer dispatchResp

        let outputJson = utf8NoBom.GetString(outputStream.ToArray()).Trim()
        let respFrame = JsonSerializer.Deserialize<WorkerResponseFrame>(outputJson, Serialization.options)
        Assert.True(respFrame.Error.IsNone)
        Assert.True(respFrame.Result.IsSome)

        let envelope = JsonSerializer.Deserialize<ResultEnvelope>(respFrame.Result.Value.GetRawText(), Serialization.options)
        Assert.Equal(ResultKind.Table, envelope.Kind)
        Assert.Equal(2, envelope.Rows.Length)

        let entColIdx = envelope.Schema |> List.findIndex (fun c -> c.Name = "EntidadePrincipal")
        Assert.Equal("Transações", envelope.Rows.[0].[entColIdx].ToString())
        Assert.Equal("Perfis de Fidelidade", envelope.Rows.[1].[entColIdx].ToString())
    }

[<Fact>]
let ``evaluating a CSV with >100,000 rows and messy rows past row 200 projects to ResultKind.Table and never falls back to val IQueryable Text`` () =
    task {
        let tmp = Path.Combine(Path.GetTempPath(), $"fqs-test-large-csv-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tmp) |> ignore
        try
            Handlers.setWorkspaceDirForTests (Some tmp)
            let csvPath = Path.Combine(tmp, "large_sales_100k.csv")
            do
                use fs = new FileStream(csvPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536)
                use writer = new StreamWriter(fs, System.Text.Encoding.UTF8, 65536)
                writer.WriteLine("Id,Customer,Quantity,Amount,Active,OrderDate")
                for i = 1 to 105_000 do
                    if i <= 200 then
                        // Clean first 200 rows so type inference infers non-option int, decimal, bool, DateTime
                        writer.WriteLine($"{i},Customer_{i},3,199.90,true,2026-03-01")
                    elif i % 1000 = 0 then
                        // Empty values after row 200 in non-option columns
                        writer.WriteLine($"{i},Customer_{i},,,,")
                    elif i % 1000 = 1 then
                        // Sentinels N/A, -, null, #N/A after row 200
                        writer.WriteLine($"{i},Customer_{i},N/A,-,null,#N/A")
                    elif i % 1000 = 2 then
                        // Float in int column and comma decimal
                        writer.WriteLine($"{i},Customer_{i},4.7,\"350,75\",1,2026-03-15")
                    else
                        writer.WriteLine($"{i},Customer_{i},2,120.00,false,2026-03-10")
                writer.WriteLine()

            let connDef: ConnectionDefinition =
                { Id = Guid.NewGuid()
                  Name = "LargeSales"
                  Kind = ConnectorKind.Csv
                  Configuration = JsonSerializer.SerializeToElement({| filePath = csvPath |}, Serialization.options)
                  SecretReference = None
                  IsTrusted = true }

            let! regResp = Handlers.dispatch (registerConnectionRequest "reg-100k" connDef)
            Assert.True(regResp.Error.IsNone)
            Handlers.waitForPendingGeneration ()

            let docUri = Path.Combine(tmp, "large_test.fsqnb")

            // 1. Evaluate direct table reference `Connections.csv.LargeSales`
            let evalDirect = notebookCellRequest "eval-100k-direct" "Connections.csv.LargeSales" docUri
            let! respDirect = Handlers.dispatch evalDirect
            Assert.True(respDirect.Error.IsNone, (match respDirect.Error with Some e -> e.Message | None -> ""))
            let envDirect = JsonSerializer.Deserialize<ResultEnvelope>(respDirect.Result.Value.GetRawText(), Serialization.options)
            Assert.Equal(ResultKind.Table, envDirect.Kind)
            Assert.Equal(500, envDirect.Rows.Length)
            Assert.True(envDirect.IsTruncated)
            Assert.Equal<string list>(
                [ "Id"; "Customer"; "Quantity"; "Amount"; "Active"; "OrderDate" ],
                envDirect.Schema |> List.map (fun c -> c.Name)
            )

            // 2. Evaluate LINQ IQueryable query over >100,000 rows (matches 200 initial rows + 104 rows where i % 1000 = 2 = 304 rows)
            let linqCode = """
query {
    for r in Connections.csv.LargeSales do
    where (r.Amount >= 150.0M)
    select {|
        Id = r.Id
        Customer = r.Customer
        Quantity = r.Quantity
        Amount = r.Amount
    |}
}"""
            let evalLinq = notebookCellRequest "eval-100k-linq" linqCode docUri
            let! respLinq = Handlers.dispatch evalLinq
            Assert.True(respLinq.Error.IsNone, (match respLinq.Error with Some e -> e.Message | None -> ""))
            let envLinq = JsonSerializer.Deserialize<ResultEnvelope>(respLinq.Result.Value.GetRawText(), Serialization.options)
            Assert.Equal(ResultKind.Table, envLinq.Kind)
            Assert.Equal(304, envLinq.Rows.Length)
            Assert.False(envLinq.IsTruncated)
            Assert.Equal<string list>([ "Amount"; "Customer"; "Id"; "Quantity" ], envLinq.Schema |> List.map (fun c -> c.Name) |> List.sort)
        finally
            Handlers.setWorkspaceDirForTests None
            cleanTempDir tmp
    }

[<Fact>]
let ``evaluating an Excel workbook with >1,000 rows, a Data sheet name, and messy cells past row 200 projects to ResultKind.Table`` () =
    task {
        let tmp = Path.Combine(Path.GetTempPath(), $"fqs-test-large-xlsx-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tmp) |> ignore
        try
            Handlers.setWorkspaceDirForTests (Some tmp)
            let xlsxPath = Path.Combine(tmp, "large_workbook.xlsx")
            do
                use wb = new ClosedXML.Excel.XLWorkbook()
                let ws = wb.Worksheets.Add("Data")
                ws.Cell(1, 1).Value <- "Id"
                ws.Cell(1, 2).Value <- "Region"
                ws.Cell(1, 3).Value <- "Units"
                ws.Cell(1, 4).Value <- "Revenue"
                for i = 1 to 1200 do
                    let rowIdx = i + 1
                    ws.Cell(rowIdx, 1).Value <- i
                    ws.Cell(rowIdx, 2).Value <- $"Region_{i % 5}"
                    if i <= 200 then
                        ws.Cell(rowIdx, 3).Value <- 10
                        ws.Cell(rowIdx, 4).Value <- 250.50
                    elif i = 250 then
                        // Blank cells after row 200
                        ()
                    elif i = 300 then
                        // Text sentinel and float in integer column after row 200
                        ws.Cell(rowIdx, 3).Value <- "N/A"
                        ws.Cell(rowIdx, 4).Value <- "invalid"
                    else
                        ws.Cell(rowIdx, 3).Value <- 20
                        ws.Cell(rowIdx, 4).Value <- 500.75
                wb.SaveAs(xlsxPath)

            let connDef: ConnectionDefinition =
                { Id = Guid.NewGuid()
                  Name = "LargeExcel"
                  Kind = ConnectorKind.Excel
                  Configuration = JsonSerializer.SerializeToElement({| filePath = xlsxPath |}, Serialization.options)
                  SecretReference = None
                  IsTrusted = true }

            let! regResp = Handlers.dispatch (registerConnectionRequest "reg-xlsx" connDef)
            Assert.True(regResp.Error.IsNone)
            Handlers.waitForPendingGeneration ()

            let docUri = Path.Combine(tmp, "large_excel_test.fsqnb")
            let linqCode = """
query {
    for r in Connections.excel.LargeExcel.Data do
    where (r.Id > 220)
    select r
}"""
            let evalLinq = notebookCellRequest "eval-xlsx-linq" linqCode docUri
            let! respLinq = Handlers.dispatch evalLinq
            Assert.True(respLinq.Error.IsNone, (match respLinq.Error with Some e -> e.Message | None -> ""))
            let envLinq = JsonSerializer.Deserialize<ResultEnvelope>(respLinq.Result.Value.GetRawText(), Serialization.options)
            Assert.Equal(ResultKind.Table, envLinq.Kind)
            Assert.Equal(500, envLinq.Rows.Length)
            Assert.True(envLinq.IsTruncated)
        finally
            Handlers.setWorkspaceDirForTests None
            cleanTempDir tmp
    }




[<Fact>]
let ``evaluating a SQLite table with >100,000 rows and messy values past row 200 projects to ResultKind.Table`` () =
    task {
        let tmp = Path.Combine(Path.GetTempPath(), $"fqs-test-large-sqlite-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tmp) |> ignore
        try
            Handlers.setWorkspaceDirForTests (Some tmp)
            let dbPath = Path.Combine(tmp, "big_store.db")
            do
                use conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}")
                conn.Open()
                use create = conn.CreateCommand()
                create.CommandText <-
                    "create table Orders (Id INTEGER, Customer TEXT, Quantity INTEGER, Amount NUMERIC, Active BOOLEAN, OrderDate DATETIME)"
                create.ExecuteNonQuery() |> ignore
                use tx = conn.BeginTransaction()
                use insert = conn.CreateCommand()
                insert.Transaction <- tx
                insert.CommandText <- "insert into Orders values ($id, $c, $q, $a, $act, $d)"
                let ps =
                    [| "$id"; "$c"; "$q"; "$a"; "$act"; "$d" |]
                    |> Array.map (fun n -> insert.Parameters.Add(n, Microsoft.Data.Sqlite.SqliteType.Text))
                let setRow (values: obj array) =
                    for k = 0 to values.Length - 1 do
                        ps.[k].Value <- (if isNull values.[k] then box DBNull.Value else values.[k])
                for i = 1 to 105_000 do
                    if i <= 200 then setRow [| box i; box $"C_{i}"; box 3; box 199.90; box 1; box "2026-03-01" |]
                    elif i % 1000 = 0 then setRow [| box i; box $"C_{i}"; null; null; null; null |]
                    elif i % 1000 = 1 then setRow [| box i; box $"C_{i}"; box "N/A"; box "-"; box "null"; box "#N/A" |]
                    elif i % 1000 = 2 then setRow [| box i; box $"C_{i}"; box 4.7; box "350,75"; box "yes"; box "invalid" |]
                    else setRow [| box i; box $"C_{i}"; box 2; box 120.00; box 0; box "2026-03-10" |]
                    insert.ExecuteNonQuery() |> ignore
                tx.Commit()
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()

            let connDef: ConnectionDefinition =
                { Id = Guid.NewGuid()
                  Name = "BigStore"
                  Kind = ConnectorKind.Sqlite
                  Configuration = JsonSerializer.SerializeToElement({| filePath = dbPath |}, Serialization.options)
                  SecretReference = None
                  IsTrusted = true }

            let! regResp = Handlers.dispatch (registerConnectionRequest "reg-big-sqlite" connDef)
            Assert.True(regResp.Error.IsNone)
            Handlers.waitForPendingGeneration ()

            let docUri = Path.Combine(tmp, "big_sqlite_test.fsqnb")

            // 1. Direct table reference over all 105,000 rows
            let! respDirect = Handlers.dispatch (notebookCellRequest "eval-big-sqlite-direct" "Connections.sql.BigStore.Orders" docUri)
            Assert.True(respDirect.Error.IsNone, (match respDirect.Error with Some e -> e.Message | None -> ""))
            let envDirect = JsonSerializer.Deserialize<ResultEnvelope>(respDirect.Result.Value.GetRawText(), Serialization.options)
            Assert.True((envDirect.Kind = ResultKind.Table), $"Expected Table but got {envDirect.Kind}: {envDirect.DisplayText}")
            Assert.Equal(500, envDirect.Rows.Length)
            Assert.True(envDirect.IsTruncated)

            // 2. LINQ query (pushdown) projecting an anonymous record
            let linqCode = """
query {
    for o in Connections.sql.BigStore.Orders do
    where (o.Id > 104_000)
    select {| Id = o.Id; Quantity = o.Quantity; Amount = o.Amount; Active = o.Active |}
}"""
            let! respLinq = Handlers.dispatch (notebookCellRequest "eval-big-sqlite-linq" linqCode docUri)
            Assert.True(respLinq.Error.IsNone, (match respLinq.Error with Some e -> e.Message | None -> ""))
            let envLinq = JsonSerializer.Deserialize<ResultEnvelope>(respLinq.Result.Value.GetRawText(), Serialization.options)
            Assert.True((envLinq.Kind = ResultKind.Table), $"Expected Table but got {envLinq.Kind}: {envLinq.DisplayText}")
            Assert.Equal(500, envLinq.Rows.Length)
            Assert.True(envLinq.IsTruncated)
        finally
            Handlers.setWorkspaceDirForTests None
            cleanTempDir tmp
    }

[<Fact>]
let ``connections registered with relative file paths resolve against the workspace root and evaluate to tables`` () =
    task {
        let tmp = Path.Combine(Path.GetTempPath(), $"fqs-test-relative-{Guid.NewGuid():N}")
        Directory.CreateDirectory(Path.Combine(tmp, "data")) |> ignore
        try
            Handlers.setWorkspaceDirForTests (Some tmp)
            File.WriteAllText(Path.Combine(tmp, "data", "people.csv"), "Id,Name\n1,Alice\n2,Bob\n3,Carol\n")
            do
                use conn = new Microsoft.Data.Sqlite.SqliteConnection($"""Data Source={Path.Combine(tmp, "data", "items.db")}""")
                conn.Open()
                use cmd = conn.CreateCommand()
                cmd.CommandText <- "create table Items (Id INTEGER, Label TEXT); insert into Items values (1, 'a'), (2, 'b');"
                cmd.ExecuteNonQuery() |> ignore
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()

            let csvDef: ConnectionDefinition =
                { Id = Guid.NewGuid()
                  Name = "RelPeople"
                  Kind = ConnectorKind.Csv
                  Configuration = JsonSerializer.SerializeToElement({| filePath = "data/people.csv" |}, Serialization.options)
                  SecretReference = None
                  IsTrusted = true }
            let dbDef: ConnectionDefinition =
                { Id = Guid.NewGuid()
                  Name = "RelItems"
                  Kind = ConnectorKind.Sqlite
                  Configuration = JsonSerializer.SerializeToElement({| filePath = "./data/items.db" |}, Serialization.options)
                  SecretReference = None
                  IsTrusted = true }

            let! r1 = Handlers.dispatch (registerConnectionRequest "reg-rel-csv" csvDef)
            Assert.True(r1.Error.IsNone)
            let! r2 = Handlers.dispatch (registerConnectionRequest "reg-rel-db" dbDef)
            Assert.True(r2.Error.IsNone)
            Handlers.waitForPendingGeneration ()

            // The relative path is persisted as-is (portable workspace.json)
            let persisted = Workspace.readConnections tmp |> List.find (fun c -> c.Name = "RelPeople")
            Assert.Contains("data/people.csv", persisted.Configuration.GetRawText())

            let docUri = Path.Combine(tmp, "relative_test.fsqnb")

            let! respCsv = Handlers.dispatch (notebookCellRequest "eval-rel-csv" "Connections.csv.RelPeople" docUri)
            Assert.True(respCsv.Error.IsNone, (match respCsv.Error with Some e -> e.Message | None -> ""))
            let envCsv = JsonSerializer.Deserialize<ResultEnvelope>(respCsv.Result.Value.GetRawText(), Serialization.options)
            Assert.True((envCsv.Kind = ResultKind.Table), $"Expected Table but got {envCsv.Kind}: {envCsv.DisplayText}")
            Assert.Equal(3, envCsv.Rows.Length)

            let! respDb = Handlers.dispatch (notebookCellRequest "eval-rel-db" "Connections.sql.RelItems.Items" docUri)
            Assert.True(respDb.Error.IsNone, (match respDb.Error with Some e -> e.Message | None -> ""))
            let envDb = JsonSerializer.Deserialize<ResultEnvelope>(respDb.Result.Value.GetRawText(), Serialization.options)
            Assert.True((envDb.Kind = ResultKind.Table), $"Expected Table but got {envDb.Kind}: {envDb.DisplayText}")
            Assert.Equal(2, envDb.Rows.Length)

            // No stray database was created relative to the worker's transient working directory
            Assert.False(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "data", "items.db")))
        finally
            Handlers.setWorkspaceDirForTests None
            cleanTempDir tmp
    }

[<Fact>]
let ``evaluating a cell with ResultKind.toRawSql renders the generated query as text before database execution`` () =
    task {
        let tmp = Path.Combine(Path.GetTempPath(), "fqs-rawsql-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tmp) |> ignore
        Handlers.setWorkspaceDirForTests (Some tmp)

        try
            let dbPath = Path.Combine(tmp, "store.db")
            do
                use c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Pooling=false")
                c.Open()
                use cmd = c.CreateCommand()
                cmd.CommandText <- "CREATE TABLE Items (Id INTEGER PRIMARY KEY, Name TEXT, Price NUMERIC); INSERT INTO Items VALUES (1, 'Widget', 19.99);"
                cmd.ExecuteNonQuery() |> ignore
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()

            let dbDef: ConnectionDefinition =
                { Id = Guid.NewGuid()
                  Name = "RawSqlDb"
                  Kind = ConnectorKind.Sqlite
                  Configuration = JsonSerializer.SerializeToElement({| filePath = dbPath |}, Serialization.options)
                  SecretReference = None
                  IsTrusted = true }

            let! r = Handlers.dispatch (registerConnectionRequest "reg-rawsql-db" dbDef)
            Assert.True(r.Error.IsNone)
            Handlers.waitForPendingGeneration ()

            let docUri = Path.Combine(tmp, "rawsql_test.fsqnb")

            // Evaluate with ResultKind.toRawSql
            let cellSource =
                "query {\n" +
                "    for item in Connections.sql.RawSqlDb.Items do\n" +
                "    where (item.Price > 10.0M)\n" +
                "    select item.Name\n" +
                "}\n" +
                "|> ResultKind.toRawSql"

            let! resp = Handlers.dispatch (notebookCellRequest "eval-rawsql" cellSource docUri)
            Assert.True(resp.Error.IsNone, (match resp.Error with Some e -> e.Message | None -> ""))
            let env = JsonSerializer.Deserialize<ResultEnvelope>(resp.Result.Value.GetRawText(), Serialization.options)

            Assert.Equal(ResultKind.Text, env.Kind)
            Assert.True(env.DisplayText.IsSome)
            let text = env.DisplayText.Value
            Assert.Contains("SELECT", text)
            Assert.Contains("Name", text)
            Assert.Contains("FROM Items", text)
            Assert.Contains("-- Parameters:", text)
            Assert.Contains("@p0 = 10", text)
        finally
            Handlers.setWorkspaceDirForTests None
            cleanTempDir tmp
    }

[<Fact>]
let ``workspace/autoImportDataSources creates data_sources and discovers files automatically`` () =
    task {
        let tmp = Path.Combine(Path.GetTempPath(), "fqs-autoimport-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(tmp) |> ignore
        Handlers.setWorkspaceDirForTests (Some tmp)

        try
            let escapedTmp = tmp.Replace("\\", "\\\\")

            // 1. First run: folder does not exist yet
            let req1: WorkerRequestFrame =
                { Id = "auto-1"
                  Method = "workspace/autoImportDataSources"
                  Params = JsonDocument.Parse($"""{{"targetDir": "{escapedTmp}"}}""").RootElement }
            let! res1 = Handlers.dispatch req1
            Assert.True(res1.Error.IsNone, (match res1.Error with Some e -> e.Message | None -> ""))
            Assert.True(res1.Result.IsSome)

            let r1 = res1.Result.Value
            Assert.True(r1.GetProperty("folderCreated").GetBoolean())
            Assert.Equal(0, r1.GetProperty("foundFilesCount").GetInt32())
            Assert.Equal(0, r1.GetProperty("importedCount").GetInt32())

            let dataSourcesDir = Path.Combine(tmp, "data_sources")
            Assert.True(Directory.Exists dataSourcesDir)
            Assert.True(File.Exists(Path.Combine(dataSourcesDir, "README.txt")))

            // 2. Add files to data_sources: a CSV and a SQLite DB
            let csvPath = Path.Combine(dataSourcesDir, "orders_feed.csv")
            File.WriteAllText(csvPath, "OrderId,Product,Amount\n1,Alpha,99.5\n2,Beta,49.0\n")

            let dbPath = Path.Combine(dataSourcesDir, "catalog_store.db")
            do
                use c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Pooling=false")
                c.Open()
                use cmd = c.CreateCommand()
                cmd.CommandText <- "CREATE TABLE Items (Id INTEGER PRIMARY KEY, SKU TEXT); INSERT INTO Items VALUES (1, 'SKU-001');"
                cmd.ExecuteNonQuery() |> ignore
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()

            // 3. Run auto-import again
            let req2: WorkerRequestFrame =
                { Id = "auto-2"
                  Method = "workspace/autoImportDataSources"
                  Params = JsonDocument.Parse($"""{{"targetDir": "{escapedTmp}"}}""").RootElement }
            let! res2 = Handlers.dispatch req2
            Assert.True(res2.Error.IsNone, (match res2.Error with Some e -> e.Message | None -> ""))
            let r2 = res2.Result.Value
            Assert.False(r2.GetProperty("folderCreated").GetBoolean())
            Assert.Equal(2, r2.GetProperty("foundFilesCount").GetInt32())
            Assert.Equal(2, r2.GetProperty("importedCount").GetInt32())

            // Verify connections are registered in workspace.json
            let conns = Workspace.readConnections tmp
            Assert.Equal(2, conns.Length)
            let names = conns |> List.map (fun c -> c.Name) |> Set.ofList
            Assert.Contains("OrdersFeed", names)
            Assert.Contains("CatalogStore", names)

            // Verify relative paths
            let orderConn = conns |> List.find (fun c -> c.Name = "OrdersFeed")
            let catConn = conns |> List.find (fun c -> c.Name = "CatalogStore")
            Assert.Equal(ConnectorKind("Csv"), orderConn.Kind)
            Assert.Equal(ConnectorKind("Sqlite"), catConn.Kind)
            Assert.Equal("data_sources/orders_feed.csv", orderConn.Configuration.GetProperty("filePath").GetString())
            Assert.Equal("data_sources/catalog_store.db", catConn.Configuration.GetProperty("filePath").GetString())

            // 4. Re-running without adding new files skips already registered files
            let req3: WorkerRequestFrame =
                { Id = "auto-3"
                  Method = "workspace/autoImportDataSources"
                  Params = JsonDocument.Parse($"""{{"targetDir": "{escapedTmp}"}}""").RootElement }
            let! res3 = Handlers.dispatch req3
            Assert.True(res3.Error.IsNone)
            let r3 = res3.Result.Value
            Assert.Equal(0, r3.GetProperty("importedCount").GetInt32())
            Assert.Equal(2, r3.GetProperty("skippedCount").GetInt32())
            Assert.Equal(0, r3.GetProperty("removedCount").GetInt32())

            // 5. Delete orders_feed.csv from data_sources and verify auto-removal
            let mutable deleted = false
            let mutable retries = 0
            while not deleted && retries < 30 do
                try
                    File.Delete csvPath
                    deleted <- true
                with :? System.IO.IOException ->
                    do! Task.Delay 100
                    retries <- retries + 1
            Assert.True(deleted, "Expected orders_feed.csv to be deleted")

            let req4: WorkerRequestFrame =
                { Id = "auto-4"
                  Method = "workspace/autoImportDataSources"
                  Params = JsonDocument.Parse($"""{{"targetDir": "{escapedTmp}"}}""").RootElement }
            let! res4 = Handlers.dispatch req4
            Assert.True(res4.Error.IsNone)
            let r4 = res4.Result.Value
            Assert.Equal(1, r4.GetProperty("removedCount").GetInt32())

            let connsAfterDelete = Workspace.readConnections tmp
            Assert.Equal(1, connsAfterDelete.Length)
            Assert.Equal("CatalogStore", connsAfterDelete.[0].Name)
        finally
            Handlers.setWorkspaceDirForTests None
            cleanTempDir tmp
    }
