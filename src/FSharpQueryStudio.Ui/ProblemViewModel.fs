namespace FSharpQueryStudio.Ui

open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Editor

/// One row in the Problems pane — see Section 5. `sendToEditor` is the
/// composition root's hook into the real WebView (same optional-function
/// pattern as `ShellViewModel.ExecuteAsync`/`CancelAsync`); `Navigate` is
/// a no-op if it's never configured.
type ProblemViewModel(documentUri: string, diagnostic: Diagnostic, sendToEditor: (EditorEnvelope -> unit) option) =
    member _.Diagnostic = diagnostic

    /// Moves the editor caret to this diagnostic's start position.
    member _.Navigate() =
        match sendToEditor with
        | None -> ()
        | Some send ->
            let envelope =
                EditorBridge.encodeNotification (CaretMoved(documentUri, diagnostic.StartLine, diagnostic.StartColumn))

            send envelope
