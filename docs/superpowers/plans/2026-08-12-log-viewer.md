# Log Viewer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user see what the app is actually doing, from inside the app, instead of having to know `%LOCALAPPDATA%/FSharpQueryStudio/logs/` exists and open raw files in a text editor. Adds a "Logs…" window (new `_Help` menu on `MainWindow`) showing structured, filterable rows (Time/Level/Process/Message) for the Host and Worker's Serilog output — live-tailing today's files, or browsing any past day within the existing 14-day retention window.

**Architecture:** `FSharpQueryStudio.Logging`'s `LoggerSetup.build` already writes daily-rolling `host-YYYYMMDD.log`/`worker-YYYYMMDD.log` files with redaction already applied before any sink sees an event — this plan doesn't touch redaction (`Redaction.fs`) at all, only what happens *after* it. The file sinks switch from Serilog's default plain-text template to its built-in `JsonFormatter` (already part of Serilog core, no new package) so a new `LogFileReader` module can parse each line reliably instead of regex-scraping human-readable text. A `LogViewerViewModel` (`Ui`) merges/sorts/filters parsed entries and exposes plain-string filter properties (never a raw F# `option` bound to XAML — see Global Constraints), polled by a real `DispatcherTimer` in a new `LogViewerWindow`'s code-behind. Full design rationale: `docs/superpowers/specs/2026-08-12-log-viewer-design.md`.

**Tech Stack:** F#, Serilog 4.4.0 (already referenced), Avalonia 12.1.1, xUnit, existing `FSharpQueryStudio.{Logging,Ui}` projects.

## Global Constraints

- TDD throughout: a failing test before any production code, per this repo's established convention (every prior milestone task in `docs/DEVLOG.md` follows this). `LogViewerWindow.axaml`/`.axaml.fs` is the one exception — XAML/code-behind isn't unit-testable in this codebase (no headless rendering harness available), verified instead by a clean `dotnet build` plus the `LogViewerViewModel` tests covering every real behavior the window's code-behind delegates to it.
- **Never bind a raw F# `option` type directly to an Avalonia XAML binding.** This codebase has hit this exact bug twice already this session (`ResultViewModel.DisplayText`, `WorkspaceConnectionViewModel.ErrorMessage`/`HasError`): Avalonia's reflection-based binding falls back to calling `.ToString()` on the boxed `FSharpOption<'T>` itself, rendering literal text like `"Some(x)"`/`"None"`, or — for a two-way `ComboBox.SelectedItem` binding specifically, which is what Task 3 uses for the Level/Process filters — simply fails to round-trip a plain `string` selection into an `option`-typed property at all, since there's no implicit conversion. `LogViewerViewModel.LevelFilter`/`ProcessFilter` are therefore plain `string`, using the sentinel value `"All"` for "no filter", never `string option`, from the very first version written in Task 3 — don't introduce the option-typed version and unwrap it later.
- `JsonFormatter` is `Serilog.Formatting.Json.JsonFormatter` — already part of the `Serilog` package (4.4.0, already referenced by `FSharpQueryStudio.Logging`). Do not add `Serilog.Formatting.Compact` or any other new package for this.
- Log file naming is fixed by `LoggerSetup.build`'s existing `filePrefix` convention: `{filePrefix}-{yyyyMMdd}.log`. The real app uses `filePrefix = "host"` / `"worker"` (see `App.axaml.fs`/`Worker`'s composition roots, unchanged by this plan) — `LogFileReader`/`LogViewerViewModel` construct paths against exactly `host-{date}.log`/`worker-{date}.log`, never a different convention.
- `dotnet build` clean (0 warnings/errors) and `dotnet test` green (excluding the pre-existing `RequiresDocker` `Integration.Tests` failures, unrelated to this work) after every task.
- Update `docs/DEVLOG.md` with an entry once the feature is end-to-end working (Task 5), per this repo's established convention (see any existing entry for the expected shape: what changed, why, what was verified, files touched) — required by project convention, not optional polish.

---

### Task 1: `LogFileReader.parseLine` + switch the file sinks to JSON

**Files:**
- Create: `src/FSharpQueryStudio.Logging/LogFileReader.fs`
- Modify: `src/FSharpQueryStudio.Logging/LoggerSetup.fs` (switch the `WriteTo.File(...)` call to use `JsonFormatter`)
- Modify: `src/FSharpQueryStudio.Logging/FSharpQueryStudio.Logging.fsproj` (add `LogFileReader.fs` to `<Compile>`, right after `LoggerSetup.fs`)
- Test: Create `tests/FSharpQueryStudio.Logging.Tests/LogFileReaderTests.fs`; add it to `tests/FSharpQueryStudio.Logging.Tests/FSharpQueryStudio.Logging.Tests.fsproj`'s `<Compile>` list, right after `LoggerSetupTests.fs`

**Interfaces:**
- Consumes: `FSharpQueryStudio.Logging.LoggerSetup.build` (existing, modified in this task), `FSharpQueryStudio.Logging.Redaction.RedactedPlaceholder` (existing, unmodified).
- Produces: `FSharpQueryStudio.Logging.LogEntry` (`{ Timestamp: DateTimeOffset; Level: string; ProcessRole: string; Message: string }`) and `FSharpQueryStudio.Logging.LogFileReader.parseLine: string -> LogEntry option`. Task 2 adds more functions to the same module; Task 3 (`Ui`) consumes `LogEntry` and (indirectly, via Task 2) the whole `LogFileReader` module.

- [ ] **Step 1: Write the failing tests**

