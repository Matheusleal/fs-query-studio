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
