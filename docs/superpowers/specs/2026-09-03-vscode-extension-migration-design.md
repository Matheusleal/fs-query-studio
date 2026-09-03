# VS Code Extension Migration — Design

Move F# Query Studio from a Windows-only Avalonia desktop app to a cross-platform VS Code extension. The F# worker process and its libraries (`Execution`, `Connectors`, `Results`, `Protocol`, `Storage`, `Security`, `Fsac`, `Logging`) are reused almost unchanged; the entire UI tier (`App`, `Ui`, `Editor`) is retired. The new host is a TypeScript extension under `editors/vscode/` that drives the same NDJSON/stdio worker and surfaces queries as a custom **notebook** (`.fsqnb`), with a notebook renderer for the results grid and thin VS Code language providers for in-cell F# intelligence.

Ionide is **not** a dependency. It handles F# in ordinary `.fs`/`.fsx` files; it never sees notebook cells (see Background). The two extensions coexist passively.

## Background

### Current architecture

`fs-studio-plan.md` describes the shipped system: an Avalonia host process, a restartable **F# worker process** speaking newline-delimited JSON frames (`WorkerRequestFrame`/`WorkerResponseFrame`) over stdio, and a Monaco editor in a WebView2 control. The worker owns everything that touches F# Compiler Service, database drivers, and runtime values:

- `execution/evaluate`, `execution/cancel` — isolated-by-default FSI evaluation, with an opt-in **Persistent** session mode (Milestone E.1) keyed by `SessionId`, plus E.3 append-invalidation.
- `session/reset` — drops a Persistent session and its paging cursor.
- `results/page`, `results/discard` — bounded table projection with a continuation token.
- `connection/register`, `connection/remove`, `connection/schema` — connection definitions persisted to `workspace.json` (never secrets), schema discovery.
- `analysis/check`, `analysis/complete`, `analysis/hover`, `analysis/definition`, `analysis/rename` — stateless-per-request F# analysis via the `Fsac` project (FSAC backend), which injects a controlled prologue so a script can reference `Connections.*` / `Results.*` unqualified, applying a line offset between the user's source and what is actually checked. This is what powers connection-name autocomplete inside `Connections.sql/csv/excel/rest` (DEVLOG 2026-08-24).
- `worker/health` — returns `HealthInfo.Version`.

The worker is **one evaluation at a time** (`Interlocked` guard on `evaluationInFlight`); `FsiSession.create` mutates process-wide state (cwd, `Console.Out`/`Error`). `analysis/*` runs backgrounded off the read loop so a keystroke never stalls a concurrent `execution/cancel`. The host-side `WorkerClient` (in `App`) detects worker exit (`readFrame` returning `None`), fails all pending requests, and starts a fresh process on the next send — no automatic retry of the dead request. Responses are matched to requests by `Id` because they can arrive out of order.

The persistent workspace directory is `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)` + `"FSharpQueryStudio"` — already cross-platform in .NET (maps to `~/.local/share` on Linux, `~/Library/Application Support` on macOS).

### Secret storage — the one real cross-platform gap

`ISecretStore` (in `Security`) is a platform-neutral abstraction. Implementations today: `WindowsCredentialStore` (P/Invoke into `advapi32` `CredWriteW`/`CredReadW`) and `InMemorySecretStore` (tests + non-Windows fallback). There is **no real macOS/Linux implementation**. `ConnectionDefinition.SecretReference` is a pointer; the raw credential lives only in the OS store. Connectors resolve through `ISecretStore` + `ConnectionStringSecrets.resolve`.

### Spike finding — Ionide cannot analyze notebook cells

Ionide's LSP client `documentSelector` (`ionide-vscode-fsharp/src/Core/LanguageService.fs:114`) is exactly:

```fsharp
[ { language = "fsharp"; scheme = "file" }
  { language = "fsharp"; scheme = "untitled" } ]
```

