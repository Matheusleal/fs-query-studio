namespace FSharpQueryStudio.Protocol

/// `worker/health` result payload — see Section 7's method table.
type HealthInfo = { Version: string; Capabilities: string list }

/// Selects whether `execution/evaluate` uses a fresh session (`Isolated`,
/// the Milestone A default) or reuses a session tracked worker-side by
/// `EvaluateParams.SessionId` across calls (`Persistent`) — see the
/// architecture doc's "Preparing for persistent sessions" table.
/// `Persistent` without a `SessionId` falls back to an isolated session
/// (logged as a warning) rather than failing the request.
type SessionMode =
    | Isolated
    | Persistent

/// `execution/evaluate` request payload — see Section 7's method table.
/// `Mode` defaults to `Isolated`: every call gets a fresh session in a
/// fresh working directory. `SessionId` only matters for `Persistent`,
/// where it keys the worker-side session to reuse or create.
type EvaluateParams =
    { Source: string
      SessionId: string option
      Mode: SessionMode }

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

/// A resolved symbol declaration's location — Milestone E.6's go-to-
/// definition. `FilePath` may point outside the requesting document
/// (e.g. another file in an E.5 multi-file project); lines are 0-based,
/// matching `Diagnostic`'s Monaco-facing convention.
type DefinitionLocation =
    { FilePath: string
      StartLine: int
      StartColumn: int
      EndLine: int
      EndColumn: int }

/// `analysis/definition` request payload — see Section 7's method table.
/// Same shape and rationale as `AnalysisHoverParams`: the worker is
/// stateless across requests, so the source snapshot travels with every
/// request rather than being cached worker-side.
type AnalysisDefinitionParams =
    { DocumentUri: string
      Version: int
      Source: string
      Line: int
      Column: int }

/// `None` means FCS found no resolvable declaration at that position
/// (e.g. a keyword, a syntax error, or whitespace) — a normal, successful
/// `Result`, never an `Error`, same convention as `AnalysisHoverResult`.
type AnalysisDefinitionResult = { Location: DefinitionLocation option }

/// `session/reset` request payload — see Section 7's method table. Drops
/// the worker-side Persistent-mode session tracked under `SessionId`, if
/// any; a no-op if nothing was tracked (never started, already reset, or
/// was always Isolated).
type SessionResetParams = { SessionId: string }

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
