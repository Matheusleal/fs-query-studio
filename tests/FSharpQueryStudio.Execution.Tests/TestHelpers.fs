module TestHelpers

open System
open System.IO
open System.Threading.Tasks

let withTimeout (seconds: float) (work: Task<'T>) : Task<'T> =
    task {
        let! winner = Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds seconds))

        if winner = (work :> Task) then
            return! work
        else
            return failwith $"timed out after {seconds}s"
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
    Directory.SetCurrentDirectory(Path.GetTempPath())
    Directory.Delete(dir, true)
