namespace FSharpQueryStudio.App

open System.Diagnostics
open System.Threading.Tasks
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Worker

/// Owns the worker process from the host's side — see Section 9's crash-
/// and-restart ownership. The worker never restarts itself: this type
/// detects its exit (an unexpectedly closed stdout stream — `readFrame`
/// returning `None` after we sent something is never a normal outcome for
/// a healthy worker) and starts a fresh process on the next `Send`, rather
/// than retrying the failed request automatically.
type WorkerClient(workerDllPath: string) =
    let mutable currentProcess: Process option = None

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
        p

    let crashedResponse (id: string) : WorkerResponseFrame =
        { Id = id
          Result = None
          Error =
            Some
                { Code = "worker/crashed"
                  Message = "The worker process exited before responding." } }

    /// The active worker process, if one is currently running. Exposed for
    /// diagnostics/tests; production callers should only need `Send`.
    member _.CurrentProcess = currentProcess

    member _.Send(request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            let proc =
                match currentProcess with
                | Some existing when not existing.HasExited -> existing
                | _ ->
                    let started = startProcess ()
                    currentProcess <- Some started
                    started

            do! NdjsonStream.writeFrame proc.StandardInput request
            let! response = NdjsonStream.readFrame<WorkerResponseFrame> proc.StandardOutput

            match response with
            | Some r -> return r
            | None ->
                currentProcess <- None
                return crashedResponse request.Id
        }
