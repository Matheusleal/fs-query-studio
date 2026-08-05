namespace FSharpQueryStudio.Protocol

/// `worker/health` result payload — see Section 7's method table.
type HealthInfo = { Version: string; Capabilities: string list }

/// `execution/evaluate` request payload — see Section 7's method table.
/// Isolated by default (Section 9): every call gets a fresh session in a
/// fresh working directory, there is no session identifier to reuse yet.
type EvaluateParams = { Source: string }

/// `analysis/check` request payload — see Section 7's method table.
type AnalysisCheckParams =
    { DocumentUri: string
      Version: int
      Source: string }

/// A diagnostic as FCS reports it, with no coordinate conversion into the
/// protocol-neutral `Diagnostic` shape yet — that mapping is a later task
/// (Section 8's explicit 1-based-FCS-vs-0-based-Monaco call-out).
type RawDiagnostic = { Message: string; Severity: string }

type AnalysisCheckResult = { Diagnostics: RawDiagnostic list }
