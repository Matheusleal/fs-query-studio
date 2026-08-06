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
        let projectOptions, _diagnostics =
            checker.GetProjectOptionsFromScript(
                scriptPath,
                sourceText,
                assumeDotNetFramework = false,
                useSdkRefs = true,
                useFsiAuxLib = false
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
