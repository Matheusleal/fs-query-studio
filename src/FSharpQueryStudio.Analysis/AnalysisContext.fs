namespace FSharpQueryStudio.Analysis

open System.IO
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text

/// Standalone-script analysis context — see Section 8. Deliberately separate
/// from `Execution.FsiSession`: this only ever asks FCS what a script *would*
/// see if checked, it never evaluates anything.
module AnalysisContext =

    type Context =
        { WorkingDirectory: string
          ScriptPath: string
          References: string list
          ProjectOptions: FSharpProjectOptions }

    /// Shared with `Checker` so both modules benefit from FCS's own
    /// project/parse caching instead of each holding a separate, colder
    /// instance.
    let checker = FSharpChecker.Create()

    /// Opens the controlled script API's namespaces ahead of whatever's
    /// checked, so live analysis (diagnostics, hover, go-to-definition,
    /// signature help, completions, rename) sees `Connections`/`Results`
    /// resolve exactly as real execution already does — `FsiSession
    /// .create`'s own prologue auto-references *and opens* both, and a
    /// mere assembly reference (`build`'s `otherFlags`, above) makes the
    /// two modules' members resolvable but not their own qualified names
    /// (`Connections.sql`, not just `sql`) without this. Two real lines,
    /// so every caller below must add `prologueLineCount` to its own
    /// 1-based `line` before asking FCS to look anything up, and
    /// subtract it back from any FCS-reported line for a location that
    /// resolved inside *this same checked file* (`range.FileName =
    /// context.ScriptPath`) before it reaches the wire — never applied
    /// to a location FCS resolves in a different file (FSharp.Core,
    /// another project file), which has its own real, unshifted lines.
    let prologue = "open FSharpQueryStudio.Connectors\nopen FSharpQueryStudio.Results\n"
    let prologueLineCount = 2

    /// Only for a standalone-script context (`build`, not
    /// `buildForProject`) — an E.5 multi-file project checks each of its
    /// own real files by name (`buildForProject`'s own doc comment: a
    /// caller checking one specific file must point `ScriptPath` at that
    /// file directly), and the prologue has no per-file story there yet;
    /// applying it would shift only the one file being live-checked
    /// while every *other* project file stays on disk unprefixed,
    /// breaking cross-file resolution. Multi-file `Connections`/`Results`
    /// support is out of scope here — those checks run exactly as before.
    let private isStandaloneScript (context: Context) : bool =
        context.ProjectOptions.UseScriptResolutionRules

    /// The source text to actually hand FCS for `context` — prologue-
    /// prefixed for a standalone script, unchanged for a multi-file
    /// project.
    let sourceForCheck (context: Context) (source: string) : string =
        if isStandaloneScript context then prologue + source else source

    /// How far a caller's own 1-based `line` (against their *unprefixed*
    /// source) must be shifted before asking FCS to look anything up at
    /// it for `context`, and how far an FCS-reported line for a location
    /// inside *this same checked file* must be shifted back before it
    /// reaches the wire. Zero for a multi-file project (no prologue was
    /// ever prepended there).
    let lineOffset (context: Context) : int =
        if isStandaloneScript context then prologueLineCount else 0

    /// Resolves the baseline FSharp.Core/framework reference set an empty
    /// standalone script anchored at `workingDirectory` would see. Sorted so
    /// two calls with the same inputs produce a byte-identical list — FCS's
    /// own resolution order is not a guarantee worth depending on downstream.
    let build (workingDirectory: string) : Context =
        let scriptPath = Path.Combine(workingDirectory, "__analysis_context__.fsx")
        let sourceText = SourceText.ofString ""

        // Explicit rather than FCS's own runtime auto-detection: found via
        // D.8's Linux CI job that the default heuristic misdetects the
        // .NET SDK reference-assembly layout inside a Linux container,
        // making even `int` fail to resolve ("The type 'int' is not
        // defined") instead of resolving against the running SDK's own
        // reference assemblies. Confirmed fixed with these three flags
        // set explicitly; unaffected on Windows (already the effective
        // default there).
        //
        // `otherFlags` also carries the Connectors/Results assembly
        // references real execution (`FsiSession.create`) auto-loads, so
        // a script can call `Connections.sql`/`Results.table` with no
        // `#r`/`open` of its own - both modules are `[<AutoOpen>]`
        // precisely so a mere reference (not a textual `open` injected
        // ahead of the user's own script, which would shift every line
        // number every analysis function below maps back to Monaco's
        // coordinates) is enough. This must go through `otherFlags` here,
        // not be appended to the returned `ProjectOptions.OtherOptions`
        // afterward - script resolution rules re-derive references from
        // exactly the flags passed to this call; anything appended
        // post-hoc is silently never consulted by `ParseAndCheckFileInProject`.
        let controlledApiReferences =
            [| $"-r:{typeof<FSharpQueryStudio.Connectors.IConnector>.Assembly.Location}"
               $"-r:{typeof<FSharpQueryStudio.Results.PagingCursor>.Assembly.Location}" |]

        let projectOptions, _diagnostics =
            checker.GetProjectOptionsFromScript(
                scriptPath,
                sourceText,
                assumeDotNetFramework = false,
                useSdkRefs = true,
                useFsiAuxLib = false,
                otherFlags = controlledApiReferences
            )
            |> Async.RunSynchronously

        let references =
            projectOptions.OtherOptions
            |> Array.filter (fun o -> o.StartsWith("-r:"))
            |> Array.map (fun o -> o.Substring(3))
            |> Array.sort
            |> Array.toList

        { WorkingDirectory = workingDirectory
          ScriptPath = scriptPath
          References = references
          ProjectOptions = projectOptions }

    /// Builds an FCS project context spanning `files`, in the order
    /// given — Milestone E.5's multi-file `.fsproj` support. `files`
    /// should already be absolute paths in `.fsproj` compile order (see
    /// `Storage.FsProj.resolvedFilePaths`); this module doesn't know
    /// about `.fsproj` itself, only about the ordered file list one
    /// produces — `Analysis` stays independent of `Storage`, the same
    /// way every other sibling project here only shares `Protocol`.
    /// Reuses `build`'s baseline reference resolution (D.8's explicit-
    /// flags fix applies here too) rather than re-deriving it.
    ///
    /// `Context.ScriptPath` on the result is a throwaway identity, not
    /// one of `files` — a caller checking a *specific* file in this
    /// project must call FCS's own `ParseAndCheckFileInProject` with
    /// that file's real path from `files`, not go through `Checker.
    /// check` (which is hardwired to check `context.ScriptPath`).
    let buildForProject (projectDirectory: string) (files: string list) : Context =
        let baseline = build projectDirectory

        let projectOptions =
            { baseline.ProjectOptions with
                ProjectFileName = Path.Combine(projectDirectory, "__analysis_project__.fsproj")
                SourceFiles = files |> List.toArray
                UseScriptResolutionRules = false }

        { baseline with ProjectOptions = projectOptions }
