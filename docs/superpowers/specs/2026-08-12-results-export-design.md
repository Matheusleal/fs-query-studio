# Results Export (and Full-Result Retention) — Design

Third of four planned sub-projects toward better app observability and polish for real usage (Log Viewer → Welcome Page → Results Export → UI Polish). Grew substantially during brainstorming from "add a CSV export button" into a per-tab full-result retention mechanism, because the user's actual need — export that's never limited to what the UI happened to preview — can't be met by exporting whatever's currently on screen (that data is already bounded/truncated by the time it reaches the UI process) or by re-running the query at export time (the script that produced it may have computed parameters no longer available, and re-querying live data can return something different from what the user is looking at).

## Background

`FSharpQueryStudio.Results.CsvExport.write` (built for the original C.14 milestone task) already exists, is fully tested, and streams a `TextWriter` from `columns: string list`/`rows: obj array seq` — but nothing in the running app ever calls it. It was built against a `ConnectorQueryResult`'s own *live* row sequence, directly from a connector, in a context (tests, the Milestone C acceptance test) where that live sequence was still open. That's not the situation the real app is in by the time a user might want to export: `Results.table query` (built earlier this session) already truncates `query.Rows` down to `Projection.PreviewRowLimit + 1` and discards the rest before the result ever leaves the Worker process, so by the time `ResultEnvelope.Rows` reaches the UI, the full data is already gone.

A second, related finding from this same investigation: `results/page`/`ContinuationToken` (built for C.13, `Worker/Handlers.fs`'s `currentPaging`) is *also* backend-only in a way that matters here — reading `runEvaluate`, `currentPaging` is only ever populated in the branch where `Results.currentEnvelope` returns `None`, i.e. only for the generic FSI-tail-value projection path. A script that calls `Results.table` explicitly (`Connections.sql "Sales" ... |> Results.table`, the pattern this app's own Welcome guide teaches) never populates `currentPaging` at all — `results/page` cannot page through a `Results.table`-pushed result today, regardless of whether it's ever wired to the UI. This sub-project doesn't extend or reuse that mechanism (see Architecture § "Why not `results/page`" below); it's noted here so the next reader doesn't assume `results/page` is closer to done than it is.

## Goals

- A script's `Results.table`-pushed result is retained *in full* (never truncated) in the Worker process's memory, for as long as the app runs — keyed per open document tab, refreshed on each run of that tab, evicted when that tab closes.
- "Export CSV…" — a new toolbar button, enabled whenever the active tab's current result is a table — writes that tab's retained full result straight to a file the user picks, via the existing `CsvExport.write`. No re-querying.
- The Results grid itself shows the *entire* retained result, not the `PreviewRowLimit`-bounded preview it shows today — "no truncation," per the user's explicit request — while staying performant via the DataGrid's own row-virtualization (already true today for whatever it's bound to; the actual change here is *how much* it's bound to and *when* that changes).
- Retained memory doesn't leak: closing a tab drops that tab's retained entry immediately, not just at process exit.

## Non-goals (deferred or out of scope)

- Any change to the generic FSI-tail-value projection path or `results/page`/`ContinuationToken`/`currentPaging` — untouched. This sub-project's retention mechanism is independent and new; the old mechanism keeps working exactly as it does today for scripts that don't call `Results.table`.
- Export formats other than CSV — no Excel/JSON export backend exists yet; out of scope until one is actually requested.
- Retention/export for REST/JSON results — `Connections.rest` returns a raw `JsonElement`, not a `ConnectorQueryResult`; `Results.table` (and therefore retention) only ever applies to SQL/CSV/Excel-backed table results, exactly as it does today.
- Persisting retained results to disk, or surviving an app restart — "keep alive while the application is running" is explicit; nothing here is durable across a relaunch.
- A cap on retained-result size or a warning for very large results — not requested; if this becomes a real problem in practice it's a follow-up, not something to guess at pre-emptively here.

## Architecture

### Retention (`FSharpQueryStudio.Results`)

New module `ResultRetention`, alongside `ScriptResults.fs`:

```fsharp
type RetainedResult = { Columns: string list; Rows: obj array array }

module ResultRetention =
    let private store = System.Collections.Concurrent.ConcurrentDictionary<string, RetainedResult>()

    let set (documentUri: string) (result: RetainedResult) : unit = store.[documentUri] <- result

    let tryGet (documentUri: string) : RetainedResult option =
        match store.TryGetValue(documentUri) with
        | true, result -> Some result
        | false, _ -> None

    let discard (documentUri: string) : unit = store.TryRemove(documentUri) |> ignore
```

Lives in the Worker process (the same process `Results`/`ScriptResults`'s existing mutable state already lives in, via the FSI session's auto-referenced assemblies) and — unlike `Results.explicit`, which `Results.reset()` clears before every single evaluation — is *never* reset by an evaluation. Each key's value only changes when that same tab runs another `Results.table`-producing script (overwrite) or the tab closes (`discard`).

