namespace FSharpQueryStudio.Protocol

/// Editor-neutral diagnostic shape — see Section 8. Positions are 0-based
/// on both line and column, matching the LSP-style convention the editor
/// bridge/Monaco layer expects; FCS's own 1-based line numbers are
/// converted into this shape by `Analysis.DiagnosticMapping`, never
/// consumed raw past that boundary.
type DiagnosticSeverity =
    | Error
    | Warning
    | Information

type Diagnostic =
    { Message: string
      Severity: DiagnosticSeverity
      StartLine: int
      StartColumn: int
      EndLine: int
      EndColumn: int
      Code: string option }
