namespace FSharpQueryStudio.Logging

open System
open System.IO
open Serilog
open Serilog.Events
open Serilog.Formatting.Json

/// Builds the shared Serilog configuration used by both composition roots
/// (App and Worker) — see Section 15. A daily-rolling file at Information,
/// correlation IDs from LogContext, and redaction applied before any event
/// reaches a sink. Console behavior depends on `processRole`: the worker's
/// stdout is a reserved NDJSON protocol channel (Section 7), so a
/// "worker"-role logger routes every console line to stderr instead;
/// other roles use Serilog's normal stdout console sink.
module LoggerSetup =

    /// The real per-user app-data logs folder used in production. Tests
    /// pass an explicit temp directory to `build` instead of this.
    let defaultLogsDirectory () =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FSharpQueryStudio",
            "logs"
        )

    /// `processRole` is recorded on every event (e.g. "host", "worker") and
    /// controls console routing (see module doc). `filePrefix` becomes the
    /// rolling file's base name (e.g. "host" -> host-20260805.log).
    let build (processRole: string) (filePrefix: string) (logsDirectory: string) : ILogger =
        let withConsole (config: LoggerConfiguration) =
            if processRole = "worker" then
                config.WriteTo.Console(
                    restrictedToMinimumLevel = LogEventLevel.Debug,
                    standardErrorFromLevel = Nullable(LogEventLevel.Verbose)
                )
            else
                config.WriteTo.Console(restrictedToMinimumLevel = LogEventLevel.Debug)

        LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.With(Redaction.RedactingEnricher())
            .Enrich.WithProperty("ProcessRole", processRole)
            .Enrich.WithProcessId()
            .Enrich.WithThreadId()
            .MinimumLevel.Debug()
        |> withConsole
        |> fun config ->
            config
                .WriteTo.File(
                    JsonFormatter(renderMessage = true),
                    Path.Combine(logsDirectory, $"{filePrefix}-.log"),
                    restrictedToMinimumLevel = LogEventLevel.Information,
                    rollingInterval = RollingInterval.Day,
                    retainedFileCountLimit = Nullable(14),
                    shared = true
                )
                .CreateLogger()
