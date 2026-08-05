namespace FSharpQueryStudio.Protocol

/// Messages sent from Monaco to the host — see Section 6. Carried inside
/// an `EditorEnvelope`'s `Payload`; `Editor.EditorBridge` does the
/// encode/decode, these are just the shapes.
type EditorRequest =
    | TextChanged of documentUri: string * version: int * source: string
    | CompletionRequested of documentUri: string * version: int * line: int * column: int
    | HoverRequested of documentUri: string * version: int * line: int * column: int
    | ExecuteRequested of documentUri: string * version: int * selection: string option
    | CancelRequested

/// Wire shape for each `EditorRequest` case's `EditorEnvelope.Payload`.
/// `CancelRequested` carries no fields, so it has none of these.
type TextChangedPayload =
    { DocumentUri: string
      Version: int
      Source: string }

type CompletionRequestedPayload =
    { DocumentUri: string
      Version: int
      Line: int
      Column: int }

type HoverRequestedPayload =
    { DocumentUri: string
      Version: int
      Line: int
      Column: int }

type ExecuteRequestedPayload =
    { DocumentUri: string
      Version: int
      Selection: string option }