```fsharp
module LogFileReaderTests

open System
open System.IO
open Xunit
open FSharpQueryStudio.Logging

let private withTempLogsDir (f: string -> unit) =
    let tempDir = Path.Combine(Path.GetTempPath(), "fqs-logfilereader-tests-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tempDir) |> ignore

    try
        f tempDir
    finally
        Directory.Delete(tempDir, true)

let private disposeLogger (logger: Serilog.ILogger) =
    match logger with
    | :? IDisposable as d -> d.Dispose()
    | _ -> ()

[<Fact>]
let ``parseLine reads a real event logged through LoggerSetup.build`` () =
    withTempLogsDir (fun tempDir ->
        let logger = LoggerSetup.build "host" "host-test" tempDir
        logger.Information("hello from the log viewer test")
        disposeLogger logger

        let logFile = Directory.GetFiles(tempDir, "host-test-*.log") |> Array.exactlyOne
        let line = File.ReadAllLines(logFile) |> Array.head
        let entry = LogFileReader.parseLine line

        Assert.True(entry.IsSome)
        Assert.Equal("Information", entry.Value.Level)
        Assert.Equal("host", entry.Value.ProcessRole)
        Assert.Equal("hello from the log viewer test", entry.Value.Message))

[<Fact>]
let ``parseLine redacts a password-shaped property the same as any other sink`` () =
    withTempLogsDir (fun tempDir ->
        let logger = LoggerSetup.build "host" "host-test" tempDir
        logger.Information("connecting with {password}", "hunter2")
        disposeLogger logger

        let logFile = Directory.GetFiles(tempDir, "host-test-*.log") |> Array.exactlyOne
        let line = File.ReadAllLines(logFile) |> Array.head
        let entry = LogFileReader.parseLine line

        Assert.Contains(Redaction.RedactedPlaceholder, entry.Value.Message)
        Assert.DoesNotContain("hunter2", entry.Value.Message))

[<Fact>]
let ``parseLine returns None for a truncated JSON fragment`` () =
    let result =
        LogFileReader.parseLine
            """{"Timestamp":"2026-08-12T12:00:00+00:00","Level":"Informat"""

    Assert.True(result.IsNone)

[<Fact>]
let ``parseLine returns None for an empty or whitespace-only line`` () =
    Assert.True((LogFileReader.parseLine "").IsNone)
    Assert.True((LogFileReader.parseLine "   ").IsNone)
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FSharpQueryStudio.Logging.Tests --filter "FullyQualifiedName~LogFileReaderTests"`
Expected: compile error — `LogFileReader` module doesn't exist yet.

- [ ] **Step 3: Switch the file sink to JSON in `LoggerSetup.fs`**

In `src/FSharpQueryStudio.Logging/LoggerSetup.fs`, add `open Serilog.Formatting.Json` near the top (alongside the existing `open Serilog`/`open Serilog.Events`), then change the `WriteTo.File(...)` call from:

```fsharp
                .WriteTo.File(
                    Path.Combine(logsDirectory, $"{filePrefix}-.log"),
                    restrictedToMinimumLevel = LogEventLevel.Information,
                    rollingInterval = RollingInterval.Day,
                    retainedFileCountLimit = Nullable(14)
                )
```

to:

```fsharp
                .WriteTo.File(
                    JsonFormatter(renderMessage = true),
                    Path.Combine(logsDirectory, $"{filePrefix}-.log"),
                    restrictedToMinimumLevel = LogEventLevel.Information,
                    rollingInterval = RollingInterval.Day,
                    retainedFileCountLimit = Nullable(14),
                    shared = true
                )
```

