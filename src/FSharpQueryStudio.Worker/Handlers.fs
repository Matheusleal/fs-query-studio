namespace FSharpQueryStudio.Worker

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Reflection
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Serilog
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Execution
open FSharpQueryStudio.Results
open FSharpQueryStudio.Fsac
open FSharpQueryStudio.Storage
open FSharpQueryStudio.Connectors
open FSharpQueryStudio.Security

/// Method dispatch for incoming WorkerRequestFrames — see Section 7's
/// method table.
module Handlers =

    let private workerVersion () =
        match Assembly.GetExecutingAssembly().GetName().Version with
        | null -> "0.0.0"
        | v -> v.ToString()

    let handleHealth (request: WorkerRequestFrame) : WorkerResponseFrame =
        let health: HealthInfo =
            { Version = workerVersion ()
              Capabilities = [] }

        { Id = request.Id
          Result = Some(JsonSerializer.SerializeToElement(health, Serialization.options))
          Error = None }

    /// 0 = idle, 1 = an evaluation is running. A worker handles at most one
    /// evaluation at a time — FsiSession.create mutates process-wide state
    /// (current directory, Console.Out/Error) that a second concurrent
    /// evaluation would corrupt, and CancellationSignal is keyed by
    /// working directory, one per evaluation.
    let private evaluationInFlight = ref 0

    /// The current evaluation's working directory, so `handleCancel` knows
    /// where to write the cancellation sentinel. `None` when idle.
    let mutable private currentWorkDir: string option = None

    /// Active continuation cursors for truncated Table results — Section 7's
    /// `results/page`. Keyed by the token handed to the client in
    /// `ResultEnvelope.ContinuationToken`. Supports concurrent notebook tabs.
    let private pagingCursors = ConcurrentDictionary<string, PagingCursor>()
    let private sessionTokens = ConcurrentDictionary<string, string>()

    /// The persistent, single per-machine workspace directory — connections,
    /// trust metadata, package approvals — `%LOCALAPPDATA%\FSharpQueryStudio`
    /// in production. Distinct from `currentWorkDir` above, which is a fresh
    /// throwaway temp directory per *evaluation*; this one is long-lived and
    /// shared across every evaluation. `setWorkspaceDirForTests` exists only
    /// so a test run never reads or writes the real user profile — never
    /// called from `Program.fs`.
    let mutable private workspaceDirOverride: string option = None

    let workspaceDir () : string =
        defaultArg
            workspaceDirOverride
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FSharpQueryStudio"))

    let setWorkspaceDirForTests (dir: string option) : unit =
        workspaceDirOverride <- dir
        FSharpQueryStudio.Fsac.Prologue.setWorkspaceDirForTests dir

    let waitForPendingGeneration () : unit =
        EntityBackgroundGenerator.waitForPendingGeneration()

    /// The secret store the worker's connection resolver reads `{password}`
    /// tokens from. The default `InMemorySecretStore` serves a standalone
    /// `dotnet run` of the worker and any test that never calls
    /// `configureSecretStore`; being empty, it resolves nothing, so a
    /// `{password}` connection simply fails as a missing secret. `Program.fs`
    /// swaps in a `BridgeSecretStore` at startup so the extension host's
    /// credential store (VS Code `SecretStorage`, cross-platform) is the
    /// source of truth. One process-wide store — the worker handles one
    /// evaluation at a time.
    let mutable private secretStore: ISecretStore = InMemorySecretStore()

    let configureSecretStore (store: ISecretStore) : unit = secretStore <- store

    let mutable private hostChannel: HostChannel option = None

    let configureHostChannel (ch: HostChannel) : unit =
        hostChannel <- Some ch
        EntityBackgroundGenerator.onStatusChanged <- Some (fun status ->
            let statusStr =
                match status with
                | EntityBackgroundGenerator.EntityMappingStatus.Started -> "started"
                | EntityBackgroundGenerator.EntityMappingStatus.Completed _ -> "completed"
                | EntityBackgroundGenerator.EntityMappingStatus.Failed _ -> "failed"
            ch.Request("entityMapping/status", {| status = statusStr |}) |> ignore
        )
        FileSqliteEngine.GlobalProgressCallback <- Some (fun ev ->
            ch.Request(
                "staging/progress",
                {|
                    tableName = ev.TableName
                    filePath = ev.FilePath
                    phase = ev.Phase
                    rowCount = (match ev.RowCount with Some n -> box n | None -> null)
                    currentIndex = (match ev.CurrentIndex with Some n -> box n | None -> null)
                    totalCount = (match ev.TotalCount with Some n -> box n | None -> null)
                    elapsedMs = (match ev.ElapsedMs with Some n -> box n | None -> null)
                    message = (match ev.Message with Some m -> m | None -> "")
                |}
            ) |> ignore
        )

    let private busyResponse (id: string) : WorkerResponseFrame =
        { Id = id
          Result = None
          Error =
            Some
                { Code = "execution/busy"
                  Message = "Another evaluation is already in progress." } }

    /// Reserves the evaluation slot synchronously: the busy-check and
    /// `currentWorkDir` assignment happen here, not inside the background
    /// task that actually runs FSI. This matters because the caller (the
    /// worker's read loop) must have `currentWorkDir` set *before* it goes
    /// back to reading the next frame — otherwise a client that sends
    /// execution/cancel quickly enough can have it arrive before a
    /// `Task.Run`'d evaluation has actually started on a threadpool
    /// thread (a fresh process's threadpool needs time to inject worker
    /// threads under load), and `handleCancel` would silently no-op
    /// against a still-`None` `currentWorkDir`. Reserving synchronously
    /// closes that race by construction instead of relying on timing.
    let beginEvaluate (request: WorkerRequestFrame) : Choice<string, WorkerResponseFrame> =
        if Interlocked.CompareExchange(evaluationInFlight, 1, 0) <> 0 then
            Choice2Of2(busyResponse request.Id)
        else
            let workDir =
                Path.Combine(Path.GetTempPath(), "fqs-exec-" + Guid.NewGuid().ToString("N"))

            Directory.CreateDirectory(workDir) |> ignore
            CancellationSignal.reset workDir
            currentWorkDir <- Some workDir
            try
                let evalParams = JsonSerializer.Deserialize<EvaluateParams>(request.Params, Serialization.options)
                let sessionKey =
                    evalParams.SessionId
                    |> Option.defaultValue (
                        if String.IsNullOrEmpty evalParams.DocumentUri then "default" else evalParams.DocumentUri
                    )
                match sessionTokens.TryGetValue(sessionKey) with
                | true, oldToken -> pagingCursors.TryRemove(oldToken) |> ignore
                | false, _ -> ()
            with _ -> ()
            Choice1Of2 workDir

    /// Isolated by default (Section 9): a fresh session in the reserved
    /// working directory. Opt-in Persistent mode (Milestone E.1) instead
    /// reuses/creates the `SessionId`-keyed session in `SessionStore`, so
    /// `let` bindings from a prior evaluate under the same key are still
    /// visible — unless E.3's invalidation rule kicks in first (this
    /// request's source isn't a pure append of what the session last ran),
    /// in which case `SessionStore.getOrCreate` transparently drops the
    /// stale session and starts a fresh one under the same key. An
    /// uncaught script exception becomes a structured Error result
    /// (Section 11), never an unhandled crash — EvalInteractionNonThrowing
    /// already keeps it from propagating.
    let resolveWorkspaceForDocument (docUriStr: string) : string =
        Prologue.resolveWorkspaceForDocument docUriStr
        |> Option.defaultValue (workspaceDir ())

    let private ensureSampleFilesHealed (connections: ConnectionDefinition list) : unit =
        for def in connections do
            try
                if def.Configuration.ValueKind = JsonValueKind.Object then
                    let mutable fpProp = Unchecked.defaultof<JsonElement>
                    if def.Configuration.TryGetProperty("filePath", &fpProp) && fpProp.ValueKind = JsonValueKind.String then
                        let fp = fpProp.GetString()
                        if not (String.IsNullOrWhiteSpace fp) then
                            let isMissingOrEmpty = not (File.Exists fp) || (FileInfo(fp).Length = 0L)
                            if isMissingOrEmpty then
                                if String.Equals(def.Name, "SampleStore", StringComparison.OrdinalIgnoreCase) then
                                    SampleDataGenerator.createSampleStoreDatabase fp
                                elif String.Equals(def.Name, "InventoryStore", StringComparison.OrdinalIgnoreCase) then
                                    SampleDataGenerator.createInventoryDatabase fp
                                elif String.Equals(def.Name, "SalesFeed", StringComparison.OrdinalIgnoreCase) then
                                    SampleDataGenerator.createSampleCsv fp
                                elif String.Equals(def.Name, "CustomersFeed", StringComparison.OrdinalIgnoreCase) then
                                    SampleDataGenerator.createSampleCustomersCsv fp
                                elif String.Equals(def.Name, "RegionalTargets", StringComparison.OrdinalIgnoreCase) then
                                    SampleDataGenerator.createSampleExcelWorkbook fp
                                elif String.Equals(def.Name, "CommerceOps", StringComparison.OrdinalIgnoreCase) then
                                    SampleDataGenerator.createSampleCommerceExcelWorkbook fp
            with _ -> ()

    let runEvaluate (request: WorkerRequestFrame) (workDir: string) : WorkerResponseFrame =
        try
            let evalParams =
                JsonSerializer.Deserialize<EvaluateParams>(request.Params, Serialization.options)

            let stopwatch = Stopwatch.StartNew()

            let targetWs = resolveWorkspaceForDocument evalParams.DocumentUri
            let connections = Workspace.readConnections targetWs
            ensureSampleFilesHealed connections
            Connections.configure (WorkspaceConnectionResolver(connections, secretStore, targetWs))
            EntityBackgroundGenerator.ensureGenerated targetWs connections secretStore

            let session =
                match evalParams.Mode, evalParams.SessionId with
                | Persistent, Some sessionId when evalParams.IsNotebookCell ->
                    Log.Information(
                        "Appending notebook cell to persistent FSI session {SessionId} in {WorkingDirectory} with workspace {WorkspaceDir}",
                        sessionId,
                        workDir,
                        targetWs
                    )
                    SessionStore.appendFragmentInWorkspace sessionId workDir evalParams.Source targetWs
                | Persistent, Some sessionId ->
                    Log.Information(
                        "Evaluating persistent FSI session {SessionId} in {WorkingDirectory} with workspace {WorkspaceDir}",
                        sessionId,
                        workDir,
                        targetWs
                    )
                    SessionStore.getOrCreateInWorkspace sessionId workDir evalParams.Source targetWs
                | Persistent, None ->
                    Log.Warning("Persistent mode requested without a SessionId; falling back to an isolated session")
                    FsiSession.createInWorkspace workDir targetWs
                | Isolated, _ ->
                    Log.Information("Creating FSI session in {WorkingDirectory} with workspace {WorkspaceDir}", workDir, targetWs)
                    FsiSession.createInWorkspace workDir targetWs

            let outcome = Evaluation.evaluate session evalParams.Source
            stopwatch.Stop()

            let output = Projection.toOutputLines outcome.Output outcome.ErrorOutput
            Projection.registerSourceCode evalParams.Source

            let envelope =
                match outcome.Exception with
                | Some ex ->
                    let diagMsg =
                        if outcome.Diagnostics.Length > 0 then
                            outcome.Diagnostics
                            |> Array.map (fun d ->
                                let sub = if String.IsNullOrEmpty d.Subcategory then "error" else d.Subcategory
                                $"{sub} FS{d.ErrorNumber:D4}: {d.Message} (line {d.StartLine})")
                            |> String.concat "\n"
                        elif not (String.IsNullOrWhiteSpace outcome.ErrorOutput) then
                            outcome.ErrorOutput
                        else
                            ""
                    { Projection.projectErrorWithDetails ex (if String.IsNullOrWhiteSpace diagMsg then None else Some diagMsg) stopwatch.ElapsedMilliseconds with Output = output }
                | None ->
                    let value =
                        outcome.Value |> Option.map (fun v -> v.ReflectionValue) |> Option.toObj

                    let projected, cursor, retained =
                        Projection.projectWithCursorDetailed value stopwatch.ElapsedMilliseconds (Some evalParams.Source) None

                    // Export CSV targets the notebook's most recent MATERIALIZED
                    // grid. A run that produced no such table (a scalar, a
                    // streamed scalar seq, a printfn) leaves any earlier
                    // retained result in place — it does not clear it.
                    if not (String.IsNullOrEmpty evalParams.DocumentUri) then
                        match retained with
                        | Some t ->
                            ResultRetention.set
                                evalParams.DocumentUri
                                { Columns = t.Columns; Rows = t.Rows }
                        | None -> ()

                    let sessionKey =
                        evalParams.SessionId
                        |> Option.defaultValue (
                            if String.IsNullOrEmpty evalParams.DocumentUri then "default" else evalParams.DocumentUri
                        )

                    match sessionTokens.TryGetValue(sessionKey) with
                    | true, oldToken -> pagingCursors.TryRemove(oldToken) |> ignore
                    | false, _ -> ()

                    let continuationToken =
                        cursor
                        |> Option.map (fun c ->
                            let token = Guid.NewGuid().ToString("N")
                            pagingCursors.[token] <- c
                            sessionTokens.[sessionKey] <- token
                            token)

                    { projected with
                        Output = output
                        ContinuationToken = continuationToken }

            Log.Information(
                "Execution completed with outcome {Outcome} in {ElapsedMs}ms",
                envelope.Kind,
                stopwatch.ElapsedMilliseconds
            )

            { Id = request.Id
              Result = Some(JsonSerializer.SerializeToElement(envelope, Serialization.options))
              Error = None }
        finally
            currentWorkDir <- None
            Interlocked.Exchange(evaluationInFlight, 0) |> ignore
            try
                Directory.SetCurrentDirectory(Path.GetTempPath())
                if Directory.Exists workDir then
                    Directory.Delete(workDir, true)
            with _ ->
                ()
            GC.Collect()


    /// Convenience wrapper (reserve, then run, both synchronous) for
    /// callers that don't need the two steps split — direct/unit-test use.
    /// The worker's own read loop uses `beginEvaluate`/`runEvaluate`
    /// separately so it can background only the run step.
    let handleEvaluate (request: WorkerRequestFrame) : WorkerResponseFrame =
        match beginEvaluate request with
        | Choice2Of2 busy -> busy
        | Choice1Of2 workDir -> runEvaluate request workDir

    /// `session/reset` — drops the tracked Persistent-mode session for
    /// `SessionId`, if any (E.2). A no-op, not an error, when nothing was
    /// tracked under that key: the caller can't distinguish "never
    /// started" from "already reset" and shouldn't need to.
    let handleSessionReset (request: WorkerRequestFrame) : WorkerResponseFrame =
        let resetParams =
            JsonSerializer.Deserialize<SessionResetParams>(request.Params, Serialization.options)

        Log.Information("Resetting persistent session {SessionId}", resetParams.SessionId)
        SessionStore.reset resetParams.SessionId
        match sessionTokens.TryRemove(resetParams.SessionId) with
        | true, oldToken -> pagingCursors.TryRemove(oldToken) |> ignore
        | false, _ -> ()

        { Id = request.Id
          Result = Some(JsonSerializer.SerializeToElement({| acknowledged = true |}, Serialization.options))
          Error = None }

    /// Requests cooperative cancellation of whatever evaluation is
    /// currently running (a no-op if none is) and acknowledges immediately
    /// — see Section 9's two-step cancellation policy. The evaluate
    /// request's own response arrives separately, whenever it completes.
    let handleCancel (request: WorkerRequestFrame) : WorkerResponseFrame =
        Log.Information("Cancellation requested")

        match currentWorkDir with
        | Some workDir -> CancellationSignal.requestCancel workDir
        | None -> ()

        { Id = request.Id
          Result = Some(JsonSerializer.SerializeToElement({| acknowledged = true |}, Serialization.options))
          Error = None }

    let private ensureEntitiesReadyForDocument (docUriStr: string) =
        try
            let ws = resolveWorkspaceForDocument docUriStr
            let connections = Workspace.readConnections ws
            EntityBackgroundGenerator.ensureGenerated ws connections secretStore
        with ex ->
            Log.Warning("FS Query Studio: Failed to ensure entities ready before analysis for {Uri}: {Error}", docUriStr, ex.Message)

    let private ensureEntitiesReady () =
        ensureEntitiesReadyForDocument ""

    /// Parses/type-checks the request's source snapshot via FSAC, over the
    /// shared `FsacBridge.sharedProcess` LSP connection (Section 8, step
    /// 3-4). Unlike the old FCS-direct handler, this has no per-request
    /// working directory to create or clean up — FSAC tracks the document
    /// in memory, keyed by `DocumentUri`/`Version`, the same way the old
    /// `AnalysisContext` tracked it as a `SourceText`.
    let handleAnalysisCheck (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            let checkParams =
                JsonSerializer.Deserialize<AnalysisCheckParams>(request.Params, Serialization.options)
            ensureEntitiesReadyForDocument checkParams.DocumentUri

            let! diagnostics = FsacBridge.check checkParams.DocumentUri checkParams.Version checkParams.Source
            let result: AnalysisCheckResult = { Diagnostics = diagnostics }

            return
                { Id = request.Id
                  Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
                  Error = None }
        }

    /// Answers `analysis/hover` from the request's own source snapshot, via
    /// `FsacBridge.hover` — same stateless-to-the-caller shape as
    /// `handleAnalysisCheck`. `Hover = None` (nothing at that position) is
    /// a normal, successful `Result`, never an `Error`.
    let handleAnalysisHover (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            let hoverParams =
                JsonSerializer.Deserialize<AnalysisHoverParams>(request.Params, Serialization.options)
            ensureEntitiesReadyForDocument hoverParams.DocumentUri

            let! hover =
                FsacBridge.hover
                    hoverParams.DocumentUri
                    hoverParams.Version
                    hoverParams.Source
                    hoverParams.Line
                    hoverParams.Column

            let result: AnalysisHoverResult = { Hover = hover }

            return
                { Id = request.Id
                  Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
                  Error = None }
        }

    /// Answers `analysis/definition` from the request's own source
    /// snapshot, via `FsacBridge.definition` — same shape as
    /// `handleAnalysisHover`. `Location = None` (nothing resolvable at
    /// that position) is a normal, successful `Result`, never an `Error`.
    /// The same-file `FilePath` rewrite the old FCS-direct handler did by
    /// hand (mapping the analysis context's own throwaway script path back
    /// to the requesting document's URI) now happens inside
    /// `FsacBridge.definition` itself, so this handler doesn't need it.
    let handleAnalysisDefinition (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            let definitionParams =
                JsonSerializer.Deserialize<AnalysisDefinitionParams>(request.Params, Serialization.options)
            ensureEntitiesReadyForDocument definitionParams.DocumentUri

            let! location =
                FsacBridge.definition
                    definitionParams.DocumentUri
                    definitionParams.Version
                    definitionParams.Source
                    definitionParams.Line
                    definitionParams.Column

            let result: AnalysisDefinitionResult = { Location = location }

            return
                { Id = request.Id
                  Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
                  Error = None }
        }

    /// Answers `analysis/signatureHelp` from the request's own source
    /// snapshot, via `FsacBridge.signatureHelp` — same shape as
    /// `handleAnalysisHover`/`handleAnalysisDefinition`. `SignatureHelp =
    /// None` (not a callable symbol, or nothing resolvable there) is a
    /// normal, successful `Result`, never an `Error`.
    let handleAnalysisSignatureHelp (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            let signatureHelpParams =
                JsonSerializer.Deserialize<AnalysisSignatureHelpParams>(request.Params, Serialization.options)

            let! signatureHelp =
                FsacBridge.signatureHelp
                    signatureHelpParams.DocumentUri
                    signatureHelpParams.Version
                    signatureHelpParams.Source
                    signatureHelpParams.Line
                    signatureHelpParams.Column

            let result: AnalysisSignatureHelpResult = { SignatureHelp = signatureHelp }

            return
                { Id = request.Id
                  Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
                  Error = None }
        }

    /// `analysis/complete` — Milestone B.10/B.11, now answered entirely by
    /// `FsacBridge.complete` (FSAC's own `textDocument/completion`, which
    /// already merges keyword/static and semantic/cursor-derived
    /// completions server-side). The old tier-1/tier-2 merge this handler
    /// used to do by hand against `AnalysisContext` is gone with it.
    let handleAnalysisComplete (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            let completionParams =
                JsonSerializer.Deserialize<AnalysisCompletionParams>(request.Params, Serialization.options)
            ensureEntitiesReadyForDocument completionParams.DocumentUri

            let! items =
                FsacBridge.complete
                    completionParams.DocumentUri
                    completionParams.Version
                    completionParams.Source
                    completionParams.Line
                    completionParams.Column

            let result: AnalysisCompletionResult =
                { Items = items |> List.map (fun i -> i.Label)
                  DetailedItems = Some items }

            return
                { Id = request.Id
                  Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
                  Error = None }
        }

    /// `analysis/rename` — Milestone E.8, now answered by
    /// `FsacBridge.rename` (FSAC's `textDocument/references`), which
    /// already returns the protocol-level `RenameEdit` list directly — no
    /// separate internal-to-protocol mapping step is needed any more.
    let handleAnalysisRename (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            let renameParams =
                JsonSerializer.Deserialize<AnalysisRenameParams>(request.Params, Serialization.options)

            let! edits =
                FsacBridge.rename
                    renameParams.DocumentUri
                    renameParams.Version
                    renameParams.Source
                    renameParams.Line
                    renameParams.Column

            let result: AnalysisRenameResult = { Edits = edits }

            return
                { Id = request.Id
                  Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
                  Error = None }
        }

    /// `results/page` — Section 7. An unknown or expired
    /// `ContinuationToken` (nothing currently open, or a token from a
    /// since-replaced evaluation) is a structured error, never a silent
    /// re-run of whatever produced the original result.
    let handleResultsPage (request: WorkerRequestFrame) : WorkerResponseFrame =
        let pageParams =
            JsonSerializer.Deserialize<ResultPageRequest>(request.Params, Serialization.options)

        match pagingCursors.TryGetValue(pageParams.ContinuationToken) with
        | true, cursor ->
            let rows, hasMore = cursor.TakePage(Projection.PreviewRowLimit)
            if not hasMore then
                pagingCursors.TryRemove(pageParams.ContinuationToken) |> ignore

            let page: ResultPage =
                { Rows = rows |> Array.map (fun r -> [| r |])
                  IsTruncated = hasMore
                  ContinuationToken = (if hasMore then Some pageParams.ContinuationToken else None) }

            { Id = request.Id
              Result = Some(JsonSerializer.SerializeToElement(page, Serialization.options))
              Error = None }
        | _ ->
            { Id = request.Id
              Result = None
              Error =
                Some
                    { Code = "results/unknownToken"
                      Message = "The continuation token is unknown or has expired." } }

    /// `results/exportCsv` — writes `documentUri`'s retained full result
    /// (see `ResultRetention`) straight to `filePath` via the existing,
    /// already-tested `CsvExport.write`. No re-query, no connector: the
    /// retained columns/rows are already fully in memory by this point.
    let handleResultsExportCsv (request: WorkerRequestFrame) : WorkerResponseFrame =
        let exportParams =
            JsonSerializer.Deserialize<ExportCsvParams>(request.Params, Serialization.options)

        match ResultRetention.tryGet exportParams.DocumentUri with
        | None ->
            { Id = request.Id
              Result = None
              Error =
                Some
                    { Code = "results/nothingToExport"
                      Message = $"No retained table result for '{exportParams.DocumentUri}'." } }
        | Some retained ->
            try
                // `Encoding.UTF8` (not `UTF8NoBOM`) writes the BOM Excel
                // needs to render non-ASCII CSV content correctly.
                use writer = new StreamWriter(exportParams.FilePath, false, System.Text.Encoding.UTF8)
                CsvExport.write writer retained.Columns retained.Rows

                { Id = request.Id
                  Result = Some(JsonSerializer.SerializeToElement({| acknowledged = true |}, Serialization.options))
                  Error = None }
            with ex ->
                { Id = request.Id
                  Result = None
                  Error = Some { Code = "results/exportFailed"; Message = ex.Message } }

    /// `results/discard` — evicts `documentUri`'s retained result, if any
    /// (Section "Eviction on tab close"). Always succeeds, even for a
    /// `documentUri` nothing is retained under, matching
    /// `Workspace.removeConnection`'s established no-op-on-unknown-key
    /// convention.
    let handleResultsDiscard (request: WorkerRequestFrame) : WorkerResponseFrame =
        let discardParams =
            JsonSerializer.Deserialize<ResultsDiscardParams>(request.Params, Serialization.options)

        ResultRetention.discard discardParams.DocumentUri

        { Id = request.Id
          Result = Some(JsonSerializer.SerializeToElement({| acknowledged = true |}, Serialization.options))
          Error = None }

    let private tryGetWorkspaceDir (paramsElem: JsonElement) : string =
        try
            if paramsElem.ValueKind = JsonValueKind.Object then
                let mutable prop = Unchecked.defaultof<JsonElement>
                if (paramsElem.TryGetProperty("workspaceDir", &prop)
                    || paramsElem.TryGetProperty("targetDir", &prop)
                    || paramsElem.TryGetProperty("WorkspaceDir", &prop)
                    || paramsElem.TryGetProperty("TargetDir", &prop))
                   && prop.ValueKind = JsonValueKind.String then
                    let v = prop.GetString()
                    if String.IsNullOrWhiteSpace v then workspaceDir () else v
                else
                    workspaceDir ()
            else
                workspaceDir ()
        with _ ->
            workspaceDir ()

    /// `connection/register` — Section 7. Persists `connection` to
    /// `workspace.json` via `Workspace.registerConnection`, replacing any
    /// existing binding sharing its `Id`, and returns the updated full list.
    let handleConnectionRegister (request: WorkerRequestFrame) : WorkerResponseFrame =
        let targetWs = tryGetWorkspaceDir request.Params
        let connection =
            JsonSerializer.Deserialize<ConnectionDefinition>(request.Params, Serialization.options)

        if targetWs <> (workspaceDir ()) then
            let fsqsDir = Path.Combine(targetWs, ".fsqs")
            if not (Directory.Exists fsqsDir) && not (targetWs.EndsWith(".fsqs", StringComparison.OrdinalIgnoreCase)) then
                Directory.CreateDirectory fsqsDir |> ignore

        let updated = Workspace.registerConnection targetWs connection
        Connections.configure (WorkspaceConnectionResolver(updated, secretStore, targetWs))
        EntityBackgroundGenerator.triggerBackgroundGeneration targetWs updated secretStore
        Connections.triggerBackgroundSync targetWs updated secretStore

        { Id = request.Id
          Result = Some(JsonSerializer.SerializeToElement(updated, Serialization.options))
          Error = None }

    let private cleanLeakedGlobalSampleConnections (globalWs: string) : ConnectionDefinition list =
        let conns = Workspace.readConnections globalWs
        let sampleNames = set [ "SampleStore"; "InventoryStore"; "SalesFeed"; "CustomersFeed"; "RegionalTargets"; "CommerceOps"; "GitHubFSharp" ]
        let isLeakedSample (c: ConnectionDefinition) =
            sampleNames.Contains c.Name && (
                try
                    let doc = JsonDocument.Parse(c.Configuration.GetRawText())
                    let mutable fpProp = Unchecked.defaultof<JsonElement>
                    if doc.RootElement.ValueKind = JsonValueKind.Object && doc.RootElement.TryGetProperty("filePath", &fpProp) && fpProp.ValueKind = JsonValueKind.String then
                        let fp = fpProp.GetString()
                        not (File.Exists fp) || not (fp.StartsWith(globalWs, StringComparison.OrdinalIgnoreCase))
                    else
                        false
                with _ -> false
            )
        let leaked = conns |> List.filter isLeakedSample
        if not (List.isEmpty leaked) then
            let cleaned = conns |> List.filter (fun c -> not (isLeakedSample c))
            Workspace.writeConnections globalWs cleaned
            cleaned
        else
            conns

    /// `connection/list` — the read-only companion to `connection/register` /
    /// `connection/remove` (F5). Neither of those returns the registry without
    /// mutating it, and the extension's connections TreeView needs the list on
    /// notebook/view open. A thin projection of `Workspace.readConnections`.
    let handleConnectionList (request: WorkerRequestFrame) : WorkerResponseFrame =
        let targetWs = tryGetWorkspaceDir request.Params
        let connections =
            let wsConns = Workspace.readConnections targetWs
            if targetWs <> (workspaceDir ()) then
                let globalConns = cleanLeakedGlobalSampleConnections (workspaceDir ())
                let localNames = wsConns |> List.map (fun c -> c.Name) |> Set.ofList
                let localIds = wsConns |> List.map (fun c -> c.Id) |> Set.ofList
                let filteredGlobal = globalConns |> List.filter (fun c -> not (Set.contains c.Name localNames) && not (Set.contains c.Id localIds))
                wsConns @ filteredGlobal
            else
                cleanLeakedGlobalSampleConnections targetWs

        { Id = request.Id
          Result = Some(JsonSerializer.SerializeToElement({| connections = connections |}, Serialization.options))
          Error = None }

    /// `connector/list` — returns all registered connector descriptors and their declarative form schemas.
    let handleConnectorList (request: WorkerRequestFrame) : WorkerResponseFrame =
        try
            BuiltinConnectors.registerDefaults ()
            let descriptors =
                ConnectorRegistry.Instance.AllDescriptors()
                |> List.map ConnectorDescriptor.toDto
            let result: ConnectorListResult = { Connectors = descriptors }
            { Id = request.Id
              Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
              Error = None }
        with ex ->
            { Id = request.Id
              Result = None
              Error = Some { Code = "connector/listFailed"; Message = ex.Message } }


    /// `connection/remove` — drops the connection with the given `Id`
    /// from `workspace.json` via `Workspace.removeConnection` and returns
    /// the updated full list.
    let handleConnectionRemove (request: WorkerRequestFrame) : WorkerResponseFrame =
        let targetWs = tryGetWorkspaceDir request.Params
        let removeParams =
            JsonSerializer.Deserialize<ConnectionRemoveParams>(request.Params, Serialization.options)

        let updated = Workspace.removeConnection targetWs removeParams.Id
        if targetWs <> (workspaceDir ()) then
            Workspace.removeConnection (workspaceDir ()) removeParams.Id |> ignore
        Connections.configure (WorkspaceConnectionResolver(updated, secretStore, targetWs))
        EntityBackgroundGenerator.triggerBackgroundGeneration targetWs updated secretStore

        { Id = request.Id
          Result = Some(JsonSerializer.SerializeToElement(updated, Serialization.options))
          Error = None }

    /// `connection/schema` — Section 10's schema discovery.
    let handleConnectionSchema (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            let schemaParams =
                JsonSerializer.Deserialize<ConnectionSchemaParams>(request.Params, Serialization.options)

            let targetWs = tryGetWorkspaceDir request.Params
            let connections =
                let local = Workspace.readConnections targetWs
                if local |> List.exists (fun c -> c.Id = schemaParams.Id) then local
                else Workspace.readConnections (workspaceDir ())

            match connections |> List.tryFind (fun c -> c.Id = schemaParams.Id) with
            | None ->
                return
                    { Id = request.Id
                      Result = None
                      Error =
                        Some
                            { Code = "connection/unknown"
                              Message = $"No connection is registered with Id '{schemaParams.Id}'." } }
            | Some definition ->
                try
                    let resolver =
                        WorkspaceConnectionResolver([ definition ], secretStore, targetWs) :> IConnectionResolver

                    let connector = resolver.Resolve(definition.Name)
                    let! tables = connector.DiscoverSchemaAsync(CancellationToken.None)

                    EntityBackgroundGenerator.triggerBackgroundGeneration targetWs connections secretStore

                    let result: ConnectionSchemaResult =
                        { Tables =
                            tables
                            |> List.map (fun t ->
                                { Name = t.Name
                                  Columns =
                                    t.Columns
                                    |> List.map (fun c -> ({ Name = c.Name; DataType = c.DataType }: SchemaColumn)) }: SchemaTable) }

                    return
                        { Id = request.Id
                          Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
                          Error = None }
                with ex ->
                    return
                        { Id = request.Id
                          Result = None
                          Error = Some { Code = "connection/schemaFailed"; Message = ex.Message } }
        }


    let handleCreateSampleData (request: WorkerRequestFrame) : WorkerResponseFrame =
        try
            let dir =
                let tryGetProp (name: string) =
                    try
                        if request.Params.ValueKind = JsonValueKind.Object then
                            let mutable prop = Unchecked.defaultof<JsonElement>
                            if request.Params.TryGetProperty(name, &prop) && prop.ValueKind = JsonValueKind.String then
                                let v = prop.GetString()
                                if String.IsNullOrWhiteSpace v then None else Some v
                            else
                                None
                        else
                            None
                    with _ ->
                        None

                tryGetProp "targetDir"
                |> Option.orElse (tryGetProp "targetDirectory")
                |> Option.orElse (tryGetProp "TargetDir")
                |> Option.orElse (tryGetProp "TargetDirectory")
                |> Option.defaultValue (workspaceDir ())

            let result = WorkspaceOperations.createSampleData dir secretStore
            { Id = request.Id
              Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
              Error = None }
        with ex ->
            { Id = request.Id
              Result = None
              Error = Some { Code = "sample/createFailed"; Message = ex.Message } }

    let handleInitEmptyProject (request: WorkerRequestFrame) : WorkerResponseFrame =
        try
            let rootDir = tryGetWorkspaceDir request.Params
            let result = WorkspaceOperations.initEmptyProject rootDir
            { Id = request.Id
              Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
              Error = None }
        with ex ->
            { Id = request.Id
              Result = None
              Error = Some { Code = "workspace/initFailed"; Message = ex.Message } }

    let handleAutoImportDataSources (request: WorkerRequestFrame) : WorkerResponseFrame =
        try
            let rawDir = tryGetWorkspaceDir request.Params
            let result = WorkspaceOperations.autoImportDataSources rawDir secretStore
            { Id = request.Id
              Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
              Error = None }
        with ex ->
            { Id = request.Id
              Result = None
              Error = Some { Code = "workspace/autoImportFailed"; Message = ex.Message } }

    type SyncDataSourcesResult =
        { TargetDir: string
          StagedCount: int
          SkippedCount: int
          Total: int
          ElapsedMs: int64 }

    let handleSyncDataSources (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            try
                let targetDir = tryGetWorkspaceDir request.Params
                let connections = Workspace.readConnections targetDir
                let! summary = Connections.syncDataSources targetDir connections secretStore
                let result : SyncDataSourcesResult =
                    { TargetDir = targetDir
                      StagedCount = summary.StagedCount
                      SkippedCount = summary.SkippedCount
                      Total = summary.Total
                      ElapsedMs = summary.ElapsedMs }
                return
                    { Id = request.Id
                      Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
                      Error = None }
            with ex ->
                return
                    { Id = request.Id
                      Result = None
                      Error = Some { Code = "workspace/syncDataSourcesFailed"; Message = ex.Message } }
        }

    let handleWorkspaceUpgrade (request: WorkerRequestFrame) : WorkerResponseFrame =
        try
            let rootDir = tryGetWorkspaceDir request.Params
            let report = ProjectUpgrader.upgradeWorkspace rootDir
            let result : WorkspaceUpgradeResult =
                { Upgraded = report.Upgraded
                  PreviousVersion = report.PreviousVersion
                  CurrentVersion = report.CurrentVersion
                  Actions = report.Actions }

            { Id = request.Id
              Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
              Error = None }
        with ex ->
            { Id = request.Id
              Result = None
              Error = Some { Code = "workspace/upgradeFailed"; Message = ex.Message } }

    let dispatch (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            match request.Method with
            | "worker/health" -> return handleHealth request
            | "execution/evaluate" -> return handleEvaluate request
            | "execution/cancel" -> return handleCancel request
            | "session/reset" -> return handleSessionReset request
            | "analysis/check" -> return! handleAnalysisCheck request
            | "analysis/hover" -> return! handleAnalysisHover request
            | "analysis/definition" -> return! handleAnalysisDefinition request
            | "analysis/signatureHelp" -> return! handleAnalysisSignatureHelp request
            | "analysis/complete" -> return! handleAnalysisComplete request
            | "analysis/rename" -> return! handleAnalysisRename request
            | "results/page" -> return handleResultsPage request
            | "results/exportCsv" -> return handleResultsExportCsv request
            | "results/discard" -> return handleResultsDiscard request
            | "connector/list" -> return handleConnectorList request
            | "connection/list" -> return handleConnectionList request

            | "connection/register" -> return handleConnectionRegister request
            | "connection/remove" -> return handleConnectionRemove request
            | "connection/schema" -> return! handleConnectionSchema request
            | "workspace/createSampleData" -> return handleCreateSampleData request
            | "workspace/initProject" | "workspace/initEmptyProject" -> return handleInitEmptyProject request
            | "workspace/autoImportDataSources" | "workspace/autoImportFolder" -> return handleAutoImportDataSources request
            | "workspace/syncDataSources" -> return! handleSyncDataSources request
            | "workspace/upgrade" -> return handleWorkspaceUpgrade request
            | unknown ->
                return
                    { Id = request.Id
                      Result = None
                      Error =
                        Some
                            { Code = "method/unknown"
                              Message = $"Unknown method: {unknown}" } }
        }
