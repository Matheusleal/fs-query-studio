namespace FSharpQueryStudio.Worker

open System.Reflection
open System.Text.Json
open FSharpQueryStudio.Protocol

/// Method dispatch for incoming WorkerRequestFrames — see Section 7's
/// method table. Each handler is pure: frame in, frame out.
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

    let dispatch (request: WorkerRequestFrame) : WorkerResponseFrame =
        match request.Method with
        | "worker/health" -> handleHealth request
        | unknown ->
            { Id = request.Id
              Result = None
              Error =
                Some
                    { Code = "method/unknown"
                      Message = $"Unknown method: {unknown}" } }
