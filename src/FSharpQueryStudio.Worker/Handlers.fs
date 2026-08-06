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
    /// visible. An uncaught script exception becomes a structured Error
    /// result (Section 11), never an unhandled crash —
    /// EvalInteractionNonThrowing already keeps it from propagating.
    let runEvaluate (request: WorkerRequestFrame) (workDir: string) : WorkerResponseFrame =
        try
            let evalParams =
                JsonSerializer.Deserialize<EvaluateParams>(request.Params, Serialization.options)

            let stopwatch = Stopwatch.StartNew()

            let session =
                match evalParams.Mode, evalParams.SessionId with
                | Persistent, Some sessionId ->
                    Log.Information("Reusing persistent FSI session {SessionId} in {WorkingDirectory}", sessionId, workDir)
                    SessionStore.getOrCreate sessionId workDir
                | Persistent, None ->
                    Log.Warning("Persistent mode requested without a SessionId; falling back to an isolated session")
                    FsiSession.create workDir
                | Isolated, _ ->
                    Log.Information("Creating FSI session in {WorkingDirectory}", workDir)
                    FsiSession.create workDir

            let outcome = Evaluation.evaluate session evalParams.Source
            stopwatch.Stop()

            let output = Projection.toOutputLines outcome.Output outcome.ErrorOutput

            let envelope =
                match outcome.Exception with
                | Some ex -> { Projection.projectError ex stopwatch.ElapsedMilliseconds with Output = output }
                | None ->
                    let value =
                        outcome.Value |> Option.map (fun v -> v.ReflectionValue) |> Option.toObj

                    let projected, cursor = Projection.projectWithCursor value stopwatch.ElapsedMilliseconds

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

    /// Parses/type-checks the request's source snapshot via FCS, in a fresh
    /// analysis context per call (Section 8, step 3-4) — the context is
    /// cheap to (re)build and this stays stateless, unlike `runEvaluate`'s
    /// process-wide FSI session.
    let handleAnalysisCheck (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            let checkParams =
                JsonSerializer.Deserialize<AnalysisCheckParams>(request.Params, Serialization.options)

            let workDir =
                Path.Combine(Path.GetTempPath(), "fqs-analysis-" + Guid.NewGuid().ToString("N"))

            Directory.CreateDirectory(workDir) |> ignore
            let context = AnalysisContext.build workDir
            let! diagnostics = Checker.check context checkParams.Source

            let result: AnalysisCheckResult =
                { Diagnostics = diagnostics |> Array.map DiagnosticMapping.map |> Array.toList }

            return
                { Id = request.Id
                  Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
                  Error = None }
        }

    /// Answers `analysis/hover` from the request's own source snapshot, in
    /// a fresh analysis context per call — same stateless shape as
    /// `handleAnalysisCheck`. `Hover = None` (nothing at that position) is
    /// a normal, successful `Result`, never an `Error`.
    let handleAnalysisHover (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            let hoverParams =
                JsonSerializer.Deserialize<AnalysisHoverParams>(request.Params, Serialization.options)

            let workDir =
                Path.Combine(Path.GetTempPath(), "fqs-analysis-" + Guid.NewGuid().ToString("N"))

            Directory.CreateDirectory(workDir) |> ignore
            let context = AnalysisContext.build workDir
            let! hover = Hover.hover context hoverParams.Source hoverParams.Line hoverParams.Column

            let result: AnalysisHoverResult = { Hover = hover }

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

    let dispatch (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            match request.Method with
            | "worker/health" -> return handleHealth request
            | "execution/evaluate" -> return handleEvaluate request
            | "execution/cancel" -> return handleCancel request
            | "analysis/check" -> return! handleAnalysisCheck request
            | "analysis/hover" -> return! handleAnalysisHover request
            | "results/page" -> return handleResultsPage request
            | unknown ->
                return
                    { Id = request.Id
                      Result = None
                      Error =
                        Some
                            { Code = "method/unknown"
                              Message = $"Unknown method: {unknown}" } }
        }
