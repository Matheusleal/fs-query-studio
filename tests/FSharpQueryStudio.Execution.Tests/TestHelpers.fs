module TestHelpers

open System
open System.IO
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

let tempWorkingDirectory () =
    let dir = Path.Combine(Path.GetTempPath(), "fqs-fsi-tests-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    dir

/// `FsiSession.create` calls Directory.SetCurrentDirectory, which on
/// Windows locks that directory against deletion for as long as it's the
/// process's current directory. Tests must point the current directory
/// elsewhere before cleaning up their temp directory.
let cleanupWorkingDirectory (dir: string) =
    try
        Directory.SetCurrentDirectory(Path.GetTempPath())
        if Directory.Exists(dir) then
            Directory.Delete(dir, true)
    with _ ->
        ()
    GC.Collect()
    GC.WaitForPendingFinalizers()
    GC.Collect()

