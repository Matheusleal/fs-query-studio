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

    /// Keyed on `(canonicalized uri, version)`, same as before - but now
    /// holding every waiter currently pending for that key, not just the
    /// most recent one. `check` and `hover` each register their own
    /// `TaskCompletionSource` here (via `awaitDocumentAnalyzed` below)
    /// while waiting for FSAC to finish parsing a document, and
    /// `Worker.Program.fs` backgrounds `analysis/check`/`analysis/hover`
    /// independently with no serialization between them - so a real
    /// concurrent `check` + `hover` call for the same document/version is
    /// entirely realistic. A plain indexer assignment (`.[key] <- tcs`)
    /// would silently evict whichever waiter registered first the moment
    /// a second one registers for the same key, leaving the evicted
    /// waiter's `Task.WhenAny` to always fall through to the timeout -
    /// for `check`, that means silently returning `[]` for a document
    /// that may genuinely have diagnostics. `registerWaiter`'s
    /// `AddOrUpdate` prepends instead, so every registered waiter is kept
    /// and resolved.
    let private diagnosticsPending =
        ConcurrentDictionary<struct (string * int), TaskCompletionSource<Diagnostic list> list>()

    let private registerWaiter (key: struct (string * int)) (tcs: TaskCompletionSource<Diagnostic list>) : unit =
        diagnosticsPending.AddOrUpdate(key, [ tcs ], (fun _ existing -> tcs :: existing)) |> ignore

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
                | true, waiters ->
                    let diagnostics =
                        match latestDiagnosticsByUri.TryGetValue(uri) with
                        | true, d -> d
                        | false, _ -> []

                    // Every waiter currently registered for this key gets
                    // the same result - not just the one that happened to
                    // register first. A waiter that already gave up and
                    // fell through to its own timeout is harmless to
                    // resolve again here: `TrySetResult` on an
                    // already-completed `TaskCompletionSource` just
                    // returns `false`.
                    waiters |> List.iter (fun tcs -> tcs.TrySetResult(diagnostics) |> ignore)
        )

    /// Every `documentUri` this Worker process has ever `didOpen`'d, so
    /// the next sync for that same uri sends `didChange` instead of a
    /// second `didOpen` (which LSP servers, including FSAC, reject for an
    /// already-open document). Cleared whenever `syncDocument` detects a
    /// respawned `LspClient` — see `isNewClient` — since a new FSAC
    /// process has no memory of any previously `didOpen`'d document.
    let private openDocuments = ConcurrentDictionary<string, bool>()

    let mutable private lastKnownClient: LspClient option = None

    /// Guards the "detect a respawned client, reset stale state, decide
    /// open-vs-change" sequence in `syncDocument` below as one atomic
    /// step. Confirmed as a real (not just theoretical) bug during this
    /// task: `check` and `hover` racing for *different* documents against
    /// the same `sharedProcess` (the same scenario Finding 1 above fixed
    /// for `diagnosticsPending`) can both observe `isNewClient client =
    /// true` right after a respawn, since the read of `lastKnownClient`
    /// and its eventual write are two separate steps with no
    /// synchronization between them. Without this lock, two such callers
    /// interleave their own `openDocuments.Clear()`/`TryAdd` calls
    /// unpredictably - one caller's `Clear()` can run after another's
    /// `TryAdd()`, silently forgetting that other document was ever
    /// opened. `registerDiagnosticsHandler` re-registering on the same
    /// client twice is harmless (both registrations are functionally
    /// identical closures over the same shared state), so only the
    /// state-mutating steps need to be atomic, not the notification send
    /// itself - the lock is released before `SendNotification` so no
    /// I/O ever happens while it's held.
    let private syncLock = obj ()

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
        let isFirstOpenForThisDocument =
            lock syncLock (fun () ->
                if isNewClient client then
                    openDocuments.Clear()
                    registerDiagnosticsHandler client
                    lastKnownClient <- Some client

                openDocuments.TryAdd(documentUri, true))

        if isFirstOpenForThisDocument then
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

    /// Sends `checkedSource` to FSAC (via `syncDocument`) and waits for
    /// `fsharp/documentAnalyzed` to confirm `(documentUri, version)` has
    /// actually been parsed, returning the diagnostics from that parse -
    /// `None` on the same 10-second timeout `check` always used, `Some`
    /// otherwise. This is the one place every current (`check`) and
    /// future (`hover`, Tasks 8-11) method waits for FSAC's initial parse
    /// of a document: `check` reads its `Diagnostic list` back directly,
    /// while a method that only needs the readiness signal (like `hover`)
    /// uses the `Option`'s presence alone and discards the payload on
    /// `Some`. Concurrent callers for the same `(documentUri, version)`
    /// (e.g. a `check` and a `hover` racing for the same document -
    /// `Worker.Program.fs` backgrounds them independently, so this is a
    /// real scenario, not a contrived one) are each registered via
    /// `registerWaiter` and each resolved individually when
    /// `documentAnalyzed` fires - see `diagnosticsPending`'s doc comment.
    let private awaitDocumentAnalyzed
        (client: LspClient)
        (documentUri: string)
        (version: int)
        (checkedSource: string)
        : Task<Diagnostic list option> =
        task {
            let tcs = TaskCompletionSource<Diagnostic list>(TaskCreationOptions.RunContinuationsAsynchronously)
            let pendingKey = struct (canonicalizeUri documentUri, version)
            registerWaiter pendingKey tcs

            do! syncDocument client documentUri version checkedSource

            let! winner = Task.WhenAny(tcs.Task :> Task, Task.Delay(TimeSpan.FromSeconds 10.0))

            if winner = (tcs.Task :> Task) then
                let! diagnostics = tcs.Task
                return Some diagnostics
            else
                return None
        }

    let check (documentUri: string) (version: int) (source: string) : Task<Diagnostic list> =
        task {
            let! client = sharedProcess.EnsureClient()
            let! diagnostics = awaitDocumentAnalyzed client documentUri version (Prologue.sourceForCheck source)
            return diagnostics |> Option.defaultValue []
        }

    /// A single `contents` array element's own text, independent of
    /// whether FSAC sent it as a plain markdown string or a
    /// `{language, value}` code-block object.
    let private elementText (el: JsonElement) : string =
        match el.ValueKind with
        | JsonValueKind.Object -> el.GetProperty("value").GetString()
        | JsonValueKind.String -> el.GetString()
        | _ -> ""

    /// `contents`, one text per array element - `[elementText contentsEl]`
    /// when `contents` isn't the array form FSAC always actually sends
    /// (a lone object/string instead), so callers don't need to
    /// special-case that shape separately.
    let private contentsElements (contentsEl: JsonElement) : string list =
        match contentsEl.ValueKind with
        | JsonValueKind.Array -> contentsEl.EnumerateArray() |> Seq.map elementText |> List.ofSeq
        | _ -> [ elementText contentsEl ]

    /// FSAC always appends this boilerplate to a hover response's later
    /// `contents` elements - the "Open the documentation" command link,
    /// and the "Full name"/"Assembly" footer lines - none of which is
    /// genuine XML-doc prose.
    let private isBoilerplateLine (line: string) : bool =
        line.StartsWith("<a href") || line.StartsWith("*Full name:") || line.StartsWith("*Assembly:")

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

    /// Best-effort split of FSAC's hover response into this app's
    /// `DisplayName`/`Type`/`Signature`/`XmlDoc` shape. Confirmed against
    /// real fsautocomplete 0.83.0 during this task: `contents` is a JSON
    /// array whose element 0 is always the `{language, value}` code
    /// block holding the symbol's own signature - itself keyword-prefixed
    /// (`val `/`member `/...) and, for a curried function/method,
    /// spanning several lines (one per parameter, e.g. `Connections.sql`'s
    /// `val sql:\n   name: string ->\n   ...`) - with any later elements
    /// being separate markdown strings (description prose, then FSAC's
    /// own boilerplate footer).
    ///
    /// An earlier revision of this function flattened the whole array
    /// into one string and re-derived the signature/doc boundary by
    /// scanning for a blank line. Real FSAC responses don't reliably put
    /// a blank line between the signature and the next array element -
    /// `Connections.sql`'s `"**Description**"` element sits directly
    /// adjacent to the signature's last line with no separator - so that
    /// re-derived boundary silently swallowed `"**Description**"` into
    /// the signature, corrupting `Type`/`Signature` with a trailing
    /// `"**Description**"` and dropping that heading from `XmlDoc`
    /// entirely. Using the array's own element boundaries instead of
    /// re-deriving them from whitespace avoids that: element 0 is always
    /// the signature, full stop, regardless of what markdown its
    /// neighbors happen to start with.
    let private parseHoverMarkdown (contentsEl: JsonElement) : HoverInfo =
        let signatureElement, docElements =
            match contentsElements contentsEl with
            | [] -> "", []
            | first :: rest -> first, rest

        let signatureText =
            signatureElement.Replace("\r\n", "\n").Split('\n')
            |> Array.map (fun l -> l.Trim())
            |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace l))
            |> String.concat " "
            |> fun s -> s.Trim('`', ' ')

        let docLines =
            docElements
            |> List.collect (fun el -> el.Replace("\r\n", "\n").Split('\n') |> Array.toList)
            |> List.filter (fun l -> not (String.IsNullOrWhiteSpace l))
            |> List.filter (fun l -> not (isBoilerplateLine l))

        let strippedSignature = stripSymbolKind signatureText
        let nameTypeSplit = strippedSignature.Split([| ':' |], 2)

        let displayName =
            if nameTypeSplit.Length = 2 then nameTypeSplit.[0].Trim() else strippedSignature.Trim()

        let typeText =
            if nameTypeSplit.Length = 2 then nameTypeSplit.[1].Trim() else signatureText

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
            // finished its own background parse of it yet. `awaitDocumentAnalyzed`
            // (shared with `check`, which needed the same wait for its own
            // diagnostics) is used here purely as a "the document is
            // parsed" gate - the diagnostics it returns are discarded.
            let! _ = awaitDocumentAnalyzed client documentUri version (Prologue.sourceForCheck source)

            let! result =
                client.SendRequest(
                    "textDocument/hover",
                    {| textDocument = {| uri = documentUri |}
                       position = {| line = Prologue.shiftUp line - 1; character = column |} |}
                )

            match result with
            | LspFail _ -> return None
            | LspOk resultEl when resultEl.ValueKind = JsonValueKind.Null -> return None
            | LspOk resultEl -> return Some(parseHoverMarkdown (resultEl.GetProperty("contents")))
        }

    /// LSP's `textDocument/definition` result is `Location | Location[] |
    /// LocationLink[] | null` - parses defensively and takes the first
    /// hit, matching today's "one resolved declaration" behavior.
    let private parseFirstLocation (resultEl: JsonElement) : (string * JsonElement) option =
        match resultEl.ValueKind with
        | JsonValueKind.Null -> None
        | JsonValueKind.Array when resultEl.GetArrayLength() = 0 -> None
        | JsonValueKind.Array ->
            let first = resultEl.[0]

            let rangeEl =
                match first.TryGetProperty("range") with
                | true, r -> r
                | false, _ -> first.GetProperty("targetRange") // LocationLink shape

            let uri =
                match first.TryGetProperty("uri") with
                | true, u -> u.GetString()
                | false, _ -> first.GetProperty("targetUri").GetString()

            Some(uri, rangeEl)
        | JsonValueKind.Object -> Some(resultEl.GetProperty("uri").GetString(), resultEl.GetProperty("range"))
        | _ -> None

    let definition
        (documentUri: string)
        (version: int)
        (source: string)
        (line: int)
        (column: int)
        : Task<DefinitionLocation option> =
        task {
            let! client = sharedProcess.EnsureClient()

            // Same race as `hover` (see its own comment above): a
            // `textDocument/definition` sent right after `syncDocument`
            // can land before FSAC's background parse of the just-synced
            // document finishes, producing a "No line in <path> at
            // position ..." LSP error even though the position math here
            // is correct. `awaitDocumentAnalyzed` is reused here purely as
            // that readiness gate - the diagnostics it returns are
            // discarded.
            let! _ = awaitDocumentAnalyzed client documentUri version (Prologue.sourceForCheck source)

            let! result =
                client.SendRequest(
                    "textDocument/definition",
                    {| textDocument = {| uri = documentUri |}
                       position = {| line = Prologue.shiftUp line - 1; character = column |} |}
                )

            match result with
            | LspFail _ -> return None
            | LspOk resultEl ->
                match parseFirstLocation resultEl with
                | None -> return None
                | Some(resolvedUri, range) ->
                    let start = range.GetProperty("start")
                    let end_ = range.GetProperty("end")

                    // Only a declaration inside *this* checked document was
                    // ever shifted by the prologue - one FSAC resolves in a
                    // different file (FSharp.Core, another script) has its
                    // own real, unshifted lines. `resolvedUri` for a
                    // same-file result is FSAC's own internal identity for
                    // `documentUri` (it always echoes back the uri we
                    // `didOpen`'d it under), so comparing against
                    // `documentUri` itself is the right same-file check -
                    // but, same as `registerDiagnosticsHandler`'s own uri
                    // comparisons, only once both sides are run through
                    // `canonicalizeUri`: confirmed against real
                    // fsautocomplete during this task, `resolvedUri` comes
                    // back drive-letter-qualified and percent-encoded
                    // (`file:///d%3A/foo.fsx`) even when `documentUri` was
                    // sent as the driveless `file:///foo.fsx` this task's
                    // own tests use, so a raw string comparison here always
                    // fails same-file detection. On a same-file match, the
                    // location returned to the caller still carries the
                    // caller's own original `documentUri` (not FSAC's
                    // echoed form) as `FilePath`.
                    let filePath, lineAdjustment =
                        if canonicalizeUri resolvedUri = canonicalizeUri documentUri then
                            documentUri, true
                        else
                            resolvedUri, false

                    let shift l = if lineAdjustment then Prologue.shiftDown (l + 1) - 1 else l

                    return
                        Some
                            { FilePath = filePath
                              StartLine = shift (start.GetProperty("line").GetInt32())
                              StartColumn = start.GetProperty("character").GetInt32()
                              EndLine = shift (end_.GetProperty("line").GetInt32())
                              EndColumn = end_.GetProperty("character").GetInt32() }
        }
