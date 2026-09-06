# VS Code Extension — F4 Cell Intelligence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give `.fsqnb` code cells in-editor F# completion, hover, signature help, and diagnostics by forwarding to the worker's existing `analysis/*` methods, with executed-cell concatenation and prologue-aware line offsetting.

**Architecture:** A new `editors/vscode/src/lang/` module. `cell-context.ts` (pure) builds the concatenated analysis source from a notebook's *executed* fsharp code cells plus the target cell, tracks a per-cell version, and maps diagnostic ranges back into the target cell. `analysis-client.ts` (pure, DI'd worker) wraps the four `analysis/*` RPCs, swallowing worker errors. `diagnostics.ts` holds a fully dependency-injected `DiagnosticsManager` (debounce + lifecycle, no `vscode` import). `providers.ts` is the only `vscode`-bound file: it registers the three interactive providers for selector `{ language: "fsharp", notebookType: "fsquery-notebook" }`, owns a `DiagnosticCollection`, and wires text/notebook events into the manager. No `src/` F# project changes; the worker already answers every method. The Avalonia app is untouched.

**Tech Stack:** TypeScript 5, Node 24, `esbuild` (unchanged — single node/cjs extension bundle; no new renderer bundle), `vitest` (unit; `vscode` aliased to `test/vscode-stub.ts`), `@vscode/test-cli` + `@vscode/test-electron` (extension-host integration).

