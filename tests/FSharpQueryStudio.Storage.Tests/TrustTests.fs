module TrustTests

open System
open System.IO
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Storage

let private tempWorkspaceDir () =
    let dir = Path.Combine(Path.GetTempPath(), "fqs-workspace-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    dir

let private sampleTrust () : TrustMetadata =
    { Origin = ScriptOrigin.Downloaded
      TrustState = TrustState.Reviewed
      RequestedCapabilities = [ "filesystem-read"; "network"; "connection:Sales" ]
      ReviewedAt = Some(DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero))
      ReviewedBy = Some "matheus" }

[<Fact>]
let ``writeTrust then readTrust round-trips every field`` () =
    let dir = tempWorkspaceDir ()
    let trust = sampleTrust ()

    Trust.writeTrust dir trust

    Assert.Equal(trust, Trust.readTrust dir)

[<Fact>]
let ``readTrust defaults to origin local and trustState unknown when trust.json does not exist`` () =
    let dir = tempWorkspaceDir ()

    let trust = Trust.readTrust dir

    Assert.Equal(ScriptOrigin.Local, trust.Origin)
    Assert.Equal(TrustState.Unknown, trust.TrustState)
    Assert.Empty(trust.RequestedCapabilities)
    Assert.Equal(None, trust.ReviewedAt)
    Assert.Equal(None, trust.ReviewedBy)

[<Fact>]
let ``writeTrust round-trips None ReviewedAt and ReviewedBy`` () =
    let dir = tempWorkspaceDir ()

    let trust =
        { Origin = ScriptOrigin.Imported
          TrustState = TrustState.Unknown
          RequestedCapabilities = []
          ReviewedAt = None
          ReviewedBy = None }

    Trust.writeTrust dir trust

    Assert.Equal(trust, Trust.readTrust dir)
