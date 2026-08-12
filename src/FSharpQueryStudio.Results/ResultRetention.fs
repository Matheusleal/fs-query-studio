namespace FSharpQueryStudio.Results

open System.Collections.Concurrent

/// A tab's full, never-truncated `Results.table` push, retained for as
/// long as the app runs — see docs/superpowers/specs/2026-08-12-results-export-design.md.
type RetainedResult = { Columns: string list; Rows: obj array array }

/// Keyed by document URI, one entry per open tab that has ever pushed a
/// table result. Lives in the Worker process's own memory, independent of
/// `Results.explicit` (which `Results.reset()` clears before every single
/// evaluation) — nothing here is ever cleared except by an explicit `set`
/// (a later run of the same tab overwrites it) or `discard` (the tab
/// closes). Never persisted to disk; gone on process exit by design.
module ResultRetention =
    let private store = ConcurrentDictionary<string, RetainedResult>()

    let set (documentUri: string) (result: RetainedResult) : unit = store.[documentUri] <- result

    let tryGet (documentUri: string) : RetainedResult option =
        match store.TryGetValue(documentUri) with
        | true, result -> Some result
        | false, _ -> None

    let discard (documentUri: string) : unit = store.TryRemove(documentUri) |> ignore