`Results.table query` (`ScriptResults.fs`) changes from truncate-and-discard to fully materialize:

```fsharp
let table (query: ConnectorQueryResult) : ConnectorQueryResult =
    let columns = query.Columns |> List.map (fun name -> { Name = name; DisplayType = "obj" })
    let allRows = query.Rows |> Array.ofSeq  // full materialization — was Seq.truncate before
    explicit <- Some(Table(columns, allRows))
    query
```

This is a deliberate, real memory trade-off — every `Results.table`-produced result is now fully resident in the Worker's memory for as long as its tab stays open, where before only up to `PreviewRowLimit + 1` rows were ever even read off the connector's stream. Worth stating plainly since it's the direct cost of "keep it alive, don't truncate," not an accident.

`explicit`'s `Table` case drops its own `isTruncated`/preview-slicing logic — there's no truncation left to represent once retention holds everything, and the Results grid itself now shows the full set (see below), so `IsTruncated` on the wire becomes structurally always `false` for a `Results.table`-produced envelope; the field stays on `ResultEnvelope` (still meaningful for the untouched generic-projection path) but this path stops setting it `true`.

### Committing retention (`Worker`)

`runEvaluate` (`Handlers.fs`) needs to know *which tab* just ran, to commit `Results`'s pending table (if any) into `ResultRetention` under the right key — today `EvaluateParams` only carries `SessionId: string option`, `None` for Isolated mode (the only mode reachable from the UI). `EvaluateParams` gains a new, always-populated field:

```fsharp
type EvaluateParams =
    { Source: string
      SessionId: string option
      Mode: SessionMode
      DocumentUri: string }   // new — always the running tab's own Uri, both modes
```

