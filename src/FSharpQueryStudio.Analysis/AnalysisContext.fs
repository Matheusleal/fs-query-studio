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
          References: string list }

    let private checker = FSharpChecker.Create()

    /// Resolves the baseline FSharp.Core/framework reference set an empty
    /// standalone script anchored at `workingDirectory` would see. Sorted so
    /// two calls with the same inputs produce a byte-identical list — FCS's
    /// own resolution order is not a guarantee worth depending on downstream.
    let build (workingDirectory: string) : Context =
        let scriptPath = Path.Combine(workingDirectory, "__analysis_context__.fsx")
        let sourceText = SourceText.ofString ""

        let projectOptions, _diagnostics =
            checker.GetProjectOptionsFromScript(scriptPath, sourceText)
            |> Async.RunSynchronously

        let references =
            projectOptions.OtherOptions
            |> Array.filter (fun o -> o.StartsWith("-r:"))
            |> Array.map (fun o -> o.Substring(3))
            |> Array.sort
            |> Array.toList

        { WorkingDirectory = workingDirectory
          References = references }