Notebook cell documents have scheme `vscode-notebook-cell`. The VS Code LSP client only forwards `didOpen`/`didChange`/completion/hover/diagnostics for documents matching the selector, so Ionide/FSAC **never see notebook cells**. There is no middleware remapping cell URIs, no `notebookDocumentSync` (LSP 3.17) capability in the client, and FSAC has zero notebook-related source files. Issue #1311 (closed) — a maintainer response punts F# notebooks to `dotnet/interactive`, which is deprecated.

Therefore in-cell intelligence must be provided by this extension, not Ionide.

### Replicating .NET Interactive's approach

Polyglot Notebooks (`dotnet/interactive`) got F# IntelliSense in cells **without LSP**. From `src/polyglot-notebooks-vscode-common/src/languageProvider.ts:162`:

```ts
const languages = [constants.CellLanguageIdentifier];
vscode.languages.registerCompletionItemProvider(languages, new CompletionItemProvider(...), ...triggerChars);
vscode.languages.registerHoverProvider(languages, new HoverProvider(...));
vscode.languages.registerSignatureHelpProvider(languages, new SignatureHelpProvider(...), ...triggerChars);
// onDidChangeTextDocument (debounced) -> notebookCellChanged -> client.getDiagnostics -> DiagnosticCollection
```

Each provider maps the cell document to its notebook, picks the cell's kernel, and sends a kernel command (`client.completion(text, line, char)`, `client.getDiagnostics(text)`, `client.hover(...)`) over its own protocol. The `FSharpKernel` implements those commands with FSharp.Compiler.Service directly against accumulated submission text. Completion and diagnostics are debounced.

This maps 1:1 onto the existing worker:

| Polyglot Notebooks | This worker (already exists) |
|---|---|
| `client.completion(...)` | `analysis/complete` |
| `client.hover(...)` | `analysis/hover` |
| `client.getDiagnostics(...)` | `analysis/check` |
| `FSharpKernel` + FCS + FSI state | `Fsac`/`Analysis`, already injects `Connections`/`Results` |

`registerCompletionItemProvider` accepts a bare language-id / notebook-type selector and VS Code invokes it for cell documents regardless of scheme — this sidesteps the `documentSelector` scheme problem entirely, and there is no provider conflict with Ionide because Ionide's providers are scoped out of `vscode-notebook-cell`.

**Realistic v1 ceiling for this approach** (what Polyglot also lacked in-cell): only completion, hover, signature help, diagnostics. No semantic tokens, code lens, find-all-references, formatting, code actions/quick-fix, or cross-cell rename. Basic syntax highlighting comes from a TextMate grammar.

## Goals

