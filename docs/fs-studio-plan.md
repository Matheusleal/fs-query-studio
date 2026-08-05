# F# Query Studio — Architecture and Technical Implementation Plan

## 1. Product Definition

**F# Query Studio** is a Windows desktop application for writing, analyzing, and executing F# query scripts. It is inspired by query tools such as SSMS and DAX Studio, but F# is the query language.

The application lets local developers:

- Write standalone `.fsx` scripts in a Monaco editor.
- Execute scripts in isolated worker processes.
- Receive F# type-aware diagnostics, completions, and hover information.
- Query SQL databases, PostgreSQL, SQLite, CSV files, Excel workbooks, and REST/JSON APIs.
- View bounded, tabular results alongside standard output, errors, and diagnostics.
- Save scripts and workspace definitions without persisting secrets in source files.

### Initial product constraints

| Area | Initial decision | Future direction |
|---|---|---|
| Platforms | Windows only | Keep UI, protocol, worker, storage, and connectors cross-platform-ready for Linux and macOS. |
| Users/trust | Local developers on their own machines | Prepare capability controls and trust metadata for downloaded/shared scripts. |
| Data targets | SQL Server, PostgreSQL, SQLite, CSV, Excel, REST/JSON from day one | Add providers and reusable connector packages later. |
| Execution | Fresh isolated execution by default | Add opt-in persistent REPL/notebook sessions. |
| Script model | Standalone `.fsx` files | Add `.fsproj` / multi-file workspace support later. |

---

## 2. Scope and Product Boundaries

F# Query Studio should be a **query and exploration environment**, rather than a replacement for a full F# IDE.

### Version 1 includes

- Workspace and standalone script management.
- F# editing with syntax highlighting, diagnostics, completion, and hover.
- Isolated execution of a selected expression, cell, or complete script.
- Connections to the supported data targets.
- Table, text, object, error, and output-result views.
- CSV export and copy-to-clipboard for projected results.
- Connection definitions with secrets stored outside workspace files.

### Version 1 excludes

- Full debugger support.
- A complete Visual Studio or Ionide-compatible project system.
- General-purpose security sandboxing of hostile code.
- Collaborative editing or remote execution.
- Guaranteed support for every F# type provider.

---

## 3. Recommended Architecture

Use a multi-process design:

1. **Avalonia host process**: trusted Windows UI, workspace persistence, WebView/Monaco host, connection configuration, worker supervision.
2. **F# worker process**: F# compiler analysis, isolated script execution, data-source access, bounded result projection.
3. **Monaco editor frame**: local application assets loaded inside the WebView; it communicates only through a typed JSON bridge.

```text
┌────────────────────────────────────────────────────────────────────┐
│ Windows: Avalonia host process                                      │
│                                                                    │
│  ┌─────────────────────┐      JSON messages      ┌──────────────┐ │
│  │ Monaco WebView      │ ◄─────────────────────► │ Avalonia UI  │ │
│  │ editor-host.html    │                          │ services     │ │
│  └─────────────────────┘                          └──────┬───────┘ │
│                                                            │         │
│                              JSON-RPC / NDJSON over stdio  │         │
└────────────────────────────────────────────────────────────┼─────────┘
                                                             │
┌────────────────────────────────────────────────────────────┼─────────┐
│ Restartable F# worker process                               ▼         │
│  ┌─────────────────────┐  ┌──────────────────┐  ┌─────────────────┐ │
│  │ FCS analysis        │  │ FSI execution    │  │ Result projector │ │
│  │ diagnostics/hover/  │  │ isolated session │  │ schema/rows/text │ │
│  │ completions         │  │ stdout/stderr    │  │ paging/truncation│ │
│  └─────────────────────┘  └──────────────────┘  └─────────────────┘ │
│                    │                  │                  │          │
│                    └──────────────────┴──────────────────┘          │
│                                       │                               │
│       SQL Server · PostgreSQL · SQLite · CSV · Excel · REST/JSON    │
└────────────────────────────────────────────────────────────────────┘
```

### Why a worker process is required

Do not execute scripts in the Avalonia UI process. Arbitrary script code can hang, consume memory, terminate unexpectedly, load incompatible assemblies, or otherwise destabilize the application.

A separate worker is a **reliability boundary**:

- A failed worker can be terminated and restarted.
- The UI stays responsive during execution and cancellation.
- Future platform-specific sandboxing policies have a clear place to live.
- Persistent execution can later be introduced as an explicit worker/session mode.

It is **not**, by itself, a security sandbox against malicious code.

---

