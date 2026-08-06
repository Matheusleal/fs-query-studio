namespace FSharpQueryStudio.Storage

open System.IO
open System.Xml.Linq

/// Reads a real MSBuild-style `.fsproj`'s `<Compile Include="...">`
/// items in document order — Milestone E.5's project/file graph model.
/// F# compilation is strictly linear (each file only sees symbols from
/// files compiled before it in the project), so "resolving compile
/// order" is just preserving this document order; no dependency graph
/// or topological sort is needed, unlike C#-style projects where file
/// order doesn't matter.
module FsProj =

    type FsProject =
        { ProjectPath: string
          ProjectDirectory: string
          Files: string list }

    /// Explicit `<Compile Include="..."/>` items only — no wildcard/glob
    /// expansion. Real F# projects list files explicitly because their
    /// order is semantically meaningful; the `dotnet new` F# templates
    /// themselves never emit globs, for the same reason. Matches every
    /// `<Compile>` anywhere in the document (not just direct children of
    /// an `<ItemGroup>`, so conditionally-included items under
    /// `<Choose>`/`<When>` still show up) and ignores XML namespace, so
    /// both SDK-style (no namespace) and legacy-style
    /// (`xmlns=".../msbuild/2003"`) `.fsproj` files parse the same way.
    let load (fsprojPath: string) : FsProject =
        let doc = XDocument.Load(fsprojPath)
        let projectDirectory = Path.GetDirectoryName(fsprojPath)

        let files =
            doc.Descendants()
            |> Seq.filter (fun e -> e.Name.LocalName = "Compile")
            |> Seq.choose (fun e ->
                match e.Attribute(XName.Get "Include") with
                | null -> None
                | attr -> Some attr.Value)
            |> List.ofSeq

        { ProjectPath = fsprojPath
          ProjectDirectory = projectDirectory
          Files = files }

    /// `Files`, resolved to absolute paths rooted at the project's own
    /// directory — what a caller actually needs to read each file's
    /// contents in compile order, or feed to FCS's `SourceFiles`.
    let resolvedFilePaths (project: FsProject) : string list =
        project.Files
        |> List.map (fun relativePath -> Path.GetFullPath(Path.Combine(project.ProjectDirectory, relativePath)))
