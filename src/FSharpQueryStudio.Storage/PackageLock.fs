namespace FSharpQueryStudio.Storage

open System.Security.Cryptography
open System.Text
open FSharpQueryStudio.Protocol

/// D.5: dependency reproducibility. Locks each `packages.json` entry's
/// resolved assets to a deterministic hash, so restoring the same
/// manifest twice always reproduces byte-identical locked versions.
module PackageLock =

    /// A deterministic digest over `package`'s name, version, source, and
    /// sorted resolved assets — a stand-in for a real NuGet content hash,
    /// since this app never performs an actual package restore (Section
    /// 12's approval flow, C.16, is deliberately decoupled from one).
    let computeHash (package: ApprovedPackage) : string =
        let sortedAssets = package.ResolvedAssets |> List.sort |> String.concat ";"

        $"{package.Name}@{package.Version}|{package.Source}|{sortedAssets}"
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> System.Convert.ToHexString

    /// Recomputes and persists every manifest entry's `Hash`. Calling
    /// this twice on an unchanged `packages.json` produces an identical
    /// locked list both times.
    let restore (workspaceDir: string) : ApprovedPackage list =
        let locked =
            Packages.readPackages workspaceDir
            |> List.map (fun p -> { p with Hash = computeHash p })

        Packages.writePackages workspaceDir locked
        locked
