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

### Requirements / Scope
- stdout/stderr stream lines render as their own cell output(s), visible
  at the same time as the result — no presentation switch needed.
- The result value keeps its rich + fallback representations as
  alternatives in **one** output: `application/x-fsquery-table+json`
  first, `text/plain` second (unchanged priority *within* the result).
- Error / worker-crashed / null-result / mapping-throw paths keep their
  current single dedicated error output.
- `NoValue` with no output still clears the cell output.
- **Non-goals:** changing `mapEnvelope`'s spec order or contents; the
  worker; the renderer; the `Results.table`-eats-row-0 data-shape issue
  (separate, documented in the reply to the user — `Results.raw` is the
  right call for a connector query).

### Approach & Key Decisions
- **Decision — split into outputs in a new pure helper `groupOutputs`
  in `result.ts`, called by the controller.** *Reasoning:* keeps the
  VS Code-free, unit-tested seam that `result.ts` already is; the
  controller change stays a one-liner (`.map(group => new
  NotebookCellOutput(...))`). *Alternative considered:* inline the
  filter/split in `controller.ts` — rejected, it's logic worth testing
  without the vscode stub.
- **Decision — merge consecutive same-stream specs into one item.**
  *Reasoning:* `mapEnvelope` emits one spec per line; without merging a
  multi-line FSI echo becomes several stacked output blocks. One stdout
  block matches native notebook behaviour.
- **Decision — result representations stay grouped in one output.**
  *Reasoning:* `table-json` and `text/plain` genuinely *are* two
  renderings of the same value — that is exactly what a multi-item
  `NotebookCellOutput` is for. The bug is only stdout being mixed in.

### Risks & Assumptions
- Assumes VS Code renders multiple `NotebookCellOutput`s stacked (it
  does — standard notebook behaviour) and picks one representation per
  output by mime preference.
- Integration tests flatten `cell.outputs.flatMap(o => o.items)` so they
  are unaffected by the regrouping.
- `exec.replaceOutput([])` for the empty case: the stub and VS Code both
  treat an empty array as "clear output".

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

### Test Results
- `npx vitest run src/notebook/{result,controller}.test.ts` — 36/36
  (RED beforehand: 7 failing — 6 `groupOutputs is not a function`, 1
  controller output-shape).
- `npm test` — **241/241** (was 241; net-zero: the 2 rewritten
  controller assertions + 6 new `result` cases, minus none).
- `npm run compile` — clean (`tsc --noEmit` + esbuild).
- `npx vscode-test --grep "fsquery-notebook integration"` — **5/5**,
  including "runs a code cell and captures stdout + the tail value" and
  "renders a table-shaped result as a text grid" (both flatten
  `cell.outputs`, unaffected by the regroup, still green).
- Manual in-app run: not done this session (no interactive VS Code host
  driven here). The integration test that executes a real cell through
  the bundled worker covers the end-to-end path.

### Acceptance Criteria Check
| Requirement | Verified how | Result |
|---|---|---|
| stdout/stderr in their own output, visible with the result | `groupOutputs` "puts stream lines in their own group…" + controller "writes the mapped outputs" now asserts `[[stdout],[table-json,text]]` | ✅ |
| result keeps table-json → text/plain as alternatives in one output | controller test asserts `outputs[1].items` mimes `["application/x-fsquery-table+json","text/plain"]` | ✅ |
| error/crash/null/throw paths unchanged | existing controller tests (`worker/crashed`, null result, mapping throws) still green | ✅ |
| NoValue + no output clears output | `groupOutputs([])` → `[]` unit; controller passes `[]` to `replaceOutput` | ✅ |
| consecutive FSI echo lines = one block | `groupOutputs` "merges consecutive same-stream specs" | ✅ |

---

## 4. Notes & Follow-ups
- The user's original cell (`Results.table rows` on a materialized
  `obj[][]` from a CSV) also mis-renders because the generic
  nested-collection path consumes row 0 as headers. Correct usage for a
  connector query is `Connections.csv "test1" |> Results.raw`. Not fixed
  here — considered expected behaviour of `Results.table`; worth a
  README/guide note.
