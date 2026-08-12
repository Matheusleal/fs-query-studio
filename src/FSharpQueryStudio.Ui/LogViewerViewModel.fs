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

    /// Same never-crash-the-viewer guard as `Poll`/`SelectedDate`'s static
    /// read: `listAvailableDates` checks `Directory.Exists` itself but its
    /// subsequent `Directory.GetFiles` can still throw (permissions, the
    /// directory disappearing between the check and the call, ...), and
    /// this is bound directly to the window's date-picker `ItemsSource`.
    member _.AvailableDates =
        try
            LogFileReader.listAvailableDates logsDirectory
        with _ ->
            []
    member _.IsLive = isLive
    member _.LevelOptions = [ "All"; "Information"; "Warning"; "Error" ]
    member _.ProcessOptions = [ "All"; "host"; "worker" ]

    /// The currently-displayed rows: `rawEntries` (already merged and
    /// sorted newest-first by `Timestamp` at fetch time — `Poll`/a
    /// `SelectedDate` switch — so a live tail shows new lines at the top of
    /// the grid without needing the user to scroll), narrowed by
    /// `LevelFilter`/`ProcessFilter` on every access. Filtering never
    /// triggers a fresh read, just a re-derivation over what's already
    /// fetched.
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
                        rawEntries <- (hostEntries @ workerEntries) |> List.sortByDescending (fun e -> e.Timestamp)
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

                    rawEntries <-
                        (rawEntries @ newHostEntries @ newWorkerEntries)
                        |> List.sortByDescending (fun e -> e.Timestamp)
                    raise "Entries"
                    raise "HasEntries"
                    raise "HasNoEntries"
            with _ ->
                ()
