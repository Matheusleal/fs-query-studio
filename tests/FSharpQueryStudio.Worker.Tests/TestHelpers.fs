module TestHelpers

open System
open System.Threading.Tasks

let withTimeout (seconds: float) (work: Task<'T>) : Task<'T> =
    let timeoutSec = min seconds 45.0
    task {
        let! winner = Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds timeoutSec))

        if winner = (work :> Task) then
            return! work
        else
            return failwith $"timed out after {timeoutSec}s"
    }
