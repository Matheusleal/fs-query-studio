# FSAC as F# Analysis Backend — Design

First of four planned sub-projects toward using Ionide's tooling to improve the F# coding experience in the Monaco editor (FSAC backend → Fantomas formatting → FSharp.Analyzers.SDK lint → Ionide Monaco/UI conventions). Scoped down during brainstorming: FSAC already bundles Fantomas formatting (`textDocument/formatting`) and FSharp.Analyzers.SDK diagnostics (`fsharp/analyzerDiagnostics`) as LSP extensions, so sub-projects 2 and 3 become short follow-ons riding the same connection once this lands, rather than separate integrations. Sub-project 4 (completion icons, hover markdown conventions) is cosmetic and independent, deferred.

## Background

`FSharpQueryStudio.Analysis` (660 lines: `Checker`, `Completions`, `DiagnosticMapping`, `GoToDefinition`, `Hover`, `Rename`, `SignatureHelp`) wraps `FSharp.Compiler.Service` directly. Every `analysis/*` Worker method (`Handlers.fs`) is stateless per request — a fresh `AnalysisContext.build` plus a fresh FCS check on every single call, no server-side document tracking, since the request always carries the full source snapshot (`AnalysisCheckParams.Source`, etc.). `analysis/*` requests already run backgrounded off the Worker's main read loop (`Task.Run`, `Program.fs`), since `analysis/complete` fires on every keystroke and must never stall a concurrent `execution/cancel`.

`fs-studio-plan.md`'s Version 1 exclusions list "a complete Visual Studio or Ionide-compatible project system" as out of scope — that's about not building a project system; this sub-project swaps an internal analysis engine and changes nothing about the standalone-`.fsx` script model.

`AnalysisContext.build` injects a controlled prologue (`open FSharpQueryStudio.Connectors\nopen FSharpQueryStudio.Results\n`, `prologueLineCount = 2`) plus two `-r:` `otherFlags` (the `Connectors`/`Results` assembly paths) ahead of every checked script, so a script can call `Connections.sql`/`Results.table` unqualified — matching what `FsiSession.create` already does for real execution. Every analysis function then applies `AnalysisContext.lineOffset` to shift positions between the user's real (unprefixed) source and what FCS actually checked. This machinery has no FSAC equivalent out of the box and must be reproduced at the new translation layer (see Architecture).

`AnalysisContext.buildForProject` (multi-file `.fsproj` support, referenced as "Milestone E.5" in its own doc comments) is unwired dead code today — no handler calls it. Confirmed via a repo-wide search: `buildForProject` has exactly one match, its own definition.

## Goals

- Replace `FSharpQueryStudio.Analysis`'s FCS-direct implementation with `fsautocomplete` (FSAC) as the backend for all six `analysis/*` methods: `check`, `hover`, `definition`, `signatureHelp`, `complete`, `rename`.
- Zero visible change to `Protocol`, `EditorBridge`, the App host, or `editor-host.html`/Monaco JS — every existing wire type (`AnalysisCheckResult`, `AnalysisHoverResult`, etc.) keeps its current shape.
- Preserve the `Connections`/`Results` unqualified-resolution behavior with no dependency on FSAC exposing any custom configuration knob — via literal `#r`/`open` lines in the prologue instead of FCS `otherFlags`.
- FSAC runs fully offline: bundled as a self-contained executable in the build output, no tool-restore or network access at runtime — same convention as the bundled Monaco assets (`AssetHost.fs`).
- A dead or hung FSAC process never blocks or crashes `execution/evaluate` — analysis and execution stay fully independent failure domains, same as they already are today (`Analysis` and `Execution` are separate projects; nothing in `runEvaluate` touches `Analysis`).

## Non-goals

- Enriching `AnalysisCompletionResult` beyond its current flat `Items: string list` shape. FSAC's `textDocument/completion` response carries real `kind`/`detail`/`documentation` per item; this sub-project only takes the `label` of each and flattens the rest away, matching what the app already does with today's FCS-direct completions. Consuming the richer shape is a natural, cheap follow-up, deliberately deferred so this stays a pure backend swap.
- Fantomas formatting, FSharp.Analyzers.SDK diagnostics, Ionide-style Monaco completion icons/hover markdown — each its own short spec once this lands (see intro).
- Multi-file `.fsproj` project analysis. `buildForProject` is unwired today (see Background); this design only replaces the standalone-script path real handlers actually call (`AnalysisContext.build`). Wiring multi-file analysis, through FSAC or otherwise, is a separate, not-yet-scheduled piece of work.
- Any change to `execution/evaluate` or the FSI execution path — untouched, different subsystem.
- Worker-side document state beyond what satisfying the LSP protocol shape strictly requires. `didOpen`/`didChange` calls exist to speak valid LSP to FSAC, not to add new caching/statefulness to the Worker.

