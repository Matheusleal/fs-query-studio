namespace FSharpQueryStudio.Worker

open System
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

    /// The still-open continuation for the current evaluation's truncated
    /// Table result, if any — Section 7's `results/page`. Keyed by the
    /// token handed to the client in `ResultEnvelope.ContinuationToken`.
    /// One worker handles one evaluation at a time (Section 9), so there
    /// is only ever one paging cursor to track; a fresh evaluate replaces
    /// or clears it, which is what "expires when the session is reset"
    /// means in a worker that never actually holds multiple sessions.
    let mutable private currentPaging: (string * PagingCursor) option = None

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

    let setWorkspaceDirForTests (dir: string option) : unit = workspaceDirOverride <- dir

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
            currentPaging <- None // a fresh evaluation invalidates any previous continuation token
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
    let runEvaluate (request: WorkerRequestFrame) (workDir: string) : WorkerResponseFrame =
        try
            let evalParams =
                JsonSerializer.Deserialize<EvaluateParams>(request.Params, Serialization.options)

            let stopwatch = Stopwatch.StartNew()

            let session =
                match evalParams.Mode, evalParams.SessionId with
                | Persistent, Some sessionId ->
                    Log.Information("Evaluating persistent FSI session {SessionId} in {WorkingDirectory}", sessionId, workDir)
                    SessionStore.getOrCreate sessionId workDir evalParams.Source
                | Persistent, None ->
                    Log.Warning("Persistent mode requested without a SessionId; falling back to an isolated session")
                    FsiSession.create workDir
                | Isolated, _ ->
                    Log.Information("Creating FSI session in {WorkingDirectory}", workDir)
                    FsiSession.create workDir

            let connections = Workspace.readConnections (workspaceDir ())
            Connections.configure (WorkspaceConnectionResolver(connections, WindowsCredentialStore()))
            Results.reset ()

            let outcome = Evaluation.evaluate session evalParams.Source
            stopwatch.Stop()

            let output = Projection.toOutputLines outcome.Output outcome.ErrorOutput

            let envelope =
                match outcome.Exception with
                | Some ex -> { Projection.projectError ex stopwatch.ElapsedMilliseconds with Output = output }
                | None ->
                    // A script's explicit `Results.table`/`Results.text` push
                    // (Section 11's controlled results surface) wins over FSI's
                    // own tail-value convention - the whole point is showing
                    // something meaningful even when the script's last line is
                    // a `let` binding or a side-effecting call that never
                    // produces an "it" value of its own.
                    match Results.currentEnvelope stopwatch.ElapsedMilliseconds output with
                    | Some explicitEnvelope ->
                        // Retention is committed here, not on `envelope.Kind`
                        // after the fact — this keeps the generic FSI-tail-
                        // value path (below) untouched even when it also
                        // happens to produce a Table-kind envelope. See
                        // docs/superpowers/specs/2026-08-12-results-export-design.md.
                        //
                        // Guarded on a non-empty DocumentUri: every real
                        // caller populates it, but `ResultRetention.set`
                        // could otherwise throw on a null key inside this
                        // backgrounded task, silently hanging the request
                        // with no response frame ever written.
                        if
                            explicitEnvelope.Kind = ResultKind.Table
                            && not (System.String.IsNullOrEmpty evalParams.DocumentUri)
                        then
                            ResultRetention.set
                                evalParams.DocumentUri
                                { Columns = explicitEnvelope.Schema |> List.map (fun c -> c.Name)
                                  Rows = explicitEnvelope.Rows }

                        explicitEnvelope
                    | None ->
                        let value =
                            outcome.Value |> Option.map (fun v -> v.ReflectionValue) |> Option.toObj

                        let projected, cursor = Projection.projectWithCursor value stopwatch.ElapsedMilliseconds

                        // The generic FSI-tail-value path never retains a
                        // result of its own (see the comment above), but it
                        // can still land on `ResultKind.Table` — any record
                        // or `IEnumerable` tail value does. Left alone, the
                        // Export button (enabled whenever `Kind = Table`,
                        // regardless of which path produced it) would
                        // either silently export nothing for a fresh tab or
                        // silently export a stale, earlier `Results.table`
                        // push for a tab that ran one before. Discarding
                        // here collapses both into the honest
                        // "nothing to export" case. Same DocumentUri guard
                        // as above.
                        if
                            projected.Kind = ResultKind.Table
                            && not (System.String.IsNullOrEmpty evalParams.DocumentUri)
                        then
                            ResultRetention.discard evalParams.DocumentUri

                        let continuationToken =
                            cursor
                            |> Option.map (fun c ->
                                let token = Guid.NewGuid().ToString("N")
                                currentPaging <- Some(token, c)
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

            let! items =
                FsacBridge.complete
                    completionParams.DocumentUri
                    completionParams.Version
                    completionParams.Source
                    completionParams.Line
                    completionParams.Column

            let result: AnalysisCompletionResult = { Items = items }

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

        match currentPaging with
        | Some(token, cursor) when token = pageParams.ContinuationToken ->
            let rows, hasMore = cursor.TakePage(Projection.PreviewRowLimit)
            currentPaging <- if hasMore then Some(token, cursor) else None

            let page: ResultPage =
                { Rows = rows |> Array.map (fun r -> [| r |])
                  IsTruncated = hasMore
                  ContinuationToken = (if hasMore then Some token else None) }

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

    /// `connection/register` — Section 7. Persists `connection` to
    /// `workspace.json` via `Workspace.registerConnection`, replacing any
    /// existing binding sharing its `Id`, and returns the updated full list.
    let handleConnectionRegister (request: WorkerRequestFrame) : WorkerResponseFrame =
        let connection =
            JsonSerializer.Deserialize<ConnectionDefinition>(request.Params, Serialization.options)

        let updated = Workspace.registerConnection (workspaceDir ()) connection

        { Id = request.Id
          Result = Some(JsonSerializer.SerializeToElement(updated, Serialization.options))
          Error = None }

    /// `connection/list` — the read-only companion to `connection/register` /
    /// `connection/remove` (F5). Neither of those returns the registry without
    /// mutating it, and the extension's connections TreeView needs the list on
    /// notebook/view open. A thin projection of `Workspace.readConnections`.
    let handleConnectionList (request: WorkerRequestFrame) : WorkerResponseFrame =
        let connections = Workspace.readConnections (workspaceDir ())

        { Id = request.Id
          Result = Some(JsonSerializer.SerializeToElement({| connections = connections |}, Serialization.options))
          Error = None }

    /// `connection/remove` — drops the connection with the given `Id`
    /// from `workspace.json` via `Workspace.removeConnection` and returns
    /// the updated full list. Deleting the credential itself (if the
    /// removed connection had a `SecretReference`) is the App layer's
    /// job, same rationale as `connection/register` saving a plaintext
    /// secret before ever reaching the worker — the worker only ever
    /// touches `workspace.json`, never the OS credential store.
    let handleConnectionRemove (request: WorkerRequestFrame) : WorkerResponseFrame =
        let removeParams =
            JsonSerializer.Deserialize<ConnectionRemoveParams>(request.Params, Serialization.options)

        let updated = Workspace.removeConnection (workspaceDir ()) removeParams.Id

        { Id = request.Id
          Result = Some(JsonSerializer.SerializeToElement(updated, Serialization.options))
          Error = None }

    /// `connection/schema` — Section 10's schema discovery, wired to the
    /// UI for the first time: resolves the registered connection named by
    /// `Id` (the same `WorkspaceConnectionResolver` `runEvaluate` already
    /// uses for `Connections.*`), then calls its own `DiscoverSchemaAsync`
    /// and maps the result to the wire-facing `SchemaTable`/`SchemaColumn`
    /// shape. A `connection/unknown` error for an `Id` nothing is
    /// registered under; a `connection/schemaFailed` error — not an
    /// unhandled exception taking the read loop down — for a real
    /// discovery failure (bad credentials, unreachable server, missing
    /// file), since unlike `Connections.sql` this runs before any script
    /// exists to catch its own exception.
    let handleConnectionSchema (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            let schemaParams =
                JsonSerializer.Deserialize<ConnectionSchemaParams>(request.Params, Serialization.options)

            let connections = Workspace.readConnections (workspaceDir ())

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
                        WorkspaceConnectionResolver([ definition ], WindowsCredentialStore()) :> IConnectionResolver

                    let connector = resolver.Resolve(definition.Name)
                    let! tables = connector.DiscoverSchemaAsync(CancellationToken.None)

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
            | "connection/list" -> return handleConnectionList request
            | "connection/register" -> return handleConnectionRegister request
            | "connection/remove" -> return handleConnectionRemove request
            | "connection/schema" -> return! handleConnectionSchema request
            | unknown ->
                return
                    { Id = request.Id
                      Result = None
                      Error =
                        Some
                            { Code = "method/unknown"
                              Message = $"Unknown method: {unknown}" } }
        }
