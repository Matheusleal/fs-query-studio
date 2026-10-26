# VS Code Extension — F1 Scaffold Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the `editors/vscode/` TypeScript project with a working, tested worker client that can spawn the existing F# worker, complete a `worker/health` handshake, and negotiate protocol version — no notebook UI yet.

**Architecture:** A new npm project under `editors/vscode/` compiled by `tsc` and bundled by `esbuild`. A hand-written mirror of the worker's NDJSON protocol frames in TypeScript. A `WorkerClient` class that is a direct port of `src/FSharpQueryStudio.App/WorkerClient.fs` — spawn `dotnet <worker>.dll`, correlate responses to requests by `id`, tolerate out-of-order responses, restart the process on unexpected exit, serialize stdin writes, and support cooperative-then-forced cancellation. `activate()` wires `resolveDotnet` + `resolveWorkerDll` + `WorkerClient` and exposes one command that runs the health handshake and logs the result to an `OutputChannel`.

**Tech Stack:** TypeScript 5, Node 24 (`node:child_process`, `node:stream`), `esbuild` (bundle), `vitest` (unit tests, with `vscode` stubbed), `@types/vscode`. Tasks 1–12 use no real VS Code; Task 13 (added during execution) brings in `@vscode/test-cli` + `@vscode/test-electron` for extension-host tests against a real downloaded VS Code.

**Spec:** `docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md`

## Global Constraints

- **.NET floor:** worker requires .NET **10** on `PATH`. `resolveDotnet` rejects anything below major version 10.
- **Node engine:** development on Node 24; `package.json` `engines.node` = `">=20"`.
- **VS Code engine:** `package.json` `engines.vscode` = `"^1.90.0"`.
- **Extension identity (placeholder until F6):** `name` = `fs-query-studio`, `publisher` = `fs-query-studio`, `displayName` = `F# Query Studio`.
- **Command id:** `fs-query-studio.checkWorker`.
- **Activation:** `activationEvents` = `["onCommand:fs-query-studio.checkWorker"]` only — no `onStartupFinished`, no always-on activation.
- **Worker frames are camelCase on the wire** (`{ id, method, params }` / `{ id, result?, error? }`, error = `{ code, message }`) — the F# side sets `JsonNamingPolicy.CamelCase`.
- **Worker stdout carries NDJSON frames only** — Serilog writes to a file sink and stderr, never stdout. One JSON document per `\n`-delimited line, UTF-8.
- **Bundled worker location:** `editors/vscode/dist/worker/FSharpQueryStudio.Worker.dll` (produced by `dotnet publish`). Dev override: `FSQS_WORKER_DLL` env var.
- **TDD:** every task writes the failing test first. **Commit after every task.** Add a one-line `docs/DEVLOG.md` entry as the second-to-last step of every task.
- All new source lives under `editors/vscode/`. Do not modify any `src/` F# project in this phase.

---

### Task 1: npm project scaffold

**Files:**
- Create: `editors/vscode/package.json`
- Create: `editors/vscode/tsconfig.json`
- Create: `editors/vscode/.gitignore`
- Create: `editors/vscode/.vscodeignore`
- Create: `editors/vscode/src/extension.ts`
- Create: `editors/vscode/esbuild.mjs`
- Create: `editors/vscode/vitest.config.ts`
- Create: `editors/vscode/test/vscode-stub.ts`
- Modify: `.gitignore` (repo root — append the `editors/vscode` ignores)

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `editors/vscode/src/extension.ts` exports `export function activate(context: vscode.ExtensionContext): Promise<void>` and `export function deactivate(): Promise<void>` (no-op bodies in this task).
  - npm scripts: `compile` (`tsc --noEmit` + `node esbuild.mjs`), `test` (`vitest run`), `watch` (`node esbuild.mjs --watch`).
  - `test/vscode-stub.ts` default-exports an object usable as the `vscode` module in unit tests; vitest aliases `vscode` to it.

- [x] **Step 1: Write `editors/vscode/package.json`**

```json
{
  "name": "fs-query-studio",
  "publisher": "fs-query-studio",
  "displayName": "F# Query Studio",
  "description": "Write, analyze, and run F# query scripts against SQL, files, and REST APIs.",
  "version": "0.0.1",
  "engines": {
    "vscode": "^1.90.0",
    "node": ">=20"
  },
  "main": "./dist/extension.js",
  "activationEvents": [
    "onCommand:fs-query-studio.checkWorker"
  ],
  "contributes": {
    "commands": [
      {
        "command": "fs-query-studio.checkWorker",
        "title": "F# Query Studio: Check Worker"
      }
    ]
  },
  "scripts": {
    "compile": "tsc --noEmit && node esbuild.mjs",
    "watch": "node esbuild.mjs --watch",
    "test": "vitest run"
  },
  "devDependencies": {
    "@types/node": "^20.14.0",
    "@types/vscode": "^1.90.0",
    "esbuild": "^0.23.0",
    "typescript": "^5.5.0",
    "vitest": "^2.0.0"
  }
}
```

- [x] **Step 2: Write `editors/vscode/tsconfig.json`**

```json
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "Node16",
    "moduleResolution": "Node16",
    "lib": ["ES2022"],
    "strict": true,
    "esModuleInterop": true,
    "skipLibCheck": true,
    "forceConsistentCasingInFileNames": true,
    "types": ["node"],
    "outDir": "out"
  },
  "include": ["src", "test"]
}
```

- [x] **Step 3: Write `editors/vscode/esbuild.mjs`**

```js
import * as esbuild from "esbuild";

const watch = process.argv.includes("--watch");

const options = {
  entryPoints: ["src/extension.ts"],
  bundle: true,
  platform: "node",
  format: "cjs",
  target: "node20",
  outfile: "dist/extension.js",
  external: ["vscode"],
  sourcemap: true,
  logLevel: "info",
};

if (watch) {
  const ctx = await esbuild.context(options);
  await ctx.watch();
} else {
  await esbuild.build(options);
}
```

- [x] **Step 4: Write `editors/vscode/vitest.config.ts`**

```ts
import { defineConfig } from "vitest/config";
import * as path from "node:path";

export default defineConfig({
  test: {
    include: ["src/**/*.test.ts"],
    environment: "node",
  },
  resolve: {
    alias: {
      vscode: path.resolve(__dirname, "test/vscode-stub.ts"),
    },
  },
});
```

- [x] **Step 5: Write `editors/vscode/test/vscode-stub.ts`**

```ts
// Minimal stand-in for the `vscode` module in unit tests. Only the surface
// the extension actually touches is implemented; extend as later tasks need.
type Disposable = { dispose: () => void };

export const window = {
  createOutputChannel(_name: string) {
    const lines: string[] = [];
    return {
      lines,
      appendLine: (line: string) => lines.push(line),
      show: () => {},
      dispose: () => {},
    };
  },
  showErrorMessage: async (_message: string, ..._items: string[]) => undefined as string | undefined,
  showInformationMessage: async (_message: string, ..._items: string[]) => undefined as string | undefined,
};

export const commands = {
  _handlers: new Map<string, (...args: unknown[]) => unknown>(),
  registerCommand(id: string, handler: (...args: unknown[]) => unknown): Disposable {
    this._handlers.set(id, handler);
    return { dispose: () => this._handlers.delete(id) };
  },
  async executeCommand(id: string, ...args: unknown[]) {
    return this._handlers.get(id)?.(...args);
  },
};

export const env = { openExternal: async (_uri: unknown) => true };
export const Uri = { parse: (s: string) => ({ toString: () => s }) };

export default { window, commands, env, Uri };
```

- [x] **Step 6: Write `editors/vscode/src/extension.ts` (stub)**

```ts
import * as vscode from "vscode";

export async function activate(_context: vscode.ExtensionContext): Promise<void> {
  // Wiring added in Task 9.
}

export async function deactivate(): Promise<void> {
  // Teardown added in Task 9.
}
```

- [x] **Step 7: Write `editors/vscode/.gitignore`**

```gitignore
node_modules/
dist/
out/
*.vsix
```

- [x] **Step 8: Write `editors/vscode/.vscodeignore`**

```
.vscode/**
src/**
test/**
out/**
node_modules/**
esbuild.mjs
vitest.config.ts
tsconfig.json
**/*.test.ts
**/*.map
```

- [x] **Step 9: Append repo-root `.gitignore` entries**

Add these lines to the end of `.gitignore` at the repo root:

```gitignore

# VS Code extension (editors/vscode)
editors/vscode/node_modules/
editors/vscode/dist/
editors/vscode/out/
editors/vscode/*.vsix
```

- [x] **Step 10: Install and compile**

Run:
```bash
cd editors/vscode && npm install && npm run compile
```
Expected: `npm install` completes; `tsc --noEmit` reports no errors; `esbuild` writes `dist/extension.js`.

- [x] **Step 11: Run the test harness (no tests yet)**

Run: `cd editors/vscode && npm test`
Expected: vitest exits 0 with "No test files found" (not an error — the harness is wired).

- [x] **Step 12: DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-03 — F1.1: editors/vscode/ npm project scaffold

