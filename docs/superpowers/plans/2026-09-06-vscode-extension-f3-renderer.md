# VS Code Extension — F3 Results Renderer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Render `.fsqnb` tabular cell results as an interactive `NotebookRenderer` grid — windowed rows, a schema view, CSV export, and "Load more" paging — instead of F2's monospace text block.

**Architecture:** `mapEnvelope` gains a second output item for Table results: a JSON body under a new MIME type `application/x-fsquery-table+json` (the F2 text grid stays as a `text/plain` fallback in the same output). A browser-side renderer bundle (`src/renderer/` → esbuild ESM → `media/renderer/index.js`) draws the grid in the output webview and talks to the extension host over `NotebookRendererMessaging`. The host module `notebook/renderer-host.ts` answers those messages by calling the existing worker's `results/page` (paging) and `results/exportCsv` (export) RPCs through the F2 window-lifetime worker session. No `src/` F# project changes; the Avalonia app is untouched.

**Tech Stack:** TypeScript 5, Node 24, `esbuild` (two bundles now: node/cjs extension + browser/esm renderer), `vitest` (unit; `vscode` aliased to `test/vscode-stub.ts`; renderer DOM tests use `// @vitest-environment jsdom`), `jsdom` (new devDependency), `@vscode/test-cli` + `@vscode/test-electron` (extension-host integration).

**Spec:** `docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md` (rollout row **F3 Renderer**; sections *Architecture → Extension components* rows `notebook/renderer.ts` + `media/renderer/`, *Data flow → Run a cell* / *Restart Kernel*, *Testing* row "Renderer"). This plan also implements the F3-specific design captured in the brainstorming session on 2026-09-06 (renderer stack = plain TS + DOM; CSV = worker `results/exportCsv` + save dialog; virtualization = windowed rendering).

## Global Constraints

- **.NET floor:** worker requires .NET **10** on `PATH`. Reuse the F1 `resolveDotnet`; never re-implement it.
- **Node engine:** development on Node 24; `package.json` `engines.node` = `">=20"` (unchanged).
- **VS Code engine:** `package.json` `engines.vscode` = `"^1.90.0"` (unchanged).
- **Extension identity (placeholder until F6):** `name` = `fs-query-studio`, `publisher` = `fs-query-studio`, `displayName` = `F# Query Studio`.
- **Notebook type id:** `fsquery-notebook`. **Controller id:** `fs-query-studio-kernel`. **Restart command id:** `fs-query-studio.restartKernel` (all unchanged from F2).
- **New identifiers (this phase):** renderer id `fs-query-studio-table`; MIME type `application/x-fsquery-table+json`; renderer bundle output `media/renderer/index.js`.
- **Worker frames are camelCase on the wire** (`{ id, method, params }` / `{ id, result?, error? }`, error `{ code, message }`). The worker (FSharp.SystemTextJson) writes an absent `result`/`error` as an explicit JSON `null` — treat `null` and `undefined` the same.
- **F# discriminated unions on the wire** use `{ "Case": "<Name>" }` (AdjacentTag, fieldless cases not unwrapped). Applies here to `ResultKind` (received).
- **`results/page` request payload** (`ResultPageRequest`, `src/FSharpQueryStudio.Protocol/Payloads.fs:172`): `{ sessionId: string, continuationToken: string }`. **Response** (`ResultPage`, same file:175): `{ rows: unknown[][], isTruncated: boolean, continuationToken: string | null }`. An unknown/expired token → structured error `{ code: "results/unknownToken", message }`, never a silent re-run.
- **`results/exportCsv` request payload** (`ExportCsvParams`, `Payloads.fs:183`): `{ documentUri: string, filePath: string }`. `filePath` is a real filesystem path the extension obtains from its own save dialog — the worker never prompts. Success → `{ result: { acknowledged: true } }`. No retained table for that `documentUri` → structured error `{ code: "results/nothingToExport", message }`.
- **Two worker paths produce a `Table` envelope, and they differ** (verified in `src/FSharpQueryStudio.Worker/Handlers.fs:144-210`, `src/FSharpQueryStudio.Results/ScriptResults.fs`, `src/FSharpQueryStudio.Results/Projection.fs`):
  - **`Results.table` push:** real column schema, all rows materialized, `isTruncated = false`, `continuationToken = null`. Retained → `results/exportCsv` works. No paging.
  - **FSI tail value that is a `seq`/`list`:** schema `[{ name: "Value", displayType: "obj" }]`, rows are **single-cell** `[value]` arrays (preview ≤ 500), `isTruncated` true when more remain, `continuationToken` a GUID. **Not** retained → `results/exportCsv` returns `results/nothingToExport`. `results/page` returns the same single-cell row shape. Paging works.
  - **FSI tail value that is a record:** `Table`, multi-cell single row, no token, not retained.
  - The renderer therefore treats every row positionally against `columns` and must handle a token that is `null`. Export failing with `results/nothingToExport` is an expected, user-facing outcome for the tail-value path (same limitation as the Avalonia app), surfaced as a clear message, not an error dialog.
- **The renderer bundle must not import anything Node-only.** `src/protocol.ts` imports `node:crypto` at module top level; a browser esbuild bundle that pulls it in fails. Renderer source (`src/renderer/**`) may only import from other `src/renderer/**` modules. Cross-tier types are duplicated as plain interfaces in `src/renderer/types.ts`; the host-side `table-payload.ts` imports those with `import type` only.
- **TDD:** every task writes the failing test first, runs it red, then implements. **Commit after every task.** Add a one-line `docs/DEVLOG.md` entry as the second-to-last step of every task.
- All new source lives under `editors/vscode/`. Do not modify any `src/` F# project.
- **Run the whole unit suite** (`npm test` in `editors/vscode`) at the end of each task, not just the new file — `test/vscode-stub.ts` is shared and a regression there breaks earlier tests silently.

---

### Task 1: Renderer wire types + message contracts (`src/renderer/types.ts`)

**Files:**
- Create: `editors/vscode/src/renderer/types.ts`
- Test: `editors/vscode/src/renderer/types.test.ts`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `const FSQUERY_TABLE_MIME = "application/x-fsquery-table+json"`
  - `const FSQUERY_RENDERER_ID = "fs-query-studio-table"`
  - `const GRID_ROW_HEIGHT_PX = 22`
  - `interface TableColumn { name: string; displayType: string }`
  - `interface TablePayload { documentUri: string; columns: TableColumn[]; rows: unknown[][]; truncated: boolean; continuationToken: string | null; elapsedMs: number }`
  - `interface LoadMoreMessage { type: "loadMore"; outputId: string; documentUri: string; token: string }`
  - `interface ExportCsvMessage { type: "exportCsv"; outputId: string; documentUri: string }`
  - `type RendererToHostMessage = LoadMoreMessage | ExportCsvMessage`
  - `interface RowsMessage { type: "rows"; outputId: string; rows: unknown[][]; truncated: boolean; nextToken: string | null }`
  - `interface HostErrorMessage { type: "error"; outputId: string; scope: "loadMore" | "exportCsv"; message: string }`
  - `interface ExportDoneMessage { type: "exportDone"; outputId: string; path: string }`
  - `type HostToRendererMessage = RowsMessage | HostErrorMessage | ExportDoneMessage`
  - `function isRendererToHostMessage(value: unknown): value is RendererToHostMessage`

- [ ] **Step 1: Write `editors/vscode/src/renderer/types.test.ts`**

```ts
import { describe, it, expect } from "vitest";
import {
  FSQUERY_TABLE_MIME,
  FSQUERY_RENDERER_ID,
  GRID_ROW_HEIGHT_PX,
  isRendererToHostMessage,
} from "./types";

describe("renderer wire constants", () => {
  it("pins the MIME type and renderer id the package.json contribution depends on", () => {
    expect(FSQUERY_TABLE_MIME).toBe("application/x-fsquery-table+json");
    expect(FSQUERY_RENDERER_ID).toBe("fs-query-studio-table");
    expect(GRID_ROW_HEIGHT_PX).toBeGreaterThan(0);
  });
});

describe("isRendererToHostMessage", () => {
  it("accepts loadMore and exportCsv shapes", () => {
    expect(
      isRendererToHostMessage({ type: "loadMore", outputId: "o", documentUri: "u", token: "t" }),
    ).toBe(true);
    expect(isRendererToHostMessage({ type: "exportCsv", outputId: "o", documentUri: "u" })).toBe(true);
  });

  it("rejects anything else", () => {
    expect(isRendererToHostMessage(null)).toBe(false);
    expect(isRendererToHostMessage(undefined)).toBe(false);
    expect(isRendererToHostMessage("loadMore")).toBe(false);
    expect(isRendererToHostMessage({ type: "rows" })).toBe(false);
    expect(isRendererToHostMessage({})).toBe(false);
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd editors/vscode && npx vitest run src/renderer/types.test.ts`
Expected: FAIL — `Cannot find module './types'`.

- [ ] **Step 3: Write `editors/vscode/src/renderer/types.ts`**

```ts
/**
 * Wire types shared between the extension host and the results-grid
 * renderer. This module is imported by browser-side renderer code, so it
 * must stay dependency-free — no `import` of `../protocol` (which pulls
 * `node:crypto`) or `vscode`. The host side re-declares the matching
 * shapes via `import type` only.
 */

/** MIME type of the output item that carries a tabular result. */
export const FSQUERY_TABLE_MIME = "application/x-fsquery-table+json";

/** Must equal `contributes.notebookRenderer[].id` in `package.json`. */
export const FSQUERY_RENDERER_ID = "fs-query-studio-table";

/** Fixed row height the windowed grid lays rows out on. */
export const GRID_ROW_HEIGHT_PX = 22;

export interface TableColumn {
  name: string;
  displayType: string;
}

/** JSON body of an `application/x-fsquery-table+json` output item. */
export interface TablePayload {
  /** Notebook URI; echoed in renderer→host messages so the host can RPC. */
  documentUri: string;
  columns: TableColumn[];
  /** Preview rows, positional against `columns`. */
  rows: unknown[][];
  /** More rows exist on the worker behind `continuationToken`. */
  truncated: boolean;
  /** Opaque worker-issued paging token, or `null` when paging is unavailable. */
  continuationToken: string | null;
  elapsedMs: number;
}

// --- renderer -> host ---

export interface LoadMoreMessage {
  type: "loadMore";
  outputId: string;
  documentUri: string;
  token: string;
}

export interface ExportCsvMessage {
  type: "exportCsv";
  outputId: string;
  documentUri: string;
}

export type RendererToHostMessage = LoadMoreMessage | ExportCsvMessage;

// --- host -> renderer ---

export interface RowsMessage {
  type: "rows";
  outputId: string;
  rows: unknown[][];
  truncated: boolean;
  nextToken: string | null;
}

export interface HostErrorMessage {
  type: "error";
  outputId: string;
  scope: "loadMore" | "exportCsv";
  message: string;
}

export interface ExportDoneMessage {
  type: "exportDone";
  outputId: string;
  path: string;
}

export type HostToRendererMessage = RowsMessage | HostErrorMessage | ExportDoneMessage;

export function isRendererToHostMessage(value: unknown): value is RendererToHostMessage {
  if (value === null || typeof value !== "object") {
    return false;
  }
  const type = (value as { type?: unknown }).type;
  return type === "loadMore" || type === "exportCsv";
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd editors/vscode && npx vitest run src/renderer/types.test.ts`
Expected: PASS (5 assertions).

