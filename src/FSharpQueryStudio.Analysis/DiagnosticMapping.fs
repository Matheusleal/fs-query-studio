namespace FSharpQueryStudio.Analysis

open FSharp.Compiler.Diagnostics
open FSharpQueryStudio.Protocol

/// Maps raw FCS diagnostics into the protocol-neutral `Diagnostic` shape —
/// see Section 8's explicit 1-based-FCS-vs-0-based-Monaco call-out. FCS
/// itself already reports StartColumn/EndColumn 0-based; only the 1-based
/// StartLine/EndLine need converting to land on a uniform 0-based
/// coordinate system end to end.
module DiagnosticMapping =

    let private mapSeverity (severity: FSharpDiagnosticSeverity) : DiagnosticSeverity =
        match severity with
        | FSharpDiagnosticSeverity.Error -> DiagnosticSeverity.Error
        | FSharpDiagnosticSeverity.Warning -> DiagnosticSeverity.Warning
        | FSharpDiagnosticSeverity.Info
        | FSharpDiagnosticSeverity.Hidden -> DiagnosticSeverity.Information

    let map (diagnostic: FSharpDiagnostic) : Diagnostic =
        { Message = diagnostic.Message
          Severity = mapSeverity diagnostic.Severity
          StartLine = diagnostic.StartLine - 1
          StartColumn = diagnostic.StartColumn
          EndLine = diagnostic.EndLine - 1
          EndColumn = diagnostic.EndColumn
          Code = if diagnostic.ErrorNumber = 0 then None else Some diagnostic.ErrorNumberText }