**Spec:** `docs/superpowers/specs/2026-09-06-vscode-extension-f4-cell-intelligence-design.md` (which itself implements the rollout row **F4 Cell intelligence** of `docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md`, plus the migration spec's *Architecture → Extension components* row `lang/providers.ts` and its *Data flow → Edit a cell* paragraph).

## Global Constraints

- **.NET floor:** worker requires .NET **10** on `PATH`. Reuse the existing `resolveDotnet` / `worker/session.ts`; never re-implement worker startup.
- **Node engine:** development on Node 24; `package.json` `engines.node` = `">=20"` (unchanged).
- **VS Code engine:** `package.json` `engines.vscode` = `"^1.90.0"` (unchanged).
- **Extension identity (placeholder until F6):** `name` = `fs-query-studio`, `publisher` = `fs-query-studio`, `displayName` = `F# Query Studio`.
- **Provider selector:** `{ language: "fsharp", notebookType: "fsquery-notebook" }`. Notebook type id `fsquery-notebook` (unchanged from F2). **No `contributes.languages` entry and no TextMate grammar this phase** — providers are registered programmatically in `activate`.
- **New `DiagnosticCollection` name:** `"F# Query Studio"`. **Diagnostic `source` string:** `"F# Query Studio"`.
- **Debounce:** diagnostics check = **300 ms** after the last edit to a cell. Interactive providers (completion/hover/signature help) have **no** manual debounce — VS Code drives their cadence.
- **Worker frames are camelCase on the wire** (`{ id, method, params }` / `{ id, result?, error? }`, error `{ code, message }`). The worker (FSharp.SystemTextJson) writes an absent `result`/`error` as explicit JSON `null` — treat `null` and `undefined` the same.
- **F# discriminated unions on the wire** use `{ "Case": "<Name>" }` (AdjacentTag). Applies here to `Diagnostic.severity` (received): `{ "Case": "Error" | "Warning" | "Information" }`. Decode with the existing `readUnionTag` from `src/protocol.ts`.
- **F# `option` on the wire** is unwrapped to the bare value or JSON `null` (FSharp.SystemTextJson). Applies to `HoverInfo` / `SignatureHelpInfo` results (`hover` / `signatureHelp` are `option`) and `Diagnostic.code` (`string option`).
- **Line/column base — the one asymmetry (verified in `src/FSharpQueryStudio.Fsac/Prologue.fs`):**
  - `analysis/complete`, `analysis/hover`, `analysis/signatureHelp` request params expect **1-based `line`**, **0-based `column`** (`shiftUp`'s doc comment: "A caller's own 1-based line").
  - `analysis/check` returns `Diagnostic.startLine` / `endLine` / `startColumn` / `endColumn` as **0-based** against the same unprefixed source.
  - VS Code `Position.line` and `Position.character` are both **0-based**.
  - So: interactive providers send `position.line + 1 + cellLineOffset`; `mapDiagnosticIntoCell` subtracts `cellLineOffset` only (no ±1).
- **Concatenation semantics:** the analysis source for a target cell = every `fsharp` **code** cell in the notebook whose `executionSummary?.executionOrder != null` **and** that is not the target, sorted ascending by `executionOrder`, joined with `"\n"`, then `"\n"` + the target cell's live text. `cellLineOffset` = the joined-prefix's line count (`prefixText.split("\n").length`), or `0` when the prefix is empty.
- **Diagnostics refresh triggers:** the target cell's own text edits (debounced) and the first time its document is opened (immediate). **Executing a cell does not refresh any other cell's diagnostics.** Clear on cell-document close and on cell removal.
- **Worker error during a check** leaves the cell's existing diagnostics untouched (the client returns `[]` on a clean no-diagnostics result and also on an error frame, matching `AnalysisService.check`; the manager only calls `publish` on a resolved check, and a *thrown* send is logged and dropped).
- **TDD:** every task writes the failing test first, runs it red, then implements. **Commit after every task.** Add a one-line `docs/DEVLOG.md` entry (`## 2026-09-06 — F4.N: <title>`) as the second-to-last step of every task.
- All new source lives under `editors/vscode/`. Do not modify any `src/` F# project.
- **Run the whole unit suite** (`npm test` in `editors/vscode`) at the end of each task, not just the new file — `test/vscode-stub.ts` is shared and a regression there breaks earlier tests silently.

---

### Task 1: Analysis wire types + severity decoder (`src/protocol.ts`)

**Files:**
- Modify: `editors/vscode/src/protocol.ts` (append a new section after `ResultEnvelope`)
- Test: `editors/vscode/src/protocol.test.ts` (append)

**Interfaces:**
- Consumes: existing `UnionTag`, `readUnionTag` from `src/protocol.ts`.
- Produces:
  - `type WireSeverity = "Error" | "Warning" | "Information"`
  - `function readSeverity(value: unknown): WireSeverity` — `readUnionTag(value)` then match; anything unrecognized → `"Error"`.
  - `interface AnalysisCheckParams { documentUri: string; version: number; source: string }`
  - `interface AnalysisPositionParams { documentUri: string; version: number; source: string; line: number; column: number }`
  - `interface WireDiagnostic { message: string; severity: UnionTag<WireSeverity>; startLine: number; startColumn: number; endLine: number; endColumn: number; code: string | null }`
  - `interface AnalysisCheckResult { diagnostics: WireDiagnostic[] }`
  - `interface AnalysisCompletionResult { items: string[] }`
  - `interface HoverInfo { displayName: string; type: string; signature: string | null; xmlDoc: string | null }`
  - `interface AnalysisHoverResult { hover: HoverInfo | null }`
  - `interface SignatureHelpInfo { displayName: string; parameters: string[]; returnType: string }`
  - `interface AnalysisSignatureHelpResult { signatureHelp: SignatureHelpInfo | null }`

- [ ] **Step 1: Write the failing test** — append to `editors/vscode/src/protocol.test.ts`:

```ts
import { readSeverity } from "./protocol";

describe("readSeverity", () => {
  it("decodes each F# DU severity tag", () => {
    expect(readSeverity({ Case: "Error" })).toBe("Error");
    expect(readSeverity({ Case: "Warning" })).toBe("Warning");
    expect(readSeverity({ Case: "Information" })).toBe("Information");
  });

  it("falls back to Error for anything unrecognized", () => {
    expect(readSeverity({ Case: "Nonsense" })).toBe("Error");
    expect(readSeverity(null)).toBe("Error");
    expect(readSeverity(undefined)).toBe("Error");
    expect(readSeverity("Warning")).toBe("Error"); // bare string, not a tag
    expect(readSeverity({})).toBe("Error");
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd editors/vscode && npx vitest run src/protocol.test.ts`
Expected: FAIL — `readSeverity` is not exported.

- [ ] **Step 3: Write minimal implementation** — append to `editors/vscode/src/protocol.ts`:

```ts
// --- F4: analysis/* request + response payloads ---

export type WireSeverity = "Error" | "Warning" | "Information";

/**
 * `Diagnostic.Severity` is a fieldless F# DU — FSharp.SystemTextJson
 * writes it as `{ "Case": "Error" }`. Anything we don't recognize is
 * treated as an error so a surprising value is never silently dropped.
 */
export function readSeverity(value: unknown): WireSeverity {
  switch (readUnionTag(value)) {
    case "Warning":
      return "Warning";
    case "Information":
      return "Information";
    default:
      return "Error";
  }
}

/** `analysis/check` request — mirror of `Protocol.AnalysisCheckParams`. */
export interface AnalysisCheckParams {
  documentUri: string;
  version: number;
  source: string;
}

/**
 * `analysis/complete` / `analysis/hover` / `analysis/signatureHelp`
 * request. `line` is **1-based**, `column` is **0-based** — the worker's
 * `Prologue.shiftUp` contract.
 */
export interface AnalysisPositionParams {
  documentUri: string;
  version: number;
  source: string;
  line: number;
  column: number;
}

/** One diagnostic as it arrives from `analysis/check`. Lines/columns are
 *  0-based against the *unprefixed* source we sent (the worker already
 *  shifted its FSAC prologue back out). */
export interface WireDiagnostic {
  message: string;
  severity: UnionTag<WireSeverity>;
  startLine: number;
  startColumn: number;
  endLine: number;
  endColumn: number;
  code: string | null;
}

export interface AnalysisCheckResult {
  diagnostics: WireDiagnostic[];
}

export interface AnalysisCompletionResult {
  items: string[];
}

export interface HoverInfo {
  displayName: string;
  type: string;
  signature: string | null;
  xmlDoc: string | null;
}

export interface AnalysisHoverResult {
  hover: HoverInfo | null;
}

export interface SignatureHelpInfo {
  displayName: string;
  parameters: string[];
  returnType: string;
}

export interface AnalysisSignatureHelpResult {
  signatureHelp: SignatureHelpInfo | null;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd editors/vscode && npx vitest run src/protocol.test.ts`
Expected: PASS.

- [ ] **Step 5: Run the full unit suite**

Run: `cd editors/vscode && npm test`
Expected: PASS (all pre-existing tests + the new `readSeverity` block).

- [ ] **Step 6: DEVLOG entry** — append to `docs/DEVLOG.md`:

```markdown
## 2026-09-06 — F4.1: analysis/* wire types

`src/protocol.ts`: request/response interfaces for the four `analysis/*` methods (`AnalysisCheckParams`, `AnalysisPositionParams` with its 1-based-line / 0-based-column contract, `WireDiagnostic`, and the completion/hover/signature-help result shapes), plus `readSeverity` decoding the `{ "Case": "Error" | "Warning" | "Information" }` DU tag with an Error fallback.
```

- [ ] **Step 7: Commit**

```bash
git add editors/vscode/src/protocol.ts editors/vscode/src/protocol.test.ts docs/DEVLOG.md
git commit -m "feat(vscode): analysis/* wire types + severity tag decoder for F4"
```

---

### Task 2: `buildAnalysisInput` — executed-cell concatenation (`src/lang/cell-context.ts`)

**Files:**
- Create: `editors/vscode/src/lang/cell-context.ts`
- Test: `editors/vscode/src/lang/cell-context.test.ts`

**Interfaces:**
- Consumes: nothing (pure; no `vscode` import).
- Produces:
  - `interface AnalysisCellDocument { uri: { toString(): string }; languageId: string; getText(): string; lineCount: number; version: number }`
  - `interface AnalysisCell { kind: number; document: AnalysisCellDocument; executionSummary?: { executionOrder?: number } }`
  - `interface AnalysisPosition { line: number; character: number }`
  - `interface AnalysisInput { source: string; line: number; column: number; cellLineOffset: number }`
  - `function buildAnalysisInput(cells: readonly AnalysisCell[], target: AnalysisCell, position?: AnalysisPosition): AnalysisInput`
  - `const CODE_CELL_KIND = 2` (equals `vscode.NotebookCellKind.Code`)

- [ ] **Step 1: Write the failing test** — `editors/vscode/src/lang/cell-context.test.ts`:

```ts
import { describe, it, expect } from "vitest";
import { buildAnalysisInput, type AnalysisCell } from "./cell-context";

function cell(
  uri: string,
  text: string,
  opts: { kind?: number; lang?: string; executionOrder?: number } = {},
): AnalysisCell {
  return {
    kind: opts.kind ?? 2,
    document: {
      uri: { toString: () => uri },
      languageId: opts.lang ?? "fsharp",
      getText: () => text,
      lineCount: text.split("\n").length,
      version: 1,
    },
    executionSummary:
      opts.executionOrder === undefined ? undefined : { executionOrder: opts.executionOrder },
  };
}

describe("buildAnalysisInput", () => {
  it("with no executed prefix, source is just the target text and offset is 0", () => {
    const t = cell("c1", "let x = 1");
    const r = buildAnalysisInput([t], t, { line: 0, character: 4 });
    expect(r.source).toBe("let x = 1");
    expect(r.cellLineOffset).toBe(0);
    expect(r.line).toBe(1); // 0-based 0 -> 1-based 1, + 0 prefix
    expect(r.column).toBe(4);
  });

  it("prepends executed fsharp code cells, newline-joined, and offsets the position", () => {
    const a = cell("a", "let a = 1\nlet b = 2", { executionOrder: 1 });
    const t = cell("t", "a + b");
    const r = buildAnalysisInput([a, t], t, { line: 0, character: 5 });
    expect(r.source).toBe("let a = 1\nlet b = 2\na + b");
    expect(r.cellLineOffset).toBe(2); // "let a = 1\nlet b = 2".split("\n").length === 2
    expect(r.line).toBe(3); // 0 + 1 + 2
    expect(r.column).toBe(5);
  });

  it("excludes markdown cells, non-fsharp cells, and never-executed cells", () => {
    const md = cell("md", "# title", { kind: 1, executionOrder: 1 });
    const csharp = cell("cs", "int x;", { lang: "csharp", executionOrder: 2 });
    const notRun = cell("nr", "let unused = 0");
    const ran = cell("r", "let y = 9", { executionOrder: 3 });
    const t = cell("t", "y");
    const r = buildAnalysisInput([md, csharp, notRun, ran, t], t, { line: 0, character: 1 });
    expect(r.source).toBe("let y = 9\ny");
    expect(r.cellLineOffset).toBe(1);
  });

  it("orders the prefix by executionOrder, not document order", () => {
    const first = cell("first", "let p = 1", { executionOrder: 2 }); // physically first, ran second
    const second = cell("second", "let q = 2", { executionOrder: 1 }); // physically second, ran first
    const t = cell("t", "p + q");
    const r = buildAnalysisInput([first, second, t], t, { line: 0, character: 0 });
    expect(r.source).toBe("let q = 2\nlet p = 1\np + q");
  });

  it("excludes the target cell from its own prefix even when it has executed", () => {
    const t = cell("t", "let z = 5", { executionOrder: 1 });
    const r = buildAnalysisInput([t], t, { line: 0, character: 0 });
    expect(r.source).toBe("let z = 5");
    expect(r.cellLineOffset).toBe(0);
  });

  it("returns line 0 / column 0 when no position is given (the check path)", () => {
    const a = cell("a", "let a = 1", { executionOrder: 1 });
    const t = cell("t", "a");
    const r = buildAnalysisInput([a, t], t);
    expect(r.line).toBe(0);
    expect(r.column).toBe(0);
    expect(r.source).toBe("let a = 1\na");
    expect(r.cellLineOffset).toBe(1);
  });

  it("offset is unaffected by a prefix cell whose text ends in a newline", () => {
    const a = cell("a", "let a = 1\n", { executionOrder: 1 }); // trailing newline
    const t = cell("t", "a");
    const r = buildAnalysisInput([a, t], t, { line: 0, character: 1 });
    // prefix text is "let a = 1\n" -> split -> ["let a = 1", ""] -> length 2
    expect(r.cellLineOffset).toBe(2);
    expect(r.source).toBe("let a = 1\n\na");
    expect(r.line).toBe(3); // 0 + 1 + 2
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd editors/vscode && npx vitest run src/lang/cell-context.test.ts`
Expected: FAIL — `Cannot find module './cell-context'`.

- [ ] **Step 3: Write minimal implementation** — `editors/vscode/src/lang/cell-context.ts`:

```ts
/**
 * Pure helpers for in-cell F# analysis. No `vscode` import — the shapes
 * below are the minimal slice of `vscode.NotebookCell` the logic needs,
 * so this module unit-tests with plain objects.
 */

/** Equals `vscode.NotebookCellKind.Code`. */
export const CODE_CELL_KIND = 2;

export interface AnalysisCellDocument {
  uri: { toString(): string };
  languageId: string;
  getText(): string;
  lineCount: number;
  version: number;
}

export interface AnalysisCell {
  kind: number;
  document: AnalysisCellDocument;
  executionSummary?: { executionOrder?: number };
}

export interface AnalysisPosition {
  line: number;
  character: number;
}

export interface AnalysisInput {
  /** Concatenated prefix + target text, sent verbatim as `source`. */
  source: string;
  /** 1-based line for an interactive request; 0 when no position given. */
  line: number;
  /** 0-based column; 0 when no position given. */
  column: number;
  /** Lines the prefix occupies — subtract from returned diagnostic lines. */
  cellLineOffset: number;
}

function hasExecuted(cell: AnalysisCell): boolean {
  return cell.executionSummary?.executionOrder != null;
}

export function buildAnalysisInput(
  cells: readonly AnalysisCell[],
  target: AnalysisCell,
  position?: AnalysisPosition,
): AnalysisInput {
  const targetUri = target.document.uri.toString();

  const prefixCells = cells
    .filter(
      (c) =>
        c.kind === CODE_CELL_KIND &&
        c.document.languageId === "fsharp" &&
        c.document.uri.toString() !== targetUri &&
        hasExecuted(c),
    )
    .sort(
      (a, b) =>
        (a.executionSummary!.executionOrder ?? 0) - (b.executionSummary!.executionOrder ?? 0),
    );

  const prefixText = prefixCells.map((c) => c.document.getText()).join("\n");
  const targetText = target.document.getText();
  const source = prefixText === "" ? targetText : `${prefixText}\n${targetText}`;
  const cellLineOffset = prefixText === "" ? 0 : prefixText.split("\n").length;

  return {
    source,
    line: position ? position.line + 1 + cellLineOffset : 0,
    column: position ? position.character : 0,
    cellLineOffset,
  };
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd editors/vscode && npx vitest run src/lang/cell-context.test.ts`
Expected: PASS.

- [ ] **Step 5: Run the full unit suite**

Run: `cd editors/vscode && npm test`
Expected: PASS.

- [ ] **Step 6: DEVLOG entry** — append to `docs/DEVLOG.md`:

```markdown
## 2026-09-06 — F4.2: buildAnalysisInput (executed-cell concatenation)

`src/lang/cell-context.ts` (pure, no vscode): `buildAnalysisInput(cells, target, position?)` prepends every executed fsharp code cell (ordered by `executionSummary.executionOrder`, target excluded) newline-joined ahead of the target's live text, returns the concatenated `source`, the prefix line count as `cellLineOffset`, and a 1-based `line` (`position.line + 1 + cellLineOffset`) for interactive requests / `0` for the check path.
```

- [ ] **Step 7: Commit**

```bash
git add editors/vscode/src/lang/cell-context.ts editors/vscode/src/lang/cell-context.test.ts docs/DEVLOG.md
git commit -m "feat(vscode): buildAnalysisInput concatenates executed cells for F4 analysis"
```

---

### Task 3: `mapDiagnosticIntoCell` + `versionFor` (`src/lang/cell-context.ts`)

**Files:**
- Modify: `editors/vscode/src/lang/cell-context.ts`
- Test: `editors/vscode/src/lang/cell-context.test.ts` (append)

**Interfaces:**
- Consumes: `WireDiagnostic`, `WireSeverity`, `readSeverity` from `../protocol` (Task 1).
- Produces:
  - `interface CellDiagnostic { message: string; severity: WireSeverity; startLine: number; startColumn: number; endLine: number; endColumn: number; code: string | null }`
  - `function mapDiagnosticIntoCell(wire: WireDiagnostic, cellLineOffset: number, cellLineCount: number): CellDiagnostic | null`
  - `function versionFor(cellUri: string, source: string): number`
  - `function forgetVersion(cellUri: string): void`

- [ ] **Step 1: Write the failing test** — append to `editors/vscode/src/lang/cell-context.test.ts`:

```ts
import { mapDiagnosticIntoCell, versionFor, forgetVersion } from "./cell-context";
import type { WireDiagnostic } from "../protocol";

function wire(over: Partial<WireDiagnostic> = {}): WireDiagnostic {
  return {
    message: "oops",
    severity: { Case: "Error" },
    startLine: 0,
    startColumn: 0,
    endLine: 0,
    endColumn: 1,
    code: null,
    ...over,
  };
}

describe("mapDiagnosticIntoCell", () => {
  it("shifts an in-cell diagnostic back by the prefix offset", () => {
    const d = mapDiagnosticIntoCell(wire({ startLine: 5, endLine: 5 }), 3, 10);
    expect(d).not.toBeNull();
    expect(d!.startLine).toBe(2);
    expect(d!.endLine).toBe(2);
  });

  it("drops a diagnostic that lands entirely above the cell (in the prefix)", () => {
    expect(mapDiagnosticIntoCell(wire({ startLine: 1, endLine: 1 }), 3, 10)).toBeNull();
  });

  it("drops a diagnostic that starts past the end of the cell", () => {
    expect(mapDiagnosticIntoCell(wire({ startLine: 20, endLine: 20 }), 3, 5)).toBeNull();
  });

  it("clamps a diagnostic straddling the prefix/cell boundary into range", () => {
    const d = mapDiagnosticIntoCell(wire({ startLine: 2, endLine: 4 }), 3, 5);
    expect(d).not.toBeNull();
    expect(d!.startLine).toBe(0); // 2 - 3 = -1 -> clamped to 0
    expect(d!.endLine).toBe(1); // 4 - 3 = 1
  });

  it("decodes the severity tag and carries the code through", () => {
    const d = mapDiagnosticIntoCell(
      wire({ startLine: 3, endLine: 3, severity: { Case: "Warning" }, code: "FS0025" }),
      3,
      5,
    );
    expect(d!.severity).toBe("Warning");
    expect(d!.code).toBe("FS0025");
  });
});

describe("versionFor / forgetVersion", () => {
  it("returns a stable number for an unchanged source and bumps on change", () => {
    forgetVersion("v1");
    const a = versionFor("v1", "let x = 1");
    expect(versionFor("v1", "let x = 1")).toBe(a);
    expect(versionFor("v1", "let x = 2")).toBeGreaterThan(a);
  });

  it("tracks URIs independently", () => {
    forgetVersion("vA");
    forgetVersion("vB");
    const a = versionFor("vA", "same");
    const b = versionFor("vB", "same");
    versionFor("vA", "changed");
    expect(versionFor("vB", "same")).toBe(b); // vB untouched by vA's bump
    expect(a).toBeDefined();
  });

  it("forgetVersion drops the entry so the next source counts as new", () => {
    versionFor("vC", "one");
    forgetVersion("vC");
    // no assertion on the exact number; just that it does not throw and returns a number
    expect(typeof versionFor("vC", "one")).toBe("number");
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd editors/vscode && npx vitest run src/lang/cell-context.test.ts`
Expected: FAIL — `mapDiagnosticIntoCell` / `versionFor` / `forgetVersion` not exported.

- [ ] **Step 3: Write minimal implementation** — append to `editors/vscode/src/lang/cell-context.ts` (add the import at the top):

```ts
import { readSeverity, type WireDiagnostic, type WireSeverity } from "../protocol";
```

```ts
export interface CellDiagnostic {
  message: string;
  severity: WireSeverity;
  startLine: number;
  startColumn: number;
  endLine: number;
  endColumn: number;
  code: string | null;
}

function clamp(n: number, lo: number, hi: number): number {
  return Math.max(lo, Math.min(hi, n));
}

/**
 * Convert a `WireDiagnostic` (0-based against the concatenated source)
 * into a cell-local diagnostic. Returns `null` when the diagnostic
 * belongs to a prefix cell, not the target. A diagnostic straddling the
 * boundary is clamped into `[0, cellLineCount - 1]`.
 */
export function mapDiagnosticIntoCell(
  wire: WireDiagnostic,
  cellLineOffset: number,
  cellLineCount: number,
): CellDiagnostic | null {
  const startLine = wire.startLine - cellLineOffset;
  const endLine = wire.endLine - cellLineOffset;
  if (endLine < 0 || startLine >= cellLineCount) {
    return null;
  }
  const lastLine = Math.max(0, cellLineCount - 1);
  return {
    message: wire.message,
    severity: readSeverity(wire.severity),
    startLine: clamp(startLine, 0, lastLine),
    startColumn: wire.startColumn,
    endLine: clamp(endLine, 0, lastLine),
    endColumn: wire.endColumn,
    code: wire.code,
  };
}

const versions = new Map<string, { source: string; version: number }>();

/**
 * A monotonic per-cell version that changes whenever `source` differs
 * from the last value seen for `cellUri`. The worker keys FSAC's
 * in-memory document by `(documentUri, version)`, and the concatenated
 * source can change without the cell's own `document.version` moving, so
 * we cannot reuse that.
 */
export function versionFor(cellUri: string, source: string): number {
  const prev = versions.get(cellUri);
  if (prev && prev.source === source) {
    return prev.version;
  }
  const version = (prev?.version ?? 0) + 1;
  versions.set(cellUri, { source, version });
  return version;
}

export function forgetVersion(cellUri: string): void {
  versions.delete(cellUri);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd editors/vscode && npx vitest run src/lang/cell-context.test.ts`
Expected: PASS.

- [ ] **Step 5: Run the full unit suite**

Run: `cd editors/vscode && npm test`
Expected: PASS.

- [ ] **Step 6: DEVLOG entry** — append to `docs/DEVLOG.md`:

```markdown
## 2026-09-06 — F4.3: diagnostic remap + per-cell version tracking

`src/lang/cell-context.ts`: `mapDiagnosticIntoCell` subtracts `cellLineOffset` from a wire diagnostic's lines, drops any that fall in the prefix, clamps a straddler into `[0, cellLineCount)`, and decodes the severity tag; `versionFor(cellUri, source)` / `forgetVersion` give FSAC a version that moves whenever the concatenated source changes (the cell's own `document.version` does not).
```

- [ ] **Step 7: Commit**

```bash
git add editors/vscode/src/lang/cell-context.ts editors/vscode/src/lang/cell-context.test.ts docs/DEVLOG.md
git commit -m "feat(vscode): map analysis diagnostics into cell coords + version tracker"
```

---

### Task 4: `analysis-client.ts` — the four `analysis/*` RPC wrappers

**Files:**
- Create: `editors/vscode/src/lang/analysis-client.ts`
- Test: `editors/vscode/src/lang/analysis-client.test.ts`

**Interfaces:**
- Consumes: `newRequestId`, `WorkerResponseFrame`, `AnalysisCheckParams`, `AnalysisPositionParams`, `WireDiagnostic`, `HoverInfo`, `SignatureHelpInfo` from `../protocol` (Task 1).
- Produces:
  - `interface WorkerLike { send(req: { id: string; method: string; params: unknown }): Promise<WorkerResponseFrame> }`
  - `function checkAnalysis(worker: WorkerLike, params: AnalysisCheckParams): Promise<WireDiagnostic[]>`
  - `function completeAnalysis(worker: WorkerLike, params: AnalysisPositionParams): Promise<string[]>`
  - `function hoverAnalysis(worker: WorkerLike, params: AnalysisPositionParams): Promise<HoverInfo | undefined>`
  - `function signatureHelpAnalysis(worker: WorkerLike, params: AnalysisPositionParams): Promise<SignatureHelpInfo | undefined>`

- [ ] **Step 1: Write the failing test** — `editors/vscode/src/lang/analysis-client.test.ts`:

```ts
import { describe, it, expect, vi } from "vitest";
import type { WorkerResponseFrame } from "../protocol";
import {
  checkAnalysis,
  completeAnalysis,
  hoverAnalysis,
  signatureHelpAnalysis,
  type WorkerLike,
} from "./analysis-client";

function fakeWorker(response: WorkerResponseFrame | (() => Promise<WorkerResponseFrame>)): {
  worker: WorkerLike;
  calls: Array<{ method: string; params: unknown }>;
} {
  const calls: Array<{ method: string; params: unknown }> = [];
  const worker: WorkerLike = {
    send: (req) => {
      calls.push({ method: req.method, params: req.params });
      return typeof response === "function" ? response() : Promise.resolve(response);
    },
  };
  return { worker, calls };
}

const checkParams = { documentUri: "c1", version: 3, source: "let x = 1" };
const posParams = { documentUri: "c1", version: 3, source: "x", line: 1, column: 1 };

describe("checkAnalysis", () => {
  it("sends analysis/check with the params and returns the diagnostics", async () => {
    const diag = {
      message: "m",
      severity: { Case: "Error" as const },
      startLine: 0,
      startColumn: 0,
      endLine: 0,
      endColumn: 1,
      code: null,
    };
    const { worker, calls } = fakeWorker({ id: "1", result: { diagnostics: [diag] } });
    const out = await checkAnalysis(worker, checkParams);
    expect(calls[0]).toEqual({ method: "analysis/check", params: checkParams });
    expect(out).toEqual([diag]);
  });

  it("returns [] on an error frame, a null result, or a thrown send", async () => {
    expect(
      await checkAnalysis(fakeWorker({ id: "1", error: { code: "x", message: "y" } }).worker, checkParams),
    ).toEqual([]);
    expect(await checkAnalysis(fakeWorker({ id: "1", result: null }).worker, checkParams)).toEqual([]);
    expect(
      await checkAnalysis(
        fakeWorker(() => Promise.reject(new Error("boom"))).worker,
        checkParams,
      ),
    ).toEqual([]);
  });
});

describe("completeAnalysis", () => {
  it("sends analysis/complete and returns items", async () => {
    const { worker, calls } = fakeWorker({ id: "1", result: { items: ["foo", "bar"] } });
    expect(await completeAnalysis(worker, posParams)).toEqual(["foo", "bar"]);
    expect(calls[0].method).toBe("analysis/complete");
  });

  it("returns [] on error / null / throw", async () => {
    expect(
      await completeAnalysis(fakeWorker({ id: "1", error: { code: "x", message: "y" } }).worker, posParams),
    ).toEqual([]);
    expect(await completeAnalysis(fakeWorker({ id: "1", result: null }).worker, posParams)).toEqual([]);
  });
});

describe("hoverAnalysis", () => {
  it("sends analysis/hover and returns the hover, or undefined", async () => {
    const hover = { displayName: "x", type: "int", signature: null, xmlDoc: null };
    const { worker, calls } = fakeWorker({ id: "1", result: { hover } });
    expect(await hoverAnalysis(worker, posParams)).toEqual(hover);
    expect(calls[0].method).toBe("analysis/hover");
    expect(await hoverAnalysis(fakeWorker({ id: "1", result: { hover: null } }).worker, posParams)).toBeUndefined();
    expect(
      await hoverAnalysis(fakeWorker({ id: "1", error: { code: "x", message: "y" } }).worker, posParams),
    ).toBeUndefined();
  });
});

describe("signatureHelpAnalysis", () => {
  it("sends analysis/signatureHelp and returns it, or undefined", async () => {
    const sh = { displayName: "f", parameters: ["a: int"], returnType: "int" };
    const { worker, calls } = fakeWorker({ id: "1", result: { signatureHelp: sh } });
    expect(await signatureHelpAnalysis(worker, posParams)).toEqual(sh);
    expect(calls[0].method).toBe("analysis/signatureHelp");
    expect(
      await signatureHelpAnalysis(fakeWorker({ id: "1", result: { signatureHelp: null } }).worker, posParams),
    ).toBeUndefined();
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd editors/vscode && npx vitest run src/lang/analysis-client.test.ts`
Expected: FAIL — `Cannot find module './analysis-client'`.

- [ ] **Step 3: Write minimal implementation** — `editors/vscode/src/lang/analysis-client.ts`:

```ts
import {
  newRequestId,
  type AnalysisCheckParams,
  type AnalysisPositionParams,
  type HoverInfo,
  type SignatureHelpInfo,
  type WireDiagnostic,
  type WorkerResponseFrame,
} from "../protocol";

/**
 * The one `WorkerClient` method these wrappers need — injectable for
 * tests, and the same shape `notebook/controller.ts` and
 * `notebook/renderer-host.ts` already depend on.
 */
export interface WorkerLike {
  send(req: { id: string; method: string; params: unknown }): Promise<WorkerResponseFrame>;
}

/**
 * Every wrapper swallows a worker error into an empty result: a failed
 * or crashed analysis call must never surface as a thrown provider (VS
 * Code would show an error toast on every keystroke). Mirrors
 * `App/AnalysisService.fs`.
 */
async function call<T>(
  worker: WorkerLike,
  method: string,
  params: unknown,
  pick: (result: Record<string, unknown>) => T,
  empty: T,
): Promise<T> {
  let response: WorkerResponseFrame;
  try {
    response = await worker.send({ id: newRequestId(), method, params });
  } catch {
    return empty;
  }
  if (response.error || response.result == null) {
    return empty;
  }
  return pick(response.result as Record<string, unknown>);
}

export function checkAnalysis(
  worker: WorkerLike,
  params: AnalysisCheckParams,
): Promise<WireDiagnostic[]> {
  return call(
    worker,
    "analysis/check",
    params,
    (r) => (r.diagnostics as WireDiagnostic[] | undefined) ?? [],
    [],
  );
}

export function completeAnalysis(
  worker: WorkerLike,
  params: AnalysisPositionParams,
): Promise<string[]> {
  return call(worker, "analysis/complete", params, (r) => (r.items as string[] | undefined) ?? [], []);
}

export function hoverAnalysis(
  worker: WorkerLike,
  params: AnalysisPositionParams,
): Promise<HoverInfo | undefined> {
  return call(
    worker,
    "analysis/hover",
    params,
    (r) => (r.hover as HoverInfo | null) ?? undefined,
    undefined,
  );
}

export function signatureHelpAnalysis(
  worker: WorkerLike,
  params: AnalysisPositionParams,
): Promise<SignatureHelpInfo | undefined> {
  return call(
    worker,
    "analysis/signatureHelp",
    params,
    (r) => (r.signatureHelp as SignatureHelpInfo | null) ?? undefined,
    undefined,
  );
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd editors/vscode && npx vitest run src/lang/analysis-client.test.ts`
Expected: PASS.

- [ ] **Step 5: Run the full unit suite**

Run: `cd editors/vscode && npm test`
Expected: PASS.

- [ ] **Step 6: DEVLOG entry** — append to `docs/DEVLOG.md`:

```markdown
## 2026-09-06 — F4.4: analysis-client (analysis/* RPC wrappers)

`src/lang/analysis-client.ts`: `checkAnalysis` / `completeAnalysis` / `hoverAnalysis` / `signatureHelpAnalysis` over an injected `WorkerLike`, each collapsing an error frame, a null result, or a thrown send into `[]` / `undefined` so a provider never throws on a keystroke (mirrors `App/AnalysisService.fs`).
```

- [ ] **Step 7: Commit**

```bash
git add editors/vscode/src/lang/analysis-client.ts editors/vscode/src/lang/analysis-client.test.ts docs/DEVLOG.md
git commit -m "feat(vscode): analysis-client wrappers for the four analysis/* methods"
```

---

### Task 5: `DiagnosticsManager` — debounce + lifecycle (`src/lang/diagnostics.ts`)

**Files:**
- Create: `editors/vscode/src/lang/diagnostics.ts`
- Test: `editors/vscode/src/lang/diagnostics.test.ts`

No `vscode` import and **no `vscode-stub` changes** — the manager is fully dependency-injected, including its timer.

**Interfaces:**
- Consumes: `buildAnalysisInput`, `versionFor`, `forgetVersion`, `mapDiagnosticIntoCell`, `AnalysisCell`, `CellDiagnostic` from `./cell-context`; `checkAnalysis`, `WorkerLike` from `./analysis-client`.
- Produces:
  - `interface DiagnosticsManagerDeps { getWorker: () => Promise<WorkerLike>; findNotebookOf: (cellUri: string) => readonly AnalysisCell[] | undefined; publish: (cellUri: string, diags: CellDiagnostic[]) => void; clear: (cellUri: string) => void; log: (line: string) => void; debounceMs?: number; setTimer?: (fn: () => void, ms: number) => unknown; clearTimer?: (handle: unknown) => void }`
  - `class DiagnosticsManager` with `onCellChanged(cellUri: string): void`, `onCellOpened(cellUri: string): void`, `onCellClosed(cellUri: string): void`, `dispose(): void`

- [ ] **Step 1: Write the failing test** — `editors/vscode/src/lang/diagnostics.test.ts`:

```ts
import { describe, it, expect, vi } from "vitest";
import { DiagnosticsManager, type DiagnosticsManagerDeps } from "./diagnostics";
import type { AnalysisCell } from "./cell-context";
import type { WorkerResponseFrame } from "../protocol";

function codeCell(uri: string, text: string, executionOrder?: number): AnalysisCell {
  return {
    kind: 2,
    document: {
      uri: { toString: () => uri },
      languageId: "fsharp",
      getText: () => text,
      lineCount: text.split("\n").length,
      version: 1,
    },
    executionSummary: executionOrder === undefined ? undefined : { executionOrder },
  };
}

/** A manual clock: `setTimer` records the callback; `flush()` runs it. */
function manualTimers() {
  let pending: Array<{ id: number; fn: () => void }> = [];
  let nextId = 1;
  return {
    setTimer: (fn: () => void) => {
      const id = nextId++;
      pending.push({ id, fn });
      return id;
    },
    clearTimer: (handle: unknown) => {
      pending = pending.filter((p) => p.id !== handle);
    },
    flush: () => {
      const run = pending;
      pending = [];
      run.forEach((p) => p.fn());
    },
    get count() {
      return pending.length;
    },
  };
}

function baseDeps(over: Partial<DiagnosticsManagerDeps> = {}): {
  deps: DiagnosticsManagerDeps;
  sends: number;
  published: Array<{ uri: string; count: number }>;
  cleared: string[];
} {
  let sends = 0;
  const published: Array<{ uri: string; count: number }> = [];
  const cleared: string[] = [];
  const okResponse: WorkerResponseFrame = { id: "1", result: { diagnostics: [] } };
  const deps: DiagnosticsManagerDeps = {
    getWorker: async () => ({
      send: async () => {
        sends++;
        return okResponse;
      },
    }),
    findNotebookOf: (uri) => [codeCell(uri, "let x = 1")],
    publish: (uri, diags) => published.push({ uri, count: diags.length }),
    clear: (uri) => cleared.push(uri),
    log: () => {},
    debounceMs: 300,
    ...over,
  };
  return { deps, get sends() { return sends; }, published, cleared };
}

describe("DiagnosticsManager", () => {
  it("coalesces rapid edits into one check after the debounce", async () => {
    const t = manualTimers();
    const b = baseDeps({ setTimer: t.setTimer, clearTimer: t.clearTimer });
    const mgr = new DiagnosticsManager(b.deps);

    mgr.onCellChanged("c1");
    mgr.onCellChanged("c1");
    mgr.onCellChanged("c1");
    expect(t.count).toBe(1); // only the latest timer is pending
    expect(b.sends).toBe(0);

    t.flush();
    await new Promise((r) => setImmediate(r)); // let the async check settle
    expect(b.sends).toBe(1);
    expect(b.published).toEqual([{ uri: "c1", count: 0 }]);
  });

  it("checks immediately on cell open, with no timer", async () => {
    const t = manualTimers();
    const b = baseDeps({ setTimer: t.setTimer, clearTimer: t.clearTimer });
    const mgr = new DiagnosticsManager(b.deps);
    mgr.onCellOpened("c1");
    await new Promise((r) => setImmediate(r));
    expect(t.count).toBe(0);
    expect(b.sends).toBe(1);
  });

  it("clears the cell and cancels a pending timer on close", () => {
    const t = manualTimers();
    const b = baseDeps({ setTimer: t.setTimer, clearTimer: t.clearTimer });
    const mgr = new DiagnosticsManager(b.deps);
    mgr.onCellChanged("c1");
    expect(t.count).toBe(1);
    mgr.onCellClosed("c1");
    expect(t.count).toBe(0);
    expect(b.cleared).toEqual(["c1"]);
  });

  it("publishes only diagnostics that map into the cell", async () => {
    const t = manualTimers();
    const b = baseDeps({
      setTimer: t.setTimer,
      clearTimer: t.clearTimer,
      findNotebookOf: (uri) => [
        codeCell("prefix", "let a = 1", 1),
        codeCell(uri, "a +\nlet b = 2"),
      ],
      getWorker: async () => ({
        send: async () => ({
          id: "1",
          result: {
            diagnostics: [
              // offset is 1 (prefix "let a = 1"); cell has 2 lines
              { message: "in cell", severity: { Case: "Error" }, startLine: 1, startColumn: 0, endLine: 1, endColumn: 3, code: null },
              { message: "in prefix", severity: { Case: "Error" }, startLine: 0, startColumn: 0, endLine: 0, endColumn: 1, code: null },
            ],
          },
        }),
      }),
    });
    const mgr = new DiagnosticsManager(b.deps);
    mgr.onCellChanged("c1");
    t.flush();
    await new Promise((r) => setImmediate(r));
    expect(b.published).toEqual([{ uri: "c1", count: 1 }]);
  });

  it("does not publish (and does not throw) when the worker send rejects", async () => {
    const t = manualTimers();
    const logs: string[] = [];
    const b = baseDeps({
      setTimer: t.setTimer,
      clearTimer: t.clearTimer,
      log: (l) => logs.push(l),
      getWorker: async () => ({ send: async () => Promise.reject(new Error("boom")) }),
    });
    const mgr = new DiagnosticsManager(b.deps);
    mgr.onCellChanged("c1");
    t.flush();
    await new Promise((r) => setImmediate(r));
    expect(b.published).toEqual([]);
  });

  it("does nothing when findNotebookOf returns undefined", async () => {
    const t = manualTimers();
    const b = baseDeps({
      setTimer: t.setTimer,
      clearTimer: t.clearTimer,
      findNotebookOf: () => undefined,
    });
    const mgr = new DiagnosticsManager(b.deps);
    mgr.onCellChanged("c1");
    t.flush();
    await new Promise((r) => setImmediate(r));
    expect(b.published).toEqual([]);
  });

  it("cancels every pending timer on dispose", () => {
    const t = manualTimers();
    const b = baseDeps({ setTimer: t.setTimer, clearTimer: t.clearTimer });
    const mgr = new DiagnosticsManager(b.deps);
    mgr.onCellChanged("c1");
    mgr.onCellChanged("c2");
    expect(t.count).toBe(2);
    mgr.dispose();
    expect(t.count).toBe(0);
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd editors/vscode && npx vitest run src/lang/diagnostics.test.ts`
Expected: FAIL — `Cannot find module './diagnostics'`.

- [ ] **Step 3: Write minimal implementation** — `editors/vscode/src/lang/diagnostics.ts`:

```ts
import {
  buildAnalysisInput,
  forgetVersion,
  mapDiagnosticIntoCell,
  versionFor,
  type AnalysisCell,
  type CellDiagnostic,
} from "./cell-context";
import { checkAnalysis, type WorkerLike } from "./analysis-client";

export interface DiagnosticsManagerDeps {
  getWorker: () => Promise<WorkerLike>;
  /** The cells of the notebook that owns `cellUri`, or `undefined` if none. */
  findNotebookOf: (cellUri: string) => readonly AnalysisCell[] | undefined;
  /** Push the mapped diagnostics onto whatever collection the host owns. */
  publish: (cellUri: string, diags: CellDiagnostic[]) => void;
  /** Drop all diagnostics for a cell. */
  clear: (cellUri: string) => void;
  log: (line: string) => void;
  debounceMs?: number;
  setTimer?: (fn: () => void, ms: number) => unknown;
  clearTimer?: (handle: unknown) => void;
}

const DEFAULT_DEBOUNCE_MS = 300;

/**
 * Owns the per-cell debounce and the check → map → publish flow. Holds no
 * `vscode` reference: the host wires real events and a real
 * `DiagnosticCollection` in via `deps`.
 */
export class DiagnosticsManager {
  private readonly deps: DiagnosticsManagerDeps;
  private readonly debounceMs: number;
  private readonly setTimer: (fn: () => void, ms: number) => unknown;
  private readonly clearTimer: (handle: unknown) => void;
  private readonly timers = new Map<string, unknown>();

  constructor(deps: DiagnosticsManagerDeps) {
    this.deps = deps;
    this.debounceMs = deps.debounceMs ?? DEFAULT_DEBOUNCE_MS;
    this.setTimer = deps.setTimer ?? ((fn, ms) => setTimeout(fn, ms));
    this.clearTimer = deps.clearTimer ?? ((h) => clearTimeout(h as ReturnType<typeof setTimeout>));
  }

  onCellChanged(cellUri: string): void {
    const existing = this.timers.get(cellUri);
    if (existing !== undefined) {
      this.clearTimer(existing);
    }
    const handle = this.setTimer(() => {
      this.timers.delete(cellUri);
      void this.runCheck(cellUri);
    }, this.debounceMs);
    this.timers.set(cellUri, handle);
  }

  onCellOpened(cellUri: string): void {
    void this.runCheck(cellUri);
  }

  onCellClosed(cellUri: string): void {
    const existing = this.timers.get(cellUri);
    if (existing !== undefined) {
      this.clearTimer(existing);
      this.timers.delete(cellUri);
    }
    forgetVersion(cellUri);
    this.deps.clear(cellUri);
  }

  dispose(): void {
    for (const handle of this.timers.values()) {
      this.clearTimer(handle);
    }
    this.timers.clear();
  }

  private async runCheck(cellUri: string): Promise<void> {
    const cells = this.deps.findNotebookOf(cellUri);
    if (!cells) {
      return;
    }
    const target = cells.find((c) => c.document.uri.toString() === cellUri);
    if (!target) {
      return;
    }

    const { source, cellLineOffset } = buildAnalysisInput(cells, target);
    const version = versionFor(cellUri, source);

    let wire;
    try {
      const worker = await this.deps.getWorker();
      wire = await checkAnalysis(worker, { documentUri: cellUri, version, source });
    } catch (err) {
      this.deps.log(`diagnostics: check for ${cellUri} failed: ${(err as Error).message}`);
      return;
    }

    const mapped = wire
      .map((d) => mapDiagnosticIntoCell(d, cellLineOffset, target.document.lineCount))
      .filter((d): d is CellDiagnostic => d !== null);
    this.deps.publish(cellUri, mapped);
  }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd editors/vscode && npx vitest run src/lang/diagnostics.test.ts`
Expected: PASS.

- [ ] **Step 5: Run the full unit suite**

Run: `cd editors/vscode && npm test`
Expected: PASS.

- [ ] **Step 6: DEVLOG entry** — append to `docs/DEVLOG.md`:

```markdown
## 2026-09-06 — F4.5: DiagnosticsManager (debounce + lifecycle)

`src/lang/diagnostics.ts`: a fully dependency-injected `DiagnosticsManager` (no vscode import) — `onCellChanged` debounces 300 ms then runs `buildAnalysisInput` → `checkAnalysis` → `mapDiagnosticIntoCell` → `publish`; `onCellOpened` checks immediately; `onCellClosed` cancels the timer, `forgetVersion`s, and `clear`s; `dispose` cancels every pending timer. A thrown worker send is logged and drops silently, leaving existing squiggles in place.
```

- [ ] **Step 7: Commit**

```bash
git add editors/vscode/src/lang/diagnostics.ts editors/vscode/src/lang/diagnostics.test.ts docs/DEVLOG.md
git commit -m "feat(vscode): DiagnosticsManager with per-cell debounce and lifecycle"
```

---

### Task 6: `providers.ts` — the four providers + `registerLanguageFeatures`

**Files:**
- Modify: `editors/vscode/test/vscode-stub.ts` (add the `languages` surface + the classes the mappers construct)
- Create: `editors/vscode/src/lang/providers.ts`
- Test: `editors/vscode/src/lang/providers.test.ts`

**Interfaces:**
- Consumes: `buildAnalysisInput`, `AnalysisCell`, `CellDiagnostic` from `./cell-context`; `completeAnalysis`, `hoverAnalysis`, `signatureHelpAnalysis`, `WorkerLike` from `./analysis-client`; `DiagnosticsManager` from `./diagnostics`; `vscode`.
- Produces:
  - `interface LanguageFeaturesDeps { getWorker: () => Promise<WorkerLike>; log: (line: string) => void }`
  - `function registerLanguageFeatures(deps: LanguageFeaturesDeps): vscode.Disposable`
  - `const FSHARP_CELL_SELECTOR = { language: "fsharp", notebookType: "fsquery-notebook" }`

- [ ] **Step 1: Add the `vscode-stub` surface** — in `editors/vscode/test/vscode-stub.ts`:

Add these classes/enums near the other exported classes:

```ts
export class Position {
  constructor(
    public line: number,
    public character: number,
  ) {}
}

export class Range {
  constructor(
    public startLine: number,
    public startCharacter: number,
    public endLine: number,
    public endCharacter: number,
  ) {}
}

export enum DiagnosticSeverity {
  Error = 0,
  Warning = 1,
  Information = 2,
  Hint = 3,
}

export class Diagnostic {
  code: string | number | undefined;
  source: string | undefined;
  constructor(
    public range: Range,
    public message: string,
    public severity: DiagnosticSeverity = DiagnosticSeverity.Error,
  ) {}
}

export class MarkdownString {
  value = "";
  constructor(value?: string) {
    if (value) this.value = value;
  }
  appendText(s: string) {
    this.value += s;
    return this;
  }
  appendMarkdown(s: string) {
    this.value += s;
    return this;
  }
  appendCodeblock(code: string, language = "") {
    this.value += `\n\`\`\`${language}\n${code}\n\`\`\`\n`;
    return this;
  }
}

export class Hover {
  constructor(
    public contents: unknown,
    public range?: Range,
  ) {}
}

export enum CompletionItemKind {
  Text = 0,
  Method = 1,
  Function = 2,
  Field = 4,
  Variable = 5,
  Value = 11,
}

export class CompletionItem {
  constructor(
    public label: string,
    public kind?: CompletionItemKind,
  ) {}
}

export class ParameterInformation {
  constructor(
    public label: string,
    public documentation?: string | MarkdownString,
  ) {}
}

export class SignatureInformation {
  parameters: ParameterInformation[] = [];
  constructor(
    public label: string,
    public documentation?: string | MarkdownString,
  ) {}
}

export class SignatureHelp {
  signatures: SignatureInformation[] = [];
  activeSignature = 0;
  activeParameter = 0;
}

export const Disposable = {
  from: (...items: Array<{ dispose: () => void }>) => ({
    dispose: () => items.forEach((i) => i.dispose?.()),
  }),
};
```

Extend `workspace` with the event registrations and the notebook list (add to the existing `workspace` object literal):

```ts
  notebookDocuments: [] as Array<{
    notebookType: string;
    getCells: () => unknown[];
  }>,
  onDidChangeTextDocument(_fn: (e: unknown) => unknown): Disposable {
    return { dispose: () => {} };
  },
  onDidOpenTextDocument(_fn: (e: unknown) => unknown): Disposable {
    return { dispose: () => {} };
  },
  onDidCloseTextDocument(_fn: (e: unknown) => unknown): Disposable {
    return { dispose: () => {} };
  },
  onDidChangeNotebookDocument(_fn: (e: unknown) => unknown): Disposable {
    return { dispose: () => {} };
  },
```

Add a `languages` export that captures registrations:

```ts
export const languages = {
  _completion: [] as Array<{ selector: unknown; provider: unknown; triggers: string[] }>,
  _hover: [] as Array<{ selector: unknown; provider: unknown }>,
  _signature: [] as Array<{ selector: unknown; provider: unknown; triggers: string[] }>,
  _collections: [] as Array<{ name: string; set: unknown }>,
  registerCompletionItemProvider(selector: unknown, provider: unknown, ...triggers: string[]): Disposable {
    this._completion.push({ selector, provider, triggers });
    return { dispose: () => {} };
  },
  registerHoverProvider(selector: unknown, provider: unknown): Disposable {
    this._hover.push({ selector, provider });
    return { dispose: () => {} };
  },
  registerSignatureHelpProvider(
    selector: unknown,
    provider: unknown,
    ...triggers: string[]
  ): Disposable {
    this._signature.push({ selector, provider, triggers });
    return { dispose: () => {} };
  },
  createDiagnosticCollection(name: string) {
    const map = new Map<string, unknown>();
    const collection = {
      name,
      _map: map,
      set: (uri: { toString(): string }, diags: unknown) => map.set(uri.toString(), diags),
      delete: (uri: { toString(): string }) => map.delete(uri.toString()),
      get: (uri: { toString(): string }) => map.get(uri.toString()),
      clear: () => map.clear(),
      dispose: () => map.clear(),
    };
    this._collections.push({ name, set: collection.set });
    return collection;
  },
};
```

Add every new name to the `export default { ... }` object at the bottom (`languages`, `Position`, `Range`, `DiagnosticSeverity`, `Diagnostic`, `MarkdownString`, `Hover`, `CompletionItem`, `CompletionItemKind`, `SignatureHelp`, `SignatureInformation`, `ParameterInformation`, `Disposable`).

- [ ] **Step 2: Write the failing test** — `editors/vscode/src/lang/providers.test.ts`:

```ts
import { describe, it, expect } from "vitest";
import * as vscode from "vscode";
import { registerLanguageFeatures } from "./providers";
import type { WorkerResponseFrame } from "../protocol";

function fsharpCellDoc(uri: string, text: string) {
  return {
    uri: { toString: () => uri, scheme: "vscode-notebook-cell" },
    languageId: "fsharp",
    getText: () => text,
    lineCount: text.split("\n").length,
    version: 1,
  };
}

/** Register one fake notebook containing `docs` as its cells. */
function stageNotebook(cellDocs: ReturnType<typeof fsharpCellDoc>[]) {
  (vscode.workspace as unknown as { notebookDocuments: unknown[] }).notebookDocuments = [
    {
      notebookType: "fsquery-notebook",
      getCells: () =>
        cellDocs.map((document, i) => ({
          kind: 2,
          document,
          executionSummary: i === 0 ? { executionOrder: 1 } : undefined,
        })),
    },
  ];
}

function worker(result: unknown): { getWorker: () => Promise<{ send: () => Promise<WorkerResponseFrame> }> } {
  return {
    getWorker: async () => ({ send: async () => ({ id: "1", result }) as WorkerResponseFrame }),
  };
}

const token = { isCancellationRequested: false } as never;

describe("registerLanguageFeatures", () => {
  it("registers one completion, hover, and signature-help provider and a diagnostic collection", () => {
    const before = {
      c: vscode.languages._completion.length,
      h: vscode.languages._hover.length,
      s: vscode.languages._signature.length,
      d: vscode.languages._collections.length,
    };
    const sub = registerLanguageFeatures({ getWorker: async () => ({ send: async () => ({ id: "1" }) }), log: () => {} });
    expect(vscode.languages._completion.length).toBe(before.c + 1);
    expect(vscode.languages._hover.length).toBe(before.h + 1);
    expect(vscode.languages._signature.length).toBe(before.s + 1);
    expect(vscode.languages._collections.length).toBe(before.d + 1);
    sub.dispose();
  });

  it("completion maps analysis items to CompletionItems", async () => {
    const cell = fsharpCellDoc("c2", "answ");
    stageNotebook([fsharpCellDoc("c1", "let answer = 42"), cell]);
    registerLanguageFeatures({ ...worker({ items: ["answer"] }), log: () => {} });
    const provider = vscode.languages._completion.at(-1)!.provider as {
      provideCompletionItems: (d: unknown, p: unknown, t: unknown) => Promise<unknown[]>;
    };
    const items = await provider.provideCompletionItems(cell, new vscode.Position(0, 4), token);
    expect(items.map((i) => (i as vscode.CompletionItem).label)).toEqual(["answer"]);
  });

  it("completion returns undefined for a document that is not a cell in any notebook", async () => {
    (vscode.workspace as unknown as { notebookDocuments: unknown[] }).notebookDocuments = [];
    registerLanguageFeatures({ ...worker({ items: ["x"] }), log: () => {} });
    const provider = vscode.languages._completion.at(-1)!.provider as {
      provideCompletionItems: (d: unknown, p: unknown, t: unknown) => Promise<unknown>;
    };
    const out = await provider.provideCompletionItems(fsharpCellDoc("orphan", "x"), new vscode.Position(0, 1), token);
    expect(out).toBeUndefined();
  });

  it("hover builds a markdown Hover, or undefined when there is nothing", async () => {
    const cell = fsharpCellDoc("c2", "answer");
    stageNotebook([fsharpCellDoc("c1", "let answer = 42"), cell]);

    registerLanguageFeatures({
      ...worker({ hover: { displayName: "answer", type: "int", signature: null, xmlDoc: null } }),
      log: () => {},
    });
    let provider = vscode.languages._hover.at(-1)!.provider as {
      provideHover: (d: unknown, p: unknown, t: unknown) => Promise<vscode.Hover | undefined>;
    };
    const hover = await provider.provideHover(cell, new vscode.Position(0, 3), token);
    expect((hover!.contents as vscode.MarkdownString).value).toMatch(/answer : int/);

    registerLanguageFeatures({ ...worker({ hover: null }), log: () => {} });
    provider = vscode.languages._hover.at(-1)!.provider as typeof provider;
    expect(await provider.provideHover(cell, new vscode.Position(0, 3), token)).toBeUndefined();
  });

  it("signature help builds one signature with a parameter list", async () => {
    const cell = fsharpCellDoc("c2", "id (");
    stageNotebook([fsharpCellDoc("c1", "let id x = x"), cell]);
    registerLanguageFeatures({
      ...worker({ signatureHelp: { displayName: "id", parameters: ["x: 'a"], returnType: "'a" } }),
      log: () => {},
    });
    const provider = vscode.languages._signature.at(-1)!.provider as {
      provideSignatureHelp: (d: unknown, p: unknown, t: unknown) => Promise<vscode.SignatureHelp | undefined>;
    };
    const help = await provider.provideSignatureHelp(cell, new vscode.Position(0, 4), token);
    expect(help!.signatures[0].label).toBe("id(x: 'a) : 'a");
    expect(help!.signatures[0].parameters.map((p) => p.label)).toEqual(["x: 'a"]);
    expect(help!.activeParameter).toBe(0);
  });
});
```

- [ ] **Step 3: Run test to verify it fails**

Run: `cd editors/vscode && npx vitest run src/lang/providers.test.ts`
Expected: FAIL — `Cannot find module './providers'`.

- [ ] **Step 4: Write minimal implementation** — `editors/vscode/src/lang/providers.ts`:

```ts
import * as vscode from "vscode";
import {
  buildAnalysisInput,
  type AnalysisCell,
  type CellDiagnostic,
} from "./cell-context";
import {
  completeAnalysis,
  hoverAnalysis,
  signatureHelpAnalysis,
  type WorkerLike,
} from "./analysis-client";
import { DiagnosticsManager } from "./diagnostics";

export const FSHARP_CELL_SELECTOR = {
  language: "fsharp",
  notebookType: "fsquery-notebook",
} as const;

const DIAGNOSTIC_SOURCE = "F# Query Studio";

export interface LanguageFeaturesDeps {
  getWorker: () => Promise<WorkerLike>;
  log: (line: string) => void;
}

interface ResolvedCell {
  cells: AnalysisCell[];
  target: AnalysisCell;
  version: number;
}

/** Find the notebook cell backed by `doc`, plus its sibling cells. */
function resolveCell(doc: { uri: { toString(): string } }): ResolvedCell | undefined {
  const uri = doc.uri.toString();
  for (const notebook of vscode.workspace.notebookDocuments) {
    if (notebook.notebookType !== FSHARP_CELL_SELECTOR.notebookType) {
      continue;
    }
    const cells = notebook.getCells() as unknown as AnalysisCell[];
    const target = cells.find((c) => c.document.uri.toString() === uri);
    if (target) {
      return { cells, target, version: target.document.version };
    }
  }
  return undefined;
}

function isFsharpCell(doc: { uri: { scheme?: string; toString(): string }; languageId: string }): boolean {
  return doc.uri.scheme === "vscode-notebook-cell" && doc.languageId === "fsharp";
}

function severityOf(s: CellDiagnostic["severity"]): vscode.DiagnosticSeverity {
  switch (s) {
    case "Warning":
      return vscode.DiagnosticSeverity.Warning;
    case "Information":
      return vscode.DiagnosticSeverity.Information;
    default:
      return vscode.DiagnosticSeverity.Error;
  }
}

function toVsDiagnostic(d: CellDiagnostic): vscode.Diagnostic {
  const diag = new vscode.Diagnostic(
    new vscode.Range(d.startLine, d.startColumn, d.endLine, d.endColumn),
    d.message,
    severityOf(d.severity),
  );
  if (d.code !== null) {
    diag.code = d.code;
  }
  diag.source = DIAGNOSTIC_SOURCE;
  return diag;
}

function hoverMarkdown(info: {
  displayName: string;
  type: string;
  signature: string | null;
  xmlDoc: string | null;
}): vscode.MarkdownString {
  const md = new vscode.MarkdownString();
  md.appendCodeblock(info.signature ?? `${info.displayName} : ${info.type}`, "fsharp");
  if (info.xmlDoc) {
    md.appendMarkdown(`\n${info.xmlDoc}`);
  }
  return md;
}

export function registerLanguageFeatures(deps: LanguageFeaturesDeps): vscode.Disposable {
  const completion = vscode.languages.registerCompletionItemProvider(
    FSHARP_CELL_SELECTOR,
    {
      async provideCompletionItems(document, position, token) {
        const resolved = resolveCell(document);
        if (!resolved || token.isCancellationRequested) {
          return undefined;
        }
        const { source, line, column } = buildAnalysisInput(resolved.cells, resolved.target, {
          line: position.line,
          character: position.character,
        });
        const items = await completeAnalysis(await deps.getWorker(), {
          documentUri: document.uri.toString(),
          version: resolved.version,
          source,
          line,
          column,
        });
        return items.map((name) => new vscode.CompletionItem(name, vscode.CompletionItemKind.Value));
      },
    },
    ".",
  );

  const hover = vscode.languages.registerHoverProvider(FSHARP_CELL_SELECTOR, {
    async provideHover(document, position, token) {
      const resolved = resolveCell(document);
      if (!resolved || token.isCancellationRequested) {
        return undefined;
      }
      const { source, line, column } = buildAnalysisInput(resolved.cells, resolved.target, {
        line: position.line,
        character: position.character,
      });
      const info = await hoverAnalysis(await deps.getWorker(), {
        documentUri: document.uri.toString(),
        version: resolved.version,
        source,
        line,
        column,
      });
      return info ? new vscode.Hover(hoverMarkdown(info)) : undefined;
    },
  });

  const signature = vscode.languages.registerSignatureHelpProvider(
    FSHARP_CELL_SELECTOR,
    {
      async provideSignatureHelp(document, position, token) {
        const resolved = resolveCell(document);
        if (!resolved || token.isCancellationRequested) {
          return undefined;
        }
        const { source, line, column } = buildAnalysisInput(resolved.cells, resolved.target, {
          line: position.line,
          character: position.character,
        });
        const info = await signatureHelpAnalysis(await deps.getWorker(), {
          documentUri: document.uri.toString(),
          version: resolved.version,
          source,
          line,
          column,
        });
        if (!info) {
          return undefined;
        }
        const sig = new vscode.SignatureInformation(
          `${info.displayName}(${info.parameters.join(", ")}) : ${info.returnType}`,
        );
        sig.parameters = info.parameters.map((p) => new vscode.ParameterInformation(p));
        const help = new vscode.SignatureHelp();
        help.signatures = [sig];
        help.activeSignature = 0;
        help.activeParameter = 0;
        return help;
      },
    },
    "(",
    ",",
  );

  const collection = vscode.languages.createDiagnosticCollection(DIAGNOSTIC_SOURCE);
  const manager = new DiagnosticsManager({
    getWorker: deps.getWorker,
    log: deps.log,
    findNotebookOf: (cellUri) => {
      const resolved = resolveCell({ uri: { toString: () => cellUri } });
      return resolved?.cells;
    },
    publish: (cellUri, diags) =>
      collection.set(vscode.Uri.parse(cellUri), diags.map(toVsDiagnostic)),
    clear: (cellUri) => collection.delete(vscode.Uri.parse(cellUri)),
  });

  const onChange = vscode.workspace.onDidChangeTextDocument((e) => {
    if (isFsharpCell(e.document)) {
      manager.onCellChanged(e.document.uri.toString());
    }
  });
  const onOpen = vscode.workspace.onDidOpenTextDocument((doc) => {
    if (isFsharpCell(doc)) {
      manager.onCellOpened(doc.uri.toString());
    }
  });
  const onClose = vscode.workspace.onDidCloseTextDocument((doc) => {
    if (isFsharpCell(doc)) {
      manager.onCellClosed(doc.uri.toString());
    }
  });
  const onNotebookChange = vscode.workspace.onDidChangeNotebookDocument((e) => {
    for (const change of e.contentChanges ?? []) {
      for (const cell of change.removedCells ?? []) {
        manager.onCellClosed(cell.document.uri.toString());
      }
    }
  });

  return vscode.Disposable.from(
    completion,
    hover,
    signature,
    collection,
    { dispose: () => manager.dispose() },
    onChange,
    onOpen,
    onClose,
    onNotebookChange,
  );
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `cd editors/vscode && npx vitest run src/lang/providers.test.ts`
Expected: PASS.

- [ ] **Step 6: Run the full unit suite**

Run: `cd editors/vscode && npm test`
Expected: PASS (the shared stub grew — every pre-existing test must still be green).

- [ ] **Step 7: DEVLOG entry** — append to `docs/DEVLOG.md`:

```markdown
## 2026-09-06 — F4.6: language providers + registerLanguageFeatures

`src/lang/providers.ts` (the only vscode-bound F4 file): `registerLanguageFeatures` registers completion (`.` trigger), hover, and signature-help providers for `{ language: "fsharp", notebookType: "fsquery-notebook" }`, each resolving the cell + siblings from `workspace.notebookDocuments`, running `buildAnalysisInput`, and mapping the `analysis/*` result to VS Code types (flat `CompletionItem`s, a fenced-fsharp `MarkdownString` hover, one un-indexed `SignatureInformation`). It also owns the `DiagnosticCollection`, wires text/notebook events into a `DiagnosticsManager`, and returns one `Disposable.from(...)`. `test/vscode-stub.ts` gained `languages`, `Position`, `Range`, `Diagnostic`/`DiagnosticSeverity`, `Hover`, `MarkdownString`, `CompletionItem`, `SignatureHelp`/`SignatureInformation`/`ParameterInformation`, `Disposable`, and the `workspace` document events.
```

- [ ] **Step 8: Commit**

```bash
git add editors/vscode/src/lang/providers.ts editors/vscode/src/lang/providers.test.ts editors/vscode/test/vscode-stub.ts docs/DEVLOG.md
git commit -m "feat(vscode): register cell completion/hover/signature/diagnostics providers"
```

---

### Task 7: Wire `registerLanguageFeatures` into `activate` (`src/extension.ts`)

**Files:**
- Modify: `editors/vscode/src/extension.ts` (import + one call in `activate`, add to `context.subscriptions`)
- Test: `editors/vscode/src/extension.test.ts` (append one spec to the `activate — notebook wiring` block)

**Interfaces:**
- Consumes: `registerLanguageFeatures` from `./lang/providers`; the existing `getWorker` closure already built in `activate`.
- Produces: nothing new (integration point only).

- [ ] **Step 1: Write the failing test** — append inside the `describe("activate — notebook wiring", ...)` block in `editors/vscode/src/extension.test.ts`:

```ts
  it("registers the fsharp cell language providers", async () => {
    const vscodeStub2 = await import("../test/vscode-stub");
    const langs = vscodeStub2.languages as unknown as {
      _completion: unknown[];
      _hover: unknown[];
      _signature: unknown[];
      _collections: unknown[];
    };
    const before = {
      c: langs._completion.length,
      h: langs._hover.length,
      s: langs._signature.length,
      d: langs._collections.length,
    };
    await activate(fakeContext());
    expect(langs._completion.length).toBe(before.c + 1);
    expect(langs._hover.length).toBe(before.h + 1);
    expect(langs._signature.length).toBe(before.s + 1);
    expect(langs._collections.length).toBe(before.d + 1);
    await deactivate();
  });
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd editors/vscode && npx vitest run src/extension.test.ts`
Expected: FAIL — the counts don't increase (providers not registered in `activate`).

- [ ] **Step 3: Write minimal implementation** — in `editors/vscode/src/extension.ts`:

Add the import near the other `./notebook` / `./renderer` imports:

```ts
import { registerLanguageFeatures } from "./lang/providers";
```

In `activate`, after the `rendererHost` block and before `restartCommand` (or anywhere after `getWorker` is defined):

```ts
  const languageFeatures = registerLanguageFeatures({
    getWorker,
    log: (line) => outputChannel!.appendLine(line),
  });
```

Add `languageFeatures` to the `context.subscriptions.push(...)` list:

```ts
  context.subscriptions.push(
    command,
    notebookSerializer,
    controller,
    rendererHost,
    restartCommand,
    languageFeatures,
    outputChannel,
  );
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd editors/vscode && npx vitest run src/extension.test.ts`
Expected: PASS.

- [ ] **Step 5: Compile + full unit suite**

Run: `cd editors/vscode && npm run compile && npm test`
Expected: `tsc --noEmit` clean; esbuild bundle written; all unit tests pass.

- [ ] **Step 6: DEVLOG entry** — append to `docs/DEVLOG.md`:

```markdown
## 2026-09-06 — F4.7: activate wires the language providers

`extension.ts`: `activate` now calls `registerLanguageFeatures({ getWorker, log })` (reusing the shared window-lifetime worker session) and disposes it via `context.subscriptions`.
```

- [ ] **Step 7: Commit**

```bash
git add editors/vscode/src/extension.ts editors/vscode/src/extension.test.ts docs/DEVLOG.md
git commit -m "feat(vscode): activate registers the F4 cell language providers"
```

---

### Task 8: Extension-host integration spec + fixture

**Files:**
- Create: `editors/vscode/test/integration/fixtures/sample-intelligence.fsqnb`
- Create: `editors/vscode/test/integration/intelligence.test.ts`

**Interfaces:**
- Consumes: the activated extension (same `EXTENSION_ID` / worker-presence gate pattern as `test/integration/notebook.test.ts`).
- Produces: nothing (test-only).

- [ ] **Step 1: Write the fixture** — `editors/vscode/test/integration/fixtures/sample-intelligence.fsqnb`:

```json
{
  "version": 1,
  "cells": [
    { "kind": "markdown", "value": "# F4 cell intelligence fixture" },
    { "kind": "code", "language": "fsharp", "value": "let answer = 42" },
    { "kind": "code", "language": "fsharp", "value": "answ" }
  ],
  "metadata": {}
}
```

- [ ] **Step 2: Write the failing test** — `editors/vscode/test/integration/intelligence.test.ts`:

```ts
import * as assert from "node:assert";
import * as fs from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";

const EXTENSION_ID = "fs-query-studio.fs-query-studio";
const extensionRoot = path.resolve(__dirname, "..", "..", "..");
const fixture = path.join(
  extensionRoot,
  "test",
  "integration",
  "fixtures",
  "sample-intelligence.fsqnb",
);
const bundledWorker = path.join(extensionRoot, "dist", "worker", "FSharpQueryStudio.Worker.dll");
const hasWorker = fs.existsSync(bundledWorker);

async function delay(ms: number) {
  await new Promise((r) => setTimeout(r, ms));
}

describe("fsquery-notebook cell intelligence", () => {
  before(async () => {
    const ext = vscode.extensions.getExtension(EXTENSION_ID);
    assert.ok(ext, `extension ${EXTENSION_ID} not found`);
    await ext!.activate();
  });

  (hasWorker ? it : it.skip)(
    "offers a completion from an executed sibling cell",
    async function () {
      this.timeout(120000);
      const doc = await vscode.workspace.openNotebookDocument(vscode.Uri.file(fixture));
      await vscode.window.showNotebookDocument(doc);

      // Run cell 1 (`let answer = 42`) so it lands in the executed prefix.
      await vscode.commands.executeCommand("notebook.cell.execute", {
        ranges: [{ start: 1, end: 2 }],
        document: doc.uri,
      });
      for (let i = 0; i < 60 && doc.cellAt(1).outputs.length === 0; i++) {
        await delay(500);
      }

      const target = doc.cellAt(2).document; // "answ"
      const list = (await vscode.commands.executeCommand(
        "vscode.executeCompletionItemProvider",
        target.uri,
        new vscode.Position(0, 4),
      )) as vscode.CompletionList;

      const labels = list.items.map((i) =>
        typeof i.label === "string" ? i.label : i.label.label,
      );
      assert.ok(
        labels.includes("answer"),
        `expected 'answer' among completions, got: ${labels.slice(0, 20).join(", ")}`,
      );
    },
  );

  (hasWorker ? it : it.skip)("reports a diagnostic for a broken edit", async function () {
    this.timeout(120000);
    const doc = await vscode.workspace.openNotebookDocument(vscode.Uri.file(fixture));
    await vscode.window.showNotebookDocument(doc);

    const target = doc.cellAt(2).document;
    const edit = new vscode.WorkspaceEdit();
    edit.replace(target.uri, new vscode.Range(0, 0, target.lineCount, 0), "answer +");
    await vscode.workspace.applyEdit(edit);

    let diags: vscode.Diagnostic[] = [];
    for (let i = 0; i < 60; i++) {
      diags = vscode.languages.getDiagnostics(target.uri);
      if (diags.length > 0) break;
      await delay(500);
    }
    assert.ok(diags.length > 0, "expected at least one diagnostic on the broken cell");
    assert.strictEqual(diags[0].source, "F# Query Studio");
  });
});
```

- [ ] **Step 3: Run the integration suite to verify the new spec fails without the wiring / passes with it**

Run: `cd editors/vscode && npm run pretest:vscode && npm run test:vscode`
Expected (no `dist/worker` publish in this environment): both new specs **pending** (skipped green), the suite stays green. If a worker publish is present, both specs run and pass.

Note: this task's real verification is that the file compiles under `tsconfig.integration.json` and the suite still runs. The worker-backed assertions are exercised by CI, which publishes the worker before `test:vscode` (same arrangement as the F2/F3 integration specs).

- [ ] **Step 4: DEVLOG entry** — append to `docs/DEVLOG.md`:

```markdown
## 2026-09-06 — F4.8: extension-host integration spec

`test/integration/intelligence.test.ts` (+ `fixtures/sample-intelligence.fsqnb`): opens the fixture, executes `let answer = 42`, and asserts `vscode.executeCompletionItemProvider` on the sibling cell offers `answer`; a second spec applies a broken edit and waits for a `"F# Query Studio"`-sourced diagnostic. Both gated on a bundled worker (skipped green without one, run in CI).
```

- [ ] **Step 5: Commit**

```bash
git add editors/vscode/test/integration/intelligence.test.ts editors/vscode/test/integration/fixtures/sample-intelligence.fsqnb docs/DEVLOG.md
git commit -m "test(vscode): extension-host spec for F4 cell completion + diagnostics"
```

---

### Task 9: F4 close-out — DEVLOG summary, spec status, full verification

**Files:**
- Modify: `docs/DEVLOG.md` (close-out entry)
- Modify: `docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md` (Status line under `## Rollout — phased`)

- [ ] **Step 1: Run the complete verification pass**

Run: `cd editors/vscode && npm run compile && npm test && npm run pretest:vscode && npm run test:vscode`
Expected: `tsc --noEmit` clean; esbuild bundle written; every unit test green; the integration suite green (F4 worker-backed specs pending without a `dist/worker` publish, same as F2/F3). Record the exact pass/skip counts for the DEVLOG entry.

- [ ] **Step 2: Update the migration design status line** — in `docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md`, the line beginning `**Status:**` under `## Rollout — phased`:

Change

```
F4 Cell intelligence is next.
```

to

```
F4 Cell intelligence is complete on `main-vscode` (not yet merged) — see the DEVLOG close-out entry (`2026-09-06 — F4 complete`). F5 Connections is next.
```

- [ ] **Step 3: Write the close-out DEVLOG entry** — append to `docs/DEVLOG.md` (fill the counts in from Step 1):

```markdown
## 2026-09-06 — F4 complete: cell intelligence

`.fsqnb` code cells now get F# completion, hover, signature help, and diagnostics, provided by this extension (Ionide never sees `vscode-notebook-cell` documents). New `src/lang/` module: `cell-context.ts` (pure) builds each request's `source` by concatenating the notebook's *executed* fsharp code cells — ordered by `executionSummary.executionOrder`, target excluded — ahead of the target cell's live text, tracks a per-cell `version` that moves whenever that concatenation changes, and maps `analysis/check` diagnostics back into cell coordinates (subtract the prefix line count; drop anything landing in a prefix cell). `analysis-client.ts` wraps `analysis/check|complete|hover|signatureHelp`, collapsing any worker error into an empty result. `diagnostics.ts` — a dependency-injected `DiagnosticsManager` — debounces edits 300 ms, checks a cell once on open, and clears on close/removal. `providers.ts` registers the three interactive providers for `{ language: "fsharp", notebookType: "fsquery-notebook" }`, owns the `"F# Query Studio"` `DiagnosticCollection`, and wires `workspace` text/notebook events into the manager; `extension.ts` calls it with the shared worker session.

Line-base contract (worker's `Prologue.shiftUp`): interactive requests send a **1-based** `line` (`position.line + 1 + cellLineOffset`) and **0-based** `column`; `analysis/check` returns 0-based diagnostics, so the reverse map only subtracts the prefix offset.

Scope: providers only — **no `contributes.languages` entry and no TextMate grammar** this phase (cell highlighting relies on another `fsharp` grammar being installed). No completion `kind`/`detail`, no active-parameter in signature help, no semantic tokens / references / formatting / code actions (the migration design's stated ceiling). Known limitations, carried from the spec: a cell's diagnostics go stale when an upstream executed cell is edited or re-run (refresh only on the cell's own next edit); after `Restart Kernel` the executed-cells prefix still includes pre-restart cells until VS Code clears their execution summaries; a physically-later cell can contribute to the prefix if it executed earlier; a transient worker error blanks a cell's squiggles for one debounce cycle.

No `src/` F# changes — the worker already answered every `analysis/*` method. Avalonia untouched (retired at F6). New: <N> F4 unit tests across `src/lang/**` + `src/protocol.test.ts`; 2 extension-host specs (pending without a bundled worker, run in CI).

Verification (from `editors/vscode`): `npm run compile` — <result>; `npm test` — <pass>/<skip>; `npm run test:vscode` — <pass>/<pending>.
```

- [ ] **Step 4: Commit**

```bash
git add docs/DEVLOG.md docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md
git commit -m "docs: F4 cell intelligence phase complete"
```

---

## Self-Review

**1. Spec coverage**

| Spec section | Task |
|---|---|
| `lang/` module layout (`cell-context` / `analysis-client` / `diagnostics` / `providers`) | 2–7 |
| `buildAnalysisInput` — executed-only filter, execution-order sort, target exclusion, offset | 2 |
| Line base (1-based line + `cellLineOffset`; column 0-based) | 1 (types), 2 (`line` math), 6 (providers pass it) |
| `mapDiagnosticIntoCell` — shift back, drop prefix diagnostics, clamp straddler | 3 |
| `versionFor` — monotonic per-cell, bumps on source change | 3 |
| `analysis-client` — four wrappers, worker error → empty, never throw | 4 |
| `DiagnosticsManager` — 300 ms debounce, open baseline, clear on close/removal, no execution refresh | 5 |
| Provider → VS Code type mapping (completion / hover / signature help / diagnostics) | 6 |
| Selector `{ language: "fsharp", notebookType: "fsquery-notebook" }`, programmatic registration, no `languages` contribution | 6 |
| `protocol.ts` additions + `readSeverity` | 1 |
| `extension.ts` wiring via shared `getWorker` | 7 |
| Ionide coexistence (out of selector by scheme — nothing to build) | n/a — asserted in spec, no code |
| Unit test files (`cell-context` / `analysis-client` / `diagnostics` / `providers`) | 2–6 |
| `vscode-stub.ts` additions | 6 (folded in) |
| Integration spec + fixture | 8 |
| Manual release checklist entry | recorded in the spec; not a code task |
| DEVLOG per phase + status line | every task (per-task line) + 9 (close-out + status) |

No gaps.

**2. Placeholder scan** — the close-out DEVLOG entry (Task 9 Step 3) has `<N>` / `<result>` / `<pass>` placeholders **by design**: they are filled from the Step 1 verification run. Every code step contains complete, runnable content. No "TODO" / "handle edge cases" / "similar to Task N".

**3. Type consistency**
- `WorkerLike` — defined once in `analysis-client.ts` (Task 4), imported by `diagnostics.ts` (5) and `providers.ts` (6). `DiagnosticsManagerDeps.getWorker` and `LanguageFeaturesDeps.getWorker` are both `() => Promise<WorkerLike>`.
- `AnalysisCell` / `AnalysisCellDocument` / `AnalysisInput` — defined in `cell-context.ts` (Task 2), reused unchanged in 3/5/6.
- `CellDiagnostic` — defined in `cell-context.ts` (Task 3), consumed by `diagnostics.ts` (`publish` signature) and `providers.ts` (`toVsDiagnostic`). Field names (`startLine`/`startColumn`/`endLine`/`endColumn`/`severity`/`code`) match across 3, 5, 6.
- `WireDiagnostic` / `HoverInfo` / `SignatureHelpInfo` / `AnalysisCheckParams` / `AnalysisPositionParams` — defined in `protocol.ts` (Task 1), imported by 3 (`WireDiagnostic`), 4 (all), 6 (via `analysis-client`).
- `buildAnalysisInput(cells, target, position?)` — same 3-arg shape in Tasks 2, 5, 6.
- `versionFor` / `forgetVersion` — Task 3; `forgetVersion` called in `diagnostics.ts` `onCellClosed` (Task 5). Note: `providers.ts` (Task 6) passes `resolved.target.document.version` as the interactive-request `version`, **not** `versionFor` — intentional: the interactive providers do not need the concatenation-aware bump (a stale FSAC parse on a hover is harmless and self-corrects), and threading `versionFor` through three providers plus the manager risks version races between a check and a hover on the same cell. The manager owns `versionFor` for the check path, where a stale parse would mean wrong squiggles.
- `readSeverity` — Task 1, used by `mapDiagnosticIntoCell` (Task 3).
- Stub `Disposable.from` — added Task 6, used by `providers.ts` return.

Consistent.

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-09-06-vscode-extension-f4-cell-intelligence.md`. Two execution options:**

**1. Subagent-Driven (recommended)** — a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — tasks run in this session via `executing-plans`, batched with checkpoints for review.

**Which approach?**