`renderMessage = true` is what makes each JSON line carry a ready-to-display `RenderedMessage` string (property values substituted in), not just the raw `{Property}`-templated `MessageTemplate`. `shared = true` allows another process (the Host, reading the Worker's log file for the live viewer) to open the file for reading while the writing process still holds it open — Serilog's file sink defaults to an exclusive lock otherwise.

- [ ] **Step 4: Write `LogFileReader.fs`**

```fsharp
namespace FSharpQueryStudio.Logging

open System
open System.Text.Json

/// One parsed row from a `LoggerSetup.build`-produced JSON log file — the
/// in-app log viewer's unit of display. `ProcessRole` comes from the
/// `ProcessRole` property `LoggerSetup.build` enriches onto every event
/// ("host" or "worker"), not from which file the line was read out of, so
/// it stays correct even after a caller merges multiple files together.
type LogEntry =
    { Timestamp: DateTimeOffset
      Level: string
      ProcessRole: string
      Message: string }

/// Reads and parses the JSON log files `LoggerSetup.build` writes — see
/// `docs/superpowers/specs/2026-08-12-log-viewer-design.md`.
module LogFileReader =

    /// Parses one `JsonFormatter(renderMessage = true)`-produced line.
    /// `None` for anything that isn't a complete, well-formed event —
    /// deliberately swallows the parse exception rather than letting it
    /// escape, since a line caught mid-write (the live-tail poller reads a
    /// partial JSON fragment written so far) is an expected, routine case,
    /// not a real error; the next poll re-reads the same line once whole.
    let parseLine (json: string) : LogEntry option =
        if String.IsNullOrWhiteSpace json then
            None
        else
            try
                use doc = JsonDocument.Parse(json)
                let root = doc.RootElement

                let processRole =
                    match root.TryGetProperty("Properties") with
                    | true, properties ->
                        match properties.TryGetProperty("ProcessRole") with
                        | true, value -> value.GetString()
                        | false, _ -> ""
                    | false, _ -> ""

                Some
                    { Timestamp = root.GetProperty("Timestamp").GetDateTimeOffset()
                      Level = root.GetProperty("Level").GetString()
                      ProcessRole = processRole
                      Message = root.GetProperty("RenderedMessage").GetString() }
            with _ ->
                None
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/FSharpQueryStudio.Logging.Tests --filter "FullyQualifiedName~LogFileReaderTests"`
Expected: PASS (4/4). Also re-run `dotnet test tests/FSharpQueryStudio.Logging.Tests` in full — `LoggerSetupTests`/`DiagnosticBundleTests` assert on file existence/console routing and hand-written fixture content respectively, neither depends on the file sink's text format, so both stay green unmodified.

- [ ] **Step 6: Commit**

```bash
git add src/FSharpQueryStudio.Logging/LogFileReader.fs src/FSharpQueryStudio.Logging/LoggerSetup.fs src/FSharpQueryStudio.Logging/FSharpQueryStudio.Logging.fsproj tests/FSharpQueryStudio.Logging.Tests/LogFileReaderTests.fs tests/FSharpQueryStudio.Logging.Tests/FSharpQueryStudio.Logging.Tests.fsproj
git commit -m "Feature: LogEntry/parseLine, switch log file sinks to JSON"
```

---

### Task 2: `LogFileReader` — `listAvailableDates`, `readEntriesSince`, `readAllEntries`

**Files:**
- Modify: `src/FSharpQueryStudio.Logging/LogFileReader.fs` (append to the same module)
- Test: Modify `tests/FSharpQueryStudio.Logging.Tests/LogFileReaderTests.fs` (append)

**Interfaces:**
- Consumes: `LogFileReader.parseLine` (Task 1), `System.IO`.
- Produces: `LogFileReader.listAvailableDates: string -> DateOnly list`, `LogFileReader.readEntriesSince: string -> int64 -> LogEntry list * int64`, `LogFileReader.readAllEntries: string -> LogEntry list`. Task 3 (`Ui`) consumes all three directly.

- [ ] **Step 1: Write the failing tests**

The hand-typed JSON fixture in `readEntriesSince excludes a trailing line with no newline yet...` below must use the exact same field shape Task 1's `parseLine` actually settled on — if Task 1's real-`LoggerSetup.build`-driven test led to any adjustment of the assumed `Timestamp`/`Level`/`RenderedMessage`/`Properties.ProcessRole` field names, mirror that adjustment here too, otherwise this fixture parses against a shape `parseLine` no longer expects.

Append to `tests/FSharpQueryStudio.Logging.Tests/LogFileReaderTests.fs`:

```fsharp
[<Fact>]
let ``listAvailableDates finds every host/worker log file's date, deduplicated and newest first`` () =
    withTempLogsDir (fun tempDir ->
        File.WriteAllText(Path.Combine(tempDir, "host-20260812.log"), "")
        File.WriteAllText(Path.Combine(tempDir, "worker-20260812.log"), "")
        File.WriteAllText(Path.Combine(tempDir, "host-20260810.log"), "")
        File.WriteAllText(Path.Combine(tempDir, "not-a-log-file.txt"), "")

        let dates = LogFileReader.listAvailableDates tempDir

        Assert.Equal<DateOnly list>([ DateOnly(2026, 8, 12); DateOnly(2026, 8, 10) ], dates))

[<Fact>]
let ``listAvailableDates returns empty for a directory that does not exist`` () =
    let missing = Path.Combine(Path.GetTempPath(), "fqs-nonexistent-" + Guid.NewGuid().ToString("N"))
    Assert.Empty(LogFileReader.listAvailableDates missing)

[<Fact>]
let ``readEntriesSince returns only entries past the given offset, and the returned offset advances past them`` () =
    withTempLogsDir (fun tempDir ->
        let logger1 = LoggerSetup.build "host" "host-test" tempDir
        logger1.Information("first")
        disposeLogger logger1

        let logFile = Directory.GetFiles(tempDir, "host-test-*.log") |> Array.exactlyOne
        let firstEntries, offsetAfterFirst = LogFileReader.readEntriesSince logFile 0L

        Assert.Equal<string list>([ "first" ], firstEntries |> List.map (fun e -> e.Message))

        let logger2 = LoggerSetup.build "host" "host-test" tempDir
        logger2.Information("second")
        disposeLogger logger2

        let secondEntries, _offsetAfterSecond = LogFileReader.readEntriesSince logFile offsetAfterFirst

        Assert.Equal<string list>([ "second" ], secondEntries |> List.map (fun e -> e.Message)))

[<Fact>]
let ``readEntriesSince excludes a trailing line with no newline yet, and does not advance the offset past it`` () =
    withTempLogsDir (fun tempDir ->
        let logFile = Path.Combine(tempDir, "partial.log")

        let complete =
            """{"Timestamp":"2026-08-12T12:00:00+00:00","Level":"Information","RenderedMessage":"complete","Properties":{"ProcessRole":"host"}}"""

        File.WriteAllText(logFile, complete + "\n" + """{"Timestamp":"2026-08-12T12:00:01""")

        let entries, offset = LogFileReader.readEntriesSince logFile 0L

        Assert.Equal<string list>([ "complete" ], entries |> List.map (fun e -> e.Message))
        Assert.True(offset < (FileInfo logFile).Length))

[<Fact>]
let ``readEntriesSince returns nothing new when the offset already equals the file's length`` () =
    withTempLogsDir (fun tempDir ->
        let logger = LoggerSetup.build "host" "host-test" tempDir
        logger.Information("only entry")
        disposeLogger logger

        let logFile = Directory.GetFiles(tempDir, "host-test-*.log") |> Array.exactlyOne
        let _entries, offset = LogFileReader.readEntriesSince logFile 0L
        let againEntries, againOffset = LogFileReader.readEntriesSince logFile offset

        Assert.Empty(againEntries)
        Assert.Equal(offset, againOffset))

[<Fact>]
let ``readEntriesSince returns nothing for a file that does not exist`` () =
    let missing = Path.Combine(Path.GetTempPath(), "fqs-nonexistent-" + Guid.NewGuid().ToString("N") + ".log")
    let entries, offset = LogFileReader.readEntriesSince missing 0L

    Assert.Empty(entries)
    Assert.Equal(0L, offset)

[<Fact>]
let ``readAllEntries reads every entry in a file from the start`` () =
    withTempLogsDir (fun tempDir ->
        let logger = LoggerSetup.build "host" "host-test" tempDir
        logger.Information("one")
        logger.Information("two")
        disposeLogger logger

        let logFile = Directory.GetFiles(tempDir, "host-test-*.log") |> Array.exactlyOne
        let entries = LogFileReader.readAllEntries logFile

        Assert.Equal<string list>([ "one"; "two" ], entries |> List.map (fun e -> e.Message)))
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FSharpQueryStudio.Logging.Tests --filter "FullyQualifiedName~LogFileReaderTests"`
Expected: compile error — `listAvailableDates`/`readEntriesSince`/`readAllEntries` don't exist yet.

- [ ] **Step 3: Append the implementation to `LogFileReader.fs`**

```fsharp
    /// Every distinct date with a `host-*.log`/`worker-*.log` file in
    /// `logsDirectory`, newest first — what populates the log viewer's
    /// date picker. Empty, not an error, for a directory that doesn't
    /// exist yet (a freshly-installed app before its first log line).
    let listAvailableDates (logsDirectory: string) : DateOnly list =
        if not (System.IO.Directory.Exists logsDirectory) then
            []
        else
            let pattern =
                System.Text.RegularExpressions.Regex(@"^(?:host|worker)-(\d{8})\.log$")

            System.IO.Directory.GetFiles(logsDirectory)
            |> Array.choose (fun path ->
                let name = System.IO.Path.GetFileName path
                let m = pattern.Match name

                if m.Success then
                    match DateOnly.TryParseExact(m.Groups.[1].Value, "yyyyMMdd") with
                    | true, date -> Some date
                    | false, _ -> None
                else
                    None)
            |> Array.distinct
            |> Array.sortDescending
            |> Array.toList

    /// Reads only the bytes appended to `filePath` since `byteOffset`,
    /// parsing every complete line into a `LogEntry` (skipping any that
    /// fail to parse — `parseLine`'s own routine-partial-write case) and
    /// returning the new offset alongside them. A trailing line with no
    /// terminating `\n` yet (the poller caught a write in progress) is
    /// left unconsumed — the returned offset stops before it, so the next
    /// call picks it up once it's whole. `[], byteOffset` (offset
    /// unchanged) for a file that doesn't exist or has nothing new since
    /// `byteOffset` — never an error, since both are routine for a
    /// live-tail poller (the file may not exist yet at the very start of
    /// a session; most polls find nothing new).
    let readEntriesSince (filePath: string) (byteOffset: int64) : LogEntry list * int64 =
        if not (System.IO.File.Exists filePath) then
            [], byteOffset
        else
            use stream =
                new System.IO.FileStream(
                    filePath,
                    System.IO.FileMode.Open,
                    System.IO.FileAccess.Read,
                    System.IO.FileShare.ReadWrite
                )

            if byteOffset >= stream.Length then
                [], byteOffset
            else
                stream.Seek(byteOffset, System.IO.SeekOrigin.Begin) |> ignore
                let buffer = Array.zeroCreate<byte> (int (stream.Length - byteOffset))
                stream.ReadExactly(buffer, 0, buffer.Length)

                match buffer |> Array.tryFindIndexBack (fun b -> b = byte '\n') with
                | None -> [], byteOffset
                | Some lastNewlineIndex ->
                    let complete = System.Text.Encoding.UTF8.GetString(buffer, 0, lastNewlineIndex + 1)

                    let entries =
                        complete.Split('\n')
                        |> Array.choose (fun line -> parseLine (line.TrimEnd('\r')))
                        |> Array.toList

                    entries, byteOffset + int64 (lastNewlineIndex + 1)

    /// Every entry in `filePath`, from the start — a past date's static
    /// view (never live-polled again once selected).
    let readAllEntries (filePath: string) : LogEntry list =
        readEntriesSince filePath 0L |> fst
```

Add `open System` and `open System.IO` are not required beyond what's already present — this step uses fully-qualified `System.IO.*`/`System.Text.RegularExpressions.*` names throughout deliberately, so no new `open` lines are needed at the top of the file (avoids a name clash between `System.IO.File`/`System.IO.Directory` and anything else already open in this module).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FSharpQueryStudio.Logging.Tests --filter "FullyQualifiedName~LogFileReaderTests"`
Expected: PASS (11/11 — the 4 from Task 1 plus 7 new).

- [ ] **Step 5: Commit**

```bash
git add src/FSharpQueryStudio.Logging/LogFileReader.fs tests/FSharpQueryStudio.Logging.Tests/LogFileReaderTests.fs
git commit -m "Feature: LogFileReader.listAvailableDates/readEntriesSince/readAllEntries"
```

---

### Task 3: `LogViewerViewModel` — merge, sort, live/static switching, filters

**Files:**
- Create: `src/FSharpQueryStudio.Ui/LogViewerViewModel.fs`
- Modify: `src/FSharpQueryStudio.Ui/FSharpQueryStudio.Ui.fsproj` — add a `ProjectReference` to `..\FSharpQueryStudio.Logging\FSharpQueryStudio.Logging.fsproj` (alongside the existing `FSharpQueryStudio.Editor` reference); add `<Compile Include="LogViewerViewModel.fs" />` right before `<Compile Include="ShellViewModel.fs" />`
- Test: Create `tests/FSharpQueryStudio.Ui.Tests/LogViewerViewModelTests.fs`; add it to `tests/FSharpQueryStudio.Ui.Tests/FSharpQueryStudio.Ui.Tests.fsproj`'s `<Compile>` list, right after `WorkspaceConnectionViewModelTests.fs` (no explicit `ProjectReference` to `Logging` needed in the test project — `LogEntry`'s already reachable transitively through the existing reference to `Ui`, the same way `ShellViewModelTests.fs` already reaches `FSharpQueryStudio.Protocol` types today)