## Architecture

New project, `FSharpQueryStudio.Fsac`, replacing `FSharpQueryStudio.Analysis`'s role:

- **`FsacProcess`** — spawns and supervises the bundled `fsautocomplete` child process. Crash detection/restart mirrors the existing worker-crash-detection pattern (`docs/DEVLOG.md`'s A.8 entry) applied one process level down: the App supervises the Worker, the Worker now supervises FSAC the same way.
- **`LspClient`** — a new stdio transport using LSP's `Content-Length:`-framed JSON-RPC (distinct from `NdjsonStream`, which is the App↔Worker leg's own newline-delimited framing and stays exactly as-is). Owns request/response correlation by LSP id and a separate dispatch path for server-initiated notifications (`publishDiagnostics`).
- **`FsacBridge`** — the translation layer. Takes the existing `AnalysisCheckParams`/`AnalysisHoverParams`/etc. unchanged, drives the right LSP calls through `LspClient`, and maps LSP responses back into the existing `AnalysisCheckResult`/`AnalysisHoverResult`/etc. Owns the ported prologue/line-offset logic (see Data flow).

`FSharpQueryStudio.Analysis` is retired once `FsacBridge` covers all six methods and the new test suite (see Testing) passes — see Rollout below.

### Packaging

`fsautocomplete` ships on NuGet as a dotnet tool package. Restored at build time via `dotnet tool install fsautocomplete --tool-path <output>/FsacTool` (a new build step, checked into the build alongside however `EditorAssets` already gets copied into `App`'s/`Worker`'s output), so the running app never restores or reaches the network. `FsacProcess` locates the bundled executable via `AppContext.BaseDirectory`, the same lookup pattern the existing Worker-subprocess integration test (A.3, `docs/DEVLOG.md`) already uses to find the Worker's own compiled output.

### Method mapping

