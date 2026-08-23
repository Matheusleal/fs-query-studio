namespace FSharpQueryStudio.Fsac

/// The controlled prologue every script checked through FSAC gets
/// prepended with, so `Connections.sql`/`Results.table` resolve
/// unqualified — matching what real execution (`Execution.FsiSession
/// .create`) already does, and what `Analysis.AnalysisContext` used to do
/// via FCS `otherFlags`. Unlike `otherFlags` (an FCS-project-options-only
/// mechanism with no FSAC equivalent), this uses real `#r`/`open` lines
/// physically in the checked text — an ordinary FCS/FSI script directive
/// FSAC's own FCS-backed script handling resolves with no special
/// configuration on FSAC's side.
module Prologue =

    /// `#r "..."` is a real F# string literal, parsed by FSAC's own
    /// F#-literal escape rules — a raw Windows path's single backslashes
    /// (e.g. `\fs-query-studio`, `\net10.0`) are NOT inert path separators
    /// there, they're escape-sequence introducers (`\f` = form feed, `\n`
    /// = newline, ...) for any backslash followed by a lowercase letter
    /// F# recognizes. Confirmed against real fsautocomplete during this
    /// task: an unescaped path silently corrupted mid-string and FSAC
    /// reported FS0213 "not a valid assembly name" for every check.
    /// Doubling backslashes first makes the literal round-trip intact.
    let private escapeForStringLiteral (path: string) : string = path.Replace("\\", "\\\\")

    let build () : string =
        let connectorsPath = (typeof<FSharpQueryStudio.Connectors.IConnector>).Assembly.Location
        let resultsPath = (typeof<FSharpQueryStudio.Results.PagingCursor>).Assembly.Location

        $"#r \"{escapeForStringLiteral connectorsPath}\"\n#r \"{escapeForStringLiteral resultsPath}\"\nopen FSharpQueryStudio.Connectors\nopen FSharpQueryStudio.Results\n"

    /// The number of real (newline-terminated) lines `prologueText`
    /// occupies — computed from the actual text rather than a hardcoded
    /// constant, so a future edit to `build()` can't silently desync the
    /// offset math from what's really being prepended.
    let lineCount (prologueText: string) : int =
        prologueText.Split('\n').Length - 1

    let sourceForCheck (source: string) : string = build () + source

    /// A caller's own 1-based line (against the unprefixed source) ->
    /// the line to ask FSAC about in the prologue-prefixed document.
    let shiftUp (line: int) : int = line + lineCount (build ())

    /// An FSAC-reported line *inside this same checked document* -> the
    /// caller's real, unprefixed line. Never apply this to a location FSAC
    /// resolves in a different file (FSharp.Core, another script) — that
    /// has its own real, unshifted lines, same caveat `AnalysisContext
    /// .lineOffset`'s doc comment already carries.
    let shiftDown (line: int) : int = line - lineCount (build ())