**Interfaces:**
- Consumes: `FSharpQueryStudio.Logging.LogEntry` (Task 1), `FSharpQueryStudio.Logging.LogFileReader.readEntriesSince`'s exact signature (Task 2) — this task's `readEntriesSince` constructor parameter must stay `string -> int64 -> LogEntry list * int64` so `LogFileReader.readEntriesSince` can be passed directly, uncurried-wrapper-free, in Task 4.
- Produces: `FSharpQueryStudio.Ui.LogViewerViewModel(logsDirectory: string, readEntriesSince: string -> int64 -> LogEntry list * int64)`, with members `AvailableDates: DateOnly list`, `IsLive: bool`, `Entries: LogEntry list`, `HasEntries: bool`, `HasNoEntries: bool`, `LevelFilter: string with get, set` (sentinel `"All"` for no filter — see Global Constraints), `LevelOptions: string list`, `ProcessFilter: string with get, set` (same sentinel), `ProcessOptions: string list`, `SelectedDate: DateOnly with get, set`, `Poll(): unit`. Task 4 (`LogViewerWindow`) constructs one directly and drives `Poll()` with a real timer; its XAML binds every member above except the constructor.

- [ ] **Step 1: Write the failing tests**

```fsharp
module LogViewerViewModelTests

open System
open System.ComponentModel
open Xunit
open FSharpQueryStudio.Logging
open FSharpQueryStudio.Ui

let private entry (offsetMinutes: int) (level: string) (role: string) (message: string) : LogEntry =
    { Timestamp = DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero).AddMinutes(float offsetMinutes)
      Level = level
      ProcessRole = role
      Message = message }

/// A fake `readEntriesSince` keyed by file path: returns the mapped entry
/// list (once, advancing the offset to `1L`) the first time a path is
/// asked for at offset `0L`, and nothing for any other call — enough to
/// drive every `LogViewerViewModel` behavior under test without touching
/// real files or a real clock.
let private fakeReader (byPath: Map<string, LogEntry list>) : string -> int64 -> LogEntry list * int64 =
    fun path offset ->
        if offset = 0L then
            match byPath.TryFind path with
            | Some entries -> entries, 1L
            | None -> [], 0L
        else
            [], offset

let private today = DateOnly.FromDateTime(DateTime.Today)
let private hostPathFor (date: DateOnly) = IO.Path.Combine("logs", $"host-{date:yyyyMMdd}.log")
let private workerPathFor (date: DateOnly) = IO.Path.Combine("logs", $"worker-{date:yyyyMMdd}.log")

[<Fact>]
let ``a freshly-constructed LogViewerViewModel defaults to today, live, with no entries`` () =
    let vm = LogViewerViewModel("logs", fun _ _ -> [], 0L)

    Assert.Equal(today, vm.SelectedDate)
    Assert.True(vm.IsLive)
    Assert.Empty(vm.Entries)

[<Fact>]
let ``Poll merges host and worker entries and sorts them by Timestamp`` () =
    let reader =
        fakeReader (
            Map [ hostPathFor today, [ entry 2 "Information" "host" "later host" ]
                  workerPathFor today, [ entry 1 "Information" "worker" "earlier worker" ] ]
        )

    let vm = LogViewerViewModel("logs", reader)
    vm.Poll()

    Assert.Equal<string list>([ "earlier worker"; "later host" ], vm.Entries |> List.map (fun e -> e.Message))

[<Fact>]
let ``Poll is a no-op once IsLive is false`` () =
    let mutable callCount = 0

    let reader =
        fun (_path: string) (_offset: int64) ->
            callCount <- callCount + 1
            [], 0L

    let vm = LogViewerViewModel("logs", reader)
    vm.SelectedDate <- today.AddDays(-1.0)
    callCount <- 0 // Reset past the switch's own static read, which does call the reader.

    vm.Poll()

    Assert.Equal(0, callCount)

[<Fact>]
let ``LevelFilter narrows Entries without a fresh read, using All as the no-filter sentinel`` () =
    let reader =
        fakeReader (
            Map [ hostPathFor today,
                  [ entry 1 "Information" "host" "info line"
                    entry 2 "Error" "host" "error line" ] ]
        )

    let vm = LogViewerViewModel("logs", reader)
    vm.Poll()

    Assert.Equal("All", vm.LevelFilter)

    vm.LevelFilter <- "Error"

    Assert.Equal<string list>([ "error line" ], vm.Entries |> List.map (fun e -> e.Message))

[<Fact>]
let ``ProcessFilter narrows Entries to just the selected process`` () =
    let reader =
        fakeReader (
            Map [ hostPathFor today, [ entry 1 "Information" "host" "host line" ]
                  workerPathFor today, [ entry 2 "Information" "worker" "worker line" ] ]
        )

    let vm = LogViewerViewModel("logs", reader)
    vm.Poll()

    vm.ProcessFilter <- "worker"

    Assert.Equal<string list>([ "worker line" ], vm.Entries |> List.map (fun e -> e.Message))

[<Fact>]
let ``SelectedDate set to a past date reads that date's files once, statically, and sets IsLive false`` () =
    let pastDate = today.AddDays(-1.0)
    let reader = fakeReader (Map [ hostPathFor pastDate, [ entry 0 "Information" "host" "yesterday's entry" ] ])

    let vm = LogViewerViewModel("logs", reader)
    vm.SelectedDate <- pastDate

    Assert.False(vm.IsLive)
    Assert.Equal<string list>([ "yesterday's entry" ], vm.Entries |> List.map (fun e -> e.Message))

[<Fact>]
let ``SelectedDate set back to today resumes live polling`` () =
    let vm = LogViewerViewModel("logs", fun _ _ -> [], 0L)
    vm.SelectedDate <- today.AddDays(-1.0)
    vm.SelectedDate <- today

    Assert.True(vm.IsLive)

[<Fact>]
let ``PropertyChanged fires for Entries when Poll finds new entries`` () =
    let reader = fakeReader (Map [ hostPathFor today, [ entry 0 "Information" "host" "one" ] ])
    let vm = LogViewerViewModel("logs", reader)
    let raisedFor = ResizeArray<string>()
    (vm :> INotifyPropertyChanged).PropertyChanged.Add(fun args -> raisedFor.Add(args.PropertyName))

    vm.Poll()

    Assert.Contains("Entries", raisedFor)

[<Fact>]
let ``LevelOptions and ProcessOptions both start with the All sentinel`` () =
    let vm = LogViewerViewModel("logs", fun _ _ -> [], 0L)

    Assert.Equal("All", List.head vm.LevelOptions)
    Assert.Equal("All", List.head vm.ProcessOptions)

[<Fact>]
let ``HasEntries and HasNoEntries track Entries, including after a filter empties it`` () =
    let reader = fakeReader (Map [ hostPathFor today, [ entry 0 "Information" "host" "one" ] ])
    let vm = LogViewerViewModel("logs", reader)

    Assert.False(vm.HasEntries)
    Assert.True(vm.HasNoEntries)

    vm.Poll()

    Assert.True(vm.HasEntries)
    Assert.False(vm.HasNoEntries)

    vm.LevelFilter <- "Error" // No entries at this level - filters everything out.

    Assert.False(vm.HasEntries)
    Assert.True(vm.HasNoEntries)
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FSharpQueryStudio.Ui.Tests --filter "FullyQualifiedName~LogViewerViewModelTests"`
Expected: compile error — `LogViewerViewModel` doesn't exist yet.