| Worker method | FSAC/LSP calls | Notes |
|---|---|---|
| `analysis/check` | `didOpen`/`didChange`, then await `publishDiagnostics` | Push-notification bridge — see Data flow step 5. |
| `analysis/hover` | `didOpen`/`didChange`, `textDocument/hover` | Direct request/response. |
| `analysis/definition` | `didOpen`/`didChange`, `textDocument/definition` | Direct request/response; the existing same-file-declaration `FilePath` rewrite (today's `handleAnalysisDefinition`, swapping the throwaway analysis-context script path for the real `DocumentUri`) carries over unchanged. |
| `analysis/signatureHelp` | `didOpen`/`didChange`, `textDocument/signatureHelp` | Direct request/response. |
| `analysis/complete` | `didOpen`/`didChange`, `textDocument/completion` | Response is flattened: each `CompletionItem.label` becomes one `Items` entry, `kind`/`detail`/`documentation` discarded (see Non-goals). |
| `analysis/rename` | `didOpen`/`didChange`, `textDocument/references` (`includeDeclaration = true`) | **Not** `textDocument/rename`. `AnalysisRenameParams` carries no `newName` and `AnalysisRenameResult` carries no replacement text — this app only ever returns reference spans and applies the edit client-side in Monaco. LSP `rename` computes and returns a `WorkspaceEdit` with real replacement text, which is the wrong shape entirely; `references` with the declaration included is what actually matches today's `Rename.findReferences` behavior. |

### Data flow, per `analysis/*` request

1. Worker receives the request exactly as today, full `Source` text included.
2. `FsacBridge` builds the checked source: prepends `open FSharpQueryStudio.Connectors\nopen FSharpQueryStudio.Results\n`, exactly as `AnalysisContext.sourceForCheck` does today, but replaces the current `-r:` `otherFlags` with two real `#r "<absolute path>"` lines physically in the prologue instead (one for `Connectors.dll`, one for `Results.dll`). This works through FSAC's own FCS-backed script handling with no FSAC-side configuration dependency — `#r` is an ordinary, well-supported FCS/FSI script directive. `prologueLineCount` (now counting the `#r` lines too) and `lineOffset` port over unchanged in spirit, applied at this layer instead of inside `AnalysisContext`.
3. `FsacBridge` sends `textDocument/didOpen` (first call for a `DocumentUri`) or `textDocument/didChange` (subsequent calls), using the request's own `DocumentUri`/`Version` as the LSP document identity and version.
4. For the five request-shaped methods (everything but `check`), `FsacBridge` awaits the matching LSP request directly, shifts every returned position back through the inverse of step 2's offset, and maps the result into the existing `Protocol` result type.
5. For `analysis/check`, diagnostics arrive as a server-pushed `textDocument/publishDiagnostics` notification, not a request/response — LSP has no "check and tell me the diagnostics" request. `FsacBridge` keeps a version-keyed table of pending `TaskCompletionSource<Diagnostic list>` per `DocumentUri`; after sending `didOpen`/`didChange` for version `v`, it registers a pending entry for `(uri, v)` and awaits it, bounded by a timeout (see Error handling). The notification-dispatch path in `LspClient` resolves the matching entry when `publishDiagnostics` arrives for that `uri`/version. A notification for a `uri`/version that's already been superseded by a newer `didChange` (no pending entry exists any more, or a fresher version has been registered since) is discarded — mirroring the "stale response ignored" version-guard principle Section 7 already applies on the App↔Worker leg, now applied on the Worker↔FSAC leg.

### Error handling

- **FSAC crash or unresponsive process**: detected by `FsacProcess`; in-flight and new `analysis/*` requests get a structured `analysis/backendUnavailable` error rather than hanging. FSAC restarts lazily on the next `analysis/*` call. `execution/evaluate` is entirely unaffected — it has no dependency on `Analysis`/`Fsac` today and none is introduced here.
- **`publishDiagnostics` timeout**: bounded wait (a few seconds); on timeout, the pending entry is resolved with a structured error, logged, and removed — never an indefinite hang of the calling `analysis/check` request or the read loop.
- **Version staleness**: handled by the pending-entry table itself, as described in Data flow step 5.
- **Packaging/startup failure** (missing bundled executable, wrong runtime): surfaces as a clear, structured error on the first `analysis/*` request that needs FSAC, not a Worker crash — an execution-only session (no analysis calls made) keeps working regardless.

## Testing

- `LspClient` (`Fsac.Tests`): Content-Length framing round-trip — fragmented stream (one byte per read, same style as the existing `NdjsonStream` fragmented-stream test), embedded-newline and Unicode payloads, end-of-stream handling; request/response correlation; notification dispatch to a registered handler.
- `FsacBridge` integration tests (`Fsac.Tests` or `Worker.Tests`): spawn the real bundled `fsautocomplete`, same pattern as A.3's real-Worker-subprocess integration test. Cover: hover/definition/signatureHelp/completion/rename against real scripts with known expected results (porting the *behavioral* cases from today's `Analysis.Tests`, not its implementation); `analysis/check` diagnostics round trip including the publishDiagnostics wait; the prologue/line-offset math (a diagnostic or hover reported on the script's own first real line maps back to line 0, not line 2).
- Crash/restart/timeout paths get explicit tests: kill the FSAC process mid-request → structured error, Worker read loop stays alive, a subsequent request succeeds after FSAC restarts; a `publishDiagnostics` that never arrives → timeout error, no hang.
- Packaging: a build-output check that the bundled `fsautocomplete` executable is actually present and launchable, so a broken build step fails fast in CI rather than surfacing as a runtime `backendUnavailable` error later.
- `FSharpQueryStudio.Analysis.Tests`: behavioral cases (not implementation-detail ones) migrate into the new integration suite as black-box expectations against the new backend; tests of retired modules (`Checker`, `Completions`, `Hover`, `GoToDefinition`, `Rename`, `SignatureHelp` as FCS-direct code) are deleted alongside the modules themselves.

## Rollout

Single cutover, not incremental per-method: `Worker.Handlers.dispatch` already routes every `analysis/*` method uniformly, and running FCS-direct and FSAC side by side in production would mean keeping two engines agreeing on identical wire output for no real benefit — added complexity with no payoff. All six methods land together behind one branch, `FSharpQueryStudio.Analysis` is deleted in the same change once the new suite is green.
