# Log Viewer — Design

First of four planned sub-projects toward better app observability and polish for real usage (Log Viewer → Welcome Page → Results Export → UI Polish). This one adds an in-app viewer over the Serilog log files the app already writes — no other sub-project's work is a dependency of this one.

## Background

`FSharpQueryStudio.Logging`'s `LoggerSetup.build` (Section 15) already writes daily-rolling files — `host-YYYYMMDD.log` and `worker-YYYYMMDD.log` — to `%LOCALAPPDATA%/FSharpQueryStudio/logs/`, correlation-tagged and redacted before any event reaches a sink. `DiagnosticBundle.fs` (D.4) can zip recent days of these files for sharing during troubleshooting. But there is no way to see any of this *from inside the running app* — a user has to know the folder exists and open the raw files in a text editor. The user asked for "a log visualization for the use of the app to have a clear view of what's going on."

The existing file sinks use Serilog's default plain-text template. That's fine for a human tailing a file with a text editor, but brittle to parse reliably into structured rows (level/time/process/message) for a real UI — regex-scraping a human-readable template is exactly the kind of fragile parsing this codebase avoids elsewhere (see `AnalysisContext`'s line-offset handling, `DiagnosticMapping`'s explicit coordinate conversion). This design switches the file sinks to Serilog's built-in `JsonFormatter` (already part of Serilog core — no new package) instead.

## Goals

- A new "Logs…" window, reachable from a new `_Help` menu on `MainWindow`, showing structured log rows (Time, Level, Process, Message).
- Live tail of the current day's Host + Worker log files while the window is open, merged into one chronologically-sorted stream.
- Browse any past day's logs (within the existing 14-day retention window) via a date picker, replacing the live view with a static read of that day's files.
- Filter the displayed rows by Level (Information/Warning/Error) and by Process (Host/Worker).
- Never crash or show a raw exception if a line is caught mid-write (a partial JSON fragment) — skip it, the next poll re-reads it whole.

## Non-goals (deferred or out of scope)

- Exporting the currently-filtered log view to a file — the existing `DiagnosticBundle` zip export (D.4) already covers "share these logs with someone"; this window is for looking, not exporting. Could be revisited later, not part of this sub-project.
- Searching/full-text query across the log body — Level/Process filters only, per the user's stated scope.
- Real-time push (`FileSystemWatcher`) — polling is simpler and reliable enough at this data volume (see Architecture).
- Changing the *console* sink's format — only the *file* sinks change formatter; console output (visible only when running from a terminal, not the packaged app) is unaffected.
- Any change to redaction logic itself (`Redaction.fs`) — only the formatter around already-redacted events changes.

## Architecture

### File format change

`LoggerSetup.build`'s `WriteTo.File(...)` call gains two arguments: a `Serilog.Formatting.Json.JsonFormatter(renderMessage = true)` (replacing the default plain-text template — `renderMessage = true` is what makes the JSON carry a ready-to-display `RenderedMessage` string with property values substituted in, rather than only the raw `{Property}`-templated `MessageTemplate`) and `shared = true` (so the Host process's log viewer can open the Worker's log file for reading while the Worker process still holds it open for writing — Serilog's file sink defaults to an exclusive lock otherwise). `RedactingEnricher` runs before formatting regardless of formatter (it mutates `LogEvent.Properties`, upstream of any sink), so redaction behavior is unaffected — verified by a test asserting a redacted property survives round-trip through the new JSON formatter unchanged in content, just different in shape.

### Reading and parsing (`FSharpQueryStudio.Logging`)

New `LogEntry` record and `LogFileReader` module, parallel to `DiagnosticBundle.fs`'s existing role as the "read side" of this project's logging:

```fsharp
type LogEntry =
    { Timestamp: DateTimeOffset
      Level: string        // "Information" | "Warning" | "Error" | ...
      ProcessRole: string   // "host" | "worker"
      Message: string }     // JsonFormatter's own rendered message
```