- [ ] **Step 3: Write `LogViewerViewModel.fs`**

```fsharp
namespace FSharpQueryStudio.Ui

open System
open System.ComponentModel
open FSharpQueryStudio.Logging

/// Backs the in-app log viewer window (`LogViewerWindow`) — see
/// `docs/superpowers/specs/2026-08-12-log-viewer-design.md`. `readEntriesSince`
/// is the same composition-root-hook shape used everywhere else in this
/// codebase (`ShellViewModel.ExecuteAsync`, etc.), except a required
/// constructor argument rather than an optional settable property — there
/// is no meaningful "unconfigured" state for a log viewer with no way to
/// read log files. Tests inject a fake in-memory reader; the real window
/// wires `LogFileReader.readEntriesSince` directly (Task 4).
///
/// `LevelFilter`/`ProcessFilter` are plain `string` (sentinel `"All"` for
/// "no filter"), never `string option` — see this plan's Global
/// Constraints for why binding a raw F# option to XAML is a known,
/// already-hit-twice bug in this codebase.
type LogViewerViewModel(logsDirectory: string, readEntriesSince: string -> int64 -> LogEntry list * int64) as self =
    let mutable rawEntries: LogEntry list = []
    let mutable levelFilter = "All"
    let mutable processFilter = "All"
    let mutable selectedDate = DateOnly.FromDateTime(DateTime.Today)
    let mutable isLive = true
    let mutable hostOffset = 0L
    let mutable workerOffset = 0L
    let propertyChanged = Event<PropertyChangedEventHandler, PropertyChangedEventArgs>()

    let raise name =
        propertyChanged.Trigger(self, PropertyChangedEventArgs(name: string))

    let logFilePath (date: DateOnly) (role: string) =
        IO.Path.Combine(logsDirectory, $"{role}-{date:yyyyMMdd}.log")

    interface INotifyPropertyChanged with
        [<CLIEvent>]
        member _.PropertyChanged = propertyChanged.Publish

    member _.AvailableDates = LogFileReader.listAvailableDates logsDirectory
    member _.IsLive = isLive
    member _.LevelOptions = [ "All"; "Information"; "Warning"; "Error" ]
    member _.ProcessOptions = [ "All"; "host"; "worker" ]

    /// The currently-displayed rows: `rawEntries` (already merged and
    /// sorted by `Timestamp` at fetch time — `Poll`/a `SelectedDate`
    /// switch), narrowed by `LevelFilter`/`ProcessFilter` on every access.
    /// Filtering never triggers a fresh read, just a re-derivation over
    /// what's already fetched.
    member _.Entries =
        rawEntries
        |> List.filter (fun e -> levelFilter = "All" || levelFilter = e.Level)
        |> List.filter (fun e -> processFilter = "All" || processFilter = e.ProcessRole)

    /// Derived, XAML-safe booleans for the grid/"No logs yet" placeholder
    /// switch — same reason `WorkspaceConnectionViewModel.HasNoSchema`
    /// exists rather than binding a count directly: explicit booleans
    /// don't depend on Avalonia's int-truthiness binding conversion.
    member _.HasEntries = not (List.isEmpty self.Entries)
    member _.HasNoEntries = List.isEmpty self.Entries

    member _.LevelFilter
        with get () = levelFilter
        and set value =
            levelFilter <- value
            raise "Entries"
            raise "HasEntries"
            raise "HasNoEntries"

    member _.ProcessFilter
        with get () = processFilter
        and set value =
            processFilter <- value
            raise "Entries"
            raise "HasEntries"
            raise "HasNoEntries"

    member _.SelectedDate
        with get () = selectedDate
        and set (date: DateOnly) =
            if date <> selectedDate then
                selectedDate <- date
                isLive <- date = DateOnly.FromDateTime(DateTime.Today)

                if isLive then
                    hostOffset <- 0L
                    workerOffset <- 0L
                    rawEntries <- []
                    self.Poll()
                else
                    // Same never-crash-the-viewer rationale as `Poll`'s
                    // own try/with — a past date's static read is still a
                    // file read that can fail for reasons outside this
                    // view model's control.
                    try
                        let hostEntries, _ = readEntriesSince (logFilePath date "host") 0L
                        let workerEntries, _ = readEntriesSince (logFilePath date "worker") 0L
                        rawEntries <- (hostEntries @ workerEntries) |> List.sortBy (fun e -> e.Timestamp)
                    with _ ->
                        rawEntries <- []

                raise "IsLive"
                raise "Entries"
                raise "HasEntries"
                raise "HasNoEntries"

    /// Reads whatever's new since the last call and merges it into
    /// `Entries` — a no-op while `IsLive` is `false` (viewing a past
    /// date), so the window's real timer can call this unconditionally on
    /// every tick without checking `IsLive` itself. Any read exception
    /// (e.g. a transient sharing violation despite `shared = true`, or
    /// disk pressure) is caught and swallowed for this tick — the log
    /// viewer must never be able to crash the running app it exists to
    /// help debug; the next poll simply tries again.
    member _.Poll() =
        if isLive then
            try
                let newHostEntries, newHostOffset = readEntriesSince (logFilePath selectedDate "host") hostOffset
                let newWorkerEntries, newWorkerOffset = readEntriesSince (logFilePath selectedDate "worker") workerOffset

                if not (List.isEmpty newHostEntries) || not (List.isEmpty newWorkerEntries) then
                    hostOffset <- newHostOffset
                    workerOffset <- newWorkerOffset
                    rawEntries <- (rawEntries @ newHostEntries @ newWorkerEntries) |> List.sortBy (fun e -> e.Timestamp)
                    raise "Entries"
                    raise "HasEntries"
                    raise "HasNoEntries"
            with _ ->
                ()
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FSharpQueryStudio.Ui.Tests --filter "FullyQualifiedName~LogViewerViewModelTests"`
Expected: PASS (10/10).

