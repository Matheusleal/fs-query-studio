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

/// A function/method's signature, broken into individually-displayable
/// parameters — Milestone E.7's signature help. Resolved from the single
/// symbol at the requested position (same click-on-the-identifier model
/// as `AnalysisHoverParams`/`AnalysisDefinitionParams`), not from cursor
/// position inside an argument list — no active-parameter-index tracking
/// yet, unlike a full IDE's signature help popup.
type SignatureHelpInfo =
    { DisplayName: string
      Parameters: string list
      ReturnType: string }

/// `analysis/signatureHelp` request payload — see Section 7's method
/// table. Same shape and rationale as `AnalysisHoverParams`.
type AnalysisSignatureHelpParams =
    { DocumentUri: string
      Version: int
      Source: string
      Line: int
      Column: int }

/// `None` means the symbol at that position isn't callable (not a
/// function/method), or nothing resolved there at all — a normal,
/// successful `Result`, never an `Error`, same convention as
/// `AnalysisHoverResult`.
type AnalysisSignatureHelpResult = { SignatureHelp: SignatureHelpInfo option }

/// `analysis/complete` request payload — Milestone B.10/B.11's
/// completions. Same shape/rationale as `AnalysisHoverParams`.
type AnalysisCompletionParams =
    { DocumentUri: string
      Version: int
      Source: string
      Line: int
      Column: int }

/// Tier 1 (keywords/snippets/connector helper names) and tier 2
/// (semantic, FCS-derived at the cursor position) merged and
/// de-duplicated — see `Completions.complete`/`completeAtPosition`. A
/// flat name list, not yet Monaco's richer `CompletionItem` shape
/// (kind/detail/documentation) — `editor-host.html` maps each name to a
/// plain-text completion item; a later task can enrich this if
/// per-item kind/detail turns out to matter in practice.
type AnalysisCompletionResult = { Items: string list }

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
