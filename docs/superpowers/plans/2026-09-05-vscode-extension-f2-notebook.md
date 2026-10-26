# VS Code Extension — F2 Minimal Notebook Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Open, edit, save, and run `.fsqnb` notebooks in VS Code: a `NotebookSerializer` for the file format, a `NotebookController` ("kernel") that runs each code cell through the existing worker's `execution/evaluate` in Persistent mode, cell output (stdout/stderr + value) drawn from the response envelope, tabular results rendered as a plain-text grid, plus interrupt and restart-kernel.

**Architecture:** New TypeScript modules under `editors/vscode/src/notebook/` and one under `src/worker/`. `fsqnb.ts` is a pure JSON codec; `serializer.ts` adapts it to `vscode.NotebookSerializer`. `result.ts` is a pure `ResultEnvelope` → output-item-spec mapper. `worker/session.ts` owns a single window-lifetime `WorkerClient` behind a lazy singleton, gated by a `worker/health` version check. `controller.ts` builds the `NotebookController`, serializes the cell queue (the worker runs one evaluation at a time), and maps responses to cell outputs. `extension.ts` and `package.json` register all of it and add a restart-kernel command. No `src/` F# project changes; the Avalonia app is untouched.

**Tech Stack:** TypeScript 5, Node 24, `esbuild` (bundle), `vitest` (unit, `vscode` aliased to `test/vscode-stub.ts`), `@vscode/test-cli` + `@vscode/test-electron` (extension-host integration), `@types/vscode` `^1.90`.

**Spec:** `docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md` (rollout row **F2 Minimal notebook**; sections *Architecture → Extension components*, *Data flow → Run a cell / Run All / Cancel / Restart Kernel*, *`.fsqnb` format*, *Error handling and lifecycle*, *Testing*).

## Global Constraints

- **.NET floor:** worker requires .NET **10** on `PATH`. Reuse `resolveDotnet` from F1; do not re-implement the check.
- **Node engine:** development on Node 24; `package.json` `engines.node` = `">=20"` (unchanged).
- **VS Code engine:** `package.json` `engines.vscode` = `"^1.90.0"` (unchanged).
- **Extension identity (placeholder until F6):** `name` = `fs-query-studio`, `publisher` = `fs-query-studio`, `displayName` = `F# Query Studio`.
- **Notebook type id:** `fsquery-notebook`. **File glob:** `*.fsqnb`. **Controller id:** `fs-query-studio-kernel`. **Restart command id:** `fs-query-studio.restartKernel`.
- **Activation:** add `"onNotebook:fsquery-notebook"` to `activationEvents`; keep `"onCommand:fs-query-studio.checkWorker"`. No `onStartupFinished`.
- **Worker frames are camelCase on the wire** (`{ id, method, params }` / `{ id, result?, error? }`, error `{ code, message }`). The worker (FSharp.SystemTextJson) writes an absent `result`/`error` as an explicit JSON `null` — treat `null` and `undefined` the same.
- **F# discriminated unions on the wire** use FSharp.SystemTextJson's default *AdjacentTag* encoding with fieldless cases **not** unwrapped: a union case is `{ "Case": "<Name>" }`. Verified in `tests/FSharpQueryStudio.Integration.Tests/Tests.fs:61` (`"mode":{"Case":"Isolated"}`). This applies to `SessionMode` (sent), `ResultKind` and `OutputStream` (received).
- **`execution/evaluate` request payload** (`EvaluateParams`, `src/FSharpQueryStudio.Protocol/Payloads.fs:26`): `{ source: string, sessionId: string | null, mode: {Case:"Persistent"|"Isolated"}, documentUri: string }`. `Persistent` + a `sessionId` reuses/creates the worker-side FSI session keyed by `sessionId`.
- **`execution/evaluate` response payload** (`ResultEnvelope`, `src/FSharpQueryStudio.Protocol/Results.fs:24`): `{ kind: {Case:...}, displayText: string | null, schema: {name,displayType}[], rows: unknown[][], isTruncated: boolean, continuationToken: string | null, elapsedMs: number, output: {stream:{Case:"Stdout"|"Stderr"}, text:string}[] }`. `ResultKind` cases: `Scalar | Text | Table | Object | Error | NoValue`. **One response frame per evaluation — there is no incremental/streamed output frame in the protocol.**
- **`session/reset` request payload** (`SessionResetParams`): `{ sessionId: string }`. No-op, never an error, when nothing is tracked.
- **`execution/cancel` request payload:** `{}` (empty). Already wired as `WorkerClient.cancel(requestId, gracePeriodMs)` in F1.
- **`sessionId` value:** the notebook's `uri.toString()`. `documentUri` uses the same value. One persistent FSI session per open notebook.
- **TDD:** every task writes the failing test first. **Commit after every task.** Add a one-line `docs/DEVLOG.md` entry as the second-to-last step of every task.
- All new source lives under `editors/vscode/`. Do not modify any `src/` F# project.
- **Run the whole unit suite** (`npm test` in `editors/vscode`) at the end of each task, not just the new file — the shared `test/vscode-stub.ts` is edited by several tasks and a regression there breaks earlier tests silently.

---

### Task 1: Protocol types for evaluate + result payloads and union-tag helpers

**Files:**
- Modify: `editors/vscode/src/protocol.ts` (append)
- Modify: `editors/vscode/src/protocol.test.ts` (append)

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `interface UnionTag<T extends string = string> { Case: T }`
  - `function makeUnionTag<T extends string>(name: T): UnionTag<T>`
  - `function readUnionTag(value: unknown): string | undefined` — returns `value.Case` when `value` is an object with a string `Case`, else `undefined`.
  - `type SessionModeName = "Isolated" | "Persistent"`
  - `const SESSION_MODE_PERSISTENT: UnionTag<"Persistent">` = `{ Case: "Persistent" }`
  - `interface EvaluateParams { source: string; sessionId: string | null; mode: UnionTag<SessionModeName>; documentUri: string }`
  - `interface SessionResetParams { sessionId: string }`
  - `type ResultKindName = "Scalar" | "Text" | "Table" | "Object" | "Error" | "NoValue"`
  - `type OutputStreamName = "Stdout" | "Stderr"`
  - `interface Column { name: string; displayType: string }`
  - `interface OutputLine { stream: UnionTag<OutputStreamName>; text: string }`
  - `interface ResultEnvelope { kind: UnionTag<ResultKindName>; displayText: string | null; schema: Column[]; rows: unknown[][]; isTruncated: boolean; continuationToken: string | null; elapsedMs: number; output: OutputLine[] }`

- [ ] **Step 1: Append the failing tests to `editors/vscode/src/protocol.test.ts`**

```ts
import {
  readUnionTag,
  makeUnionTag,
  SESSION_MODE_PERSISTENT,
} from "./protocol";

describe("union tag helpers", () => {
  it("reads a Case tag from a union-shaped value", () => {
    expect(readUnionTag({ Case: "Table" })).toBe("Table");
  });

  it("returns undefined for values that are not union-shaped", () => {
    expect(readUnionTag(null)).toBeUndefined();
    expect(readUnionTag(undefined)).toBeUndefined();
    expect(readUnionTag({})).toBeUndefined();
    expect(readUnionTag("Table")).toBeUndefined();
    expect(readUnionTag({ Case: 3 })).toBeUndefined();
  });

  it("round-trips through make/read", () => {
    expect(readUnionTag(makeUnionTag("Persistent"))).toBe("Persistent");
  });

  it("SESSION_MODE_PERSISTENT serializes to the worker's adjacent-tag shape", () => {
    expect(JSON.stringify(SESSION_MODE_PERSISTENT)).toBe('{"Case":"Persistent"}');
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd editors/vscode && npx vitest run src/protocol.test.ts`
Expected: FAIL — `readUnionTag` / `makeUnionTag` / `SESSION_MODE_PERSISTENT` are not exported.

- [ ] **Step 3: Append to `editors/vscode/src/protocol.ts`**

```ts
// --- F2: discriminated-union wire encoding ---

/**
 * An F# discriminated-union case as it appears on the wire.
 * FSharp.SystemTextJson's default (AdjacentTag, fieldless cases not
 * unwrapped) serializes a union case as `{ "Case": "<Name>" }`.
 * Verified against tests/FSharpQueryStudio.Integration.Tests/Tests.fs.
 */
export interface UnionTag<T extends string = string> {
  Case: T;
}

export function makeUnionTag<T extends string>(name: T): UnionTag<T> {
  return { Case: name };
}

export function readUnionTag(value: unknown): string | undefined {
  if (value !== null && typeof value === "object" && "Case" in value) {
    const tag = (value as { Case: unknown }).Case;
    return typeof tag === "string" ? tag : undefined;
  }
  return undefined;
}

// --- F2: execution/evaluate + session/reset request payloads ---

export type SessionModeName = "Isolated" | "Persistent";

export const SESSION_MODE_PERSISTENT: UnionTag<"Persistent"> = { Case: "Persistent" };

export interface EvaluateParams {
  source: string;
  sessionId: string | null;
  mode: UnionTag<SessionModeName>;
  documentUri: string;
}

export interface SessionResetParams {
  sessionId: string;
}

// --- F2: execution/evaluate response payload (ResultEnvelope) ---

export type ResultKindName = "Scalar" | "Text" | "Table" | "Object" | "Error" | "NoValue";

export type OutputStreamName = "Stdout" | "Stderr";

export interface Column {
  name: string;
  displayType: string;
}

export interface OutputLine {
  stream: UnionTag<OutputStreamName>;
  text: string;
}

/**
 * Mirror of `FSharpQueryStudio.Protocol.ResultEnvelope`. `displayText` and
 * `continuationToken` are F# `string option`, which FSharp.SystemTextJson
 * unwraps to the bare string or JSON `null`.
 */
export interface ResultEnvelope {
  kind: UnionTag<ResultKindName>;
  displayText: string | null;
  schema: Column[];
  rows: unknown[][];
  isTruncated: boolean;
  continuationToken: string | null;
  elapsedMs: number;
  output: OutputLine[];
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd editors/vscode && npx vitest run src/protocol.test.ts`
Expected: PASS (existing protocol tests + 4 new assertions).

- [ ] **Step 5: DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-05 — F2.1: protocol types for execution/evaluate + ResultEnvelope