Created the TypeScript project for the VS Code extension: package.json (command `fs-query-studio.checkWorker`, command-only activation), tsconfig (Node16 modules, strict), esbuild bundle to `dist/extension.js`, vitest with a `vscode` module stub. No behavior yet — activate/deactivate are no-ops.
```

- [x] **Step 13: Commit**

```bash
git add editors/vscode .gitignore docs/DEVLOG.md
git commit -m "feat(vscode): scaffold editors/vscode TypeScript project"
```

---

### Task 2: Protocol types and version negotiation

**Files:**
- Create: `editors/vscode/src/protocol.ts`
- Test: `editors/vscode/src/protocol.test.ts`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `interface WorkerRequestFrame { id: string; method: string; params: unknown }`
  - `interface WorkerErrorInfo { code: string; message: string }`
  - `interface WorkerResponseFrame { id: string; result?: unknown; error?: WorkerErrorInfo }`
  - `interface HealthInfo { version: string; capabilities: string[] }`
  - `const SUPPORTED_WORKER_MAJOR = 0` and `const SUPPORTED_WORKER_MAJOR_MAX = 1`
  - `function isWorkerVersionSupported(version: string): boolean` — parses the leading integer of a dotted version string; supported when `SUPPORTED_WORKER_MAJOR <= major <= SUPPORTED_WORKER_MAJOR_MAX`. Non-numeric / empty → `false`.
  - `function newRequestId(): string` — returns `crypto.randomUUID()`.

- [x] **Step 1: Write the failing test**

`editors/vscode/src/protocol.test.ts`:
```ts
import { describe, it, expect } from "vitest";
import { isWorkerVersionSupported, newRequestId } from "./protocol";

describe("isWorkerVersionSupported", () => {
  it("accepts a v0 worker", () => {
    expect(isWorkerVersionSupported("0.0.0")).toBe(true);
  });
  it("accepts a v1 worker", () => {
    expect(isWorkerVersionSupported("1.4.2.0")).toBe(true);
  });
  it("rejects a v2 worker", () => {
    expect(isWorkerVersionSupported("2.0.0")).toBe(false);
  });
  it("rejects a non-numeric version", () => {
    expect(isWorkerVersionSupported("")).toBe(false);
    expect(isWorkerVersionSupported("nope")).toBe(false);
  });
});

describe("newRequestId", () => {
  it("returns distinct values", () => {
    expect(newRequestId()).not.toEqual(newRequestId());
  });
});
```

- [x] **Step 2: Run test to verify it fails**

Run: `cd editors/vscode && npx vitest run src/protocol.test.ts`
Expected: FAIL — `Cannot find module './protocol'`.

- [x] **Step 3: Write `editors/vscode/src/protocol.ts`**

```ts
import { randomUUID } from "node:crypto";

export interface WorkerRequestFrame {
  id: string;
  method: string;
  params: unknown;
}

export interface WorkerErrorInfo {
  code: string;
  message: string;
}

export interface WorkerResponseFrame {
  id: string;
  result?: unknown;
  error?: WorkerErrorInfo;
}

export interface HealthInfo {
  version: string;
  capabilities: string[];
}

export const SUPPORTED_WORKER_MAJOR = 0;
export const SUPPORTED_WORKER_MAJOR_MAX = 1;

export function isWorkerVersionSupported(version: string): boolean {
  const match = /^(\d+)/.exec(version.trim());
  if (!match) {
    return false;
  }
  const major = Number(match[1]);
  return major >= SUPPORTED_WORKER_MAJOR && major <= SUPPORTED_WORKER_MAJOR_MAX;
}

export function newRequestId(): string {
  return randomUUID();
}
```

- [x] **Step 4: Run test to verify it passes**

Run: `cd editors/vscode && npx vitest run src/protocol.test.ts`
Expected: PASS (6 assertions).

- [x] **Step 5: DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-03 — F1.2: TypeScript mirror of the worker protocol frames

`protocol.ts`: hand-written `WorkerRequestFrame`/`WorkerResponseFrame`/`WorkerErrorInfo`/`HealthInfo` (camelCase, matching the F# `JsonNamingPolicy.CamelCase`), plus `isWorkerVersionSupported` (accepts worker major 0–1) and `newRequestId`.
```

- [x] **Step 6: Commit**

```bash
git add editors/vscode/src/protocol.ts editors/vscode/src/protocol.test.ts docs/DEVLOG.md
git commit -m "feat(vscode): protocol frame types and worker version check"
```

---

### Task 3: NDJSON framing over Node streams

**Files:**
- Create: `editors/vscode/src/worker/ndjson.ts`
- Test: `editors/vscode/src/worker/ndjson.test.ts`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `function writeFrame(stream: NodeJS.WritableStream, frame: unknown): Promise<void>` — JSON-stringifies `frame`, writes it followed by `"\n"`, resolves when the write is flushed to the stream's buffer.
  - `function readFrames(stream: NodeJS.ReadableStream, handlers: { onFrame: (frame: unknown) => void; onEnd: () => void }): void` — buffers incoming UTF-8 data, splits on `"\n"`, `JSON.parse`s each complete line and calls `onFrame`; calls `onEnd` once on stream `end`/`close`. A blank line is skipped. A line that fails `JSON.parse` is passed to `onFrame` as `{ __parseError: true, raw: string }` (never throws out of the reader).

- [x] **Step 1: Write the failing test**

`editors/vscode/src/worker/ndjson.test.ts`:
```ts
import { describe, it, expect } from "vitest";
import { PassThrough } from "node:stream";
import { writeFrame, readFrames } from "./ndjson";

describe("writeFrame / readFrames", () => {
  it("round-trips multiple frames", async () => {
    const stream = new PassThrough();
    const got: unknown[] = [];
    let ended = false;
    readFrames(stream, { onFrame: (f) => got.push(f), onEnd: () => (ended = true) });

    await writeFrame(stream, { id: "1", method: "a", params: {} });
    await writeFrame(stream, { id: "2", method: "b", params: { x: 1 } });
    stream.end();
    await new Promise((r) => setTimeout(r, 10));

    expect(got).toEqual([
      { id: "1", method: "a", params: {} },
      { id: "2", method: "b", params: { x: 1 } },
    ]);
    expect(ended).toBe(true);
  });

  it("reassembles a frame split across two chunks", async () => {
    const stream = new PassThrough();
    const got: unknown[] = [];
    readFrames(stream, { onFrame: (f) => got.push(f), onEnd: () => {} });

    stream.write('{"id":"1",');
    stream.write('"method":"a","params":{}}\n');
    await new Promise((r) => setTimeout(r, 10));

    expect(got).toEqual([{ id: "1", method: "a", params: {} }]);
  });

  it("surfaces an unparseable line without throwing", async () => {
    const stream = new PassThrough();
    const got: unknown[] = [];
    readFrames(stream, { onFrame: (f) => got.push(f), onEnd: () => {} });

    stream.write("not json\n");
    await new Promise((r) => setTimeout(r, 10));

    expect(got).toEqual([{ __parseError: true, raw: "not json" }]);
  });
});
```

- [x] **Step 2: Run test to verify it fails**

Run: `cd editors/vscode && npx vitest run src/worker/ndjson.test.ts`
Expected: FAIL — `Cannot find module './ndjson'`.

- [x] **Step 3: Write `editors/vscode/src/worker/ndjson.ts`**

```ts
export function writeFrame(stream: NodeJS.WritableStream, frame: unknown): Promise<void> {
  return new Promise((resolve, reject) => {
    const line = JSON.stringify(frame) + "\n";
    stream.write(line, "utf8", (err) => (err ? reject(err) : resolve()));
  });
}

export interface FrameHandlers {
  onFrame: (frame: unknown) => void;
  onEnd: () => void;
}

export function readFrames(stream: NodeJS.ReadableStream, handlers: FrameHandlers): void {
  let buffer = "";
  let ended = false;

  const finish = () => {
    if (ended) {
      return;
    }
    ended = true;
    handlers.onEnd();
  };

  stream.setEncoding("utf8");
  stream.on("data", (chunk: string) => {
    buffer += chunk;
    let newlineIndex = buffer.indexOf("\n");
    while (newlineIndex !== -1) {
      const line = buffer.slice(0, newlineIndex).trim();
      buffer = buffer.slice(newlineIndex + 1);
      if (line.length > 0) {
        try {
          handlers.onFrame(JSON.parse(line));
        } catch {
          handlers.onFrame({ __parseError: true, raw: line });
        }
      }
      newlineIndex = buffer.indexOf("\n");
    }
  });
  stream.on("end", finish);
  stream.on("close", finish);
}
```

- [x] **Step 4: Run test to verify it passes**

Run: `cd editors/vscode && npx vitest run src/worker/ndjson.test.ts`
Expected: PASS (3 tests).

- [x] **Step 5: DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-03 — F1.3: NDJSON framing for the worker transport (TS side)

`worker/ndjson.ts`: `writeFrame` (JSON + `\n`) and `readFrames` (buffered split on `\n`, `JSON.parse` per line, chunk reassembly, unparseable lines surfaced as `{__parseError}` rather than thrown). Mirrors `src/FSharpQueryStudio.Worker/NdjsonStream.fs`.
```

- [x] **Step 6: Commit**

```bash
git add editors/vscode/src/worker/ndjson.ts editors/vscode/src/worker/ndjson.test.ts docs/DEVLOG.md
git commit -m "feat(vscode): NDJSON frame reader/writer for the worker transport"
```

---

### Task 4: Resolve the `dotnet` executable and enforce the .NET 10 floor

**Files:**
- Create: `editors/vscode/src/worker/dotnet.ts`
- Test: `editors/vscode/src/worker/dotnet.test.ts`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `interface DotnetInfo { command: string; version: string }`
  - `type RunCommand = (command: string, args: string[]) => Promise<{ stdout: string; code: number }>`
  - `class DotnetNotFoundError extends Error` — has `readonly userMessage: string` and `readonly kind = "not-found"`.
  - `class DotnetTooOldError extends Error` — has `readonly userMessage: string`, `readonly kind = "too-old"`, `readonly foundVersion: string`.
  - `const MINIMUM_DOTNET_MAJOR = 10`
  - `async function resolveDotnet(opts?: { run?: RunCommand; env?: NodeJS.ProcessEnv }): Promise<DotnetInfo>` — runs `<command> --version`, where `<command>` is `${env.DOTNET_ROOT}/dotnet` if `DOTNET_ROOT` is set and that file exists, else `"dotnet"`. Throws `DotnetNotFoundError` if the process cannot be spawned (`run` rejects) or exits non-zero. Parses the leading integer of stdout; throws `DotnetTooOldError` if `< MINIMUM_DOTNET_MAJOR`. Otherwise resolves `{ command, version: stdout.trim() }`.

- [x] **Step 1: Write the failing test**

`editors/vscode/src/worker/dotnet.test.ts`:
```ts
import { describe, it, expect } from "vitest";
import { resolveDotnet, DotnetNotFoundError, DotnetTooOldError } from "./dotnet";

