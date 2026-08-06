module PackageLockTests

open System
open System.IO
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Storage

let private tempWorkspaceDir () =
    let dir = Path.Combine(Path.GetTempPath(), "fqs-workspace-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    dir

let private unlockedPackage (name: string) (version: string) : ApprovedPackage =
    { Name = name
      Version = version
      Source = "nuget.org"
      ResolvedAssets = [ $"{name}.dll"; $"{name}.Http.dll" ]
      Hash = "" }

[<Fact>]
let ``restore locks every manifest entry with a non-empty Hash`` () =
    let dir = tempWorkspaceDir ()
    Packages.writePackages dir [ unlockedPackage "FSharp.Data" "6.3.0" ]

    let locked: ApprovedPackage list = PackageLock.restore dir

    Assert.All(locked, (fun (p: ApprovedPackage) -> Assert.False(String.IsNullOrEmpty p.Hash)))

[<Fact>]
let ``two restores from the same manifest resolve to identical locked versions`` () =
    let dir = tempWorkspaceDir ()
    Packages.writePackages dir [ unlockedPackage "FSharp.Data" "6.3.0"; unlockedPackage "FParsec" "1.1.1" ]

    let firstRestore = PackageLock.restore dir
    let secondRestore = PackageLock.restore dir

    Assert.Equal<ApprovedPackage list>(firstRestore, secondRestore)

[<Fact>]
let ``restore persists locked hashes back to packages.json`` () =
    let dir = tempWorkspaceDir ()
    Packages.writePackages dir [ unlockedPackage "FSharp.Data" "6.3.0" ]

    let locked = PackageLock.restore dir

    Assert.Equal<ApprovedPackage list>(locked, Packages.readPackages dir)

[<Fact>]
let ``different packages lock to different hashes`` () =
    let dir = tempWorkspaceDir ()
    Packages.writePackages dir [ unlockedPackage "FSharp.Data" "6.3.0"; unlockedPackage "FParsec" "1.1.1" ]

    let locked = PackageLock.restore dir

    let hashes = locked |> List.map (fun p -> p.Hash) |> Set.ofList
    Assert.Equal(2, hashes.Count)
