module TestHelpers

open System
open System.IO
open System.Threading.Tasks
open FSharpQueryStudio.Protocol

let workerDllPath () =
    Path.Combine(AppContext.BaseDirectory, "FSharpQueryStudio.Worker.dll")

let withTimeout (seconds: float) (work: Task<'T>) : Task<'T> =
    let timeoutSec = min seconds 45.0
    task {
        let! winner = Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds timeoutSec))

        if winner = (work :> Task) then
            return! work
        else
            return failwith $"timed out after {timeoutSec}s waiting for the worker process"
    }

let request (id: string) (method_: string) (paramsJson: string) : WorkerRequestFrame =
    { Id = id
      Method = method_
      Params = Text.Json.JsonDocument.Parse(paramsJson).RootElement }