const ok = (version: string) => async () => ({ stdout: version, code: 0 });

describe("resolveDotnet", () => {
  it("resolves when dotnet 10 is present", async () => {
    const info = await resolveDotnet({ run: ok("10.0.100\n"), env: {} });
    expect(info).toEqual({ command: "dotnet", version: "10.0.100" });
  });

  it("throws DotnetTooOldError for dotnet 8", async () => {
    await expect(resolveDotnet({ run: ok("8.0.404"), env: {} })).rejects.toBeInstanceOf(
      DotnetTooOldError,
    );
  });

  it("DotnetTooOldError carries the found version and a user message", async () => {
    const err = await resolveDotnet({ run: ok("8.0.404"), env: {} }).catch((e) => e);
    expect(err.foundVersion).toBe("8.0.404");
    expect(err.userMessage).toMatch(/10/);
  });

  it("throws DotnetNotFoundError when the process cannot start", async () => {
    const run = async () => {
      throw new Error("ENOENT");
    };
    await expect(resolveDotnet({ run, env: {} })).rejects.toBeInstanceOf(DotnetNotFoundError);
  });

  it("throws DotnetNotFoundError when --version exits non-zero", async () => {
    const run = async () => ({ stdout: "", code: 1 });
    await expect(resolveDotnet({ run, env: {} })).rejects.toBeInstanceOf(DotnetNotFoundError);
  });
});
```

- [x] **Step 2: Run test to verify it fails**

Run: `cd editors/vscode && npx vitest run src/worker/dotnet.test.ts`
Expected: FAIL — `Cannot find module './dotnet'`.

- [x] **Step 3: Write `editors/vscode/src/worker/dotnet.ts`**

```ts
import { execFile } from "node:child_process";
import * as fs from "node:fs";
import * as path from "node:path";

export const MINIMUM_DOTNET_MAJOR = 10;

export interface DotnetInfo {
  command: string;
  version: string;
}

export type RunCommand = (
  command: string,
  args: string[],
) => Promise<{ stdout: string; code: number }>;

const defaultRun: RunCommand = (command, args) =>
  new Promise((resolve, reject) => {
    execFile(command, args, { timeout: 10_000 }, (err, stdout) => {
      if (err && (err as NodeJS.ErrnoException).code === "ENOENT") {
        reject(err);
        return;
      }
      resolve({ stdout: stdout ?? "", code: err ? (err.code as number) ?? 1 : 0 });
    });
  });

export class DotnetNotFoundError extends Error {
  readonly kind = "not-found";
  readonly userMessage =
    "The .NET SDK was not found on PATH. F# Query Studio needs .NET 10 or newer to run its worker.";
  constructor(cause?: unknown) {
    super("dotnet not found");
    this.cause = cause;
  }
}

export class DotnetTooOldError extends Error {
  readonly kind = "too-old";
  readonly userMessage: string;
  constructor(readonly foundVersion: string) {
    super(`dotnet ${foundVersion} is older than ${MINIMUM_DOTNET_MAJOR}`);
    this.userMessage = `F# Query Studio needs .NET ${MINIMUM_DOTNET_MAJOR} or newer; found ${foundVersion}.`;
  }
}

function pickCommand(env: NodeJS.ProcessEnv): string {
  const root = env.DOTNET_ROOT;
  if (root) {
    const candidate = path.join(root, process.platform === "win32" ? "dotnet.exe" : "dotnet");
    if (fs.existsSync(candidate)) {
      return candidate;
    }
  }
  return "dotnet";
}

export async function resolveDotnet(opts?: {
  run?: RunCommand;
  env?: NodeJS.ProcessEnv;
}): Promise<DotnetInfo> {
  const run = opts?.run ?? defaultRun;
  const env = opts?.env ?? process.env;
  const command = pickCommand(env);

  let result: { stdout: string; code: number };
  try {
    result = await run(command, ["--version"]);
  } catch (cause) {
    throw new DotnetNotFoundError(cause);
  }
  if (result.code !== 0) {
    throw new DotnetNotFoundError();
  }

  const version = result.stdout.trim();
  const match = /^(\d+)/.exec(version);
  const major = match ? Number(match[1]) : NaN;
  if (!Number.isFinite(major) || major < MINIMUM_DOTNET_MAJOR) {
    throw new DotnetTooOldError(version || "unknown");
  }

  return { command, version };
}
```

- [x] **Step 4: Run test to verify it passes**

Run: `cd editors/vscode && npx vitest run src/worker/dotnet.test.ts`
Expected: PASS (5 tests).

- [x] **Step 5: DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-03 — F1.4: dotnet resolution with a .NET 10 floor

`worker/dotnet.ts`: `resolveDotnet` runs `dotnet --version` (honouring `DOTNET_ROOT`), throwing `DotnetNotFoundError` (spawn failure / non-zero exit) or `DotnetTooOldError` (major < 10). Both errors carry a `userMessage` for a notification. Injectable `run` for tests.
```

- [x] **Step 6: Commit**

```bash
git add editors/vscode/src/worker/dotnet.ts editors/vscode/src/worker/dotnet.test.ts docs/DEVLOG.md
git commit -m "feat(vscode): resolve dotnet and enforce the .NET 10 floor"
```

---

### Task 5: Resolve the worker DLL path

**Files:**
- Create: `editors/vscode/src/worker/worker-path.ts`
- Test: `editors/vscode/src/worker/worker-path.test.ts`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `class WorkerDllNotFoundError extends Error` — `readonly userMessage: string`, lists the paths that were checked.
  - `function resolveWorkerDll(extensionRoot: string, env?: NodeJS.ProcessEnv): string` — returns the first existing path of: (1) `env.FSQS_WORKER_DLL` if set, (2) `${extensionRoot}/dist/worker/FSharpQueryStudio.Worker.dll`. Throws `WorkerDllNotFoundError` if none exist.

- [x] **Step 1: Write the failing test**

`editors/vscode/src/worker/worker-path.test.ts`:
```ts
import { describe, it, expect, beforeEach, afterEach } from "vitest";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { resolveWorkerDll, WorkerDllNotFoundError } from "./worker-path";

let root: string;

beforeEach(() => {
  root = fs.mkdtempSync(path.join(os.tmpdir(), "fsqs-"));
});
afterEach(() => {
  fs.rmSync(root, { recursive: true, force: true });
});

describe("resolveWorkerDll", () => {
  it("finds the bundled dll under dist/worker", () => {
    const bundled = path.join(root, "dist", "worker");
    fs.mkdirSync(bundled, { recursive: true });
    const dll = path.join(bundled, "FSharpQueryStudio.Worker.dll");
    fs.writeFileSync(dll, "");
    expect(resolveWorkerDll(root, {})).toBe(dll);
  });

  it("prefers the FSQS_WORKER_DLL override", () => {
    const override = path.join(root, "custom.dll");
    fs.writeFileSync(override, "");
    expect(resolveWorkerDll(root, { FSQS_WORKER_DLL: override })).toBe(override);
  });

  it("throws WorkerDllNotFoundError when nothing exists", () => {
    expect(() => resolveWorkerDll(root, {})).toThrow(WorkerDllNotFoundError);
  });
});
```

- [x] **Step 2: Run test to verify it fails**

Run: `cd editors/vscode && npx vitest run src/worker/worker-path.test.ts`
Expected: FAIL — `Cannot find module './worker-path'`.

- [x] **Step 3: Write `editors/vscode/src/worker/worker-path.ts`**

```ts
import * as fs from "node:fs";
import * as path from "node:path";

export class WorkerDllNotFoundError extends Error {
  readonly userMessage: string;
  constructor(readonly checked: string[]) {
    super(`worker dll not found; checked: ${checked.join(", ")}`);
    this.userMessage =
      "The F# Query Studio worker was not found. Run `dotnet publish` into editors/vscode/dist/worker, " +
      "or set FSQS_WORKER_DLL to the built FSharpQueryStudio.Worker.dll.";
  }
}

export function resolveWorkerDll(extensionRoot: string, env: NodeJS.ProcessEnv = process.env): string {
  const checked: string[] = [];

  const override = env.FSQS_WORKER_DLL;
  if (override) {
    checked.push(override);
    if (fs.existsSync(override)) {
      return override;
    }
  }

  const bundled = path.join(extensionRoot, "dist", "worker", "FSharpQueryStudio.Worker.dll");
  checked.push(bundled);
  if (fs.existsSync(bundled)) {
    return bundled;
  }

  throw new WorkerDllNotFoundError(checked);
}
```

- [x] **Step 4: Run test to verify it passes**

Run: `cd editors/vscode && npx vitest run src/worker/worker-path.test.ts`
Expected: PASS (3 tests).

- [x] **Step 5: DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-03 — F1.5: worker DLL path resolution

