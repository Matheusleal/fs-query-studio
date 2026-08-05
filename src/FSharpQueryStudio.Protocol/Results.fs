namespace FSharpQueryStudio.Protocol

/// Bounded, protocol-neutral result shape — see Section 11. `Results`
/// produces these from raw evaluation outcomes; it doesn't own the types.
type ResultKind =
    | Scalar
    | Text
    | Table
    | Object
    | Error
    | NoValue

type Column = { Name: string; DisplayType: string }

type OutputStream =
    | Stdout
    | Stderr

type OutputLine = { Stream: OutputStream; Text: string }

type ResultEnvelope =
    { Kind: ResultKind
      DisplayText: string option
      Schema: Column list
      Rows: obj array array
      IsTruncated: bool
      ContinuationToken: string option
      ElapsedMs: int64
      Output: OutputLine list }