## 4. Solution and Project Structure

```text
FSharpQueryStudio.sln
├── src/
│   ├── FSharpQueryStudio.App/          # Avalonia executable/composition root
│   ├── FSharpQueryStudio.Ui/           # XAML, view models, commands
│   ├── FSharpQueryStudio.Editor/       # WebView + Monaco assets and message bridge
│   ├── FSharpQueryStudio.Protocol/     # Shared protocol DTOs and serialization
│   ├── FSharpQueryStudio.Worker/       # Worker executable and request routing
│   ├── FSharpQueryStudio.Analysis/     # FSharp.Compiler.Service integration
│   ├── FSharpQueryStudio.Execution/    # FSI process/session management
│   ├── FSharpQueryStudio.Results/      # Bounded result projection and export
│   ├── FSharpQueryStudio.Connectors/   # Connector abstraction and implementations
│   ├── FSharpQueryStudio.Storage/      # Workspace, preferences, secret references
│   └── FSharpQueryStudio.Security/     # Trust policy/capabilities, initially small
└── tests/
    ├── FSharpQueryStudio.Analysis.Tests/
    ├── FSharpQueryStudio.Execution.Tests/
    ├── FSharpQueryStudio.Connectors.Tests/
    ├── FSharpQueryStudio.Protocol.Tests/
    ├── FSharpQueryStudio.Results.Tests/
    ├── FSharpQueryStudio.Storage.Tests/
    ├── FSharpQueryStudio.Security.Tests/
    ├── FSharpQueryStudio.Ui.Tests/          # Avalonia.Headless view/view-model tests
    └── FSharpQueryStudio.Integration.Tests/
```

