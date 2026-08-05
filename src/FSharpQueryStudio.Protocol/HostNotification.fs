namespace FSharpQueryStudio.Protocol

/// Messages sent from the host to Monaco — see Section 6. The opposite
/// direction of `EditorRequest`: carried inside an `EditorEnvelope`'s
/// `Payload`, `Editor.EditorBridge` does the encode side here, the JS side
/// in `editor-host.html` decodes and applies it (e.g. `setModelMarkers`
/// for `DiagnosticsUpdated`) — never the reverse, the host never decodes
/// its own notifications.
type HostNotification =
    | DiagnosticsUpdated of documentUri: string * diagnostics: Diagnostic list
    /// Moves Monaco's caret to a 0-based (line, column) — e.g. the Problems
    /// pane's "go to diagnostic" navigation (Section 8/5).
    | CaretMoved of documentUri: string * line: int * column: int

type DiagnosticsUpdatedPayload =
    { DocumentUri: string
      Diagnostics: Diagnostic list }

type CaretMovedPayload =
    { DocumentUri: string
      Line: int
      Column: int }
