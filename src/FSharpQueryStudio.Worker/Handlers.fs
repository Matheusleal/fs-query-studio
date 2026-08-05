namespace FSharpQueryStudio.Worker

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Text.Json
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

    /// Isolated by default (Section 9): every call gets a fresh session in
    /// a fresh temp working directory. An uncaught script exception becomes
    /// a structured Error result (Section 11), never an unhandled crash —
    /// EvalInteractionNonThrowing already keeps it from propagating.
    let handleEvaluate (request: WorkerRequestFrame) : WorkerResponseFrame =
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
                let value = outcome.Value |> Option.map (fun v -> v.ReflectionValue) |> Option.toObj
                Projection.project value stopwatch.ElapsedMilliseconds

        { Id = request.Id
          Result = Some(JsonSerializer.SerializeToElement(envelope, Serialization.options))
          Error = None }

    let dispatch (request: WorkerRequestFrame) : WorkerResponseFrame =
        match request.Method with
        | "worker/health" -> handleHealth request
        | "execution/evaluate" -> handleEvaluate request
        | unknown ->
            { Id = request.Id
              Result = None
              Error =
                Some
                    { Code = "method/unknown"
                      Message = $"Unknown method: {unknown}" } }