Every `src/` project has a matching `tests/` project from the moment it is created — there is no project without one. See [Section 16](#16-engineering-standards-and-tdd-workflow) for the workflow that keeps them paired.

### Responsibilities

| Project | Responsibility |
|---|---|
| `App` | Startup, dependency injection, worker lifecycle, application-wide services. |
| `Ui` | Avalonia views, layout, commands, view models, state binding. |
| `Editor` | Monaco assets, browser bridge, editor-neutral models. |
| `Protocol` | Versioned JSON-serializable request/response/event contracts. |
| `Worker` | Standard-input/output host, request dispatch, health events. |
| `Analysis` | FCS parsing/type checking and mapping to protocol diagnostics. |
| `Execution` | Isolated evaluation, cancellation, session reset, captured output. |
| `Results` | Safe preview/table schema projection, paging, export operations. |
| `Connectors` | SQL/file/API connector interfaces and runtime implementations. |
| `Storage` | Workspace files, local history, layout, secret references. |
| `Security` | Script trust declarations and capability checks for future sharing. |

Keep FSharp.Compiler.Service types, database driver objects, and runtime values inside the worker. The host only receives stable protocol models.

---

## 5. Avalonia UX and Layout

Use a straightforward `Grid` with persisted splitter positions for the MVP. Avoid a complex docking framework until users need arbitrary pane docking.

```text
┌─────────────────────────────────────────────────────────────────┐
│ File · Edit · View · Connections · Run  | New Save Run Cancel    │
├────────────────┬────────────────────────────────────────────────┤
│ Workspace      │ Script tabs                                    │
│ - Scripts      ├────────────────────────────────────────────────┤
│ - Connections  │ Monaco F# editor                               │
│ - History      │                                                │
│ - Packages     ├────────────────────────────────────────────────┤
│                │ Results | Output | Problems | Schema           │
└────────────────┴────────────────────────────────────────────────┘
```

### Core view models

- `ShellViewModel`: workspace, active document, global commands, worker status.
- `DocumentViewModel`: text, URI, version, dirty state, cursor location, execution state.
- `ConnectionViewModel`: connection display name, connector type, health, non-secret settings.
- `ResultViewModel`: result kind, schema, preview rows, truncation state, elapsed time.
- `ProblemViewModel`: diagnostic severity/message/span and navigation command.
- `WorkerStateViewModel`: `Starting`, `Ready`, `Running`, `Cancelling`, `Stopped`, `Faulted`.

All updates from worker callbacks must be dispatched to Avalonia’s UI thread before mutating bound state.

---

## 6. Monaco Editor Integration

### Hosting strategy

Monaco must run within a browser/WebView. For Windows v1, select and validate an Avalonia WebView implementation backed by WebView2. Keep the editor layer isolated so later macOS/Linux WebView backends can replace the host without changing editor protocol or analysis code.

Bundle Monaco and a small `editor-host.html` locally. Serve those assets through a loopback HTTP server bound to `127.0.0.1` on an ephemeral port, or through a WebView-specific virtual-host mapping if it reliably supports Monaco web workers.

The local asset host must:

- Bind only to loopback.
- Use an ephemeral port.
- Serve only embedded/static application assets.
- Reject path traversal and unknown assets.
- Apply restrictive content-security-policy headers.
- Never expose workspace folders or connection files as arbitrary web content.

### Editor bridge

Use JSON envelopes, not ad-hoc string messages.

```fsharp
type EditorEnvelope =
    { Id: string
      Kind: string
      Payload: JsonElement }

type EditorRequest =
    | TextChanged of documentUri: string * version: int * source: string
    | CompletionRequested of documentUri: string * version: int * line: int * column: int
    | HoverRequested of documentUri: string * version: int * line: int * column: int
    | ExecuteRequested of documentUri: string * version: int * selection: string option
    | CancelRequested
```

Messages sent from Monaco include text changes, cursor position, completion/hover requests, keyboard execution actions, and initialization events. Messages from Avalonia include diagnostics, completion items, hover content, navigation, themes, and execution state.

### F# editor functionality

Monaco provides the editor surface, but not native F# semantic intelligence. Implement in layers:

1. Register an `fsharp` language with comments, brackets, indentation, tokens, snippets, and styling.
2. Register custom Monaco providers for completion, hover, and later signature help/definition navigation.
3. Obtain semantic data from the worker’s FCS analysis service.
4. Push diagnostics using Monaco markers.

Monaco must never execute user script code.

---

## 7. Host ↔ Worker Protocol

Start with newline-delimited JSON (NDJSON) over standard input/output.

- Worker `stdout`: protocol frames only.
- Captured FSI standard output/error: included in structured execution events.
- Worker `stderr`: reserved for pre-Serilog-init crash traces only (e.g. a startup failure before logging is configured); the host redirects it into its own log for diagnostics. Once running, worker logging goes to its own Serilog file sink — see [Section 15](#15-logging-and-observability) — not `stderr`.

This works on Windows now and maps cleanly to Linux/macOS later.

### Request shape

```json
{
  "id": "30c714ec",
  "method": "analysis/check",
  "params": {
    "sessionId": "a8eebdab",
    "documentUri": "file:///workspace/sales.fsx",
    "version": 42,
    "source": "let total = rows |> Seq.sumBy _.Amount"
  }
}
```

### Important methods

| Method | Purpose |
|---|---|
| `worker/health` | Version handshake and capability announcement. |
| `session/create` | Prepare an isolated script execution context. |
| `session/reset` | Dispose state and recreate a fresh execution context. |
| `analysis/check` | Parse/type-check a source snapshot. |
| `analysis/completions` | Retrieve completions at a source position. |
| `analysis/hover` | Retrieve symbol/type/documentation at a source position. |
| `execution/evaluate` | Run selection, cell, or complete script. |
| `execution/cancel` | Request cooperative cancellation. |
| `results/page` | Fetch a later result page when supported. |
| `connection/register` | Register named approved connection bindings. |
| `package/approve` | Approve/package a requested dependency according to policy. |

Every request should carry a correlation ID. Source-analysis requests must also carry the Monaco document version. The UI applies a response only if its version equals the current document version; this prevents stale diagnostics or completions from replacing newer editor state.

### Result paging shape

`results/page` params and response:

```fsharp
type ResultPageRequest =
    { SessionId: string
      ContinuationToken: string }

type ResultPage =
    { Rows: obj array array
      IsTruncated: bool
      ContinuationToken: string option }
```

`ContinuationToken` is opaque and worker-issued, never constructed by the client. It is tied to the still-open reader/enumerator for that session and expires when the session is disposed or reset. A `results/page` request against an unknown or expired token returns a structured error — it never silently re-runs the original query.

---

## 8. F# Analysis with FSharp.Compiler.Service

Use `FSharp.Compiler.Service` (FCS) for static analysis. Keep it logically separate from runtime FSI evaluation.

### Analysis pipeline

1. Monaco emits a source change with URI, text, and monotonically increasing version.
2. Avalonia debounces the request, normally 300–500 ms.
3. The worker constructs the standalone-script analysis context.
4. FCS parses and checks the snapshot.
5. The worker converts FCS-specific data into protocol-neutral diagnostics, completions, and hover values.
6. The host verifies the response version, then updates Monaco markers/providers.

### Standalone script context

For v1, scripts are analyzed as standalone `.fsx` files with a generated context containing:

- FSharp.Core and baseline framework references.
- Approved connector assemblies.
- Registered connection helper API references.
- Explicit, workspace-controlled package references.
- A deterministic working directory/reference search policy.

Do not claim static analysis perfectly reproduces a future persistent FSI session. Persistent execution state is intentionally out of scope for v1. The generated context must be deterministic and auditable.

### Editor-neutral diagnostics

```fsharp
type DiagnosticSeverity =
    | Error
    | Warning
    | Information

type Diagnostic =
    { Message: string
      Severity: DiagnosticSeverity
      StartLine: int
      StartColumn: int
      EndLine: int
      EndColumn: int
      Code: string option }
```

Write explicit tests for one-based vs zero-based FCS/Monaco coordinate conversion, multiline spans, empty lines, end-of-file spans, and Unicode source.

### Completion rollout

1. MVP: F# keywords, snippets, built-in connector helper names, and names from the most recent successful check.
2. Semantic completions: FCS completion data at cursor position.
3. Advanced: auto-open/import suggestions, signatures, go-to-definition, rename/refactoring support.

Hover should return the display name, inferred type, full signature where available, and XML documentation when FCS can provide it.

---

## 9. Execution Model: Isolated by Default

### Version 1 execution behavior

Each execution runs in a **fresh worker/session by default**. This provides predictable behavior and avoids hidden state:

- Run selection: execute selected source in a fresh worker context.
- Run cell: execute one cell/block in a fresh context, after required generated setup.
- Run document: execute the entire `.fsx` script in a fresh context.
- Run fresh: explicit command equivalent to the default behavior in v1.

The application should visually state that execution is isolated, so developers do not expect previous `let` bindings to survive.

### Preparing for persistent sessions

Design `sessionId` and session lifecycle into the protocol from day one, even though v1 uses ephemeral sessions. Later, add a user-selected execution mode:

| Future mode | Expected behavior |
|---|---|
| Isolated | Fresh worker/session every execution; safest default. |
| Persistent tab session | Bindings remain available within a document tab. |
| Persistent workspace session | Selected shared bindings can be available across scripts. |
| Notebook/cell session | Ordered `// %%` cells execute incrementally. |

Persistent mode must clearly expose `Reset Session` and display session state. It must also define how script edits invalidate prior state.

### Worker lifecycle

```text
Host starts worker
  → worker initializes FSI/FCS services
  → worker loads approved baseline references
  → host registers non-secret connection metadata / capability grants
  → worker executes one request
  → worker emits output, result, diagnostics, and termination status
  → host disposes worker after isolated run
```

Use `FsiEvaluationSession` inside the worker. Configure a controlled working directory, explicit reference paths, redirected output/error, and no interactive console prompts.

### Crash and restart ownership

The host alone owns process supervision. It detects worker exit through the process `Exited` event or an unexpectedly closed stdout stream; the worker never restarts itself. A crash mid-request surfaces as a structured execution-error result to that request's caller — the host does not automatically retry the failed request. The *next* request gets a freshly started worker.

### Cancellation

Arbitrary F# code may not cooperate with cancellation. Use a two-step policy:

1. Send a cooperative cancellation/interrupt request.
2. If the worker does not return inside a configured grace period, terminate it and report that the isolated session was forcibly stopped.

Never wait synchronously on the Avalonia UI thread.

---

## 10. Data Connectors from Day One

Implement a common connector abstraction, but do not force all data sources into a lowest-common-denominator query API. F# scripts should access targeted helpers and return standard sequences/tables.

```fsharp
type ConnectorKind =
    | SqlServer
    | PostgreSql
    | Sqlite
    | Csv
    | Excel
    | RestJson

type ConnectionDefinition =
    { Id: Guid
      Name: string
      Kind: ConnectorKind
      Configuration: JsonElement
      SecretReference: string option
      IsTrusted: bool }
```

### Initial connector set

| Target | Worker capability |
|---|---|
| SQL Server | Connection, parameterized commands, reader-based paging, schema discovery. |
| PostgreSQL | Connection, parameterized commands, reader-based paging, schema discovery. |
| SQLite | Local database connection, parameterized commands, file-aware configuration. |
| CSV | Delimiter/encoding/header options, streaming read, typed conversion helpers. |
| Excel | Workbook/sheet selection, header options, bounded worksheet reads. |
| REST/JSON | URL/method/header/query/body configuration, JSON document parsing, pagination helpers where configured. |

### Connector implementation guidance

- Use standard runtime data-access libraries and ADO.NET-style readers for database connectors first.
- Keep database credentials in OS-backed credential storage, not source scripts.
- Require parameters instead of interpolating user data into SQL command text.
- Let scripts call controlled APIs, for example:

```fsharp
let rows = Connections.sql "Sales" "select * from Orders where CreatedAt >= @from" [ "from", box startDate ]
```

- For CSV/Excel/JSON, return stream-friendly enumerables or row sequences that the result projector can bound.
- For REST requests, do not silently execute arbitrary network calls from downloaded/shared scripts once shared-script support is introduced; route them through capability checks.

### Type providers

F# type providers are valuable but should be treated as an advanced optional path rather than the primary connector mechanism for v1. They introduce package resolution, design-time assembly loading, cache, target-framework, and reproducibility complexity.

Design the package/reference policy so type providers can be enabled later for trusted workspaces.

---

## 11. Result Projection and Rendering

Never serialize arbitrary F# values directly. Runtime values can be huge, recursive, lazy, disposable, or impossible to serialize.

Create a bounded result-projection layer in the worker.

```fsharp
type ResultKind =
    | Scalar
    | Text
    | Table
    | Object
    | Error
    | NoValue

type Column =
    { Name: string
      DisplayType: string }

type ResultEnvelope =
    { Kind: ResultKind
      DisplayText: string option
      Schema: Column list
      Rows: obj array array
      IsTruncated: bool
      ContinuationToken: string option
      ElapsedMs: int64
      Output: OutputLine list }
```

### Projection rules

| Returned value | Rendered result |
|---|---|
| `unit` | No result; preserve console output. |
| Primitive/string | Scalar/text result. |
| Record/object | Property view or one-row table. |
| Array/list/`seq<'T>` | Bounded table preview. |
| Data reader | Streamed table schema and rows. |
| Nested object | Formatted text initially; object inspector later. |
| Exception | Structured failure with message and details. |

### Bounded sequence rules

- Materialize at most a configured preview-row limit, such as 500 rows.
- Enforce an elapsed-time limit for preview generation.
- Never enumerate on the UI thread.
- Inform the user when results are truncated.
- Avoid double enumeration of sequences.
- Support paging only where the connector/result representation supports it.

Use Avalonia DataGrid virtualization and display-ready cell values. Export CSV as a worker-side streamed operation instead of exporting the current UI viewport.

---

## 12. Package, Reference, and NuGet Policy

`#r "nuget: ..."` is helpful but must be controlled for repeatability and future script sharing.

### MVP policy

- Record approved package names, versions, source, and resolved assets in workspace metadata.
- Restore into a workspace-specific cache or managed cache location.
- Ask for explicit approval for a package not listed in the workspace manifest.
- Require a session reset/recreation after references change.
- Log dependency resolution without logging secrets.

### Avoid

- Silent package restoration from unapproved sources.
- Untracked global-cache behavior.
- Arbitrary package-feed URLs without user confirmation.
- Assuming every package or type provider is compatible with the selected worker runtime.

---

## 13. Workspace, Secrets, and Sharing Preparation

### Workspace layout

```text
my-workspace/
├── scripts/
│   ├── revenue-analysis.fsx
│   └── customer-segmentation.fsx
├── .fqs/
│   ├── workspace.json
│   ├── packages.json
│   ├── layout.json
│   ├── history.json
│   └── trust.json
└── exports/
```

### Storage responsibilities

- `workspace.json`: connector names and non-secret settings.
- `packages.json`: approved package/version manifest.
- `layout.json`: local pane and splitter preferences.
- `history.json`: optional local execution history.
- `trust.json`: future script provenance/trust/capability metadata.
- OS credential store: passwords, tokens, certificates, and sensitive headers.

### Shared/downloaded script plan

Although v1 targets local developers, introduce these concepts early:

- **Origin metadata**: local, imported, downloaded, shared workspace.
- **Trust state**: unknown, reviewed, trusted, blocked.
- **Requested capabilities**: filesystem read/write, network access, package restore, specific named connections, process launch.
- **User approval UI**: show requested capabilities before running scripts that are not locally authored/trusted.

### `trust.json` v1 schema

```json
{
  "origin": "local | imported | downloaded | shared-workspace",
  "trustState": "unknown | reviewed | trusted | blocked",
  "requestedCapabilities": ["filesystem-read", "filesystem-write", "network", "package-restore", "connection:<name>", "process-launch"],
  "reviewedAt": "2026-08-05T00:00:00Z | null",
  "reviewedBy": "string | null"
}
```

For v1, this file is read, written, and displayed — it is not enforced. Later releases can enforce policies through worker-level capabilities and operating-system sandboxing where available.

---

## 14. Windows-First, Cross-Platform-Ready Design

The first release targets Windows, but avoid Windows-only coupling outside the platform integration layer.

### Windows v1

- Target supported Windows desktop runtimes.
- Use a Windows-compatible Avalonia WebView backend, likely WebView2-based.
- Use the Windows credential manager or a vetted cross-platform secret abstraction backed by Windows facilities.
- Package with Windows-compatible self-contained publishing/installer strategy.

### Cross-platform preparation

- Use `System.Diagnostics.Process`, `System.IO`, and .NET abstractions rather than Windows command shell APIs.
- Keep worker IPC on stdio/NDJSON rather than named-pipe-only protocols.
- Store paths as `Uri`/relative workspace paths where possible and normalize through .NET APIs.
- Isolate WebView and secret-store implementations behind interfaces.
- Run analysis/execution/protocol tests on Windows plus future Linux/macOS CI agents as soon as feasible, even before the UI is officially shipped there.
- Avoid assuming the presence of Excel desktop automation; use a workbook-reading library appropriate for cross-platform file reading.

---

## 15. Logging and Observability

The application should always be able to answer "what was it doing right before this went wrong" without a debugger attached. Use [Serilog](https://serilog.net/) as the logging library across `App` (host) and `Worker`. Other libraries depend only on `Microsoft.Extensions.Logging`'s abstraction, never on Serilog types directly — Serilog is wired to that abstraction once, at each process's composition root, so libraries stay testable without a real sink.

### Sinks

- **File**, one file per calendar day: `Serilog.Sinks.File` with `rollingInterval: RollingInterval.Day` and a `retainedFileCountLimit` (14 days by default). Host and worker write to separate files — `logs/host-.log` and `logs/worker-.log` under a per-user application-data directory (e.g. `%LOCALAPPDATA%/FSharpQueryStudio/logs/`) — so a worker crash's log doesn't get interleaved with host output, while a shared correlation ID (below) lets the two be joined back together when reading them.
- **Console**, active during development at `Debug` level; the file sink defaults to `Information` in production, overridable via minimum-level configuration.

This logs directory is distinct from the per-workspace `.fqs/history.json` in [Section 13](#13-workspace-secrets-and-sharing-preparation): `history.json` is a user-facing, per-workspace execution history; the Serilog files are process-wide, developer-facing diagnostic logs.

### Structured fields

Every log event carries, via enrichers and `LogContext.PushProperty` around request handling:

- `CorrelationId` — the protocol request ID from [Section 7](#7-host--worker-protocol), pushed once when a request starts so every log line emitted while handling it is automatically tagged.
- `SessionId` — the worker session, once one exists.
- `ProcessRole` — `host` or `worker`.
- Process ID and thread ID, via `Serilog.Enrichers.Process`/`Thread`.

### What gets logged

| Where | Events | Level |
|---|---|---|
| Host | Worker process start/stop/crash/restart | Information (crash: Warning/Error) |
| Host | Protocol request/response — method + correlation ID | Debug |
| Host | Full request/response payload | Verbose, opt-in only — large source text and result rows are not dumped into logs by default |
| Host | UI-triggered actions: open/save/run/cancel | Information |
| Host | Connection test attempts — success/failure, never the connection string itself | Information |
| Host | Package approval decisions | Information |
| Worker | Session create/reset | Information |
| Worker | FSI evaluation start, elapsed time, outcome | Information |
| Worker | FCS check timings | Debug |
| Worker | Connector calls — kind, elapsed time, row count; parameter *values* are never logged | Information |
| Worker | Cancellation/termination events | Information |
| Both | Uncaught exceptions, with full detail | Error |

The log is allowed to be more detailed than what round-trips to the UI: an exception's full stack trace belongs in the log file, while the protocol-level `ResultEnvelope` returned to the caller stays the existing user-safe structured shape from [Section 11](#11-result-projection-and-rendering).

### Redaction

A Serilog enricher/destructuring policy scrubs known secret-shaped fields — connection strings, tokens, headers named `Authorization` — before any event reaches a sink. This is the same trust boundary as the secret-reference design in [Section 13](#13-workspace-secrets-and-sharing-preparation), generalized to every log call rather than a one-off check: nothing that would be a secret in a connection definition is ever a plain value in a log line.

### Diagnostic bundles

Milestone D adds a diagnostic-bundle export that zips the last N days of host and worker log files for sharing during troubleshooting. Because redaction happens at write time, bundling needs no extra scrubbing step — the files on disk are already safe to hand over.

### Testing logging behavior

Capture emitted events in memory (Serilog's in-memory/test-sink pattern) and assert specific events fire with the expected properties and that redaction actually strips secret values — without touching the real file system in unit tests. The daily-rolling file behavior itself gets one integration test, using a temp directory and an injected/fake clock to cross a day boundary and confirm a new file starts.

---

## 16. Engineering Standards and TDD Workflow

Every project in the solution, every milestone, no exceptions: **tests are not a separate phase**. They are written before the code that satisfies them, and the suite runs green before a task is considered finished.

### TDD cycle per task

1. **Red** — write a failing test that expresses the behavior the task requires. Run it, confirm it fails for the expected reason.
2. **Green** — write the minimum implementation that makes it pass. Resist adding behavior the current test doesn't demand.
3. **Refactor** — clean up implementation and test code with the safety net of a passing suite, then re-run to confirm still green.

Throwaway spike code to explore an unfamiliar API (e.g. probing `FsiEvaluationSession` behavior, or an Avalonia WebView control) is fine, but it must be deleted and rebuilt through the red/green/refactor cycle before it becomes part of a task's actual diff. Spiking is for learning, not for shipping.

### Test pyramid by project

| Project | Primary coverage | Notes |
|---|---|---|
| `Protocol` | Unit | Serialization round-trips, schema-version compatibility. |
| `Analysis` | Unit | FCS-to-protocol mapping, coordinate conversion, stale-version handling — see [Section 8](#8-f-analysis-with-fsharpcompilerservice). |
| `Execution` | Unit + Integration | Unit tests mock the FSI boundary; integration tests run real `FsiEvaluationSession` scripts. |
| `Results` | Unit | Projection rules per [Section 11](#11-result-projection-and-rendering) table, truncation, paging token issuance. |
| `Connectors` | Unit + Integration | Unit tests cover query-building/parameter binding; integration tests hit a local SQLite file, a local Postgres/SQL Server dev instance (or test container), temp CSV/Excel fixtures, and a local HTTP stub for REST. No integration test ever depends on a remote/shared service. |
| `Storage` | Unit | Workspace/manifest/trust-file read-write round-trips, using a temp directory per test. |
| `Security` | Unit | Capability/trust decision logic. |
| `Worker` | Integration | Starts the real worker executable and drives it over NDJSON — see [Section 7](#7-host--worker-protocol). |
| `Ui` | Avalonia.Headless | Views and view models exercised without a real window; input simulated through Avalonia's headless input APIs; assertions against bound state and, where relevant, the rendered visual tree. |
| `App` | Covered indirectly | Thin composition root; exercised through `Worker` and `Ui` integration tests rather than a dedicated heavy suite. |

### Definition of done

A task is done only when all of the following are true:

- A test existed and was observed failing before the implementation was written.
- The implementation makes that test — and the full existing suite — pass.
- `dotnet test` run locally is green across the whole solution, with no skipped or ignored tests left without a tracked reason.
- The diff has been re-read once, end to end, before moving to the next task.

### CI is deliberately deferred

No CI pipeline exists until Milestone D (see [Section 18](#18-delivery-roadmap)). Until then, a local `dotnet test` run is the only gate, run before every task is marked complete. Do not build pipeline infrastructure earlier than that milestone — it is scoped there intentionally, once the shape of the test suite has stabilized.

---

## 17. Test Strategy

This section indexes what the pyramid in [Section 16](#16-engineering-standards-and-tdd-workflow) covers concretely, project by project. It is a checklist for coverage completeness, not a separate testing phase.

### Unit tests

- Diagnostic coordinate conversion between FCS and Monaco.
- Completion and hover mapping.
- Stale document-version response rejection.
- Result projection of scalar, record, sequence, data reader, and exception values, including paging-token issuance and expiry.
- Protocol serialization/backward compatibility.
- Connection-binding generation and parameter handling.
- Package approval and trust/capability decisions.
- Workspace, packages, layout, history, and trust manifest read/write round-trips.

### Integration tests

Start the actual worker executable and verify:

1. A simple F# script executes and returns a scalar.
2. Syntax/type errors return structured diagnostics.
3. An isolated run does not retain bindings after it completes.
4. Cancellation returns cooperatively or forces worker termination.
5. A worker crash is detected by the host and the in-flight request surfaces a structured execution-error result; the next request gets a freshly started worker.
6. SQL Server, PostgreSQL, SQLite, CSV, Excel, and REST fixtures return expected projected results.
7. Large sequences are truncated without UI blocking.
8. References/packages require manifest approval.

### UI tests (Avalonia.Headless)

- Create/open/save a script.
- Modify source and receive Monaco markers.
- Trigger Run with F5.
- Inspect table/output/problems views.
- Cancel execution.
- Configure a connection without exposing secret material in the workspace.

---

## 18. Delivery Roadmap

### Milestone A — Windows Vertical Slice

- Avalonia shell and fixed multi-pane layout.
- One Monaco editor tab through Windows WebView hosting.
- Local assets host and typed editor bridge.
- Worker executable with NDJSON protocol.
- Isolated whole-script execution.
- Output console, scalar view, and basic table preview.
- Worker cancellation/restart.

**Acceptance criterion:** A user can open a standalone `.fsx` script, execute it in isolation, read output/results, cancel it, and recover from a worker crash without restarting the UI.

### Milestone B — F# Editor Intelligence

- Debounced FCS checking.
- Monaco markers and Problems pane.
- Hover type display.
- Basic semantic completions.
- Source navigation from Problems pane.
- Document-version race handling.

**Acceptance criterion:** Invalid F# produces useful squiggles, local symbols/type information are discoverable, and stale analysis never overwrites current editor state.

### Milestone C — Query Workflow and All First-Day Connectors

- Workspace/script explorer and multiple tabs.
- SQL Server, PostgreSQL, SQLite, CSV, Excel, and REST/JSON connectors.
- Connection testing and secret references.
- Results tabs, virtualized grid, CSV export.
- Package manifest and reference approval workflow.

**Acceptance criterion:** Each supported data target can be configured, queried through F# helpers, and displayed/exported through bounded results.

### Milestone D — Hardening and Sharing Foundations

- Trusted/untrusted script origin metadata.
- Capability approval UX.
- Structured logs and diagnostic bundles.
- Dependency reproducibility improvements.
- Automated Windows packaging tests.
- Linux/macOS compatibility test matrix for non-UI components.

### Milestone E — Persistent and Project-Based Futures

- Opt-in persistent per-tab FSI sessions.
- Notebook cell execution and Reset Session semantics.
- Project/file graph support for `.fsproj` and multi-file workspaces.
- Go-to-definition, signature help, and richer refactoring.
- Official Linux/macOS UI support after WebView/packaging validation.

---

## 19. Key Technical Risks and Mitigations

| Risk | Mitigation |
|---|---|
| Monaco WebView behavior differs across future platforms | Encapsulate WebView/editor host behind interfaces; validate Windows first. |
| FSI code hangs or crashes | Worker-process isolation, cooperative cancel, forced termination fallback. |
| FCS analysis differs from runtime behavior | Deterministic generated standalone-script context; transparent isolated-execution model. |
| Large/lazy results overwhelm the app | Worker-side bounded projection, virtualized UI, explicit truncation. |
| Data-source and credential complexity | Connector abstraction, OS secret references, parameterized database access. |
| Type-provider/package complexity | Treat as optional advanced capability; package manifest and explicit reference policy. |
| Downloaded scripts become risky | Trust/origin/capability metadata now; enforcement and sandboxing later. |
| Windows-only code blocks portability | Keep IPC, worker, storage, and connectors platform-neutral from the beginning. |

---

## 20. Final Recommended Baseline

Build the first release with the following non-negotiable baseline:

- Windows-only **user interface**, but platform-neutral core libraries and stdio worker protocol.
- Standalone `.fsx` workspaces.
- Fresh isolated execution for every run.
- FSharp.Compiler.Service-powered diagnostics, completions, and type hover.
- Monaco inside a local WebView host with a typed JSON bridge.
- SQL Server, PostgreSQL, SQLite, CSV, Excel, and REST/JSON support from day one.
- Worker-side bounded result projection and UI-side virtualized display.
- External secret storage and workspace manifests that contain no credentials.
- Package approval and early trust/capability metadata to prepare for shared scripts.
- TDD as the working discipline for every project, with unit, integration, and Avalonia.Headless UI coverage from the first task — see [Section 16](#16-engineering-standards-and-tdd-workflow) — and CI intentionally deferred to Milestone D rather than built early.
- Serilog-based structured logging from Milestone A, daily rolling log files, correlation IDs threading host and worker events together — see [Section 15](#15-logging-and-observability).

This delivers a useful F# query tool early while preserving the architectural routes needed for persistent sessions, shared/downloaded script safety, multi-file F# projects, and Linux/macOS support.