- [ ] **Step 5: DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-06 — F3.1: renderer wire types + message contracts

`src/renderer/types.ts`: dependency-free module shared across the host/renderer tiers — `TablePayload` (the `application/x-fsquery-table+json` body: columns, positional rows, `truncated`, `continuationToken`, `elapsedMs`), the renderer↔host message unions (`loadMore` / `exportCsv` out; `rows` / `error` / `exportDone` back), plus `FSQUERY_TABLE_MIME`, `FSQUERY_RENDERER_ID`, `GRID_ROW_HEIGHT_PX`, and an `isRendererToHostMessage` guard.
```

- [ ] **Step 6: Commit**

```bash
git add editors/vscode/src/renderer/types.ts editors/vscode/src/renderer/types.test.ts docs/DEVLOG.md
git commit -m "feat(vscode): renderer wire types and message contracts for F3"
```

---

### Task 2: `buildTablePayload` — `ResultEnvelope` → `TablePayload` (`src/notebook/table-payload.ts`)

**Files:**
- Create: `editors/vscode/src/notebook/table-payload.ts`
- Test: `editors/vscode/src/notebook/table-payload.test.ts`

**Interfaces:**
- Consumes: `type ResultEnvelope` from `../protocol`; `type TablePayload` from `../renderer/types`.
- Produces:
  - `function buildTablePayload(env: ResultEnvelope, documentUri: string): TablePayload` — copies `schema` → `columns`, `rows` through unchanged, `truncated = env.isTruncated === true`, `continuationToken = env.continuationToken ?? null`, `elapsedMs = env.elapsedMs`. Never inspects `env.kind` — callers only invoke it for Table envelopes.

- [ ] **Step 1: Write `editors/vscode/src/notebook/table-payload.test.ts`**

```ts
import { describe, it, expect } from "vitest";
import { buildTablePayload } from "./table-payload";
import type { ResultEnvelope } from "../protocol";

function tableEnv(overrides: Partial<ResultEnvelope>): ResultEnvelope {
  return {
    kind: { Case: "Table" },
    displayText: null,
    schema: [{ name: "id", displayType: "int" }],
    rows: [[1], [2]],
    isTruncated: false,
    continuationToken: null,
    elapsedMs: 9,
    output: [],
    ...overrides,
  };
}

