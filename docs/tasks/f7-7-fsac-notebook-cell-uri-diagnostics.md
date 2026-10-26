# F7.7 — FSAC never analysed `vscode-notebook-cell:` URIs (no in-cell diagnostics)

**Status:** Concluído
**Started:** 2026-09-07
**Last updated:** 2026-09-07
**Related:** branch `main-vscode`; `docs/f6-parity-checklist.md` (row 23 observation); DEVLOG F4; integration test `editors/vscode/test/integration/intelligence.test.ts` "reports a diagnostic for a broken edit"

---

## 1. Planning

### Objective
The integration test `intelligence.test.ts` "reports a diagnostic for a broken
edit" failed locally (no diagnostic within its 30 s poll) and was CI-gated. It
is the last non-parity item from the F6 checklist blocking a clean local
`test:vscode` run. Find the root cause and fix it.

### Requirements / Scope
- `analysis/check` for a notebook cell must return real FSAC diagnostics.
- Fix verified by a fast test that does not depend on a live FSAC process where
  possible (the shared-process test classes are timing-sensitive).
- Non-goals: the *separate* pre-existing ~40 %/run flake in the full
  `FSharpQueryStudio.Fsac.Tests` suite (see Notes) — not caused here, not fixed
  here.

### Approach & Key Decisions
Systematic debugging, evidence first:
1. Reproduced with `npx vscode-test --grep "reports a diagnostic for a broken edit"`
   — consistent 32 s failure.
2. Instrumented every pipeline boundary (extension `onDidChangeTextDocument` →
   `DiagnosticsManager` → `analysis/check` → `FsacBridge`). Found: the extension
   side is fine; **every `FsacBridge.check` call times out after 10 s** waiting
   for `fsharp/documentAnalyzed`, and neither `publishDiagnostics` nor
   `documentAnalyzed` ever fires for these URIs. Worker log also showed
   `FSAC completion failed … "Couldn't find …\_d:_…_sample-intelligence.fsqnb
   in LoadedProjects"`.
3. **Root cause:** `FsacBridge.fsacUriFor` builds the synthetic `file://` URI it
   hands FSAC by replacing only `Path.GetInvalidFileNameChars()`. On Windows
   that set excludes `#` and `%`. A `vscode-notebook-cell:` URI carries a
   `#<base64 cell handle>` fragment and `%`-encoded path segments, so the
   synthetic string ends up as
   `file:///…/_d%3A_…_sample-intelligence.fsqnb#W2sZmlsZQ%3D%3D.fsx`. FSAC parses
   the `#` as a URI fragment, keeping only `…sample-intelligence.fsqnb` as the
   path — the `.fsx` suffix that `analysisDocumentUri` appends (so FSAC treats
   the buffer as an F# script) is discarded. FSAC does not script-analyse a
   `.fsqnb` file → no `documentAnalyzed`, no `publishDiagnostics` → `check`
   always times out to `[]`.

**Decision — sanitise strictly + guarantee `.fsx`.** In `fsacUriFor`, for a
non-`file:` caller URI, map every character outside `[A-Za-z0-9._-]` to `_`
(stricter than `GetInvalidFileNameChars`), then append `.fsx` if the result does
not already end with it. Deterministic (unchanged property) and collision-safe
for distinct cell handles. Alternative considered — percent-encode the path
properly instead of flattening it; rejected as more code for no user-visible
gain, since this synthetic identity never leaves the module.

**Decision — pure test, not another live-FSAC test.** First cut added a real
`FsacBridge.check` test with the notebook-cell URI shape. It reproduced the bug
(RED → GREEN) but adding one more serial live-FSAC check to
`FSharpQueryStudio.Fsac.Tests` measurably raised the hit rate of a *pre-existing*
suite flake (see Notes). Replaced it with `FsacBridgeUriTests.fs` — pure
assertions on `fsacUriFor`'s output (no `#`/`%`, ends `.fsx`, deterministic,
distinct per handle, `file:` passes through). `fsacUriFor` dropped `private`
for this; documented as test-only exposure.

### Risks & Assumptions
- Assumes FSAC keys script analysis on the `.fsx` extension of the URI path
  (confirmed by the "Couldn't find … in LoadedProjects" error naming the
  fragment-truncated `.fsqnb` path).
