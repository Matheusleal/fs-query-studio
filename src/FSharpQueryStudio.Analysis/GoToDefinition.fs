namespace FSharpQueryStudio.Analysis

open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.EditorServices
open FSharp.Compiler.Text
open FSharpQueryStudio.Protocol

/// `analysis/definition` — go-to-definition (Milestone E.6). Same FCS
/// coordinate convention and symbol-lookup approach as `Hover` (1-based
/// line, 0-based column, at/after the identifier's end); mapping the
/// resolved declaration's own range back to the protocol's 0-based-line
/// convention is this module's own job, same as `DiagnosticMapping`.
module GoToDefinition =

    let findDefinition
        (context: AnalysisContext.Context)
        (source: string)
        (line: int)
        (column: int)
        : Async<DefinitionLocation option> =
        async {
            let sourceText = SourceText.ofString (AnalysisContext.sourceForCheck context source)

            let! _parseResults, checkAnswer =
                AnalysisContext.checker.ParseAndCheckFileInProject(
                    context.ScriptPath,
                    0,
                    sourceText,
                    context.ProjectOptions
                )

            match checkAnswer with
            | FSharpCheckFileAnswer.Aborted -> return None
            | FSharpCheckFileAnswer.Succeeded results ->
                let lines = source.Replace("\r\n", "\n").Split('\n')

                if line < 1 || line > lines.Length then
                    return None
                else
                    let lineText = lines.[line - 1]
                    let partial = QuickParse.GetPartialLongNameEx(lineText, column)
                    let names = partial.QualifyingIdents @ [ partial.PartialIdent ]
                    let fcsLine = line + AnalysisContext.lineOffset context

                    match results.GetSymbolUseAtLocation(fcsLine, column, lineText, names) with
                    | None -> return None
                    | Some symbolUse ->
                        match symbolUse.Symbol.DeclarationLocation with
                        | None -> return None
                        | Some range ->
                            // Only a declaration resolved inside *this*
                            // checked file was ever shifted by the
                            // prologue - one resolved elsewhere
                            // (FSharp.Core, another project file) has its
                            // own real, unshifted lines.
                            let lineAdjustment =
                                if range.FileName = context.ScriptPath then
                                    AnalysisContext.lineOffset context
                                else
                                    0

                            return
                                Some
                                    { FilePath = range.FileName
                                      StartLine = range.StartLine - 1 - lineAdjustment
                                      StartColumn = range.StartColumn
                                      EndLine = range.EndLine - 1 - lineAdjustment
                                      EndColumn = range.EndColumn }
        }