`worker/worker-path.ts`: `resolveWorkerDll` returns `FSQS_WORKER_DLL` (dev override) or the bundled `dist/worker/FSharpQueryStudio.Worker.dll`, throwing `WorkerDllNotFoundError` (with an actionable `userMessage`) if neither exists.
```

- [x] **Step 6: Commit**

```bash
git add editors/vscode/src/worker/worker-path.ts editors/vscode/src/worker/worker-path.test.ts docs/DEVLOG.md
git commit -m "feat(vscode): resolve the bundled or dev-override worker dll path"
```

---

### Task 6: `WorkerClient` — spawn, send, response correlation

**Files:**
- Create: `editors/vscode/src/worker/client.ts`
- Test: `editors/vscode/src/worker/client.test.ts`
- Create: `editors/vscode/test/fake-child.ts`

**Interfaces:**
- Consumes: `writeFrame`, `readFrames` from `./ndjson`; `WorkerRequestFrame`, `WorkerResponseFrame` from `../protocol`.
- Produces:
  - `interface WorkerProcess { stdin: NodeJS.WritableStream; stdout: NodeJS.ReadableStream; stderr: NodeJS.ReadableStream; pid: number | undefined; kill(): void; onExit(cb: () => void): void }`
  - `type SpawnWorker = () => WorkerProcess`
  - `interface WorkerClientOptions { spawn: SpawnWorker }`
  - `class WorkerClient` with:
    - `constructor(options: WorkerClientOptions)`
    - `send(request: WorkerRequestFrame): Promise<WorkerResponseFrame>` — spawns the process on first use (and after an exit), registers `request.id` as pending, serializes the stdin write behind an internal mutex, resolves when a response with the same `id` arrives (order-independent).
    - `get currentPid(): number | undefined`
    - `dispose(): void` — kills the process if running and rejects/settles nothing further.
  - `test/fake-child.ts` exports `createFakeChild()` returning `{ process: WorkerProcess, sentFrames: unknown[], reply(frame: WorkerResponseFrame): void, emitExit(): void, rawStdinLines: string[] }`.

- [x] **Step 1: Write `editors/vscode/test/fake-child.ts`**

```ts
import { PassThrough } from "node:stream";
import { readFrames, writeFrame } from "../src/worker/ndjson";
import type { WorkerProcess } from "../src/worker/client";
import type { WorkerResponseFrame } from "../src/protocol";

export function createFakeChild() {
  const stdin = new PassThrough();
  const stdout = new PassThrough();
  const stderr = new PassThrough();
  const sentFrames: unknown[] = [];
  const rawStdinLines: string[] = [];
  let exitCb: (() => void) | undefined;

  readFrames(stdin, { onFrame: (f) => sentFrames.push(f), onEnd: () => {} });
  stdin.on("data", (chunk: Buffer) => {
    for (const line of chunk.toString("utf8").split("\n")) {
      if (line.trim().length > 0) {
        rawStdinLines.push(line);
      }
    }
  });

  const process: WorkerProcess = {
    stdin,
    stdout,
    stderr,
    pid: 4242,
    kill: () => exitCb?.(),
    onExit: (cb) => {
      exitCb = cb;
    },
  };

  return {
    process,
    sentFrames,
    rawStdinLines,
    reply: (frame: WorkerResponseFrame) => {
      void writeFrame(stdout, frame);
    },
    emitExit: () => {
      stdout.end();
      exitCb?.();
    },
  };
}
```

- [x] **Step 2: Write the failing test**

`editors/vscode/src/worker/client.test.ts`:
```ts
import { describe, it, expect } from "vitest";
import { WorkerClient } from "./client";
import { createFakeChild } from "../../test/fake-child";

describe("WorkerClient send + correlation", () => {
  it("resolves a send with the matching response", async () => {
    const child = createFakeChild();
    const client = new WorkerClient({ spawn: () => child.process });

    const pending = client.send({ id: "req-1", method: "worker/health", params: {} });
    await new Promise((r) => setTimeout(r, 5));
    child.reply({ id: "req-1", result: { version: "0.1.0", capabilities: [] } });

    const response = await pending;
    expect(response).toEqual({ id: "req-1", result: { version: "0.1.0", capabilities: [] } });
  });

  it("matches responses that arrive out of order", async () => {
    const child = createFakeChild();
    const client = new WorkerClient({ spawn: () => child.process });

    const a = client.send({ id: "a", method: "m", params: {} });
    const b = client.send({ id: "b", method: "m", params: {} });
    await new Promise((r) => setTimeout(r, 5));
    child.reply({ id: "b", result: "B" });
    child.reply({ id: "a", result: "A" });

    expect((await a).result).toBe("A");
    expect((await b).result).toBe("B");
  });

  it("writes each request as one whole NDJSON line", async () => {
    const child = createFakeChild();
    const client = new WorkerClient({ spawn: () => child.process });

    client.send({ id: "a", method: "m", params: {} });
    client.send({ id: "b", method: "m", params: {} });
    await new Promise((r) => setTimeout(r, 10));

    expect(child.rawStdinLines).toHaveLength(2);
    expect(JSON.parse(child.rawStdinLines[0]).id).toBe("a");
    expect(JSON.parse(child.rawStdinLines[1]).id).toBe("b");
  });

  it("spawns the process only once for multiple sends", async () => {
    let spawnCount = 0;
    const child = createFakeChild();
    const client = new WorkerClient({
      spawn: () => {
        spawnCount++;
        return child.process;
      },
    });
    client.send({ id: "a", method: "m", params: {} });
    client.send({ id: "b", method: "m", params: {} });
    await new Promise((r) => setTimeout(r, 5));
    expect(spawnCount).toBe(1);
  });
});
```

- [x] **Step 3: Run test to verify it fails**

Run: `cd editors/vscode && npx vitest run src/worker/client.test.ts`
Expected: FAIL — `Cannot find module './client'`.

- [x] **Step 4: Write `editors/vscode/src/worker/client.ts`**

```ts
import { readFrames, writeFrame } from "./ndjson";
import type { WorkerRequestFrame, WorkerResponseFrame } from "../protocol";

export interface WorkerProcess {
  stdin: NodeJS.WritableStream;
  stdout: NodeJS.ReadableStream;
  stderr: NodeJS.ReadableStream;
  pid: number | undefined;
  kill(): void;
  onExit(cb: () => void): void;
}

export type SpawnWorker = () => WorkerProcess;

export interface WorkerClientOptions {
  spawn: SpawnWorker;
}

interface Pending {
  resolve: (frame: WorkerResponseFrame) => void;
}

export class WorkerClient {
  private readonly spawn: SpawnWorker;
  private current: WorkerProcess | undefined;
  private readonly pending = new Map<string, Pending>();
  private writeChain: Promise<void> = Promise.resolve();

  constructor(options: WorkerClientOptions) {
    this.spawn = options.spawn;
  }

  get currentPid(): number | undefined {
    return this.current?.pid;
  }

  send(request: WorkerRequestFrame): Promise<WorkerResponseFrame> {
    const proc = this.ensureProcess();
    return new Promise<WorkerResponseFrame>((resolve) => {
      this.pending.set(request.id, { resolve });
      this.writeChain = this.writeChain.then(() => writeFrame(proc.stdin, request)).catch(() => {
        /* a failed write surfaces as an exit via readFrames onEnd */
      });
    });
  }

  dispose(): void {
    this.current?.kill();
    this.current = undefined;
  }

  protected ensureProcess(): WorkerProcess {
    if (this.current) {
      return this.current;
    }
    const proc = this.spawn();
    this.current = proc;
    readFrames(proc.stdout, {
      onFrame: (frame) => this.onFrame(frame),
      onEnd: () => this.onExit(),
    });
    proc.onExit(() => this.onExit());
    return proc;
  }

  private onFrame(frame: unknown): void {
    const response = frame as WorkerResponseFrame;
    if (!response || typeof response.id !== "string") {
      return;
    }
    const waiter = this.pending.get(response.id);
    if (waiter) {
      this.pending.delete(response.id);
      waiter.resolve(response);
    }
  }

  // Replaced with real crash handling in Task 7.
  protected onExit(): void {
    this.current = undefined;
  }
}
```

- [x] **Step 5: Run test to verify it passes**

Run: `cd editors/vscode && npx vitest run src/worker/client.test.ts`
Expected: PASS (4 tests).

- [x] **Step 6: DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-03 — F1.6: WorkerClient — spawn, send, response correlation

`worker/client.ts`: lazy single spawn, `send` registers the request id as pending and resolves on the matching response frame (order-independent), stdin writes serialized behind a promise chain (the `writeLock` equivalent from `App/WorkerClient.fs`). `SpawnWorker` is injected; `test/fake-child.ts` scripts a process for the unit tests.
```

- [x] **Step 7: Commit**

```bash
git add editors/vscode/src/worker/client.ts editors/vscode/src/worker/client.test.ts editors/vscode/test/fake-child.ts docs/DEVLOG.md
git commit -m "feat(vscode): WorkerClient spawn + send + response correlation"
```

---

### Task 7: `WorkerClient` — crash detection and restart

**Files:**
- Modify: `editors/vscode/src/worker/client.ts`
- Modify: `editors/vscode/src/worker/client.test.ts` (add a `describe` block)

**Interfaces:**
- Consumes: everything from Task 6.
- Produces:
  - On worker exit (`readFrames` `onEnd` or `onExit` callback), every pending request settles with `{ id, error: { code: "worker/crashed", message: "The worker process exited before responding." } }` and the pending map is cleared.
  - The next `send()` after an exit spawns a fresh process.
  - New optional option: `WorkerClientOptions.onCrash?: () => void` — called once per crash, after pending requests are settled.

- [x] **Step 1: Write the failing test (append to `client.test.ts`)**