`protocol.ts` gains the F2 wire types: `EvaluateParams`, `SessionResetParams`, `ResultEnvelope` (+ `Column`, `OutputLine`), and `UnionTag` / `readUnionTag` / `makeUnionTag` / `SESSION_MODE_PERSISTENT` for FSharp.SystemTextJson's `{"Case":"..."}` adjacent-tag encoding (fieldless F# union cases are not unwrapped — confirmed against the worker integration tests).
```

- [ ] **Step 6: Commit**

```bash
git add editors/vscode/src/protocol.ts editors/vscode/src/protocol.test.ts docs/DEVLOG.md
git commit -m "feat(vscode): protocol types for execution/evaluate and ResultEnvelope"
```

---

### Task 2: `.fsqnb` JSON codec (pure, no VS Code)

**Files:**
- Create: `editors/vscode/src/notebook/fsqnb.ts`
- Test: `editors/vscode/src/notebook/fsqnb.test.ts`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `type FsqnbCellKind = "code" | "markdown"`
  - `interface FsqnbCell { kind: FsqnbCellKind; language: string; value: string }`
  - `interface FsqnbDocument { version: 1; cells: FsqnbCell[]; metadata: Record<string, unknown> }`
  - `const FSQNB_VERSION = 1`
  - `class FsqnbError extends Error` — `readonly userMessage: string`
  - `class FsqnbVersionError extends FsqnbError` — `readonly found: unknown`
  - `function deserializeFsqnb(text: string): FsqnbDocument` — empty/whitespace input → empty document. Non-JSON or non-object root → `FsqnbError`. `version !== 1` → `FsqnbVersionError`. Each cell: `kind` defaults to `"code"` unless exactly `"markdown"`; `value` defaults to `""`; `language` is `"markdown"` for markdown cells, else the string `language` or `"fsharp"`.
  - `function serializeFsqnb(doc: FsqnbDocument): string` — pretty JSON (2-space) + trailing `"\n"`. Markdown cells serialize as `{ kind, value }` (no `language`); code cells as `{ kind, language, value }`. Always writes `version: 1` and `metadata` (defaulting to `{}`).

- [ ] **Step 1: Write `editors/vscode/src/notebook/fsqnb.test.ts`**

```ts
import { describe, it, expect } from "vitest";
import {
  deserializeFsqnb,
  serializeFsqnb,
  FsqnbError,
  FsqnbVersionError,
  type FsqnbDocument,
} from "./fsqnb";

describe("deserializeFsqnb", () => {
  it("parses a minimal document", () => {
    const doc = deserializeFsqnb(
      JSON.stringify({
        version: 1,
        cells: [{ kind: "code", language: "fsharp", value: "1 + 1" }],
        metadata: {},
      }),
    );
    expect(doc.cells).toEqual([{ kind: "code", language: "fsharp", value: "1 + 1" }]);
    expect(doc.metadata).toEqual({});
  });

  it("defaults a missing cell language to fsharp", () => {
    const doc = deserializeFsqnb(
      JSON.stringify({ version: 1, cells: [{ kind: "code", value: "x" }] }),
    );
    expect(doc.cells[0].language).toBe("fsharp");
  });

  it("normalises a markdown cell", () => {
    const doc = deserializeFsqnb(
      JSON.stringify({ version: 1, cells: [{ kind: "markdown", value: "# Hi" }] }),
    );
    expect(doc.cells[0]).toEqual({ kind: "markdown", language: "markdown", value: "# Hi" });
  });

  it("treats empty / whitespace input as an empty notebook", () => {
    expect(deserializeFsqnb("")).toEqual({ version: 1, cells: [], metadata: {} });
    expect(deserializeFsqnb("   \n ")).toEqual({ version: 1, cells: [], metadata: {} });
  });

  it("throws FsqnbVersionError for a future version, and carries the found value", () => {
    const err = (() => {
      try {
        deserializeFsqnb(JSON.stringify({ version: 2, cells: [] }));
      } catch (e) {
        return e as FsqnbVersionError;
      }
    })();
    expect(err).toBeInstanceOf(FsqnbVersionError);
    expect(err!.found).toBe(2);
    expect(err!.userMessage).toMatch(/version 1/);
  });

  it("throws FsqnbError for non-JSON", () => {
    expect(() => deserializeFsqnb("not json")).toThrow(FsqnbError);
  });

  it("throws FsqnbError when the root is not an object", () => {
    expect(() => deserializeFsqnb("[]")).toThrow(FsqnbError);
  });
});

