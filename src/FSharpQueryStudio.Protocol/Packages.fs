namespace FSharpQueryStudio.Protocol

/// One approved `#r "nuget: ..."` reference — Section 12's
/// `packages.json` manifest: "record approved package names, versions,
/// source, and resolved assets."
type ApprovedPackage =
    { Name: string
      Version: string
      Source: string
      ResolvedAssets: string list }