```ts
import { WorkerClient as WC } from "./client";

describe("WorkerClient crash + restart", () => {
  it("settles pending requests with worker/crashed on exit", async () => {
    const child = createFakeChild();
    const client = new WC({ spawn: () => child.process });

    const pending = client.send({ id: "x", method: "m", params: {} });
    await new Promise((r) => setTimeout(r, 5));
    child.emitExit();

    const response = await pending;
    expect(response).toEqual({
      id: "x",
      error: { code: "worker/crashed", message: "The worker process exited before responding." },
    });
  });

  it("spawns a fresh process on the next send after a crash", async () => {
    let spawnCount = 0;
    const children = [createFakeChild(), createFakeChild()];
    const client = new WC({
      spawn: () => children[spawnCount++].process,
    });

    client.send({ id: "a", method: "m", params: {} });
    await new Promise((r) => setTimeout(r, 5));
    children[0].emitExit();
    await new Promise((r) => setTimeout(r, 5));

    client.send({ id: "b", method: "m", params: {} });
    await new Promise((r) => setTimeout(r, 5));
    expect(spawnCount).toBe(2);
  });

  it("invokes onCrash once", async () => {
    const child = createFakeChild();
    let crashes = 0;
    const client = new WC({ spawn: () => child.process, onCrash: () => crashes++ });
    client.send({ id: "a", method: "m", params: {} });
    await new Promise((r) => setTimeout(r, 5));
    child.emitExit();
    await new Promise((r) => setTimeout(r, 5));
    expect(crashes).toBe(1);
  });
});
```

- [x] **Step 2: Run test to verify it fails**

Run: `cd editors/vscode && npx vitest run src/worker/client.test.ts`
Expected: FAIL — the first new test hangs/fails (pending never settles); `onCrash` is not a known option.

- [x] **Step 3: Update `editors/vscode/src/worker/client.ts`**

Replace the `WorkerClientOptions` interface and the `onExit` method, and add a `crashResponse` helper:

```ts
export interface WorkerClientOptions {
  spawn: SpawnWorker;
  onCrash?: () => void;
}
```

Add a private field in the constructor area:
```ts
  private readonly onCrash?: () => void;
```
and in the constructor body:
```ts
    this.onCrash = options.onCrash;
```

Replace `onExit`:
```ts
  protected onExit(): void {
    if (!this.current) {
      return;
    }
    this.current = undefined;
    const crashed: WorkerResponseFrame[] = [];
    for (const [id, waiter] of this.pending) {
      crashed.push({
        id,
        error: { code: "worker/crashed", message: "The worker process exited before responding." },
      });
      waiter.resolve({
        id,
        error: { code: "worker/crashed", message: "The worker process exited before responding." },
      });
    }
    this.pending.clear();
    this.writeChain = Promise.resolve();
    this.onCrash?.();
  }
```

