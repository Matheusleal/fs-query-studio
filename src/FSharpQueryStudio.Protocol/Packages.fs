namespace FSharpQueryStudio.Protocol

/// One approved `#r "nuget: ..."` reference — Section 12's
/// `packages.json` manifest: "record approved package names, versions,
/// source, and resolved assets." `Hash` is D.5's dependency-reproducibility
/// lock: a deterministic digest over the resolved assets, so re-restoring
/// the same manifest always reproduces the same value.
type ApprovedPackage =
    { Name: string
      Version: string
      Source: string
      ResolvedAssets: string list
      Hash: string }
