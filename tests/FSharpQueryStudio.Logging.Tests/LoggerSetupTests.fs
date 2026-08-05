module LoggerSetupTests

open System
open System.IO
open Xunit
open FSharpQueryStudio.Logging

[<Fact>]
let ``worker-role logger never writes to stdout, only stderr and its file`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), "fqs-logger-tests-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tempDir) |> ignore
    let originalOut = Console.Out
    let originalErr = Console.Error
    use outWriter = new StringWriter()
    use errWriter = new StringWriter()

    try
        Console.SetOut(outWriter)
        Console.SetError(errWriter)

        let logger = LoggerSetup.build "worker" "worker-test" tempDir
        logger.Information("test message for stdout purity check")

        match logger with
        | :? IDisposable as d -> d.Dispose()
        | _ -> ()
    finally
        Console.SetOut(originalOut)
        Console.SetError(originalErr)
        Directory.Delete(tempDir, true)

    Assert.Equal("", outWriter.ToString())
    Assert.Contains("test message for stdout purity check", errWriter.ToString())

[<Fact>]
let ``host-role logger writes its file under the given logs directory`` () =
    let tempDir = Path.Combine(Path.GetTempPath(), "fqs-logger-tests-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tempDir) |> ignore

    try
        let logger = LoggerSetup.build "host" "host-test" tempDir
        logger.Information("hello")

        match logger with
        | :? IDisposable as d -> d.Dispose()
        | _ -> ()

        let files = Directory.GetFiles(tempDir, "host-test-*.log")
        Assert.Single(files) |> ignore
    finally
        Directory.Delete(tempDir, true)
