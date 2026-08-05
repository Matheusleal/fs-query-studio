module TestHelpers

open System
open System.IO
open System.Threading.Tasks
open FSharpQueryStudio.App
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Ui

let workerDllPath () =
    Path.Combine(AppContext.BaseDirectory, "FSharpQueryStudio.Worker.dll")

let withTimeout (seconds: float) (work: Task<'T>) : Task<'T> =
    task {
        let! winner = Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds seconds))

        if winner = (work :> Task) then
            return! work
        else
            return failwith $"timed out after {seconds}s waiting for the worker process"
    }

let request (id: string) (method_: string) (paramsJson: string) : WorkerRequestFrame =
    { Id = id
      Method = method_
      Params = Text.Json.JsonDocument.Parse(paramsJson).RootElement }

/// Wires `ExecuteAsync`/`CancelAsync` the way the App composition root
/// does: `CancelAsync` closes over whatever request id the most recent
/// `ExecuteAsync` call sent, so `shell.Cancel()` cancels the evaluation
/// actually in flight without `ShellViewModel` itself knowing request ids
/// exist.
let wireShell (gracePeriod: TimeSpan) (client: WorkerClient) (shell: ShellViewModel) =
    let currentRequestId = ref None

    shell.ExecuteAsync <-
        Some(fun source ->
            task {
                let id = Guid.NewGuid().ToString()
                currentRequestId.Value <- Some id
                let! result = ExecutionService.evaluateWithId client id source
                currentRequestId.Value <- None
                return result
            })

    shell.CancelAsync <-
        Some(fun () ->
            task {
                match currentRequestId.Value with
                | Some id -> do! client.Cancel(id, gracePeriod)
                | None -> ()
            })
