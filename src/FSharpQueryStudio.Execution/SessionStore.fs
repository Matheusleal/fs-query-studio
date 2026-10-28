namespace FSharpQueryStudio.Execution

open System
open System.IO
open System.Collections.Concurrent

/// Backing store for opt-in persistent per-tab sessions — see the
/// architecture doc's "Preparing for persistent sessions" table. Isolated
/// mode (the Milestone A default) never touches this; callers build a
/// fresh `FsiSession.Session` directly instead.
module SessionStore =

    /// `LastFragments` is the ordered log of source text this session has
    /// executed so far. `getOrCreate` (whole-document mode) and
    /// `appendFragment` (E.4's notebook/cell mode) are two different
    /// update policies over the same log: the former treats it as a
    /// single revise-in-place entry, the latter always grows it by one.
    type private Entry = { Session: FsiSession.Session; LastFragments: string list }

    let private sessions = ConcurrentDictionary<string, Entry>()

    /// E.3's invalidation rule: `newSource` invalidates a session that
    /// last ran `lastSource` unless it's a pure append — `newSource`
    /// starts with `lastSource` exactly, character for character.
    let invalidates (lastSource: string) (newSource: string) : bool =
        not (newSource.StartsWith(lastSource, StringComparison.Ordinal))

    /// Whole-document mode (Milestone A/E.1/E.2/E.3, unchanged): `source`
    /// is this key's entire current script text, as `ShellViewModel.
    /// RunAsync` resends on every run. Reuses the tracked session only if
    /// `source` is a pure append of whatever it last ran — anything else
    /// (edited or deleted earlier text) invalidates and starts fresh, so
    /// stale bindings can never leak into the new evaluation. Every call
    /// leaves the log holding exactly `[source]`, replacing whatever was
    /// there before.
    let getOrCreateInWorkspace (key: string) (workingDirectory: string) (source: string) (workspaceDirectory: string) : FsiSession.Session =
        let session =
            match sessions.TryGetValue(key) with
            | true, existing ->
                let lastSource = existing.LastFragments |> List.tryLast |> Option.defaultValue ""
                if invalidates lastSource source then
                    FsiSession.createInWorkspace workingDirectory workspaceDirectory
                else
                    Directory.SetCurrentDirectory(workingDirectory)
                    existing.Session
            | false, _ -> FsiSession.createInWorkspace workingDirectory workspaceDirectory

        sessions.[key] <- { Session = session; LastFragments = [ source ] }
        session

    let getOrCreate (key: string) (workingDirectory: string) (source: string) : FsiSession.Session =
        getOrCreateInWorkspace key workingDirectory source ""

    /// Notebook/cell mode (E.4): `fragment` is one cell's own isolated
    /// text — never the whole document — appended as the next step in
    /// `key`'s execution log. Unlike `getOrCreate`, appending never
    /// invalidates: a genuinely new, unrelated fragment isn't an edit of
    /// anything already executed, so there's nothing to invalidate. This
    /// is what lets later cells see earlier cells' bindings — they share
    /// one live `FsiEvaluationSession`, called once per cell in order —
    /// while still reusing the same per-key tracked session `getOrCreate`
    /// would also see, since both policies share the same `sessions` map.
    let appendFragmentInWorkspace (key: string) (workingDirectory: string) (fragment: string) (workspaceDirectory: string) : FsiSession.Session =
        let entry =
            match sessions.TryGetValue(key) with
            | true, existing ->
                Directory.SetCurrentDirectory(workingDirectory)
                existing
            | false, _ -> { Session = FsiSession.createInWorkspace workingDirectory workspaceDirectory; LastFragments = [] }

        let updated =
            { entry with LastFragments = entry.LastFragments @ [ fragment ] }

        sessions.[key] <- updated
        updated.Session

    let appendFragment (key: string) (workingDirectory: string) (fragment: string) : FsiSession.Session =
        appendFragmentInWorkspace key workingDirectory fragment ""

    /// Drops the tracked session for `key`, if any — the next
    /// `getOrCreate`/`appendFragment` for that key starts a fresh one.
    /// `FsiEvaluationSession` has no `Dispose`; its collectible load
    /// context is reclaimed once nothing (including this dictionary)
    /// still references it.
    let reset (key: string) : unit = sessions.TryRemove(key) |> ignore