describe("buildTablePayload", () => {
  it("maps a Results.table-style envelope (real columns, no token)", () => {
    const payload = buildTablePayload(
      tableEnv({
        schema: [
          { name: "name", displayType: "string" },
          { name: "age", displayType: "int" },
        ],
        rows: [
          ["ada", 36],
          ["grace", 45],
        ],
      }),
      "file:///q.fsqnb",
    );
    expect(payload).toEqual({
      documentUri: "file:///q.fsqnb",
      columns: [
        { name: "name", displayType: "string" },
        { name: "age", displayType: "int" },
      ],
      rows: [
        ["ada", 36],
        ["grace", 45],
      ],
      truncated: false,
      continuationToken: null,
      elapsedMs: 9,
    });
  });

  it("carries the continuation token and truncated flag from a paged tail-value envelope", () => {
    const payload = buildTablePayload(
      tableEnv({
        schema: [{ name: "Value", displayType: "obj" }],
        rows: [[1], [2], [3]],
        isTruncated: true,
        continuationToken: "abc123",
      }),
      "file:///q.fsqnb",
    );
    expect(payload.truncated).toBe(true);
    expect(payload.continuationToken).toBe("abc123");
    expect(payload.columns).toEqual([{ name: "Value", displayType: "obj" }]);
  });

  it("tolerates missing schema/rows", () => {
    const payload = buildTablePayload(
      { ...tableEnv({}), schema: undefined as never, rows: undefined as never },
      "u",
    );
    expect(payload.columns).toEqual([]);
    expect(payload.rows).toEqual([]);
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd editors/vscode && npx vitest run src/notebook/table-payload.test.ts`
Expected: FAIL — `Cannot find module './table-payload'`.

- [ ] **Step 3: Write `editors/vscode/src/notebook/table-payload.ts`**

```ts
import type { ResultEnvelope } from "../protocol";
import type { TablePayload } from "../renderer/types";

/**
 * Projects a `Table`-kind `ResultEnvelope` onto the renderer's wire body.
 * Works for both worker paths unchanged: `Results.table` (real columns,
 * full rows, `continuationToken = null`) and an FSI tail-value `seq`
 * (single `Value` column, preview rows, a token when truncated). Rows are
 * passed through positionally — the renderer formats each cell.
 */
export function buildTablePayload(env: ResultEnvelope, documentUri: string): TablePayload {
  return {
    documentUri,
    columns: (env.schema ?? []).map((c) => ({ name: c.name, displayType: c.displayType })),
    rows: env.rows ?? [],
    truncated: env.isTruncated === true,
    continuationToken: env.continuationToken ?? null,
    elapsedMs: env.elapsedMs,
  };
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd editors/vscode && npx vitest run src/notebook/table-payload.test.ts`
Expected: PASS (3 assertions).

- [ ] **Step 5: DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-06 — F3.2: buildTablePayload (ResultEnvelope → TablePayload)

`notebook/table-payload.ts`: pure `buildTablePayload(env, documentUri)` copies a Table envelope's schema/rows/`isTruncated`/`continuationToken`/`elapsedMs` into the renderer wire body. Type-only imports keep it out of the browser bundle's Node dependency path. Covered for both worker Table paths (`Results.table` real columns + no token; paged tail-value `Value` column + token).
```

- [ ] **Step 6: Commit**

```bash
git add editors/vscode/src/notebook/table-payload.ts editors/vscode/src/notebook/table-payload.test.ts docs/DEVLOG.md
git commit -m "feat(vscode): buildTablePayload maps a Table envelope to the renderer body"
```

---

### Task 3: Grid core math (`src/renderer/grid-core.ts`)

**Files:**
- Create: `editors/vscode/src/renderer/grid-core.ts`
- Test: `editors/vscode/src/renderer/grid-core.test.ts`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `interface VisibleRange { start: number; end: number }`
  - `function visibleRange(scrollTop: number, rowHeight: number, viewportHeight: number, totalRows: number, overscan?: number): VisibleRange` — `overscan` default `6`. Clamps to `[0, totalRows]`. Returns `{ start: 0, end: 0 }` when `totalRows <= 0` or `rowHeight <= 0`.
  - `function formatCell(value: unknown): string` — `null`/`undefined` → `""`; `string`/`number`/`boolean`/`bigint` → `String(value)`; anything else → `JSON.stringify(value)`. (Same rule as `notebook/result.ts::renderCell`; duplicated here because that module is not importable from the browser bundle.)

- [ ] **Step 1: Write `editors/vscode/src/renderer/grid-core.test.ts`**

```ts
import { describe, it, expect } from "vitest";
import { visibleRange, formatCell } from "./grid-core";

describe("visibleRange", () => {
  it("returns an empty range for no rows", () => {
    expect(visibleRange(0, 22, 400, 0)).toEqual({ start: 0, end: 0 });
  });

  it("covers the viewport plus overscan on both sides", () => {
    // scrollTop 220 / 22 = row 10 first visible; 400/22 ≈ 19 visible; overscan 6
    const range = visibleRange(220, 22, 400, 1000, 6);
    expect(range.start).toBe(4); // 10 - 6
    expect(range.end).toBe(10 + 19 + 6); // 35
  });

  it("clamps start at 0 and end at totalRows", () => {
    expect(visibleRange(0, 22, 400, 5, 6)).toEqual({ start: 0, end: 5 });
    const far = visibleRange(100000, 22, 400, 12, 6);
    expect(far.start).toBe(12);
    expect(far.end).toBe(12);
  });

  it("guards a zero row height", () => {
    expect(visibleRange(50, 0, 400, 100)).toEqual({ start: 0, end: 0 });
  });
});

describe("formatCell", () => {
  it("blanks null and undefined", () => {
    expect(formatCell(null)).toBe("");
    expect(formatCell(undefined)).toBe("");
  });

  it("stringifies primitives directly", () => {
    expect(formatCell(42)).toBe("42");
    expect(formatCell(true)).toBe("true");
    expect(formatCell("ada")).toBe("ada");
  });

  it("JSON-encodes objects and arrays", () => {
    expect(formatCell({ a: 1 })).toBe('{"a":1}');
    expect(formatCell([1, 2])).toBe("[1,2]");
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd editors/vscode && npx vitest run src/renderer/grid-core.test.ts`
Expected: FAIL — `Cannot find module './grid-core'`.

- [ ] **Step 3: Write `editors/vscode/src/renderer/grid-core.ts`**

```ts
/** Inclusive-start, exclusive-end row indices to mount for a scroll offset. */
export interface VisibleRange {
  start: number;
  end: number;
}

/**
 * Windowed-rendering math: which row indices need to be in the DOM for a
 * given scroll offset and geometry. `overscan` rows are kept on each side
 * so a fast scroll does not flash blank.
 */
export function visibleRange(
  scrollTop: number,
  rowHeight: number,
  viewportHeight: number,
  totalRows: number,
  overscan = 6,
): VisibleRange {
  if (totalRows <= 0 || rowHeight <= 0) {
    return { start: 0, end: 0 };
  }
  const first = Math.floor(scrollTop / rowHeight);
  const visibleCount = Math.ceil(viewportHeight / rowHeight);
  const start = Math.max(0, first - overscan);
  const end = Math.min(totalRows, first + visibleCount + overscan);
  return { start, end };
}

/**
 * Renders one cell value as text — the same rule as
 * `notebook/result.ts::renderCell`, duplicated because that module sits
 * behind a Node-only import and cannot enter the browser bundle.
 */
export function formatCell(value: unknown): string {
  if (value === null || value === undefined) {
    return "";
  }
  if (
    typeof value === "string" ||
    typeof value === "number" ||
    typeof value === "boolean" ||
    typeof value === "bigint"
  ) {
    return String(value);
  }
  return JSON.stringify(value);
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd editors/vscode && npx vitest run src/renderer/grid-core.test.ts`
Expected: PASS (7 assertions).

- [ ] **Step 5: DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-06 — F3.3: grid core math (windowing + cell formatting)

`src/renderer/grid-core.ts`: pure `visibleRange(scrollTop, rowHeight, viewportHeight, totalRows, overscan=6)` (clamped, empty on zero rows/height) for windowed row rendering, and `formatCell` (blanks nullish, `String()` for primitives, `JSON.stringify` otherwise — mirrors `result.ts::renderCell`).
```

- [ ] **Step 6: Commit**

```bash
git add editors/vscode/src/renderer/grid-core.ts editors/vscode/src/renderer/grid-core.test.ts docs/DEVLOG.md
git commit -m "feat(vscode): windowed-grid core math for the F3 renderer"
```

---

### Task 4: `mapEnvelope` emits a `table-json` output spec (`src/notebook/result.ts`)

**Files:**
- Modify: `editors/vscode/src/notebook/result.ts`
- Modify: `editors/vscode/src/notebook/result.test.ts`

**Interfaces:**
- Consumes: `buildTablePayload` from `./table-payload`; `type TablePayload` from `../renderer/types`.
- Produces (changed):
  - `type OutputItemSpec` gains a member `{ kind: "table-json"; payload: TablePayload }`.
  - `function mapEnvelope(env: ResultEnvelope, opts?: { documentUri?: string }): OutputItemSpec[]` — new optional second argument. For a `Table` envelope: when `opts?.documentUri` is a non-empty string, push `{ kind: "table-json", payload: buildTablePayload(env, opts.documentUri) }` **before** the existing `{ kind: "text", text: formatTextTable(...) }`; when it is absent, behave exactly as F2 (text spec only). All other envelope kinds unchanged.
  - `formatTextTable` unchanged except the truncation note string becomes `"(truncated — more rows available in the grid view)"`.

- [ ] **Step 1: Update the failing tests in `editors/vscode/src/notebook/result.test.ts`**

Replace the existing `"adds a truncation note when isTruncated"` test body and add the new `mapEnvelope` Table cases. The truncation test becomes:

```ts
  it("adds a truncation note when isTruncated", () => {
    const text = formatTextTable([{ name: "x", displayType: "int" }], [[1]], 4, true);
    expect(text).toContain("truncated");
    expect(text).toContain("grid view");
  });
```

Add inside `describe("mapEnvelope", ...)`:

```ts
  it("emits a table-json spec before the text grid when a documentUri is given", () => {
    const specs = mapEnvelope(
      envelope({
        kind: { Case: "Table" },
        schema: [{ name: "n", displayType: "int" }],
        rows: [[1], [2], [3]],
        isTruncated: true,
        continuationToken: "tok",
        elapsedMs: 7,
      }),
      { documentUri: "file:///a.fsqnb" },
    );
    expect(specs.map((s) => s.kind)).toEqual(["table-json", "text"]);
    const json = specs[0] as Extract<OutputItemSpec, { kind: "table-json" }>;
    expect(json.payload.documentUri).toBe("file:///a.fsqnb");
    expect(json.payload.continuationToken).toBe("tok");
    expect(json.payload.rows).toHaveLength(3);
  });

  it("still emits only the text grid when no documentUri is given (F2 behavior)", () => {
    const specs = mapEnvelope(
      envelope({
        kind: { Case: "Table" },
        schema: [{ name: "n", displayType: "int" }],
        rows: [[1]],
        elapsedMs: 7,
      }),
    );
    expect(specs.map((s) => s.kind)).toEqual(["text"]);
  });

  it("puts stdout/stderr lines before the table-json spec", () => {
    const specs = mapEnvelope(
      envelope({
        kind: { Case: "Table" },
        schema: [{ name: "n", displayType: "int" }],
        rows: [[1]],
        output: [{ stream: { Case: "Stdout" }, text: "hi\n" }],
      }),
      { documentUri: "u" },
    );
    expect(specs.map((s) => s.kind)).toEqual(["stdout", "table-json", "text"]);
  });
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd editors/vscode && npx vitest run src/notebook/result.test.ts`
Expected: FAIL — `mapEnvelope` ignores the second argument; `specs.map(...)` is `["text"]` not `["table-json","text"]`; truncation note still says `"F3"`.

- [ ] **Step 3: Edit `editors/vscode/src/notebook/result.ts`**

Add the import near the top:

```ts
import { buildTablePayload } from "./table-payload";
import type { TablePayload } from "../renderer/types";
```

Extend the `OutputItemSpec` union with the new member:

```ts
export type OutputItemSpec =
  | { kind: "stdout"; text: string }
  | { kind: "stderr"; text: string }
  | { kind: "text"; text: string }
  | { kind: "error"; name: string; message: string }
  | { kind: "table-json"; payload: TablePayload };
```

In `formatTextTable`, change the truncation line from:

```ts
    out.push("(truncated — full grid and paging arrive in F3)");
```

to:

```ts
    out.push("(truncated — more rows available in the grid view)");
```

Change the `mapEnvelope` signature and its `Table` case:

```ts
export function mapEnvelope(
  env: ResultEnvelope,
  opts?: { documentUri?: string },
): OutputItemSpec[] {
```

```ts
    case "Table": {
      if (opts?.documentUri) {
        specs.push({ kind: "table-json", payload: buildTablePayload(env, opts.documentUri) });
      }
      specs.push({
        kind: "text",
        text: formatTextTable(env.schema ?? [], env.rows ?? [], env.elapsedMs, env.isTruncated),
      });
      break;
    }
```

(The `switch` already has `case "Table":` — replace its body with the block above, keeping the surrounding `switch`.)

- [ ] **Step 4: Run to verify it passes, then the whole suite**

Run: `cd editors/vscode && npx vitest run src/notebook/result.test.ts`
Expected: PASS.
Run: `cd editors/vscode && npm test`
Expected: PASS — `controller.test.ts` still green (it calls `mapEnvelope` indirectly via the controller, which does not yet pass `documentUri`; Task 5 changes that).

- [ ] **Step 5: DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-06 — F3.4: mapEnvelope emits a table-json output spec

`notebook/result.ts`: `OutputItemSpec` gains `{ kind: "table-json"; payload: TablePayload }`, and `mapEnvelope(env, { documentUri })` now prepends that spec (built via `buildTablePayload`) before the `text/plain` grid for Table envelopes. Called without a `documentUri` it keeps the exact F2 behavior (text only), so the renderer item only appears on the real controller path. Text-grid truncation note reworded ("more rows available in the grid view").
```

- [ ] **Step 6: Commit**

```bash
git add editors/vscode/src/notebook/result.ts editors/vscode/src/notebook/result.test.ts docs/DEVLOG.md
git commit -m "feat(vscode): mapEnvelope emits the table-json renderer item for Table results"
```

---

### Task 5: Controller maps `table-json` to a MIME output item (`src/notebook/controller.ts`)

**Files:**
- Modify: `editors/vscode/src/notebook/controller.ts`
- Modify: `editors/vscode/src/notebook/controller.test.ts`
- Modify: `editors/vscode/test/vscode-stub.ts`

**Interfaces:**
- Consumes: `FSQUERY_TABLE_MIME` from `../renderer/types`.
- Produces:
  - `toOutputItem` handles `spec.kind === "table-json"` → `vscode.NotebookCellOutputItem.json(spec.payload, FSQUERY_TABLE_MIME)`.
  - `runBatch` calls `mapEnvelope(envelope, { documentUri: sessionId })` (was `mapEnvelope(envelope)`).
  - `test/vscode-stub.ts` `NotebookCellOutputItem` gains `static json(value: unknown, mime?: string)` — stores `data` as `new TextEncoder().encode(JSON.stringify(value))`, `mime` defaulting to `"application/json"`.

- [ ] **Step 1: Add `NotebookCellOutputItem.json` to `editors/vscode/test/vscode-stub.ts`**

Inside `class NotebookCellOutputItem`, add after `static text(...)`:

```ts
  static json(value: unknown, mime = "application/json") {
    return new NotebookCellOutputItem(new TextEncoder().encode(JSON.stringify(value)), mime);
  }
```

- [ ] **Step 2: Update `editors/vscode/src/notebook/controller.test.ts`**

The existing test `"writes the mapped outputs and ends the execution successfully for a non-error result"` currently asserts `outputs[0].items[1].mime === "text/plain"`. With F3 the Table output items are `[stdout, table-json, text/plain]`. Replace that test's assertions block with:

```ts
    const exec = execs(controller)[0];
    expect(exec.ended).toBe(true);
    const outputs = exec.outputs as Array<{ items: Array<{ data: unknown; mime: string }> }>;
    expect(outputs[0].items.map((i) => i.mime)).toEqual([
      "application/vnd.code.notebook.stdout",
      "application/x-fsquery-table+json",
      "text/plain",
    ]);
    const tableItem = outputs[0].items[1];
    const body = JSON.parse(new TextDecoder().decode(tableItem.data as Uint8Array));
    expect(body.documentUri).toBe("file:///a.fsqnb");
    expect(body.rows).toEqual([[1], [2]]);
```

Add a focused test right after it:

```ts
  it("omits the table-json item for a non-Table result", async () => {
    const worker: WorkerLike = {
      send: async (req) => ({
        id: req.id,
        result: tableEnvelope({ kind: { Case: "Scalar" }, displayText: "42", schema: [], rows: [] }),
      }),
      cancel: async () => {},
    };
    const controller = createFsQueryController({ getWorker: async () => worker, log: () => {} });
    await controller.executeHandler!([cell("q")], notebook("file:///a.fsqnb"), controller);
    const outputs = execs(controller)[0].outputs as Array<{ items: Array<{ mime: string }> }>;
    expect(outputs[0].items.some((i) => i.mime === "application/x-fsquery-table+json")).toBe(false);
  });
```

- [ ] **Step 3: Run to verify it fails**

Run: `cd editors/vscode && npx vitest run src/notebook/controller.test.ts`
Expected: FAIL — items are `[stdout, text/plain]` (controller still calls `mapEnvelope(envelope)` with no `documentUri`), so the `.toEqual([...3 mimes])` assertion fails.

- [ ] **Step 4: Edit `editors/vscode/src/notebook/controller.ts`**

Add to the imports from `../renderer/types` (new import line):

```ts
import { FSQUERY_TABLE_MIME } from "../renderer/types";
```

In `toOutputItem`, add a case before `case "error":`:

```ts
    case "table-json":
      return vscode.NotebookCellOutputItem.json(spec.payload, FSQUERY_TABLE_MIME);
```

In `runBatch`, change:

```ts
        const items = mapEnvelope(envelope).map(toOutputItem);
```

to:

```ts
        const items = mapEnvelope(envelope, { documentUri: sessionId }).map(toOutputItem);
```

- [ ] **Step 5: Run to verify it passes, then the whole suite**

Run: `cd editors/vscode && npx vitest run src/notebook/controller.test.ts`
Expected: PASS.
Run: `cd editors/vscode && npm test`
Expected: PASS.

- [ ] **Step 6: DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-06 — F3.5: controller writes the table-json MIME item

`notebook/controller.ts`: `toOutputItem` maps a `table-json` spec to `NotebookCellOutputItem.json(payload, "application/x-fsquery-table+json")`, and `runBatch` passes `{ documentUri: sessionId }` into `mapEnvelope` so Table cells now carry three items — stdout/stderr, the renderer JSON, and the `text/plain` fallback. Stub `NotebookCellOutputItem.json` added.
```

- [ ] **Step 7: Commit**

```bash
git add editors/vscode/src/notebook/controller.ts editors/vscode/src/notebook/controller.test.ts editors/vscode/test/vscode-stub.ts docs/DEVLOG.md
git commit -m "feat(vscode): controller writes the table-json output item for Table cells"
```

---

### Task 6: Renderer host — messaging → worker RPCs (`src/notebook/renderer-host.ts`)

**Files:**
- Create: `editors/vscode/src/notebook/renderer-host.ts`
- Test: `editors/vscode/src/notebook/renderer-host.test.ts`

**Interfaces:**
- Consumes: `type WorkerResponseFrame`, `newRequestId` from `../protocol`; `type HostToRendererMessage`, `type RendererToHostMessage`, `isRendererToHostMessage` from `../renderer/types`.
- Produces:
  - `interface WorkerSendLike { send(req: { id: string; method: string; params: unknown }): Promise<WorkerResponseFrame> }`
  - `interface RendererMessagingLike { onDidReceiveMessage(listener: (e: { editor: unknown; message: unknown }) => unknown): { dispose(): void }; postMessage(message: HostToRendererMessage, editor?: unknown): Promise<boolean> | Thenable<boolean> }`
  - `interface RendererHostDeps { messaging: RendererMessagingLike; getWorker: () => Promise<WorkerSendLike>; pickSaveFile: (defaultName: string, documentUri: string) => Promise<string | undefined>; log: (line: string) => void }`
  - `function registerRendererHost(deps: RendererHostDeps): { dispose(): void }` — subscribes to `messaging.onDidReceiveMessage`; ignores anything failing `isRendererToHostMessage`; handles `loadMore` and `exportCsv` (see steps). Every reply is `messaging.postMessage(reply, e.editor)`. A thrown `getWorker()` (carries `userMessage`) or a thrown `send` becomes an `error` message with the matching `scope`. Returns the subscription disposable.

- [ ] **Step 1: Write `editors/vscode/src/notebook/renderer-host.test.ts`**

```ts
import { describe, it, expect } from "vitest";
import { registerRendererHost, type RendererHostDeps, type WorkerSendLike } from "./renderer-host";
import type { WorkerResponseFrame } from "../protocol";
import type { HostToRendererMessage } from "../renderer/types";

function harness(overrides: Partial<RendererHostDeps> = {}) {
  let listener: ((e: { editor: unknown; message: unknown }) => unknown) | undefined;
  const posted: Array<{ message: HostToRendererMessage; editor: unknown }> = [];
  const messaging = {
    onDidReceiveMessage(fn: (e: { editor: unknown; message: unknown }) => unknown) {
      listener = fn;
      return { dispose: () => (listener = undefined) };
    },
    postMessage(message: HostToRendererMessage, editor?: unknown) {
      posted.push({ message, editor });
      return Promise.resolve(true);
    },
  };
  const deps: RendererHostDeps = {
    messaging,
    getWorker: async () => ({ send: async (r) => ({ id: r.id, result: {} }) }) as WorkerSendLike,
    pickSaveFile: async () => "/tmp/results.csv",
    log: () => {},
    ...overrides,
  };
  const host = registerRendererHost(deps);
  return {
    host,
    posted,
    send: (message: unknown, editor: unknown = { id: "ed1" }) => listener!({ editor, message }),
  };
}

describe("registerRendererHost — loadMore", () => {
  it("calls results/page with the token and posts the returned rows", async () => {
    const calls: Array<{ method: string; params: unknown }> = [];
    const h = harness({
      getWorker: async () => ({
        send: async (r) => {
          calls.push({ method: r.method, params: r.params });
          return {
            id: r.id,
            result: { rows: [[4], [5]], isTruncated: true, continuationToken: "next" },
          } as WorkerResponseFrame;
        },
      }),
    });
    await h.send({ type: "loadMore", outputId: "o1", documentUri: "file:///q.fsqnb", token: "t0" });

    expect(calls[0].method).toBe("results/page");
    expect(calls[0].params).toEqual({ sessionId: "file:///q.fsqnb", continuationToken: "t0" });
    expect(h.posted[0].message).toEqual({
      type: "rows",
      outputId: "o1",
      rows: [[4], [5]],
      truncated: true,
      nextToken: "next",
    });
    expect(h.posted[0].editor).toEqual({ id: "ed1" });
  });

  it("maps results/unknownToken to a friendly loadMore error", async () => {
    const h = harness({
      getWorker: async () => ({
        send: async (r) => ({
          id: r.id,
          error: { code: "results/unknownToken", message: "expired" },
        }),
      }),
    });
    await h.send({ type: "loadMore", outputId: "o1", documentUri: "u", token: "t" });
    expect(h.posted[0].message).toMatchObject({ type: "error", outputId: "o1", scope: "loadMore" });
    expect((h.posted[0].message as { message: string }).message).toMatch(/re-?run/i);
  });

  it("reports a worker-unavailable error with its userMessage", async () => {
    const h = harness({
      getWorker: async () => {
        throw Object.assign(new Error("x"), { userMessage: "No .NET 10 on PATH." });
      },
    });
    await h.send({ type: "loadMore", outputId: "o1", documentUri: "u", token: "t" });
    expect(h.posted[0].message).toEqual({
      type: "error",
      outputId: "o1",
      scope: "loadMore",
      message: "No .NET 10 on PATH.",
    });
  });
});

describe("registerRendererHost — exportCsv", () => {
  it("prompts for a path, calls results/exportCsv, and posts exportDone", async () => {
    const calls: Array<{ method: string; params: unknown }> = [];
    const h = harness({
      pickSaveFile: async (name, uri) => {
        expect(name).toBe("results.csv");
        expect(uri).toBe("file:///q.fsqnb");
        return "/home/me/out.csv";
      },
      getWorker: async () => ({
        send: async (r) => {
          calls.push({ method: r.method, params: r.params });
          return { id: r.id, result: { acknowledged: true } };
        },
      }),
    });
    await h.send({ type: "exportCsv", outputId: "o1", documentUri: "file:///q.fsqnb" });

    expect(calls[0].method).toBe("results/exportCsv");
    expect(calls[0].params).toEqual({
      documentUri: "file:///q.fsqnb",
      filePath: "/home/me/out.csv",
    });
    expect(h.posted[0].message).toEqual({ type: "exportDone", outputId: "o1", path: "/home/me/out.csv" });
  });

  it("stays silent when the save dialog is cancelled", async () => {
    const h = harness({ pickSaveFile: async () => undefined });
    await h.send({ type: "exportCsv", outputId: "o1", documentUri: "u" });
    expect(h.posted).toHaveLength(0);
  });

  it("explains results/nothingToExport as a limitation, not a raw error", async () => {
    const h = harness({
      getWorker: async () => ({
        send: async (r) => ({
          id: r.id,
          error: { code: "results/nothingToExport", message: "no retained table" },
        }),
      }),
    });
    await h.send({ type: "exportCsv", outputId: "o1", documentUri: "u" });
    expect(h.posted[0].message).toMatchObject({ type: "error", scope: "exportCsv" });
    expect((h.posted[0].message as { message: string }).message).toMatch(/Results\.table/);
  });
});

describe("registerRendererHost — hygiene", () => {
  it("ignores messages that are not renderer→host shapes", async () => {
    const h = harness();
    await h.send({ type: "rows", outputId: "x" });
    await h.send("nope");
    await h.send(null);
    expect(h.posted).toHaveLength(0);
  });

  it("dispose() unsubscribes", async () => {
    const h = harness();
    h.host.dispose();
    expect(() => h.send({ type: "exportCsv", outputId: "o", documentUri: "u" })).toThrow();
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd editors/vscode && npx vitest run src/notebook/renderer-host.test.ts`
Expected: FAIL — `Cannot find module './renderer-host'`.

- [ ] **Step 3: Write `editors/vscode/src/notebook/renderer-host.ts`**

```ts
import { newRequestId, type WorkerResponseFrame } from "../protocol";
import {
  isRendererToHostMessage,
  type HostToRendererMessage,
  type RendererToHostMessage,
} from "../renderer/types";

/** The one `WorkerClient` method this module needs — injectable for tests. */
export interface WorkerSendLike {
  send(req: { id: string; method: string; params: unknown }): Promise<WorkerResponseFrame>;
}

/**
 * The slice of `vscode.NotebookRendererMessaging` used here. `postMessage`
 * takes the `editor` from the incoming event so a reply lands on the
 * webview that asked.
 */
export interface RendererMessagingLike {
  onDidReceiveMessage(
    listener: (e: { editor: unknown; message: unknown }) => unknown,
  ): { dispose(): void };
  postMessage(
    message: HostToRendererMessage,
    editor?: unknown,
  ): Promise<boolean> | Thenable<boolean>;
}

export interface RendererHostDeps {
  messaging: RendererMessagingLike;
  /** Resolves the shared worker; may reject with a `userMessage`-carrying error. */
  getWorker: () => Promise<WorkerSendLike>;
  /** Opens a save dialog; resolves to the chosen path, or `undefined` if cancelled. */
  pickSaveFile: (defaultName: string, documentUri: string) => Promise<string | undefined>;
  log: (line: string) => void;
}

const LOAD_MORE_STALE =
  "This result is no longer available for paging — re-run the cell to load more rows.";
const EXPORT_UNAVAILABLE =
  "CSV export is only available for tables produced by Results.table.";

function errorMessage(err: unknown): string {
  return (err as { userMessage?: string }).userMessage ?? (err as Error).message;
}

export function registerRendererHost(deps: RendererHostDeps): { dispose(): void } {
  const subscription = deps.messaging.onDidReceiveMessage(async ({ editor, message }) => {
    if (!isRendererToHostMessage(message)) {
      return;
    }
    const msg: RendererToHostMessage = message;
    try {
      if (msg.type === "loadMore") {
        await handleLoadMore(deps, msg, editor);
      } else {
        await handleExportCsv(deps, msg, editor);
      }
    } catch (err) {
      deps.log(`renderer-host: ${msg.type} failed: ${errorMessage(err)}`);
      void deps.messaging.postMessage(
        {
          type: "error",
          outputId: msg.outputId,
          scope: msg.type === "loadMore" ? "loadMore" : "exportCsv",
          message: errorMessage(err),
        },
        editor,
      );
    }
  });

  return { dispose: () => subscription.dispose() };
}

async function handleLoadMore(
  deps: RendererHostDeps,
  msg: Extract<RendererToHostMessage, { type: "loadMore" }>,
  editor: unknown,
): Promise<void> {
  const worker = await deps.getWorker();
  const response = await worker.send({
    id: newRequestId(),
    method: "results/page",
    params: { sessionId: msg.documentUri, continuationToken: msg.token },
  });

  if (response.error) {
    const text =
      response.error.code === "results/unknownToken" ? LOAD_MORE_STALE : response.error.message;
    void deps.messaging.postMessage(
      { type: "error", outputId: msg.outputId, scope: "loadMore", message: text },
      editor,
    );
    return;
  }

  const page = (response.result ?? {}) as {
    rows?: unknown[][];
    isTruncated?: boolean;
    continuationToken?: string | null;
  };
  void deps.messaging.postMessage(
    {
      type: "rows",
      outputId: msg.outputId,
      rows: page.rows ?? [],
      truncated: page.isTruncated === true,
      nextToken: page.continuationToken ?? null,
    },
    editor,
  );
}

async function handleExportCsv(
  deps: RendererHostDeps,
  msg: Extract<RendererToHostMessage, { type: "exportCsv" }>,
  editor: unknown,
): Promise<void> {
  const target = await deps.pickSaveFile("results.csv", msg.documentUri);
  if (!target) {
    return; // cancelled — nothing to report
  }

  const worker = await deps.getWorker();
  const response = await worker.send({
    id: newRequestId(),
    method: "results/exportCsv",
    params: { documentUri: msg.documentUri, filePath: target },
  });

  if (response.error) {
    const text =
      response.error.code === "results/nothingToExport"
        ? EXPORT_UNAVAILABLE
        : response.error.message;
    void deps.messaging.postMessage(
      { type: "error", outputId: msg.outputId, scope: "exportCsv", message: text },
      editor,
    );
    return;
  }

  void deps.messaging.postMessage(
    { type: "exportDone", outputId: msg.outputId, path: target },
    editor,
  );
}
```

- [ ] **Step 4: Run to verify it passes, then the whole suite**

Run: `cd editors/vscode && npx vitest run src/notebook/renderer-host.test.ts`
Expected: PASS.
Run: `cd editors/vscode && npm test`
Expected: PASS.

- [ ] **Step 5: DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-06 — F3.6: renderer host (messaging → worker RPCs)

`notebook/renderer-host.ts`: `registerRendererHost` subscribes to `NotebookRendererMessaging`, validates inbound messages, and answers `loadMore` via `results/page` (`{ sessionId, continuationToken }`; `results/unknownToken` → "re-run the cell" text) and `exportCsv` via a save dialog + `results/exportCsv` (`results/nothingToExport` → a plain "only Results.table tables" line; a cancelled dialog stays silent). Worker-unavailable and thrown sends collapse into a scoped `error` message. Fully dependency-injected — no `vscode` import.
```

- [ ] **Step 6: Commit**

```bash
git add editors/vscode/src/notebook/renderer-host.ts editors/vscode/src/notebook/renderer-host.test.ts docs/DEVLOG.md
git commit -m "feat(vscode): renderer host bridges grid messages to results/page and results/exportCsv"
```

---

### Task 7: The grid renderer (`src/renderer/index.ts`)

**Files:**
- Create: `editors/vscode/src/renderer/index.ts`
- Test: `editors/vscode/src/renderer/index.test.ts`
- Modify: `editors/vscode/package.json` (add `jsdom` devDependency)

**Interfaces:**
- Consumes: `visibleRange`, `formatCell` from `./grid-core`; `GRID_ROW_HEIGHT_PX`, `type TablePayload`, `type HostToRendererMessage`, `type RendererToHostMessage` from `./types`.
- Produces:
  - `interface OutputItemLike { id: string; json(): unknown }`
  - `interface RendererContextLike { postMessage?(message: RendererToHostMessage): void; onDidReceiveMessage?(listener: (message: HostToRendererMessage) => void): { dispose(): void } }`
  - `function activate(context: RendererContextLike): { renderOutputItem(item: OutputItemLike, element: HTMLElement): void; disposeOutputItem(id: string): void }`
  - Behavior:
    - `renderOutputItem`: parse `item.json()` as `TablePayload`; build (or rebuild) a `Grid` bound to `item.id` inside `element`. A `<style>` block is injected into `element` once per element.
    - Toolbar: two view buttons (`Data`, `Schema`), a row-count label (`"<n> rows"` / `"<n> row"`), an `Export CSV` button. `Data` shows the windowed grid; `Schema` shows a two-column `name` / `displayType` table.
    - Windowed body: a scrolling `.fsq-viewport`, an inner sizer of height `rows.length * GRID_ROW_HEIGHT_PX`, and a translated `.fsq-rows` holding only `visibleRange(scrollTop, GRID_ROW_HEIGHT_PX, viewport.clientHeight || 400, rows.length)` rows. Recompute on `scroll` (via `requestAnimationFrame` when available, else synchronously).
    - `Load more` button below the body — present only when the grid's current `token` is a non-empty string; clicking it posts `{ type: "loadMore", outputId: id, documentUri, token }` and disables itself until a reply.
    - `Export CSV` click posts `{ type: "exportCsv", outputId: id, documentUri }`.
    - Inbound routing (`context.onDidReceiveMessage`): `rows` with a matching `outputId` → append rows, set `token = nextToken`, hide `Load more` when `token` is null, refresh the window and count; `error` → show `.fsq-notice` with the message and, for `scope: "loadMore"`, hide `Load more`; `exportDone` → briefly show `.fsq-notice` as a confirmation (`"Exported to <path>"`).
    - `disposeOutputItem(id)`: drop the `Grid` from the internal map.
  - All colors are `var(--vscode-*)`; no literal hex.

- [ ] **Step 1: Add `jsdom` to `editors/vscode/package.json` devDependencies**

Add `"jsdom": "^25.0.1"` to `devDependencies` (keep alphabetical-ish ordering near `esbuild`). Then:

Run: `cd editors/vscode && npm install`
Expected: `jsdom` resolved, `package-lock.json` updated.

- [ ] **Step 2: Write `editors/vscode/src/renderer/index.test.ts`**

```ts
// @vitest-environment jsdom
import { describe, it, expect, beforeEach } from "vitest";
import { activate, type RendererContextLike, type OutputItemLike } from "./index";
import type { HostToRendererMessage, RendererToHostMessage, TablePayload } from "./types";

function payload(overrides: Partial<TablePayload> = {}): TablePayload {
  return {
    documentUri: "file:///q.fsqnb",
    columns: [
      { name: "id", displayType: "int" },
      { name: "name", displayType: "string" },
    ],
    rows: [
      [1, "ada"],
      [2, "grace"],
      [3, "lin"],
    ],
    truncated: false,
    continuationToken: null,
    elapsedMs: 4,
    ...overrides,
  };
}

function ctx() {
  const sent: RendererToHostMessage[] = [];
  let route: ((m: HostToRendererMessage) => void) | undefined;
  const context: RendererContextLike = {
    postMessage: (m) => sent.push(m),
    onDidReceiveMessage: (fn) => {
      route = fn;
      return { dispose: () => (route = undefined) };
    },
  };
  return { context, sent, route: (m: HostToRendererMessage) => route!(m) };
}

const item = (id: string, p: TablePayload): OutputItemLike => ({ id, json: () => p });

let host: ReturnType<typeof activate>;
let el: HTMLElement;

beforeEach(() => {
  el = document.createElement("div");
  document.body.appendChild(el);
});

describe("grid renderer", () => {
  it("renders the column headers and some rows", () => {
    host = activate(ctx().context);
    host.renderOutputItem(item("o1", payload()), el);
    expect(el.textContent).toContain("id");
    expect(el.textContent).toContain("name");
    expect(el.textContent).toContain("ada");
    expect(el.querySelector(".fsq-count")?.textContent).toBe("3 rows");
  });

  it("hides Load more when there is no token", () => {
    host = activate(ctx().context);
    host.renderOutputItem(item("o1", payload()), el);
    const btn = el.querySelector(".fsq-loadmore") as HTMLButtonElement;
    expect(btn.hidden).toBe(true);
  });

  it("shows Load more and posts loadMore on click when truncated with a token", () => {
    const c = ctx();
    host = activate(c.context);
    host.renderOutputItem(
      item("o1", payload({ truncated: true, continuationToken: "t0" })),
      el,
    );
    const btn = el.querySelector(".fsq-loadmore") as HTMLButtonElement;
    expect(btn.hidden).toBe(false);
    btn.click();
    expect(c.sent[0]).toEqual({
      type: "loadMore",
      outputId: "o1",
      documentUri: "file:///q.fsqnb",
      token: "t0",
    });
  });

  it("appends rows and updates the count when a rows message arrives", () => {
    const c = ctx();
    host = activate(c.context);
    host.renderOutputItem(
      item("o1", payload({ truncated: true, continuationToken: "t0" })),
      el,
    );
    c.route({ type: "rows", outputId: "o1", rows: [[4, "kay"]], truncated: false, nextToken: null });
    expect(el.querySelector(".fsq-count")?.textContent).toBe("4 rows");
    expect((el.querySelector(".fsq-loadmore") as HTMLButtonElement).hidden).toBe(true);
  });

  it("routes a rows message only to the matching outputId", () => {
    const c = ctx();
    host = activate(c.context);
    const elA = document.createElement("div");
    const elB = document.createElement("div");
    host.renderOutputItem(item("a", payload({ truncated: true, continuationToken: "t" })), elA);
    host.renderOutputItem(item("b", payload()), elB);
    c.route({ type: "rows", outputId: "a", rows: [[9, "z"]], truncated: false, nextToken: null });
    expect(elA.querySelector(".fsq-count")?.textContent).toBe("4 rows");
    expect(elB.querySelector(".fsq-count")?.textContent).toBe("3 rows");
  });

  it("toggles to the schema view and back", () => {
    host = activate(ctx().context);
    host.renderOutputItem(item("o1", payload()), el);
    (el.querySelector('[data-view="schema"]') as HTMLButtonElement).click();
    const schema = el.querySelector(".fsq-schema") as HTMLElement;
    expect(schema.hidden).toBe(false);
    expect(schema.textContent).toContain("displayType");
    expect(schema.textContent).toContain("int");
    (el.querySelector('[data-view="data"]') as HTMLButtonElement).click();
    expect((el.querySelector(".fsq-schema") as HTMLElement).hidden).toBe(true);
  });

  it("posts exportCsv on Export click", () => {
    const c = ctx();
    host = activate(c.context);
    host.renderOutputItem(item("o1", payload()), el);
    (el.querySelector(".fsq-export") as HTMLButtonElement).click();
    expect(c.sent[0]).toEqual({ type: "exportCsv", outputId: "o1", documentUri: "file:///q.fsqnb" });
  });

  it("shows an error notice and hides Load more on a loadMore error", () => {
    const c = ctx();
    host = activate(c.context);
    host.renderOutputItem(
      item("o1", payload({ truncated: true, continuationToken: "t0" })),
      el,
    );
    c.route({ type: "error", outputId: "o1", scope: "loadMore", message: "re-run the cell" });
    const notice = el.querySelector(".fsq-notice") as HTMLElement;
    expect(notice.hidden).toBe(false);
    expect(notice.textContent).toContain("re-run the cell");
    expect((el.querySelector(".fsq-loadmore") as HTMLButtonElement).hidden).toBe(true);
  });

  it("uses a singular label for one row", () => {
    host = activate(ctx().context);
    host.renderOutputItem(item("o1", payload({ rows: [[1, "solo"]] })), el);
    expect(el.querySelector(".fsq-count")?.textContent).toBe("1 row");
  });
});
```

- [ ] **Step 3: Run to verify it fails**

Run: `cd editors/vscode && npx vitest run src/renderer/index.test.ts`
Expected: FAIL — `Cannot find module './index'`.

- [ ] **Step 4: Write `editors/vscode/src/renderer/index.ts`**

```ts
import { visibleRange, formatCell } from "./grid-core";
import {
  GRID_ROW_HEIGHT_PX,
  type HostToRendererMessage,
  type RendererToHostMessage,
  type TablePayload,
} from "./types";

/** The bits of `vscode-notebook-renderer`'s `OutputItem` used here. */
export interface OutputItemLike {
  id: string;
  json(): unknown;
}

/** The bits of the renderer `RendererContext` used here (messaging is optional). */
export interface RendererContextLike {
  postMessage?(message: RendererToHostMessage): void;
  onDidReceiveMessage?(listener: (message: HostToRendererMessage) => void): { dispose(): void };
}

const STYLE = `
.fsq-grid { font: 12px var(--vscode-editor-font-family, monospace); color: var(--vscode-foreground); }
.fsq-toolbar { display: flex; gap: 6px; align-items: center; padding: 4px 0; }
.fsq-toolbar button { background: var(--vscode-button-secondaryBackground, transparent);
  color: var(--vscode-button-secondaryForeground, var(--vscode-foreground));
  border: 1px solid var(--vscode-panel-border); border-radius: 2px; padding: 2px 8px; cursor: pointer; }
.fsq-toolbar button.active { background: var(--vscode-button-background); color: var(--vscode-button-foreground); }
.fsq-count { margin-left: auto; opacity: 0.8; }
.fsq-viewport { overflow: auto; max-height: 360px; border: 1px solid var(--vscode-panel-border); }
.fsq-sizer { position: relative; }
.fsq-rows { position: absolute; left: 0; right: 0; }
.fsq-row { display: flex; height: ${GRID_ROW_HEIGHT_PX}px; line-height: ${GRID_ROW_HEIGHT_PX}px; }
.fsq-row:hover { background: var(--vscode-list-hoverBackground); }
.fsq-head { display: flex; height: ${GRID_ROW_HEIGHT_PX}px; line-height: ${GRID_ROW_HEIGHT_PX}px;
  position: sticky; top: 0; background: var(--vscode-editor-background);
  border-bottom: 1px solid var(--vscode-panel-border); font-weight: 600; }
.fsq-cell { flex: 1 1 0; min-width: 80px; padding: 0 8px; overflow: hidden;
  text-overflow: ellipsis; white-space: nowrap; }
.fsq-schema { border: 1px solid var(--vscode-panel-border); }
.fsq-schema .fsq-row { padding: 0 8px; }
.fsq-loadmore { margin: 6px 0; padding: 2px 10px; cursor: pointer;
  background: var(--vscode-button-background); color: var(--vscode-button-foreground);
  border: none; border-radius: 2px; }
.fsq-notice { margin: 6px 0; padding: 4px 8px; border-radius: 2px;
  background: var(--vscode-inputValidation-warningBackground, var(--vscode-editorWidget-background));
  border: 1px solid var(--vscode-inputValidation-warningBorder, var(--vscode-panel-border)); }
`;

interface Grid {
  id: string;
  payload: TablePayload;
  rows: unknown[][];
  token: string | null;
  view: "data" | "schema";
  els: {
    count: HTMLElement;
    viewport: HTMLElement;
    sizer: HTMLElement;
    rowsHost: HTMLElement;
    schema: HTMLElement;
    loadMore: HTMLButtonElement;
    notice: HTMLElement;
    dataBtn: HTMLButtonElement;
    schemaBtn: HTMLButtonElement;
  };
}

function raf(fn: () => void): void {
  if (typeof requestAnimationFrame === "function") {
    requestAnimationFrame(fn);
  } else {
    fn();
  }
}

function countLabel(n: number): string {
  return `${n} ${n === 1 ? "row" : "rows"}`;
}

export function activate(context: RendererContextLike): {
  renderOutputItem(item: OutputItemLike, element: HTMLElement): void;
  disposeOutputItem(id: string): void;
} {
  const grids = new Map<string, Grid>();

  context.onDidReceiveMessage?.((message) => {
    const grid = grids.get(message.outputId);
    if (!grid) {
      return;
    }
    if (message.type === "rows") {
      grid.rows = grid.rows.concat(message.rows);
      grid.token = message.nextToken;
      grid.els.loadMore.disabled = false;
      grid.els.loadMore.hidden = !grid.token;
      grid.els.count.textContent = countLabel(grid.rows.length);
      renderWindow(grid);
    } else if (message.type === "error") {
      showNotice(grid, message.message);
      grid.els.loadMore.disabled = false;
      if (message.scope === "loadMore") {
        grid.els.loadMore.hidden = true;
      }
    } else if (message.type === "exportDone") {
      showNotice(grid, `Exported to ${message.path}`);
    }
  });

  function showNotice(grid: Grid, text: string): void {
    grid.els.notice.textContent = text;
    grid.els.notice.hidden = false;
  }

  function renderWindow(grid: Grid): void {
    const { viewport, sizer, rowsHost } = grid.els;
    const total = grid.rows.length;
    sizer.style.height = `${total * GRID_ROW_HEIGHT_PX}px`;
    const { start, end } = visibleRange(
      viewport.scrollTop,
      GRID_ROW_HEIGHT_PX,
      viewport.clientHeight || 400,
      total,
    );
    rowsHost.style.transform = `translateY(${start * GRID_ROW_HEIGHT_PX}px)`;
    rowsHost.textContent = "";
    for (let i = start; i < end; i++) {
      const row = document.createElement("div");
      row.className = "fsq-row";
      for (const col of grid.payload.columns.length ? grid.payload.columns : [{ name: "", displayType: "" }]) {
        void col;
      }
      const values = grid.rows[i] ?? [];
      const width = Math.max(grid.payload.columns.length, values.length, 1);
      for (let c = 0; c < width; c++) {
        const cell = document.createElement("div");
        cell.className = "fsq-cell";
        cell.textContent = formatCell(values[c]);
        row.appendChild(cell);
      }
      rowsHost.appendChild(row);
    }
  }

  function setView(grid: Grid, view: "data" | "schema"): void {
    grid.view = view;
    grid.els.dataBtn.classList.toggle("active", view === "data");
    grid.els.schemaBtn.classList.toggle("active", view === "schema");
    grid.els.viewport.hidden = view !== "data";
    grid.els.schema.hidden = view !== "schema";
  }

  function build(item: OutputItemLike, element: HTMLElement): Grid {
    const payload = item.json() as TablePayload;
    element.textContent = "";

    const style = document.createElement("style");
    style.textContent = STYLE;
    element.appendChild(style);

    const root = document.createElement("div");
    root.className = "fsq-grid";
    element.appendChild(root);

    const toolbar = document.createElement("div");
    toolbar.className = "fsq-toolbar";
    const dataBtn = document.createElement("button");
    dataBtn.dataset.view = "data";
    dataBtn.textContent = "Data";
    const schemaBtn = document.createElement("button");
    schemaBtn.dataset.view = "schema";
    schemaBtn.textContent = "Schema";
    const count = document.createElement("span");
    count.className = "fsq-count";
    const exportBtn = document.createElement("button");
    exportBtn.className = "fsq-export";
    exportBtn.textContent = "Export CSV";
    toolbar.append(dataBtn, schemaBtn, count, exportBtn);
    root.appendChild(toolbar);

    const head = document.createElement("div");
    head.className = "fsq-head";
    for (const col of payload.columns) {
      const cell = document.createElement("div");
      cell.className = "fsq-cell";
      cell.textContent = col.name;
      head.appendChild(cell);
    }
    root.appendChild(head);

    const viewport = document.createElement("div");
    viewport.className = "fsq-viewport";
    const sizer = document.createElement("div");
    sizer.className = "fsq-sizer";
    const rowsHost = document.createElement("div");
    rowsHost.className = "fsq-rows";
    sizer.appendChild(rowsHost);
    viewport.appendChild(sizer);
    root.appendChild(viewport);

    const schema = document.createElement("div");
    schema.className = "fsq-schema";
    schema.hidden = true;
    const schemaHead = document.createElement("div");
    schemaHead.className = "fsq-row";
    schemaHead.innerHTML = `<div class="fsq-cell">name</div><div class="fsq-cell">displayType</div>`;
    schema.appendChild(schemaHead);
    for (const col of payload.columns) {
      const r = document.createElement("div");
      r.className = "fsq-row";
      const n = document.createElement("div");
      n.className = "fsq-cell";
      n.textContent = col.name;
      const t = document.createElement("div");
      t.className = "fsq-cell";
      t.textContent = col.displayType;
      r.append(n, t);
      schema.appendChild(r);
    }
    root.appendChild(schema);

    const notice = document.createElement("div");
    notice.className = "fsq-notice";
    notice.hidden = true;
    root.appendChild(notice);

    const loadMore = document.createElement("button");
    loadMore.className = "fsq-loadmore";
    loadMore.textContent = "Load more";
    loadMore.hidden = !payload.continuationToken;
    root.appendChild(loadMore);

    const grid: Grid = {
      id: item.id,
      payload,
      rows: payload.rows.slice(),
      token: payload.continuationToken,
      view: "data",
      els: { count, viewport, sizer, rowsHost, schema, loadMore, notice, dataBtn, schemaBtn },
    };

    count.textContent = countLabel(grid.rows.length);
    dataBtn.classList.add("active");
    dataBtn.addEventListener("click", () => setView(grid, "data"));
    schemaBtn.addEventListener("click", () => setView(grid, "schema"));
    exportBtn.addEventListener("click", () => {
      context.postMessage?.({
        type: "exportCsv",
        outputId: grid.id,
        documentUri: grid.payload.documentUri,
      });
    });
    loadMore.addEventListener("click", () => {
      if (!grid.token) {
        return;
      }
      loadMore.disabled = true;
      context.postMessage?.({
        type: "loadMore",
        outputId: grid.id,
        documentUri: grid.payload.documentUri,
        token: grid.token,
      });
    });
    viewport.addEventListener("scroll", () => raf(() => renderWindow(grid)));

    renderWindow(grid);
    return grid;
  }

  return {
    renderOutputItem(item, element) {
      const grid = build(item, element);
      grids.set(item.id, grid);
    },
    disposeOutputItem(id) {
      grids.delete(id);
    },
  };
}
```

> The `for (const col of grid.payload.columns.length ? ... )` loop in `renderWindow` is dead scaffolding left by an earlier draft — delete it while implementing; it does nothing. (Kept out of the test surface deliberately.)

- [ ] **Step 5: Run to verify it passes, then the whole suite**

Run: `cd editors/vscode && npx vitest run src/renderer/index.test.ts`
Expected: PASS (9 assertions).
Run: `cd editors/vscode && npm test`
Expected: PASS.

- [ ] **Step 6: DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-06 — F3.7: the results-grid renderer

`src/renderer/index.ts`: browser-side `activate(context)` returning `renderOutputItem` / `disposeOutputItem`. Draws a windowed grid (`visibleRange`, fixed 22px rows, translate-based recycling on scroll), a Data/Schema toolbar toggle, a row-count label, an `Export CSV` button (posts `exportCsv`), and a `Load more` button shown only while a `continuationToken` exists (posts `loadMore`, disables until reply). Inbound `rows` append + retoken, `error` shows a notice (and hides `Load more` for the `loadMore` scope), `exportDone` confirms — all routed by `outputId`. Theme entirely via `--vscode-*` vars. New devDependency: `jsdom` (renderer unit tests run under `// @vitest-environment jsdom`).
```

- [ ] **Step 7: Commit**

```bash
git add editors/vscode/src/renderer/index.ts editors/vscode/src/renderer/index.test.ts editors/vscode/package.json editors/vscode/package-lock.json docs/DEVLOG.md
git commit -m "feat(vscode): windowed results-grid renderer with schema view, export, and paging"
```

---

### Task 8: Wire it up — esbuild bundle, package.json contribution, activation

**Files:**
- Modify: `editors/vscode/esbuild.mjs`
- Modify: `editors/vscode/package.json` (`contributes.notebookRenderer`)
- Modify: `editors/vscode/src/extension.ts`
- Modify: `editors/vscode/src/extension.test.ts`
- Modify: `editors/vscode/test/vscode-stub.ts`

**Interfaces:**
- Consumes: `registerRendererHost` from `./notebook/renderer-host`; `FSQUERY_RENDERER_ID` from `./renderer/types`.
- Produces:
  - `esbuild.mjs` builds two outputs: the existing `dist/extension.js` (node/cjs) and a new `media/renderer/index.js` (browser/esm, `target: "es2020"`, no `external`), both honoring `--watch`.
  - `package.json` `contributes.notebookRenderer` = one entry (`id: "fs-query-studio-table"`, `entrypoint: "./media/renderer/index.js"`, `requiresMessaging: "optional"`, `mimeTypes: ["application/x-fsquery-table+json"]`).
  - `extension.ts` `activate` creates `vscode.notebooks.createNotebookRendererMessaging?`… no — uses `vscode.notebooks.createRendererMessaging(FSQUERY_RENDERER_ID)`, passes it plus a `pickSaveFile` closure into `registerRendererHost`, and pushes the returned disposable onto `context.subscriptions`.
  - `test/vscode-stub.ts`: `notebooks.createRendererMessaging(id)` returns a recorder (`{ _id, _posted, onDidReceiveMessage, postMessage }`) and appends `id` to `notebooks._rendererMessagingIds`; `window.showSaveDialog` added (default `undefined`); `Uri.joinPath` and `Uri.parse` return `{ toString, fsPath }`.

- [ ] **Step 1: Extend `editors/vscode/test/vscode-stub.ts`**

Replace the `Uri` line with:

```ts
export const Uri = {
  parse: (s: string) => ({ toString: () => s, fsPath: s }),
  joinPath: (base: { toString(): string }, ...segments: string[]) => {
    const joined = [base.toString().replace(/\/+$/, ""), ...segments].join("/");
    return { toString: () => joined, fsPath: joined };
  },
};
```

Add to the `window` object literal (after `activeNotebookEditor`):

```ts
  showSaveDialog: async (_opts?: unknown) => undefined as { fsPath: string } | undefined,
```

Add to the `notebooks` object (after `createNotebookController`, before `dispose`-less end — i.e. as a sibling method) and to the default export:

```ts
  _rendererMessagingIds: [] as string[],
  createRendererMessaging(id: string) {
    this._rendererMessagingIds.push(id);
    const posted: Array<{ message: unknown; editor: unknown }> = [];
    let listener: ((e: { editor: unknown; message: unknown }) => unknown) | undefined;
    return {
      _id: id,
      _posted: posted,
      onDidReceiveMessage(fn: (e: { editor: unknown; message: unknown }) => unknown) {
        listener = fn;
        return { dispose: () => (listener = undefined) };
      },
      postMessage(message: unknown, editor?: unknown) {
        posted.push({ message, editor });
        return Promise.resolve(true);
      },
      _emit(e: { editor: unknown; message: unknown }) {
        return listener?.(e);
      },
    };
  },
```

Add `Uri` is already exported; ensure the default export still lists `notebooks` (it does).

- [ ] **Step 2: Add the failing assertion to `editors/vscode/src/extension.test.ts`**

Inside `describe("activate — notebook wiring", ...)`, add:

```ts
  it("creates renderer messaging for the results grid", async () => {
    (vscodeStub.notebooks as unknown as { _rendererMessagingIds: string[] })._rendererMessagingIds =
      [];
    await activate(fakeContext());
    expect(
      (vscodeStub.notebooks as unknown as { _rendererMessagingIds: string[] })._rendererMessagingIds,
    ).toContain("fs-query-studio-table");
    await deactivate();
  });
```

- [ ] **Step 3: Run to verify it fails**

Run: `cd editors/vscode && npx vitest run src/extension.test.ts`
Expected: FAIL — `activate` never calls `createRendererMessaging`; the id array stays empty. (May also fail to compile until Step 4 adds the stub method reference — that is fine, it is still red.)

- [ ] **Step 4: Edit `editors/vscode/src/extension.ts`**

Add imports:

```ts
import { registerRendererHost } from "./notebook/renderer-host";
import { FSQUERY_RENDERER_ID } from "./renderer/types";
```

In `activate`, after `const controller = createFsQueryController({...});` and before the `restartCommand`:

```ts
  const rendererMessaging = vscode.notebooks.createRendererMessaging(FSQUERY_RENDERER_ID);
  const rendererHost = registerRendererHost({
    messaging: rendererMessaging,
    getWorker,
    pickSaveFile: async (defaultName, documentUri) => {
      const picked = await vscode.window.showSaveDialog({
        defaultUri: vscode.Uri.joinPath(vscode.Uri.parse(documentUri), "..", defaultName),
        filters: { CSV: ["csv"] },
        saveLabel: "Export",
      });
      return picked?.fsPath;
    },
    log: (line) => outputChannel!.appendLine(line),
  });
```

Add `rendererHost` to the `context.subscriptions.push(...)` list:

```ts
  context.subscriptions.push(
    command,
    notebookSerializer,
    controller,
    rendererHost,
    restartCommand,
    outputChannel,
  );
```

- [ ] **Step 5: Run to verify it passes, then the whole suite**

Run: `cd editors/vscode && npx vitest run src/extension.test.ts`
Expected: PASS.
Run: `cd editors/vscode && npm test`
Expected: PASS.

- [ ] **Step 6: Update `editors/vscode/esbuild.mjs`**

Replace the whole file with:

```js
import * as esbuild from "esbuild";

const watch = process.argv.includes("--watch");

/** @type {import("esbuild").BuildOptions[]} */
const builds = [
  {
    entryPoints: ["src/extension.ts"],
    bundle: true,
    platform: "node",
    format: "cjs",
    target: "node20",
    outfile: "dist/extension.js",
    external: ["vscode"],
    sourcemap: true,
    logLevel: "info",
  },
  {
    // The results-grid renderer runs in the notebook output webview — a
    // browser context. No `vscode` import; nothing Node-only may enter
    // this graph (see the plan's Global Constraints).
    entryPoints: ["src/renderer/index.ts"],
    bundle: true,
    platform: "browser",
    format: "esm",
    target: "es2020",
    outfile: "media/renderer/index.js",
    sourcemap: true,
    logLevel: "info",
  },
];

if (watch) {
  const contexts = await Promise.all(builds.map((options) => esbuild.context(options)));
  await Promise.all(contexts.map((context) => context.watch()));
} else {
  await Promise.all(builds.map((options) => esbuild.build(options)));
}
```

- [ ] **Step 7: Add the `notebookRenderer` contribution to `editors/vscode/package.json`**

Inside `"contributes"`, after `"notebooks"`, add:

```json
    "notebookRenderer": [
      {
        "id": "fs-query-studio-table",
        "displayName": "F# Query Studio Results Grid",
        "entrypoint": "./media/renderer/index.js",
        "requiresMessaging": "optional",
        "mimeTypes": ["application/x-fsquery-table+json"]
      }
    ]
```

- [ ] **Step 8: Build to verify both bundles emit**

Run: `cd editors/vscode && npm run compile`
Expected: `tsc --noEmit` clean; esbuild writes `dist/extension.js` **and** `media/renderer/index.js`. Confirm: `ls media/renderer/index.js`.

If esbuild reports resolving `node:crypto` (or any `node:*`) into the renderer bundle, a renderer file has imported something it must not — fix the import (see Global Constraints) rather than marking it external.

- [ ] **Step 9: Add `media/renderer/` build output to `.gitignore`**

Append to `editors/vscode/.gitignore` (create the entry if the file already ignores `dist/`):

```
media/renderer/index.js
media/renderer/index.js.map
```

- [ ] **Step 10: DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-06 — F3.8: renderer bundle + contribution + activation wiring

`esbuild.mjs` now emits two bundles — the node/cjs extension and a browser/esm `media/renderer/index.js` (both watch-aware). `package.json` contributes `notebookRenderer` `fs-query-studio-table` for `application/x-fsquery-table+json` with `requiresMessaging: "optional"`. `extension.ts` `activate` creates the renderer messaging channel and `registerRendererHost` (with a `showSaveDialog`-backed `pickSaveFile`), disposed via `context.subscriptions`. `media/renderer/index.js` is git-ignored.
```

- [ ] **Step 11: Commit**

```bash
git add editors/vscode/esbuild.mjs editors/vscode/package.json editors/vscode/src/extension.ts editors/vscode/src/extension.test.ts editors/vscode/test/vscode-stub.ts editors/vscode/.gitignore docs/DEVLOG.md
git commit -m "feat(vscode): bundle the renderer, contribute it, and wire the renderer host into activate"
```

---

### Task 9: Extension-host integration spec + phase close-out

**Files:**
- Create: `editors/vscode/test/integration/renderer.test.ts`
- Modify: `docs/DEVLOG.md`
- Modify: `docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md`

**Interfaces:**
- Consumes: the running extension in a real VS Code host (same pattern as `test/integration/notebook.test.ts` from F2).
- Produces: one integration spec that opens an `.fsqnb`, runs a cell producing a truncatable sequence, and asserts a `application/x-fsquery-table+json` output item is present. Skipped when no worker is bundled (mirrors the F2 specs).

- [ ] **Step 1: Read the existing F2 integration spec for the exact helpers**

Run: `cat editors/vscode/test/integration/notebook.test.ts`
Note how it resolves a fixture `.fsqnb`, opens it (`vscode.workspace.openNotebookDocument`), runs cells (`vscode.commands.executeCommand("notebook.cell.execute", ...)` or the controller), reads `cell.outputs`, and how it guards on `FSQS_WORKER_DLL` / a bundled `dist/worker`. Reuse those helpers verbatim.

- [ ] **Step 2: Write `editors/vscode/test/integration/renderer.test.ts`**

```ts
import * as assert from "node:assert";
import * as path from "node:path";
import * as fs from "node:fs";
import * as vscode from "vscode";

const WORKER_DLL = process.env.FSQS_WORKER_DLL;
const hasWorker =
  !!WORKER_DLL ||
  fs.existsSync(
    path.join(__dirname, "..", "..", "..", "dist", "worker", "FSharpQueryStudio.Worker.dll"),
  );

const maybe = hasWorker ? suite : suite.skip;

maybe("F3 renderer — table-json output item", () => {
  test("a truncatable sequence cell produces an application/x-fsquery-table+json item", async function () {
    this.timeout(60000);

    const dir = fs.mkdtempSync(path.join(require("node:os").tmpdir(), "fsqnb-f3-"));
    const file = path.join(dir, "grid.fsqnb");
    fs.writeFileSync(
      file,
      JSON.stringify({
        version: 1,
        cells: [{ kind: "code", language: "fsharp", value: "seq { 1 .. 600 }" }],
        metadata: {},
      }),
    );

    const notebook = await vscode.workspace.openNotebookDocument(vscode.Uri.file(file));
    await vscode.window.showNotebookDocument(notebook);
    await vscode.commands.executeCommand("notebook.execute");

    // Poll for the executed cell output (the controller resolves async).
    let items: readonly vscode.NotebookCellOutputItem[] = [];
    for (let i = 0; i < 40 && items.length === 0; i++) {
      await new Promise((r) => setTimeout(r, 500));
      const outs = notebook.cellAt(0).outputs;
      if (outs.length > 0) {
        items = outs[0].items;
      }
    }

    const mimes = items.map((it) => it.mime);
    assert.ok(
      mimes.includes("application/x-fsquery-table+json"),
      `expected a table-json item, got: ${mimes.join(", ")}`,
    );
    assert.ok(mimes.includes("text/plain"), "expected the text/plain fallback alongside it");
  });
});
```

(If `cat` in Step 1 shows the F2 spec uses `describe`/`it` rather than Mocha TDD `suite`/`test`, match that style instead — `.vscode-test.mjs` sets `ui: "bdd"`, so prefer `describe`/`it` and `describe.skip`.)

- [ ] **Step 3: Compile the integration tests**

Run: `cd editors/vscode && npm run pretest:vscode`
Expected: `tsc -p tsconfig.integration.json` clean; `out/test/integration/renderer.test.js` emitted.

- [ ] **Step 4: Run the extension-host suite (best effort)**

Run: `cd editors/vscode && npm run test:vscode`
Expected: the new spec runs if a worker is bundled/`FSQS_WORKER_DLL` is set, otherwise it is skipped alongside the F2 specs. No failures introduced either way.

- [ ] **Step 5: Full unit suite once more**

Run: `cd editors/vscode && npm test`
Expected: PASS.

- [ ] **Step 6: DEVLOG close-out entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-06 — F3 complete: results-grid renderer

`.fsqnb` Table cells now render as an interactive grid instead of a text block. `execution/evaluate` Table envelopes carry a new `application/x-fsquery-table+json` item (`buildTablePayload`: columns, positional rows, `truncated`, `continuationToken`, `elapsedMs`) plus the F2 `text/plain` grid as a fallback. `src/renderer/index.ts` (its own browser/esm esbuild bundle → `media/renderer/index.js`, contributed as renderer `fs-query-studio-table`) draws a windowed grid — fixed 22px rows, translate-recycled on scroll — with a Data/Schema toggle, a row counter, `Export CSV`, and `Load more` (shown only while a `continuationToken` exists). `notebook/renderer-host.ts` answers the grid's messages over `NotebookRendererMessaging`: `loadMore` → `results/page`, `exportCsv` → save dialog + `results/exportCsv`. Known limits (same as the Avalonia app): paging only for the FSI tail-value path; CSV export only for `Results.table` tables (`results/nothingToExport` otherwise, surfaced as a plain explanatory line). New: 33 F3 unit tests (`jsdom` added for the renderer) + 1 extension-host spec (skipped without a bundled worker). No `src/` F# changes; Avalonia untouched.
```

- [ ] **Step 7: Flip the F3 status in the migration design doc**

In `docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md`, the **Status** paragraph under *Rollout — phased* currently ends:

> F2 Minimal notebook is complete on `main-vscode` (not yet merged) — see the DEVLOG close-out entries (`2026-09-03 — F1 complete`, `2026-09-05 — F2 complete`). F3 Renderer is next.

Change it to:

> F2 Minimal notebook and F3 Renderer are complete on `main-vscode` (not yet merged) — see the DEVLOG close-out entries (`2026-09-03 — F1 complete`, `2026-09-05 — F2 complete`, `2026-09-06 — F3 complete`). F4 Cell intelligence is next.

- [ ] **Step 8: Commit**

```bash
git add editors/vscode/test/integration/renderer.test.ts docs/DEVLOG.md docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md
git commit -m "test(vscode): extension-host spec for the F3 table-json item; close out F3"
```

---

## Self-Review

**1. Spec coverage**

| Spec item (migration design, F3 row + component rows) | Task |
|---|---|
| `NotebookRenderer` for the tabular mime | 1 (mime const), 5 (item emitted), 7 (renderer), 8 (contribution) |
| Virtualized grid | 3 (`visibleRange`), 7 (windowed DOM) |
| Schema tab | 7 |
| CSV export | 6 (`results/exportCsv` bridge), 7 (button), 8 (save dialog) |
| Paging via `results/page` ("Load more" → command → `postMessage` append) | 6 (bridge), 7 (button + append) |
| Isolated esbuild bundle | 8 |
| Theme via VS Code CSS variables | 7 (`--vscode-*` only) |
| Renderer testing (jsdom of the grid) | 7 |
| Extension-host test grows: open `.fsqnb` + run + tabular mime | 9 |
| No worker/`src` changes; Avalonia intact | all tasks (constraint) |

No gaps.

**2. Placeholder scan**

Every code step carries the full source or a precise diff. The one intentional call-out — the dead `for (const col of ...)` loop in Task 7's `renderWindow` — is flagged in prose with an instruction to delete it, and is not referenced by any test. The Task 9 spec notes a style fallback (`suite`/`test` vs `describe`/`it`) to match whatever the F2 spec uses; this is a real ambiguity resolved by reading one file in Step 1, not a placeholder.

**3. Type consistency**

- `TablePayload` fields (`documentUri`, `columns`, `rows`, `truncated`, `continuationToken`, `elapsedMs`) — identical in Task 1 (definition), Task 2 (builder), Task 7 (consumer).
- Message types (`loadMore`/`exportCsv`/`rows`/`error`/`exportDone`, fields `outputId`, `documentUri`, `token`, `nextToken`, `scope`, `path`) — identical across Tasks 1, 6, 7.
- `OutputItemSpec` new member `{ kind: "table-json"; payload: TablePayload }` — added in Task 4, consumed in Task 5 (`toOutputItem`).
- `mapEnvelope(env, opts?: { documentUri?: string })` — signature changed in Task 4, called with `{ documentUri: sessionId }` in Task 5.
- `results/page` params `{ sessionId, continuationToken }` and response `{ rows, isTruncated, continuationToken }` — consistent in Task 6 with `Payloads.fs` (verified against source).
- `results/exportCsv` params `{ documentUri, filePath }` — consistent in Task 6 and Task 8's `pickSaveFile` closure.
- `FSQUERY_TABLE_MIME` / `FSQUERY_RENDERER_ID` — defined Task 1, used Tasks 5, 8, and the package.json contribution (Task 8) and integration assertion (Task 9).
- Stub additions (`NotebookCellOutputItem.json`, `createRendererMessaging`, `showSaveDialog`, `Uri.joinPath`) — added in the task that first needs them (5, 8), never referenced earlier.

Consistent.
