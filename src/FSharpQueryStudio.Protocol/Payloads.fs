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

type AnalysisCheckResult = { Diagnostics: Diagnostic list }

/// `analysis/hover` result payload — see Section 7's method table and
/// Section 8's hover call-out (display name, inferred type, full
/// signature where available, XML doc when FCS can provide it).
type HoverInfo =
    { DisplayName: string
      Type: string
      Signature: string option
      XmlDoc: string option }

/// `analysis/hover` request payload — see Section 7's method table. Like
/// `analysis/check`, the worker is stateless across requests, so the
/// source snapshot travels with every request rather than being cached
/// worker-side.
type AnalysisHoverParams =
    { DocumentUri: string
      Version: int
      Source: string
      Line: int
      Column: int }

type AnalysisHoverResult = { Hover: HoverInfo option }

/// `results/page` request payload — see Section 7. `ContinuationToken`
/// is opaque and worker-issued, never constructed by the client; a
/// request against an unknown or expired token gets a structured error,
/// never a silent re-run of whatever produced the original result.
/// `SessionId` travels with the request per Section 7's shape, though
/// nothing worker-side validates it yet — the worker handles one
/// evaluation (and one open paging cursor) at a time, so there is only
/// ever one session's continuation state to look up.
type ResultPageRequest = { SessionId: string; ContinuationToken: string }

/// `results/page` result payload — see Section 7.
type ResultPage =
    { Rows: obj array array
      IsTruncated: bool
      ContinuationToken: string option }
