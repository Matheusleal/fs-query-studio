# Results.table Generalization — Design

Grew out of "UI Polish" brainstorming into its own sub-project — content-wise this is a script-API/backend change (the `FSharpQueryStudio.Results` module scripts call), not a visual change, and comparable in scope to the Results Export sub-project. UI Polish proper (visual/feedback/keyboard) gets its own, later spec.

## Background

`Results.table` (built during the Results Export sub-project, `docs/superpowers/specs/2026-08-12-results-export-design.md`) only accepts a `ConnectorQueryResult` — the shape `Connections.sql`/`csv`/`excel` return. Any other collection a script wants to display as a table (a list of records it built itself, a sequence of tuples, a `Map`, a plain array of arrays) has no path into the Results grid except `Results.text`, which renders it as one opaque structured-printed string, not a real table.

## Goals

- `Results.table` (current, `ConnectorQueryResult`-only version) is renamed to `Results.raw`, unchanged in behavior.
- A new `Results.table` accepts any collection — records, tuples, nested collections (array/list/seq of array/list/seq), `Map`/`IDictionary`, or a flat sequence of scalars — and renders it as a real table in the Results grid, inferring columns from the data's own shape wherever possible, with an optional explicit column-names override.
- Both functions keep the existing pass-through convention (`table`/`raw` return their input unchanged) so they compose in a pipeline without disturbing the script's own logic.

## Non-goals

