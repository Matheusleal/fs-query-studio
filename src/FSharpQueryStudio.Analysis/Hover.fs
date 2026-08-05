namespace FSharpQueryStudio.Analysis

open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.EditorServices
open FSharp.Compiler.Symbols
open FSharp.Compiler.Text
open FSharpQueryStudio.Protocol

/// `analysis/hover` — see Section 8. Inputs use FCS's own coordinate
/// convention directly (1-based line, 0-based column, at/after the end of
/// the identifier) since this layer talks straight to FCS; whatever wires
/// the real request converts from the UI's coordinates the same way
/// `Analysis.Checker`'s caller feeds it a raw source snapshot.
module Hover =

    let hover (context: AnalysisContext.Context) (source: string) (line: int) (column: int) : Async<HoverInfo option> =
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
                        | :? FSharpMemberOrFunctionOrValue as mfv ->
                            let typeText = mfv.FullType.Format(symbolUse.DisplayContext)

                            let signature =
                                if mfv.IsFunction || mfv.CurriedParameterGroups.Count > 0 then
                                    Some typeText
                                else
                                    None

                            let xmlDoc =
                                match mfv.XmlDoc with
                                | FSharpXmlDoc.FromXmlText doc when not doc.IsEmpty ->
                                    Some(doc.UnprocessedLines |> String.concat "\n")
                                | _ -> None

                            return
                                Some
                                    { DisplayName = symbolUse.Symbol.DisplayName
                                      Type = typeText
                                      Signature = signature
                                      XmlDoc = xmlDoc }
                        | symbol ->
                            return
                                Some
                                    { DisplayName = symbol.DisplayName
                                      Type = symbol.DisplayName
                                      Signature = None
                                      XmlDoc = None }
        }
