module PackageApprovalTests

open System
open System.IO
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Storage

let private tempWorkspaceDir () =
    let dir = Path.Combine(Path.GetTempPath(), "fqs-workspace-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    dir

let private script =
    "#r \"nuget: FSharp.Data, 6.3.0\"\nopen FSharp.Data\n1 + 1"

[<Fact>]
let ``extractNugetReferences finds every #r "nuget: ..." reference in a script`` () =
    let refs =
        PackageApproval.extractNugetReferences
            "#r \"nuget: FSharp.Data, 6.3.0\"\n#r \"nuget: FParsec, 1.1.1\"\n1 + 1"

    Assert.Equal<(string * string) list>([ "FSharp.Data", "6.3.0"; "FParsec", "1.1.1" ], refs)

[<Fact>]
let ``an unlisted package is rejected until it is approved in the real workspace manifest`` () =
    let dir = tempWorkspaceDir ()

    // Nothing approved yet - the reference must be rejected.
    let beforeApproval = PackageApproval.findUnapproved (Packages.readPackages dir) script
    Assert.Equal<(string * string) list>([ "FSharp.Data", "6.3.0" ], beforeApproval)

    // Approve it for real, through Storage's packages.json.
    Packages.approvePackage
        dir
        { Name = "FSharp.Data"
          Version = "6.3.0"
          Source = "nuget.org"
          ResolvedAssets = [ "FSharp.Data.dll" ]
          Hash = "" }
    |> ignore

    // Now the same script's reference is approved.
    let afterApproval = PackageApproval.findUnapproved (Packages.readPackages dir) script
    Assert.Empty(afterApproval)

[<Fact>]
let ``approving a package changes the manifest fingerprint, invalidating any session cached against the old one`` () =
    let dir = tempWorkspaceDir ()

    let fingerprintBefore = PackageApproval.manifestFingerprint (Packages.readPackages dir)

    Packages.approvePackage
        dir
        { Name = "FSharp.Data"
          Version = "6.3.0"
          Source = "nuget.org"
          ResolvedAssets = [ "FSharp.Data.dll" ]
          Hash = "" }
    |> ignore

    let fingerprintAfter = PackageApproval.manifestFingerprint (Packages.readPackages dir)

    Assert.NotEqual<string>(fingerprintBefore, fingerprintAfter)