(The `crashed` array is not strictly required; if you prefer, settle inside the loop only. Keep the loop body's two identical error objects or hoist a local `const err = () => ({ code: ..., message: ... })`.)

- [x] **Step 4: Run test to verify it passes**

Run: `cd editors/vscode && npx vitest run src/worker/client.test.ts`
Expected: PASS (all Task 6 + Task 7 tests).

- [x] **Step 5: DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-03 — F1.7: WorkerClient — crash detection and restart

On worker stdout close / process exit, every pending request settles with a `worker/crashed` error frame and the pending map clears; the next `send` spawns a fresh process. Optional `onCrash` callback. Mirrors the crash-and-restart ownership in `App/WorkerClient.fs` (no automatic retry of the dead request).
```

- [x] **Step 6: Commit**

```bash
git add editors/vscode/src/worker/client.ts editors/vscode/src/worker/client.test.ts docs/DEVLOG.md
git commit -m "feat(vscode): WorkerClient crash detection and restart"
```

---

### Task 8: `WorkerClient` — cooperative-then-forced cancel

**Files:**
- Modify: `editors/vscode/src/worker/client.ts`
- Modify: `editors/vscode/src/worker/client.test.ts` (add a `describe` block)

**Interfaces:**
- Consumes: everything from Tasks 6–7; `newRequestId` from `../protocol`.
- Produces:
  - `cancel(requestId: string, gracePeriodMs: number): Promise<void>` on `WorkerClient` — sends a `{ id: newRequestId(), method: "execution/cancel", params: {} }` request, then waits up to `gracePeriodMs` for the original `requestId` to settle. If it does not, calls `kill()` on the process and settles the original with `{ id: requestId, error: { code: "execution/forciblyStopped", message: "The worker did not stop cooperatively within the grace period and was terminated." } }`. If `requestId` is not pending (already done) it returns after sending the cancel.

- [x] **Step 1: Write the failing test (append to `client.test.ts`)**

```ts
describe("WorkerClient cancel", () => {
  it("does not kill the process when the request settles within the grace period", async () => {
    const child = createFakeChild();
    let killed = false;
    const proc = { ...child.process, kill: () => (killed = true) };
    const client = new WorkerClient({ spawn: () => proc });

    const pending = client.send({ id: "run-1", method: "execution/evaluate", params: {} });
    await new Promise((r) => setTimeout(r, 5));

    const cancelling = client.cancel("run-1", 200);
    child.reply({ id: "run-1", error: { code: "execution/cancelled", message: "Cancelled." } });
    await cancelling;
    await pending;

    expect(killed).toBe(false);
  });

  it("kills the process and force-settles when the grace period elapses", async () => {
    const child = createFakeChild();
    let killed = false;
    const proc = { ...child.process, kill: () => (killed = true) };
    const client = new WorkerClient({ spawn: () => proc });

    const pending = client.send({ id: "run-2", method: "execution/evaluate", params: {} });
    await new Promise((r) => setTimeout(r, 5));

    await client.cancel("run-2", 20);
    const response = await pending;

    expect(killed).toBe(true);
    expect(response.error?.code).toBe("execution/forciblyStopped");
  });

  it("sends an execution/cancel frame", async () => {
    const child = createFakeChild();
    const client = new WorkerClient({ spawn: () => child.process });
    client.send({ id: "run-3", method: "execution/evaluate", params: {} });
    await new Promise((r) => setTimeout(r, 5));
    void client.cancel("run-3", 20);
    await new Promise((r) => setTimeout(r, 5));
    const methods = child.sentFrames.map((f) => (f as { method: string }).method);
    expect(methods).toContain("execution/cancel");
  });
});
```

- [x] **Step 2: Run test to verify it fails**

Run: `cd editors/vscode && npx vitest run src/worker/client.test.ts`
Expected: FAIL — `client.cancel is not a function`.

- [x] **Step 3: Update `editors/vscode/src/worker/client.ts`**

Add the import:
```ts
import { newRequestId } from "../protocol";
```

Add the method to `WorkerClient`:
```ts
  async cancel(requestId: string, gracePeriodMs: number): Promise<void> {
    await this.send({ id: newRequestId(), method: "execution/cancel", params: {} });

    const waiter = this.pending.get(requestId);
    if (!waiter) {
      return;
    }

    const settled = new Promise<"settled">((resolve) => {
      const original = waiter.resolve;
      waiter.resolve = (frame) => {
        original(frame);
        resolve("settled");
      };
    });
    const timedOut = new Promise<"timeout">((resolve) =>
      setTimeout(() => resolve("timeout"), gracePeriodMs),
    );

    const outcome = await Promise.race([settled, timedOut]);
    if (outcome === "timeout" && this.pending.has(requestId)) {
      const stillPending = this.pending.get(requestId)!;
      this.pending.delete(requestId);
      this.current?.kill();
      stillPending.resolve({
        id: requestId,
        error: {
          code: "execution/forciblyStopped",
          message:
            "The worker did not stop cooperatively within the grace period and was terminated.",
        },
      });
    }
  }
```

Note: this wraps `waiter.resolve` in place, so the existing `Pending` interface must allow reassignment — it already does (`resolve` is a plain property). No other change needed.

- [x] **Step 4: Run test to verify it passes**

Run: `cd editors/vscode && npx vitest run src/worker/client.test.ts`
Expected: PASS (all Task 6–8 tests).

- [x] **Step 5: DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-03 — F1.8: WorkerClient — cooperative-then-forced cancel

`cancel(requestId, gracePeriodMs)`: sends `execution/cancel`, waits out the grace period for the original request to settle, otherwise kills the process and force-settles it with `execution/forciblyStopped`. Ports Section 9's two-step cancellation from `App/WorkerClient.fs`.
```

- [x] **Step 6: Commit**

```bash
git add editors/vscode/src/worker/client.ts editors/vscode/src/worker/client.test.ts docs/DEVLOG.md
git commit -m "feat(vscode): WorkerClient cooperative-then-forced cancel"
```

---

### Task 9: `spawnWorkerProcess` helper + extension activation

**Files:**
- Create: `editors/vscode/src/worker/spawn.ts`
- Modify: `editors/vscode/src/extension.ts`
- Create: `editors/vscode/src/extension.test.ts`
- Modify: `editors/vscode/test/vscode-stub.ts` (add `ExtensionContext`-shaped helper if needed)

**Interfaces:**
- Consumes: `resolveDotnet`, `DotnetNotFoundError`, `DotnetTooOldError` from `./worker/dotnet`; `resolveWorkerDll`, `WorkerDllNotFoundError` from `./worker/worker-path`; `WorkerClient`, `WorkerProcess` from `./worker/client`; `HealthInfo`, `isWorkerVersionSupported`, `newRequestId` from `./protocol`.
- Produces:
  - `src/worker/spawn.ts`: `function spawnWorkerProcess(dotnetCommand: string, workerDllPath: string): WorkerProcess` — wraps `child_process.spawn(dotnetCommand, [workerDllPath], { stdio: ["pipe","pipe","pipe"] })` into the `WorkerProcess` shape (`onExit` binds to the child's `"exit"` and `"error"` events).
  - `src/extension.ts`:
    - `export async function activate(context: vscode.ExtensionContext): Promise<void>` — registers the `fs-query-studio.checkWorker` command; the command body runs `runHealthCheck(deps)` and shows the result / errors.
    - `export async function deactivate(): Promise<void>` — disposes the module-level `WorkerClient` if created.
    - `export async function runHealthCheck(deps: HealthCheckDeps): Promise<HealthCheckResult>` — testable core, no `vscode` import needed at call sites that pass a fake channel.
    - `interface HealthCheckDeps { extensionRoot: string; env: NodeJS.ProcessEnv; log: (line: string) => void; resolveDotnet: typeof resolveDotnet; resolveWorkerDll: typeof resolveWorkerDll; makeClient: (dotnetCommand: string, dll: string) => { send: WorkerClient["send"]; dispose: () => void }; }`
    - `type HealthCheckResult = { ok: true; version: string; capabilities: string[] } | { ok: false; kind: "dotnet-missing" | "dotnet-old" | "worker-missing" | "version-unsupported" | "error"; message: string }`

- [x] **Step 1: Write `editors/vscode/src/worker/spawn.ts`**

```ts
import { spawn } from "node:child_process";
import type { WorkerProcess } from "./client";

export function spawnWorkerProcess(dotnetCommand: string, workerDllPath: string): WorkerProcess {
  const child = spawn(dotnetCommand, [workerDllPath], { stdio: ["pipe", "pipe", "pipe"] });
  return {
    stdin: child.stdin!,
    stdout: child.stdout!,
    stderr: child.stderr!,
    get pid() {
      return child.pid;
    },
    kill: () => {
      child.kill();
    },
    onExit: (cb) => {
      child.on("exit", cb);
      child.on("error", cb);
    },
  };
}
```

- [x] **Step 2: Write the failing test**

`editors/vscode/src/extension.test.ts`:
```ts
import { describe, it, expect } from "vitest";
import { runHealthCheck, type HealthCheckDeps } from "./extension";
import { DotnetNotFoundError, DotnetTooOldError } from "./worker/dotnet";
import { WorkerDllNotFoundError } from "./worker/worker-path";

function baseDeps(overrides: Partial<HealthCheckDeps>): HealthCheckDeps {
  return {
    extensionRoot: "/ext",
    env: {},
    log: () => {},
    resolveDotnet: async () => ({ command: "dotnet", version: "10.0.100" }),
    resolveWorkerDll: () => "/ext/dist/worker/FSharpQueryStudio.Worker.dll",
    makeClient: () => ({
      send: async () => ({ id: "1", result: { version: "0.1.0", capabilities: ["fsi"] } }),
      dispose: () => {},
    }),
    ...overrides,
  };
}

describe("runHealthCheck", () => {
  it("returns ok with the worker version and capabilities", async () => {
    const result = await runHealthCheck(baseDeps({}));
    expect(result).toEqual({ ok: true, version: "0.1.0", capabilities: ["fsi"] });
  });

  it("reports dotnet-missing", async () => {
    const result = await runHealthCheck(
      baseDeps({
        resolveDotnet: async () => {
          throw new DotnetNotFoundError();
        },
      }),
    );
    expect(result).toMatchObject({ ok: false, kind: "dotnet-missing" });
  });

  it("reports dotnet-old", async () => {
    const result = await runHealthCheck(
      baseDeps({
        resolveDotnet: async () => {
          throw new DotnetTooOldError("8.0.404");
        },
      }),
    );
    expect(result).toMatchObject({ ok: false, kind: "dotnet-old" });
  });

  it("reports worker-missing", async () => {
    const result = await runHealthCheck(
      baseDeps({
        resolveWorkerDll: () => {
          throw new WorkerDllNotFoundError(["/ext/dist/worker/FSharpQueryStudio.Worker.dll"]);
        },
      }),
    );
    expect(result).toMatchObject({ ok: false, kind: "worker-missing" });
  });

  it("reports version-unsupported for a v2 worker", async () => {
    const result = await runHealthCheck(
      baseDeps({
        makeClient: () => ({
          send: async () => ({ id: "1", result: { version: "2.0.0", capabilities: [] } }),
          dispose: () => {},
        }),
      }),
    );
    expect(result).toMatchObject({ ok: false, kind: "version-unsupported" });
  });

  it("reports error when the worker returns an error frame", async () => {
    const result = await runHealthCheck(
      baseDeps({
        makeClient: () => ({
          send: async () => ({ id: "1", error: { code: "boom", message: "nope" } }),
          dispose: () => {},
        }),
      }),
    );
    expect(result).toMatchObject({ ok: false, kind: "error" });
  });

  it("logs the outcome", async () => {
    const lines: string[] = [];
    await runHealthCheck(baseDeps({ log: (l) => lines.push(l) }));
    expect(lines.join("\n")).toMatch(/0\.1\.0/);
  });

  it("disposes the client it creates", async () => {
    let disposed = false;
    await runHealthCheck(
      baseDeps({
        makeClient: () => ({
          send: async () => ({ id: "1", result: { version: "0.1.0", capabilities: [] } }),
          dispose: () => {
            disposed = true;
          },
        }),
      }),
    );
    expect(disposed).toBe(true);
  });
});
```

- [x] **Step 3: Run test to verify it fails**

Run: `cd editors/vscode && npx vitest run src/extension.test.ts`
Expected: FAIL — `runHealthCheck` is not exported.

- [x] **Step 4: Write `editors/vscode/src/extension.ts`**

```ts
import * as vscode from "vscode";
import { randomUUID } from "node:crypto";
import {
  resolveDotnet,
  DotnetNotFoundError,
  DotnetTooOldError,
} from "./worker/dotnet";
import { resolveWorkerDll, WorkerDllNotFoundError } from "./worker/worker-path";
import { WorkerClient } from "./worker/client";
import { spawnWorkerProcess } from "./worker/spawn";
import { isWorkerVersionSupported, type HealthInfo, type WorkerResponseFrame } from "./protocol";

export interface HealthCheckDeps {
  extensionRoot: string;
  env: NodeJS.ProcessEnv;
  log: (line: string) => void;
  resolveDotnet: typeof resolveDotnet;
  resolveWorkerDll: typeof resolveWorkerDll;
  makeClient: (
    dotnetCommand: string,
    dll: string,
  ) => { send: (req: { id: string; method: string; params: unknown }) => Promise<WorkerResponseFrame>; dispose: () => void };
}

export type HealthCheckResult =
  | { ok: true; version: string; capabilities: string[] }
  | {
      ok: false;
      kind: "dotnet-missing" | "dotnet-old" | "worker-missing" | "version-unsupported" | "error";
      message: string;
    };

export async function runHealthCheck(deps: HealthCheckDeps): Promise<HealthCheckResult> {
  let dotnet: { command: string; version: string };
  try {
    dotnet = await deps.resolveDotnet({ env: deps.env });
  } catch (err) {
    if (err instanceof DotnetNotFoundError) {
      deps.log(`dotnet not found: ${err.userMessage}`);
      return { ok: false, kind: "dotnet-missing", message: err.userMessage };
    }
    if (err instanceof DotnetTooOldError) {
      deps.log(`dotnet too old: ${err.userMessage}`);
      return { ok: false, kind: "dotnet-old", message: err.userMessage };
    }
    throw err;
  }
  deps.log(`Using dotnet ${dotnet.version} (${dotnet.command}).`);

  let dll: string;
  try {
    dll = deps.resolveWorkerDll(deps.extensionRoot, deps.env);
  } catch (err) {
    if (err instanceof WorkerDllNotFoundError) {
      deps.log(`worker dll not found: ${err.userMessage}`);
      return { ok: false, kind: "worker-missing", message: err.userMessage };
    }
    throw err;
  }
  deps.log(`Worker dll: ${dll}`);

  const client = deps.makeClient(dotnet.command, dll);
  try {
    const response = await client.send({ id: randomUUID(), method: "worker/health", params: {} });
    if (response.error) {
      const message = `Worker health failed: ${response.error.code} ${response.error.message}`;
      deps.log(message);
      return { ok: false, kind: "error", message };
    }
    const health = response.result as HealthInfo;
    if (!isWorkerVersionSupported(health.version)) {
      const message = `Worker protocol version ${health.version} is not supported by this extension.`;
      deps.log(message);
      return { ok: false, kind: "version-unsupported", message };
    }
    deps.log(
      `Worker healthy: version ${health.version}, capabilities [${health.capabilities.join(", ")}].`,
    );
    return { ok: true, version: health.version, capabilities: health.capabilities };
  } finally {
    client.dispose();
  }
}

let outputChannel: vscode.OutputChannel | undefined;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  outputChannel = vscode.window.createOutputChannel("F# Query Studio");

  const command = vscode.commands.registerCommand("fs-query-studio.checkWorker", async () => {
    outputChannel!.show(true);
    const result = await runHealthCheck({
      extensionRoot: context.extensionPath,
      env: process.env,
      log: (line) => outputChannel!.appendLine(line),
      resolveDotnet,
      resolveWorkerDll,
      makeClient: (dotnetCommand, dll) => {
        const client = new WorkerClient({ spawn: () => spawnWorkerProcess(dotnetCommand, dll) });
        return { send: (req) => client.send(req), dispose: () => client.dispose() };
      },
    });

    if (result.ok) {
      void vscode.window.showInformationMessage(
        `F# Query Studio worker OK (version ${result.version}).`,
      );
    } else if (result.kind === "dotnet-missing" || result.kind === "dotnet-old") {
      const pick = await vscode.window.showErrorMessage(result.message, "Install .NET");
      if (pick === "Install .NET") {
        void vscode.env.openExternal(vscode.Uri.parse("https://dotnet.microsoft.com/download"));
      }
    } else {
      void vscode.window.showErrorMessage(result.message);
    }
  });

  context.subscriptions.push(command, outputChannel);
}