- Very long notebook paths could still approach `MAX_PATH` for the synthetic
  filename — same exposure as the previous implementation, not worsened.

---

## 2. Execution

### 2026-09-07 — investigation
- Instrumented `providers.ts`, `diagnostics.ts`, `FsacBridge.fs` with temporary
  probes; ran the failing integration test; read `%LOCALAPPDATA%/FSharpQueryStudio/logs/worker-*.log`.
  All probes reverted afterwards.

### 2026-09-07 — fix + tests
- Files touched: `src/FSharpQueryStudio.Fsac/FsacBridge.fs` (`fsacUriFor`
  sanitisation + `.fsx` guarantee; dropped `private`),
  `tests/FSharpQueryStudio.Fsac.Tests/FsacBridgeUriTests.fs` (new, 5 pure
  cases), `tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj`
  (compile entry).
- Rebuilt the bundled worker (`dotnet publish src/FSharpQueryStudio.Worker -c Release
  -o editors/vscode/dist/worker`) for the integration run.

---

## 3. Validation

### Test Plan
- New `FsacBridgeUriTests` — pure, fast.
- `intelligence.test.ts` "reports a diagnostic for a broken edit" — the real
  end-to-end guard.
- Regression: `FSharpQueryStudio.Fsac.Tests`, `FSharpQueryStudio.Worker.Tests`,
  `editors/vscode` vitest, full `editors/vscode` `test:vscode`.

### Test Results
- `FsacBridgeUriTests` — **5/5 pass**, 17 ms.
- `intelligence.test.ts` "reports a diagnostic for a broken edit" — **passes**
  (< 1 s; was a 32 s timeout failure). Both `cell intelligence` specs: 2 passing.
- `test:vscode` full — **12 passing / 1 failing** (was 11 / 2). The remaining
  failure is `connections.test.ts` `{password}` host-echo — the documented
  Windows/Npgsql variance from F6 checklist row 6, green on Linux CI, unrelated.
- `FSharpQueryStudio.Fsac.Tests` — 41 tests; passes clean on some runs, hits the
  pre-existing suite flake (one unrelated `file:`-URI real-FSAC test, `Assert.NotEmpty`
  empty) on others. Confirmed identical flake on a clean `git stash` tree
  (2 of 5 runs), so **pre-existing, not introduced here**.
- `FSharpQueryStudio.Worker.Tests` — **62/62**.
- `editors/vscode` vitest — **224/224**.

### Acceptance Criteria Check
| Requirement | Verified how | Result |
|---|---|---|
| `analysis/check` returns diagnostics for a notebook cell | `intelligence.test.ts` broken-edit spec | ✅ |
| Fast, FSAC-independent guard for the fix | `FsacBridgeUriTests` (pure) | ✅ |
| No regression in FSAC bridge behaviour | Fsac.Tests real-FSAC classes still green (modulo pre-existing flake); Worker.Tests 62/62 | ✅ |
| No extension regression | vitest 224/224; `test:vscode` 12 pass (up from 11) | ✅ |

---

## 4. Notes & Follow-ups
- **Pre-existing flake (separate bug, not this task):** the full
  `FSharpQueryStudio.Fsac.Tests` suite fails ~40 % of runs — a single real-FSAC
  `check`/`hover` test returns `[]` (`Assert.NotEmpty` empty) in ~200 ms, i.e.
  `fsharp/documentAnalyzed` fires but `latestDiagnosticsByUri` holds nothing for
  that URI yet. `FsacBridge.awaitDocumentAnalyzed` assumes FSAC always sends
  `publishDiagnostics` *before* `documentAnalyzed` (see its doc comment); under
  the shared long-lived process — especially right after the
  `KillForTests()` respawn test — that ordering does not always hold. Reproduced
  on a clean `main-vscode` tree (2 of 5 runs) with none of this task's changes.
  A fix would make the `documentAnalyzed` handler tolerate the missing push
  (short grace wait, or re-read on the next push for that URI+version). Left as
  an F7 follow-up.
- Long notebook paths vs `MAX_PATH` for the synthetic filename — untouched
  exposure, worth a hash-suffix scheme if it ever bites.
