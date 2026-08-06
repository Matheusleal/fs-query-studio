namespace FSharpQueryStudio.Execution

open System
open System.Collections.Concurrent

/// Backing store for opt-in persistent per-tab sessions — see the
/// architecture doc's "Preparing for persistent sessions" table. Isolated
/// mode (the Milestone A default) never touches this; callers build a
/// fresh `FsiSession.Session` directly instead.
module SessionStore =

    type private Entry = { Session: FsiSession.Session; LastSource: string }

    let private sessions = ConcurrentDictionary<string, Entry>()

    /// E.3's invalidation rule: `newSource` invalidates a session that
    /// last ran `lastSource` unless it's a pure append — `newSource`
    /// starts with `lastSource` exactly, character for character. Milestone
    /// A/E.1/E.2 send the whole document as one `Source` string per run
    /// (there's no notebook-cell/Run-Selection concept yet to invalidate
    /// sub-document ranges of), so "prefix stayed the same" is the
    /// closest textual stand-in for "everything already executed stays
    /// valid, only the new tail is unproven" until E.4's real cell
    /// boundaries give this a finer grain. Edited or deleted earlier text
    /// (the prefix no longer matches) invalidates unconditionally, since
    /// bindings built from text that no longer exists in the document
    /// could reference definitions the user just changed or removed.
    let invalidates (lastSource: string) (newSource: string) : bool =
        not (newSource.StartsWith(lastSource, StringComparison.Ordinal))

    /// Returns the session to evaluate `source` against under `key`,
    /// applying the invalidation rule above: a tracked session whose last
    /// source isn't a prefix of `source` is dropped and replaced with a
    /// fresh one rooted at `workingDirectory` before this returns, so its
    /// stale bindings can never leak into the new evaluation. Every call —
    /// first-ever, reused, or freshly invalidated — records `source` as
    /// this key's new "last executed", ready for the next call's check.
    let getOrCreate (key: string) (workingDirectory: string) (source: string) : FsiSession.Session =
        let session =
            match sessions.TryGetValue(key) with
            | true, existing when not (invalidates existing.LastSource source) -> existing.Session
            | _ -> FsiSession.create workingDirectory

        sessions.[key] <- { Session = session; LastSource = source }
        session

    /// Drops the tracked session for `key`, if any — the next
    /// `getOrCreate` for that key starts a fresh one regardless of what
    /// source it's called with. `FsiEvaluationSession` has no `Dispose`;
    /// its collectible load context is reclaimed once nothing (including
    /// this dictionary) still references it.
    let reset (key: string) : unit = sessions.TryRemove(key) |> ignore
