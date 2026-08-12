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