Additive; `SessionId`'s own semantics (driving `SessionStore`'s Persistent-mode session reuse) are untouched — this is a second, independent piece of information about the same request, not a repurposing of the first. `ShellViewModel.RunAsync` (`Ui`) is the only call site that constructs `EvaluateParams`; it already has `doc.Uri` in scope (used two lines away for `SessionId`'s own `Some doc.Uri` case), so populating the new field costs one line.

`runEvaluate` already branches on whether `Results.currentEnvelope` returned an explicit push before falling back to the generic projection path:

```fsharp
match Results.currentEnvelope stopwatch.ElapsedMilliseconds output with
| Some explicitEnvelope -> explicitEnvelope
| None -> (* generic projection path, untouched *)
```

Retention is committed *only* inside the `Some explicitEnvelope` arm, and only when that envelope is a table (an explicit `Results.text` push is `Some` too, but isn't tabular):

```fsharp
match Results.currentEnvelope stopwatch.ElapsedMilliseconds output with
| Some explicitEnvelope ->
    if explicitEnvelope.Kind = ResultKind.Table then
        ResultRetention.set
            evalParams.DocumentUri
            { Columns = explicitEnvelope.Schema |> List.map (fun c -> c.Name)
              Rows = explicitEnvelope.Rows }

    explicitEnvelope
| None -> (* generic projection path, untouched *)
```

This is deliberately narrower than "any Table-kind envelope" — the generic path's own Table results (via `Projection.projectWithCursor`) stay exactly as bounded/paged as they are today (Non-goals), so checking `Results.currentEnvelope`'s own `Some`/`None` first, not `envelope.Kind` after the fact, is what keeps the two paths from bleeding into each other. If the run's result *wasn't* a `Results.table` push (generic path, `Text`, or `Error`), that tab's existing retained entry is left untouched — a script that prints something or errors doesn't erase a previous run's real table result the user might still want to export.

### Export (`Worker` → `App` → `Ui`)

New Worker RPC `results/exportCsv`:

```fsharp
type ExportCsvParams = { DocumentUri: string; FilePath: string }
```

Handler: `ResultRetention.tryGet documentUri` — `None` returns a structured `results/nothingToExport` error (never run, or last result wasn't a table); `Some retained` opens `filePath` and calls the existing `CsvExport.write` directly against the retained (already fully in memory) columns/rows — no connector, no re-query, no worker-side streaming-from-a-live-reader at all, since there's nothing live left to stream from by this point.

`App`'s `ExportService.exportCsv client documentUri filePath : Task<Result<unit, string>>` is a thin wrapper, same shape as every other App-layer service in this codebase (`ConnectionService`, etc.).

`Ui`: a new "Export CSV…" toolbar button (parallel to the existing `CopyResultButton`), bound enabled/disabled to `shellViewModel.Result.IsTable` (already exists — no new derived property needed). Click handler opens a native Save File dialog via `IStorageProvider` (same pattern `MainWindow`'s existing Save/Save As handlers already use), then calls the composition-root's `ExportCsvAsync` hook (same no-op-when-unconfigured shape as every other `ShellViewModel` hook) with the active document's `Uri` and the chosen path.

### Eviction on tab close (`Ui` → `App` → `Worker`)

New Worker RPC `results/discard`:

```fsharp
type ResultsDiscardParams = { DocumentUri: string }
```

Handler: `ResultRetention.discard documentUri` — always succeeds, including for a `documentUri` nothing is retained under (already-discarded, or a tab that never produced a table result; same "no-op for an unknown key" convention `Workspace.removeConnection` already established).

`ShellViewModel.CloseDocument` gains a new hook, `DiscardResultsAsync: (string -> Task<unit>) option`, same no-op-when-unconfigured shape as every other hook — called fire-and-forget right after a document is actually removed from `documents` (not called if the close was refused by the dirty-tab-discard prompt). `App.axaml.fs` wires it to a new `ExportService.discardResults client documentUri`.

### The results view stops truncating (`Ui`)

Since `ResultEnvelope.Rows` for a `Results.table`-produced result now carries the *entire* retained set (§ "Committing retention" above — the envelope embeds the same full array `ResultRetention` stores, not a truncated slice), `ResultViewModel.Rows`/`ResultsView.axaml`'s `DataGrid` need no new code at all to stop truncating — they already bind directly to whatever `Rows` contains. The DataGrid's existing UI-level row virtualization (true since C.12: only on-screen rows are actually realized as visual elements, regardless of how many are in `ItemsSource`) is what keeps this performant — rendering cost stays bounded to the viewport regardless of result size, matching "maximum performance … without truncat[ing] data."

**Why not `results/page`, or a new scroll-triggered incremental-fetch RPC.** The alternative considered was wiring `results/page` (or a new equivalent) to fire as the user scrolls the DataGrid near its current bottom, fetching more rows on demand instead of sending everything at once. Rejected for this pass: (1) `results/page` doesn't apply to `Results.table` results at all today (see Background) — using it would mean either extending that separate cursor-based mechanism to a case it was never built for, or building a second, parallel paging RPC directly against `ResultRetention` (straightforward, since the whole array is already index-addressable in memory, but real new protocol/Worker/Ui surface all the same); (2) Avalonia's `DataGrid` has no built-in "near the end of scroll" event — detecting it means hooking the control's internal `ScrollViewer.ScrollChanged` and comparing offsets, a fragile pattern this session has no way to visually verify (no display available, no headless rendering harness in this codebase); (3) sending the whole retained result once is simpler, and for the realistic result sizes this app's connectors return (thousands of rows, not millions), the one-time IPC/deserialization cost is a reasonable trade against a materially more complex and less-verifiable incremental-fetch mechanism. If a genuinely huge result set ever makes the one-shot transfer itself the bottleneck, incremental fetch is the natural follow-up — deferred, not designed away.

## Error handling

- `results/exportCsv` against a tab with nothing retained (never run, last result wasn't a table, or the tab's entry was already discarded) — structured `results/nothingToExport` error, surfaced to the user as a simple log/status message (no new toast/notification system exists in this app; matching its established low-friction error-handling convention elsewhere).
- A file-write failure during export (disk full, permission denied, path invalid) — the export RPC's own handler catches it and returns a structured error with the real exception message, same convention `connection/schema`'s `schemaFailed` error already uses for a comparable "real I/O can fail" case.
- `results/discard` never errors — discarding an unknown/already-gone key is a no-op, matching `Workspace.removeConnection`'s established convention.

## Testing

- `ResultRetention` (`Results.Tests`): `set`/`tryGet`/`discard` round-trip; `tryGet` on an unset key returns `None`; `discard` on an unset key doesn't throw; `set` overwrites an existing entry for the same key.
- `ScriptResults.table` (`Results.Tests`): a query with more rows than `Projection.PreviewRowLimit` — the value `Results.table` returns is unchanged (still passes `query` through); `Results.currentEnvelope`'s `Table` case now carries *all* the rows, not a truncated slice, and `IsTruncated` is `false`.
- Worker (`Worker.Tests`): a real `execution/evaluate` with `DocumentUri` set and a `Results.table`-calling script commits a retained entry under that URI, verified via a subsequent `results/exportCsv` call producing the full CSV (a genuine end-to-end round trip, not a unit test of retention in isolation); a second evaluate under the *same* `DocumentUri` with a *different* query overwrites the first tab's retained entry; an evaluate that doesn't call `Results.table` (e.g. a bare `printfn`) leaves an *existing* retained entry for that tab untouched; `results/exportCsv` against an unknown `DocumentUri` returns the structured `nothingToExport` error; `results/discard` removes a previously-retained entry, and a repeat call is a no-op, not an error.
- `Ui.Tests`: `ShellViewModel.DiscardResultsAsync` fires (with the closed document's `Uri`) when `CloseDocument` actually removes a tab, and does *not* fire when the close is refused (dirty tab, no confirm hook); the hook is a no-op when unconfigured, same pattern as every other hook's own test in this codebase.
- No XAML-level test for the new toolbar button/Save dialog wiring — same standing limitation as every other XAML/code-behind change in this codebase (no headless rendering harness); verified by a clean `dotnet build` plus the layers below it being independently tested.
