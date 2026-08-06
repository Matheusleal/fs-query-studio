namespace FSharpQueryStudio.Storage

open System.Text.RegularExpressions
open FSharpQueryStudio.Protocol

/// The `#r "nuget: ..."` approval policy — Section 12. Decoupled from
/// FSI/evaluation entirely: this only reads script text and the
/// `packages.json` manifest, so the approval decision never depends on
/// (and never risks triggering) an actual package restore.
module PackageApproval =
    let private nugetReferencePattern =
        Regex(@"#r\s+""nuget:\s*([^,""]+?)\s*,\s*([^""]+?)\s*""", RegexOptions.Compiled)

    /// Every `#r "nuget: Name, Version"` reference in `source`, in the
    /// order they appear.
    let extractNugetReferences (source: string) : (string * string) list =
        nugetReferencePattern.Matches(source)
        |> Seq.cast<Match>
        |> Seq.map (fun m -> m.Groups.[1].Value, m.Groups.[2].Value)
        |> List.ofSeq

    /// Every reference `source` requests that isn't present (by Name and
    /// Version) in `manifest` — Section 12: "ask for explicit approval
    /// for a package not listed in the workspace manifest." Non-empty
    /// means the caller must not evaluate `source` yet.
    let findUnapproved (manifest: ApprovedPackage list) (source: string) : (string * string) list =
        let approved = manifest |> List.map (fun p -> p.Name, p.Version) |> Set.ofList
        extractNugetReferences source |> List.filter (approved.Contains >> not)

    /// A fingerprint of the manifest's approved set — Section 12:
    /// "require a session reset/recreation after references change."
    /// Any session cache keyed by an earlier fingerprint is stale the
    /// moment this value differs: the set of packages a script can
    /// legitimately reference has changed underneath it, so reusing that
    /// session would let it see approvals (or revocations) it never
    /// actually went through.
    let manifestFingerprint (manifest: ApprovedPackage list) : string =
        manifest
        |> List.map (fun p -> $"{p.Name}@{p.Version}")
        |> List.sort
        |> String.concat ";"
        |> System.Text.Encoding.UTF8.GetBytes
        |> System.Security.Cryptography.SHA256.HashData
        |> System.Convert.ToHexString
