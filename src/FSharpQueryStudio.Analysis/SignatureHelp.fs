namespace FSharpQueryStudio.Analysis

open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.EditorServices
open FSharp.Compiler.Symbols
open FSharp.Compiler.Text
open FSharpQueryStudio.Protocol

/// `analysis/signatureHelp` — Milestone E.7. Same FCS lookup and 1-based-
/// line/0-based-column convention as `Hover`/`GoToDefinition`: resolves
/// the single symbol at the requested position (click-on-the-identifier,
/// not cursor-inside-an-argument-list with active-parameter tracking —
/// that needs walking up the enclosing call expression, a real design
/// step of its own left for whenever the UI actually needs it).
module SignatureHelp =

    let signatureHelp
        (context: AnalysisContext.Context)
        (source: string)
        (line: int)
        (column: int)
        : Async<SignatureHelpInfo option> =
        async {
            let sourceText = SourceText.ofString source

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

                    match results.GetSymbolUseAtLocation(line, column, lineText, names) with
                    | None -> return None
                    | Some symbolUse ->
                        match symbolUse.Symbol with
                        | :? FSharpMemberOrFunctionOrValue as mfv when mfv.CurriedParameterGroups.Count > 0 ->
                            let parameters =
                                mfv.CurriedParameterGroups
                                |> Seq.collect id
                                |> Seq.map (fun p ->
                                    let typeText = p.Type.Format(symbolUse.DisplayContext)

                                    match p.Name with
                                    | Some name -> $"{name}: {typeText}"
                                    | None -> typeText)
                                |> List.ofSeq

                            let returnType = mfv.ReturnParameter.Type.Format(symbolUse.DisplayContext)

                            return
                                Some
                                    { DisplayName = mfv.DisplayName
                                      Parameters = parameters
                                      ReturnType = returnType }
                        | _ -> return None
        }
