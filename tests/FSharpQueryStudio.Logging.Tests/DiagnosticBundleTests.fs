module DiagnosticBundleTests

open System
open System.IO
open System.IO.Compression
open Serilog
open Serilog.Events
open Xunit
open FSharpQueryStudio.Logging

let private tempDir () =
    let dir = Path.Combine(Path.GetTempPath(), "fqs-bundle-tests-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    dir

let private writeLogFile (dir: string) (name: string) (content: string) (lastWriteUtc: DateTime) =
    let path = Path.Combine(dir, name)
    File.WriteAllText(path, content)
    File.SetLastWriteTimeUtc(path, lastWriteUtc)
    path

[<Fact>]
let ``createBundle zips log files written within the retention window`` () =
    let logsDir = tempDir ()
    let now = DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero)
    writeLogFile logsDir "host-20260806.log" "host line" now.UtcDateTime |> ignore
    writeLogFile logsDir "worker-20260806.log" "worker line" now.UtcDateTime |> ignore
    let zipPath = Path.Combine(logsDir, "bundle.zip")

    DiagnosticBundle.createBundle logsDir 14 now zipPath

    use archive = ZipFile.OpenRead(zipPath)
    let names = archive.Entries |> Seq.map (fun e -> e.FullName) |> Set.ofSeq
    Assert.Equal<Set<string>>(set [ "host-20260806.log"; "worker-20260806.log" ], names)

[<Fact>]
let ``createBundle excludes log files older than the retention window`` () =
    let logsDir = tempDir ()
    let now = DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero)
    writeLogFile logsDir "host-20260806.log" "recent" now.UtcDateTime |> ignore
    writeLogFile logsDir "host-20260101.log" "stale" (now.AddDays(-30.0).UtcDateTime) |> ignore
    let zipPath = Path.Combine(logsDir, "bundle.zip")

    DiagnosticBundle.createBundle logsDir 14 now zipPath

    use archive = ZipFile.OpenRead(zipPath)
    let names = archive.Entries |> Seq.map (fun e -> e.FullName) |> Set.ofSeq
    Assert.Equal<Set<string>>(set [ "host-20260806.log" ], names)

[<Fact>]
let ``scanForLeakedSecrets flags an unredacted password assignment`` () =
    let logsDir = tempDir ()
    let now = DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero)
    writeLogFile logsDir "host-20260806.log" "connecting with password=hunter2" now.UtcDateTime |> ignore
    let zipPath = Path.Combine(logsDir, "bundle.zip")
    DiagnosticBundle.createBundle logsDir 14 now zipPath

    let leaks = DiagnosticBundle.scanForLeakedSecrets zipPath

    Assert.Equal<string list>([ "host-20260806.log" ], leaks)

[<Fact>]
let ``scanForLeakedSecrets flags an unredacted password in JSON-formatted log lines`` () =
    // The file sinks write JSON (`LoggerSetup.fs`), which puts quotes
    // around both the key and the value - a shape the plain-text
    // `key: value`/`key=value` pattern alone can't match.
    let logsDir = tempDir ()
    let now = DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero)

    writeLogFile
        logsDir
        "host-20260806.log"
        """{"Timestamp":"2026-08-06T12:00:00Z","Level":"Information","Properties":{"password":"hunter2"}}"""
        now.UtcDateTime
    |> ignore

    let zipPath = Path.Combine(logsDir, "bundle.zip")
    DiagnosticBundle.createBundle logsDir 14 now zipPath

    let leaks = DiagnosticBundle.scanForLeakedSecrets zipPath

    Assert.Equal<string list>([ "host-20260806.log" ], leaks)

[<Fact>]
let ``scanForLeakedSecrets finds nothing for an already-redacted JSON property value`` () =
    let logsDir = tempDir ()
    let now = DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero)

    writeLogFile
        logsDir
        "host-20260806.log"
        """{"Timestamp":"2026-08-06T12:00:00Z","Level":"Information","Properties":{"password":"***REDACTED***"}}"""
        now.UtcDateTime
    |> ignore

    let zipPath = Path.Combine(logsDir, "bundle.zip")
    DiagnosticBundle.createBundle logsDir 14 now zipPath

    let leaks = DiagnosticBundle.scanForLeakedSecrets zipPath

    Assert.Empty(leaks)

[<Fact>]
let ``scanForLeakedSecrets finds nothing once RedactingEnricher has already redacted the value`` () =
    // Builds a minimal file-only logger (no console sink) rather than
    // LoggerSetup.build - that call touches the real, process-global
    // Console.Out/Error, which races with LoggerSetupTests' own
    // Console.SetOut/SetError swap when xUnit runs these test classes
    // in parallel.
    let logsDir = tempDir ()
    let now = DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero)

    let logger =
        LoggerConfiguration()
            .Enrich.With(Redaction.RedactingEnricher())
            .WriteTo.File(
                Path.Combine(logsDir, "host-.log"),
                restrictedToMinimumLevel = LogEventLevel.Information,
                rollingInterval = RollingInterval.Day
            )
            .CreateLogger()

    logger.Information("connecting {Password}", "hunter2")
    (logger :> IDisposable).Dispose()

    let zipPath = Path.Combine(logsDir, "bundle.zip")
    DiagnosticBundle.createBundle logsDir 14 now zipPath

    let leaks = DiagnosticBundle.scanForLeakedSecrets zipPath

    Assert.Empty(leaks)
