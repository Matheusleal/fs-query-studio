namespace FSharpQueryStudio.Execution

open System.Collections.Concurrent

/// Backing store for opt-in persistent per-tab sessions — see the
/// architecture doc's "Preparing for persistent sessions" table. Isolated
/// mode (the Milestone A default) never touches this; callers build a
/// fresh `FsiSession.Session` directly instead.
module SessionStore =

    let private sessions = ConcurrentDictionary<string, FsiSession.Session>()

    /// Returns the session already tracked under `key`, or creates and
    /// tracks a fresh one rooted at `workingDirectory` if none exists yet.
    let getOrCreate (key: string) (workingDirectory: string) : FsiSession.Session =
        sessions.GetOrAdd(key, (fun _ -> FsiSession.create workingDirectory))

    /// Drops the tracked session for `key`, if any — the next
    /// `getOrCreate` for that key starts a fresh one. `FsiEvaluationSession`
    /// has no `Dispose`; its collectible load context is reclaimed once
    /// nothing (including this dictionary) still references it.
    let reset (key: string) : unit = sessions.TryRemove(key) |> ignore
