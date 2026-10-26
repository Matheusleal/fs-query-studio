# F9 — "New F# Query Notebook" create commands

**Status:** Concluído
**Started:** 2026-09-08
**Last updated:** 2026-09-08
**Related:** branch `main-vscode`; `docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md`; `editors/vscode/src/notebook/serializer.ts` (`FSQNB_NOTEBOOK_TYPE`), `editors/vscode/src/notebook/fsqnb.ts` (`serializeFsqnb`)

---

## 1. Planning

### Objective
There is no UI to create a `.fsqnb` file — a user must hand-make a file with
that extension before the notebook opens. Add first-class "new notebook"
entry points: Command Palette, File > New File…, and Explorer right-click.

### Requirements / Scope
- Command `fs-query-studio.newNotebook` — opens a new **untitled**
  `fsquery-notebook` with **one empty F# code cell**. Reachable from the
  Command Palette and from `contributes.menus.file/newFile` (which also
  feeds the Welcome page "New…" list).
- Command `fs-query-studio.newNotebookHere` — Explorer context menu on a
  folder; prompts for a name (default `query.fsqnb`, `.fsqnb` appended if
  absent), writes a valid empty `.fsqnb` to disk in that folder, opens it.
- New module `src/notebook/new-notebook.ts`, dependency-injected for
  vitest (matches `src/connections/add.ts`) — no direct `vscode.*` IO in
  the unit-tested code path.
- **Non-goals:** starter template with sample cells (chose one empty code
  cell); a `.fsqnb` language/file-icon contribution; changing the
  serializer or the on-disk format; any worker / renderer / protocol
  change.

### Approach & Key Decisions
- **Decision — untitled for palette / File>New File, on-disk for Explorer.**
  *Reasoning:* `openNotebookDocument(type, data)` with no URI is VS Code's
  normal "New File" behaviour (user picks a path on first save); the
  Explorer entry already has a target folder, so writing the file
  immediately is the natural fit and lets it appear in the tree at once.
- **Decision — the create logic is DI'd, not `vscode`-coupled.**
  *Reasoning:* keeps unit tests stub-free; the thin `vscode` adapters live
  in `extension.ts` inside the command closures (so `activate` still needs
  nothing new from the test stub). Same shape as `add.ts` /
  `restartKernel`.
- **Decision — collision handling throws a typed error.**
  `showInputBox.validateInput` is synchronous and cannot stat the disk, so
  `createNotebookFileInFolder` re-checks existence after the prompt and
  throws `NotebookFileExistsError` (carrying `userMessage`); `extension.ts`
  surfaces it with `showErrorMessage`, mirroring the restart-command
  pattern. *Alternative:* silently pick a free name (`query-2.fsqnb`) —
  rejected as surprising.
- **Decision — Explorer entry is folder-only** (`explorerResourceIsFolder`
  in `when`). The empty-explorer-area right-click has no clean `when`
  clause and is already covered by the palette / File>New File paths.

### Risks & Assumptions
- Assumes `contributes.menus.file/newFile` is the right key for the
  File > New File… picker (VS Code ≥ 1.70; engine here is `^1.90.0`).
- Assumes contributed commands auto-activate the extension on invocation
  (implicit `onCommand:` for `engines.vscode ≥ 1.74`) — no
  `activationEvents` entry added. To be confirmed in the extension-host
  integration run.
- The test `vscode` stub gains `Uri.file`; no other stub surface needed
  because the notebook-open / fs-write calls are only made through the
  injected deps.

---

## 2. Execution

### 2026-09-08 — task doc + planning
- Files touched: `docs/tasks/f9-new-notebook-command.md` (new).
- Read `extension.ts`, `serializer.ts`, `fsqnb.ts`, `test/vscode-stub.ts`,
  `src/connections/add.test.ts`, `src/extension.test.ts` for the DI +
  test conventions.

### 2026-09-08 — module (TDD) + wiring
- New `src/notebook/new-notebook.ts`: `emptyNotebookData`,
  `emptyFsqnbBytes`, `openNewUntitledNotebook(deps)`,
  `createNotebookFileInFolder(folder, deps)`, `validateNotebookName`,
  `NotebookFileExistsError`. All IO injected — the module imports `vscode`
  only for the `NotebookCellData` / `NotebookData` / `NotebookCellKind`
  values.
- New `src/notebook/new-notebook.test.ts` (9 cases) written first, RED
  (module missing), then GREEN.
- `src/extension.ts`: registered `fs-query-studio.newNotebook` (untitled,
  via `openNewUntitledNotebook`) and `fs-query-studio.newNotebookHere`
  (folder, via `createNotebookFileInFolder`) with thin `vscode` adapters
  inside the command closures; both pushed to `subscriptions`. The
  `newNotebookHere` folder falls back to `workspaceFolders[0]` when no
  resource is passed.
