module PublishedAppTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open Xunit

/// tests/FSharpQueryStudio.Integration.Tests/bin/{Debug|Release}/net10.0
/// -> repo root, five levels up from the test assembly's own output dir.
let private repoRoot =
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."))

let private runProcess (fileName: string) (arguments: string) (workingDirectory: string) : int * string =
    let psi =
        ProcessStartInfo(
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        )

    use proc = Process.Start(psi)
    let output = proc.StandardOutput.ReadToEnd()
    let error = proc.StandardError.ReadToEnd()
    proc.WaitForExit()
    proc.ExitCode, output + error

/// D.7's own verification line: not wired into the D.6 CI filter (see
/// its `[<Trait>]`) because a full self-contained publish plus a real
/// windowed launch is heavy and depends on a real desktop session with
/// the WebView2 runtime present - both true on a dev machine, neither
/// guaranteed cheap/fast on a shared CI runner. Run explicitly with
/// `--filter "Category=RequiresPublish"` when verifying a packaging
/// change.
[<Fact>]
[<Trait("Category", "RequiresPublish")>]
let ``a self-contained win-x64 publish of App launches and reaches a ready state`` () =
    let publishDir = Path.Combine(Path.GetTempPath(), "fqs-publish-test-" + Guid.NewGuid().ToString("N"))
    let appProject = Path.Combine(repoRoot, "src", "FSharpQueryStudio.App", "FSharpQueryStudio.App.fsproj")

    try
        let exitCode, output =
            runProcess
                "dotnet"
                $"publish \"{appProject}\" -c Release -r win-x64 --self-contained true -o \"{publishDir}\""
                repoRoot

        Assert.True((exitCode = 0), $"publish failed:\n{output}")

        let exePath = Path.Combine(publishDir, "FSharpQueryStudio.App.exe")
        Assert.True(File.Exists exePath, $"expected a published executable at {exePath}")

        let logsDir =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FSharpQueryStudio",
                "logs"
            )

        // Today's exact file, matching RollingInterval.Day's naming
        // (`host-yyyyMMdd.log`) - not a `host-*.log` glob. A glob also
        // matches every still-retained older day's file (14-day
        // retention, per A0.5), and picking "the first match" from that
        // set is a real bug, not a hypothetical: it silently diffed
        // against yesterday's untouched file instead of today's active
        // one, so every check below saw zero new bytes no matter what
        // the app actually logged.
        let hostLogPath = Path.Combine(logsDir, $"host-{DateTime.Now:yyyyMMdd}.log")

        // Byte-offset diffing rather than `File.GetLastWriteTimeUtc`
        // filtering: old runs' "Host window ready" lines are already in
        // today's file too, so only text appended after this process's
        // own launch can answer whether *this* launch reached ready.
        let priorLength = try FileInfo(hostLogPath).Length with _ -> 0L

        let psi = ProcessStartInfo(FileName = exePath, WorkingDirectory = publishDir, UseShellExecute = false)
        use proc = Process.Start(psi)

        try
            // Serilog's file sink holds the log file open with an
            // exclusive lock for as long as the process is alive - a
            // concurrent reader gets IOException the whole time, not
            // just during a brief flush window, so this can't poll the
            // live file the way a lock-free readiness signal could.
            // Instead: give the app a generous bounded window to reach
            // ready (observed ~2-3s locally), then kill it - releasing
            // the lock - and check what it actually wrote.
            Thread.Sleep(TimeSpan.FromSeconds(15.0))

            if not proc.HasExited then
                proc.Kill()

            proc.WaitForExit(5000) |> ignore

            let newText =
                if File.Exists hostLogPath then
                    let bytes = File.ReadAllBytes hostLogPath
                    let startOffset = int priorLength

                    if bytes.Length > startOffset then
                        Text.Encoding.UTF8.GetString(bytes, startOffset, bytes.Length - startOffset)
                    else
                        ""
                else
                    ""

            Assert.True(
                newText.Contains("Host window ready"),
                $"expected the published app to log \"Host window ready\"; exit code was {proc.ExitCode}, new log text:\n{newText}"
            )
        finally
            if not proc.HasExited then
                proc.Kill()
                proc.WaitForExit(5000) |> ignore
    finally
        // Best-effort: Kill() returning doesn't guarantee every DLL's file
        // handle has been released yet, and cleanup failing here must
        // never mask whatever the try block's own assertion decided.
        try
            if Directory.Exists publishDir then
                Directory.Delete(publishDir, true)
        with _ ->
            ()
