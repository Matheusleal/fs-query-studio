# Fix — cell result hidden behind FSI stdout in a single notebook output

**Status:** Concluído
**Started:** 2026-09-08
**Last updated:** 2026-09-08
**Related:** branch `main-vscode`; `editors/vscode/src/notebook/controller.ts`, `editors/vscode/src/notebook/result.ts`; user report ("result is not being displayed after running a fsharp block")

---

## 1. Planning

### Objective
Running a cell whose script prints to stdout (FSI's `val x: … =` echo is
stdout) *and* produces a result value shows only the stdout text. The
table grid / `text/plain` result is reachable only by manually switching
the output's presentation (`application/vnd.code.notebook.stdout` →
`text/plain` / `application/x-fsquery-table+json`). It reads as "no result".

### Root cause
`controller.ts` packs **every** mapped item — stdout/stderr lines, the
`application/x-fsquery-table+json` payload, and the `text/plain` grid —
into a **single** `vscode.NotebookCellOutput`. VS Code treats the items of
one `NotebookCellOutput` as *alternative representations of the same
value* and renders exactly one, chosen by mime priority; it picks the
stdout mime, so the result representation is hidden.

`mapEnvelope` (`result.ts`) is correct on its own — it returns the specs
in the right order. The defect is purely in how the controller groups
them into outputs.

### Requirements / Scope (revised after first attempt — see below)
- The grid (`application/x-fsquery-table+json`) is the cell's **default**
  view when the result is a table.
- The FSI declaration echo (`val result: … =`, `val rows: … = [| … |]`)
  and any `printfn` output go to a **non-default "raw data"** view —
  reachable through the output's presentation picker, not shown up front.
- No `application/vnd.code.notebook.stdout` / `.stderr` item is emitted:
  that mime sits near the top of VS Code's `NOTEBOOK_DISPLAY_ORDER`
  (right after `error`), so any stdout item becomes the cell's default
  view and hides the grid — the exact bug reported.
- Error / worker-crashed / null-result / mapping-throw paths keep their
  single dedicated error output (an error must stay the default view).
- `NoValue` with no output still clears the cell output.
- A cell with only `printfn` output and no value still shows that text
  (it is then the only representation).
- **Non-goals:** the worker / FSI flags (not touched — no `--quiet`); the
  renderer; the `Results.table`-eats-row-0 data-shape issue (separate,
  `Results.raw` is the right call for a connector query).

### Approach & Key Decisions
- **First attempt (reverted same day): split stream lines into their own
  `NotebookCellOutput` via a `groupOutputs` helper.** This made the echo
  and the grid *both* visible, stacked. The user then clarified they want
  the echo **not shown by default at all** — only the table, with the raw
  dump behind the presentation picker. A separate `NotebookCellOutput`
  can't be hidden-by-default (VS Code renders every output), so this
  approach could not meet the requirement. `groupOutputs` was removed.
- **Decision — fold stdout/stderr into the `text/plain` item inside the
  one result output; never emit a stdout/stderr mime.** *Reasoning:*
  within a single `NotebookCellOutput`, VS Code picks the default
  representation by mime rank. A registered renderer mime
  (`application/x-fsquery-table+json`) outranks `text/plain`, so the grid
  is the default and the folded echo (now part of `text/plain`) is the
  one-click "raw data" alternative — exactly the requested behaviour.
  *Alternative considered:* `--quiet` on the FSI session to drop the
  echo entirely — rejected for now, larger blast radius (all execution),
  and the user wants the raw dump *available*, just not default.
- **Decision — `text/plain` = `streamText` + `\n\n` + body** (the text
  grid for a Table, `displayText` otherwise). *Reasoning:* one switchable
  view carries both the echo and a copy-pasteable grid.
- **Decision — Error returns only the error item, echo discarded.**
  *Reasoning:* a failed cell must surface the error, not bury it under
  FSI chatter.

### Risks & Assumptions
- Assumes `application/x-fsquery-table+json` (custom renderer) outranks
  `text/plain` in VS Code's default output display order — it does
  (renderer mimes are inserted above `text/plain`, which is the built-in
  floor); confirmed by the original report, where switching *to* either
  `text/plain` or the table mime showed the result and only the stdout
  mime was auto-selected.
- For non-Table kinds (Scalar/Text/Object) there is no rich mime, so the
  FSI echo *is* shown (as part of the sole `text/plain` item). Accepted —
  the user's requirement is about the table case.
- `exec.replaceOutput(new NotebookCellOutput([]))` for the empty case:
  stub and VS Code both treat it as "clear output".

---

## 2. Execution

### 2026-09-08 — `groupOutputs` helper + controller regrouping
- Files touched: `editors/vscode/src/notebook/result.ts`,
  `editors/vscode/src/notebook/result.test.ts`,
  `editors/vscode/src/notebook/controller.ts`,
  `editors/vscode/src/notebook/controller.test.ts`
- What changed:
  - `result.ts`: new pure `groupOutputs(specs): OutputItemSpec[][]`.
    Every stdout/stderr run → its own group; a trailing group holds all
    non-stream specs (`text` / `table-json` / `error`). Consecutive
    same-stream specs are merged into one item (`\n`-joined) so a
    multi-line FSI echo is one block. Returns `[]` for `[]`, and omits
    the trailing group entirely when there is no value spec.
  - `controller.ts`: `runBatch` now does
    `groupOutputs(mapEnvelope(...)).map(g => new NotebookCellOutput(...))`
    and `exec.replaceOutput(outputs)` with the array. Error/crash/null/
    mapping-throw branches untouched — they already build one dedicated
    `errorOutput`.
  - `mapEnvelope` unchanged (order/content of specs was already right).
