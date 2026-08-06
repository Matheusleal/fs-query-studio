namespace FSharpQueryStudio.Protocol

open System

/// How a workspace/script came to be on this machine — Section 13's
/// origin metadata.
type ScriptOrigin =
    | Local
    | Imported
    | Downloaded
    | SharedWorkspace

/// Section 13's trust state for a workspace/script. v1 is read/written/
/// displayed only — never enforced.
type TrustState =
    | Unknown
    | Reviewed
    | Trusted
    | Blocked

/// `trust.json` v1 schema — Section 13. Capability strings follow that
/// section's vocabulary (`"filesystem-read"`, `"network"`,
/// `"connection:<name>"`, ...) but are stored as plain strings since the
/// full capability set is still evolving.
type TrustMetadata =
    { Origin: ScriptOrigin
      TrustState: TrustState
      RequestedCapabilities: string list
      ReviewedAt: DateTimeOffset option
      ReviewedBy: string option }
