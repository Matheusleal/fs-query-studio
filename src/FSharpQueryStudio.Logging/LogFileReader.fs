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
