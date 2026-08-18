namespace FSharpQueryStudio.Fsac

open System
open System.Collections.Concurrent
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks
open FSharpQueryStudio.Protocol

/// Translates the Worker's existing `analysis/*` payloads into LSP calls
/// against a shared `FsacProcess`, and maps LSP responses back into the
/// existing `Protocol` result types. `Handlers.fs` calls straight into
/// this module (Task 12) — no change to any wire type.
module FsacBridge =

    let private fsacDllPath () =
        Path.Combine(AppContext.BaseDirectory, "FsacTool", "fsautocomplete.dll")

    /// One shared `FsacProcess` for the Worker's whole lifetime, same
    /// "one long-lived instance" shape as `AnalysisContext.checker`.
    let sharedProcess = FsacProcess(fsacDllPath ())

    /// Every `documentUri` this Worker process has ever `didOpen`'d, so
    /// the next sync for that same uri sends `didChange` instead of a
    /// second `didOpen` (which LSP servers, including FSAC, reject for an
    /// already-open document).
    let private openDocuments = ConcurrentDictionary<string, bool>()

    let syncDocument (client: LspClient) (documentUri: string) (version: int) (checkedSource: string) : Task =
        if openDocuments.TryAdd(documentUri, true) then
            client.SendNotification(
                "textDocument/didOpen",
                {| textDocument =
                    {| uri = documentUri
                       languageId = "fsharp"
                       version = version
                       text = checkedSource |} |}
            )
        else
            client.SendNotification(
                "textDocument/didChange",
                {| textDocument = {| uri = documentUri; version = version |}
                   contentChanges = [ {| text = checkedSource |} ] |}
            )

    /// FSAC round-trips whatever `documentUri` this module sends in
    /// `didOpen`/`didChange` through its own absolute-path normalization
    /// before echoing it back in `publishDiagnostics` — confirmed against
    /// real fsautocomplete during this task: a `documentUri` with no
    /// drive letter (`file:///foo.fsx`, as this task's own tests use)
    /// came back with a lowercase, percent-encoded one inserted
    /// (`file:///d%3A/foo.fsx`). Stripping any single-letter-drive
    /// segment right after `file:///` — on both the uri this module sent
    /// and the uri FSAC echoes — makes the two compare equal regardless
    /// of which form either side happens to use.
    let private canonicalizeUri (uri: string) : string =
        let decoded = Uri.UnescapeDataString(uri)
        Regex.Replace(decoded, "^file:///[A-Za-z]:/", "file:///", RegexOptions.IgnoreCase)

    let private diagnosticsPending =
        ConcurrentDictionary<struct (string * int), TaskCompletionSource<Diagnostic list>>()

    /// The most recent `publishDiagnostics` push for each (canonicalized)
    /// uri. FSAC's own pushes carry no `version` — see
    /// `registerDiagnosticsHandler`'s doc comment — so this is keyed on
    /// uri alone and read back out once `fsharp/documentAnalyzed` confirms
    /// which version those diagnostics belong to.
    let private latestDiagnosticsByUri = ConcurrentDictionary<string, Diagnostic list>()

    let private mapSeverity (severity: int option) : DiagnosticSeverity =
        match severity with
        | Some 1 -> DiagnosticSeverity.Error
        | Some 2 -> DiagnosticSeverity.Warning
        | _ -> DiagnosticSeverity.Information

    let private parseDiagnostics (paramsEl: JsonElement) : Diagnostic list =
        paramsEl.GetProperty("diagnostics").EnumerateArray()
        |> Seq.map (fun d ->
            let range = d.GetProperty("range")
            let start = range.GetProperty("start")
            let end_ = range.GetProperty("end")

            let severity =
                match d.TryGetProperty("severity") with
                | true, s when s.ValueKind = JsonValueKind.Number -> Some(s.GetInt32())
                | _ -> None

            let code =
                match d.TryGetProperty("code") with
                | true, c when c.ValueKind = JsonValueKind.String -> Some(c.GetString())
                | true, c when c.ValueKind = JsonValueKind.Number -> Some(string (c.GetInt32()))
                | _ -> None

            { Message = d.GetProperty("message").GetString()
              Severity = mapSeverity severity
              StartLine = Prologue.shiftDown (start.GetProperty("line").GetInt32() + 1) - 1
              StartColumn = start.GetProperty("character").GetInt32()
              EndLine = Prologue.shiftDown (end_.GetProperty("line").GetInt32() + 1) - 1
              EndColumn = end_.GetProperty("character").GetInt32()
              Code = code })
        |> List.ofSeq

    /// Registered once, the first time any `FsacBridge` function runs
    /// against a live client — `EnsureClient` always returns the *same*
    /// `LspClient` instance while the underlying process is alive, so a
    /// second registration would just overwrite the first with an
    /// identical handler pair.
    ///
    /// The brief's original design correlated a pending `check` directly
    /// off `textDocument/publishDiagnostics`'s own `(uri, version)`. Real
    /// fsautocomplete 0.83.0 doesn't work that way: its
    /// `publishDiagnostics` push carries no `version` field at all
    /// (confirmed against the real process during this task — every
    /// notification's `params` was just `{uri, diagnostics}`), so there is
    /// no version to correlate against directly. What FSAC *does* send is
    /// its own custom `fsharp/documentAnalyzed` notification,
    /// `{textDocument: {uri, version}}`, once — per every run observed —
    /// *after* the `publishDiagnostics` push carrying that version's
    /// results. So `documentAnalyzed` is the actual "diagnostics for this
    /// (uri, version) are ready" signal `check` waits on here; the
    /// diagnostics themselves are read back out of the latest
    /// `publishDiagnostics` push seen for that uri. This still keeps the
    /// version-staleness guard the design calls for: a `documentAnalyzed`
    /// for a version nobody is waiting on (superseded, or unrelated) finds
    /// no pending entry and is dropped.
    let private registerDiagnosticsHandler (client: LspClient) =
        client.OnNotification(
            "textDocument/publishDiagnostics",
            fun paramsEl ->
                let uri = canonicalizeUri (paramsEl.GetProperty("uri").GetString())
                latestDiagnosticsByUri.[uri] <- parseDiagnostics paramsEl
        )

        client.OnNotification(
            "fsharp/documentAnalyzed",
            fun paramsEl ->
                let textDocument = paramsEl.GetProperty("textDocument")
                let uri = canonicalizeUri (textDocument.GetProperty("uri").GetString())
                let version = textDocument.GetProperty("version").GetInt32()

                match diagnosticsPending.TryRemove(struct (uri, version)) with
                | false, _ -> ()
                | true, tcs ->
                    let diagnostics =
                        match latestDiagnosticsByUri.TryGetValue(uri) with
                        | true, d -> d
                        | false, _ -> []

                    tcs.TrySetResult(diagnostics) |> ignore
        )

    let mutable private handlerRegistered = false

    let check (documentUri: string) (version: int) (source: string) : Task<Diagnostic list> =
        task {
            let! client = sharedProcess.EnsureClient()

            if not handlerRegistered then
                registerDiagnosticsHandler client
                handlerRegistered <- true

            let tcs = TaskCompletionSource<Diagnostic list>(TaskCreationOptions.RunContinuationsAsynchronously)
            let pendingKey = struct (canonicalizeUri documentUri, version)
            diagnosticsPending.[pendingKey] <- tcs

            do! syncDocument client documentUri version (Prologue.sourceForCheck source)

            let! winner = Task.WhenAny(tcs.Task :> Task, Task.Delay(TimeSpan.FromSeconds 10.0))

            if winner = (tcs.Task :> Task) then
                return! tcs.Task
            else
                diagnosticsPending.TryRemove(pendingKey) |> ignore
                return []
        }