- TDD: added 6 `groupOutputs` cases + updated 2 `controller.test.ts`
  assertions (two-output shape; flattened-mime check) — RED first
  (`groupOutputs is not a function`), then GREEN.
- Commands run: `npx vitest run src/notebook/{result,controller}.test.ts`,
  `npm test`, `npm run compile`,
  `npx vscode-test --grep "fsquery-notebook integration"`.

### 2026-09-08 — revised: fold stdout into text/plain, drop `groupOutputs`
- Files touched: `editors/vscode/src/notebook/result.ts`,
  `editors/vscode/src/notebook/result.test.ts`,
  `editors/vscode/src/notebook/controller.ts`,
  `editors/vscode/src/notebook/controller.test.ts`
- What changed:
  - `result.ts`: removed `groupOutputs`; removed `"stdout"`/`"stderr"`
    from `OutputItemSpec`. `mapEnvelope` now computes `streamText` (all
    `env.output` lines joined) and folds it into a single `text` spec as
    `streamText + "\n\n" + body`, where `body` is the text grid for a
    Table or `displayText` otherwise. `Error` returns only the error
    spec. A Table still emits `[table-json, text]` (renderer mime first);
    no stdout/stderr mime is ever produced.
  - `controller.ts`: `toOutputItem` lost its `stdout`/`stderr` cases;
    `runBatch` back to one `new vscode.NotebookCellOutput(items)`.
  - `result.test.ts`: deleted the `groupOutputs` describe block; rewrote
    the two stdout-ordering cases to assert the fold (`["text"]` /
    `["table-json","text"]`, echo inside the text item); added "printf
    cell with no value → single text spec".
  - `controller.test.ts`: "writes the mapped outputs" now asserts one
    output, items `["application/x-fsquery-table+json","text/plain"]`,
    with `"hi"` inside the text item.
- Commands run: `npm run compile`, `npm test`,
  `npx vscode-test` (full integration suite).

---

## 3. Validation

### Test Plan
- New `groupOutputs` unit cases in `result.test.ts`: stdout+value split;
  consecutive stdout merge; stderr kept separate from stdout; value-only
  (no stream) → one group; stream-only (NoValue) → no trailing empty
  group; empty input → `[]`.
- Update `controller.test.ts` "writes the mapped outputs" to assert two
  outputs (`[stdout]`, then `[table-json, text/plain]`).
- Update "omits the table-json item" to assert against the flattened
  mime list.
- `npm test` (vitest) green.
- `npm run compile` clean.
- `vscode-test --grep "fsquery-notebook integration"` still 5/5.
- Manual (if a worker build is available): run
  `let result = Connections.csv "test1"` + `Results.raw` in a `.fsqnb`,
  confirm the grid shows without touching the presentation menu.

### Test Results (final — after the revision)
- `npm test` (vitest) — **236/236** (was 241 before this task; the 6
  transient `groupOutputs` cases were added then removed, the two
  stdout-ordering `mapEnvelope` cases were rewritten, one new case
  added — net −5 vs the pre-task baseline).
- `npm run compile` — clean (`tsc --noEmit` + esbuild).
- `npx vscode-test` (full integration) — **13/14**; the one failure is
  the pre-existing `fsquery connections > resolves a {password} token…`
  → `KNOWN NPGSQL VARIANCE`, unrelated (no notebook-output code in its
  path). Notebook specs all green: "runs a code cell and captures stdout
  + the tail value" (`/hello from fsi/` and `/2/` now both in the one
  text/plain item), "renders a table-shaped result as a text grid",
  "a truncatable sequence cell produces an application/x-fsquery-table+json item".
- Manual in-app run: not driven this session. The integration test that
  executes a real cell through the bundled worker covers the end-to-end
  path; the user still needs to reinstall the rebuilt VSIX to see it.

### Acceptance Criteria Check
| Requirement | Verified how | Result |
|---|---|---|
| grid is the default view for a table result | `mapEnvelope` emits `[table-json, text]` (renderer mime ranks above `text/plain`); controller test asserts that item order in one output | ✅ |
| FSI echo / printf output → non-default "raw data" view | `mapEnvelope` "never emits a stdout/stderr mime — echo lives in text/plain…"; controller test finds `"hi"` inside the `text/plain` item, not its own output | ✅ |
| no `application/vnd.code.notebook.stdout` item emitted | `OutputItemSpec` no longer has `stdout`/`stderr`; `toOutputItem` has no such case; grep clean | ✅ |
| error paths unchanged, error stays default | existing controller tests (`worker/crashed`, null result, mapping throws) green; `mapEnvelope` Error returns only the error spec | ✅ |
| NoValue + no output clears output | `mapEnvelope` → `[]`, controller `replaceOutput(new NotebookCellOutput([]))` | ✅ |
| printf-only cell still shows its text | `mapEnvelope` "emits only stdout as text when a printf cell has no value" → `[{text:"hello"}]` | ✅ |

---

## 4. Notes & Follow-ups
- The user's original cell (`Results.table rows` on a materialized
  `obj[][]` from a CSV) also mis-renders because the generic
  nested-collection path consumes row 0 as headers. Correct usage for a
  connector query is `Connections.csv "test1" |> Results.raw`. Not fixed
  here — considered expected behaviour of `Results.table`; worth a
  README/guide note.