- A cross-platform (Windows, macOS, Linux) VS Code extension that reaches feature parity with the desktop app for the query-and-execution workflow.
- Reuse the F# worker and every `src/` library except `App`/`Ui`/`Editor` with no change to `Protocol` wire types, the NDJSON transport, or the FSI execution path.
- Retire `App`, `Ui`, `Editor`, `App/WorkerClient.fs`, the Avalonia themes, and `WindowsCredentialStore` from the active path once parity is reached.
- Queries live in a custom notebook file, `.fsqnb` (own JSON schema), executed by a `NotebookController` that is a thin front end over `execution/evaluate` in Persistent mode.
- Tabular results render in a `NotebookRenderer` grid with schema view, CSV export, and paging via `results/page`.
- In-cell F# intelligence (completion, hover, signature help, diagnostics) via VS Code language providers forwarding to the worker's `analysis/*` methods, Polyglot-style. Connection-name autocomplete keeps working.
- Cross-platform secret storage via VS Code `SecretStorage`, reached from the worker through a reverse RPC, replacing `WindowsCredentialStore` for the extension host.
- Ship as a single VSIX; require .NET 10 on `PATH` (audience already has the SDK; matches Ionide's own requirement).

## Non-goals

- **No fork of, contribution to, or runtime dependency on Ionide.** It is a passive coexisting extension and at most a README recommendation.
- **No `.NET Interactive` / Polyglot kernel reuse** — deprecated. Its provider-and-kernel-command pattern is copied; none of its code is.
- No `.ipynb` or `.fsx`-with-`// %%` file format. The format is a purpose-built JSON schema.
- No semantic tokens, code lens, find-all-references, formatting, or code actions in cells for v1. `analysis/definition` and `analysis/rename` in-cell are F7 backlog.
- No self-contained / per-RID VSIX for v1. Framework-dependent worker, `dotnet` on `PATH`. Offline self-contained packaging is F7 backlog.
- No parallel multi-worker execution. The worker stays one-evaluation-at-a-time; the extension serializes the cell queue, and evaluations across different open notebooks serialize against each other.
- No change to Execution, Connectors, Results, Fsac internals, or FSI behavior. Worker changes are additive only.
- No in-app log viewer. Replaced by an `OutputChannel` plus the existing Serilog file sink (accepted downgrade).
- Keeping the Avalonia app alive alongside the extension indefinitely — it is deleted at F6.

## Architecture

### Runtime processes

```text
VS Code Extension Host (Node)
 ├─ fs-query-studio extension (TypeScript, editors/vscode/)
 │   ├─ worker/client.ts      NDJSON/stdio  ─────►  worker .NET (child process)
 │   ├─ notebook/serializer    .fsqnb  <->  NotebookData
 │   ├─ notebook/controller    "kernel": executeCell -> execution/evaluate
 │   ├─ notebook/renderer      mime application/x-fsquery-table+json -> grid
 │   ├─ lang/providers         completion/hover/sigHelp/diagnostics -> analysis/*
 │   ├─ connections/tree       add/remove/schema -> connection/*
 │   └─ secrets/bridge         worker asks -> context.secrets answers
 │
 └─ Ionide (unchanged)         F# in .fs/.fsx files only; never a cell

worker .NET = src/FSharpQueryStudio.Worker + Execution, Connectors, Results,
              Protocol, Storage, Security, Fsac, Logging.  One process per
              VS Code window.  SessionId = notebook URI isolates FSI sessions.
```

### Repo layout

```text
src/                         unchanged (worker + libs)
  FSharpQueryStudio.App/      RETIRED at F6
  FSharpQueryStudio.Ui/       RETIRED at F6
  FSharpQueryStudio.Editor/   RETIRED at F6  (Monaco / WebView removed)
editors/vscode/              NEW
  package.json                contributes: notebooks, notebookRenderer,
                              commands, views (Activity Bar), languages
  src/                        TypeScript modules below
  syntaxes/                   F# TextMate grammar for cell highlighting
  media/renderer/             bundled results-grid webview asset (esbuild)
  dist/worker/                dotnet publish output, packed into the VSIX
tests/                        unchanged; + editors/vscode/src/**/*.test.ts
docs/                         this spec + DEVLOG entries per phase
```

### Extension components

| Module | Responsibility | Talks to |
|---|---|---|
| `activate.ts` | `activationEvents: onNotebook:fsquery-notebook`. Resolve `dotnet`, spawn worker, `worker/health` version check, register everything. `deactivate` disposes the worker. | — |
| `worker/client.ts` | Port of `App/WorkerClient.fs` to TS: spawn child, NDJSON framing, correlate by `Id`, tolerate out-of-order responses, detect exit and restart on next send (no dead-request retry), a write lock serializing stdin writes. Also carries the reverse channel for host-bound requests (secret RPC). | worker stdio |
| `worker/dotnet.ts` | Locate `dotnet` (`PATH`, `DOTNET_ROOT`); verify `>= 10`. Actionable error with an "Install .NET" action if missing. | — |
| `notebook/serializer.ts` | `NotebookSerializer` for `.fsqnb` JSON <-> `NotebookData`. Never persists outputs. | — |
| `notebook/controller.ts` | `NotebookController` (the kernel). `executeHandler`: per cell -> `execution/evaluate { source, sessionId, mode: Persistent }`. Stream stdout/stderr as incremental `NotebookCellOutputItem`. Tabular result -> item mime `application/x-fsquery-table+json`. `interruptHandler` -> `execution/cancel`. "Restart Kernel" command -> `session/reset`. | worker |
| `notebook/renderer.ts` + `media/renderer/` | `NotebookRenderer` for the tabular mime: virtualized grid, schema tab, CSV export. Isolated esbuild bundle. Theme via VS Code CSS variables. "Load more" -> extension command -> `results/page` -> `postMessage` append. | — |
| `lang/providers.ts` | `registerCompletionItemProvider` / `registerHoverProvider` / `registerSignatureHelpProvider` with selector `{ language: 'fsharp', notebookType: 'fsquery-notebook' }`. `onDidChangeTextDocument` (debounced) -> `analysis/check` -> `DiagnosticCollection`. Each request sends the concatenated text of preceding same-language cells + the current cell; positions are offset accordingly. | worker `analysis/*` |
| `connections/tree.ts` | `TreeDataProvider` in the Activity Bar: connections -> Tables -> Columns (lazy via `connection/schema`). Add / remove commands. | worker `connection/*` |
| `connections/add.ts` | Multi-step `QuickPick` (type, name, connection string with a secret placeholder) replacing the Avalonia dialog. Secret -> `context.secrets`; `connection/register` gets only the `SecretReference`. | worker + secrets |
| `secrets/bridge.ts` | Answers host-bound `secret/get|set|delete` from `context.secrets`. | worker (reverse) |
| `log/channel.ts` | `OutputChannel` "F# Query Studio" and "F# Query Studio (Worker)"; tails the worker's stderr (Serilog). Replaces the in-app log viewer. | — |

### Worker changes — additive, small

1. **`BridgeSecretStore : ISecretStore`** replaces `WindowsCredentialStore` in the extension host's worker. On `resolve`/`store`/`delete` it sends a host-bound request (`secret/get|set|delete`) and awaits the reply. This requires the worker to *initiate* requests (today it only responds): add a host-bound request path to the NDJSON loop — same framing, worker-generated `Id` with a distinct prefix so it cannot collide with host-initiated request ids.
   - *Rejected alternative:* native libsecret / Keychain implementations — more native code, two platforms, for no gain over the bridge.
2. **`Program.fs` composition** swaps `WindowsCredentialStore` for `BridgeSecretStore`. `workspaceDir` is unchanged (already cross-platform).
3. **`session/reset`** already exists; confirm it clears both the Persistent session for the `SessionId` and `currentPaging`.
4. **`workspace/prelude`** (F7, optional) — returns the path of a generated prelude `.fsx` with real `Connections`/`Results` definitions, so a query edited as a loose script gets Ionide help. The notebook path does not depend on it.
5. Nothing else changes. `Protocol` frames, transport, Execution, Connectors, Results, `Fsac` all untouched.

**Protocol additions:** `secret/get`, `secret/set`, `secret/delete` (host-bound); `workspace/prelude` (F7). Version negotiation: the extension checks `HealthInfo.Version` against a supported range at activation and refuses to activate the kernel on mismatch, with a clear message.

### `.fsqnb` format

Own JSON schema under this project's control. Minimum shape:

```jsonc
{
  "version": 1,
  "cells": [
    { "kind": "code", "language": "fsharp", "value": "..." },
    { "kind": "markdown", "value": "..." }
  ],
  "metadata": { }          // reserved; e.g. a default connection later
}
```

Outputs are never written to the file. Not runnable as a script outside the extension (accepted; no external tooling).

## Data flow

**Open `.fsqnb`.** `onNotebook` activation -> resolve `dotnet` -> spawn worker -> `worker/health` (version check) -> `NotebookSerializer.deserializeNotebook` builds cells -> controller becomes the selected kernel. No FSI session yet (lazy on first execution).

**Edit a cell (completion / diagnostics).** Keystroke -> provider takes the active cell -> concatenates the text of preceding same-language cells + the current cell -> `analysis/complete` (or `analysis/check` on the debounce) with `SessionId` = notebook URI -> worker runs FCS aware of `Connections`/`Results` -> returns items / diagnostics -> `DiagnosticCollection` updates. Debounce: ~300 ms for check, ~150 ms for complete. Ionide is not invoked (out of selector).

**Run a cell.** `executeHandler([cell])` -> `NotebookCellExecution.start()` -> `execution/evaluate { source, sessionId, mode: Persistent }` -> worker:
- stdout/stderr stream as incremental `replaceOutput` (`CellOutputItem.stdout`/`stderr`);
- final result: tabular -> `CellOutput` with an `application/x-fsquery-table+json` item (schema + bounded rows + `continuationToken` + `truncated`); text/error -> `stdout` / `error` item;
- `execution.end(ok, timestamp)`.
The renderer draws the grid from the mime item. "Load more" -> extension command -> `results/page { continuationToken }` -> renderer appends via `postMessage`.

**Run All.** VS Code queues cells in order. The worker is one-eval-at-a-time (`evaluationInFlight`), so the extension serializes: it sends the next cell only after the previous finishes. A cell failure stops the queue (standard notebook behavior).

**Cancel.** Interrupt button -> `interruptHandler` -> `execution/cancel`. The worker writes its cancellation sentinel into the current evaluation's work dir. `execution.end(false)`.

**Restart Kernel.** Command -> `session/reset { sessionId }` -> worker drops the Persistent session + `currentPaging`. Next execution starts a fresh FSI session; diagnostics revalidate.

**Add a connection.** `QuickPick` -> `context.secrets.store(ref, value)` -> `connection/register { definition with SecretReference }` -> worker persists to `workspace.json` (no secret) -> TreeView refresh. A later execution resolves `Connections.foo` -> `BridgeSecretStore.resolve` -> host-bound `secret/get { ref }` -> extension answers from `context.secrets`.

## Error handling and lifecycle

| Situation | Handling |
|---|---|
| `dotnet` missing / `< 10` | Actionable notification ("Install .NET 10", "Configure path"). Notebook opens; kernel shows "unavailable". No crash. |
| Worker crash (stdout closes) | `client.ts` sees `readFrame -> None`, fails all pending with `worker/crashed`, marks the kernel down. The next `executeCell` spawns a fresh worker (session lost — noted in the cell output). No auto-retry of the dead request. |
| Protocol version mismatch | `worker/health` at activation; outside the supported range -> clear error, kernel does not activate. |
| `execution/busy` | The extension never lets two evaluations race (it serializes the queue). If it still occurs: surfaced in the cell, not fatal. |
| Cancel arrives before the evaluation starts | Already handled worker-side (`beginEvaluate` reserves `currentWorkDir` synchronously). |
| Secret missing from `context.secrets` | `secret/get` returns `null` -> worker returns a structured connection error -> the cell shows "credential not found for ref X" with a link to re-add. |
| `context.secrets` unavailable (rare) | The bridge catches it and returns an error; same UX as above. |
| Notebook closed with an evaluation running | `deactivate` / dispose -> `execution/cancel` + worker dispose (grace period, then kill). |
| Multiple `.fsqnb` open | One worker per VS Code window; `SessionId` per URI isolates FSI sessions. The worker is globally one-eval-at-a-time, so evaluations across notebooks serialize (accepted for v1). |
| Worker stderr | Goes to the "(Worker)" `OutputChannel`. The Serilog file sink continues (14-day retention) under `LocalApplicationData`. |

## Packaging and distribution

- **Worker:** framework-dependent. `dotnet publish -c Release` of `FSharpQueryStudio.Worker` into `editors/vscode/dist/worker/`, packed into the VSIX. The extension runs `dotnet <path>/FSharpQueryStudio.Worker.dll`.
- **VSIX:** single, no RID. `vsce package`. Tens of MB (worker DLLs only, no runtime).
- **Marketplace:** new publisher, `vsce publish`. `extensionKind: ["workspace"]` (the worker needs the workspace filesystem/network). Name clearly distinct from "Ionide".
- **`extensionDependencies`: none.** Ionide is a README recommendation only.
- **Offline fallback (F7, optional):** platform-specific VSIX with a self-contained worker (`vsce package --target`).
- **CI:** GitHub Actions, matrix win/linux/mac — `dotnet test` (worker/libs, unchanged) + `npm test` (extension) + `dotnet publish` + `vsce package` as an artifact. Marketplace publish is manual / tag-driven for v1.

## Testing

| Layer | How | Covers |
|---|---|---|
| Worker + libs | Existing `dotnet test`, **unchanged** | Execution, Connectors, Results, Fsac, Protocol |
| `worker/client.ts` | Unit TS, mock child process (stdin/stdout pipes) | NDJSON framing, ordering, restart, write lock, reverse channel |
| `serializer.ts` | Unit TS, round-trip `.fsqnb` <-> NotebookData | format, no persisted output |
| `lang/providers.ts` | Unit TS, mock `WorkerClient` | cell concatenation, position offset, debounce |
| `secrets/bridge.ts` | Unit TS, fake `context.secrets` | get/set/delete, missing key |
| Integration | `@vscode/test-electron` — headless VS Code, extension + real worker, Ionide not installed | open `.fsqnb`, run a cell, see the tabular mime, cancel, restart kernel, add a connection and resolve its secret |
| Renderer | Unit TS (jsdom) of the grid | render schema + rows, "load more", CSV export |

**Not automated (accepted, noted here):** renderer appearance, real completion interaction in the VS Code UI, side-by-side behavior with Ionide installed — all on the manual release checklist.

## Rollout — phased

Each phase is its own spec -> plan -> implementation -> DEVLOG entry.

| Phase | Delivers | Avalonia |
|---|---|---|
| **F1 Scaffold** | `editors/vscode/` compiles; `activate`; `worker/client.ts` + tests; `dotnet` resolution; health check. No notebook yet. | intact |
| **F2 Minimal notebook** | `.fsqnb` serializer + controller + run a cell + stdout/stderr. Tabular result shown as **text** (no renderer). Persistent mode. | intact |
| **F3 Renderer** | `NotebookRenderer` grid + schema + CSV export + paging. | intact |
| **F4 Cell intelligence** | completion / hover / signature help / diagnostics providers -> `analysis/*`. Cell concatenation. | intact |
| **F5 Connections** | TreeView + QuickPick add/remove + `BridgeSecretStore` + reverse secret RPC. Cross-platform secrets. | intact |
| **F6 Parity + retire** | Parity checklist vs the app. Remove `App`/`Ui`/`Editor` + `WorkerClient.fs` + themes + `WindowsCredentialStore` from the active path. Clean `.slnx`. Review `installer/`. Marketplace publish. | **deleted** |
| **F7 Extras (backlog)** | prelude `.fsx` for Ionide on loose scripts; `analysis/definition` + `rename` in-cell; self-contained offline VSIX; parallel multi-worker. | — |

**Parity criteria (F6):** write and run a query, all six connectors, CSV export, connection-name autocomplete, schema navigation, secrets kept out of files, session persistence. Log viewer -> `OutputChannel` + file sink (accepted downgrade).

## Open risks

- **In-cell intelligence ceiling.** Completion / hover / signature help / diagnostics only. If users expect find-all-references, formatting, or code actions in cells, that is a later, larger piece of work (a real LSP scoped to cells, or upstream notebook support in FSAC).
- **Cross-notebook serialization.** Evaluations in different open `.fsqnb` files block each other because the worker is globally single-evaluation. Acceptable for v1; parallel workers are F7.
- **`dotnet` on PATH requirement.** Friction for users without the SDK. Mitigated by the audience (F# developers) and an actionable install prompt; self-contained packaging is the F7 escape hatch.
- **Reverse RPC on the worker.** The worker gaining the ability to initiate requests is new surface. Kept minimal (three secret methods), distinct id prefix, and covered by `worker/client.ts` tests.
