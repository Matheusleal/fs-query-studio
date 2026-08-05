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
            Choice1Of2 workDir

    /// Isolated by default (Section 9): a fresh session in the reserved
    /// working directory. An uncaught script exception becomes a
    /// structured Error result (Section 11), never an unhandled crash —
    /// EvalInteractionNonThrowing already keeps it from propagating.
    let runEvaluate (request: WorkerRequestFrame) (workDir: string) : WorkerResponseFrame =
        try
            let evalParams =
                JsonSerializer.Deserialize<EvaluateParams>(request.Params, Serialization.options)

            let stopwatch = Stopwatch.StartNew()
            Log.Information("Creating FSI session in {WorkingDirectory}", workDir)
            let session = FsiSession.create workDir
            let outcome = Evaluation.evaluate session evalParams.Source
            stopwatch.Stop()

            let output = Projection.toOutputLines outcome.Output outcome.ErrorOutput

            let envelope =
                match outcome.Exception with
                | Some ex -> { Projection.projectError ex stopwatch.ElapsedMilliseconds with Output = output }
                | None ->
                    let value =
                        outcome.Value |> Option.map (fun v -> v.ReflectionValue) |> Option.toObj

                    { Projection.project value stopwatch.ElapsedMilliseconds with
                        Output = output }

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

    let dispatch (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            match request.Method with
            | "worker/health" -> return handleHealth request
            | "execution/evaluate" -> return handleEvaluate request
            | "execution/cancel" -> return handleCancel request
            | "analysis/check" -> return! handleAnalysisCheck request
            | unknown ->
                return
                    { Id = request.Id
                      Result = None
                      Error =
                        Some
                            { Code = "method/unknown"
                              Message = $"Unknown method: {unknown}" } }
        }
