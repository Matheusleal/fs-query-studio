namespace FSharpQueryStudio.Logging

open System
open System.IO
open Serilog
open Serilog.Events

/// Builds the shared Serilog configuration used by both composition roots
/// (App and Worker) — see Section 15. Console at Debug during development,
/// a daily-rolling file at Information, correlation IDs from LogContext,
/// and redaction applied before any event reaches a sink.
module LoggerSetup =

    let private logsDirectory () =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FSharpQueryStudio",
            "logs"
        )

    /// `processRole` is recorded on every event (e.g. "host", "worker").
    /// `filePrefix` becomes the rolling file's base name (e.g. "host" -> host-20260805.log).
    let build (processRole: string) (filePrefix: string) : ILogger =
        LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.With(Redaction.RedactingEnricher())
            .Enrich.WithProperty("ProcessRole", processRole)
            .Enrich.WithProcessId()
            .Enrich.WithThreadId()
            .MinimumLevel.Debug()
            .WriteTo.Console(restrictedToMinimumLevel = LogEventLevel.Debug)
            .WriteTo.File(
                Path.Combine(logsDirectory (), $"{filePrefix}-.log"),
                restrictedToMinimumLevel = LogEventLevel.Information,
                rollingInterval = RollingInterval.Day,
                retainedFileCountLimit = Nullable(14)
            )
            .CreateLogger()
