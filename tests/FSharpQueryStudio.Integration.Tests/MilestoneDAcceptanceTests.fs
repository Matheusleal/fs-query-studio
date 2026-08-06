module MilestoneDAcceptanceTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security
open FSharpQueryStudio.Storage
open FSharpQueryStudio.Ui

let private tempWorkspaceDir () =
    let dir = Path.Combine(Path.GetTempPath(), "fqs-milestone-d-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    dir

/// Milestone D's synthesized acceptance criterion (no verbatim spec
/// wording exists for this milestone - see the development plan's D.9
/// row) has three parts. This test covers the one that's genuinely a
/// behavioral integration test: "an imported/untrusted script shows its
/// requested capabilities and requires explicit approval before
/// running." The other two aren't unit-testable claims - "CI is green
/// on a fresh push" is the state of the two `ci.yml` jobs added in D.6/
/// D.8 (verified against real GitHub Actions runs, not just locally),
/// and "a packaged Windows build launches from a clean machine image"
/// is D.7's `PublishedAppTests`, which already exercises exactly that
/// against the real self-contained publish output.
///
/// Wires real `Security`/`Storage`/`Ui` components together rather than
/// each in isolation (unlike D.1's `TrustTests`, D.2's
/// `OriginDetectionTests`, and D.3's `CapabilityApprovalTests`, which
/// each drive one piece against a fake/literal): this is what an actual
/// "workspace opened from an external path" run looks like end to end.
[<Fact>]
let ``an imported script requesting capabilities shows them and requires approval before running, then persists it`` () =
    task {
        let workspaceDir = tempWorkspaceDir ()

        // 1. The workspace was opened from a path outside anything this
        // app created - Section 13's "imported" origin, via D.2's real
        // detection logic rather than constructing the enum by hand.
        let origin = OriginDetection.detectOrigin @"C:\Users\someone\Downloads" (ExternalPath workspaceDir)
        Assert.Equal(ScriptOrigin.Imported, origin)

        let requestedCapabilities = [ "network"; "filesystem-read"; "connection:Sales" ]

        let initialTrust: TrustMetadata =
            { Origin = origin
              TrustState = TrustState.Unknown
              RequestedCapabilities = requestedCapabilities
              ReviewedAt = None
              ReviewedBy = None }

        // 2. Persisted and re-read through the real trust.json round trip
        // (D.1), not held only in memory - this is what "opening" an
        // already-imported workspace on a second launch actually does.
        Trust.writeTrust workspaceDir initialTrust
        let loadedTrust = Trust.readTrust workspaceDir

        let vm = ShellViewModel()
        vm.Trust <- loadedTrust
        let mutable executed = false
        vm.ExecuteAsync <- Some(fun _source ->
            executed <- true
            Task.FromResult(
                { Kind = ResultKind.Scalar
                  DisplayText = Some "2"
                  Schema = []
                  Rows = [||]
                  IsTruncated = false
                  ContinuationToken = None
                  ElapsedMs = 0L
                  Output = [] }
                : ResultEnvelope
            ))

        // 3. Requires explicit approval before running - no approval UI
        // wired up yet, so the run must not go through, matching D.3's
        // deny-by-default rule for capability requests.
        do! vm.RunAsync()
        Assert.False(executed, "an unapproved capability request must not reach ExecuteAsync")

        // 4. Shows its requested capabilities - the approval hook is the
        // stand-in for the real UI D.3 describes ("list requested
        // capabilities before running"); asserting on what it's handed
        // is what proves the capabilities actually reached the prompt.
        let mutable shownCapabilities = None
        vm.RequestCapabilityApproval <- Some(fun trust ->
            shownCapabilities <- Some trust.RequestedCapabilities
            true)

        do! vm.RunAsync()

        Assert.Equal(Some requestedCapabilities, shownCapabilities)
        Assert.True(executed, "approving must let the run reach ExecuteAsync")
        Assert.Equal(TrustState.Trusted, vm.Trust.TrustState)

        // 5. The approval persists - a second launch of the same
        // workspace must not prompt again for the same script.
        Trust.writeTrust workspaceDir vm.Trust
        let reloadedTrust = Trust.readTrust workspaceDir
        Assert.Equal(TrustState.Trusted, reloadedTrust.TrustState)
    }
