# Development Log

Chronological record of what was done and why, alongside [`fs-studio-plan.md`](./fs-studio-plan.md) (architecture) and [`fs-studio-development-plan.md`](./fs-studio-development-plan.md) (task breakdown). Each entry is a real step taken, not a restatement of the plan — see the linked commit for the diff.

---

## 2026-08-05 — Docs: architecture spec and development plan (`8029847`)

Wrote the initial architecture spec and a granular, TDD-paired development plan covering all five milestones (A–E). Key decisions locked in during brainstorming: strict TDD, Avalonia.Headless for UI tests, CI deferred to Milestone D, solo execution, no deadline.

## 2026-08-05 — Docs: logging strategy (folded into `8029847`'s follow-up edits)

Added Section 15 "Logging and Observability" to the architecture spec: Serilog across `App`/`Worker`, daily-rolling file sinks (host/worker separate files, 14-day retention), correlation IDs via `LogContext`, a full table of what gets logged where, and redaction of secret-shaped values. Updated the development plan to instrument Milestone A and C code paths with real log events instead of treating logging as a Milestone D afterthought.

## 2026-08-05 — A0: solution scaffold + Serilog foundation (`79e5c63`)

Created the F# solution with all `src/` projects and their matching `tests/` projects (xUnit), `Directory.Build.props` for shared TFM/warnings-as-errors, and `.gitignore` hardening for secrets.

While wiring Serilog, found the architecture doc never assigned the correlation/redaction enrichers a home project — every other piece of shared logic lives in a named project, this didn't. Added a `Logging` project (+ `Logging.Tests`) rather than duplicating the enricher code in both `App` and `Worker`, and updated Section 4 of the architecture spec to match. TDD'd `Correlation.pushCorrelationId` and `Redaction.RedactingEnricher` (5 tests), then refactored `App`/`Worker`'s `Program.fs` down to a single `LoggerSetup.build role prefix` call each. Also caught that neither composition root had `Enrich.FromLogContext()` wired in the very first draft — without it, correlation IDs pushed via `LogContext.PushProperty` would never have reached the real sinks, only the tests (which added it themselves) would have passed.

Verified manually: ran both `App` and `Worker` once, confirmed dated log files with startup lines under `%LOCALAPPDATA%/FSharpQueryStudio/logs/`.

## 2026-08-05 — A.1: protocol envelope and worker frame types (`7c371a4`)

Added `EditorEnvelope`, `WorkerRequestFrame`, `WorkerResponseFrame` to `Protocol`, using `System.Text.Json` + `FSharp.SystemTextJson` (needed for F# records/options to serialize correctly — plain `System.Text.Json` doesn't understand F# option types out of the box).

TDD caught a real bug: the architecture doc's own wire examples in Section 7 use camelCase field names (`"id"`, `"method"`, `"params"`), but the default `JsonSerializerOptions` emit PascalCase to match the F# record field names. The round-trip tests passed regardless (serialize and deserialize used the same convention), but a test deserializing a hand-written camelCase JSON string (simulating what a real worker would receive) failed with "missing field Id". Fixed with `PropertyNamingPolicy = JsonNamingPolicy.CamelCase` and `PropertyNameCaseInsensitive = true` on the shared `Serialization.options`. Worth remembering: round-trip tests alone don't catch wire-format mismatches — at least one test per protocol type needs to assert against a literal JSON string, not just serialize-then-deserialize the same object.

## 2026-08-05 — A.2: NDJSON stream reader/writer

Added `NdjsonStream.readFrame`/`writeFrame` in `Worker`, generic over any protocol frame type. Four tests: a fragmented-stream test using a custom `Stream` that yields exactly one byte per `Read`/`ReadAsync` call (proves `TextReader.ReadLineAsync` reassembles a full line regardless of how the transport chooses to deliver bytes — relevant since a real pipe won't hand over a whole line at once), an embedded-newline test (a string value containing `\n` still round-trips as exactly one frame, because the serializer escapes it rather than emitting a raw line break), a Unicode round-trip, and an end-of-stream case.

Note for future reference: an early build of the test file threw a cascade of unrelated-looking errors (ambiguous record field resolution between `WorkerRequestFrame`/`WorkerResponseFrame`, wrong-type errors) purely because `NdjsonStream` didn't exist yet — the missing module broke type inference for everything downstream in the file. Implementing the real module made all of it resolve cleanly; worth not chasing phantom errors like that before checking whether the primary "not defined" error is the actual root cause.

## 2026-08-05 — Fix: worker's console sink was writing to stdout

Caught while starting A.3's stdin/stdout main loop, before it was wired up: `LoggerSetup.build` always sent its console sink to stdout, but Section 7 reserves the worker's stdout exclusively for NDJSON protocol frames. A worker-role logger would have interleaved Serilog console lines into the same stream the host parses as JSON frames — a bug that wouldn't have shown up until real logging volume made a parse failure likely, and would have been confusing to diagnose from the symptom alone.

Fixed by making `LoggerSetup.build` route console output through stderr whenever `processRole = "worker"` (via Serilog's `standardErrorFromLevel`), and added a `logsDirectory` parameter (previously hardcoded to the real `%LOCALAPPDATA%` path) so tests can point the file sink at a temp directory instead of writing into the user's real logs folder on every test run. Two new tests assert the stdout/stderr split and that the file lands where told.

---

## In progress

A.3 — Worker process bootstrap and `worker/health` handshake.