- `test/vscode-stub.ts`: added `Uri.file`.
- `src/extension.test.ts`: +2 cases — both command ids registered;
  executing `newNotebook` opens an `fsquery-notebook` with one cell.

### 2026-09-08 — package.json contributions
- 2 commands (`newNotebook` with `shortTitle: "F# Query Notebook"`,
  `newNotebookHere`).
- `menus.file/newFile` → `newNotebook` (also feeds the Welcome "New…"
  list).
- `menus.explorer/context` → `newNotebookHere`, `when:
  "explorerViewletVisible && explorerResourceIsFolder"`.
- `menus.commandPalette` → `newNotebookHere` hidden (`when: false`); it
  needs an Explorer folder, palette users take `newNotebook`.
- No `activationEvents` entry — the extension-host run confirmed the
  contributed command activates the extension on first invocation.

### 2026-09-08 — integration test
- Added one spec to `test/integration/notebook.test.ts`:
  `fs-query-studio.newNotebook` opens an untitled `fsquery-notebook` with
  one empty `fsharp` cell — proves registration + implicit activation +
  the untitled path end-to-end in real VS Code.
- A second spec exercising `newNotebookHere` was dropped: `showInputBox`
  has no resolver in the headless host and hangs the command to a 60 s
  timeout. Cancel / collision / suffix are fully covered by the unit
  suite.

---

## 3. Validation

### Test Plan
- `src/notebook/new-notebook.test.ts` (vitest):
  - `emptyNotebookData()` → one cell, `Code` kind, `fsharp`, `""` value,
    `metadata {}`.
  - `emptyFsqnbBytes()` → decodes to JSON with `version: 1`, one
    `{ kind: "code", language: "fsharp", value: "" }` cell, `metadata {}`.
  - `openNewUntitledNotebook` → calls `openNotebookDocument(FSQNB_NOTEBOOK_TYPE,
    <data>)` then `showNotebookDocument` with the returned doc.
  - `createNotebookFileInFolder`:
    - appends `.fsqnb` when the typed name lacks it; writes the empty bytes
      to `<folder>/<name>.fsqnb`; then opens it.
    - keeps an existing `.fsqnb` extension as-is.
    - returns `undefined` and does **not** write when the prompt is
      cancelled.
    - throws `NotebookFileExistsError` (no write) when the target exists.
- `src/extension.test.ts` — `fs-query-studio.newNotebook` and
  `fs-query-studio.newNotebookHere` are registered by `activate`; invoking
  `newNotebook` through the stub does not throw.
- Regression: full `npm test`; `npm run compile`.
- Manual / integration: palette command opens an untitled notebook with one
  cell; File > New File… lists the entry; Explorer folder right-click
  writes and opens the file.

### Test Results
- `npm test` — **235 / 235** (was 224; +9 `new-notebook.test.ts`, +2
  `extension.test.ts`). `npm run compile` clean.
- `npx vscode-test --grep "fsquery-notebook integration"` — **5 / 5**,
  including the new `fs-query-studio.newNotebook` untitled-notebook spec
  and the three worker-backed specs.
- Not automated: File > New File… list entry, Welcome "New…" entry,
  Explorer folder right-click writing the file — on the manual checklist
  (the `showInputBox` prompt can't be driven headless).

### Acceptance Criteria Check
| Requirement | Verified how | Result |
|---|---|---|
| `newNotebook` opens an untitled notebook, one empty F# cell | `new-notebook.test.ts`, `extension.test.ts`, integration spec | ✅ |
| Command in Palette + File > New File… menu | `package.json` `commands` + `menus.file/newFile` | ✅ (menu entry manual) |
| `newNotebookHere` writes + opens a valid `.fsqnb` in a folder | `new-notebook.test.ts` "appends .fsqnb, writes…, opens it" | ✅ (unit) |
| name defaulting / `.fsqnb` suffix / collision / cancel | `new-notebook.test.ts` (5 cases) | ✅ |
| DI, no vscode coupling in unit path | tests run against the stub; module only uses `vscode` enum/class values | ✅ |
| no worker/renderer/protocol change | diff: only `src/notebook/new-notebook.ts` (new), `src/extension.ts`, `package.json`, tests, stub | ✅ |

---

## 4. Notes & Follow-ups
- Implicit `onCommand` activation fired fine in the extension-host run — no
  `activationEvents` entry needed.
- A `.fsqnb` file-icon / language contribution (so the file shows a
  distinct icon in the Explorer before it is opened) is a possible later
  polish — out of scope here.
- No integration coverage for the `newNotebookHere` on-disk path because
  `showInputBox` can't be resolved headless. If it regresses, the unit
  suite catches the logic; the `vscode` glue is a thin adapter.
</content>
