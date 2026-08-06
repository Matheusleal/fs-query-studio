namespace FSharpQueryStudio.Logging

open System
open System.IO
open System.IO.Compression

/// D.4: zips the last N days of host/worker Serilog log files for
/// sharing during troubleshooting. Every log call already went through
/// `Redaction.RedactingEnricher` at write time (A0.6); `scanForLeakedSecrets`
/// exists purely as a defense-in-depth check that redaction actually held.
module DiagnosticBundle =

    /// Zips every `*.log` file under `logsDirectory` last written within
    /// `retainDays` of `now`, named by its own file name inside the
    /// archive. `now` is caller-supplied so bundling is deterministic in
    /// tests rather than depending on the real clock.
    let createBundle (logsDirectory: string) (retainDays: int) (now: DateTimeOffset) (destinationZipPath: string) : unit =
        let cutoffUtc = now.AddDays(-(float retainDays)).UtcDateTime

        let filesToInclude =
            Directory.GetFiles(logsDirectory, "*.log")
            |> Array.filter (fun path -> File.GetLastWriteTimeUtc(path) >= cutoffUtc)

        if File.Exists destinationZipPath then
            File.Delete destinationZipPath

        use archive = ZipFile.Open(destinationZipPath, ZipArchiveMode.Create)

        for path in filesToInclude do
            archive.CreateEntryFromFile(path, Path.GetFileName path) |> ignore

    /// Entry names (empty when clean) whose contents match
    /// `Redaction.containsLeakedSecret` — a secret-shaped assignment that
    /// isn't the redaction placeholder, meaning it slipped through
    /// unredacted.
    let scanForLeakedSecrets (zipPath: string) : string list =
        use archive = ZipFile.OpenRead(zipPath)

        [ for entry in archive.Entries do
              use reader = new StreamReader(entry.Open())
              let text = reader.ReadToEnd()

              if Redaction.containsLeakedSecret text then
                  yield entry.FullName ]