- [ ] **Step 5: Run the full solution build and test suite**

Run: `dotnet build` then `dotnet test tests/FSharpQueryStudio.Ui.Tests`
Expected: build clean; every existing `Ui.Tests` case still green (the new `ProjectReference` to `Logging` and the new file don't change anything existing consumes).

- [ ] **Step 6: Commit**

```bash
git add src/FSharpQueryStudio.Ui/LogViewerViewModel.fs src/FSharpQueryStudio.Ui/FSharpQueryStudio.Ui.fsproj tests/FSharpQueryStudio.Ui.Tests/LogViewerViewModelTests.fs tests/FSharpQueryStudio.Ui.Tests/FSharpQueryStudio.Ui.Tests.fsproj
git commit -m "Feature: LogViewerViewModel - merge, sort, live/static switching, filters"
```

---

### Task 4: `LogViewerWindow` — DataGrid, filter dropdowns, date picker, live poll timer

**Files:**
- Create: `src/FSharpQueryStudio.Ui/LogViewerWindow.axaml`
- Create: `src/FSharpQueryStudio.Ui/LogViewerWindow.axaml.fs`
- Modify: `src/FSharpQueryStudio.Ui/FSharpQueryStudio.Ui.fsproj` — add `<Compile Include="LogViewerWindow.axaml.fs" />` right before `<Compile Include="MainWindow.axaml.fs" />` (the `.axaml` file itself needs no separate entry — Avalonia's SDK auto-globs `**/*.axaml`, matching every other view in this project)

**Interfaces:**
- Consumes: `FSharpQueryStudio.Ui.LogViewerViewModel` (Task 3), `FSharpQueryStudio.Logging.LoggerSetup.defaultLogsDirectory` (existing), `FSharpQueryStudio.Logging.LogFileReader.readEntriesSince` (Task 2).
- Produces: `FSharpQueryStudio.Ui.LogViewerWindow()` — a parameterless `Window` subtype, same constructor shape `MainWindow` itself already uses. Task 5 constructs one and calls `.Show()`.

This task has no TDD cycle of its own — XAML/code-behind isn't unit-testable in this codebase (no headless rendering harness available), the same standing limitation as `MainWindow.axaml`/`ResultsView.axaml`. Verified by a clean `dotnet build` (the XAML compiler resolves every `{Binding ...}` path against `LogViewerViewModel`'s real members at build time) plus Task 3's `LogViewerViewModelTests` already covering every real behavior this window's code-behind delegates to it.

- [ ] **Step 1: Write `LogViewerWindow.axaml`**

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        x:Class="FSharpQueryStudio.Ui.LogViewerWindow"
        x:CompileBindings="False"
        Title="F# Query Studio — Logs"
        Width="900"
        Height="500">
  <DockPanel Margin="8">
    <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Spacing="8" Margin="0,0,0,8">
      <TextBlock Text="Date" VerticalAlignment="Center" />
      <ComboBox Name="DateCombo" ItemsSource="{Binding AvailableDates}" SelectedItem="{Binding SelectedDate}" Width="120" />
      <TextBlock Text="Level" VerticalAlignment="Center" Margin="16,0,0,0" />
      <ComboBox Name="LevelCombo" ItemsSource="{Binding LevelOptions}" SelectedItem="{Binding LevelFilter}" Width="140" />
      <TextBlock Text="Process" VerticalAlignment="Center" Margin="16,0,0,0" />
      <ComboBox Name="ProcessCombo" ItemsSource="{Binding ProcessOptions}" SelectedItem="{Binding ProcessFilter}" Width="120" />
    </StackPanel>
    <TextBlock Text="No logs yet" Opacity="0.7" Margin="8" IsVisible="{Binding HasNoEntries}" />
    <DataGrid Name="LogsGrid"
              ItemsSource="{Binding Entries}"
              IsVisible="{Binding HasEntries}"
              AutoGenerateColumns="False"
              IsReadOnly="True">
      <DataGrid.Columns>
        <DataGridTextColumn Header="Time" Binding="{Binding Timestamp}" Width="180" />
        <DataGridTextColumn Header="Level" Binding="{Binding Level}" Width="100" />
        <DataGridTextColumn Header="Process" Binding="{Binding ProcessRole}" Width="80" />
        <DataGridTextColumn Header="Message" Binding="{Binding Message}" Width="*" />
      </DataGrid.Columns>
    </DataGrid>
  </DockPanel>
</Window>
```

- [ ] **Step 2: Write `LogViewerWindow.axaml.fs`**

```fsharp
namespace FSharpQueryStudio.Ui

open System
open Avalonia.Controls
open Avalonia.Markup.Xaml
open Avalonia.Threading
open FSharpQueryStudio.Logging

/// Hosts a `LogViewerViewModel`, driving its `Poll()` with a real
/// `DispatcherTimer` while the window is open — see
/// `docs/superpowers/specs/2026-08-12-log-viewer-design.md`. Everything
/// else (merging, filtering, live/static switching) lives in the view
/// model itself, already unit-tested (Task 3); this file's only real
/// logic is starting/stopping the timer.
type LogViewerWindow() as this =
    inherit Window()

    let viewModel = LogViewerViewModel(LoggerSetup.defaultLogsDirectory (), LogFileReader.readEntriesSince)

    do
        this.DataContext <- viewModel
        AvaloniaXamlLoader.Load(this)

        let timer = DispatcherTimer(Interval = TimeSpan.FromSeconds(1.0))
        timer.Tick.Add(fun _ -> viewModel.Poll())
        timer.Start()
        this.Closed.Add(fun _ -> timer.Stop())
```

- [ ] **Step 3: Build to verify the XAML compiles**

Run: `dotnet build src/FSharpQueryStudio.Ui`
Expected: clean build. An `AVLN2000` error here means a binding path or control name doesn't resolve against `LogViewerViewModel`'s real members — fix the XAML, don't add a member just to satisfy a typo.

- [ ] **Step 4: Commit**

```bash
git add src/FSharpQueryStudio.Ui/LogViewerWindow.axaml src/FSharpQueryStudio.Ui/LogViewerWindow.axaml.fs src/FSharpQueryStudio.Ui/FSharpQueryStudio.Ui.fsproj
git commit -m "Feature: LogViewerWindow - DataGrid, filters, date picker, live poll timer"
```

---

### Task 5: Wire "Logs…" into `MainWindow`'s new `_Help` menu; DEVLOG entry

**Files:**
- Modify: `src/FSharpQueryStudio.Ui/MainWindow.axaml` (add a `_Help` menu)
- Modify: `src/FSharpQueryStudio.Ui/MainWindow.axaml.fs` (wire the click handler)
- Modify: `docs/DEVLOG.md` (new entry for the whole feature)

**Interfaces:**
- Consumes: `FSharpQueryStudio.Ui.LogViewerWindow` (Task 4).
- Produces: nothing further downstream — this is the plan's last task.

No TDD cycle — same XAML/code-behind limitation as Task 4, verified by a clean `dotnet build`.

- [ ] **Step 1: Add the `_Help` menu to `MainWindow.axaml`**

In the `<Menu>` block, add a new top-level item after the existing `_Run` menu:

```xml
      <MenuItem Header="_Help">
        <MenuItem Header="_Logs…" Name="LogsMenuItem" />
      </MenuItem>
```

- [ ] **Step 2: Wire the click handler in `MainWindow.axaml.fs`**

Near the other menu-item wiring (e.g. alongside `NewConnectionMenuItem`'s), add:

```fsharp
        this.FindControl<MenuItem>("LogsMenuItem").Click.Add(fun _ -> LogViewerWindow().Show())
```

- [ ] **Step 3: Build and run the full test suite**

Run: `dotnet build` then `dotnet test tests/FSharpQueryStudio.Logging.Tests` and `dotnet test tests/FSharpQueryStudio.Ui.Tests`
Expected: build clean; `Logging.Tests` 11 new (from Tasks 1-2) over its prior baseline; `Ui.Tests` 10 new (from Task 3) over its prior baseline; everything else unchanged and green.

- [ ] **Step 4: Write the DEVLOG entry**

Append an entry to `docs/DEVLOG.md` covering: the user request (in-app log visualization), the JSON-formatter switch and why (`renderMessage`/`shared`), the poll-based live-tail design and why not `FileSystemWatcher`, the `option`-vs-plain-string-with-`"All"`-sentinel decision for the filters (tie it explicitly to the two prior `Some(null)`/`ErrorMessage` incidents this session already hit), what was verified (build + exact test counts), and files changed — matching the shape of every other entry in this file.

- [ ] **Step 5: Commit**

```bash
git add src/FSharpQueryStudio.Ui/MainWindow.axaml src/FSharpQueryStudio.Ui/MainWindow.axaml.fs docs/DEVLOG.md
git commit -m "Feature: wire the log viewer into a new Help menu"
```
