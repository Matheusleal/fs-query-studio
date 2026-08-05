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

## 2026-08-05 — A.3: worker bootstrap and worker/health handshake

Added `HealthInfo` to `Protocol`, `Handlers.dispatch`/`handleHealth` to `Worker` (unit-tested directly, 3 tests: happy path, non-empty version, unknown-method error), and wired `Program.fs`'s real stdin/stdout loop — read a frame, push its ID as the correlation ID for the duration of handling it, dispatch, write the response, repeat until EOF.

The integration test spawns the actual compiled `FSharpQueryStudio.Worker.dll` as a child process (`dotnet <dll>`, located via `AppContext.BaseDirectory` — confirmed the referenced project's build output, dll *and* apphost `.exe`, gets copied into the referencing test project's own output directory automatically, so no fragile relative-path guessing across configurations), writes a `worker/health` request to its stdin, and reads the response from its stdout. Wrote the test before the loop existed — it failed with "expected a health response, got end of stream" (the old `Program.fs` returned immediately without reading anything), confirming RED for the right reason before wiring the loop made it pass. Added a `Task.WhenAny`-based timeout wrapper around the read so a real hang would fail loudly instead of blocking the test run indefinitely.

## 2026-08-05 — A.4: isolated FsiEvaluationSession construction

Added `FsiSession.create` in `Execution`, wrapping `FSharp.Compiler.Interactive.Shell.FsiEvaluationSession` with a controlled working directory, redirected output/error, and a pre-exhausted stdin reader so no interactive prompt can ever block the process.

Before writing any real code, spiked the FCS API in a throwaway console app under the scratchpad (deleted afterward, per the TDD skill's rule on spikes) — first tried via `dotnet fsi` directly, but that failed with an assembly-identity mismatch (running FCS *inside* another FCS-hosted process rather than a normal net10.0 host doesn't reflect the real target environment). Rebuilding the spike as a plain console app matching how `Execution` actually references the package worked immediately and confirmed the exact shape needed: `EvalInteractionNonThrowing(code) : Choice<FsiValue option, exn> * FSharpDiagnostic[]`.

Two environment issues along the way, both fixed narrowly rather than by loosening standards:
- `FSharp.Compiler.Service` 43.12.204 pins `FSharp.Core = 10.1.204` exactly; the .NET 10 SDK resolves `10.1.302`. `TreatWarningsAsErrors` turned that advisory (NU1608) into a build failure. Suppressed just `NU1608`, just in `Execution` and `Execution.Tests` (the only projects that pull FCS in), with a comment explaining why — not a blanket `NoWarn`.
- On Windows, `Directory.SetCurrentDirectory` locks that directory against deletion for as long as it's the process's current directory. The first test run failed at cleanup with "process cannot access the file" — not a bug in `FsiSession`, but a real OS constraint worth remembering for later workspace-management work (e.g. can't rename/delete a workspace folder while a worker has it open as its CWD). Tests now point the current directory elsewhere before deleting their temp directories.

## 2026-08-05 — A.5: execution/evaluate for a whole script

Added `Evaluation.evaluate` in `Execution`: runs a script through the session from A.4 and returns a raw `EvaluationOutcome` (FSI value, captured output/error text, diagnostics, exception) — deliberately not yet the protocol-neutral `ResultEnvelope` from Section 11, which is a later task that will consume this.

The first test run (`printfn "hi"; 1 + 1`, asserting `"hi"` shows up in captured output) failed for a real reason: `FsiEvaluationSession`'s own `outWriter`/`errorWriter` only capture FSI's REPL echo (`"> val it: int = 2"`), not the script's own `printfn`/`Console.Write` calls — those go through `System.Console.Out`, a separate global that isn't automatically redirected just because you passed a custom writer into `FsiEvaluationSession.Create`. Fixed by having `FsiSession.create` also call `Console.SetOut`/`Console.SetError` with the same writers, so both land in one captured buffer.

Second, unrelated failure surfaced at the same time: the CWD test from A.4 started failing intermittently once a second test module (`EvaluationTests`) existed, because `FsiSession.create`'s `Directory.SetCurrentDirectory` call is process-global and xUnit runs different test classes/modules in parallel by default — two tests each setting a different "current directory" race each other. Fixed with `[<assembly: CollectionBehavior(DisableTestParallelization = true)>]` in a new `AssemblyInfo.fs`, scoped to `Execution.Tests` only (the one test project whose subject mutates process-global state; parallelism elsewhere is unaffected). Re-ran the suite three times after the fix to confirm the flake was actually gone, not just not reproduced once.

## 2026-08-05 — A.6: result projection for Scalar/Text/Table

Filled a gap noticed while re-reading Section 11 for this task: `ResultEnvelope`'s `Output: OutputLine list` field referenced a type that was never actually defined anywhere in the doc. Added `OutputStream`/`OutputLine`, and made explicit that these projection types are protocol-neutral wire shapes belonging in `Protocol` (matching `Diagnostic`'s placement from Section 8) — `Results` produces them, it doesn't own them. Also turned "such as 500 rows" into a concrete named constant, `PreviewRowLimit = 500`.

Added `Projection.project` in `Results`, covering the three kinds in scope: primitives → `Scalar`, strings → `Text`, records → a one-row `Table` (reflection-derived columns), sequences → a bounded `Table` preview. `Object`/`Error`/`NoValue` stay out of scope here, landing with whatever later task actually needs them.

One test failure, `a record projects to a one-row Table...`, initially looked like it might be an ordering bug in the `match`, but turned out to be a real gap in the projection code once isolated: `Microsoft.FSharp.Reflection.FSharpType.IsRecord`/`GetRecordFields` default to public-only reflection, so a `type private Foo = { ... }` — which a `.fsx` script can legitimately write — would silently fall through to the generic `Text` fallback instead of being recognized as a record. Confirmed the diagnosis by temporarily removing `private` from the test's record (test went green), then reverted that and fixed the real cause instead: pass `BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance` explicitly everywhere a record is inspected. Worth the extra step — the tempting fix (adjust the test) would have shipped a real bug.

---

## In progress

A.4 — `session/create`: isolated `FsiEvaluationSession` construction.
