namespace FSharpQueryStudio.Analysis

/// Notebook-style `// %%` cell parsing — see the architecture doc's
/// "Preparing for persistent sessions" table's Notebook/cell session row.
/// Pure text splitting only; running the resulting cells in order against
/// a persistent session is `SessionStore.appendFragment`'s job.
module NotebookCells =

    /// `StartLine` is 0-based, matching `Diagnostic`'s Monaco-facing
    /// convention elsewhere in this project (see `DiagnosticMapping`).
    type Cell = { Text: string; StartLine: int }

    /// A cell boundary marker: a line that, trimmed, is exactly `// %%`
    /// or `// %%` followed by a space and an optional title (Jupyter/
    /// Polyglot-style, e.g. `// %% Cell 2`). The marker line itself is a
    /// separator, not code - it never appears in any cell's `Text`.
    let private isBoundary (line: string) = line.Trim().StartsWith("// %%")

    /// Splits `source` into cells at `// %%` boundaries, in document
    /// order. A script with no boundaries at all is one single cell
    /// holding everything. A boundary immediately followed by another
    /// boundary (or by the start/end of the document) produces no empty
    /// cell for that gap - there's nothing to run there.
    let parse (source: string) : Cell list =
        let lines = source.Replace("\r\n", "\n").Split('\n')

        let boundaryIndices =
            lines |> Array.mapi (fun i line -> i, line) |> Array.filter (snd >> isBoundary) |> Array.map fst

        let segmentStarts = 0 :: (boundaryIndices |> Array.map ((+) 1) |> Array.toList)
        let segmentEnds = (boundaryIndices |> Array.toList) @ [ lines.Length ]

        List.zip segmentStarts segmentEnds
        |> List.choose (fun (startLine, endExclusive) ->
            if startLine >= endExclusive then
                None
            else
                let text = lines.[startLine .. endExclusive - 1] |> String.concat "\n"
                if System.String.IsNullOrWhiteSpace(text) then None
                else Some { Text = text; StartLine = startLine })
