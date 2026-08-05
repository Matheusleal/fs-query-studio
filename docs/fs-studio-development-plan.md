# F# Query Studio — Development Plan

Companion to [`fs-studio-plan.md`](./fs-studio-plan.md), which is the source of truth for architecture, protocol shapes, and project structure. This document breaks that architecture's five-milestone roadmap into a granular, sequential task list for solo execution.

## How to use this plan

- Work tasks **top to bottom**, one at a time. Later tasks assume earlier ones are done.
- Every task tagged `TDD` follows the cycle defined in the architecture spec's [Section 16 — Engineering Standards and TDD Workflow](./fs-studio-plan.md#16-engineering-standards-and-tdd-workflow): write the failing test described under **Test first**, watch it fail, write the minimum code under **Implementation** to pass it, refactor, re-run the whole solution's tests green. That cycle is not repeated in every entry — assume it for every `TDD` task.
- Tasks marked `Infra` are setup/config work with no test-first cycle (there's nothing to red/green — e.g. creating a project file). They still end with `dotnet build`/`dotnet test` passing.
- Each milestone ends with its **acceptance criterion**, taken verbatim from the architecture spec where one exists, or synthesized (marked as such) where the original roadmap didn't state one explicitly for that milestone.
- No CI exists until Milestone D. Until then, `dotnet test` run locally, solution-wide, is the gate for every task.
- Do not build ahead of the current task's milestone — e.g. don't wire persistent sessions (Milestone E) while still on Milestone A, even if it looks convenient. Isolation now, persistence later, is a deliberate design choice, not a placeholder.

---

## Milestone A0 — Scaffolding

Precedes the architecture doc's Milestone A. Nothing here is feature work; it exists so every later task has a project and a test project to land in.

| # | Task | Type |
|---|---|---|
| A0.1 | Create `FSharpQueryStudio.sln` and every `src/` project from [Section 4](./fs-studio-plan.md#4-solution-and-project-structure): `App`, `Ui`, `Editor`, `Protocol`, `Worker`, `Analysis`, `Execution`, `Results`, `Connectors`, `Storage`, `Security`. Empty class libraries/executables, no logic yet. `dotnet build` succeeds for the whole solution. | Infra |
| A0.2 | Create the matching `tests/` project for every `src/` project, plus `FSharpQueryStudio.Integration.Tests` and `FSharpQueryStudio.Ui.Tests`, referencing their corresponding `src/` project. No tests written yet. `dotnet test` succeeds solution-wide with zero tests collected. This is the baseline gate every later task is measured against. | Infra |
| A0.3 | Add `Directory.Build.props` at the solution root: shared target framework, nullable reference types enabled, implicit usings, common analyzers. Confirm it applies to every project via `dotnet build`. | Infra |
| A0.4 | Confirm `.gitignore` covers `bin/`, `obj/`, and any local secret/credential files a future task might introduce (cross-check against [Section 13](./fs-studio-plan.md#13-workspace-secrets-and-sharing-preparation)'s OS credential store note — nothing secret should ever be a file the repo could accidentally track). | Infra |
| A0.5 | Add Serilog to `App` and `Worker` per [Section 15](./fs-studio-plan.md#15-logging-and-observability): `Serilog`, `Serilog.Sinks.File`, `Serilog.Sinks.Console`, `Serilog.Extensions.Logging`, `Serilog.Enrichers.Process`/`Thread`. Configure the console sink and the daily-rolling file sink (`RollingInterval.Day`, 14-day retention) at each composition root, writing to `%LOCALAPPDATA%/FSharpQueryStudio/logs/host-.log` and `.../worker-.log`. Every other project depends only on `Microsoft.Extensions.Logging`'s `ILogger<T>`, never on Serilog directly. | Infra | Verification: run each executable once, confirm a log file dated today appears with at least a startup line. |
| A0.6 | Correlation and redaction enrichers. | TDD | Test first, using an in-memory/test sink: (1) two log calls made while a `CorrelationId` is pushed via `LogContext.PushProperty` both carry the same ID, calls outside that scope don't; (2) a log call containing a connection-string-shaped value or a header named `Authorization` has that value replaced before it reaches the sink. Both assert on captured event properties, not the real file system. |

---

## Milestone A — Windows Vertical Slice

Goal: a script can be opened, run in isolation, show output, be cancelled, and survive a worker crash — with the UI and worker talking over a real protocol, not a stub.

| # | Task | Type | Notes |
|---|---|---|---|
| A.1 | Protocol envelope and NDJSON frame types (`EditorEnvelope`, worker request/response frame) in `Protocol`. | TDD | Test first: round-trip serialize/deserialize for each frame shape, including an unknown/extra field surviving round-trip (forward-compat). |
| A.2 | NDJSON stream reader/writer (one JSON object per line over stdio) in `Worker`. | TDD | Test first: a message split across multiple buffer reads still parses correctly; a payload containing embedded `\n` inside a JSON string is not mistaken for a frame boundary; Unicode payloads round-trip. |
| A.3 | Worker process bootstrap and `worker/health` handshake. | TDD | `Worker.Tests` unit-tests the handler; `Integration.Tests` starts the real worker executable and asserts a health response comes back. |
| A.4 | `session/create`: isolated `FsiEvaluationSession` construction in `Execution`, controlled working directory, redirected output, no interactive prompts. | TDD | Integration test: create a session, confirm no console prompt hangs the process. |
| A.5 | `execution/evaluate` for a whole script: capture stdout/stderr, return raw evaluation outcome. | TDD | Integration test: evaluate `printfn "hi"; 1 + 1` and assert captured output plus a raw scalar result. |
| A.6 | Result projection for `Scalar`, `Text`, and `Table` (`ResultEnvelope`/`ResultKind` from [Section 11](./fs-studio-plan.md#11-result-projection-and-rendering)) — enough for the vertical slice; `Object`/`Error`/`NoValue` land with their consumers in later tasks. | TDD | `Results.Tests`: one test per projection rule row in the Section 11 table for these three kinds, plus a truncation test at the configured preview-row limit. |
| A.7 | `Error` projection: uncaught script exception becomes a structured `ResultEnvelope` with message/details, never an unhandled worker crash. | TDD | Test first: a script that throws produces `ResultKind.Error` with a non-empty message, and the worker process is still alive afterward. |
| A.8 | Host-side worker supervision: detect exit/crash (process `Exited` event or stdout stream closing unexpectedly), per [Section 9's crash-and-restart ownership](./fs-studio-plan.md#9-execution-model-isolated-by-default). | TDD | Integration test: kill the worker process mid-request; assert the in-flight request surfaces a structured execution-error and the *next* request gets a freshly started worker (no auto-retry of the failed one). |
| A.9 | `execution/cancel`: cooperative cancellation, then forced termination after a grace period. | TDD | Integration test covers both paths: a script that checks a cancellation token and stops cleanly, and one that doesn't (forced termination, reported as forcibly stopped). |
| A.10 | Avalonia shell: fixed multi-pane `Grid` layout, `ShellViewModel`, `WorkerStateViewModel` (`Starting`/`Ready`/`Running`/`Cancelling`/`Stopped`/`Faulted`). | TDD | `Ui.Tests` (Avalonia.Headless): state transitions on `WorkerStateViewModel` follow the defined set; invalid transitions are rejected. |
| A.11 | Loopback asset host for Monaco: binds `127.0.0.1` only, ephemeral port, serves only embedded/static assets. | TDD | Test first: a path-traversal request (`../../secret.txt`) is rejected; a request for an unknown asset 404s; a request for a known asset succeeds; attempting to bind on all interfaces is not exercised by the code path at all (no such code path exists). |
| A.12 | Editor bridge message contract: `EditorRequest`/host-to-editor message serialization, both directions. | TDD | `Editor.Tests`: serialize/deserialize every `EditorRequest` case from [Section 6](./fs-studio-plan.md#6-monaco-editor-integration); a malformed envelope produces a typed error, not an exception escaping the bridge. |
| A.13 | WebView host wiring: `editor-host.html` + Monaco loads inside the Windows WebView2-backed control, bridge connects. | Manual | Avalonia WebView rendering isn't headlessly automatable — verify manually per this task, and again at the milestone acceptance check. Keep this the *only* manual-verification task in the milestone; everything either side of it stays automated. |
| A.14 | Wire `TextChanged` from Monaco through the bridge to a `DocumentViewModel` (text, URI, version, dirty state) — no analysis yet, just plumbing. | TDD | `Ui.Tests`: a bridge `TextChanged` message updates `DocumentViewModel.Version` monotonically and marks it dirty. |
| A.15 | Wire Run (F5) end to end: `Ui` command → host service → `execution/evaluate` → `ResultEnvelope` → `ResultViewModel`. | TDD | `Ui.Tests` for the view-model wiring; `Integration.Tests` for the full round trip through a real worker. |
| A.16 | Output console, scalar view, and basic table preview binding, using Avalonia `DataGrid` virtualization. | TDD | `Ui.Tests`: binding a `ResultEnvelope` of each kind (`Scalar`, `Text`, `Table`, `Error`) produces the expected view-model state; enumeration never happens on the UI thread (assert via a slow/blocking fake sequence not stalling the dispatcher in the test). |
| A.17 | Wire Cancel button to `execution/cancel`, reflected in `WorkerStateViewModel`. | TDD | `Ui.Tests` + `Integration.Tests` covering the cooperative and forced-termination paths from A.9, now through the UI command. |
| A.18 | Instrument the Milestone A code paths with the structured log events from [Section 15's table](./fs-studio-plan.md#15-logging-and-observability): worker start/stop/crash/restart, session create, execution start/elapsed/outcome, cancellation requested, each carrying the request's `CorrelationId`. | TDD | Test first, in-memory sink: driving A.3–A.9's handlers emits the expected event for each, with the correlation ID from the triggering request attached. |
| A.19 | Milestone acceptance test. | TDD | `Integration.Tests`: open a standalone `.fsx` script, execute it in isolation, read output/results, cancel it, and recover from a worker crash — all without restarting the UI process. |

**Acceptance criterion** (from architecture spec): A user can open a standalone `.fsx` script, execute it in isolation, read output/results, cancel it, and recover from a worker crash without restarting the UI.

---

## Milestone B — F# Editor Intelligence

Goal: FCS-backed diagnostics, hover, and completions, with stale responses never clobbering current editor state.

| # | Task | Type | Notes |
|---|---|---|---|
| B.1 | Standalone-script analysis context builder in `Analysis`: baseline FSharp.Core/framework references, deterministic working-directory/reference-search policy. | TDD | Test first: building the context twice for the same inputs produces byte-identical reference sets (determinism), per [Section 8](./fs-studio-plan.md#8-f-analysis-with-fsharpcompilerservice). |
| B.2 | `analysis/check` handler: parse/type-check a source snapshot via FCS. | TDD | Worker-level integration test: a script with a type error returns at least one FCS diagnostic. |
| B.3 | FCS-to-protocol `Diagnostic` mapping and coordinate conversion (1-based FCS vs 0-based Monaco). | TDD | `Analysis.Tests`: explicit cases for multiline spans, empty lines, end-of-file spans, and Unicode source, per [Section 8](./fs-studio-plan.md#8-f-analysis-with-fsharpcompilerservice)'s explicit call-out. |
| B.4 | Debounce source-change requests in the UI (300–500ms) before sending `analysis/check`. | TDD | `Ui.Tests` with a virtual/fake clock: rapid successive keystrokes produce exactly one request after the debounce window. |
| B.5 | Document-version guard: a response is applied only if its version equals the current document version. | TDD | Test first: simulate an out-of-order response (older version arriving after a newer one) and assert it's discarded, not applied. |
| B.6 | Monaco marker rendering from `Diagnostic` list, via the editor bridge. | TDD | `Editor.Tests`: bridge message contract test (diagnostics-in → marker-message-out shape); actual Monaco marker rendering confirmed manually at milestone acceptance, same rationale as A.13. |
| B.7 | Problems pane: `ProblemViewModel` list bound to current diagnostics, with a navigation command that moves the editor caret to the diagnostic's span. | TDD | `Ui.Tests`: invoking the navigation command produces the expected caret-position bridge message. |
| B.8 | `analysis/hover`: FCS hover data (display name, inferred type, signature where available, XML doc when present) mapped to protocol `HoverInfo`. | TDD | `Analysis.Tests` covering a typed `let` binding, a function with a signature, and a symbol with no XML doc (graceful `None`, not an error). |
| B.9 | Hover wiring through the bridge to a Monaco hover provider. | TDD | `Editor.Tests` bridge contract test, same pattern as B.6. |
| B.10 | Completions tier 1 (MVP): keywords, snippets, connector helper names, names from the most recent successful check. | TDD | `Analysis.Tests`: a known keyword and a known connector helper name both appear in the completion list at an arbitrary cursor position. |
| B.11 | Completions tier 2 (semantic): FCS completion data at cursor position. | TDD | `Analysis.Tests` with a fake/minimal script exercising member access (`foo.` triggers member completions); one integration test against a real FCS session for a known standard-library symbol. |
| B.12 | Milestone acceptance test. | TDD | Integration/UI test: invalid F# produces squiggles, hovering a local symbol shows its type, and an artificially delayed stale analysis response never overwrites newer editor state (reuses the B.5 guard under a real request/response race). |

**Acceptance criterion** (from architecture spec): Invalid F# produces useful squiggles, local symbols/type information are discoverable, and stale analysis never overwrites current editor state.

---

## Milestone C — Query Workflow and All First-Day Connectors

Goal: every connector from day one, wired through controlled script helpers, with bounded/paged/exportable results and a package approval workflow.

| # | Task | Type | Notes |
|---|---|---|---|
| C.1 | Workspace/script explorer view model and multi-tab document management. | TDD | `Ui.Tests`: opening a second script creates a second tab without disturbing the first's state; closing a dirty tab prompts before discarding. |
| C.2 | `ConnectorKind`/`ConnectionDefinition` protocol types and connector abstraction interface. | TDD | `Protocol.Tests` round-trip serialization; `Connectors.Tests` for the interface contract (a fake connector implementation satisfies it). |
| C.3 | `connection/register` handler plus `workspace.json` read/write for non-secret connection metadata. | TDD | `Storage.Tests`: round-trip a `ConnectionDefinition` list through a temp-directory workspace file, secrets never present in the written JSON. |
| C.4 | Secret-reference abstraction over the OS credential store. | TDD | `Security.Tests`/`Storage.Tests` against an in-memory fake store; one `Integration.Tests` case against the real Windows credential manager, marked Windows-only. |
| C.5 | SQLite connector: connection, parameterized commands, reader-based paging, schema discovery. | TDD | `Connectors.Tests` for parameter-binding logic; integration test against a real temp SQLite file. |
| C.6 | SQL Server connector, same capability shape as C.5. | TDD | Integration test against a local dev instance or test container — never a shared/remote service. |
| C.7 | PostgreSQL connector, same capability shape as C.5. | TDD | Integration test against a local dev instance or test container. |
| C.8 | CSV connector: delimiter/encoding/header options, streaming read, typed conversion helpers. | TDD | Integration tests against temp fixtures covering quoted commas, embedded newlines, and at least one non-UTF8 encoding. |
| C.9 | Excel connector: workbook/sheet selection, header options, bounded worksheet reads. | TDD | Integration tests against temp `.xlsx` fixtures covering multiple sheets and a header-less sheet. |
| C.10 | REST/JSON connector: URL/method/header/query/body configuration, JSON parsing, pagination helpers. | TDD | Integration test against a local HTTP stub (in-process test server) — never a real external endpoint. |
| C.11 | Controlled script API surface (e.g. `Connections.sql "Sales" "select ... where x = @p" [...]`) exposed to `.fsx` scripts. | TDD | Test first: a script attempting to interpolate a parameter directly into SQL text is the *wrong* usage the API structurally prevents — write a regression test asserting parameters are always bound, never concatenated, for every connector from C.5–C.10. |
| C.12 | Results tabs and virtualized `DataGrid` binding for table results, building on A.16. | TDD | `Ui.Tests`. |
| C.13 | `results/page` end-to-end wiring using the `ResultPage`/`ContinuationToken` shape from [Section 7](./fs-studio-plan.md#7-host--worker-protocol). | TDD | Integration test: a large sequence truncates at the preview-row limit, then pages further via the token; a request with an unknown/expired token returns a structured error, not a silent re-query. |
| C.14 | CSV export as a worker-side streamed operation (not limited to the current UI viewport). | TDD | `Results.Tests`/`Connectors.Tests`: exported file matches full source data, including rows beyond what the UI currently renders. |
| C.15 | Copy-to-clipboard for projected results. | TDD | `Ui.Tests`. |
| C.16 | Package manifest (`packages.json`) and `#r "nuget: ..."` approval workflow: unknown package triggers approval request; approved references require a session reset. | TDD | `Storage.Tests` for manifest read/write; integration test asserting an unlisted package is rejected until approved, and that a reference change invalidates the current session. |
| C.17 | Connector call logging: kind, elapsed time, and row count logged for every query per [Section 15](./fs-studio-plan.md#15-logging-and-observability); parameter values are never logged. | TDD | Test first, in-memory sink: running a query against a fake connector emits one event with those three fields, and asserting the parameter values passed in are absent from every captured event's properties. |
| C.18 | Milestone acceptance test. | TDD | Integration test: each of the six connectors is configured, queried through its F# helper, and its result is displayed and exported through the bounded/paged pipeline. |

**Acceptance criterion** (from architecture spec): Each supported data target can be configured, queried through F# helpers, and displayed/exported through bounded results.

---

## Milestone D — Hardening and Sharing Foundations

Goal: trust/capability metadata becomes real and visible, logs are safe to share, and CI exists — deliberately not before now.

| # | Task | Type | Notes |
|---|---|---|---|
| D.1 | `trust.json` read/write matching the schema in [Section 13](./fs-studio-plan.md#13-workspace-secrets-and-sharing-preparation). | TDD | `Storage.Tests`: round-trip every field; a missing file defaults to `origin: local`, `trustState: unknown`. |
| D.2 | Origin detection logic (local / imported / downloaded / shared-workspace). | TDD | `Security.Tests`: each origin case driven by how a workspace was opened (created fresh vs. opened from an external path vs. opened from a marked shared source). |
| D.3 | Capability approval UX: list requested capabilities before running an untrusted script, block until approved or denied. | TDD | `Ui.Tests` (headless): approving updates `trustState`; denying prevents `execution/evaluate` from being sent. |
| D.4 | Diagnostic-bundle export: zip the last N days of host and worker Serilog log files (already redacted at write time by A0.6) for sharing during troubleshooting. | TDD | Test first: bundling a temp logs directory produces a zip containing the expected files; a defense-in-depth scan of the bundle's contents against known secret-shaped patterns finds none, confirming A0.6's redaction actually held across every log call made since Milestone A. |
| D.5 | Dependency reproducibility: lock resolved package versions/hashes into `packages.json`. | TDD | `Storage.Tests`: two restores from the same manifest resolve to identical locked versions. |
| D.6 | GitHub Actions CI pipeline: build + run the full test suite (unit, integration, `Ui.Tests`) on push. This is the CI task deferred since [Section 16](./fs-studio-plan.md#16-engineering-standards-and-tdd-workflow) — it belongs here, not earlier. | Infra | Verification: push a branch, confirm the workflow runs and is green; intentionally break a test, confirm the workflow goes red. |
| D.7 | Windows packaging/installer, with an automated test around the published artifact. | TDD | Integration test: self-contained publish output launches and reaches a ready state on a clean environment. |
| D.8 | Extend CI to a Linux compatibility matrix for non-UI projects (`Protocol`, `Analysis`, `Execution`, `Results`, `Connectors`, `Storage`, `Security`, `Worker`, `Integration.Tests`). | Infra | Verification: the same CI workflow's Linux job is green for every non-UI test project; `Ui`/`Editor`/`App` stay Windows-only for now, per [Section 14](./fs-studio-plan.md#14-windows-first-cross-platform-ready-design). |
| D.9 | Milestone acceptance test. | TDD | *Synthesized — the architecture spec doesn't state one for this milestone.* Integration test: an imported/untrusted script shows its requested capabilities and requires explicit approval before running; CI is green on a fresh push; a packaged Windows build launches from a clean machine image. |

---

## Milestone E — Persistent and Project-Based Futures

Goal: opt-in persistent sessions, notebook-style execution, multi-file projects, and richer navigation — all additive to the isolated-by-default model, never replacing it.

| # | Task | Type | Notes |
|---|---|---|---|
| E.1 | Opt-in persistent per-tab `FsiEvaluationSession` mode. | TDD | `Execution.Tests`/`Integration.Tests`: a paired test asserting bindings survive across two sequential executions in Persistent mode, and explicitly do *not* survive in Isolated mode — the same assertion both ways guards against regressing the Milestone A default. |
| E.2 | `Reset Session` command and session-state display in the UI. | TDD | `Ui.Tests`. |
| E.3 | Session-invalidation rules on script edit (define which edits invalidate persistent state, then implement it). | TDD | `Execution.Tests` codifying the rule as explicit cases, not just prose — e.g. editing an already-executed cell invalidates from that point forward. |
| E.4 | Notebook `// %%` cell parsing and ordered incremental cell execution. | TDD | `Analysis.Tests` for cell-boundary detection; `Integration.Tests` running a multi-cell script and asserting later cells see earlier cells' bindings only within a persistent/notebook session. |
| E.5 | `.fsproj`/multi-file workspace support: project/file graph model. | TDD | `Storage.Tests`/`Analysis.Tests`: a project referencing multiple files resolves a correct compile order. |
| E.6 | Go-to-definition. | TDD | `Analysis.Tests` for symbol resolution; `Editor.Tests` bridge contract test for the navigation message. |
| E.7 | Signature help. | TDD | `Analysis.Tests` + bridge contract test, same pattern as E.6. |
| E.8 | Rename/refactoring support. | TDD | `Analysis.Tests`: rename updates every reference within the analyzed scope, not just the declaration. |
| E.9 | Official Linux/macOS UI support: validate the WebView backend abstraction on a second platform, extend CI's `Ui.Tests` job accordingly. | Infra + TDD | Depends on the WebView/editor-host interface boundary from [Section 6](./fs-studio-plan.md#6-monaco-editor-integration) and [Section 14](./fs-studio-plan.md#14-windows-first-cross-platform-ready-design) having stayed clean through A–D; if it hasn't, this task starts with the refactor to make it so. |
| E.10 | Milestone acceptance test. | TDD | *Synthesized — the architecture spec doesn't state one for this milestone.* Integration test: a persistent-mode session retains bindings across executions until reset, a notebook script runs cell-by-cell, a multi-file `.fsproj` workspace opens and analyzes correctly, and the UI runs on at least one non-Windows platform in CI. |

---

## What this plan deliberately doesn't do

- It doesn't estimate time. No deadline exists for this project; sequencing correctness matters more than a schedule.
- It doesn't pre-plan CI, sandboxing enforcement, or cross-platform UI ahead of the milestones that own them (D and E respectively) — see [Section 16](./fs-studio-plan.md#16-engineering-standards-and-tdd-workflow) and [Section 19](./fs-studio-plan.md#19-key-technical-risks-and-mitigations) for why that sequencing is deliberate, not an oversight.
- It doesn't enumerate every trivial task (e.g. adding a single XAML style) — granularity stops at "one coherent TDD cycle," not "one line of code."
