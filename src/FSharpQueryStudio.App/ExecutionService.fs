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
    /// (which needs that id) to a UI cancel command. See A.17.
    let evaluateWithId (client: WorkerClient) (requestId: string) (source: string) : Task<ResultEnvelope> =
        task {
            let request: WorkerRequestFrame =
                { Id = requestId
                  Method = "execution/evaluate"
                  Params = JsonSerializer.SerializeToElement<EvaluateParams>({ Source = source }, Serialization.options) }

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

    let evaluate (client: WorkerClient) (source: string) : Task<ResultEnvelope> =
        evaluateWithId client (Guid.NewGuid().ToString()) source
