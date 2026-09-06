# VS Code Extension — F4 Cell Intelligence — Design

Fourth phase of the Avalonia → VS Code extension migration (`docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md`). F1 Scaffold, F2 Minimal notebook, and F3 Renderer are complete on `main-vscode`. This phase adds in-cell F# intelligence — completion, hover, signature help, diagnostics — for `.fsqnb` code cells, forwarding to the worker's existing `analysis/*` methods.

## Background

Ionide never sees notebook cells: its LSP client `documentSelector` is `scheme: file | untitled` only, and cell documents are `scheme: vscode-notebook-cell` (migration design, "Spike finding"). So in-cell intelligence must be provided by this extension.

The worker already answers every method this phase needs — `analysis/check`, `analysis/complete`, `analysis/hover`, `analysis/signatureHelp` (also `analysis/definition` / `analysis/rename`, which are F7, not F4). `src/FSharpQueryStudio.Worker/Handlers.fs` dispatches them; `src/FSharpQueryStudio.Fsac/FsacBridge.fs` runs them against FSAC with a controlled prologue so a script can reference `Connections.*` / `Results.*` unqualified, and shifts the prologue's line count back out of every position it returns (`Prologue.shiftUp` / `shiftDown`). `src/FSharpQueryStudio.Worker/Program.fs` routes `analysis/*` off the evaluation path — several run concurrently, out of order, without shared mutable state. **No worker change in this phase.**

The Avalonia host's `src/FSharpQueryStudio.App/AnalysisService.fs` is the reference client: one `WorkerRequestFrame` per call, no tracked request id (a stale response is discarded by a version guard, not cancelled), worker error → empty result, never a host crash.

Today the extension has no language code at all. `notebook/controller.ts` sends only the single cell's own text as `execution/evaluate` source; there is no concatenation anywhere.

## Goals

- A `lang/` module registering four providers for the selector `{ language: "fsharp", notebookType: "fsquery-notebook" }`:
  - **completion** → `analysis/complete`
  - **hover** → `analysis/hover`
  - **signature help** → `analysis/signatureHelp`
  - **diagnostics** → `analysis/check`, published to a `DiagnosticCollection`, refreshed on the cell's own edits (debounced) and on first sight of the cell document
- **Cross-cell concatenation**: each request's source is the concatenation of the notebook's *executed* `fsharp` code cells (in execution order, target cell excluded from the prefix) followed by the target cell's live text. Request positions are offset by the prefix's line count; diagnostic ranges are mapped back and any that fall outside the target cell are dropped.
- Connection-name autocomplete inside `Connections.sql/csv/excel/rest` keeps working — it already does, server-side, because FSAC sees `Connections.*` through the worker's prologue.
- Ionide, if installed, produces no duplicate items in cells (it is out of selector by scheme — verified in the migration spike; this phase adds nothing to change that).

## Non-goals

