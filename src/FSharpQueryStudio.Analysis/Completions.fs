namespace FSharpQueryStudio.Analysis

open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.EditorServices
open FSharp.Compiler.Text

/// Completions — see Section 8's completion rollout: tier 1 (MVP, not
/// position-sensitive) and tier 2 (semantic, cursor-aware via FCS).
module Completions =

    /// F# keywords — not exhaustive, just enough to be useful for MVP.
    let keywords: string list =
        [ "let"
          "rec"
          "mutable"
          "fun"
          "match"
          "with"
          "if"
          "then"
          "else"
          "for"
          "while"
          "do"
          "type"
          "module"
          "open"
          "try"
          "finally"
          "async"
          "task"
          "yield"
          "return" ]

    /// Common snippet templates surfaced as candidate names.
    let snippets: string list = [ "printfn \"%s\""; "match x with"; "for i in" ]

    /// Controlled script API surface names — see Section 11's
    /// `Connections.sql "Sales" "select ..." [...]` example. C.11 wires the
    /// real connector helpers; this is the static MVP set.
    let connectorHelperNames: string list =
        [ "Connections.sql"
          "Connections.sqlServer"
          "Connections.postgres"
          "Connections.csv"
          "Connections.excel"
          "Connections.rest" ]

    /// `recentNames` is whatever the most recent successful `analysis/check`
    /// declared — owned and cached by the caller, not this module.
    let complete (recentNames: string list) : string list =
        keywords @ snippets @ connectorHelperNames @ recentNames |> List.distinct

    /// Tier 2 (semantic): real FCS completion data at `(line, column)`,
    /// using the same "line 1-based, column 0-based at/after the token"
    /// convention as `Hover.hover` — this layer talks straight to FCS too.
    let completeAtPosition
        (context: AnalysisContext.Context)
        (source: string)
        (line: int)
        (column: int)
        : Async<string list> =
        async {
            let sourceText = SourceText.ofString (AnalysisContext.sourceForCheck context source)

            let! parseResults, checkAnswer =
                AnalysisContext.checker.ParseAndCheckFileInProject(
                    context.ScriptPath,
                    0,
                    sourceText,
                    context.ProjectOptions
                )

            match checkAnswer with
            | FSharpCheckFileAnswer.Aborted -> return []
            | FSharpCheckFileAnswer.Succeeded results ->
                let lines = source.Replace("\r\n", "\n").Split('\n')

                if line < 1 || line > lines.Length then
                    return []
                else
                    let fcsLine = line + AnalysisContext.lineOffset context
                    let lineText = lines.[line - 1]

                    // `column` follows this layer's shared convention - a
                    // 0-based column at/*after* the end of the token, i.e.
                    // an exclusive end. `GetPartialLongNameEx` instead wants
                    // an *inclusive* index: the 0-based position of the last
                    // character typed, and it hands back
                    // `PartialLongName.Empty` for any index at or past the
                    // line's length. Passing the exclusive end straight
                    // through therefore blanked the partial name in exactly
                    // the case that matters most - the caret sitting at the
                    // end of the line, which is where completion is always
                    // requested from. With an empty partial name the
                    // qualifying idents are lost too, so `s.Sub|` fell back
                    // to a dump of everything in top-level scope instead of
                    // String's members. A negative index (caret at column 0)
                    // is fine: GetPartialLongNameEx treats it as empty, which
                    // is the correct "nothing typed yet" answer.
                    let partial = QuickParse.GetPartialLongNameEx(lineText, column - 1)

                    let declInfo =
                        results.GetDeclarationListInfo(Some parseResults, fcsLine, lineText, partial, (fun () -> []))

                    return declInfo.Items |> Array.map (fun item -> item.NameInList) |> Array.toList
        }
