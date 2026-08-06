namespace FSharpQueryStudio.App

open System
open System.Text.Json
open System.Threading.Tasks
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Results

/// The "host service" between a Ui command and the worker — see Section
/// 4's App responsibility and Section 7's execution/evaluate method.
/// Turns a script's source text into a bounded ResultEnvelope, whatever
/// the worker actually did: succeeded, faulted the script (Section 11's
/// Error kind, already projected by the worker), or never got to run at
/// all because the process crashed or was busy (Section 9's
/// WorkerErrorInfo, projected here since the worker never produced a
/// Result to begin with).
module ExecutionService =

    /// Same as `evaluate`, but with the request id supplied by the caller
    /// instead of generated internally — lets a composition root track
    /// which request is in flight so it can wire `WorkerClient.Cancel`
    /// (which needs that id) to a UI cancel command. See A.17. Takes the
    /// full `EvaluateParams` (E.1/E.2) so `ShellViewModel.RunAsync`'s
    /// choice of `SessionMode`/`SessionId` reaches the worker unchanged,
    /// rather than this service silently forcing Isolated.
    let evaluateWithId (client: WorkerClient) (requestId: string) (evalParams: EvaluateParams) : Task<ResultEnvelope> =
        task {
            let request: WorkerRequestFrame =
                { Id = requestId
                  Method = "execution/evaluate"
                  Params = JsonSerializer.SerializeToElement<EvaluateParams>(evalParams, Serialization.options) }

            let! response = client.Send(request)

            match response.Result, response.Error with
            | Some resultJson, _ -> return JsonSerializer.Deserialize<ResultEnvelope>(resultJson, Serialization.options)
            | None, Some workerError -> return Projection.projectWorkerError workerError
            | None, None ->
                return
                    Projection.projectWorkerError
                        { Code = "execution/unknown"
                          Message = "The worker returned neither a result nor an error." }
        }

    let evaluate (client: WorkerClient) (evalParams: EvaluateParams) : Task<ResultEnvelope> =
        evaluateWithId client (Guid.NewGuid().ToString()) evalParams

    /// `session/reset` (E.2) — drops the worker-side Persistent-mode
    /// session tracked under `sessionId`, if any. Fire-and-forget from the
    /// UI's perspective: the worker acknowledges unconditionally (a no-op
    /// is not an error), so there's no `Result`/`Error` branch to project
    /// here the way `evaluateWithId` needs one.
    let resetSession (client: WorkerClient) (sessionId: string) : Task<unit> =
        task {
            let request: WorkerRequestFrame =
                { Id = Guid.NewGuid().ToString()
                  Method = "session/reset"
                  Params =
                    JsonSerializer.SerializeToElement<SessionResetParams>(
                        { SessionId = sessionId },
                        Serialization.options
                    ) }

            let! _response = client.Send(request)
            return ()
        }
