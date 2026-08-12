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
open FSharpQueryStudio.Analysis
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

    /// Every `analysis/*` handler anchors its throwaway `AnalysisContext`
    /// at a fresh temp directory. Nothing is ever written into it (the
    /// source is handed to FCS as a `SourceText`, never as a file), but
    /// the directory itself has to exist for `GetProjectOptionsFromScript`
    /// to resolve against it — so it is created per request and, unlike
    /// before, deleted again when the request finishes.
    let private createAnalysisWorkDir () : string =
        let workDir =
            Path.Combine(Path.GetTempPath(), "fqs-analysis-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory(workDir) |> ignore
        workDir

    /// Best-effort cleanup: a failed delete (a locked file, a racing
    /// antivirus scan) must never mask the analysis result or throw past
    /// the handler's own `finally`.
    let private deleteAnalysisWorkDir (workDir: string) : unit =
        try
            Directory.Delete(workDir, true)
        with ex ->
            Log.Debug(ex, "Could not delete the analysis working directory {WorkingDirectory}", workDir)

    /// Parses/type-checks the request's source snapshot via FCS, in a fresh
    /// analysis context per call (Section 8, step 3-4) — the context is
    /// cheap to (re)build and this stays stateless, unlike `runEvaluate`'s
    /// process-wide FSI session.
    let handleAnalysisCheck (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            let checkParams =
                JsonSerializer.Deserialize<AnalysisCheckParams>(request.Params, Serialization.options)

            let workDir = createAnalysisWorkDir ()

            try
                let context = AnalysisContext.build workDir
                let! diagnostics = Checker.check context checkParams.Source

                let result: AnalysisCheckResult =
                    { Diagnostics = diagnostics |> Array.map DiagnosticMapping.map |> Array.toList }

                return
                    { Id = request.Id
                      Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
                      Error = None }
            finally
                deleteAnalysisWorkDir workDir
        }

    /// Answers `analysis/hover` from the request's own source snapshot, in
    /// a fresh analysis context per call — same stateless shape as
    /// `handleAnalysisCheck`. `Hover = None` (nothing at that position) is
    /// a normal, successful `Result`, never an `Error`.
    let handleAnalysisHover (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            let hoverParams =
                JsonSerializer.Deserialize<AnalysisHoverParams>(request.Params, Serialization.options)

            let workDir = createAnalysisWorkDir ()

            try
                let context = AnalysisContext.build workDir
                let! hover = Hover.hover context hoverParams.Source hoverParams.Line hoverParams.Column

                let result: AnalysisHoverResult = { Hover = hover }

                return
                    { Id = request.Id
                      Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
                      Error = None }
            finally
                deleteAnalysisWorkDir workDir
        }

    /// Answers `analysis/definition` from the request's own source
    /// snapshot, in a fresh analysis context per call — same stateless
    /// shape as `handleAnalysisHover`. `Location = None` (nothing
    /// resolvable at that position) is a normal, successful `Result`,
    /// never an `Error`.
    let handleAnalysisDefinition (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            let definitionParams =
                JsonSerializer.Deserialize<AnalysisDefinitionParams>(request.Params, Serialization.options)

            let workDir = createAnalysisWorkDir ()

            try
                let context = AnalysisContext.build workDir

                let! location =
                    GoToDefinition.findDefinition
                        context
                        definitionParams.Source
                        definitionParams.Line
                        definitionParams.Column

                // A same-file declaration comes back carrying the analysis
                // context's own throwaway script path (a per-request
                // fqs-analysis-<guid> temp file that never existed on disk
                // and is deleted below). That path is meaningless to the
                // UI and can never equal the document URI the request came
                // from, so the editor's "is this the document I'm looking
                // at?" check could never match. Rewriting it here keeps
                // that temp identity off the wire entirely.
                let location =
                    location
                    |> Option.map (fun l ->
                        if l.FilePath = context.ScriptPath then
                            { l with FilePath = definitionParams.DocumentUri }
                        else
                            l)

                let result: AnalysisDefinitionResult = { Location = location }

                return
                    { Id = request.Id
                      Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
                      Error = None }
            finally
                deleteAnalysisWorkDir workDir
        }

    /// Answers `analysis/signatureHelp` from the request's own source
    /// snapshot, in a fresh analysis context per call — same stateless
    /// shape as `handleAnalysisHover`/`handleAnalysisDefinition`.
    /// `SignatureHelp = None` (not a callable symbol, or nothing
    /// resolvable there) is a normal, successful `Result`, never an
    /// `Error`.
    let handleAnalysisSignatureHelp (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            let signatureHelpParams =
                JsonSerializer.Deserialize<AnalysisSignatureHelpParams>(request.Params, Serialization.options)

            let workDir = createAnalysisWorkDir ()

            try
                let context = AnalysisContext.build workDir

                let! signatureHelp =
                    SignatureHelp.signatureHelp
                        context
                        signatureHelpParams.Source
                        signatureHelpParams.Line
                        signatureHelpParams.Column

                let result: AnalysisSignatureHelpResult = { SignatureHelp = signatureHelp }

                return
                    { Id = request.Id
                      Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
                      Error = None }
            finally
                deleteAnalysisWorkDir workDir
        }

    /// `analysis/complete` — Milestone B.10/B.11. Merges tier 1 (static,
    /// position-insensitive) and tier 2 (semantic, FCS-derived at the
    /// cursor) into one de-duplicated list. `recentNames` (tier 1's
    /// "names from the most recent successful check") is `[]` here —
    /// nothing upstream tracks a document's last-checked declared names
    /// yet; tracked as a known, accepted limitation, not silently
    /// dropped scope (see this plan's Self-Review Notes).
    let handleAnalysisComplete (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            let completionParams =
                JsonSerializer.Deserialize<AnalysisCompletionParams>(request.Params, Serialization.options)

            let workDir = createAnalysisWorkDir ()

            try
                let context = AnalysisContext.build workDir

                let! semantic =
                    Completions.completeAtPosition
                        context
                        completionParams.Source
                        completionParams.Line
                        completionParams.Column

                let items = (Completions.complete [] @ semantic) |> List.distinct
                let result: AnalysisCompletionResult = { Items = items }

                return
                    { Id = request.Id
                      Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
                      Error = None }
            finally
                deleteAnalysisWorkDir workDir
        }

    /// `analysis/rename` — Milestone E.8. `Analysis.Rename` only locates
    /// references; this handler's own job is mapping its internal
    /// `RenameEdit` to the protocol-level one, the same separation
    /// `DiagnosticMapping` keeps for `analysis/check`.
    let private toProtocolRenameEdit (edit: FSharpQueryStudio.Analysis.Rename.RenameEdit) : RenameEdit =
        { StartLine = edit.StartLine
          StartColumn = edit.StartColumn
          EndLine = edit.EndLine
          EndColumn = edit.EndColumn }

    let handleAnalysisRename (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            let renameParams =
                JsonSerializer.Deserialize<AnalysisRenameParams>(request.Params, Serialization.options)

            let workDir = createAnalysisWorkDir ()

            try
                let context = AnalysisContext.build workDir

                let! edits =
                    Rename.findReferences context renameParams.Source renameParams.Line renameParams.Column

                let result: AnalysisRenameResult =
                    { Edits = edits |> Option.map (List.map toProtocolRenameEdit) }

                return
                    { Id = request.Id
                      Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
                      Error = None }
            finally
                deleteAnalysisWorkDir workDir
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
