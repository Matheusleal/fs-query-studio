namespace FSharpQueryStudio.Execution

open System.IO

/// Cooperative cancellation for the currently running evaluation — see
/// Section 9. Backed by a sentinel file in the evaluation's working
/// directory, not a static field: an earlier version used a static bool
/// that a script observed by calling back into this assembly via a
/// `--reference:` self-load, but `FsiEvaluationSession`'s `collectible`
/// AssemblyLoadContext sometimes loads its own separate copy of an
/// explicitly path-referenced assembly rather than reusing the one
/// already loaded in the process — giving the script its own independent
/// static field, disconnected from the one the host mutates, so
/// cancellation silently never took effect. A file is OS-level and has
/// no assembly identity to get out of sync: the script just calls
/// `System.IO.File.Exists(".cancel-requested")` directly (relative to
/// its CWD, which `FsiSession.create` already points at the working
/// directory this module uses) — no reference to this assembly needed.
module CancellationSignal =

    [<Literal>]
    let SentinelFileName = ".cancel-requested"

    let private sentinelPath (workingDirectory: string) =
        Path.Combine(workingDirectory, SentinelFileName)

    let isCancelled (workingDirectory: string) : bool =
        File.Exists(sentinelPath workingDirectory)

    let requestCancel (workingDirectory: string) : unit =
        File.WriteAllText(sentinelPath workingDirectory, "")

    let reset (workingDirectory: string) : unit =
        let path = sentinelPath workingDirectory
        if File.Exists(path) then File.Delete(path)
