# Notebook cell results: capture the FSI tail value, polyglot-style

**Date:** 2026-09-08
**Status:** Approved (design), pending implementation plan

## Problem

Today a notebook cell only renders a rich result if the script *explicitly*
calls into the `Results` module — `Results.raw` for a `ConnectorQueryResult`,
`Results.table` / `Results.tableWithColumns` for any other collection,
`Results.text` for forced `%A`. These push into a process-wide `explicit`
mutable slot that `Handlers.runEvaluate` reads *before* it ever looks at F#
Interactive's own last-expression (`it`) value.

We want the opposite, matching .NET Interactive / Polyglot Notebooks: the
cell's last expression *is* the result. Ending a cell with a value renders
that value — collections become grids, connector queries become exportable
grids, scalars print — with no `Results.*` call and no `Results` module at
all.

## Decisions (locked)

- **The `Results` module is removed entirely.** `ScriptResults.fs` is
  deleted. No optional override survives.
- **`ConnectorQueryResult` as a tail value** renders as a grid *and* is
  fully materialized into `ResultRetention`, so the existing **Export CSV**
  button keeps working — exactly what `Results.raw` did, now triggered by
  the tail value.
- **Tuple sequences** render with positional columns `Item1 .. ItemN`
  (F#'s own tuple field names). There is no header-override mechanism any
  more.
- **A cell with no tail value** (ends in a `let` binding or a side-effecting
  call like `printfn`) renders its captured stdout plus a quiet "no value".
  This is the accepted polyglot behavior — no fallback to an earlier
  expression's value.

## Architecture

### Capture

Unchanged. `FsiSession.create` runs a prologue, `Evaluation.evaluate` calls
`EvalInteractionNonThrowing` and captures `Value: FsiValue option` — the
`it` value. That `Option.map (_.ReflectionValue)` is the tail value the
projector receives.

### Dispatch — `Projection.projectWithCursor`

Signature changes to make the retention output explicit:

```fsharp
type RetainedTable = { Columns: string list; Rows: obj array array }

// was: obj -> int64 -> ResultEnvelope * PagingCursor option
val projectWithCursor : obj -> int64 -> ResultEnvelope * PagingCursor option * RetainedTable option
```

`project` (the `fst` convenience) and `projectError` / `projectWorkerError`
are unchanged.

Dispatch on the tail value's runtime shape:

| Tail value | `ResultEnvelope.Kind` | Rows | Cursor | Retained |
|---|---|---|---|---|
| `null` / no `it` | `NoValue` | — | none | none |
| `string` | `Text` (`DisplayText`) | — | none | none |
| primitive — `IsPrimitive`, `decimal`, `DateTime`, `DateTimeOffset`, `Guid`, `TimeSpan` | `Scalar` (`DisplayText = string v`) | — | none | none |
| single F# record | `Table`, real columns | 1 | none | **yes** |
| `ConnectorQueryResult` | `Table`, `query.Columns` | all, materialized | none | **yes** |
| `seq` of F# records | `Table`, record field names | preview (`PreviewRowLimit`), `IsTruncated` when more | none | **yes** (full) |
| `seq` of tuples | `Table`, `Item1..ItemN` | preview, `IsTruncated` | none | **yes** (full) |
| `seq` of `Map` / `IDictionary` | `Table`, first-seen key union | preview, `IsTruncated` | none | **yes** (full) |
| `seq` of nested `IEnumerable` (non-string) | `Table`, positional `""` headers | preview, `IsTruncated` | none | **yes** (full) |
| `seq` of scalars / anything else | `Table`, single `Value` column | preview | **`PagingCursor`** when more | **none** |
| any other object | `Text` (`sprintf "%A" v`) | — | none | none |

The record / tuple / dict / nested branches are ported from
`Results.dispatchRows`, dropping every `columnNames: string list option`
parameter and the `resolveHeaders` override logic. The `ConnectorQueryResult`
branch is ported from `Results.raw` (`query.Columns` → `Column list`,
`query.Rows |> Array.ofSeq`).

**Materialize vs. stream.** Structured sequences and connector results are
fully materialized — identical to what `Results.table` / `Results.raw` do
today — so a `RetainedTable` can back Export CSV. The envelope still carries
only up to `PreviewRowLimit` rows, with `IsTruncated = true` when the full
set is larger. Only the flat-scalar `seq` keeps the lazy
`PagingCursor` + `continuationToken` "Load more" path, unchanged, and
retains nothing (a lazy stream can't be honestly exported — matches today).

`Projection.fs` already has `FSharpType` reflection helpers and opens
`FSharpQueryStudio.Protocol`; it gains `open FSharpQueryStudio.Connectors`
(the `FSharpQueryStudio.Results` project already references that assembly)
for the `ConnectorQueryResult` type.

### Worker wiring — `Handlers.runEvaluate`

The `Results.currentEnvelope` pre-check and `Results.reset ()` are deleted.
The success branch collapses to one path:

```fsharp
| None ->
    let value = outcome.Value |> Option.map (fun v -> v.ReflectionValue) |> Option.toObj
    let projected, cursor, retained =
        Projection.projectWithCursor value stopwatch.ElapsedMilliseconds

    if not (String.IsNullOrEmpty evalParams.DocumentUri) then
        match retained with
        | Some t -> ResultRetention.set evalParams.DocumentUri { Columns = t.Columns; Rows = t.Rows }
        | None   -> ()   // a non-materialized result leaves the notebook's last exportable grid alone

    let continuationToken =
        cursor
        |> Option.map (fun c ->
            let token = Guid.NewGuid().ToString("N")
            currentPaging <- Some(token, c)
            token)

    { projected with Output = output; ContinuationToken = continuationToken }
```

Retention is now keyed on `retained.IsSome` — the projector's explicit
"this is a fully-materialized, exportable table" signal — not on
`envelope.Kind = Table` after the fact. A run that produces no such table
(`printfn`, a scalar, a streamed scalar `seq`) leaves any earlier retained
result in place; **Export CSV targets the notebook's most recent
materialized grid.** The old "discard a stale generic Table" guard is
deleted — with the `Results` module gone there is only one projection path,
so a materialized generic table legitimately *becomes* the retained result.
`handleResultsExportCsv`, `handleResultsPage`, `handleResultsDiscard`,
`ResultRetention`, `CsvExport` are untouched.

### Robustness

The shape dispatch runs in the worker, *after* FSI evaluation, outside
`EvalInteractionNonThrowing`'s catch. A reflection failure there (e.g. a
`seq` whose first element is a tuple but whose later elements are not) must
not escape as an unhandled exception in the backgrounded task — that would
leave the request with no response frame. The whole shape-dispatch body is
wrapped: on any exception it falls back to a `Text` envelope carrying
`sprintf "%A" value`, `retained = None`, `cursor = None`.

### Prologues

`FsiSession.create` and `Fsac/Prologue.build`:

- Keep `#r` on the connectors assembly and `open FSharpQueryStudio.Connectors`
  — scripts still call `Connections.*`.
- Keep `#r` on the `FSharpQueryStudio.Results` assembly — the worker still
  projects types from it.
- **Drop `open FSharpQueryStudio.Results`** — no script-facing surface
  remains in that namespace.
- Fix the stale `Results.table` mentions in both files' doc comments.

`Prologue.lineCount` / `shiftUp` / `shiftDown` recompute from the built
text, so dropping one `open` line needs no constant edit.

## Files

**Source:**

- `src/FSharpQueryStudio.Results/Projection.fs` — shape dispatch, new
  `ConnectorQueryResult` branch, `RetainedTable` output, `open
  FSharpQueryStudio.Connectors`.
- `src/FSharpQueryStudio.Results/ScriptResults.fs` — **deleted**.
- `src/FSharpQueryStudio.Results/FSharpQueryStudio.Results.fsproj` — drop
  the `ScriptResults.fs` `<Compile>` entry.
- `src/FSharpQueryStudio.Worker/Handlers.fs` — collapse `runEvaluate`
  success branch; remove `Results.reset` / `Results.currentEnvelope`.
- `src/FSharpQueryStudio.Execution/FsiSession.fs` — prologue `open` +
  comment.
- `src/FSharpQueryStudio.Fsac/Prologue.fs` — prologue `open` + comment.

**Tests:**

- `tests/FSharpQueryStudio.Results.Tests/ScriptResultsTests.fs` — deleted,
  replaced by `ProjectionTests.fs` (new).
- `tests/FSharpQueryStudio.Results.Tests/FSharpQueryStudio.Results.Tests.fsproj`
  — swap the `<Compile>` entry.
- `tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs` — rewrite
  `Results.*`-seeded cases to end the eval source in the expression itself.
- `ResultRetentionTests.fs`, `CsvExportTests.fs` — untouched.

**Docs:**

- `editors/vscode/README.md`, `docs/TUTORIAL.md` — replace the
  `Results.raw / table / tableWithColumns / text` tables with the
  "end a cell with an expression" model; note tuple seqs show `Item1..ItemN`;
  fix the `tableWithColumns` example.
- `docs/DEVLOG.md` — new dated entry.
- `editors/vscode/src/notebook/result.ts`, `table-payload.ts` — comment-only
  touch-ups; no code change.

## Testing

TDD on `Projection.projectWithCursor`. `ProjectionTests.fs` ports every
shape case from the deleted `ScriptResultsTests.fs` (minus `columnNames`):

- record seq → real columns, materialized rows, `retained.IsSome`
- tuple seq → `Item1..ItemN`, `retained.IsSome`
- `Map` seq → first-seen key union
- nested `IEnumerable` seq → positional headers
- scalar seq → single `Value` column, `cursor.IsSome` when > `PreviewRowLimit`,
  `retained = None`
- empty seq → `Table`, zero rows
- single record → one row, real columns
- single scalar / `string` / `DateTime` → `Scalar` / `Text`
- `null` → `NoValue`
- plain object → `Text` with `%A`
- **new:** `ConnectorQueryResult` → `Table` with `query.Columns`,
  `retained` carries every row
- **new:** structured seq larger than `PreviewRowLimit` → `IsTruncated`,
  `retained` still holds the full set

`HandlersTests.fs`, worker level:

- cell source ending in `[ {| Id = 1 |} ]` → `Table` envelope, real columns
- cell source ending in `printfn "hi"` → `NoValue` + stdout line
- cell source ending in a connector query → `Table`, then `results/exportCsv`
  writes the rows
- cell source ending in `[ 1 .. 1000 ]` → single `Value` column,
  `results/page` serves the next page

## Out of scope

- Rewriting `PagingCursor` to carry a row projection.
- Unifying connector + computed results under a single retention/paging path.
- Any renderer or `.fsqnb` format change.
- Header-override / column-rename UX (removed with `Results.tableWithColumns`,
  not replaced).
