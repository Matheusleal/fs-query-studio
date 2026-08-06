module PackagesTests

open System
open System.IO
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Storage

let private tempWorkspaceDir () =
    let dir = Path.Combine(Path.GetTempPath(), "fqs-workspace-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    dir

let private samplePackage (name: string) (version: string) : ApprovedPackage =
    { Name = name
      Version = version
      Source = "nuget.org"
      ResolvedAssets = [ $"{name}.dll" ] }

[<Fact>]
let ``writePackages then readPackages round-trips an ApprovedPackage list`` () =
    let dir = tempWorkspaceDir ()
    let packages = [ samplePackage "FSharp.Data" "6.3.0"; samplePackage "FParsec" "1.1.1" ]

    Packages.writePackages dir packages

    Assert.Equal<ApprovedPackage list>(packages, Packages.readPackages dir)

[<Fact>]
let ``readPackages returns an empty list when packages.json does not yet exist`` () =
    let dir = tempWorkspaceDir ()

    Assert.Empty(Packages.readPackages dir)

[<Fact>]
let ``approvePackage adds a new package and persists it`` () =
    let dir = tempWorkspaceDir ()
    let package = samplePackage "FSharp.Data" "6.3.0"

    let result = Packages.approvePackage dir package

    Assert.Equal<ApprovedPackage list>([ package ], result)
    Assert.Equal<ApprovedPackage list>([ package ], Packages.readPackages dir)

[<Fact>]
let ``approvePackage replaces an existing Name+Version entry instead of duplicating it`` () =
    let dir = tempWorkspaceDir ()
    let original = samplePackage "FSharp.Data" "6.3.0"
    Packages.writePackages dir [ original ]

    let reapproved = { original with Source = "nuget.org (re-approved)" }
    let result = Packages.approvePackage dir reapproved

    Assert.Equal<ApprovedPackage list>([ reapproved ], result)

[<Fact>]
let ``approvePackage preserves other packages when adding a second one`` () =
    let dir = tempWorkspaceDir ()
    let first = samplePackage "FSharp.Data" "6.3.0"
    Packages.writePackages dir [ first ]

    let second = samplePackage "FParsec" "1.1.1"
    let result = Packages.approvePackage dir second

    Assert.Equal<ApprovedPackage list>([ first; second ], result)
