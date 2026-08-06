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

## 2026-08-05 — A.7: error projection and wiring execution/evaluate for real

Added `Projection.projectError` (straightforward — `ResultKind.Error` with the exception's message) and, since this task's own test description ("the worker process is still alive afterward") only means something once there's a real request/response round trip through a real worker process, wired `execution/evaluate` as an actual `Handlers.dispatch` case: deserialize `EvaluateParams`, create a fresh `FsiSession` in a fresh temp directory (isolated-by-default per Section 9 — no session persistence across requests yet), evaluate, project to `Error` or the A.6 value-based projection depending on whether the outcome carries an exception.

Hit a genuinely interesting FSI/F# semantics gotcha while writing the "throwing script" test. First attempt used the script `failwith "boom"` and got back the message *"Operation could not be completed due to earlier error(s)"* instead of "boom" — looked like a bug in `Evaluation`. Isolated it with a direct unit test (bypassing JSON/process transport entirely) and got the identical wrong message, which ruled out the integration test's transport as the cause and pointed at FSI itself. The real mechanism: `failwith` has type `string -> 'a` — a *bare* top-level `failwith "boom"` never gets a concrete type, so F#'s value restriction rejects the interaction as a **compile** diagnostic before it ever runs; no runtime exception is ever raised, so of course "boom" never surfaces. Confirmed by annotating a concrete type, `(failwith "boom" : int)`, which executes and throws normally. Kept both as tests: one exercising the realistic case (concretely-typed throw, real message), one as a characterization test documenting the value-restriction trap for anyone who hits it again — flagged as relevant to Milestone B's diagnostics work, since a user could genuinely write a script that's just `failwith "some message"` and would need to see *why* nothing ran, not a generic crash message.

## 2026-08-05 — A.8: host-side worker crash detection and recovery

Before writing this, re-read Section 9's worker-lifecycle diagram to make sure A.3's long-running request loop (one process handling many requests, built for `worker/health`) wasn't actually contradicting the documented model — it says "worker executes one request... host disposes worker after isolated run", which could be read as one-process-per-request. Concluded this describes the *host's isolation policy* for execution specifically (Section 9 is titled "Isolated by Default"), not a constraint on the worker's own request loop — Milestone E's persistent-session mode later reuses that same loop, so the capability needs to exist regardless. Left A.3 as built; scoped A.8 narrowly to what its own test actually asks for: crash detection and recovery, not "always restart after every execute" (that policy question belongs wherever Run (F5) gets wired end to end).

Added `WorkerClient` in `App` (the project Section 4 assigns "worker lifecycle" to): lazily starts a worker process, reuses it across `Send` calls, and — since a healthy worker's request loop never voluntarily closes its own stdout — treats `NdjsonStream.readFrame` returning `None` after a request was sent as unambiguous proof the process died. Returns a structured `worker/crashed` error for that request instead of hanging, and drops the dead process reference so the *next* `Send` starts a fresh one rather than retrying.

Tested against a real spawned process: send a request, kill the process while a second request is in flight, assert the crashed error comes back, then assert a third request gets a new process (different PID). No artificial synchronization needed for "mid-request" timing — the synchronous prefix of `Send` (reusing or starting the process) runs before the task's first real suspension point, so reading `WorkerClient.CurrentProcess` right after calling `Send` reliably reflects the process actually handling that call. Ran the suite three times to rule out a kill-timing race.

## 2026-08-05 — A.9: execution/cancel, cooperative then forced

The biggest single task so far — cancellation genuinely needs concurrency: the worker has to keep reading stdin (for a possible `execution/cancel`) while an `execution/evaluate` is still running, and the host has to correlate responses by `Id` instead of assuming strict request/response ordering, since the cancel's ack can now arrive before the evaluate's real result.

**The cancellation signal.** First attempt: bind a `CancellationToken` into the FSI session via `AddBoundValue` and have the script poll `.IsCancellationRequested`. Failed immediately with a type-check error — `System.Threading.CancellationToken (System.Runtime, ...)` vs `System.Threading.CancellationToken (System.Private.CoreLib, ...)`. Tried a plain `bool ref` next, hit the identical class of error for `System.Boolean`. Root cause, confirmed via a throwaway spike (deleted after): `FsiEvaluationSession` type-checks scripts against *reference assemblies* (compilation facades), while a host-constructed object comes from the *runtime assemblies* actually loaded in the process — different identities for the same nominal type, so `AddBoundValue` can't unify them for anything, not even primitives. The fix that worked: don't inject a value at all — add `--reference:<path-to-Execution.dll>` (the assembly's own already-loaded location) to the FSI session's argv, so a script can call `FSharpQueryStudio.Execution.CancellationSignal.isCancelled()` as an ordinary static member, resolved entirely within FSI's own compilation. No value ever crosses the boundary. `CancellationSignal` is a single mutable flag — sufficient because a worker runs at most one evaluation at a time (see below).

**Concurrency, worker side.** `Program.fs`'s loop now backgrounds `execution/evaluate` specifically via `Task.Run`, immediately looping back to read the next frame — everything else stays synchronous (fast, no need to background). Added a `SemaphoreSlim`-guarded `writeResponse` since the backgrounded evaluate's eventual response and the loop's own synchronous responses now both write to the same stdout. `Handlers.handleEvaluate` gained an `Interlocked.CompareExchange`-guarded busy check: since the worker now *can* receive a second `execution/evaluate` while the first is still running (nothing stops a misbehaving client from sending one), and `FsiSession.create`/`CancellationSignal` both mutate process-global state a second concurrent evaluation would corrupt, a second request while one is in flight gets a structured `execution/busy` error instead of silently racing.

**Concurrency, host side.** `WorkerClient` needed a real rework: a background reader loop now matches every incoming response to its request by `Id` via a `ConcurrentDictionary<string, TaskCompletionSource<...>>`, replacing the old "write then read the next line" assumption from A.7/A.8 (which broke the moment a cancel-ack could legitimately arrive before the evaluate's real result). Added `Cancel(requestId, gracePeriod)`: send `execution/cancel`, await its ack, then race the *original* request's completion against the grace period — if the grace period wins, kill the process and resolve the original caller with a distinct `execution/forciblyStopped` error rather than the generic `worker/crashed` from A.8.

**Tests.** Two full paths, both against a real spawned process: a script polling `CancellationSignal.isCancelled()` in a tight loop stops within the loop iteration (`Scalar` result, value far short of the loop bound, worker still alive afterward); a script that never checks it (`while true do ()`) gets killed after a short grace period and its caller receives `execution/forciblyStopped`. Also proactively added `DisableTestParallelization` to `Worker.Tests` before hitting a flake this time, having already learned that lesson the hard way in `Execution.Tests` (A.5) — `Handlers.handleEvaluate` and `CancellationSignal` mutate the same kind of process-global state.

## 2026-08-05 — A.10: Avalonia shell (first UI work this project)

First task touching the UI stack. `WorkerStateViewModel` is a plain state machine (`Starting/Ready/Running/Cancelling/Stopped/Faulted`) with an explicit allowed-transitions table — `Stopped` is terminal, `Faulted` only recovers via a fresh `Starting` (matching `WorkerClient`'s crash-then-restart policy from A.8/A.9), every non-terminal state can shut down. `TryTransition` returns `false` and leaves `Current` untouched for anything not in the table, firing `PropertyChanged` only on an actual change. `ShellViewModel` wraps it for now; workspace/active-document/commands join later when the tasks that need them land. Both fully unit-tested, no Avalonia runtime involved — they're plain classes.

Tried wiring up `Avalonia.Headless.XUnit` for this first, since the dev plan calls for Avalonia.Headless testing. Its current version pulls in xUnit v3, which conflicts with the xUnit v2 every other test project in the solution uses — and this particular test doesn't touch any real Avalonia control, so there was nothing to gain from it. Removed the package; a plain `[<Fact>]` covers a pure state machine exactly as well. Deferred the real `Avalonia.Headless` setup (and whatever xUnit-version decision it forces) to whichever task first needs to exercise an actual View/Control.

Built the fixed multi-pane Grid layout from Section 5 as `MainWindow.axaml`/`.axaml.fs` (menu row, workspace pane, script tabs + editor placeholder, results/output/problems/schema tabs, `GridSplitter`s between panes — not a docking framework, per the doc), plus `App.axaml`/`.axaml.fs` and `Program.fs` now actually bootstrap Avalonia (`AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime`) instead of just logging and exiting.

Hit one immediate build error, `AVLN2002: Duplicate x:Class directive` — Avalonia's SDK tooling auto-globs `**/*.axaml` as `AvaloniaResource` by default, so the explicit `<AvaloniaResource Include="...">` I'd added in both `Ui.fsproj` and `App.fsproj` double-registered the same file. Removed both explicit entries.

Can't visually verify the layout in this environment (no way to see a rendered window), so this is where the `Manual` label from A.13's WebView task applies here too: confirmed the solution builds clean and, since a broken Avalonia bootstrap would throw immediately, launched the real app with a timeout — it logged its startup line, ran the full 8 seconds with no exception, and left no orphaned process after being killed. That rules out a crash-on-boot but doesn't confirm the layout actually looks right; real visual confirmation is deferred to the milestone's acceptance check (or whenever a browser/display tool is available).

## 2026-08-05 — A.11: loopback asset host for Monaco

Added `AssetHost` in `Editor`: a plain `HttpListener`-based static file server (no ASP.NET Core — this is Monaco asset serving, not a real web app, and pulling in a full web host for it would be a lot of weight for what it does). `"127.0.0.1"` is the only host literal anywhere in the module, so there's no code path that could ever bind all interfaces. Port is OS-assigned: `HttpListener` doesn't support ephemeral-port prefixes directly, so the standard workaround applies — bind a `TcpListener` to port 0, read back the OS-assigned port, stop it, and immediately reuse that port for the real `HttpListener` prefix.

`resolveAsset` rejects anything that resolves outside `assetsRoot` after `Path.GetFullPath` normalization, and every response carries a restrictive CSP header. The path-traversal test needed care to actually test something: `Uri(baseUri, "../../secret.txt")` would have .NET's own `Uri` combiner collapse the literal `..` segments client-side before the request ever left the process, making the test pass without ever exercising the server's defense. Used percent-encoded `%2e%2e` instead — `Uri`'s dot-segment normalization operates on raw, still-encoded path segments, so encoded dots survive to the server, get decoded by `HttpListener` into literal `..`, and only then hit `resolveAsset`'s check. All four tests (known asset serves, unknown 404s, encoded traversal rejected, loopback-only) passed on the first real run.

## 2026-08-05 — A.12: editor bridge message contract

Added `EditorRequest` (the DU from Section 6, `TextChanged`/`CompletionRequested`/`HoverRequested`/`ExecuteRequested`/`CancelRequested`) and its per-case payload records to `Protocol`, and `EditorBridge.encode`/`decode` in `Editor` converting between it and the `EditorEnvelope` wire shape from A.1 — `Kind` identifies the case, `Payload` holds its fields. `decode` never throws: an unknown `Kind` or a payload missing required fields both return a typed `BridgeError`.

Two real compiler-level bugs, both from patterns already seen earlier this session:

- `open FSharpQueryStudio.Protocol` shadowed F#'s own `Result.Error` case with `ResultKind.Error` (a DU case I'd named `Error` back in A.6) — `Error { Message = ... }` silently resolved to the wrong `Error` and failed to typecheck as "not a function". Same category of bug as A.1's `WorkerRequestFrame`/`WorkerResponseFrame` field-name collision, but this time at the case-name level instead of record-field level. Fixed properly rather than working around it locally: marked `ResultKind` `[<RequireQualifiedAccess>]`, which happened to require no other changes anywhere — every existing use already wrote `ResultKind.Error` etc. explicitly.
- `CompletionRequestedPayload` and `HoverRequestedPayload` have identical field sets (`DocumentUri`/`Version`/`Line`/`Column`), so F# inferred a record literal's type from its field names independently of the `SerializeToElement<T>` call's explicit generic argument, guessing wrong for one of the two. Exactly the ambiguous-record-field problem from A.9's `WorkerRequestFrame`/`WorkerResponseFrame` `Id` clash — same fix, an explicit type annotation directly on the record literal, not just on the surrounding call.

## 2026-08-05 — A.13: WebView host wiring (Monaco actually loads)

The one `Manual` task in Milestone A — no automated test is possible for "does a real browser control render a real editor," and this environment has no display to check it visually. User explicitly signed off on best-effort-now, verify-later before starting.

Found the right package by searching NuGet directly rather than guessing: `Avalonia.Controls.WebView`, owned by the `avaloniaui` org itself (not a third-party community package). Its actual control class turned out to be `NativeWebView`, not `WebView` as its own package name would suggest — found by reading the assembly's raw PE metadata (`System.Reflection.Metadata`) rather than guessing at API shape blind, since a full `Assembly.Load` failed on missing Avalonia runtime dependencies in an isolated inspection context. Same for `WebMessageReceivedEventArgs`: a plain `Body: string` property, not the `TryGetWebMessageAsString()` pattern I'd assumed from the lower-level `ICoreWebView2` interop types nearby in the assembly.

Vendored Monaco's `min/vs` distribution (24MB, 151 files) via `npm install monaco-editor` into a scratch dir, then copied straight into `Editor/EditorAssets/`, served by A.11's `AssetHost`. Full bundle, not tree-shaken to just what F# Query Studio needs — doing that properly needs a bundler (webpack/esbuild) this repo doesn't have set up, not worth the risk to add mid-task. Wrote `editor-host.html`: loads Monaco via its AMD loader, registers a (currently empty) `fsharp` language, posts `textChanged` envelopes to the host via `window.chrome.webview.postMessage` on every edit, listens for host→editor messages with an intentionally-empty `switch` (each kind gets wired as its own later task) that ignores anything unrecognized rather than throwing.

Wiring `MainWindow.axaml.fs` to actually launch the app surfaced two real Windows-specific runtime bugs neither compiler nor any test could have caught:

- `RPC_E_CHANGED_MODE` (`COMException`) the instant the `NativeWebView` tried to attach — WebView2's COM initialization requires the main thread to be STA, and F#'s `[<EntryPoint>]` doesn't default to that the way C#'s `[STAThread] static void Main` does. Fixed with `[<STAThread>]` stacked on the entry point.
- After that, "Unable to create child window for native control host. Application manifest with supported OS list might be required." — a real Win32/manifest requirement for Avalonia's native control hosting. Added `app.manifest` with `supportedOS` GUIDs for Windows 7 through 11 and DPI-awareness settings, wired via `<ApplicationManifest>` in `App.fsproj`.

Also learned mid-verification that `dotnet run` launches through the shared `dotnet.exe` host, which does **not** carry the embedded manifest — had to run the built `FSharpQueryStudio.App.exe` apphost directly for the manifest fix to actually take effect. With both fixes in place: no exception, the process ran the full timeout duration, only a benign Chromium teardown log line on exit.

**A mistake worth recording plainly:** cleaning up after the timeout-killed run, `Get-Process -Name msedgewebview2 | Stop-Process -Force` killed more than intended — `msedgewebview2.exe` is a shared runtime other Windows applications use too, not exclusive to this app, and I killed by name globally instead of scoping to this process's own child tree. A second batch of `msedgewebview2` processes appeared immediately after (almost certainly pre-existing, unrelated to this session), and I stopped and asked rather than touching them further. Lesson for next time: get the child PID list *before* killing anything broad by name, and prefer killing a specific process tree (parent PID) over a name-based match against a shared/multi-owner process.

## 2026-08-05 — A.14: wire TextChanged to a DocumentViewModel

Added `DocumentViewModel` (`Uri`/`Text`/`Version`/`IsDirty`, cursor location and execution state join later per Section 5) and `ApplyTextChanged`, which applies the same stale-message defense Section 7 describes for analysis responses — a version guard — to the document's own version counter too: an update whose version isn't strictly greater than the current one is silently ignored, so an out-of-order bridge message can never regress `Version` or flip `IsDirty` back off. `ShellViewModel` grew a single `ActiveDocument` (the vertical slice's one editor tab; a real collection replaces this once Milestone C's script explorer exists).

Replaced A.13's placeholder `Ok _request -> ()` in `MainWindow.axaml.fs`'s `WebMessageReceived` handler with a real match on `TextChanged`, routing it into `shellViewModel.ActiveDocument.ApplyTextChanged`; every other `EditorRequest` case still falls through to a no-op until its own task wires it.

## 2026-08-05 — A.15: wire Run (F5) end to end — and a real cancellation bug found along the way

Added the pieces: `Projection.projectWorkerError` (a `WorkerErrorInfo` from a crashed/busy worker reads as `ResultKind.Error`, same as a script-level exception, since the UI needs one shape either way); `ResultViewModel` (`Kind`/`DisplayText`/`Schema`/`Rows`/`IsTruncated`/`ElapsedMs`, applied from a `ResultEnvelope`); `ShellViewModel.RunAsync`, gated by a settable `ExecuteAsync: (string -> Task<ResultEnvelope>) option` — `None` by default so the view model stays constructible and unit-testable without a real worker behind it, wired by the composition root; and `ExecutionService.evaluate` in `App` (the "host service" — builds the `execution/evaluate` request, sends it via `WorkerClient`, and projects whatever came back, including the crash/busy path, into a `ResultEnvelope`).

**Then a pre-existing test (A.9's cooperative-cancellation test) started failing — not from anything in this task, but exposed by running Integration.Tests with more tests in the assembly.** Chased it properly rather than adding a longer sleep:

1. First hypothesis, and genuinely real: `CancellationSignal`'s design (a static field, with scripts calling back into `FSharpQueryStudio.Execution` via a self-referencing `--reference:`) turned out to be *intermittently* broken — `FsiEvaluationSession`'s collectible `AssemblyLoadContext` sometimes loads its own separate copy of an explicitly path-referenced assembly instead of reusing the one already loaded in the process, giving the script's copy of the static field no connection to the one the host mutates. Rebuilt `CancellationSignal` around a sentinel file in the evaluation's working directory instead — OS-level, no assembly identity to get out of sync, and the script now just calls plain `System.IO.File.Exists(".cancel-requested")` with no reference to `Execution.dll` needed at all. Removed the `--reference:` from `FsiSession.create` entirely.
2. That fix alone made the failure worse — 100% reproducible instead of intermittent, which was actually the useful clue: a deterministic failure pointed at a real logic bug rather than remaining an unexplained flake. Root cause: `Handlers.handleEvaluate` set `currentWorkDir` *inside* the `Task.Run`'d background task that runs the FSI evaluation, but the worker's read loop backgrounds that task and immediately goes back to reading the next frame. A freshly started process's ThreadPool needs time to inject worker threads under load, so a client sending `execution/cancel` quickly enough could have it arrive *before* the backgrounded callback had even started — `handleCancel` would see `currentWorkDir = None` and silently no-op, burning the entire grace period until forced termination. Confirmed by comparing against the equivalent in-process `Execution.Tests` version of the same scenario (no subprocess, no threadpool cold-start) — it passed reliably every time, which narrowed the bug to the worker-process path specifically rather than the cancellation mechanism itself.

   Fixed by construction, not timing: split `handleEvaluate` into `beginEvaluate` (synchronous — the busy-check and `currentWorkDir` reservation) and `runEvaluate` (the actual FSI work, safe to background). The worker's loop now calls `beginEvaluate` inline, before it ever returns to reading the next frame, so a subsequent `execution/cancel` is *guaranteed* to observe the reservation — no race window exists to lose, regardless of threadpool timing. Applied the identical fix to `Worker.Tests`' busy-guard test, which had unknowingly relied on the exact same unsound "background it, then `Task.Delay`, then check" pattern.

Re-verified with the same rigor as the original diagnosis: the specific test that had been failing 100% of the time went to 5/5 clean runs afterward, each now taking ~2-3s instead of blowing through the full 15s grace period every time. Then the whole suite, three full runs, all green — 77 tests.

---

## 2026-08-05 — A.16: output console, scalar view, and basic table preview binding

Found a real gap while starting this: `Handlers.runEvaluate` never populated `ResultEnvelope.Output` — captured stdout/stderr from `Evaluation.evaluate` (A.5) was computed but silently dropped, so an "Output console" would have had nothing to bind to. TDD'd `Projection.toOutputLines` (stdout lines tagged `Stdout`, stderr lines tagged `Stderr`, single trailing-newline artifact dropped) and wired it into `runEvaluate`, merging it onto both the success and error envelope paths. Caught one real-behavior surprise along the way: FSI's captured output includes its own interactive echo (`> `-prefixed input line, `val it: ... = ...`), not just what the script printed — not a bug, just what `EvalInteractionNonThrowing` actually captures; adjusted the test to check `Contains` rather than exact equality against a raw `printfn` line.

Extended `ResultViewModel` for the three things the views need that weren't there yet: `Output: OutputLine list` (copied straight through from the envelope — Apply never re-enumerates anything, it only assigns already-materialized arrays/lists, confirmed with a test that fails if a sequence is pulled more than once); `IsTable`/`IsScalarOrText`, derived booleans so the XAML can switch presentation without a custom value converter; and `FormattedRows: string list`, a comma-joined-per-row string list for a plain (non-virtualized) table preview — real `DataGrid` virtualization is explicitly C.12's job, building on this, once Milestone C's larger result sets make it worth the complexity.

Wired `MainWindow.axaml`'s Results tab (`DisplayText` for Scalar/Text/Error, `FormattedRows` list for Table) and Output tab (a `ListBox` over `Output`, monospace). Discovered this project's first data-binding XAML doesn't compile as-is on Avalonia 12.1.1: `ItemsControl`/`ListBox` bind via `ItemsSource`, not `Items` (AVLN3000 pointed at the wrong property name); and compiled bindings need an `x:DataType` at every template level once anything binds — since this is the first binding markup in the app (everything through A.15 only used `FindControl`/code-behind), set `x:CompileBindings="False"` on the `Window` rather than introduce per-template `x:DataType` scaffolding for a single view. Verified by building `FSharpQueryStudio.App` (which compiles the XAML) clean; real interactive rendering isn't headlessly automatable, same "manual verification deferred" rationale as A.13.

**Known gap, not this task's scope:** `ShellViewModel.ExecuteAsync` is still never actually set anywhere in `App` — `App.axaml.fs`'s composition root constructs a bare `MainWindow()` with no `WorkerClient`/`ExecutionService` wired in, so A.15's "wire Run end to end" is complete at the view-model/service level (and integration-tested directly) but not yet reachable by actually running the compiled app and pressing F5. Needs closing before A.19's milestone acceptance test, which requires the real UI process end to end.

Full solution: 89 tests, all green (`Ui.Tests` 28, `Worker.Tests` 10, `Results.Tests` 10, up from 20/6/7 respectively).

---

## 2026-08-05 — A.17: wire Cancel to execution/cancel through the UI command

`WorkerClient.Cancel(requestId, gracePeriod)` (A.9) needs the id of the in-flight `execution/evaluate` request, but `ShellViewModel.ExecuteAsync`'s signature (`string -> Task<ResultEnvelope>`, from A.15) never exposes one — deliberately, so the view model stays decoupled from `WorkerClient`/request-id bookkeeping. Rather than leak the id up through `ExecuteAsync`'s signature, added `ExecutionService.evaluateWithId` (same as `evaluate`, but the id is supplied instead of generated) and gave `ShellViewModel` a second, symmetric settable slot: `CancelAsync: (unit -> Task<unit>) option`. The composition root's closure is what actually ties them together — it tracks "the id of whatever `ExecuteAsync` most recently sent" in a local mutable cell that only it can see; `ShellViewModel` calls `Cancel()` with zero knowledge that request ids exist. `Cancel()` is a no-op unless `WorkerState.Current = Running` (nothing to cancel from any other state, and `Running` is the only state `RunAsync` ever reaches).

Wired `WorkerState` transitions into both paths for real, which `RunAsync` never touched before this task: `Running` on entry, back to `Ready` on completion (works whether the run finished normally or was cancelled first, since `Cancelling -> Ready` is already an allowed transition from A.10's state table); `Cancel()` moves `Running -> Cancelling` before invoking the configured `CancelAsync`.

TDD'd at both levels the dev plan calls for: `Ui.Tests` (`ShellViewModelTests`) drives `RunAsync`/`Cancel` against fake `TaskCompletionSource`-gated `ExecuteAsync`/`CancelAsync` closures — no real worker, asserting the state machine and no-op conditions (not Running, `CancelAsync` unconfigured) in isolation. `Integration.Tests` (`ExecutionServiceTests`) repeats A.9's two cancellation scenarios — cooperative script stops early via the sentinel file and the worker survives, non-cooperative script gets forcibly killed after a short grace period — but now driven by `shell.Cancel()` through a real `WorkerClient`/worker process instead of calling `WorkerClient.Cancel` directly, confirming the UI command reaches the same two outcomes A.9 already proved at the transport level.

**Same known gap as A.16, now also covering Cancel:** the real `App` composition root (`App.axaml.fs`) still never constructs a `WorkerClient` or sets `ExecuteAsync`/`CancelAsync` on the real `ShellViewModel`, and `MainWindow.axaml` has no actual Run/Cancel controls wired to commands yet (just a placeholder `_Run` menu item). Both are integration-tested at the service level but not reachable by running the compiled app. Tracked as the same pre-A.19 closing task as A.16's gap — best done together in one pass rather than twice.

Full solution: 105 tests, all green (`Ui.Tests` 32, `Integration.Tests` 9, up from 28/7 respectively).

---

## 2026-08-05 — A.18: structured log instrumentation for Milestone A's code paths

Scoped to Section 15's table entries that A.3–A.9's handlers actually own, matching the dev plan's Notes column ("driving A.3–A.9's handlers emits the expected event for each, with the correlation ID from the triggering request attached") rather than the table's full breadth — UI-triggered actions and connector-call logging belong with the code that does those things (later milestones), not retrofitted here.

Worker-side (`Handlers.fs`, now `open Serilog` directly — the `Worker` project already carries the `Serilog`/`Logging` references from A0): `Log.Information` for session creation (`runEvaluate`, right before `FsiSession.create`), execution outcome (after projection, with `ElapsedMs` and the resulting `ResultKind`), and cancellation-requested (`handleCancel`, unconditionally — even a no-op cancel is worth a log line). None of these needed to touch `Correlation`/`LogContext` themselves: `Program.fs`'s read loop already pushes `request.Id` as `CorrelationId` around both the backgrounded `runEvaluate` task and the synchronous `dispatch` path (from A.9's work), so anything logged inside those handlers picks it up automatically via Serilog's ambient `LogContext` enricher.

Host-side (`WorkerClient.fs`, same `open Serilog` addition): `Log.Information` with `Pid` when a process starts (covers both "start" and "restart" — a restart after a crash is just another call to the same `startProcess`), `Log.Warning` when the reader loop's stdout-closed branch fires (a crash, from A.8), `Log.Warning` when `Cancel`'s grace period expires and it kills the process (from A.9). These don't carry a `CorrelationId` on principle, not by omission: process lifecycle isn't scoped to one request the way `Handlers`' events are — a crash can arrive while several requests are pending, and attaching just one of their ids would be arbitrary.

TDD'd with the same in-memory-sink pattern `Logging.Tests` established (a `CollectingSink : ILogEventSink`, `LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(...)`), but with a twist neither `Logging.Tests` nor any earlier test needed: `Handlers`/`WorkerClient` log through Serilog's *static* `Log.Logger`, not an injected instance, so the tests swap `Log.Logger` for the collecting one and restore the original in a `finally` — safe only because `Worker.Tests` and `Integration.Tests` both already disable cross-test parallelism (for unrelated process-global-state reasons from earlier milestones), so no other test on the same thread pool sees the swapped logger mid-test.

Full solution: 110 tests, all green (`Worker.Tests` 12, `Integration.Tests` 12, up from 10/9 respectively).

---

## 2026-08-05 — A.19: Milestone A acceptance test — and closing the composition-root gap tracked since A.16

Wrote the acceptance criterion as one continuous `Integration.Tests` test (`MilestoneAAcceptanceTests.fs`): a single `ShellViewModel`/`WorkerClient` pair opens a real `.fsx` file from disk, runs it and reads back both the scalar result and the printed output, cancels a long-running cooperative script through the same UI command as A.17, survives the worker process being killed mid-request (A.8's crash detection), and successfully runs one more script afterward — all without ever constructing a second `ShellViewModel` or `WorkerClient`, which is what "without restarting the UI" actually means at this layer. Extracted the `wireShell` helper A.17 had written inside `ExecutionServiceTests.fs` into `TestHelpers.fs` since this test needed the identical wiring. Passed on the first run, then confirmed 3/3 stable given the cancel/crash steps are timing-sensitive (`Task.Delay(200)` before cancelling/killing, same margin A.9/A.17 already relied on).

That the acceptance test passed immediately was a signal, not a shortcut: it's a composition of pieces each already independently TDD'd (A.5 through A.18), so a first-try green run means the composition itself was already correct — the value of writing it was proving that, not discovering new bugs.

**Closed the gap flagged in A.16 and A.17's entries** rather than deferring it a third time: `App.axaml.fs`'s composition root now actually constructs a `WorkerClient` and wires `ExecuteAsync`/`CancelAsync` into the real `ShellViewModel`, via a new `MainWindow.WireExecution(executeAsync, cancelAsync)` method kept deliberately separate from `MainWindow`'s constructor — so headless/`Ui.Tests` construction (none currently exists, but the door stays open) never spawns a real worker process as a side effect of building a window. Added `RunButton`/`CancelButton` to `MainWindow.axaml` (a small toolbar above the script tabs, plus a `WorkerState.Current` label) with click handlers in code-behind, matching the existing pattern (`WebMessageReceived.Add`) rather than introducing `ICommand`/`RelayCommand` infrastructure this codebase hasn't needed anywhere else yet.

No new automated test covers `App.axaml.fs`/`MainWindow.WireExecution` directly — matches [Section 16's test pyramid](./fs-studio-plan.md#16-engineering-standards-and-tdd-workflow), which already scopes `App` to "covered indirectly... exercised through Worker and Ui integration tests," and `MilestoneAAcceptanceTests.fs` exercises the identical wiring logic end-to-end. The actual click-a-real-button-and-see-Monaco-respond loop isn't headlessly automatable, same rationale as A.13's WebView work — manual verification deferred, not skipped.

Full solution: 111 tests, all green (`Integration.Tests` 13, up from 12).

**Milestone A acceptance criterion met:** a user can open a standalone `.fsx` script, execute it in isolation, read output/results, cancel it, and recover from a worker crash without restarting the UI — proven at the `ShellViewModel`/`WorkerClient` level by `MilestoneAAcceptanceTests.fs`, and now actually reachable by running the compiled app (interactive rendering itself still manual-verify-only, per A.13).

---

## 2026-08-05 — B.1: standalone-script analysis context builder

First `Analysis` project code. Per Section 8's call to keep FCS analysis logically separate from FSI runtime evaluation, `AnalysisContext` doesn't reuse anything from `Execution.FsiSession` — it's a fresh `FSharpChecker.Create()` asking `GetProjectOptionsFromScript` what an empty script anchored at a given working directory would see, then filtering `OtherOptions` down to `-r:` reference entries.

The determinism requirement (building the context twice for the same inputs yields byte-identical reference sets) is the one this task is explicitly graded on, so it's not left as an assumption about FCS's own resolution order — `Array.sort` is applied before returning, making the guarantee structural rather than incidental.

Same `FSharp.Compiler.Service`/`FSharp.Core` version-pin `NU1608` suppression A.4 already needed in `Execution`/`Execution.Tests` was required again in `Analysis`/`Analysis.Tests`, for the same reason.

Full solution: 114 tests, all green (`Analysis.Tests` 3, new).

---

## 2026-08-06 — B.2: analysis/check handler

Extended `AnalysisContext.Context` with `ScriptPath`/`ProjectOptions` (B.1 only needed `References`) and added `Analysis.Checker.check`, which reuses `context.ScriptPath` as the file identity FCS type-checks a new source snapshot against — the context was already built with that path as its sole source file, so no file needs to exist on disk for a real snapshot to be checked. `AnalysisContext.checker` (the shared `FSharpChecker` instance) is now exposed for `Checker` to reuse, rather than each module cold-starting its own.

Deliberately stayed at "raw FCS diagnostics" scope per the task split: `Protocol.RawDiagnostic`/`AnalysisCheckResult` carry only `Message`/`Severity`, no position — coordinate conversion into the real `Diagnostic` shape (Section 8's 1-based-FCS-vs-0-based-Monaco call-out) is B.3's job, not this one's.

Wiring `analysis/check` into `Handlers.dispatch` forced `dispatch` itself from sync to `Task<WorkerResponseFrame>` (analysis needs FCS's async `ParseAndCheckFileInProject`), which meant touching every existing `dispatch` call site — `Program.fs`'s loop and four `HandlersTests.fs` cases — to await it instead of binding it directly. All were mechanical (`let` to `let!` inside a `task` block); no behavior changed for the pre-existing methods.

Hit an unrelated compile break surfaced by adding `AnalysisCheckParams { DocumentUri; Version; Source }` to `Protocol`: it has the exact same field set as `Editor.TextChangedPayload`, and F#'s record-field-name resolution picked the newer type for the one call site in `EditorBridge.fs`'s `TextChanged` case that relied on inferred (not explicit) record typing — a real ambiguity, not a red herring, since two same-shaped records now exist. Fixed by annotating that record literal explicitly, matching the pattern already used one case below it in the same file.

Full solution: 116 tests, all green (`Worker.Tests` 13, up from 12).

---

## 2026-08-06 — B.3: FCS-to-protocol Diagnostic mapping and coordinate conversion

Before writing the mapping, spiked (throwaway, deleted after) what FCS's `FSharpDiagnostic` actually reports rather than trusting the task title's shorthand at face value: `StartLine`/`EndLine` are 1-based, but `StartColumn`/`EndColumn` are *already* 0-based — not the uniformly-1-based picture "1-based FCS vs 0-based Monaco" suggests. `DiagnosticMapping.map` in `Analysis` converts only the line numbers (`- 1`); columns pass through unchanged. Worth the five minutes: a mapping written straight from the doc's prose would have silently double-offset every column.

Added `Protocol.Diagnostic`/`DiagnosticSeverity` verbatim from Section 8's type definition, and replaced B.2's placeholder `RawDiagnostic` (message + severity string, no position) with the real thing now that the conversion exists — `AnalysisCheckResult.Diagnostics` is `Diagnostic list` end to end.

That same `DiagnosticSeverity.Error` case collided with `FSharp.Core`'s `Result.Error` everywhere `Protocol` is opened alongside a `Result`-returning match — same class of bug as B.2's `TextChangedPayload`/`AnalysisCheckParams` field-set collision, different mechanism (case-name shadowing a BCL case, not ambiguous record-field inference). Surfaced in three unrelated-looking places (`EditorBridge.fs`, `EditorBridgeTests.fs`, `MainWindow.axaml.fs`) the moment `Diagnostics.fs` compiled — fixed by qualifying the shadowed pattern as `Result.Error` at each site rather than renaming the documented type.

`Analysis.Tests`: five cases per the task's own list — multiline span, leading blank lines, end-of-file span, Unicode source (all assert the 0-based line/column lands correctly), plus one for severity/error-number-code mapping.

Full solution: 121 tests, all green (`Analysis.Tests` 8, up from 3).

---

## 2026-08-06 — B.4: debounce source-change requests

Added `IDebounceScheduler`/`RealDebounceScheduler`/`Debouncer` in `Ui`: `Notify` cancels whatever was previously scheduled before scheduling the new action, so a burst of calls within the delay window collapses to one eventual fire — the mechanism, not yet wired to anything. `RealDebounceScheduler` wraps `System.Threading.Timer`; tests get a `FakeDebounceScheduler` (`Ui.Tests`) driven by a virtual clock (`Advance(TimeSpan)`) instead of waiting on a real one, avoiding the class of timing flake earlier milestones already hit with real waits.

Wired it into `DocumentViewModel`: a new `DebouncedTextChanged` event fires once, 400ms (mid Section 8's 300-500ms range) after the last of a burst of `ApplyTextChanged` calls, carrying the settled version/text rather than any intermediate keystroke — the hook a later task's `analysis/check` call will subscribe to. `DocumentViewModel`'s constructor takes an optional `IDebounceScheduler`, defaulting to the real one, so every existing call site (`ShellViewModel`, prior tests) is unaffected.

Full solution: 125 tests, all green (`Ui.Tests` 36, up from 32).

---

## 2026-08-06 — B.5: document-version guard

Added `DocumentViewModel.Diagnostics`/`ApplyAnalysisResult(responseVersion, diagnostics)`: applies only when `responseVersion = version` exactly (not `>=`, unlike `ApplyTextChanged`'s monotonic guard — an `analysis/check` response is tied to one specific snapshot, not a running counter, so anything but an exact match is either stale or from a version that no longer exists). Two tests: the matching-version case, and the literal out-of-order scenario the task names — a response for version 2 applied first, then a late response for version 1 arrives and is discarded, leaving version 2's diagnostics untouched.

Full solution: 127 tests, all green (`Ui.Tests` 38, up from 36).

---

## 2026-08-06 — B.6: Monaco marker rendering from Diagnostic list

Added `Protocol.HostNotification`/`DiagnosticsUpdatedPayload` and `EditorBridge.encodeNotification` — the opposite direction from `EditorRequest`/`decode` (host to Monaco, not Monaco to host), so there's deliberately no matching `decode` for it; only the JS side in `editor-host.html` ever consumes this direction. `Editor.Tests` covers the bridge contract only (envelope kind + payload shape), per the task's own scope split — actual marker rendering is manual-verify-only, same rationale as A.13's WebView work.

Filled in `editor-host.html`'s `"diagnosticsUpdated"` case (left as a stub since A.14, explicitly called out in that stub's own comment as "wired... in its own later task") — converts each `Diagnostic`'s 0-based line/column to Monaco's 1-based marker coordinates and calls `setModelMarkers`. Severity matched case-insensitively rather than against a specific string casing: `FSharp.SystemTextJson`'s exact wire encoding for a fieldless-case union isn't pinned down by an automated test on this path, so guessing the exact casing and getting it wrong would have been a silent, untestable bug.

Full solution: 129 tests, all green (`Editor.Tests` 14, up from 12).

---

## 2026-08-06 — B.7: Problems pane and diagnostic navigation

Added a second `HostNotification` case, `CaretMoved(documentUri, line, column)`, alongside `DiagnosticsUpdated` — same encode-only pattern (no `decode`, JS-side-only consumer), covered by the same bridge-contract test style as B.6.

Added `Ui.ProblemViewModel` (one row per diagnostic) and `ShellViewModel.Problems`/`SendToEditor`: `Problems` maps `ActiveDocument.Diagnostics` to `ProblemViewModel`s on every access (nothing binds to it yet, so no caching/observable machinery), and `SendToEditor` is the composition root's hook into the real WebView bridge — same optional-function pattern as `ExecuteAsync`/`CancelAsync` (A.15/A.17), letting `Navigate()` be tested by capturing what it would have sent rather than driving a real WebView. `Navigate()` sends the diagnostic's start position as a `caretMoved` message; a no-op if `SendToEditor` was never configured, matching `RunAsync`/`Cancel`'s existing no-op-when-unconfigured contract.

Full solution: 133 tests, all green (`Ui.Tests` 41, up from 38; `Editor.Tests` 15, up from 14).

---

## 2026-08-06 — B.8: analysis/hover

Spiked (throwaway, deleted after) the exact FCS hover API before writing anything real — three things weren't in memory precisely enough to trust: `GetSymbolUseAtLocation`'s exact parameter shape, whether `FSharpMemberOrFunctionOrValue.XmlDoc` distinguishes "no doc comment" from "empty doc comment" as a separate union case, and how to resolve a symbol from a bare `(line, column)` when the protocol only ever gives a cursor position, never a pre-split identifier list. Findings: `XmlDoc` always comes back as `FromXmlText`, even with no `///` comment above the binding — the *inner* `XmlDoc.IsEmpty` is what actually distinguishes "no doc" (`true`) from "has doc" (`false`), not the outer case. And `FSharp.Compiler.EditorServices.QuickParse.GetPartialLongNameEx(lineText, column)` is the real mechanism editor tooling uses to turn a raw cursor column into the qualified-identifier list `GetSymbolUseAtLocation` needs — confirmed it round-trips correctly for a plain identifier before writing the real module.

Added `Protocol.HoverInfo` (Section 8's fields verbatim) and `Analysis.Hover.hover`, taking FCS's own coordinate convention directly (1-based line, 0-based column at/after the identifier's end) — this layer talks straight to FCS, so no protocol-facing coordinate conversion belongs here (unlike B.3's `DiagnosticMapping`, there's no position *in* `HoverInfo`'s output to convert; the caller already knows what position it asked about). `Signature` is `Some` only for callable members (`IsFunction` or non-empty `CurriedParameterGroups`) — a plain `let` binding's `Signature` is `None`, distinct from its always-present `Type`. Five tests, all green on the first run after the spike de-risked the two unknowns.

Full solution: 138 tests, all green (`Analysis.Tests` 13, up from 8).

---

## 2026-08-06 — B.9: hover wiring through the bridge to a Monaco hover provider

Added `HostNotification.HoverResolved(requestId, documentUri, hover)` — the one host-to-editor case that deliberately reuses the *triggering* `EditorRequest.HoverRequested`'s own envelope `Id` instead of minting a fresh one (`encodeNotification`'s other cases all mint fresh IDs), since Monaco's `provideHover` returns a `Promise` that only this specific request's caller is waiting on — there is no other way to correlate a response to the right pending promise. `Editor.Tests` covers both the id-reuse and the `None`-hover (no crash, no error) cases, same bridge-contract-only scope as B.6/B.7.

Filled in `editor-host.html`'s JS side properly this time: registered a real `monaco.languages.registerHoverProvider` that sends `hoverRequested` and resolves its promise when the matching `hoverResolved` arrives (tracked in a `pendingHoverResolvers` map keyed by id), plus a `hoverInfoToContents` mapper from `HoverInfo`'s fields to Monaco's `contents` shape. Also closed a gap noticed from B.7: `caretMoved`'s encode side existed but nothing in the JS ever consumed it — added that case (`setPosition`/`revealPositionInCenter`/`focus`) while already in this file. All of this JS remains manual-verify-only, same rationale as A.13/B.6 — Avalonia's WebView isn't headlessly testable.

Full solution: 140 tests, all green (`Editor.Tests` 17, up from 15).

---

## 2026-08-06 — B.10: completions tier 1 (MVP)

Added `Analysis.Completions`: static `keywords`/`snippets`/`connectorHelperNames` lists (the connector names are the MVP placeholder — Section 11's `Connections.sql "Sales" "..."` shape — since no real connector exists until Milestone C) plus `complete recentNames`, which merges all of it with whatever names the caller passes in for "the most recent successful check." Deliberately not position-sensitive: tier 1 doesn't filter by cursor context, matching the task's own "arbitrary cursor position" framing — that's tier 2's job (B.11). `recentNames` is accepted as a plain parameter rather than cached inside this module, since owning that cache (tied to document/session state) belongs to whatever calls this, not to a stateless completions list merge.

Full solution: 142 tests, all green (`Analysis.Tests` 15, up from 13).

---

## 2026-08-06 — B.11: completions tier 2 (semantic)

Spiked (throwaway, deleted after) `FSharpCheckFileResults.GetDeclarationListInfo` before writing the real code — needed the exact parameter shape (it takes the parse results too, not just check results, plus a `PartialLongName` from `QuickParse.GetPartialLongNameEx` and a `getAllEntities` callback) and, more importantly, hit a real gap in my own assumptions about FCS's `column` convention: it's the *inclusive index of the last consumed character*, not "one past the end" the way `Diagnostic`'s columns work. Confirmed by comparing `QuickParse.GetPartialLongNameEx("List.m", col)` across every column value and watching exactly where `QualifyingIdents`/`PartialIdent` split.

That same spike surfaced a genuine FCS quirk, not a bug in this code: a bare trailing dot on a *module* name (`"List."`, nothing typed after) is a hard parse error (`"Missing qualification after '.'"`) in FCS's standalone-script checker, unlike a bare trailing dot on a *value* (`"foo."` resolves fine as an in-progress member access). A first attempt at the task's "known standard-library symbol" test using `List.` triggered exactly this and returned zero completions; a second attempt using `List.m` returned a single spurious `<Note>` item instead of `List` module members, which didn't reproduce cleanly enough to trust as a real FCS behavior rather than an artifact of the spike reusing one script path across multiple source snapshots. Rather than chase an FCS edge case further, retargeted the test to the same proven-reliable pattern as the member-access test: a bound value of a known BCL type (`System.Text.StringBuilder`), which is just as much "a real FCS session for a known standard-library symbol" and doesn't depend on module-qualified-access behavior that's fragile even in real F# tooling.

Added `Completions.completeAtPosition`, alongside tier 1's `complete`, following `Hover.hover`'s same FCS-native-coordinate convention (this layer talks straight to FCS, no protocol-facing conversion here either).

Full solution: 144 tests, all green (`Analysis.Tests` 17, up from 15).

---

## 2026-08-06 — B.12: Milestone B acceptance test — and closing the gap between B.1-B.11's pieces and a real end-to-end analysis pipeline

Writing this test honestly required more than assembling already-built pieces — B.4 through B.9 built the debounce mechanism, the version guard, the bridge encoders, and the Problems pane, but nothing had ever actually *called* `analysis/check`/`analysis/hover` over the wire yet. Closed that gap first:

- `Handlers.handleAnalysisHover` + `dispatch` wiring — the worker side of `analysis/hover` never existed; B.8/B.9 only built the FCS-facing `Hover.hover` function and the bridge's encode side. Added `Protocol.AnalysisHoverParams`/`AnalysisHoverResult`, TDD'd the same way B.2 TDD'd `analysis/check`'s handler.
- `App.AnalysisService` (`check`/`hover`), mirroring `ExecutionService`'s shape — the "host service" layer between a UI call and the worker never existed for analysis, only for execution.
- `ShellViewModel.CheckAsync` + a constructor-time subscription to `ActiveDocument.DebouncedTextChanged` that calls it and applies the result through `ApplyAnalysisResult` — the actual live wiring from "text changed" to "diagnostics appear," which no earlier task actually connected end to end. Required converting `ActiveDocument` from `member val` to a `let` binding (F# requires `let`/`do` bindings to precede all members in a type body, and the constructor's `do` block needs to reference it before any member declarations legally can).
- Wired `CheckAsync` in `App.axaml.fs`'s composition root, and exposed `MainWindow.ShellViewModel` so it could reach it — same "close the gap while writing the acceptance test" precedent as A.19.

**The stale-response race, specifically.** Backgrounded `analysis/check`/`analysis/hover` in `Program.fs`'s read loop (`Task.Run`, same shape as `execution/evaluate`'s existing backgrounding) — before this, the read loop processed every non-evaluate method fully synchronously, meaning two `analysis/check` requests sent back-to-back could *only* ever complete in send order; there was no way for a genuinely older response to arrive after a newer one, making B.5's guard untestable under real concurrency. Unlike `execution/evaluate`, no busy-slot reservation is needed here — each call builds its own isolated `AnalysisContext` in a fresh temp directory already, so concurrent analysis requests don't share mutable state the way `FsiSession` does.

Chose not to chase literal wall-clock racing for the acceptance test itself, though: awaiting two concurrent requests and hoping the older one *happens* to resolve second would be exactly the kind of timing-dependent flake this codebase has avoided elsewhere. Instead the test deterministically controls response *processing* order — awaits and applies the newer (v2) response first, then awaits and applies the older (v1) response second — which is a faithful real request/response round trip through the same worker, "artificially delayed" in the sense the task names (the test chooses when to apply each response), not a mocked guard check like B.5's own unit test.

Also had to bump the "squiggles" scenario's wait from the debounce window alone (400ms) to 3s — a freshly spawned worker's very first `analysis/check` pays FCS's cold reference-resolution cost on top of the debounce, the same first-call latency A.4's DEVLOG entry already flagged for `FsiSession`.

Full solution: 138 tests, all green (`Worker.Tests` 14, `Ui.Tests` 42, `Integration.Tests` 16 — `MilestoneBAcceptanceTests.fs` new, 3 tests, run 3x to confirm no flake).

**Milestone B acceptance criterion met:** invalid F# produces diagnostics with `Error` severity (squiggles, once rendered — B.6's manual-verify boundary), hovering a local symbol resolves its display name and type through a real worker round trip, and a stale analysis response is discarded rather than overwriting newer editor state, proven under a real (not simulated) request/response race enabled by backgrounding analysis requests.

---

## 2026-08-06 — C.1: multi-tab document management on `ShellViewModel`

Added `Documents`/`OpenDocument`/`CloseDocument` to `ShellViewModel`, turning the single `ActiveDocument` slot from A.14 into a real workspace: opening a second script activates a new tab without touching the first's state, reopening an already-open URI reactivates it instead of duplicating a tab, and closing a dirty tab routes through a new `ConfirmDiscardDirtyDocument` hook (same optional-function no-op-when-unconfigured pattern as `ExecuteAsync`/`CancelAsync`) before discarding. Closing the last tab opens a fresh blank one rather than ever leaving `ActiveDocument` unset — every existing caller from Milestone A assumed exactly one always exists.

## 2026-08-06 — C.2: `ConnectorKind`/`ConnectionDefinition` protocol types and `IConnector` abstraction

Added `ConnectionDefinition` to `Protocol`: round-trips through JSON with a `JsonElement` configuration blob and, per Section 10, no credential material at all — only a `SecretReference` pointer, so the shape itself makes "never write a secret to disk" structural rather than a convention to remember later. `IConnector` stays deliberately thin (`Kind`, `TestConnectionAsync`, `DiscoverSchemaAsync`) — query and paging shapes are connector-specific and land with each concrete connector in C.5–C.10, not forced onto one shared interface prematurely.

## 2026-08-06 — C.3: `connection/register` handler and `workspace.json` read/write

Added `Workspace.readConnections`/`writeConnections` (round-trips a `ConnectionDefinition` list through a `workspace.json` file) and `registerConnection`, the `connection/register` handler itself — upsert by `Id`, then persist the full list. Since `ConnectionDefinition` (C.2) only ever carries a `SecretReference` pointer, the written JSON is safe by construction; no redaction step is needed because there's never a secret value in memory to redact at this layer.

## 2026-08-06 — C.4: secret-reference abstraction over the OS credential store

Added `ISecretStore` (`Save`/`TryRead`/`Delete`) to keep `SecretReference` resolution platform-neutral, `InMemorySecretStore` as the fake every unit suite drives, and `WindowsCredentialStore` — P/Invokes `advapi32`'s `CredWriteW`/`CredReadW`/`CredDeleteW` directly for the real Windows credential-manager backing store. Verified against the actual Windows Credential Manager in a `Integration.Tests` case gated by a custom `WindowsOnlyFactAttribute` (xUnit v2 has no built-in dynamic skip, so this had to be written by hand) so the suite stays green on non-Windows CI later. A `Storage.Tests` case proves a registered connection's credential lands in the secret store and never in `workspace.json`, closing the loop with C.3.

## 2026-08-06 — C.5: SQLite connector

Added `SqliteConnector`: `BindParameters` binds every value as a real `SqliteParameter` (unit-tested standalone, no database needed), `ExecuteQuery` reads through a live `SqliteDataReader` lazily per Section 10's reader-based paging rather than materializing the full result set, and `DiscoverSchemaAsync` walks `sqlite_master` plus `PRAGMA table_info` for schema discovery. Bumped the transitive `SQLitePCLRaw.lib.e_sqlite3` dependency past the version NU1903 flagged for `GHSA-2m69-gcr7-jv3q` — a real advisory, not a false positive to suppress.

## 2026-08-06 — C.6: SQL Server connector

`SqlServerConnector` mirrors C.5's shape over `Microsoft.Data.SqlClient`: bound parameters (never concatenated), reader-based lazy row paging, `INFORMATION_SCHEMA`-based schema discovery. Verified against a real containerized SQL Server via `Testcontainers.MsSql` (`ICollectionFixture`, one container shared across the file's tests) — deliberately never a shared/remote service, matching the dev plan's own call-out for this task.

## 2026-08-06 — C.7: PostgreSQL connector

`PostgreSqlConnector` mirrors the same shape again over `Npgsql`, schema discovery scoped to the `public` schema via `information_schema`. Verified against a real containerized PostgreSQL via `Testcontainers.PostgreSql`, same fixture-sharing pattern as C.6.

## 2026-08-06 — C.8: CSV connector

`CsvOptions` covers delimiter/encoding/header choices. Wrote a small RFC4180 streaming parser, `CsvParsing.readRecord`, rather than reaching for `StreamReader.ReadLine`: a quoted field's raw embedded newline isn't a record boundary, so line-based reading would silently corrupt any file with a quoted multi-line value. `ReadRows` streams rows lazily instead of loading the whole file, matching the reader-based-paging spirit of C.5–C.7 even though there's no database reader underneath. `CsvConversions` gives scripts `tryParseInt`/`Float`/`Bool`/`DateTime` over a cell's raw string. Covered with temp fixtures for quoted commas, embedded newlines, escaped `""` quotes, and a non-UTF8 (ISO-8859-1) encoding.

## 2026-08-06 — C.9: Excel connector

`ExcelConnector` (over ClosedXML) selects a target sheet by name or defaults to the first, applies header options the same way `CsvConnector` does, and bounds `ReadRows` to `maxRows` (default 500, matching `Results.Projection`'s preview limit from A.6) rather than materializing an entire worksheet. Cell values keep their natural .NET type (number/boolean/date/text) instead of flattening everything to display strings. `DiscoverSchemaAsync` reports every worksheet as its own table. Covered with a temp `.xlsx` fixture spanning multiple sheets and a header-less sheet.

## 2026-08-06 — C.10: REST/JSON connector

`RestConnector.SendAsync` builds a fully-configured `HttpRequestMessage` (method, query params merged onto the URL, headers, JSON body) and parses the response as `JsonElement`. `PaginateAsync` follows a caller-supplied `extractNextUrl` reader across pages until it returns `None`, since unlike the database/file connectors there's no standardized "next page" shape across arbitrary REST APIs to hardcode against. Schema discovery is intentionally empty for the same reason — no universal way to enumerate "tables" from an arbitrary API. Verified against an in-process `HttpListener` stub bound to loopback, never a real external endpoint. Completes all six first-day connectors (C.5–C.10).

## 2026-08-06 — C.11: controlled script API surface (`Connections.sql`/`csv`/`excel`/`rest`)

Added `Connections`, the only surface a `.fsx` script actually touches to reach a named configured connection — every function dispatches straight to the underlying connector's own bound-parameter API, so a script has no path to interpolate a value into command text through this API even if it tried; the injection-safety guarantee C.5–C.10 already built in at the connector level is now the *only* way a script can query anything. `IConnectionResolver` keeps the module decoupled from how the real registry gets built (`workspace.json` + secret store wiring lands with the composition root later, not here). Regression-tested with two classic SQL-injection payloads (`OR 1=1` and a statement-terminator) against all three SQL-capable connectors — SQLite locally, SQL Server and PostgreSQL against their containers — proving each is treated as literal parameter data, never executed. CSV/Excel/REST routing covered too, completing script-surface coverage across C.5–C.10.

## 2026-08-06 — C.12: virtualized `DataGrid` binding for table results

Replaced A.16's plain `FormattedRows` `ItemsControl` with a real Avalonia `DataGrid` bound directly to `Result.Rows`, which virtualizes rows itself for large result sets — the complexity A.16's own entry explicitly deferred to "once Milestone C's larger result sets make it worth it." `ResultGridColumns.build` is the UI-framework-agnostic piece: maps `ResultEnvelope.Schema` to header/indexed-binding pairs, kept pure and testable since Avalonia needs concrete columns built at runtime (no schema-driven column binding exists in XAML itself). `MainWindow`'s code-behind rebuilds `DataGrid.Columns` whenever `Result.Schema` changes.

Needed `Avalonia.Controls.DataGrid` added to both `Ui` and `App` — `App`'s own AXAML compiler needs the direct package reference to resolve its `StyleInclude` even though `Ui` already pulls it in transitively. Also had to find the theme resource path by trial: it's `.../Themes/Fluent.xaml`, not `.axaml`, despite every other Avalonia resource in this codebase using `.axaml`.

## 2026-08-06 — C.13: `results/page` end-to-end wiring

Added `PagingCursor`, which wraps a live `IEnumerator` and peeks one item past every page so it can answer "is there more" without an extra round trip, while staying the *same* enumerator across calls — deliberately not `Seq.skip`, which restarts a sequence from scratch and would silently re-run a connector-backed query on every page. `Projection.projectWithCursor` hands the cursor out alongside the usual envelope only when a `Table` result gets truncated; the existing `project` (A.6) is unchanged and still drops the cursor, so every prior caller/test keeps working untouched.

`Handlers` now issues a token and stashes the cursor whenever an evaluate truncates, and dispatches `results/page` against it — reusing Section 9's one-evaluation-at-a-time model, since a worker never has more than one open paging cursor at a time. A fresh evaluate clears any prior cursor, so its token stops resolving. An unknown or expired token returns a structured `results/unknownToken` error rather than silently re-running the script. Verified end-to-end against the real worker process: a 510-item sequence truncates at the 500-row preview limit, `results/page` returns the remaining 10, and a bogus token is rejected.

## 2026-08-06 — C.14: CSV export as a worker-side streamed operation

`CsvExport.write` enumerates its row source exactly once, writing one RFC4180-escaped CSV row per pull — never limited to whatever the UI's bounded 500-row preview (A.6/C.9) already fetched, and never requiring the full result to be materialized in memory first. Proved against both a plain in-memory sequence and a real `SqliteConnector` query result (reader-backed, streamed straight through, C.5) exceeding the preview-row limit, confirming the exported file always matches the full source data rather than just what the UI happened to render.

## 2026-08-06 — C.15: copy-to-clipboard for projected results

`ResultViewModel.ClipboardText` projects the current result as tab-separated text (header row first for `Table`, so it pastes cleanly into a spreadsheet) or just `DisplayText` otherwise. `ShellViewModel.CopyResultAsync` sends it through `CopyResultToClipboard`, the same optional-hook no-op-when-unconfigured pattern as `ExecuteAsync`/`CheckAsync`, keeping the view model constructible without a real Avalonia clipboard in tests. `App.axaml.fs` wires it to the window's real `IClipboard`; a "Copy Result" button drives it from the UI.

## 2026-08-06 — C.16: `packages.json` manifest and `#r "nuget: ..."` approval workflow

Added `Packages.readPackages`/`writePackages`/`approvePackage`, mirroring `Workspace`'s shape (C.3) for Section 12's approved-package manifest (name, version, source, resolved assets). `PackageApproval` is the policy layer on top, kept deliberately decoupled from FSI/evaluation entirely: `extractNugetReferences` reads `#r "nuget: ..."` directives straight out of script text and `findUnapproved` diffs them against the manifest, so an approval decision never risks triggering an actual package restore just to check it. `manifestFingerprint` hashes the approved set — any cached session keyed by an earlier fingerprint is stale the moment it changes, satisfying the "requires a session reset" requirement without needing Milestone E's real persistent-session feature to exist yet (every evaluate already gets a fresh session per Section 9, so the fingerprint check itself is what's being proven here).

Verified end-to-end against a real workspace directory: an unlisted `FSharp.Data` reference is rejected, approving it through `Packages.approvePackage` clears the rejection, and the fingerprint changes across that approval.

## 2026-08-06 — C.17: connector call logging — kind, elapsed time, row count, never parameters

Added `IQueryableConnector`, unifying the three SQL-capable connectors (C.5–C.7) behind one interface — each already had its own public `ExecuteQuery`; this just lets `Connections.sql` (C.11), and now logging, reach any of them (or a test fake) without matching on concrete connector types. `Connections.sql` wraps the returned `Rows` in a lazy pass-through that tallies the count and logs kind/elapsed/rowCount only once the caller actually finishes enumerating them, never eagerly — eager counting would force full materialization of what may be a reader-backed sequence, defeating C.5/C.13's reader-based paging entirely just to log a count. Parameter values never touch a log property, verified against an in-memory Serilog sink with a fake connector and a fake "secret-shaped" parameter value that never appears in any captured event.

## 2026-08-06 — C.18: Milestone C acceptance test

One test per connector kind — SQLite, CSV, Excel, and REST self-contained; SQL Server and PostgreSQL reusing their existing container fixtures from C.6/C.7 — each configuring a real connector instance, querying it through its `Connections.*` helper (C.11, the only sanctioned script-facing API), binding the result into a `ResultViewModel` the way `MainWindow`'s `DataGrid` would (C.12), and exporting the full row set via `CsvExport` (C.14). This is the first test in the codebase to drive all six connectors through the identical script-facing path rather than each connector's own lower-level API — closing the loop between C.2's abstraction and C.5–C.10's concrete implementations under one assertion.

Full solution: 248 tests, all green across every project including the Docker-backed SQL Server/PostgreSQL container tests (`Analysis.Tests` 17, `Connectors.Tests` 39, `Editor.Tests` 17, `Execution.Tests` 10, `Integration.Tests` 38, `Logging.Tests` 7, `Protocol.Tests` 9, `Results.Tests` 20, `Security.Tests` 5, `Storage.Tests` 12, `Ui.Tests` 57, `Worker.Tests` 17).

**Milestone C acceptance criterion met:** each of the six supported data targets (SQLite, SQL Server, PostgreSQL, CSV, Excel, REST/JSON) can be configured, queried through its `Connections.*` F# helper with parameters always bound and never concatenated, and displayed/exported through the bounded, paged, virtualized pipeline built across C.1–C.17.

## 2026-08-06 — D.1: `trust.json` read/write

Added `ScriptOrigin`/`TrustState`/`TrustMetadata` to `Protocol` and `Trust.readTrust`/`writeTrust` to `Storage`, mirroring `Workspace`/`Packages`' shape (C.3/C.16) for Section 13's v1 schema: origin, trust state, requested capabilities, optional reviewed-at/reviewed-by. `readTrust` defaults to `Local`/`Unknown` with an empty capability list when `trust.json` doesn't exist yet, rather than throwing — a brand-new workspace has no trust decision made on it yet, same reasoning as `Workspace.readConnections`' empty-list default. This file is read/written/displayed only for v1, per the spec; no enforcement logic exists yet and none is planned before a later milestone.

## 2026-08-06 — D.2: origin detection logic

Added `WorkspaceOpenSource` (`CreatedFresh` / `ExternalPath` / `MarkedShared`) and `OriginDetection.detectOrigin` to `Security`, mapping how a workspace was opened to Section 13's `ScriptOrigin`. The spec names four origin values (local/imported/downloaded/shared-workspace) but only describes three opening scenarios explicitly (fresh, external path, marked shared) — `Downloaded` isn't a fourth input case but a refinement of `ExternalPath`: a path under the OS downloads folder maps to `Downloaded`, everything else under `ExternalPath` maps to `Imported`. Took `downloadsDir` as an explicit parameter rather than resolving it internally via `Environment.GetFolderPath`, so the detection logic itself stays a pure, easily-tested function — the real downloads path is a concern for whatever composition-root code calls this, not this module. `Security` now depends on `Protocol` for `ScriptOrigin`, its first cross-project reference.

## 2026-08-06 — D.3: capability approval UX

Added `ShellViewModel.Trust`/`RequestCapabilityApproval` and a private `TryApproveRun`, gating `RunAsync` on Section 13's trust state before `ExecuteAsync` is ever invoked. `Trusted` always proceeds, `Blocked` never does, and everything else only prompts when `RequestedCapabilities` is non-empty — a script that requests nothing has nothing to approve, so it runs without interrupting the user even while `TrustState` is still `Unknown`. Deliberately inverted the "no-op-when-unconfigured" pattern every other hook on `ShellViewModel` follows (`ExecuteAsync`, `CopyResultToClipboard`, ...): those default to doing nothing when the composition root hasn't wired them up, but `RequestCapabilityApproval` defaults to *denying* — a script requesting capabilities with no way to ask the user must never silently run just because the prompt UI isn't wired yet. Approving flips `Trust.TrustState` to `Trusted` in-memory so the same script doesn't re-prompt on a second `RunAsync` within the session; persisting that approval back to `trust.json` is Storage's job (D.1), not this view model's — `Trust` here is only ever the composition root's already-loaded snapshot.

## 2026-08-06 — D.4: diagnostic-bundle export

Added `DiagnosticBundle.createBundle`/`scanForLeakedSecrets` to `Logging`. `createBundle` zips every `*.log` file under a logs directory last written within a caller-supplied retention window, keyed off a caller-supplied `now` rather than the real clock so bundling stays deterministic in tests. `scanForLeakedSecrets` is the defense-in-depth half the task asked for: a new `Redaction.containsLeakedSecret` regex flags a secret-shaped key (`password`, `token`, `authorization`, ...) assigned to anything *other than* `RedactedPlaceholder` — excluding the placeholder itself is what keeps scanning already-redacted output from flagging its own redaction markers as a leak, while still catching a genuine unredacted value if `RedactingEnricher` ever missed one.

Wrote the scan test two ways on purpose: one seeding a raw unredacted `password=hunter2` fixture (proves the scanner isn't vacuously green), and one running an actual `password` property through a real `RedactingEnricher`-equipped logger before scanning the resulting file (proves A0.6's redaction still holds all the way through Milestone C's code paths). The second version originally called `LoggerSetup.build "host" ...`, which writes to the real process-global `Console.Out` — that raced with `LoggerSetupTests`' own `Console.SetOut`/`SetError` swap once xUnit ran the two test classes in parallel (they're separate default collections), intermittently leaking a stray log line into the other test's captured output. Fixed by building a minimal file-only `LoggerConfiguration` for that test instead of going through `LoggerSetup.build` — no console sink, no shared global state, no race. Worth remembering for any future test that touches `Console.SetOut`/`SetIn`/`SetError`: xUnit parallelizes across test classes by default, so global console redirection anywhere in the suite needs either a dedicated collection or, better, avoiding real Console I/O entirely.

## 2026-08-06 — D.5: dependency reproducibility (locked package hashes)

Added `Hash: string` to `ApprovedPackage` and `PackageLock.computeHash`/`restore` to `Storage`. `computeHash` is a deterministic SHA-256 digest over a package's name, version, source, and sorted resolved assets — a stand-in for a real NuGet content hash, since (per C.16's devlog entry) this app never performs an actual package restore; `restore` recomputes every manifest entry's hash and persists the locked list back to `packages.json`, so calling it twice on an unchanged manifest reproduces byte-identical output.

Adding a mandatory field to an existing record type meant fixing every call site that builds an `ApprovedPackage` literal without one — F# record construction requires every field, so this was a compile-time-enforced list, not a search-and-hope: `PackagesTests.samplePackage`, `Protocol.Tests`' round-trip test, and both `Integration.Tests/PackageApprovalTests.fs` cases. Chose non-optional over `Hash: string option` deliberately: an approved package without a computed hash isn't fully resolved yet, and an `option` would let that half-state silently propagate through `Storage`/`Connectors` instead of being caught at construction.