export async function deactivate(): Promise<void> {
  outputChannel?.dispose();
  outputChannel = undefined;
}
```

- [x] **Step 5: Run test to verify it passes**

Run: `cd editors/vscode && npx vitest run src/extension.test.ts`
Expected: PASS (9 tests).

- [x] **Step 6: Compile the bundle**

Run: `cd editors/vscode && npm run compile`
Expected: `tsc --noEmit` clean; `dist/extension.js` rebuilt.

- [x] **Step 7: DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-03 — F1.9: extension activation + `fs-query-studio.checkWorker`

`spawn.ts` wraps `child_process.spawn("dotnet", [dll])` into the `WorkerProcess` shape. `extension.ts`: `runHealthCheck` (dependency-injected, fully unit-tested — dotnet-missing / dotnet-old / worker-missing / version-unsupported / error / ok) drives the `fs-query-studio.checkWorker` command, logging to an "F# Query Studio" output channel and surfacing an actionable "Install .NET" error where relevant.
```

- [x] **Step 8: Commit**

```bash
git add editors/vscode/src docs/DEVLOG.md
git commit -m "feat(vscode): worker spawn helper and health-check activation command"
```

---

### Task 10: Real-worker handshake integration test

**Files:**
- Create: `editors/vscode/src/worker/handshake.integration.test.ts`
- Create: `editors/vscode/README.md`

**Interfaces:**
- Consumes: `resolveDotnet` from `./dotnet`, `resolveWorkerDll` from `./worker-path`, `WorkerClient` + `spawnWorkerProcess`, `newRequestId` from `../protocol`.
- Produces: a test that is **skipped** unless a real worker dll is resolvable (`FSQS_WORKER_DLL` set, or `dist/worker/FSharpQueryStudio.Worker.dll` present), otherwise spawns the real worker and asserts a `worker/health` round-trip.

- [x] **Step 1: Write the integration test**

`editors/vscode/src/worker/handshake.integration.test.ts`:
```ts
import { describe, it, expect } from "vitest";
import * as path from "node:path";
import { resolveDotnet } from "./dotnet";
import { resolveWorkerDll } from "./worker-path";
import { WorkerClient } from "./client";
import { spawnWorkerProcess } from "./spawn";
import { newRequestId, type HealthInfo } from "../protocol";

const extensionRoot = path.resolve(__dirname, "..", "..");

let workerDll: string | undefined;
try {
  workerDll = resolveWorkerDll(extensionRoot, process.env);
} catch {
  workerDll = undefined;
}

describe.skipIf(!workerDll)("real worker handshake", () => {
  it("completes a worker/health round-trip", async () => {
    const dotnet = await resolveDotnet();
    const client = new WorkerClient({
      spawn: () => spawnWorkerProcess(dotnet.command, workerDll!),
    });
    try {
      const response = await client.send({
        id: newRequestId(),
        method: "worker/health",
        params: {},
      });
      expect(response.error).toBeUndefined();
      const health = response.result as HealthInfo;
      expect(typeof health.version).toBe("string");
      expect(health.version).toMatch(/^\d+\./);
      expect(Array.isArray(health.capabilities)).toBe(true);
    } finally {
      client.dispose();
    }
  }, 30_000);
});
```

- [x] **Step 2: Build the worker and run the test**

Run:
```bash
dotnet publish src/FSharpQueryStudio.Worker/FSharpQueryStudio.Worker.fsproj -c Release -o editors/vscode/dist/worker
cd editors/vscode && npx vitest run src/worker/handshake.integration.test.ts
```
Expected: PASS (1 test). If `dotnet publish` is not run, expected: the `describe` block is skipped (still green).

- [x] **Step 3: Write `editors/vscode/README.md`**

```markdown
# F# Query Studio — VS Code extension

Cross-platform host for the F# Query Studio worker. See
`docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md` for the
migration design.

## Development

Requirements: Node 20+, .NET 10 SDK.

```bash
cd editors/vscode
npm install

# Build the F# worker into the extension (from the repo root):
dotnet publish ../../src/FSharpQueryStudio.Worker/FSharpQueryStudio.Worker.fsproj \
  -c Release -o dist/worker

npm run compile        # type-check + bundle to dist/extension.js
npm test               # unit tests (vitest); integration test runs if dist/worker exists
```

Press F5 in VS Code to launch an Extension Development Host, then run the
**F# Query Studio: Check Worker** command from the palette.

Set `FSQS_WORKER_DLL` to point at a `FSharpQueryStudio.Worker.dll` built
elsewhere instead of publishing into `dist/worker`.
```

- [x] **Step 4: DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-03 — F1.10: real-worker handshake integration test + dev README

`handshake.integration.test.ts` spawns the actual published worker and asserts a `worker/health` round-trip; skipped (green) when no worker dll is resolvable so `npm test` works without a `dotnet publish`. `editors/vscode/README.md` documents the dev loop.
```

- [x] **Step 5: Commit**

```bash
git add editors/vscode/src/worker/handshake.integration.test.ts editors/vscode/README.md docs/DEVLOG.md
git commit -m "test(vscode): real-worker worker/health handshake integration test"
```

---

### Task 11: CI workflow for the extension

**Files:**
- Create: `.github/workflows/vscode-extension.yml`

**Interfaces:**
- Consumes: the `editors/vscode` npm scripts (`compile`, `test`) and the worker publish command.
- Produces: a GitHub Actions workflow that builds the worker, compiles and tests the extension on Ubuntu / Windows / macOS, and uploads a `.vsix` artifact from the Ubuntu job.

- [x] **Step 1: Inspect existing workflows for conventions**

Run: `ls .github/workflows` and open any existing file. Match its `on:` triggers and action versions where reasonable. If `.github/workflows` does not exist, create it.

- [x] **Step 2: Write `.github/workflows/vscode-extension.yml`**

```yaml
name: vscode-extension

on:
  push:
    paths:
      - "editors/vscode/**"
      - "src/FSharpQueryStudio.Worker/**"
      - "src/FSharpQueryStudio.Protocol/**"
      - ".github/workflows/vscode-extension.yml"
  pull_request:
    paths:
      - "editors/vscode/**"
      - "src/**"
      - ".github/workflows/vscode-extension.yml"

jobs:
  build-test:
    strategy:
      fail-fast: false
      matrix:
        os: [ubuntu-latest, windows-latest, macos-latest]
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"

      - uses: actions/setup-node@v4
        with:
          node-version: "20"

      - name: Publish worker
        run: >
          dotnet publish src/FSharpQueryStudio.Worker/FSharpQueryStudio.Worker.fsproj
          -c Release -o editors/vscode/dist/worker

      - name: Install extension deps
        working-directory: editors/vscode
        run: npm ci

      - name: Compile
        working-directory: editors/vscode
        run: npm run compile

      - name: Test
        working-directory: editors/vscode
        run: npm test

      - name: Package VSIX
        if: matrix.os == 'ubuntu-latest'
        working-directory: editors/vscode
        run: npx --yes @vscode/vsce package --out fs-query-studio.vsix

      - name: Upload VSIX
        if: matrix.os == 'ubuntu-latest'
        uses: actions/upload-artifact@v4
        with:
          name: fs-query-studio-vsix
          path: editors/vscode/fs-query-studio.vsix
```

Note: `npm ci` requires a `package-lock.json`. If Task 1's `npm install` did not commit one, run `npm install` in `editors/vscode` now and `git add editors/vscode/package-lock.json` in this task's commit.

- [x] **Step 3: Validate the YAML locally**

Run: `node -e "require('js-yaml')" 2>/dev/null || npx --yes js-yaml .github/workflows/vscode-extension.yml`
Expected: the file parses (prints the parsed document). If `js-yaml` is unavailable, visually confirm indentation.

- [x] **Step 4: DEVLOG entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-03 — F1.11: CI for the VS Code extension

`.github/workflows/vscode-extension.yml`: on changes under `editors/vscode/` or the worker/protocol projects, publishes the worker, then compiles and tests the extension on Ubuntu/Windows/macOS, and uploads a `.vsix` artifact from the Ubuntu job.
```

- [x] **Step 5: Commit**

```bash
git add .github/workflows/vscode-extension.yml editors/vscode/package-lock.json docs/DEVLOG.md
git commit -m "ci(vscode): build, test, and package the extension on three OSes"
```

---

### Task 12: Phase wrap — verify the full F1 suite and record the milestone

**Files:**
- Modify: `docs/DEVLOG.md`
- Modify: `docs/superpowers/plans/2026-09-03-vscode-extension-f1-scaffold.md` (check every box)

**Interfaces:**
- Consumes: all prior tasks.
- Produces: a green full test run and a phase-summary DEVLOG entry.

- [x] **Step 1: Full type-check + unit test run**

Run: `cd editors/vscode && npm run compile && npm test`
Expected: `tsc --noEmit` clean; all vitest unit tests pass; the integration test either passes (if `dist/worker` was published) or is reported skipped.

- [x] **Step 2: Full integration run**

Run:
```bash
dotnet publish src/FSharpQueryStudio.Worker/FSharpQueryStudio.Worker.fsproj -c Release -o editors/vscode/dist/worker
cd editors/vscode && npm test
```
Expected: the `real worker handshake` test now runs and passes.

- [x] **Step 3: Manual smoke in the Extension Development Host**

In VS Code, open `editors/vscode` as the workspace root, press F5, and in the dev host run **F# Query Studio: Check Worker** from the command palette.
Expected: the "F# Query Studio" output channel shows `Using dotnet ...`, `Worker dll: ...`, `Worker healthy: version ...`; an information message appears.

- [x] **Step 4: DEVLOG phase-summary entry**

Append to `docs/DEVLOG.md`:
```markdown
## 2026-09-03 — F1 complete: VS Code extension scaffold

`editors/vscode/` compiles and tests on three OSes in CI. A dependency-injected `WorkerClient` (NDJSON framing, response correlation, crash/restart, cooperative-then-forced cancel — ported from `App/WorkerClient.fs`) spawns the existing F# worker and completes a `worker/health` handshake with protocol-version negotiation, surfaced through the `fs-query-studio.checkWorker` command. No notebook UI yet — that is F2. The Avalonia app is untouched.
```

