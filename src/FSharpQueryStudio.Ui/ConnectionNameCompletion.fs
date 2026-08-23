namespace FSharpQueryStudio.Ui

open System.Text.RegularExpressions

/// Detects the one editor position where this app's own registered
/// connections, not FSAC's identifier completion, are the useful
/// completion source: the first string-literal argument of
/// `Connections.sql/csv/excel/rest` (Section 10's controlled connection
/// API — see `FSharpQueryStudio.Connectors.Connections`), where a script
/// author would otherwise have to type a connection's name from memory.
module ConnectionNameCompletion =
    /// Matches a line prefix ending inside an unterminated string right
    /// after `Connections.<fn>`, optionally through an opening paren —
    /// covers both `Connections.sql "..` and `Connections.sql("..`. `$`
    /// anchors to the end of the (single-line) prefix being tested, i.e.
    /// "cursor is here, inside the quotes, with no closing quote yet".
    let private pattern =
        Regex(@"Connections\.(?:sql|csv|excel|rest)\s*\(?\s*""[^""]*$", RegexOptions.Compiled)

    /// `line` is 1-based, `column` is a 0-based character offset into
    /// that line — the same convention every other editor-request handler
    /// in `MainWindow.axaml.fs` uses. Never throws on an out-of-range
    /// `line`/`column`: a stale request racing a document edit should
    /// just miss this fast path and fall through to FSAC, not crash it.
    let isInsideConnectionNameArgument (source: string) (line: int) (column: int) : bool =
        let lines = source.Replace("\r\n", "\n").Split('\n')

        if line < 1 || line > lines.Length then
            false
        else
            let lineText = lines.[line - 1]
            let column = min (max column 0) lineText.Length
            pattern.IsMatch(lineText.Substring(0, column))