describe("serializeFsqnb", () => {
  it("is stable across decode → encode → decode → encode", () => {
    const src: FsqnbDocument = {
      version: 1,
      cells: [
        { kind: "markdown", language: "markdown", value: "# Title" },
        { kind: "code", language: "fsharp", value: 'Connections.sql "db" "select 1"' },
      ],
      metadata: {},
    };
    const once = serializeFsqnb(src);
    const twice = serializeFsqnb(deserializeFsqnb(once));
    expect(twice).toBe(once);
  });

  it("drops language from a markdown cell and keeps it on a code cell", () => {
    const text = serializeFsqnb({
      version: 1,
      cells: [
        { kind: "markdown", language: "markdown", value: "m" },
        { kind: "code", language: "fsharp", value: "c" },
      ],
      metadata: {},
    });
    const parsed = JSON.parse(text);
    expect(parsed.cells[0]).toEqual({ kind: "markdown", value: "m" });
    expect(parsed.cells[1]).toEqual({ kind: "code", language: "fsharp", value: "c" });
  });

  it("ends with a trailing newline", () => {
    expect(serializeFsqnb({ version: 1, cells: [], metadata: {} }).endsWith("\n")).toBe(true);
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd editors/vscode && npx vitest run src/notebook/fsqnb.test.ts`
Expected: FAIL — `Cannot find module './fsqnb'`.

- [ ] **Step 3: Write `editors/vscode/src/notebook/fsqnb.ts`**

```ts
/**
 * In-memory shape of a `.fsqnb` file, independent of any VS Code type.
 * The format is a purpose-built JSON schema under this project's control
 * (see the migration design doc, "`.fsqnb` format"). Outputs are never
 * part of the file.
 */
export type FsqnbCellKind = "code" | "markdown";

export interface FsqnbCell {
  kind: FsqnbCellKind;
  /** `"markdown"` for markdown cells; the code cell's language otherwise. */
  language: string;
  value: string;
}

export interface FsqnbDocument {
  version: 1;
  cells: FsqnbCell[];
  metadata: Record<string, unknown>;
}

export const FSQNB_VERSION = 1 as const;

export class FsqnbError extends Error {
  readonly userMessage: string;
  constructor(message: string, userMessage?: string) {
    super(message);
    this.userMessage = userMessage ?? message;
  }
}

export class FsqnbVersionError extends FsqnbError {
  constructor(readonly found: unknown) {
    super(
      `unsupported .fsqnb version: ${JSON.stringify(found)}`,
      `This .fsqnb file declares version ${JSON.stringify(found)}, but this ` +
        `build of F# Query Studio only understands version ${FSQNB_VERSION}.`,
    );
  }
}

function emptyDocument(): FsqnbDocument {
  return { version: FSQNB_VERSION, cells: [], metadata: {} };
}

export function deserializeFsqnb(text: string): FsqnbDocument {
  const trimmed = text.trim();
  if (trimmed.length === 0) {
    return emptyDocument();
  }

  let raw: unknown;
  try {
    raw = JSON.parse(trimmed);
  } catch (cause) {
    throw new FsqnbError(`.fsqnb is not valid JSON: ${(cause as Error).message}`);
  }
  if (typeof raw !== "object" || raw === null || Array.isArray(raw)) {
    throw new FsqnbError(".fsqnb root must be a JSON object");
  }

  const obj = raw as Record<string, unknown>;
  if (obj.version !== FSQNB_VERSION) {
    throw new FsqnbVersionError(obj.version);
  }

  const cellsIn = Array.isArray(obj.cells) ? obj.cells : [];
  const cells: FsqnbCell[] = cellsIn.map((entry) => {
    const cell = (entry ?? {}) as Record<string, unknown>;
    const kind: FsqnbCellKind = cell.kind === "markdown" ? "markdown" : "code";
    const value = typeof cell.value === "string" ? cell.value : "";
    const language =
      kind === "markdown"
        ? "markdown"
        : typeof cell.language === "string" && cell.language.length > 0
          ? cell.language
          : "fsharp";
    return { kind, language, value };
  });

  const metadata =
    typeof obj.metadata === "object" && obj.metadata !== null && !Array.isArray(obj.metadata)
      ? (obj.metadata as Record<string, unknown>)
      : {};

  return { version: FSQNB_VERSION, cells, metadata };
}

export function serializeFsqnb(doc: FsqnbDocument): string {
  const out = {
    version: FSQNB_VERSION,
    cells: doc.cells.map((c) =>
      c.kind === "markdown"
        ? { kind: "markdown", value: c.value }
        : { kind: "code", language: c.language || "fsharp", value: c.value },
    ),
    metadata: doc.metadata ?? {},
  };
  return JSON.stringify(out, null, 2) + "\n";
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd editors/vscode && npx vitest run src/notebook/fsqnb.test.ts`
Expected: PASS (13 assertions across the two describes).

- [ ] **Step 5: DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-05 — F2.2: `.fsqnb` JSON codec

`notebook/fsqnb.ts`: pure `deserializeFsqnb` / `serializeFsqnb` over an in-memory `FsqnbDocument` (version 1, code + markdown cells, `metadata`). Empty input → empty notebook; unknown `version` → `FsqnbVersionError` (hard failure, no best-effort); non-JSON / non-object root → `FsqnbError`. Serialize is deterministic (2-space, trailing newline) and round-trip-stable. No VS Code dependency.
```

- [ ] **Step 6: Commit**

```bash
git add editors/vscode/src/notebook/fsqnb.ts editors/vscode/src/notebook/fsqnb.test.ts docs/DEVLOG.md
git commit -m "feat(vscode): .fsqnb JSON codec"
```

---

### Task 3: `NotebookSerializer` adapter + `vscode` stub notebook surface

**Files:**
- Create: `editors/vscode/src/notebook/serializer.ts`
- Test: `editors/vscode/src/notebook/serializer.test.ts`
- Modify: `editors/vscode/test/vscode-stub.ts`

**Interfaces:**
- Consumes: `deserializeFsqnb`, `serializeFsqnb`, `FsqnbCell`, `FsqnbDocument` from `./fsqnb`.
- Produces:
  - `const FSQNB_NOTEBOOK_TYPE = "fsquery-notebook"`
  - `class FsqnbSerializer implements vscode.NotebookSerializer` with `deserializeNotebook(content: Uint8Array): vscode.NotebookData` and `serializeNotebook(data: vscode.NotebookData): Uint8Array`.
  - `test/vscode-stub.ts` additionally exports `enum NotebookCellKind { Markup = 1, Code = 2 }`, `class NotebookCellData { constructor(kind, value, languageId) }`, `class NotebookData { constructor(cells) }` — added to the default export too.

- [ ] **Step 1: Add the notebook surface to `editors/vscode/test/vscode-stub.ts`**

Append before the `export default` line:
```ts
// --- notebook document model (F2) ---
export enum NotebookCellKind {
  Markup = 1,
  Code = 2,
}

export class NotebookCellData {
  metadata?: Record<string, unknown>;
  constructor(
    public kind: NotebookCellKind,
    public value: string,
    public languageId: string,
  ) {}
}

export class NotebookData {
  metadata?: Record<string, unknown>;
  constructor(public cells: NotebookCellData[]) {}
}
```

Then extend the default export object to include the new names:
```ts
export default {
  window,
  commands,
  env,
  Uri,
  NotebookCellKind,
  NotebookCellData,
  NotebookData,
};
```

- [ ] **Step 2: Write `editors/vscode/src/notebook/serializer.test.ts`**

```ts
import { describe, it, expect } from "vitest";
import { FsqnbSerializer } from "./serializer";

const enc = (s: string) => new TextEncoder().encode(s);
const dec = (u: Uint8Array) => new TextDecoder().decode(u);

describe("FsqnbSerializer.deserializeNotebook", () => {
  it("maps code and markdown cells to NotebookCellData", () => {
    const data = new FsqnbSerializer().deserializeNotebook(
      enc(
        JSON.stringify({
          version: 1,
          cells: [
            { kind: "markdown", value: "# H" },
            { kind: "code", language: "fsharp", value: "1 + 1" },
          ],
        }),
      ),
    );
    expect(data.cells.map((c) => c.kind)).toEqual([1, 2]); // Markup, Code
    expect(data.cells[1].value).toBe("1 + 1");
    expect(data.cells[1].languageId).toBe("fsharp");
    expect(data.cells[0].languageId).toBe("markdown");
  });

  it("accepts an empty file", () => {
    const data = new FsqnbSerializer().deserializeNotebook(enc(""));
    expect(data.cells).toEqual([]);
  });
});

describe("FsqnbSerializer.serializeNotebook", () => {
  it("round-trips content through a deserialize/serialize/deserialize cycle", () => {
    const s = new FsqnbSerializer();
    const original = enc(
      JSON.stringify({
        version: 1,
        cells: [{ kind: "code", language: "fsharp", value: "let x = 1" }],
        metadata: {},
      }),
    );
    const back = s.serializeNotebook(s.deserializeNotebook(original));
    expect(dec(back)).toContain('"let x = 1"');
    const reparsed = s.deserializeNotebook(back);
    expect(reparsed.cells[0].value).toBe("let x = 1");
    expect(reparsed.cells[0].kind).toBe(2);
  });

  it("writes a version-1 envelope", () => {
    const s = new FsqnbSerializer();
    const bytes = s.serializeNotebook(s.deserializeNotebook(enc("")));
    expect(JSON.parse(dec(bytes)).version).toBe(1);
  });
});
```

- [ ] **Step 3: Run to verify it fails**

Run: `cd editors/vscode && npx vitest run src/notebook/serializer.test.ts`
Expected: FAIL — `Cannot find module './serializer'`.

- [ ] **Step 4: Write `editors/vscode/src/notebook/serializer.ts`**

```ts
import * as vscode from "vscode";
import {
  deserializeFsqnb,
  serializeFsqnb,
  type FsqnbCell,
  type FsqnbDocument,
} from "./fsqnb";

export const FSQNB_NOTEBOOK_TYPE = "fsquery-notebook";

/**
 * Adapts the pure `.fsqnb` codec to `vscode.NotebookSerializer`. Never
 * reads or writes cell outputs — the format does not carry them.
 */
export class FsqnbSerializer implements vscode.NotebookSerializer {
  deserializeNotebook(content: Uint8Array): vscode.NotebookData {
    const doc = deserializeFsqnb(new TextDecoder().decode(content));
    const cells = doc.cells.map((c) => {
      const kind =
        c.kind === "markdown" ? vscode.NotebookCellKind.Markup : vscode.NotebookCellKind.Code;
      const languageId = c.kind === "markdown" ? "markdown" : c.language || "fsharp";
      return new vscode.NotebookCellData(kind, c.value, languageId);
    });
    return new vscode.NotebookData(cells);
  }

  serializeNotebook(data: vscode.NotebookData): Uint8Array {
    const doc: FsqnbDocument = {
      version: 1,
      cells: data.cells.map<FsqnbCell>((c) =>
        c.kind === vscode.NotebookCellKind.Markup
          ? { kind: "markdown", language: "markdown", value: c.value }
          : { kind: "code", language: c.languageId || "fsharp", value: c.value },
      ),
      metadata: {},
    };
    return new TextEncoder().encode(serializeFsqnb(doc));
  }
}
```

- [ ] **Step 5: Run to verify it passes, then the whole suite**

Run: `cd editors/vscode && npx vitest run src/notebook/serializer.test.ts`
Expected: PASS (5 assertions).
Run: `cd editors/vscode && npm test`
Expected: PASS — all prior unit tests still green (stub change is additive).

- [ ] **Step 6: DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-05 — F2.3: NotebookSerializer adapter for `.fsqnb`

`notebook/serializer.ts`: `FsqnbSerializer` wraps the codec into `vscode.NotebookSerializer` (`Uint8Array` ↔ `NotebookData`, code → `Code` cell / markdown → `Markup` cell, language defaulting to `fsharp`). `FSQNB_NOTEBOOK_TYPE = "fsquery-notebook"`. `test/vscode-stub.ts` gains `NotebookCellKind` / `NotebookCellData` / `NotebookData`.
```

- [ ] **Step 7: Commit**

```bash
git add editors/vscode/src/notebook/serializer.ts editors/vscode/src/notebook/serializer.test.ts editors/vscode/test/vscode-stub.ts docs/DEVLOG.md
git commit -m "feat(vscode): NotebookSerializer adapter for .fsqnb"
```

---

### Task 4: `ResultEnvelope` → output-item-spec mapper (pure)

**Files:**
- Create: `editors/vscode/src/notebook/result.ts`
- Test: `editors/vscode/src/notebook/result.test.ts`

**Interfaces:**
- Consumes: `readUnionTag`, `ResultEnvelope`, `Column` from `../protocol`.
- Produces:
  - `type OutputItemSpec = { kind: "stdout"; text: string } | { kind: "stderr"; text: string } | { kind: "text"; text: string } | { kind: "error"; name: string; message: string }`
  - `function formatTextTable(schema: Column[], rows: unknown[][], elapsedMs: number, isTruncated: boolean): string` — aligned monospace grid: header row, a rule row of `─`, one line per row, a blank line, then `(N row[s] · Mms)`, then (only when `isTruncated`) `(truncated — full grid and paging arrive in F3)`. Column width is `min(60, max(header, widest cell))`; a cell longer than its width is cut to `width-1` + `…`. Non-string cells: numbers/bigints/booleans via `String(...)`, `null`/`undefined` → `""`, anything else via `JSON.stringify`.
  - `function mapEnvelope(env: ResultEnvelope): OutputItemSpec[]` — first one spec per `env.output` line (`Stderr` tag → `stderr`, else `stdout`), then a value spec by `readUnionTag(env.kind)`: `Error` → an `error` spec (`name: "FSharpEvaluationError"`, `message: env.displayText ?? "Evaluation failed."`); `Table` → a `text` spec from `formatTextTable`; `NoValue` → a `text` spec only if `displayText` is a non-empty string; anything else (`Scalar`/`Text`/`Object`/unknown) → a `text` spec of `env.displayText ?? ""`.

- [ ] **Step 1: Write `editors/vscode/src/notebook/result.test.ts`**

```ts
import { describe, it, expect } from "vitest";
import { formatTextTable, mapEnvelope, type OutputItemSpec } from "./result";
import type { ResultEnvelope } from "../protocol";

function envelope(overrides: Partial<ResultEnvelope>): ResultEnvelope {
  return {
    kind: { Case: "NoValue" },
    displayText: null,
    schema: [],
    rows: [],
    isTruncated: false,
    continuationToken: null,
    elapsedMs: 3,
    output: [],
    ...overrides,
  };
}

describe("formatTextTable", () => {
  it("aligns headers and rows and reports the row count and elapsed time", () => {
    const text = formatTextTable(
      [
        { name: "id", displayType: "int" },
        { name: "name", displayType: "string" },
      ],
      [
        [1, "ada"],
        [2, "grace"],
      ],
      12,
      false,
    );
    const lines = text.split("\n");
    expect(lines[0]).toMatch(/^id\s+name\s*$/);
    expect(lines[2]).toMatch(/^1\s+ada\s*$/);
    expect(text).toContain("(2 rows · 12ms)");
    expect(text).not.toContain("truncated");
  });

  it("adds a truncation note when isTruncated", () => {
    const text = formatTextTable([{ name: "x", displayType: "int" }], [[1]], 4, true);
    expect(text).toContain("truncated");
    expect(text).toContain("F3");
  });

  it("stringifies non-primitive cells and blanks nulls", () => {
    const text = formatTextTable(
      [{ name: "v", displayType: "obj" }],
      [[{ a: 1 }], [null]],
      1,
      false,
    );
    expect(text).toContain('{"a":1}');
  });

  it("uses '1 row' (singular) for a single row", () => {
    const text = formatTextTable([{ name: "x", displayType: "int" }], [[1]], 1, false);
    expect(text).toContain("(1 row · 1ms)");
  });
});

describe("mapEnvelope", () => {
  it("emits stdout/stderr specs in order, before the value", () => {
    const specs = mapEnvelope(
      envelope({
        kind: { Case: "Scalar" },
        displayText: "42",
        output: [
          { stream: { Case: "Stdout" }, text: "working...\n" },
          { stream: { Case: "Stderr" }, text: "a warning\n" },
        ],
      }),
    );
    expect(specs.map((s) => s.kind)).toEqual(["stdout", "stderr", "text"]);
    expect((specs[2] as Extract<OutputItemSpec, { kind: "text" }>).text).toBe("42");
  });

  it("maps an Error envelope to an error spec using displayText", () => {
    const specs = mapEnvelope(envelope({ kind: { Case: "Error" }, displayText: "boom at line 2" }));
    expect(specs).toEqual([
      { kind: "error", name: "FSharpEvaluationError", message: "boom at line 2" },
    ]);
  });

  it("maps an Error envelope with no displayText to a default message", () => {
    const specs = mapEnvelope(envelope({ kind: { Case: "Error" }, displayText: null }));
    expect((specs[0] as Extract<OutputItemSpec, { kind: "error" }>).message).toBe(
      "Evaluation failed.",
    );
  });

  it("renders a Table envelope as a text grid", () => {
    const specs = mapEnvelope(
      envelope({
        kind: { Case: "Table" },
        schema: [{ name: "n", displayType: "int" }],
        rows: [[1], [2], [3]],
        elapsedMs: 7,
      }),
    );
    expect(specs).toHaveLength(1);
    expect(specs[0].kind).toBe("text");
    expect((specs[0] as Extract<OutputItemSpec, { kind: "text" }>).text).toContain("(3 rows · 7ms)");
  });

  it("emits nothing for a NoValue envelope with no displayText", () => {
    expect(mapEnvelope(envelope({ kind: { Case: "NoValue" }, displayText: null }))).toEqual([]);
  });

  it("emits the displayText for a NoValue envelope that has one", () => {
    const specs = mapEnvelope(envelope({ kind: { Case: "NoValue" }, displayText: "(done)" }));
    expect(specs).toEqual([{ kind: "text", text: "(done)" }]);
  });

  it("falls back to a text spec for an unrecognised kind", () => {
    const specs = mapEnvelope(envelope({ kind: { Case: "Object" }, displayText: "{ X = 1 }" }));
    expect(specs).toEqual([{ kind: "text", text: "{ X = 1 }" }]);
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd editors/vscode && npx vitest run src/notebook/result.test.ts`
Expected: FAIL — `Cannot find module './result'`.

- [ ] **Step 3: Write `editors/vscode/src/notebook/result.ts`**

```ts
import { readUnionTag, type Column, type ResultEnvelope } from "../protocol";

/**
 * A flat, VS Code-independent description of one cell output item. The
 * controller turns each spec into a `vscode.NotebookCellOutputItem`;
 * keeping this pure lets the mapping be unit-tested without the VS Code
 * runtime.
 */
export type OutputItemSpec =
  | { kind: "stdout"; text: string }
  | { kind: "stderr"; text: string }
  | { kind: "text"; text: string }
  | { kind: "error"; name: string; message: string };

const MAX_CELL_WIDTH = 60;

function renderCell(value: unknown): string {
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

function clip(text: string, width: number): string {
  return text.length > width ? text.slice(0, Math.max(0, width - 1)) + "…" : text;
}

export function formatTextTable(
  schema: Column[],
  rows: unknown[][],
  elapsedMs: number,
  isTruncated: boolean,
): string {
  const headers = schema.map((c) => c.name);
  const body = rows.map((row) => row.map(renderCell));
  const widths = headers.map((header, i) =>
    Math.min(
      MAX_CELL_WIDTH,
      Math.max(header.length, ...body.map((row) => (row[i] ?? "").length), 0),
    ),
  );
  const line = (cells: string[]) =>
    cells.map((cell, i) => clip(cell, widths[i]).padEnd(widths[i])).join("  ").replace(/\s+$/, "");

  const out: string[] = [];
  out.push(line(headers));
  out.push(widths.map((w) => "─".repeat(w)).join("  "));
  for (const row of body) {
    out.push(line(row));
  }
  out.push("");
  out.push(`(${rows.length} row${rows.length === 1 ? "" : "s"} · ${elapsedMs}ms)`);
  if (isTruncated) {
    out.push("(truncated — full grid and paging arrive in F3)");
  }
  return out.join("\n");
}

export function mapEnvelope(env: ResultEnvelope): OutputItemSpec[] {
  const specs: OutputItemSpec[] = [];

  for (const line of env.output ?? []) {
    const stream = readUnionTag(line.stream) === "Stderr" ? "stderr" : "stdout";
    specs.push({ kind: stream, text: line.text });
  }

  switch (readUnionTag(env.kind)) {
    case "Error":
      specs.push({
        kind: "error",
        name: "FSharpEvaluationError",
        message: env.displayText ?? "Evaluation failed.",
      });
      break;
    case "Table":
      specs.push({
        kind: "text",
        text: formatTextTable(env.schema ?? [], env.rows ?? [], env.elapsedMs, env.isTruncated),
      });
      break;
    case "NoValue":
      if (typeof env.displayText === "string" && env.displayText.length > 0) {
        specs.push({ kind: "text", text: env.displayText });
      }
      break;
    default:
      specs.push({ kind: "text", text: env.displayText ?? "" });
      break;
  }

  return specs;
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd editors/vscode && npx vitest run src/notebook/result.test.ts`
Expected: PASS (13 assertions).

- [ ] **Step 5: DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-05 — F2.4: ResultEnvelope → cell-output mapper

`notebook/result.ts`: pure `mapEnvelope` turns a worker `ResultEnvelope` into ordered `OutputItemSpec`s — stdout/stderr lines first, then the value by `kind`: `Error` → an error item (uses `displayText`), `Table` → `formatTextTable` (aligned monospace grid + `(N rows · Mms)` + an F3 truncation note), `NoValue` → nothing unless it carries `displayText`, everything else → the `displayText` string. No renderer yet — that is F3.
```

- [ ] **Step 6: Commit**

```bash
git add editors/vscode/src/notebook/result.ts editors/vscode/src/notebook/result.test.ts docs/DEVLOG.md
git commit -m "feat(vscode): map ResultEnvelope to notebook cell outputs (text)"
```

---

### Task 5: Window-lifetime worker session singleton

**Files:**
- Create: `editors/vscode/src/worker/session.ts`
- Test: `editors/vscode/src/worker/session.test.ts`

**Interfaces:**
- Consumes: `resolveDotnet`, `DotnetNotFoundError`, `DotnetTooOldError` from `./dotnet`; `resolveWorkerDll`, `WorkerDllNotFoundError` from `./worker-path`; `WorkerClient` from `./client`; `spawnWorkerProcess` from `./spawn`; `newRequestId`, `isWorkerVersionSupported`, `HealthInfo` from `../protocol`.
- Produces:
  - `type WorkerUnavailableKind = "dotnet-missing" | "dotnet-old" | "worker-missing" | "version-unsupported" | "error"`
  - `class WorkerUnavailableError extends Error` — `readonly kind: WorkerUnavailableKind`, `readonly userMessage: string`.
  - `interface WorkerSession { client: WorkerClient; health: HealthInfo; dispose(): void }`
  - `interface ResolveWorkerSessionDeps { extensionRoot: string; env: NodeJS.ProcessEnv; log: (line: string) => void; resolveDotnet?: typeof resolveDotnet; resolveWorkerDll?: typeof resolveWorkerDll; makeClient?: (dotnetCommand: string, dll: string) => WorkerClient }`
  - `async function resolveWorkerSession(deps: ResolveWorkerSessionDeps): Promise<WorkerSession>` — resolves dotnet (map `DotnetNotFoundError` → `dotnet-missing`, `DotnetTooOldError` → `dotnet-old`), resolves the dll (`WorkerDllNotFoundError` → `worker-missing`), makes a client, sends `worker/health`; an error frame or a thrown send → `error`; `!isWorkerVersionSupported` → `version-unsupported` (disposes the client first in both failure cases). On success returns `{ client, health, dispose }`.
  - `function getWorkerSession(deps: ResolveWorkerSessionDeps): Promise<WorkerSession>` — memoises the first successful (or in-flight) call; a rejection clears the memo so the next call retries.
  - `function disposeWorkerSession(): void` — disposes the memoised session if any and clears the memo.

- [ ] **Step 1: Write `editors/vscode/src/worker/session.test.ts`**

```ts
import { describe, it, expect, afterEach } from "vitest";
import {
  resolveWorkerSession,
  getWorkerSession,
  disposeWorkerSession,
  WorkerUnavailableError,
  type ResolveWorkerSessionDeps,
} from "./session";
import { DotnetNotFoundError, DotnetTooOldError } from "./dotnet";
import { WorkerDllNotFoundError } from "./worker-path";

function fakeClient(send: (req: { id: string; method: string; params: unknown }) => Promise<unknown>) {
  let disposed = false;
  return {
    client: {
      send,
      dispose: () => {
        disposed = true;
      },
    } as never,
    wasDisposed: () => disposed,
  };
}

function baseDeps(overrides: Partial<ResolveWorkerSessionDeps>): ResolveWorkerSessionDeps {
  return {
    extensionRoot: "/ext",
    env: {},
    log: () => {},
    resolveDotnet: async () => ({ command: "dotnet", version: "10.0.100" }),
    resolveWorkerDll: () => "/ext/dist/worker/FSharpQueryStudio.Worker.dll",
    makeClient: () =>
      fakeClient(async (req) => ({ id: req.id, result: { version: "1.0.0", capabilities: [] } }))
        .client,
    ...overrides,
  };
}

afterEach(() => {
  disposeWorkerSession();
});

describe("resolveWorkerSession", () => {
  it("returns a session with the worker health on success", async () => {
    const session = await resolveWorkerSession(baseDeps({}));
    expect(session.health.version).toBe("1.0.0");
  });

  it("maps a missing dotnet to WorkerUnavailableError(dotnet-missing)", async () => {
    const err = await resolveWorkerSession(
      baseDeps({
        resolveDotnet: async () => {
          throw new DotnetNotFoundError();
        },
      }),
    ).catch((e) => e as WorkerUnavailableError);
    expect(err).toBeInstanceOf(WorkerUnavailableError);
    expect(err.kind).toBe("dotnet-missing");
  });

  it("maps an old dotnet to dotnet-old", async () => {
    const err = await resolveWorkerSession(
      baseDeps({
        resolveDotnet: async () => {
          throw new DotnetTooOldError("8.0.404");
        },
      }),
    ).catch((e) => e as WorkerUnavailableError);
    expect(err.kind).toBe("dotnet-old");
  });

  it("maps a missing worker dll to worker-missing", async () => {
    const err = await resolveWorkerSession(
      baseDeps({
        resolveWorkerDll: () => {
          throw new WorkerDllNotFoundError(["/ext/dist/worker/FSharpQueryStudio.Worker.dll"]);
        },
      }),
    ).catch((e) => e as WorkerUnavailableError);
    expect(err.kind).toBe("worker-missing");
  });

  it("maps an unsupported worker version to version-unsupported and disposes the client", async () => {
    const fc = fakeClient(async (req) => ({ id: req.id, result: { version: "2.0.0", capabilities: [] } }));
    const err = await resolveWorkerSession(baseDeps({ makeClient: () => fc.client })).catch(
      (e) => e as WorkerUnavailableError,
    );
    expect(err.kind).toBe("version-unsupported");
    expect(fc.wasDisposed()).toBe(true);
  });

  it("maps a worker error frame to error and disposes the client", async () => {
    const fc = fakeClient(async (req) => ({ id: req.id, error: { code: "boom", message: "no" } }));
    const err = await resolveWorkerSession(baseDeps({ makeClient: () => fc.client })).catch(
      (e) => e as WorkerUnavailableError,
    );
    expect(err.kind).toBe("error");
    expect(fc.wasDisposed()).toBe(true);
  });
});

describe("getWorkerSession", () => {
  it("resolves the worker only once across concurrent calls", async () => {
    let made = 0;
    const deps = baseDeps({
      makeClient: () => {
        made++;
        return fakeClient(async (req) => ({
          id: req.id,
          result: { version: "1.0.0", capabilities: [] },
        })).client;
      },
    });
    const [a, b] = await Promise.all([getWorkerSession(deps), getWorkerSession(deps)]);
    expect(a).toBe(b);
    expect(made).toBe(1);
  });

  it("retries after a failed resolution", async () => {
    let attempt = 0;
    const deps = baseDeps({
      resolveDotnet: async () => {
        attempt++;
        if (attempt === 1) {
          throw new DotnetNotFoundError();
        }
        return { command: "dotnet", version: "10.0.100" };
      },
    });
    await expect(getWorkerSession(deps)).rejects.toBeInstanceOf(WorkerUnavailableError);
    const session = await getWorkerSession(deps);
    expect(session.health.version).toBe("1.0.0");
  });
});
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd editors/vscode && npx vitest run src/worker/session.test.ts`
Expected: FAIL — `Cannot find module './session'`.

- [ ] **Step 3: Write `editors/vscode/src/worker/session.ts`**

```ts
import { resolveDotnet as realResolveDotnet, DotnetNotFoundError, DotnetTooOldError } from "./dotnet";
import { resolveWorkerDll as realResolveWorkerDll, WorkerDllNotFoundError } from "./worker-path";
import { WorkerClient } from "./client";
import { spawnWorkerProcess } from "./spawn";
import { newRequestId, isWorkerVersionSupported, type HealthInfo } from "../protocol";

export type WorkerUnavailableKind =
  | "dotnet-missing"
  | "dotnet-old"
  | "worker-missing"
  | "version-unsupported"
  | "error";

export class WorkerUnavailableError extends Error {
  constructor(
    readonly kind: WorkerUnavailableKind,
    readonly userMessage: string,
  ) {
    super(userMessage);
    this.name = "WorkerUnavailableError";
  }
}

export interface WorkerSession {
  client: WorkerClient;
  health: HealthInfo;
  dispose(): void;
}

export interface ResolveWorkerSessionDeps {
  extensionRoot: string;
  env: NodeJS.ProcessEnv;
  log: (line: string) => void;
  resolveDotnet?: typeof realResolveDotnet;
  resolveWorkerDll?: typeof realResolveWorkerDll;
  makeClient?: (dotnetCommand: string, dll: string) => WorkerClient;
}

export async function resolveWorkerSession(deps: ResolveWorkerSessionDeps): Promise<WorkerSession> {
  const resolveDotnet = deps.resolveDotnet ?? realResolveDotnet;
  const resolveWorkerDll = deps.resolveWorkerDll ?? realResolveWorkerDll;
  const makeClient =
    deps.makeClient ??
    ((command, dll) => new WorkerClient({ spawn: () => spawnWorkerProcess(command, dll) }));

  let dotnet: { command: string; version: string };
  try {
    dotnet = await resolveDotnet({ env: deps.env });
  } catch (err) {
    if (err instanceof DotnetNotFoundError) {
      throw new WorkerUnavailableError("dotnet-missing", err.userMessage);
    }
    if (err instanceof DotnetTooOldError) {
      throw new WorkerUnavailableError("dotnet-old", err.userMessage);
    }
    throw new WorkerUnavailableError("error", (err as Error).message);
  }
  deps.log(`Using dotnet ${dotnet.version} (${dotnet.command}).`);

  let dll: string;
  try {
    dll = resolveWorkerDll(deps.extensionRoot, deps.env);
  } catch (err) {
    if (err instanceof WorkerDllNotFoundError) {
      throw new WorkerUnavailableError("worker-missing", err.userMessage);
    }
    throw new WorkerUnavailableError("error", (err as Error).message);
  }
  deps.log(`Worker dll: ${dll}`);

  const client = makeClient(dotnet.command, dll);
  let health: HealthInfo;
  try {
    const response = await client.send({
      id: newRequestId(),
      method: "worker/health",
      params: {},
    });
    if (response.error) {
      client.dispose();
      throw new WorkerUnavailableError(
        "error",
        `Worker health check failed: ${response.error.code} ${response.error.message}`,
      );
    }
    health = response.result as HealthInfo;
  } catch (err) {
    if (err instanceof WorkerUnavailableError) {
      throw err;
    }
    client.dispose();
    throw new WorkerUnavailableError("error", (err as Error).message);
  }

  if (!isWorkerVersionSupported(health.version)) {
    client.dispose();
    throw new WorkerUnavailableError(
      "version-unsupported",
      `Worker protocol version ${health.version} is not supported by this build of the extension.`,
    );
  }
  deps.log(`Worker healthy: version ${health.version}.`);

  return { client, health, dispose: () => client.dispose() };
}

// --- window-lifetime singleton ---

let sessionPromise: Promise<WorkerSession> | undefined;

export function getWorkerSession(deps: ResolveWorkerSessionDeps): Promise<WorkerSession> {
  if (!sessionPromise) {
    sessionPromise = resolveWorkerSession(deps).catch((err) => {
      sessionPromise = undefined; // let the next run retry
      throw err;
    });
  }
  return sessionPromise;
}

export function disposeWorkerSession(): void {
  const pending = sessionPromise;
  sessionPromise = undefined;
  if (pending) {
    void pending.then((session) => session.dispose()).catch(() => undefined);
  }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `cd editors/vscode && npx vitest run src/worker/session.test.ts`
Expected: PASS (8 assertions).

- [ ] **Step 5: DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-05 — F2.5: window-lifetime worker session singleton

`worker/session.ts`: `resolveWorkerSession` resolves dotnet + the worker dll, spawns one `WorkerClient`, and gates it behind a `worker/health` version check, collapsing every failure into a typed `WorkerUnavailableError` (`dotnet-missing` / `dotnet-old` / `worker-missing` / `version-unsupported` / `error`) with a `userMessage`. `getWorkerSession` memoises the first success (failures clear the memo so a later run retries); `disposeWorkerSession` tears it down for `deactivate`. Dependency-injected `resolveDotnet` / `resolveWorkerDll` / `makeClient` for tests.
```

- [ ] **Step 6: Commit**

```bash
git add editors/vscode/src/worker/session.ts editors/vscode/src/worker/session.test.ts docs/DEVLOG.md
git commit -m "feat(vscode): window-lifetime worker session singleton with health gate"
```

---

### Task 6: `NotebookController` — cell queue, evaluate, interrupt, restart

**Files:**
- Create: `editors/vscode/src/notebook/controller.ts`
- Test: `editors/vscode/src/notebook/controller.test.ts`
- Modify: `editors/vscode/test/vscode-stub.ts`

> Note (F2 final-review fix wave): `executeHandler` was changed from a bare `async` handler to a per-controller promise-chain (`runQueue = runQueue.then(() => runBatch(cells, notebook))`) so cell runs serialize *across* re-entrant `executeHandler` invocations, not just within one batch; the per-cell body now also runs inside a `try/catch` that always ends the execution.

**Interfaces:**
- Consumes: `FSQNB_NOTEBOOK_TYPE` from `./serializer`; `mapEnvelope`, `OutputItemSpec` from `./result`; `newRequestId`, `readUnionTag`, `SESSION_MODE_PERSISTENT`, `EvaluateParams`, `ResultEnvelope`, `SessionResetParams`, `WorkerResponseFrame` from `../protocol`.
- Produces:
  - `interface WorkerLike { send(req: { id: string; method: string; params: unknown }): Promise<WorkerResponseFrame>; cancel(requestId: string, gracePeriodMs: number): Promise<void> }`
  - `interface ControllerDeps { getWorker: () => Promise<WorkerLike>; log: (line: string) => void }`
  - `function createFsQueryController(deps: ControllerDeps): vscode.NotebookController` — creates the controller (id `fs-query-studio-kernel`, type `fsquery-notebook`, label `"F# Query Studio"`), sets `supportedLanguages = ["fsharp"]`, `supportsExecutionOrder = true`, and wires `executeHandler` (sequential; one `execution/evaluate` per cell with `mode = SESSION_MODE_PERSISTENT`, `sessionId = documentUri = notebook.uri.toString()`; stops the batch on the first failed cell) and `interruptHandler` (`worker.cancel(inFlightRequestId, 5000)`).
  - `async function restartKernel(getWorker: () => Promise<WorkerLike>, notebookUri: string): Promise<void>` — sends `session/reset` with `{ sessionId: notebookUri }`.
  - `test/vscode-stub.ts` additionally exports `class NotebookCellOutputItem` (static `stdout` / `stderr` / `text` / `error`), `class NotebookCellOutput`, and `notebooks` (with `createNotebookController` returning a fake that records `_executions`), all added to the default export.

- [ ] **Step 1: Add the notebook-execution surface to `editors/vscode/test/vscode-stub.ts`**

Append before the `export default`:
```ts
// --- notebook execution model (F2) ---
export class NotebookCellOutputItem {
  constructor(
    public data: unknown,
    public mime: string,
  ) {}
  static stdout(value: string) {
    return new NotebookCellOutputItem(value, "application/vnd.code.notebook.stdout");
  }
  static stderr(value: string) {
    return new NotebookCellOutputItem(value, "application/vnd.code.notebook.stderr");
  }
  static text(value: string, mime = "text/plain") {
    return new NotebookCellOutputItem(value, mime);
  }
  static error(err: { name: string; message: string }) {
    return new NotebookCellOutputItem(err, "application/vnd.code.notebook.error");
  }
}

export class NotebookCellOutput {
  constructor(public items: NotebookCellOutputItem[]) {}
}

export const notebooks = {
  _controllers: [] as unknown[],
  createNotebookController(id: string, notebookType: string, label: string) {
    const executions: Array<Record<string, unknown>> = [];
    const controller: Record<string, unknown> = {
      id,
      notebookType,
      label,
      supportedLanguages: [] as string[],
      supportsExecutionOrder: false,
      description: "",
      executeHandler: undefined,
      interruptHandler: undefined,
      _executions: executions,
      createNotebookCellExecution(cell: unknown) {
        const exec: Record<string, unknown> = {
          cell,
          executionOrder: 0,
          outputs: [] as unknown[],
          started: false,
          ended: undefined as boolean | undefined,
          start() {
            (this as { started: boolean }).started = true;
          },
          end(ok?: boolean) {
            (this as { ended: boolean | undefined }).ended = ok;
          },
          replaceOutput(out: unknown) {
            (this as { outputs: unknown[] }).outputs = Array.isArray(out) ? out : [out];
          },
          clearOutput() {
            (this as { outputs: unknown[] }).outputs = [];
          },
          token: { isCancellationRequested: false },
        };
        executions.push(exec);
        return exec;
      },
      dispose() {},
    };
    this._controllers.push(controller);
    return controller;
  },
};
```

Add the three names to the default export object (`NotebookCellOutputItem`, `NotebookCellOutput`, `notebooks`).

- [ ] **Step 2: Write `editors/vscode/src/notebook/controller.test.ts`**

```ts
import { describe, it, expect } from "vitest";
import * as vscode from "vscode";
import { createFsQueryController, restartKernel, type WorkerLike } from "./controller";
import type { ResultEnvelope } from "../protocol";

function tableEnvelope(overrides: Partial<ResultEnvelope> = {}): ResultEnvelope {
  return {
    kind: { Case: "Table" },
    displayText: null,
    schema: [{ name: "n", displayType: "int" }],
    rows: [[1], [2]],
    isTruncated: false,
    continuationToken: null,
    elapsedMs: 5,
    output: [{ stream: { Case: "Stdout" }, text: "hi\n" }],
    ...overrides,
  };
}

const cell = (text: string) => ({ document: { getText: () => text } }) as never;
const notebook = (uri: string) => ({ uri: { toString: () => uri } }) as never;
const execs = (c: vscode.NotebookController) =>
  (c as unknown as { _executions: Array<Record<string, unknown>> })._executions;

describe("createFsQueryController — executeHandler", () => {
  it("sends execution/evaluate in Persistent mode with sessionId = documentUri = notebook uri", async () => {
    const sent: Array<{ method: string; params: Record<string, unknown> }> = [];
    const worker: WorkerLike = {
      send: async (req) => {
        sent.push(req as never);
        return { id: req.id, result: tableEnvelope() };
      },
      cancel: async () => {},
    };
    const controller = createFsQueryController({ getWorker: async () => worker, log: () => {} });

    await controller.executeHandler!([cell("1 + 1")], notebook("file:///a.fsqnb"), controller);

    expect(sent).toHaveLength(1);
    expect(sent[0].method).toBe("execution/evaluate");
    expect(sent[0].params.mode).toEqual({ Case: "Persistent" });
    expect(sent[0].params.sessionId).toBe("file:///a.fsqnb");
    expect(sent[0].params.documentUri).toBe("file:///a.fsqnb");
    expect(sent[0].params.source).toBe("1 + 1");
  });

  it("writes the mapped outputs and ends the execution successfully for a non-error result", async () => {
    const worker: WorkerLike = {
      send: async (req) => ({ id: req.id, result: tableEnvelope() }),
      cancel: async () => {},
    };
    const controller = createFsQueryController({ getWorker: async () => worker, log: () => {} });
    await controller.executeHandler!([cell("q")], notebook("file:///a.fsqnb"), controller);

    const exec = execs(controller)[0];
    expect(exec.ended).toBe(true);
    const outputs = exec.outputs as Array<{ items: Array<{ data: unknown; mime: string }> }>;
    expect(outputs[0].items[0].mime).toBe("application/vnd.code.notebook.stdout");
    expect(outputs[0].items[1].mime).toBe("text/plain");
  });

  it("runs cells sequentially and stops after the first cell whose result is an Error", async () => {
    const seen: string[] = [];
    const worker: WorkerLike = {
      send: async (req) => {
        const src = (req.params as { source: string }).source;
        seen.push(src);
        return src === "bad"
          ? {
              id: req.id,
              result: tableEnvelope({ kind: { Case: "Error" }, displayText: "boom", output: [] }),
            }
          : { id: req.id, result: tableEnvelope() };
      },
      cancel: async () => {},
    };
    const controller = createFsQueryController({ getWorker: async () => worker, log: () => {} });

    await controller.executeHandler!(
      [cell("ok1"), cell("bad"), cell("ok2")],
      notebook("file:///a.fsqnb"),
      controller,
    );

    expect(seen).toEqual(["ok1", "bad"]);
    const last = execs(controller).at(-1)!;
    expect(last.ended).toBe(false);
  });

  it("maps a worker/crashed error frame to a cell error and stops", async () => {
    const worker: WorkerLike = {
      send: async (req) => ({
        id: req.id,
        error: { code: "worker/crashed", message: "The worker process exited before responding." },
      }),
      cancel: async () => {},
    };
    const controller = createFsQueryController({ getWorker: async () => worker, log: () => {} });
    await controller.executeHandler!([cell("q")], notebook("file:///a.fsqnb"), controller);

    const exec = execs(controller)[0];
    expect(exec.ended).toBe(false);
    const item = (exec.outputs as Array<{ items: Array<{ data: { message: string }; mime: string }> }>)[0]
      .items[0];
    expect(item.mime).toBe("application/vnd.code.notebook.error");
    expect(item.data.message).toMatch(/session was lost/i);
  });

  it("surfaces a WorkerUnavailable rejection from getWorker as a cell error", async () => {
    const controller = createFsQueryController({
      getWorker: async () => {
        throw Object.assign(new Error("x"), { userMessage: "Install .NET 10 to run F# cells." });
      },
      log: () => {},
    });
    await controller.executeHandler!([cell("q")], notebook("file:///a.fsqnb"), controller);

    const exec = execs(controller)[0];
    expect(exec.ended).toBe(false);
    const item = (exec.outputs as Array<{ items: Array<{ data: { message: string } }> }>)[0].items[0];
    expect(item.data.message).toBe("Install .NET 10 to run F# cells.");
  });

  it("interruptHandler cancels the in-flight request id", async () => {
    let release!: (frame: unknown) => void;
    const cancelled: string[] = [];
    const worker: WorkerLike = {
      send: (req) =>
        new Promise((resolve) => {
          release = resolve;
          void req;
        }),
      cancel: async (id) => {
        cancelled.push(id);
        release({ id, result: tableEnvelope() });
      },
    };
    const controller = createFsQueryController({ getWorker: async () => worker, log: () => {} });

    const run = controller.executeHandler!([cell("slow")], notebook("file:///a.fsqnb"), controller);
    await new Promise((r) => setTimeout(r, 5));
    await controller.interruptHandler!(notebook("file:///a.fsqnb"));
    await run;

    expect(cancelled).toHaveLength(1);
  });
});

describe("restartKernel", () => {
  it("sends session/reset with the notebook uri as sessionId", async () => {
    const sent: Array<{ method: string; params: unknown }> = [];
    const worker: WorkerLike = {
      send: async (req) => {
        sent.push(req as never);
        return { id: req.id, result: null };
      },
      cancel: async () => {},
    };
    await restartKernel(async () => worker, "file:///a.fsqnb");
    expect(sent[0].method).toBe("session/reset");
    expect(sent[0].params).toEqual({ sessionId: "file:///a.fsqnb" });
  });
});
```

- [ ] **Step 3: Run to verify it fails**

Run: `cd editors/vscode && npx vitest run src/notebook/controller.test.ts`
Expected: FAIL — `Cannot find module './controller'`.

- [ ] **Step 4: Write `editors/vscode/src/notebook/controller.ts`**

```ts
import * as vscode from "vscode";
import { FSQNB_NOTEBOOK_TYPE } from "./serializer";
import { mapEnvelope, type OutputItemSpec } from "./result";
import {
  newRequestId,
  readUnionTag,
  SESSION_MODE_PERSISTENT,
  type EvaluateParams,
  type ResultEnvelope,
  type SessionResetParams,
  type WorkerResponseFrame,
} from "../protocol";

const CONTROLLER_ID = "fs-query-studio-kernel";
const CANCEL_GRACE_MS = 5000;

/** The subset of `WorkerClient` the controller needs — injectable for tests. */
export interface WorkerLike {
  send(req: { id: string; method: string; params: unknown }): Promise<WorkerResponseFrame>;
  cancel(requestId: string, gracePeriodMs: number): Promise<void>;
}

export interface ControllerDeps {
  /**
   * Resolves the shared, window-lifetime worker client. Rejects with a
   * `WorkerUnavailableError` (carries `userMessage`) when the worker can't
   * be started.
   */
  getWorker: () => Promise<WorkerLike>;
  log: (line: string) => void;
}

function toOutputItem(spec: OutputItemSpec): vscode.NotebookCellOutputItem {
  switch (spec.kind) {
    case "stdout":
      return vscode.NotebookCellOutputItem.stdout(spec.text);
    case "stderr":
      return vscode.NotebookCellOutputItem.stderr(spec.text);
    case "text":
      return vscode.NotebookCellOutputItem.text(spec.text, "text/plain");
    case "error":
      return vscode.NotebookCellOutputItem.error(
        Object.assign(new Error(spec.message), { name: spec.name }),
      );
  }
}

function errorOutput(name: string, message: string): vscode.NotebookCellOutput {
  return new vscode.NotebookCellOutput([
    vscode.NotebookCellOutputItem.error(Object.assign(new Error(message), { name })),
  ]);
}

export function createFsQueryController(deps: ControllerDeps): vscode.NotebookController {
  const controller = vscode.notebooks.createNotebookController(
    CONTROLLER_ID,
    FSQNB_NOTEBOOK_TYPE,
    "F# Query Studio",
  );
  controller.supportedLanguages = ["fsharp"];
  controller.supportsExecutionOrder = true;
  controller.description = "Runs F# query cells in a persistent FSI session.";

  let executionOrder = 0;
  let inFlightRequestId: string | undefined;
  let worker: WorkerLike | undefined;

  controller.interruptHandler = async () => {
    if (worker && inFlightRequestId) {
      await worker.cancel(inFlightRequestId, CANCEL_GRACE_MS);
    }
  };

  controller.executeHandler = async (cells, notebook) => {
    const sessionId = notebook.uri.toString();

    let client: WorkerLike;
    try {
      client = await deps.getWorker();
      worker = client;
    } catch (err) {
      const message =
        (err as { userMessage?: string }).userMessage ?? (err as Error).message;
      deps.log(`Worker unavailable: ${message}`);
      for (const cell of cells) {
        const exec = controller.createNotebookCellExecution(cell);
        exec.executionOrder = ++executionOrder;
        exec.start(Date.now());
        exec.replaceOutput(errorOutput("WorkerUnavailable", message));
        exec.end(false, Date.now());
      }
      return;
    }

    for (const cell of cells) {
      const exec = controller.createNotebookCellExecution(cell);
      exec.executionOrder = ++executionOrder;
      exec.start(Date.now());

      const requestId = newRequestId();
      inFlightRequestId = requestId;
      const params: EvaluateParams = {
        source: cell.document.getText(),
        sessionId,
        mode: SESSION_MODE_PERSISTENT,
        documentUri: sessionId,
      };

      let response: WorkerResponseFrame;
      try {
        response = await client.send({ id: requestId, method: "execution/evaluate", params });
      } catch (err) {
        inFlightRequestId = undefined;
        exec.replaceOutput(errorOutput("WorkerError", (err as Error).message));
        exec.end(false, Date.now());
        break;
      }
      inFlightRequestId = undefined;

      if (response.error) {
        const message =
          response.error.code === "worker/crashed"
            ? "The worker exited before responding. The FSI session was lost; it restarts on the next run."
            : response.error.message;
        exec.replaceOutput(errorOutput(response.error.code, message));
        exec.end(false, Date.now());
        break;
      }

      const envelope = response.result as ResultEnvelope;
      const items = mapEnvelope(envelope).map(toOutputItem);
      exec.replaceOutput(new vscode.NotebookCellOutput(items));

      const isError = readUnionTag(envelope.kind) === "Error";
      exec.end(!isError, Date.now());
      if (isError) {
        break;
      }
    }
  };

  return controller;
}

export async function restartKernel(
  getWorker: () => Promise<WorkerLike>,
  notebookUri: string,
): Promise<void> {
  const client = await getWorker();
  const params: SessionResetParams = { sessionId: notebookUri };
  await client.send({ id: newRequestId(), method: "session/reset", params });
}
```

- [ ] **Step 5: Run to verify it passes, then the whole suite**

Run: `cd editors/vscode && npx vitest run src/notebook/controller.test.ts`
Expected: PASS (8 assertions).
Run: `cd editors/vscode && npm test`
Expected: PASS — every unit test green (stub change is additive).

- [ ] **Step 6: DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-05 — F2.6: NotebookController — cell queue, evaluate, interrupt, restart

`notebook/controller.ts`: `createFsQueryController` builds the `fs-query-studio-kernel` controller for `fsquery-notebook`. `executeHandler` runs cells **sequentially** (the worker is one-eval-at-a-time), one `execution/evaluate` per cell in `Persistent` mode with `sessionId = documentUri = notebook.uri.toString()`, maps the response envelope through `mapEnvelope`, and stops the batch on the first `Error` result or error frame (`worker/crashed` gets a "session was lost" message). A `getWorker` rejection (WorkerUnavailable) becomes a per-cell error output, no crash. `interruptHandler` → `WorkerClient.cancel(inFlightRequestId, 5000)`. `restartKernel` → `session/reset`. `test/vscode-stub.ts` gains `NotebookCellOutput(Item)` and a fake `notebooks.createNotebookController`.
```

- [ ] **Step 7: Commit**

```bash
git add editors/vscode/src/notebook/controller.ts editors/vscode/src/notebook/controller.test.ts editors/vscode/test/vscode-stub.ts docs/DEVLOG.md
git commit -m "feat(vscode): NotebookController for .fsqnb — evaluate, interrupt, restart"
```

---

### Task 7: Wire the notebook into `extension.ts` and `package.json`

**Files:**
- Modify: `editors/vscode/package.json`
- Modify: `editors/vscode/src/extension.ts`
- Test: `editors/vscode/src/extension.test.ts` (add a `describe` block for `activate`)

**Interfaces:**
- Consumes: `FsqnbSerializer`, `FSQNB_NOTEBOOK_TYPE` from `./notebook/serializer`; `createFsQueryController`, `restartKernel` from `./notebook/controller`; `getWorkerSession`, `disposeWorkerSession` from `./worker/session`.
- Produces:
  - `package.json`: `activationEvents` gains `"onNotebook:fsquery-notebook"`; `contributes.notebooks` array added; `contributes.commands` gains the restart command.
  - `extension.ts` `activate` registers the serializer, the controller, and the `fs-query-studio.restartKernel` command, pushing all into `context.subscriptions`; `deactivate` calls `disposeWorkerSession()`.
  - `extension.ts` exports an unchanged `runHealthCheck` / `HealthCheckResult` (existing tests keep passing).

- [ ] **Step 1: Write the failing test (append to `editors/vscode/src/extension.test.ts`)**

```ts
import * as vscodeStub from "../test/vscode-stub";
import { activate, deactivate } from "./extension";

describe("activate — notebook wiring", () => {
  function fakeContext() {
    return {
      extensionPath: "/ext",
      subscriptions: [] as Array<{ dispose: () => void }>,
    } as never;
  }

  it("registers a NotebookSerializer for fsquery-notebook", async () => {
    const registered: string[] = [];
    (vscodeStub.workspace as unknown as {
      registerNotebookSerializer: (t: string, s: unknown) => { dispose: () => void };
    }).registerNotebookSerializer = (type) => {
      registered.push(type);
      return { dispose: () => {} };
    };

    await activate(fakeContext());
    expect(registered).toContain("fsquery-notebook");
    await deactivate();
  });

  it("registers the restartKernel command", async () => {
    await activate(fakeContext());
    const commands = await vscodeStub.commands.executeCommand("fs-query-studio.restartKernel");
    // handler exists (returns undefined without an active notebook); no throw
    expect(commands).toBeUndefined();
    await deactivate();
  });

  it("creates a notebook controller", async () => {
    await activate(fakeContext());
    const made = (vscodeStub.notebooks as unknown as { _controllers: unknown[] })._controllers;
    expect(made.length).toBeGreaterThan(0);
    await deactivate();
  });
});
```

- [ ] **Step 2: Extend `editors/vscode/test/vscode-stub.ts` for `activate`**

Add a `workspace` export (and to the default export) plus `window.activeNotebookEditor`:
```ts
export const workspace = {
  registerNotebookSerializer(_type: string, _serializer: unknown): Disposable {
    return { dispose: () => {} };
  },
};
```
Add `activeNotebookEditor: undefined as unknown` to the `window` object. Include `workspace` in the default export.

- [ ] **Step 3: Run to verify it fails**

Run: `cd editors/vscode && npx vitest run src/extension.test.ts`
Expected: FAIL — `activate` does not register a serializer / controller / restart command yet.

- [ ] **Step 4: Update `editors/vscode/package.json`**

Set `activationEvents` to:
```json
  "activationEvents": [
    "onCommand:fs-query-studio.checkWorker",
    "onNotebook:fsquery-notebook"
  ],
```
Replace the `contributes` block with:
```json
  "contributes": {
    "commands": [
      {
        "command": "fs-query-studio.checkWorker",
        "title": "F# Query Studio: Check Worker"
      },
      {
        "command": "fs-query-studio.restartKernel",
        "title": "F# Query Studio: Restart Notebook Session"
      }
    ],
    "notebooks": [
      {
        "type": "fsquery-notebook",
        "displayName": "F# Query Notebook",
        "selector": [
          { "filenamePattern": "*.fsqnb" }
        ]
      }
    ]
  },
```

- [ ] **Step 5: Update `editors/vscode/src/extension.ts`**

Add imports at the top:
```ts
import { FsqnbSerializer, FSQNB_NOTEBOOK_TYPE } from "./notebook/serializer";
import { createFsQueryController, restartKernel } from "./notebook/controller";
import { getWorkerSession, disposeWorkerSession } from "./worker/session";
```

In `activate`, after `const command = vscode.commands.registerCommand("fs-query-studio.checkWorker", runCheck);` and before the `context.subscriptions.push(...)` line, insert:
```ts
  const getWorker = async () => {
    const session = await getWorkerSession({
      extensionRoot: context.extensionPath,
      env: process.env,
      log: (line) => outputChannel!.appendLine(line),
    });
    return session.client;
  };

  const notebookSerializer = vscode.workspace.registerNotebookSerializer(
    FSQNB_NOTEBOOK_TYPE,
    new FsqnbSerializer(),
  );

  const controller = createFsQueryController({
    getWorker,
    log: (line) => outputChannel!.appendLine(line),
  });

  const restartCommand = vscode.commands.registerCommand(
    "fs-query-studio.restartKernel",
    async () => {
      const active = vscode.window.activeNotebookEditor;
      if (!active || active.notebook.notebookType !== FSQNB_NOTEBOOK_TYPE) {
        void vscode.window.showInformationMessage(
          "Open an .fsqnb notebook to restart its F# session.",
        );
        return;
      }
      try {
        await restartKernel(getWorker, active.notebook.uri.toString());
        void vscode.window.showInformationMessage("F# Query Studio session restarted.");
      } catch (err) {
        void vscode.window.showErrorMessage(
          `Restart failed: ${(err as { userMessage?: string }).userMessage ?? (err as Error).message}`,
        );
      }
    },
  );
```

Change the final push in `activate` to:
```ts
  context.subscriptions.push(
    command,
    notebookSerializer,
    controller,
    restartCommand,
    outputChannel,
  );
```

Change `deactivate` to:
```ts
export async function deactivate(): Promise<void> {
  disposeWorkerSession();
  outputChannel?.dispose();
  outputChannel = undefined;
}
```

- [ ] **Step 6: Run to verify it passes, then compile + whole suite**

Run: `cd editors/vscode && npx vitest run src/extension.test.ts`
Expected: PASS (existing `runHealthCheck` tests + 3 new `activate` tests).
Run: `cd editors/vscode && npm test`
Expected: PASS — full unit suite.
Run: `cd editors/vscode && npm run compile`
Expected: `tsc --noEmit` clean; `esbuild` writes `dist/extension.js`.

- [ ] **Step 7: DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-05 — F2.7: register the notebook — serializer, controller, restart command

`package.json`: `contributes.notebooks` (`fsquery-notebook`, `*.fsqnb`), the `onNotebook:fsquery-notebook` activation event, and a `fs-query-studio.restartKernel` command. `extension.ts` `activate` now registers `FsqnbSerializer`, `createFsQueryController` (fed a `getWorker` that resolves the window's shared `getWorkerSession`), and the restart command (no-ops with a hint when the active editor isn't an `.fsqnb`); `deactivate` disposes the worker session. `checkWorker` and `runHealthCheck` are unchanged.
```

- [ ] **Step 8: Commit**

```bash
git add editors/vscode/package.json editors/vscode/src/extension.ts editors/vscode/src/extension.test.ts editors/vscode/test/vscode-stub.ts docs/DEVLOG.md
git commit -m "feat(vscode): register the .fsqnb notebook, controller, and restart command"
```

---

### Task 8: Extension-host integration test — open, run, cancel, restart

**Files:**
- Create: `editors/vscode/test/integration/fixtures/sample.fsqnb`
- Create: `editors/vscode/test/integration/notebook.test.ts`

**Interfaces:**
- Consumes: the activated extension, the bundled worker at `dist/worker/FSharpQueryStudio.Worker.dll`.
- Produces: Mocha specs compiled by `tsconfig.integration.json` into `out/test/integration/`, run by `.vscode-test.mjs` (already globs `out/test/integration/**/*.test.js` — no config change).

- [ ] **Step 1: Write `editors/vscode/test/integration/fixtures/sample.fsqnb`**

```json
{
  "version": 1,
  "cells": [
    { "kind": "markdown", "value": "# Sample F# Query Notebook" },
    { "kind": "code", "language": "fsharp", "value": "printfn \"hello from fsi\"\n1 + 1" },
    { "kind": "code", "language": "fsharp", "value": "[ for i in 1..3 -> {| n = i; sq = i * i |} ]" }
  ],
  "metadata": {}
}
```

- [ ] **Step 2: Write `editors/vscode/test/integration/notebook.test.ts`**

```ts
import * as assert from "node:assert";
import * as fs from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";

const EXTENSION_ID = "fs-query-studio.fs-query-studio";

// out/test/integration -> editors/vscode
const extensionRoot = path.resolve(__dirname, "..", "..", "..");
const fixture = path.join(extensionRoot, "test", "integration", "fixtures", "sample.fsqnb");
const bundledWorker = path.join(extensionRoot, "dist", "worker", "FSharpQueryStudio.Worker.dll");
const hasWorker = fs.existsSync(bundledWorker);

function outputText(cell: vscode.NotebookCell): string {
  return cell.outputs
    .flatMap((o) => o.items)
    .map((i) => Buffer.from(i.data).toString("utf8"))
    .join("\n");
}

describe("fsquery-notebook integration", () => {
  before(async () => {
    const ext = vscode.extensions.getExtension(EXTENSION_ID);
    assert.ok(ext, `extension ${EXTENSION_ID} not found`);
    await ext!.activate();
  });

  it("opens an .fsqnb file as a notebook with the right cells", async () => {
    const doc = await vscode.workspace.openNotebookDocument(vscode.Uri.file(fixture));
    assert.strictEqual(doc.notebookType, "fsquery-notebook");
    assert.strictEqual(doc.cellCount, 3);
    assert.strictEqual(doc.cellAt(0).kind, vscode.NotebookCellKind.Markup);
    assert.strictEqual(doc.cellAt(1).kind, vscode.NotebookCellKind.Code);
    assert.strictEqual(doc.cellAt(1).document.languageId, "fsharp");
  });

  (hasWorker ? it : it.skip)(
    "runs a code cell and captures stdout + the tail value",
    async function () {
      this.timeout(120000);
      const doc = await vscode.workspace.openNotebookDocument(vscode.Uri.file(fixture));
      await vscode.window.showNotebookDocument(doc);

      await vscode.commands.executeCommand("notebook.cell.execute", {
        ranges: [{ start: 1, end: 2 }],
        document: doc.uri,
      });

      // Poll until the execution has produced output.
      const cell = doc.cellAt(1);
      for (let i = 0; i < 60 && cell.outputs.length === 0; i++) {
        await new Promise((r) => setTimeout(r, 500));
      }
      const text = outputText(cell);
      assert.match(text, /hello from fsi/);
      assert.match(text, /2/);
    },
  );

  (hasWorker ? it : it.skip)(
    "renders a table-shaped result as a text grid",
    async function () {
      this.timeout(120000);
      const doc = await vscode.workspace.openNotebookDocument(vscode.Uri.file(fixture));
      await vscode.window.showNotebookDocument(doc);

      await vscode.commands.executeCommand("notebook.cell.execute", {
        ranges: [{ start: 2, end: 3 }],
        document: doc.uri,
      });

      const cell = doc.cellAt(2);
      for (let i = 0; i < 60 && cell.outputs.length === 0; i++) {
        await new Promise((r) => setTimeout(r, 500));
      }
      const text = outputText(cell);
      assert.match(text, /\(3 rows · \d+ms\)/);
    },
  );

  (hasWorker ? it : it.skip)("restarts the session without error", async function () {
    this.timeout(60000);
    const doc = await vscode.workspace.openNotebookDocument(vscode.Uri.file(fixture));
    await vscode.window.showNotebookDocument(doc);
    await vscode.commands.executeCommand("fs-query-studio.restartKernel");
  });
});
```

- [ ] **Step 3: Compile the integration specs**

Run: `cd editors/vscode && npm run pretest:vscode`
Expected: `tsc -p tsconfig.integration.json` clean; `out/test/integration/notebook.test.js` exists.

- [ ] **Step 4: Run the extension-host suite**

Run: `cd editors/vscode && npm run test:vscode`
Expected: PASS. Without a `dotnet publish`ed worker the three worker-backed specs skip (green); the "opens an .fsqnb file" spec always runs. With the bundled worker present, all run.

Note: if `openNotebookDocument` + `notebook.cell.execute` proves flaky in the headless host, fall back to driving the controller directly — `vscode.extensions.getExtension(...).exports` is not set, so instead export a test hook from `extension.ts` (`export const _test = { getController: () => controller }`) guarded behind `process.env.FSQS_TEST`. Only add this if the command-based approach fails; keep the DEVLOG note accurate about which path shipped.

- [ ] **Step 5: DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-05 — F2.8: extension-host integration test for the notebook

`test/integration/notebook.test.ts` + `fixtures/sample.fsqnb`: a real downloaded VS Code opens the fixture as an `fsquery-notebook` (asserts type, cell count, kinds, `fsharp` language), then — only when a bundled worker dll is present — runs cell 2 and asserts captured stdout + tail value, runs cell 3 and asserts the `(3 rows · Nms)` text-grid footer, and runs `fs-query-studio.restartKernel`. Worker-backed specs skip green with no `dotnet publish`, matching the F1 pattern.
```

- [ ] **Step 6: Commit**

```bash
git add editors/vscode/test/integration/notebook.test.ts editors/vscode/test/integration/fixtures/sample.fsqnb docs/DEVLOG.md
git commit -m "test(vscode): extension-host integration test for the .fsqnb notebook"
```

---

### Task 9: F2 phase close-out

**Files:**
- Modify: `docs/DEVLOG.md`
- Modify: `docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md` (rollout table only)

- [ ] **Step 1: Run every check green**

Run, from `editors/vscode`:
```bash
npm run compile
npm test
npm run test:vscode
```
Expected: `tsc --noEmit` clean; esbuild bundle written; full vitest suite green; extension-host suite green (worker specs skip without a publish, run with one).

- [ ] **Step 2: Optional end-to-end confirmation with a real worker**

If a worker publish is available (or produce one: `dotnet publish src/FSharpQueryStudio.Worker -c Release -o editors/vscode/dist/worker`), re-run `npm run test:vscode` and confirm the worker-backed specs execute rather than skip. Record the outcome in the close-out DEVLOG entry.

- [ ] **Step 3: Mark F2 done in the migration design's rollout table**

In `docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md`, the "Rollout — phased" table, change the **F2 Minimal notebook** row's "Delivers" cell to note completion, matching how the F1 row reads after F1 closed (a leading `✅` or `(done)` marker consistent with that file's existing convention — check the F1 row and follow it).

- [ ] **Step 4: Close-out DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-05 — F2 complete: minimal `.fsqnb` notebook

`.fsqnb` files open, edit, save, and run in VS Code. `FsqnbSerializer` (pure `notebook/fsqnb.ts` codec underneath) handles the version-1 JSON format, never persisting outputs. `createFsQueryController` ("F# Query Studio" kernel, id `fs-query-studio-kernel`) runs code cells sequentially through the existing worker's `execution/evaluate` in Persistent mode, `sessionId = documentUri = notebook uri` — one FSI session per notebook. Cell output comes from the single response envelope (`notebook/result.ts` maps stdout/stderr + value; Table results render as an aligned text grid with a `(N rows · Mms)` footer and an F3-renderer note — no renderer yet). Interrupt → `execution/cancel` via the F1 `WorkerClient.cancel`; `fs-query-studio.restartKernel` → `session/reset`. The worker runs as one shared window-lifetime process behind `worker/session.ts`, gated by a `worker/health` version check that collapses every startup failure into a typed `WorkerUnavailableError` surfaced as cell error output. New: NN unit tests + 1 extension-host integration spec (open + run + table + restart). The Avalonia app is untouched; `dist/worker` packaging and CI are unchanged from F1.
```
(Replace `NN` with the actual `npm test` count.)

- [ ] **Step 5: Commit**

```bash
git add docs/DEVLOG.md docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md
git commit -m "docs: F2 minimal notebook phase complete"
```

---

## Self-Review

**1. Spec coverage** (F2 row + referenced sections of the migration design):

| Spec item | Task |
|---|---|
| `.fsqnb` serializer, own JSON schema, outputs never persisted | 2 (codec), 3 (`NotebookSerializer`) |
| `NotebookController` = thin front end over `execution/evaluate` in Persistent mode | 6 |
| `SessionId` = notebook URI isolates FSI sessions | 6 (`sessionId`/`documentUri` = `notebook.uri.toString()`), asserted in `controller.test.ts` |
| stdout/stderr from evaluation shown in the cell | 4 (`mapEnvelope` output lines), 6 (wired), 8 (asserted) |
| Tabular result shown as **text** (no renderer) | 4 (`formatTextTable`), user-confirmed "text table + metadata" |
| `interruptHandler` → `execution/cancel` | 6 (`interruptHandler` → `WorkerClient.cancel`) |
| "Restart Kernel" → `session/reset` | 6 (`restartKernel`), 7 (command) |
| Version negotiation at activation, kernel refuses on mismatch | 5 (`resolveWorkerSession` health gate → `version-unsupported`), 6 (surfaced as cell error) |
| Worker crash → fail pending, restart on next run, session-lost note | 6 (`worker/crashed` branch); relies on F1 `WorkerClient` crash handling |
| `dotnet` missing / too old → actionable, notebook still opens, kernel unavailable | 5 (`dotnet-missing`/`dotnet-old`), 6 (per-cell error, no throw) |
| One worker per window; evaluations serialize | 5 (singleton), 6 (sequential `for` loop) |
| Run All stops on a failing cell | 6 (`break` on error result/frame) |
| `package.json` contributes notebooks + activation event | 7 |
| Testing: serializer round-trip; controller cell-concat/offset N/A for F2; open+run+mime; cancel/restart | 2, 3, 6 (unit), 8 (extension host) |
| DEVLOG entry per phase | every task + Task 9 |
| Avalonia intact | no `src/` changes in any task |

No F2 spec item is left without a task.

**2. Placeholder scan:** No "TBD"/"handle edge cases"/"similar to Task N". Every code step carries full source. `NN`/`NimN` in Task 9's DEVLOG text is an explicit "replace with the real count" instruction, not a code placeholder. Task 8 Step 4's fallback hook is spelled out, not deferred.

**3. Type consistency:**
- `FSQNB_NOTEBOOK_TYPE` (serializer.ts) — reused by name in controller.ts and extension.ts.
- `mapEnvelope` / `OutputItemSpec` / `formatTextTable` (result.ts) — signatures match their use in controller.ts (`toOutputItem` switch covers exactly `stdout|stderr|text|error`).
- `WorkerLike` (controller.ts) has `send` + `cancel`; the real `WorkerClient` (F1) exposes both with matching signatures; `session.ts` returns `client: WorkerClient`, so `getWorker` in extension.ts type-checks against `ControllerDeps.getWorker`.
- `WorkerUnavailableError` (session.ts) has `userMessage`; controller.ts and extension.ts read `.userMessage` with an `?? (err as Error).message` fallback, so the duck-typed access is safe.
- `readUnionTag` / `SESSION_MODE_PERSISTENT` / `EvaluateParams` / `ResultEnvelope` / `SessionResetParams` (protocol.ts, Task 1) — consumed unchanged in result.ts and controller.ts.
- `resolveDotnet` option shape `{ env }` matches F1 `dotnet.ts`; `resolveWorkerDll(root, env)` arity matches F1 `worker-path.ts`.
- vscode-stub additions across Tasks 3, 6, 7 are cumulative and each added to the default export; `npm test` at the end of Tasks 3, 6, 7 guards against a regression.

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-09-05-vscode-extension-f2-notebook.md`. Two execution options:

1. **Subagent-Driven (recommended)** — a fresh subagent per task, review between tasks, fast iteration.
2. **Inline Execution** — execute tasks in this session with `executing-plans`, batched with review checkpoints.

Which approach?