- `Results.table` does not accept `ConnectorQueryResult` — a caller with one must use `Results.raw`. Passing one to `Results.table` is a clear, explicit error, not a silent misinterpretation (a `ConnectorQueryResult` is itself an F# record with `Columns`/`Rows` fields, so without this guard the generic record-dispatch path below would build a nonsense 1-row/2-column table from it).
- No change to `Results.text`, `Results.reset`, `currentEnvelope`, or anything in `ResultRetention`/the Worker/App/Ui layers — this sub-project is scoped entirely to `ScriptResults.fs`'s `table`/`raw` functions and their tests. Retention/export (`ResultRetention.set`, `results/exportCsv`) already work off whatever `Column list * obj array array` ends up in `explicit`, regardless of which function produced it — no change needed there.
- `seq<ConnectorQueryResult>` (a collection *of* `ConnectorQueryResult` values) is not specifically guarded against — it would fall into the generic record-dispatch path (since `ConnectorQueryResult` is itself an F# record) and produce a `Columns`/`Rows`-headed table from the raw, unenumerated `Rows` sequences. An obscure, unrequested case; not solved here.
- No deduplication of column names — if inferred or explicit names collide, both columns render with the same header. `ResultGridColumns.build` already binds by position (`[i]`), not by name, so duplicate names are cosmetic only, not a rendering bug.

## Architecture

### Signature

```fsharp
let raw (query: ConnectorQueryResult) : ConnectorQueryResult = /* unchanged body, renamed from table */

let table (data: obj) (columnNames: string list option) : obj = /* new */
```

`data: obj` is required, not a generic type parameter — F# has no ad-hoc polymorphism that lets one function branch on "record vs. tuple vs. nested collection vs. scalar" at compile time; dispatch happens at runtime via `Microsoft.FSharp.Reflection` (`FSharpType.IsRecord`/`IsTuple`, `FSharpValue.GetRecordFields`/`GetTupleFields` — part of `FSharp.Core`, no new package reference needed). This trades static type-checking at the call site for the flexibility requested; `raw` keeps its precise `ConnectorQueryResult -> ConnectorQueryResult` signature for the one shape that still gets full type safety.

### Dispatch

The whole input is materialized to `obj array` first (`Seq.cast<obj> |> Array.ofSeq` — matches `raw`/retention's existing full-materialization convention), then dispatched on the *first* element's runtime shape:

| First element's shape | Columns | `columnNames = None` behavior | `columnNames = Some [...]` behavior |
|---|---|---|---|
| `ConnectorQueryResult` (checked before the `IEnumerable` check below) | — | **Error**: "Results.table does not accept ConnectorQueryResult — use Results.raw." | same error |
| F# record (`FSharpType.IsRecord`) | reflected field names, declaration order | field names are the headers | `columnNames` overrides the headers, positionally, count must match field count |
| Tuple (`FSharpType.IsTuple`) | — | **Error**: "Results.table requires columnNames for tuple sequences." | each tuple element becomes its own column, positionally |
| `IDictionary`/`Map` (`data :? IDictionary`) | union of keys, first-seen order across rows; a row missing a key renders `null` (blank) for that cell | key names are the headers | `columnNames` overrides the headers, positionally, count must match key count |
| Nested collection (element is `IEnumerable`, excluding `string`) | — | row 0 is consumed as the header (stringified); rows 1.. are data | no row is consumed; every row is data; count must match row width |
| Scalar (anything else) | one column | header defaults to `"Value"` | `columnNames` must have exactly 1 entry, used as the header |

Whenever `columnNames = Some [...]`, those strings are always the final header labels, applied positionally over whatever columns were inferred — and the count must match exactly, or `table` throws (`"columnNames has 2 entries but rows have 3 values"`-style message). This is the one rule that holds across every shape above.

`data` that isn't `IEnumerable` at all (a bare scalar or record passed directly, not wrapped in a sequence) is a clear error: `"Results.table expects a collection; got a single value — wrap it, e.g. [value]."` — not silently treated as a 1-element sequence.

`string` is technically `IEnumerable<char>` in .NET and is explicitly excluded from both the outer "is this a collection at all" check and the "nested collection" element check, so `seq<string>` is scalars (one column of strings), never exploded into characters.

### Edge cases

- **Empty sequence**: 0 elements means there's no first element to dispatch on — 0 rows, columns = `columnNames |> Option.defaultValue []`. Not an error.
- **Ragged rows** (nested-collection case, rows of differing width): short rows pad with `null` (blank) cells; long rows have extra trailing cells dropped. Not an error — the explicit `columnNames`-count-mismatch error above already covers "I clearly got this wrong"; ragged real-world data shouldn't need to be pre-cleaned just to display.
- Dispatch is based on the *first* element only — a sequence mixing shapes (a tuple in row 0, a record in row 1) dispatches as whatever row 0 is; later rows of a different shape will fail at the reflection/positional-access step for that row, surfacing as a runtime error naming the row.

### Return value

`table data columnNames` returns `data` unchanged (boxed as `obj`), the same pass-through convention `raw`/`text` already use — `myRecords |> ignore` after `Results.table myRecords None |> ignore`-style usage keeps the value available for the rest of the script.

## Error handling

Every error path above (`ConnectorQueryResult` passed directly, tuple sequence with no `columnNames`, non-collection input, `columnNames` count mismatch) is a normal F# exception (`failwith`/`invalidArg`-style), which the existing `EvalInteractionNonThrowing` → `Evaluation.evaluate` → `Projection.projectError` pipeline already turns into a structured `ResultKind.Error` envelope — no new error-handling plumbing needed anywhere outside `ScriptResults.fs` itself.

## Testing

`Results.Tests` (`ScriptResultsTests.fs`, alongside the existing `raw`/`text` tests — `raw`'s tests move over unchanged under the new name):

- Record sequence: columns match reflected field names in declaration order, cell values match.
- Tuple sequence with `columnNames`: columns/cells match positionally; without `columnNames`, throws the tuple-specific error.
- `Map`/`IDictionary` sequence: union-of-keys columns in first-seen order; a row missing a key renders blank; `columnNames` override works.
- Nested-collection sequence (e.g. `int array array`) without `columnNames`: row 0 becomes headers, data starts at row 1. With `columnNames`: every row is data.
- Scalar sequence (e.g. `int list`): single column, one row per value; default header `"Value"`; `columnNames` override works.
- `columnNames` count mismatch (any shape): throws with a message naming both counts.
- `ConnectorQueryResult` passed to `table`: throws, message references `Results.raw`.
- Non-collection `data` (a bare `int`): throws the "wrap it" error.
- Empty sequence: 0 rows; columns from `columnNames` if given, else `[]`.
- Ragged nested-collection rows: short rows pad blank, long rows truncate, no error.
- `seq<string>`: renders as scalars (one column), not exploded into characters.
- `raw` (renamed): all of `table`'s former tests, unchanged, just renamed alongside the function.
- Pass-through: both `table` and `raw` return their input unchanged (`Assert.Same`/`Assert.Equal` as appropriate for boxed `obj` vs. the original typed value).

No changes needed to `Worker.Tests`/`Ui.Tests`/`Integration.Tests` — `runEvaluate`'s retention-commit logic already keys off `Results.currentEnvelope`'s `Kind`/`Schema`/`Rows`, not which function produced them.
