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

    /// Registered once per live `LspClient` — see `isNewClient`/
    /// `syncDocument` below, which are what actually decide when a
    /// (re-)registration is needed, since `FsacProcess.EnsureClient()`
    /// can hand back a brand new `LspClient` instance (with its own
    /// private `notificationHandlers`, unrelated to any previous one's)
    /// after a respawn.
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

    /// Every `documentUri` this Worker process has ever `didOpen`'d, so
    /// the next sync for that same uri sends `didChange` instead of a
    /// second `didOpen` (which LSP servers, including FSAC, reject for an
    /// already-open document). Cleared whenever `syncDocument` detects a
    /// respawned `LspClient` — see `isNewClient` — since a new FSAC
    /// process has no memory of any previously `didOpen`'d document.
    let private openDocuments = ConcurrentDictionary<string, bool>()

    let mutable private lastKnownClient: LspClient option = None

    /// True exactly when `client` is a different instance than the one
    /// this module last saw — i.e. `FsacProcess.EnsureClient()` has
    /// respawned after a crash (Task 4's recovery path, exercised by
    /// `FsacProcessTests`'s "respawns after the process is killed" test).
    /// Prior document-open state and any notification handlers registered
    /// on the old, now-dead `LspClient` are stale in that case (each
    /// `LspClient` owns its own private `notificationHandlers`) and must
    /// be re-established for the new one.
    let private isNewClient (client: LspClient) : bool =
        match lastKnownClient with
        | Some c -> not (obj.ReferenceEquals(box c, box client))
        | None -> true

    /// The one chokepoint every current (`check`) and future (Tasks
    /// 7-11's `hover`/`definition`/`signatureHelp`/`complete`/`rename`)
    /// method calls right after obtaining a client from `EnsureClient()`
    /// — so respawn recovery (re-registering notification handlers,
    /// forgetting stale `didOpen` state) is handled once here rather than
    /// duplicated in every method that needs it.
    let syncDocument (client: LspClient) (documentUri: string) (version: int) (checkedSource: string) : Task =
        if isNewClient client then
            openDocuments.Clear()
            registerDiagnosticsHandler client
            lastKnownClient <- Some client

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

    let check (documentUri: string) (version: int) (source: string) : Task<Diagnostic list> =
        task {
            let! client = sharedProcess.EnsureClient()

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

    let private markupText (contentsEl: JsonElement) : string =
        match contentsEl.ValueKind with
        | JsonValueKind.Object -> contentsEl.GetProperty("value").GetString()
        | JsonValueKind.String -> contentsEl.GetString()
        | JsonValueKind.Array ->
            contentsEl.EnumerateArray()
            |> Seq.map (fun el -> if el.ValueKind = JsonValueKind.String then el.GetString() else el.GetProperty("value").GetString())
            |> String.concat "\n"
        | _ -> ""

    /// FSAC's signature block always leads with one of these keywords
    /// before the symbol's own name - confirmed against real
    /// fsautocomplete during this task (e.g. `val total: int`, `val sql:
    /// name: string -> ...`). Longer, multi-word prefixes are listed
    /// before the single-word ones they'd otherwise be shadowed by.
    let private symbolKindPrefixes =
        [ "static member "
          "abstract member "
          "active pattern "
          "member "
          "property "
          "val "
          "type "
          "module "
          "namespace " ]

    let private stripSymbolKind (signature: string) : string =
        match symbolKindPrefixes |> List.tryFind signature.StartsWith with
        | Some prefix -> signature.Substring(prefix.Length)
        | None -> signature

    /// Best-effort split of FSAC's single markdown hover blob into this
    /// app's `DisplayName`/`Type`/`Signature`/`XmlDoc` shape. Confirmed
    /// against real fsautocomplete 0.83.0 during this task: unlike the
    /// plan's original assumption of a single-line `name: type` first
    /// line, FSAC's own first content block is a keyword-prefixed
    /// (`val `/`member `/...) signature that can itself span several
    /// lines - one per curried parameter, for a function/method (e.g.
    /// `Connections.sql`'s `val sql:\n   name: string ->\n   ...`).
    /// Every line up to the first blank line is treated as that
    /// signature; everything after is candidate documentation, with
    /// FSAC's own boilerplate footer (the "Open the documentation" link,
    /// "Full name"/"Assembly" lines) filtered back out since none of it
    /// is genuine XML-doc prose.
    let private parseHoverMarkdown (markdown: string) : HoverInfo =
        let lines = markdown.Replace("\r\n", "\n").Split('\n') |> Array.toList

        let signatureText =
            lines
            |> List.takeWhile (fun l -> not (String.IsNullOrWhiteSpace l))
            |> List.map (fun l -> l.Trim())
            |> String.concat " "
            |> fun s -> s.Trim('`', ' ')

        let docLines =
            lines
            |> List.skipWhile (fun l -> not (String.IsNullOrWhiteSpace l))
            |> List.skipWhile String.IsNullOrWhiteSpace
            |> List.filter (fun l -> not (String.IsNullOrWhiteSpace l))
            |> List.filter (fun l ->
                not (l.StartsWith("<a href"))
                && not (l.StartsWith("*Full name:"))
                && not (l.StartsWith("*Assembly:")))

        let nameTypeSplit = (stripSymbolKind signatureText).Split([| ':' |], 2)

        let displayName =
            if nameTypeSplit.Length = 2 then
                nameTypeSplit.[0].Trim()
            else
                (stripSymbolKind signatureText).Trim()

        let typeText =
            if nameTypeSplit.Length = 2 then
                nameTypeSplit.[1].Trim()
            else
                signatureText

        { DisplayName = displayName
          Type = typeText
          Signature = if signatureText.Contains("->") then Some signatureText else None
          XmlDoc = if docLines.IsEmpty then None else Some(docLines |> String.concat "\n") }

    let hover (documentUri: string) (version: int) (source: string) (line: int) (column: int) : Task<HoverInfo option> =
        task {
            let! client = sharedProcess.EnsureClient()

            // FSAC's response to `textDocument/hover` sent immediately
            // after a fresh `didOpen` reflects a not-yet-parsed document -
            // confirmed against real fsautocomplete during this task: a
            // hover request sent right after `syncDocument` for a
            // brand-new uri failed with "No line in <path> at position
            // ..." even though the document's full (prologue-prefixed)
            // text had genuinely already been sent, because FSAC hadn't
            // finished its own background parse of it yet. `check` already
            // solved this exact problem for diagnostics by waiting on
            // `fsharp/documentAnalyzed`; hover reuses that same signal (via
            // the shared `diagnosticsPending` map) purely as a "the
            // document is parsed" gate, ignoring the diagnostics payload
            // itself.
            let tcs = TaskCompletionSource<Diagnostic list>(TaskCreationOptions.RunContinuationsAsynchronously)
            let pendingKey = struct (canonicalizeUri documentUri, version)
            diagnosticsPending.[pendingKey] <- tcs

            do! syncDocument client documentUri version (Prologue.sourceForCheck source)

            let! winner = Task.WhenAny(tcs.Task :> Task, Task.Delay(TimeSpan.FromSeconds 10.0))

            if winner <> (tcs.Task :> Task) then
                diagnosticsPending.TryRemove(pendingKey) |> ignore

            let! result =
                client.SendRequest(
                    "textDocument/hover",
                    {| textDocument = {| uri = documentUri |}
                       position = {| line = Prologue.shiftUp line - 1; character = column |} |}
                )

            match result with
            | LspFail _ -> return None
            | LspOk resultEl when resultEl.ValueKind = JsonValueKind.Null -> return None
            | LspOk resultEl -> return Some(parseHoverMarkdown (markupText (resultEl.GetProperty("contents"))))
        }
