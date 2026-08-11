namespace FSharpQueryStudio.Analysis

open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.EditorServices
open FSharp.Compiler.Text

/// Rename/refactoring support (Milestone E.8). `Analysis` only locates
/// every reference to rename — it never rewrites text itself, same
/// separation as everywhere else in this project (Section 4: parsing/
/// type-checking and mapping, not document mutation). Applying the
/// actual text edits at each returned range is the caller's job.
module Rename =

    /// A single reference's span — 0-based lines, matching every other
    /// protocol-facing coordinate in this project (`Diagnostic`,
    /// `DefinitionLocation`). Includes the declaration site itself: a
    /// rename must update it too, not just its uses.
    type RenameEdit =
        { StartLine: int
          StartColumn: int
          EndLine: int
          EndColumn: int }

    /// Same FCS lookup as `Hover`/`GoToDefinition`/`SignatureHelp` to
    /// resolve the symbol at (`line`, `column`), then every one of its
    /// uses within the checked file/project scope via
    /// `GetUsesOfSymbolInFile` — the declaration and every reference,
    /// not just the one clicked. `None` means nothing resolvable at that
    /// position, the same convention as the other Analysis lookups.
    let findReferences
        (context: AnalysisContext.Context)
        (source: string)
        (line: int)
        (column: int)
        : Async<RenameEdit list option> =
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
                        // `GetUsesOfSymbolInFile` is scoped to this one
                        // checked (prologue-prefixed) file, so every use
                        // it returns needs the same shift back.
                        let renameLineOffset = AnalysisContext.lineOffset context

                        let edits =
                            results.GetUsesOfSymbolInFile(symbolUse.Symbol)
                            |> Array.map (fun use_ ->
                                { StartLine = use_.Range.StartLine - 1 - renameLineOffset
                                  StartColumn = use_.Range.StartColumn
                                  EndLine = use_.Range.EndLine - 1 - renameLineOffset
                                  EndColumn = use_.Range.EndColumn })
                            |> Array.toList

                        return Some edits
        }