- [x] **Step 5: Tick every checkbox in this plan file**

Go through `docs/superpowers/plans/2026-09-03-vscode-extension-f1-scaffold.md` and change every `- [ ]` that is done to `- [x]`.

- [x] **Step 6: Commit**

```bash
git add docs/DEVLOG.md docs/superpowers/plans/2026-09-03-vscode-extension-f1-scaffold.md
git commit -m "docs: F1 scaffold phase complete"
```

---

### Task 13: Extension-host integration tests in a real VS Code (added during execution)

Added after Task 12 at the user's request — "create automated tests to verify in VS Code if it's actually working" — which replaces the manual F5 smoke that Task 12 Step 3 left pending. Same branch, same phase.

**Files:**
- Modify: `editors/vscode/package.json` (dev deps `@vscode/test-cli`, `@vscode/test-electron`, `mocha`, `@types/mocha`; scripts `pretest:vscode`, `test:vscode`, `test:all`)
- Create: `editors/vscode/.vscode-test.mjs` (`defineConfig({ files: "out/test/integration/**/*.test.js", version: "stable", mocha: { ui: "bdd", timeout: 60000 } })`)
- Create: `editors/vscode/tsconfig.integration.json` (extends base; `noEmit:false`, `rootDir:"."`, `outDir:"out"`, `types:["node","mocha"]`; include `src/**/*.ts` + `test/integration/**/*.ts`; exclude `src/**/*.test.ts`)
- Create: `editors/vscode/test/integration/extension.test.ts`
- Modify: `editors/vscode/src/extension.ts` — the `fs-query-studio.checkWorker` handler returns its `HealthCheckResult` (was implicitly `void`) so specs assert on it via `executeCommand`
- Modify: `editors/vscode/src/worker/worker-path.ts` + `worker-path.test.ts` — `FSQS_WORKER_DLL` is authoritative: set-but-missing throws `WorkerDllNotFoundError` instead of falling back to the bundled worker (new unit test)
- Modify: `.github/workflows/vscode-extension.yml` — `pretest:vscode` + `test:vscode` steps in the `build-test` job, Linux under `xvfb-run`
- Modify: `editors/vscode/.vscodeignore` (`.vscode-test/**`, `.vscode-test.mjs`, `tsconfig.integration.json`), `editors/vscode/.gitignore` (`.vscode-test/`)
- Modify: `editors/vscode/README.md` (document `test:vscode` / `test:all`)

**Specs (`test/integration/extension.test.ts`, Mocha BDD, real `vscode` module):**
- `activates` — `vscode.extensions.getExtension("fs-query-studio.fs-query-studio")` then `.activate()`, assert `isActive`.
- `registers the checkWorker command` — `vscode.commands.getCommands(true)` includes `fs-query-studio.checkWorker`.
- `reports worker-missing for a bogus FSQS_WORKER_DLL` — set the env var to a nonexistent path, `executeCommand` the command, assert `result.ok === false && result.kind === "worker-missing"`; `afterEach` clears the env var.
- `completes a real worker/health check against the bundled worker` — `it.skip` when `dist/worker/FSharpQueryStudio.Worker.dll` is absent; otherwise `executeCommand` and assert `result.ok` with a `\d+\.`-shaped `version` and an array `capabilities`. Spawns the .NET worker from inside the extension host.

- [x] **Step 1: Add dev deps and scripts**

`npm install --save-dev @vscode/test-cli @vscode/test-electron mocha @types/mocha`; add `pretest:vscode` (`tsc -p tsconfig.integration.json && node esbuild.mjs`), `test:vscode` (`vscode-test`), `test:all` (`npm test && npm run test:vscode`).

- [x] **Step 2: Write `.vscode-test.mjs` and `tsconfig.integration.json`** (see Files).

- [x] **Step 3: Make the command return its result**

In `src/extension.ts`, lift the command body into `const runCheck = async (): Promise<HealthCheckResult> => { ...; return result; }` and register that.

- [x] **Step 4: Write the failing spec** (`test/integration/extension.test.ts`, all four `it`s above).

- [x] **Step 5: Run — one spec fails**

`npm run pretest:vscode && npm run test:vscode` → `reports worker-missing` fails: a bogus `FSQS_WORKER_DLL` still resolved because `resolveWorkerDll` fell through to the bundled worker.

- [x] **Step 6: Make `FSQS_WORKER_DLL` authoritative**

`worker-path.ts`: when the override is set but the file is missing, `throw new WorkerDllNotFoundError([override])` instead of continuing to the bundled path. Add the vitest case `throws when FSQS_WORKER_DLL is set but the file is missing, ignoring the bundled fallback`.

- [x] **Step 7: Run — all green**

`npx vitest run` → 36 passing. `npm run pretest:vscode && npm run test:vscode` → 4 passing.

- [x] **Step 8: CI + ignore + README** (see Files). Validate the workflow YAML.

- [x] **Step 9: DEVLOG entry** (`## 2026-09-04 — F1.12: extension-host integration tests (real VS Code)`).

- [x] **Step 10: Commit**

```bash
git add -A
git commit -m "test(vscode): extension-host integration tests in a real VS Code"
```

---

## Self-Review

**1. Spec coverage (F1 scope only — the umbrella spec's F1 row: "editors/vscode/ compiles; activate; worker/client.ts + tests; dotnet resolution; health check. No notebook yet."):**

| F1 requirement (spec) | Task |
|---|---|
| `editors/vscode/` compiles | Task 1 (scaffold, `npm run compile`), re-verified Task 12 |
| `activate` | Task 9 (`activate` + `fs-query-studio.checkWorker`) |
| `worker/client.ts` + tests | Tasks 6 (send/correlation), 7 (crash/restart), 8 (cancel) — all TDD |
| `dotnet` resolution (`>= 10`) | Task 4 (`resolveDotnet`, `DotnetTooOldError`) |
| health check | Task 9 (`runHealthCheck` → `worker/health`), Task 10 (real worker) |
| NDJSON transport (spec "Global Constraints" / component table `worker/client.ts`) | Task 3 (`ndjson.ts`) |
| protocol version negotiation (spec: "the extension checks `HealthInfo.Version` against a supported range at activation") | Task 2 (`isWorkerVersionSupported`), consumed in Task 9 (`version-unsupported`) |
| worker DLL location (spec: "Bundled worker location: `editors/vscode/dist/worker/...`") | Task 5 (`resolveWorkerDll`) |
| `OutputChannel` "F# Query Studio" (spec component `log/channel.ts` — the worker-status half) | Task 9 |
| actionable "Install .NET" error (spec error table row `dotnet missing / < 10`) | Task 9 |
| CI (spec Testing: "GitHub Actions, matrix win/linux/mac ... `npm test` ... `vsce package` as an artifact") | Task 11, extended in Task 13 |
| dev README / `dotnet publish` step (spec Packaging) | Task 10, updated in Task 13 |
| extension-host tests in a real VS Code (spec Testing: "Extension host" row) | Task 13 (added during execution — activation, command registration, `worker-missing`, real `worker/health`) |

Deferred to later phases by design (not F1 gaps): notebook serializer/controller/renderer (F2/F3), `analysis/*` providers (F4), connections tree + secret bridge (F5), `BridgeSecretStore` worker change (F5), self-contained VSIX (F7). The extension-host **test harness** landed in F1 (Task 13); the notebook/cell/connection scenarios it will run are added with their phases.

**2. Placeholder scan:** No "TBD"/"TODO"/"handle edge cases"/"similar to Task N". Every code step has a full code block. Task 7 Step 3 gives an explicit edit (interface + method replacement) rather than a full-file rewrite because it modifies a file created in Task 6 that the executor has in front of them; the exact new method body is spelled out. Task 11 Step 1 asks the executor to look at existing workflows for house style — that is an inspection instruction, not a deferred decision (the full workflow file follows in Step 2).

**3. Type consistency:**
- `WorkerProcess` shape (`stdin`/`stdout`/`stderr`/`pid`/`kill`/`onExit`) — defined Task 6, reused verbatim in `fake-child.ts` (Task 6), `spawn.ts` (Task 9), integration test (Task 10). `pid` is `number | undefined` everywhere.
- `WorkerResponseFrame` (`{ id; result?; error? }`) and `WorkerErrorInfo` (`{ code; message }`) — Task 2; consumed unchanged in Tasks 6–10.
- `WorkerClientOptions` — `{ spawn }` in Task 6, widened to `{ spawn; onCrash? }` in Task 7. Task 8 adds a method, not an option. Consistent.
- `resolveDotnet(opts?: { run?; env? })` — Task 4; `runHealthCheck` calls `deps.resolveDotnet({ env: deps.env })` (Task 9) — matches (`run` optional).
- `resolveWorkerDll(extensionRoot, env?)` — Task 5; called `deps.resolveWorkerDll(deps.extensionRoot, deps.env)` in Task 9 — matches.
- `HealthCheckDeps.makeClient` returns `{ send; dispose }`; the real wiring in `activate` (Task 9) adapts `WorkerClient` to that shape (`send`/`dispose`), and tests pass fakes of the same shape. Consistent.
- `newRequestId` — Task 2; used in Task 8 (`client.ts`) and Task 10 (integration test). `runHealthCheck` uses `randomUUID()` directly from `node:crypto` — both produce UUID strings; acceptable, no shared contract broken.
- Command id `fs-query-studio.checkWorker` — identical in `package.json` (Task 1), `activate` (Task 9).
- Output channel name `"F# Query Studio"` — Task 9 only; matches the spec's `log/channel.ts` name.

No inconsistencies found.
