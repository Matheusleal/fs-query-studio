namespace FSharpQueryStudio.Worker

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Text.Json
open System.Threading
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Execution
open FSharpQueryStudio.Results

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
    /// evaluation would corrupt, and CancellationSignal is a single flag.
    let private evaluationInFlight = ref 0

    let private busyResponse (id: string) : WorkerResponseFrame =
        { Id = id
          Result = None
          Error =
            Some
                { Code = "execution/busy"
                  Message = "Another evaluation is already in progress." } }

    /// Isolated by default (Section 9): every call gets a fresh session in
    /// a fresh temp working directory. An uncaught script exception becomes
    /// a structured Error result (Section 11), never an unhandled crash —
    /// EvalInteractionNonThrowing already keeps it from propagating.
    let handleEvaluate (request: WorkerRequestFrame) : WorkerResponseFrame =
        if Interlocked.CompareExchange(evaluationInFlight, 1, 0) <> 0 then
            busyResponse request.Id
        else
            try
                CancellationSignal.reset ()

                let evalParams =
                    JsonSerializer.Deserialize<EvaluateParams>(request.Params, Serialization.options)

                let workDir =
                    Path.Combine(Path.GetTempPath(), "fqs-exec-" + Guid.NewGuid().ToString("N"))

                Directory.CreateDirectory(workDir) |> ignore

                let stopwatch = Stopwatch.StartNew()
                let session = FsiSession.create workDir
                let outcome = Evaluation.evaluate session evalParams.Source
                stopwatch.Stop()

                let envelope =
                    match outcome.Exception with
                    | Some ex -> Projection.projectError ex stopwatch.ElapsedMilliseconds
                    | None ->
                        let value =
                            outcome.Value |> Option.map (fun v -> v.ReflectionValue) |> Option.toObj

                        Projection.project value stopwatch.ElapsedMilliseconds

                { Id = request.Id
                  Result = Some(JsonSerializer.SerializeToElement(envelope, Serialization.options))
                  Error = None }
            finally
                Interlocked.Exchange(evaluationInFlight, 0) |> ignore

    /// Requests cooperative cancellation of whatever evaluation is
    /// currently running (a no-op if none is) and acknowledges immediately
    /// — see Section 9's two-step cancellation policy. The evaluate
    /// request's own response arrives separately, whenever it completes.
    let handleCancel (request: WorkerRequestFrame) : WorkerResponseFrame =
        CancellationSignal.requestCancel ()

        { Id = request.Id
          Result = Some(JsonSerializer.SerializeToElement({| acknowledged = true |}, Serialization.options))
          Error = None }

    let dispatch (request: WorkerRequestFrame) : WorkerResponseFrame =
        match request.Method with
        | "worker/health" -> handleHealth request
        | "execution/evaluate" -> handleEvaluate request
        | "execution/cancel" -> handleCancel request
        | unknown ->
            { Id = request.Id
              Result = None
              Error =
                Some
                    { Code = "method/unknown"
                      Message = $"Unknown method: {unknown}" } }
