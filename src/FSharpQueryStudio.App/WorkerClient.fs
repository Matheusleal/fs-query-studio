namespace FSharpQueryStudio.App

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Text.Json
open System.Threading.Tasks
open Serilog
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Worker

/// Owns the worker process from the host's side — see Section 9's crash-
/// and-restart ownership. The worker never restarts itself: this type
/// detects its exit (an unexpectedly closed stdout stream — `readFrame`
/// returning `None` is never a normal outcome for a healthy worker's
/// request loop) and starts a fresh process on the next `Send`, rather
/// than retrying the failed request automatically.
///
/// Responses can arrive out of order (execution/evaluate runs on the
/// worker's own background task — see Program.fs — so an
/// execution/cancel sent after it can be acknowledged first), so a
/// background reader loop matches each response to its request by `Id`
/// rather than assuming "next line in" answers "last line out".
type WorkerClient(workerDllPath: string) =
    let mutable currentProcess: Process option = None
    let pending = ConcurrentDictionary<string, TaskCompletionSource<WorkerResponseFrame>>()

    let crashedResponse (id: string) : WorkerResponseFrame =
        { Id = id
          Result = None
          Error =
            Some
                { Code = "worker/crashed"
                  Message = "The worker process exited before responding." } }

    let forciblyStoppedResponse (id: string) : WorkerResponseFrame =
        { Id = id
          Result = None
          Error =
            Some
                { Code = "execution/forciblyStopped"
                  Message = "The worker did not stop cooperatively within the grace period and was terminated." } }

    let failAllPending (response: string -> WorkerResponseFrame) =
        for id in pending.Keys |> Seq.toList do
            match pending.TryRemove(id) with
            | true, tcs -> tcs.TrySetResult(response id) |> ignore
            | false, _ -> ()

    let startReaderLoop (proc: Process) =
        Task.Run(fun () ->
            (task {
                let mutable keepGoing = true

                while keepGoing do
                    let! frame = NdjsonStream.readFrame<WorkerResponseFrame> proc.StandardOutput

                    match frame with
                    | Some response ->
                        match pending.TryRemove(response.Id) with
                        | true, tcs -> tcs.TrySetResult(response) |> ignore
                        | false, _ -> () // no one is waiting on this id anymore
                    | None ->
                        keepGoing <- false
                        currentProcess <- None
                        Log.Warning("Worker process exited unexpectedly")
                        failAllPending crashedResponse
            })
            :> Task)
        |> ignore

    let startProcess () =
        let psi =
            ProcessStartInfo(
                FileName = "dotnet",
                Arguments = $"\"{workerDllPath}\"",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            )

        let p = new Process(StartInfo = psi)
        p.Start() |> ignore
        Log.Information("Worker process started with pid {Pid}", p.Id)
        startReaderLoop p
        p

    let ensureProcess () =
        match currentProcess with
        | Some existing when not existing.HasExited -> existing
        | _ ->
            let started = startProcess ()
            currentProcess <- Some started
            started

    /// The active worker process, if one is currently running. Exposed for
    /// diagnostics/tests; production callers should only need `Send`/`Cancel`.
    member _.CurrentProcess = currentProcess

    member _.Send(request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            let proc = ensureProcess ()
            let tcs = TaskCompletionSource<WorkerResponseFrame>(TaskCreationOptions.RunContinuationsAsynchronously)
            pending.[request.Id] <- tcs
            do! NdjsonStream.writeFrame proc.StandardInput request
            return! tcs.Task
        }

    /// Section 9's two-step cancellation policy: send a cooperative
    /// execution/cancel for `requestId`, then wait up to `gracePeriod` for
    /// its own response to complete normally. If it doesn't, the worker is
    /// judged non-cooperative and killed; `requestId`'s pending caller gets
    /// a "forcibly stopped" result instead of hanging forever.
    member this.Cancel(requestId: string, gracePeriod: TimeSpan) : Task<unit> =
        task {
            match currentProcess with
            | None -> ()
            | Some proc ->
                let cancelRequest: WorkerRequestFrame =
                    { Id = Guid.NewGuid().ToString()
                      Method = "execution/cancel"
                      Params = JsonDocument.Parse("{}").RootElement }

                let! _ack = this.Send(cancelRequest)

                match pending.TryGetValue(requestId) with
                | false, _ -> () // already completed
                | true, originalTcs ->
                    let! winner = Task.WhenAny(originalTcs.Task :> Task, Task.Delay(gracePeriod))

                    if winner <> (originalTcs.Task :> Task) then
                        match pending.TryRemove(requestId) with
                        | false, _ -> () // completed in the gap
                        | true, tcs ->
                            Log.Warning(
                                "The worker did not stop cooperatively within the grace period and was terminated"
                            )

                            proc.Kill()
                            tcs.TrySetResult(forciblyStoppedResponse requestId) |> ignore
        }
