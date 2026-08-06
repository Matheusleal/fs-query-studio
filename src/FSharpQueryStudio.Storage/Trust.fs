namespace FSharpQueryStudio.Storage

open System.IO
open System.Text.Json
open FSharpQueryStudio.Protocol

/// Reads and writes `trust.json` — Section 13's script provenance/trust/
/// capability metadata. v1 is read, written, and displayed only; it is
/// never enforced.
module Trust =

    let private fileName = "trust.json"

    let private trustFilePath (workspaceDir: string) = Path.Combine(workspaceDir, fileName)

    let private defaultTrust: TrustMetadata =
        { Origin = ScriptOrigin.Local
          TrustState = TrustState.Unknown
          RequestedCapabilities = []
          ReviewedAt = None
          ReviewedBy = None }

    /// Defaults to `origin: local`, `trustState: unknown` when
    /// `trust.json` doesn't exist yet.
    let readTrust (workspaceDir: string) : TrustMetadata =
        let path = trustFilePath workspaceDir

        if File.Exists path then
            let json = File.ReadAllText path
            JsonSerializer.Deserialize<TrustMetadata>(json, Serialization.options)
        else
            defaultTrust

    let writeTrust (workspaceDir: string) (trust: TrustMetadata) : unit =
        Directory.CreateDirectory workspaceDir |> ignore
        let json = JsonSerializer.Serialize(trust, Serialization.options)
        File.WriteAllText(trustFilePath workspaceDir, json)
