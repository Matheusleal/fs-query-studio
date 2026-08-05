namespace FSharpQueryStudio.Analysis

open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Text

/// Parse/type-check a source snapshot via FCS — see Section 8's analysis
/// pipeline, step 4. Returns raw FCS diagnostics; mapping them into the
/// protocol-neutral `Diagnostic` shape (with coordinate conversion) is a
/// later task.
module Checker =

    /// Type-checks `source` against `context`'s project options, using
    /// `context`'s own script path as the file identity FCS checks against
    /// — the context was built with exactly that path as its one source
    /// file, so a different (real) source snapshot can be checked against
    /// it without needing a file on disk.
    let check (context: AnalysisContext.Context) (source: string) : Async<FSharpDiagnostic[]> =
        async {
            let sourceText = SourceText.ofString source

            let! _parseResults, checkAnswer =
                AnalysisContext.checker.ParseAndCheckFileInProject(
                    context.ScriptPath,
                    0,
                    sourceText,
                    context.ProjectOptions
                )

            return
                match checkAnswer with
                | FSharpCheckFileAnswer.Succeeded results -> results.Diagnostics
                | FSharpCheckFileAnswer.Aborted -> [||]
        }