- `LogFileReader.parseLine (json: string) : LogEntry option` — parses one `JsonFormatter`-produced line, reading `RenderedMessage` for `LogEntry.Message`, `Level` for `Level`, `Timestamp` for `Timestamp`, and the `ProcessRole` property (enriched onto every event by `LoggerSetup.build`) out of the JSON's `Properties` object; `None` for anything that doesn't parse as a complete, well-formed event (the mid-write-partial-line case), never an exception escaping to the caller.
- `LogFileReader.listAvailableDates (logsDirectory: string) : DateOnly list` — scans `host-*.log`/`worker-*.log` filenames for their embedded dates, deduplicated, descending (newest first) — what populates the date picker.
- `LogFileReader.readEntriesSince (filePath: string) (byteOffset: int64) : LogEntry list * int64` — reads only the bytes appended since `byteOffset`, parses complete lines (any trailing incomplete line at EOF is left unconsumed — the returned new offset stops before it, so the next call picks it up once it's whole), returns the new offset alongside the parsed entries. This is what makes live-tail polling cheap: no full-file re-read every tick.
- `LogFileReader.readAllEntries (filePath: string) : LogEntry list` — full read for a past-date static view; internally just `readEntriesSince filePath 0L`.

### View layer (`FSharpQueryStudio.Ui`)

`LogViewerViewModel` — same composition-root-hook shape used everywhere else in this codebase (`ShellViewModel.ExecuteAsync`, etc.), so it's constructible and testable with no real file I/O:

```fsharp
type LogViewerViewModel(logsDirectory: string, readEntriesSince: string -> int64 -> LogEntry list * int64) =
    member _.Entries: LogEntry list          // merged, sorted, currently-filtered rows
    member _.AvailableDates: DateOnly list
    member _.SelectedDate: DateOnly with get, set   // defaults to today; setting a past date switches IsLive off and does one static read
    member _.LevelFilter: string option with get, set
    member _.ProcessFilter: string option with get, set
    member _.IsLive: bool
    member _.Poll(): unit   // called by the window's real DispatcherTimer; no-op when IsLive is false
```

Internally tracks the last-read byte offset per file (host/worker) so repeated `Poll()` calls only parse new bytes. Switching `SelectedDate` away from today does one `readAllEntries` call per file for that date and sets `IsLive <- false`; switching back to today's date resets offsets to the live-tracked position and resumes polling.

`LogViewerWindow.axaml` — a `DataGrid` (Time/Level/Process/Message columns, same `DataGridTextColumn` construction pattern `ResultGridColumns`/`MainWindow`'s results grid already use) plus two `ComboBox` filter dropdowns and a date-picker control, all bound directly (`x:CompileBindings="False"`, matching every other view in this project). Code-behind owns exactly one thing beyond standard binding: a real Avalonia `DispatcherTimer` (~1s interval) calling `viewModel.Poll()` while the window is open, stopped on window close.

### Composition root (`MainWindow`/`App`)

`MainWindow.axaml` gains a `_Help` menu (new — doesn't exist today) with a single "Logs…" item for now (the Welcome/user-guide sub-project will add a second item here later). Its click handler constructs `LogViewerWindow` with a `LogViewerViewModel` closing over `LoggerSetup.defaultLogsDirectory()` and `LogFileReader.readEntriesSince`, then `ShowDialog`/`Show`s it (non-modal — the user should be able to keep working while watching logs) — no `App.axaml.fs` composition-root wiring needed beyond what `MainWindow` already has access to, since `defaultLogsDirectory()` needs no dependencies of its own.

## Error handling

- Logs directory doesn't exist yet (freshly installed, nothing logged this session before the window's first `Poll()` somehow runs before any file exists) — `listAvailableDates`/`readEntriesSince` treat a missing directory/file as "no entries," not an error; the window shows an empty grid with a "No logs yet" placeholder text, not a crash.
- A line that fails to parse (partial write) is silently skipped per `parseLine`'s `None` case — never surfaced to the user, never logged itself (that would risk a feedback loop through the very system being displayed).
- Any other unexpected read exception (e.g. a transient sharing violation despite `shared = true`, or disk pressure) is caught at the `Poll()` boundary and swallowed for that tick — the next poll tries again; the log viewer must never be able to crash the running app it's supposed to be helping debug.

## Testing

`Logging.Tests` (new `LogFileReaderTests.fs`):
- `parseLine` round-trips a real event logged through `LoggerSetup.build` with the new `JsonFormatter`, including one with a redacted property (`password`-shaped) — asserts the redaction placeholder, not the original value, appears in the parsed `LogEntry`.
- `parseLine` returns `None` for a truncated/partial JSON fragment, not an exception.
- `listAvailableDates` against a temp directory with a mix of `host-*.log`/`worker-*.log` fixture filenames returns the correct deduplicated, descending date list.
- `readEntriesSince` against a temp file: returns only entries past the given offset; a trailing incomplete line at EOF is excluded and the returned offset doesn't advance past it; a second call with the previous call's returned offset, after appending more complete lines, picks up exactly the new ones.

`Ui.Tests` (new `LogViewerViewModelTests.fs`):
- Constructing with a fake `readEntriesSince` and calling `Poll()` populates `Entries`, merged and sorted across host/worker.
- `LevelFilter`/`ProcessFilter` narrow `Entries` without a fresh `Poll()`/read call (filtering is over already-fetched data).
- Setting `SelectedDate` to a past date calls the injected reader for that date's files instead of the live offset-tracked read, and sets `IsLive` to `false`; setting it back to today resumes live polling from where it left off.
- `Poll()` is a no-op (no reader call) while `IsLive` is `false`.

No dedicated test for `LogViewerWindow.axaml`/its code-behind `DispatcherTimer` wiring — same standing limitation as every other XAML/code-behind change in this codebase (no headless rendering harness available this session); verified instead by a clean `dotnet build` (the XAML compiler resolves every binding/handler at build time) plus the `LogViewerViewModel` tests above covering all the real logic the window's code-behind delegates to it.
