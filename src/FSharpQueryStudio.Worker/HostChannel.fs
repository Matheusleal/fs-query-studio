namespace FSharpQueryStudio.Worker

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open System.Text.Json
open FSharpQueryStudio.Protocol

/// The worker-initiated request path (F5). The worker normally only
/// answers frames; `BridgeSecretStore` needs it to *ask* the host for a
/// secret. `Request` writes a `WorkerRequestFrame` with a `host-`-prefixed
/// id (distinct from every host-minted id) and hands back a task the read
/// loop completes via `TryComplete` when the matching response frame
/// arrives. `send` is supplied by `Program.fs` and writes through the same
/// stdout `writeLock` every response uses.
type HostChannel(send: WorkerRequestFrame -> Task) =
    let pending = ConcurrentDictionary<string, TaskCompletionSource<WorkerResponseFrame>>()

    member _.Request(method_: string, paramsValue: obj) : Task<WorkerResponseFrame> =
        let id = "host-" + Guid.NewGuid().ToString("N")

        let tcs =
            TaskCompletionSource<WorkerResponseFrame>(TaskCreationOptions.RunContinuationsAsynchronously)

        pending.[id] <- tcs

        let frame: WorkerRequestFrame =
            { Id = id
              Method = method_
              Params = JsonSerializer.SerializeToElement(paramsValue, Serialization.options) }

        (send frame)
            .ContinueWith(fun (t: Task) ->
                if t.IsFaulted then
                    match pending.TryRemove id with
                    | true, waiter -> waiter.TrySetException(t.Exception.InnerExceptions :> seq<exn>) |> ignore
                    | _ -> ())
        |> ignore

        tcs.Task

    member _.TryComplete(frame: WorkerResponseFrame) : bool =
        match pending.TryRemove frame.Id with
        | true, tcs -> tcs.TrySetResult frame
        | _ -> false
