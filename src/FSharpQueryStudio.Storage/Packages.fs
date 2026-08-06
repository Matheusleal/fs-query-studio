namespace FSharpQueryStudio.Storage

open System.IO
open System.Text.Json
open FSharpQueryStudio.Protocol

/// Reads and writes `packages.json` — Section 12's approved
/// `#r "nuget: ..."` reference manifest: package names, versions,
/// source, and resolved assets.
module Packages =

    let private fileName = "packages.json"

    let private packagesFilePath (workspaceDir: string) = Path.Combine(workspaceDir, fileName)

    /// The empty list if `packages.json` doesn't exist yet — a brand-new
    /// workspace has no packages approved.
    let readPackages (workspaceDir: string) : ApprovedPackage list =
        let path = packagesFilePath workspaceDir

        if File.Exists path then
            let json = File.ReadAllText path
            JsonSerializer.Deserialize<ApprovedPackage list>(json, Serialization.options)
        else
            []

    let writePackages (workspaceDir: string) (packages: ApprovedPackage list) : unit =
        Directory.CreateDirectory workspaceDir |> ignore
        let json = JsonSerializer.Serialize(packages, Serialization.options)
        File.WriteAllText(packagesFilePath workspaceDir, json)

    /// `package/approve` — Section 7/12. Adds `package` as newly
    /// approved, or replaces the existing entry sharing its
    /// `Name`+`Version`, then persists the full list back to
    /// `packages.json`. Returns the updated list.
    let approvePackage (workspaceDir: string) (package: ApprovedPackage) : ApprovedPackage list =
        let updated =
            readPackages workspaceDir
            |> List.filter (fun (p: ApprovedPackage) -> not (p.Name = package.Name && p.Version = package.Version))
            |> fun rest -> rest @ [ package ]

        writePackages workspaceDir updated
        updated