- **No `languages` contribution and no TextMate grammar.** Scope is providers only, matching the migration design's F4 row. Cell syntax highlighting depends on some other extension registering an `fsharp` grammar (Ionide, or the future `syntaxes/` grammar deferred to a later phase). Providers are registered programmatically in `activate`, not via `package.json` `contributes.languages`.
- **No `analysis/definition` / `analysis/rename` in cells** — F7 backlog, per the migration design.
- **No semantic tokens, code lens, find-all-references, formatting, code actions / quick-fix, cross-cell rename** — the migration design's stated v1 ceiling for this approach.
- **No execution-driven diagnostics refresh.** Running or re-running a cell does not re-check other cells. A cell's squiggles refresh only on that cell's own next edit. (Chosen for fewer moving parts; the staleness window is documented below.)
- **No completion `kind` / `detail` / `documentation`**, **no active-parameter index in signature help** — the worker returns a flat name list and a single un-indexed signature; enriching either is a later task if it proves to matter.
- No change to `notebook/controller.ts`'s `execution/evaluate` source (still the single cell's text — FSI accumulates real state at run time; concatenation is an analysis-only construct).
- No change to the worker, `Protocol` (F# side), `Fsac`, or transport.

## Architecture

### Module layout — `editors/vscode/src/lang/`

| File | Responsibility | Pure? |
|---|---|---|
| `cell-context.ts` | `buildAnalysisInput(cells, targetCell, position?)` → `{ source, line, column, cellLineOffset }`. `mapDiagnosticIntoCell(wire, cellLineOffset, cellLineCount)` → cell-local `Diagnostic` or `null`. `versionFor(cellUri, source)` → monotonic int, bumped only when `source` differs from the last value seen for that URI. | yes (`versionFor` holds a module-level `Map`) |
| `analysis-client.ts` | `check` / `complete` / `hover` / `signatureHelp`, each taking a `WorkerLike` and building one `analysis/*` request. Worker `error` frame or absent `result` → `[]` / `undefined`. Never throws. | yes |
| `diagnostics.ts` | `DiagnosticsManager` — owns one `DiagnosticCollection`, a per-cell-URI 300 ms debounce map, and `workspace.onDidChangeTextDocument` / `onDidCloseTextDocument` / `onDidChangeNotebookDocument` subscriptions. `dispose()` clears everything. | no |
| `providers.ts` | `registerLanguageFeatures({ getWorker, log }): vscode.Disposable` — registers the three interactive providers, constructs the `DiagnosticsManager`, returns a `Disposable` bundling all of them. Composition root; no logic of its own beyond mapping `analysis/*` results to VS Code types. | no |

`WorkerLike` is the existing `{ send(req): Promise<WorkerResponseFrame> }` shape already used by `notebook/controller.ts` and `notebook/renderer-host.ts` — reused, not redefined.

### `buildAnalysisInput`

```
prefixCells =
  cells
  |> filter (kind === Code && document.languageId === "fsharp")
  |> filter (executionSummary?.executionOrder != null)
  |> filter (cell !== targetCell)
  |> sortBy executionSummary.executionOrder ascending

prefixText   = prefixCells.map(c => c.document.getText()).join("\n")
source       = prefixText === "" ? targetText : prefixText + "\n" + targetText
cellLineOffset = prefixText === "" ? 0 : countLines(prefixText)
line         = position ? position.line + cellLineOffset : 0
column       = position ? position.character : 0
```

- Each prefix cell contributes its **current** document text, not the text as executed — FSAC is a static check and live text gives the best editing feedback. A cell counts as "in the prefix" purely by `executionSummary.executionOrder` being set.
- The target cell is always excluded from the prefix even if it has executed; its live text is the suffix.
- `countLines(s)` = `s.split("\n").length`. With the `"\n"` join between `prefixText` and `targetText`, the target cell's line 0 sits at source line index `countLines(prefixText)` — whether or not `prefixText` itself ends in a newline — so that is `cellLineOffset` exactly, no `+1`.

### `mapDiagnosticIntoCell`

Wire diagnostic lines are 0-based and already prologue-corrected by the worker. For a wire diagnostic `d`:

```
startLine = d.startLine - cellLineOffset
endLine   = d.endLine   - cellLineOffset
if endLine < 0 || startLine >= cellLineCount   -> null   (belongs to a prefix cell)
startLine = clamp(startLine, 0, cellLineCount - 1)
endLine   = clamp(endLine,   0, cellLineCount - 1)
-> vscode.Diagnostic(new Range(startLine, d.startColumn, endLine, d.endColumn), d.message, severity)
   .code   = d.code ?? undefined
   .source = "F# Query Studio"
```

`cellLineCount` = `targetCell.document.lineCount`. Columns are passed through unchanged (concatenation only shifts lines).

### `versionFor`

The worker keys FSAC's in-memory document by `(DocumentUri, Version)`. `DocumentUri` = the target cell's `document.uri.toString()`. `Version` cannot be `document.version`: the concatenated `source` changes when an upstream executed cell is edited, which does **not** bump the target cell's `document.version`, so FSAC would serve a stale parse. `versionFor(cellUri, source)` keeps a `Map<string, { source: string; version: number }>` and returns an incremented number whenever `source !== last.source` for that URI, else the last number. Every provider path calls it, so a check and a completion on the same unchanged source share a version.

### Providers → VS Code type mapping

| Provider | `analysis/*` result | VS Code mapping |
|---|---|---|
| Completion (`registerCompletionItemProvider`, trigger chars `.` + default) | `{ items: string[] }` | `items.map(n => new vscode.CompletionItem(n))`. No kind/detail. |
| Hover (`registerHoverProvider`) | `{ hover: { displayName, type, signature?, xmlDoc? } \| null }` | `null` → no hover. Else `new vscode.Hover(md)` where `md` is a `MarkdownString`: a fenced `fsharp` block containing `signature ?? \`${displayName} : ${type}\``, then `xmlDoc` appended as plain markdown if present. No range. |
| Signature help (`registerSignatureHelpProvider`, trigger chars `(` `,`) | `{ signatureHelp: { displayName, parameters: string[], returnType } \| null }` | `null` → no help. Else one `SignatureInformation` labelled `${displayName}(${parameters.join(", ")}) : ${returnType}`, one `ParameterInformation` per `parameters` entry, `activeParameter = 0`, `activeSignature = 0`. |
| Diagnostics | `{ diagnostics: WireDiagnostic[] }` | each through `mapDiagnosticIntoCell`, drop `null`, `collection.set(cellUri, kept)`. |

All four resolve the cell and its notebook from the `TextDocument` via `vscode.workspace.notebookDocuments` (find the notebook whose cells include one with this URI); if none, the provider returns nothing (the doc is not a cell).

Interactive providers honour the `CancellationToken` VS Code passes — if cancelled before `send` resolves, return nothing. No manual debounce on the interactive providers: VS Code drives their cadence.

### Diagnostics lifecycle

- **Trigger — edit**: `onDidChangeTextDocument` where `e.document.uri.scheme === "vscode-notebook-cell"` and the doc is an `fsharp` code cell → schedule/reset a 300 ms timer keyed by the cell URI → on fire, `buildAnalysisInput` (no position) → `analysis/check` → map → `collection.set`.
- **Trigger — first sight**: `onDidOpenTextDocument` for a matching cell → run one check immediately (no debounce) so a freshly opened notebook shows squiggles without the user touching every cell. (This is not an execution-driven refresh — it is the baseline for a cell we have not checked yet.)
- **Clear — close**: `onDidCloseTextDocument` → `collection.delete(uri)`, cancel any pending timer, drop the `versionFor` entry.
- **Clear — cell removed**: `onDidChangeNotebookDocument` with a content change that removed cells → for each removed cell, same cleanup as close.
- **`dispose()`**: clear all timers, `collection.dispose()`, dispose every subscription.

A worker error during a check leaves the cell's existing diagnostics in place (the client returns `[]` only on a clean "no diagnostics" result; on an `error` frame it also returns `[]`, matching `AnalysisService.check` — accepted: a transient worker error briefly blanks the squiggles, the next edit restores them).

### `protocol.ts` additions

camelCase field names, matching the existing `EvaluateParams` (the worker's `Serialization.options` applies a camelCase policy):

```ts
export interface AnalysisCheckParams { documentUri: string; version: number; source: string; }
export interface AnalysisPositionParams { documentUri: string; version: number; source: string; line: number; column: number; }
// completion / hover / signatureHelp all send AnalysisPositionParams

export interface WireDiagnostic {
  message: string;
  severity: UnionTag<"Error" | "Warning" | "Information">;   // FSharp.SystemTextJson DU: { "Case": "Error" }
  startLine: number; startColumn: number; endLine: number; endColumn: number;   // 0-based
  code: string | null;
}
export interface AnalysisCheckResult { diagnostics: WireDiagnostic[]; }
export interface AnalysisCompletionResult { items: string[]; }
export interface HoverInfo { displayName: string; type: string; signature: string | null; xmlDoc: string | null; }
export interface AnalysisHoverResult { hover: HoverInfo | null; }
export interface SignatureHelpInfo { displayName: string; parameters: string[]; returnType: string; }
export interface AnalysisSignatureHelpResult { signatureHelp: SignatureHelpInfo | null; }
```

`hover` / `signatureHelp` / `code` are F# `option` / `string option` on the wire — FSharp.SystemTextJson unwraps them to the value or JSON `null` (same convention `ResultEnvelope.displayText` already documents in `protocol.ts`). The severity tag decoder reuses `readUnionTag`.

### Wiring — `extension.ts`

In `activate`, after the renderer host:

```ts
const languageFeatures = registerLanguageFeatures({
  getWorker,                                  // the existing shared-session resolver
  log: (line) => outputChannel!.appendLine(line),
});
context.subscriptions.push(languageFeatures);
```

`getWorker` is the same `() => Promise<WorkerLike>` already passed to the controller and renderer host. No new activation event — `onNotebook:fsquery-notebook` already covers it.

## Known limitations (carried in the DEVLOG entry)

- **Edits-only refresh**: a cell's diagnostics go stale when an upstream executed cell is edited or re-run; they refresh only on that cell's own next edit. Completion/hover/signature help are always live (computed on demand) so they do not have this problem.
- **Post-Restart context**: after `Restart Kernel` (`session/reset`), the executed-cells prefix still includes pre-restart cells until VS Code clears their `executionSummary` (which our own command does not do). Analysis may briefly reflect bindings the live FSI session no longer has.
- **Physically-later prefix cell**: a cell below the target contributes to its analysis context if it executed earlier. This matches real FSI state but can read as surprising.
- **Transient worker error blanks squiggles** for one debounce cycle (see Diagnostics lifecycle).
- The completion / signature-help ceilings noted under Non-goals.

## Testing

### Unit — `vitest`, `src/lang/*.test.ts`

`cell-context.test.ts`:
- prefix filters to `fsharp` code cells with `executionSummary.executionOrder` set; markdown and never-run cells excluded
- prefix sorted by execution order, not document order (row-0 cell that ran second comes after a row-1 cell that ran first)
- target cell excluded from the prefix even when it has executed
- `cellLineOffset` = `countLines(prefixText)`; `0` when the prefix is empty; unchanged whether or not a prefix cell's text ends in a newline
- `position.line` offset by `cellLineOffset`; `column` unchanged
- `mapDiagnosticIntoCell`: shifts lines back; returns `null` when the range is entirely above the cell; clamps a range that straddles the prefix/cell boundary
- `versionFor`: same source → same number; changed source → incremented; independent per URI

`analysis-client.test.ts`:
- each of `check` / `complete` / `hover` / `signatureHelp` sends the right `method` and param shape
- `error` frame → `[]` / `undefined`; absent `result` → `[]` / `undefined`; neither throws

`diagnostics.test.ts`:
- rapid `onDidChangeTextDocument` events coalesce into a single `analysis/check` after the debounce
- `onDidOpenTextDocument` runs an immediate check (no debounce)
- `onDidCloseTextDocument` deletes the cell's diagnostics and cancels a pending timer
- cell removal via `onDidChangeNotebookDocument` clears that cell
- severity / `code` / range land on the collection correctly
- `dispose()` clears timers and the collection

`providers.test.ts`:
- completion names → `CompletionItem[]`
- hover `HoverInfo` → expected `MarkdownString` text; `null` → no hover
- signature help label and `ParameterInformation` list from a fake response; `null` → no help
- a non-cell `TextDocument` → provider returns nothing

### `test/vscode-stub.ts` additions

`languages.registerCompletionItemProvider` / `registerHoverProvider` / `registerSignatureHelpProvider` / `createDiagnosticCollection`; `workspace.onDidChangeTextDocument` / `onDidOpenTextDocument` / `onDidCloseTextDocument` / `onDidChangeNotebookDocument`; `Diagnostic`, `DiagnosticSeverity`, `Range`, `Position`, `Hover`, `MarkdownString`, `CompletionItem`, `CompletionItemKind`, `SignatureHelp`, `SignatureInformation`, `ParameterInformation`, `CompletionList`.

### Integration — `test/integration/`, real downloaded VS Code

Extend the extension integration suite (Ionide not installed):
- open a fixture `.fsqnb` with cell A `let answer = 42` and an empty cell B; execute A; type `answer` in cell B; assert the completion list for cell B contains `answer`
- set cell B to `answer +` (incomplete expression); assert a diagnostic appears on cell B at line 0 within a timeout
- fixture: `test/integration/fixtures/sample-intelligence.fsqnb`

### Manual release checklist (not automated)

- real completion / hover / signature-help interaction in the VS Code UI
- with Ionide installed: no duplicate completion items in cells, no diagnostics from Ionide on cell content

## Rollout

Single phase, own spec → plan → implementation → DEVLOG entry, per the migration design. No dependency on F5+. `main-vscode` branch. Avalonia app stays intact (retired at F6).
