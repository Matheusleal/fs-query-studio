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
