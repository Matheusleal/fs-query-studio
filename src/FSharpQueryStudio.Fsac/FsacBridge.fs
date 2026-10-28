namespace FSharpQueryStudio.Fsac

open System
open System.Collections.Concurrent
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks
open Serilog
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
    let sharedProcess = new FsacProcess(fsacDllPath ())

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
        let decoded = Uri.UnescapeDataString(uri).Replace('\\', '/')
        Regex.Replace(decoded, "^file:///[A-Za-z]:/", "file:///", RegexOptions.IgnoreCase).ToLowerInvariant()

    /// A this-Worker-process-unique base directory synthetic FSAC-facing
    /// uris (see `fsacUriFor`) are namespaced under, so two different
    /// Worker processes - or two callers who happen to name a document
    /// the same thing - never collide.
    let private untitledUriRoot =
        Path.Combine(Path.GetTempPath(), "fqs-fsac-untitled-" + Guid.NewGuid().ToString("N"))

    /// FSAC only round-trips a `file://` document identity predictably
    /// (see `canonicalizeUri`'s own drive-letter case above). A
    /// non-`file://` uri - in practice always `untitled:<name>` here,
    /// Monaco's own convention for an unsaved buffer and this app's own
    /// default identity for *every* freshly opened script tab
    /// (`ShellViewModel.createDocument "untitled:Script1.fsx"`) - gets
    /// silently rewritten by FSAC into an unrelated synthetic `file://`
    /// path of its own devising: confirmed against real fsautocomplete
    /// 0.83.0, `didOpen`ing `untitled:Script1.fsx` produced
    /// `publishDiagnostics`/`documentAnalyzed` notifications echoing back
    /// `file:///<fsac's own cwd>/Script1.fsx.fsx` (note the doubled
    /// `.fsx.fsx` - FSAC's own untitled-buffer naming, not anything this
    /// module controls). Nothing about that transform is derivable from
    /// the caller's own uri string - it depends on FSAC's own working
    /// directory and internal naming - so `canonicalizeUri` (built for
    /// the file://-drive-letter-echo case, where the echoed uri really
    /// is a deterministic function of the one sent) can never bridge it.
    /// Left alone, every `diagnosticsPending`/`awaitDocumentAnalyzed`
    /// wait keyed on the caller's own `untitled:...` uri never sees
    /// FSAC's differently-shaped echo match, so every `analysis/*` call
    /// against any not-yet-saved script - this app's own default state
    /// on every fresh launch and every "new script" action - always
    /// times out, not just intermittently.
    ///
    /// Fix: never hand FSAC anything but a `file://` uri this module
    /// controls end to end. For a non-`file://` caller uri, synthesize
    /// one of our own - deterministic (the same caller uri always maps
    /// to the same synthetic path, so a later `didChange` lands on the
    /// same document FSAC already has open) and namespaced under
    /// `untitledUriRoot`. Everywhere this module talks to FSAC about a
    /// document's identity - the wire `uri` in a request, and every
    /// `diagnosticsPending`/`canonicalizeUri` correlation key - goes
    /// through this first. The *caller's* own original uri is what ever
    /// reaches the wire back out (`DefinitionLocation.FilePath`, etc.);
    /// this synthetic identity never leaks past this module.
    /// Public only so `FsacBridgeUriTests` can assert the synthetic uri is
    /// a well-formed `file://` one (no `#`/`%` to break uri parsing, and a
    /// `.fsx` extension) without a real FSAC round trip; nothing outside
    /// this module calls it in production.
    let fsacUriFor (documentUri: string) : string =
        if documentUri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) then
            documentUri
        else
            let rawName =
                match documentUri.IndexOf(':') with
                | -1 -> documentUri
                | i -> documentUri.Substring(i + 1)

            // One path segment safe to sit inside a `file://` uri: every
            // character outside `[A-Za-z0-9._-]` becomes `_`. Deliberately
            // stricter than `Path.GetInvalidFileNameChars()` - a
            // `vscode-notebook-cell:` uri (the VS Code extension's cell
            // identity) carries a `#<base64 cell handle>` fragment and
            // `%`-encoded path segments, and neither `#` nor `%` is an
            // invalid *filename* char on Windows, yet both break `file://`
            // uri parsing: FSAC reads the `#` as a fragment delimiter,
            // drops everything after it (including the `.fsx` suffix that
            // marks the buffer as an F# script), and so never analyses the
            // document - every `check` then times out and returns `[]`.
            let sanitized =
                String(
                    rawName.ToCharArray()
                    |> Array.map (fun c ->
                        if
                            (c >= 'A' && c <= 'Z')
                            || (c >= 'a' && c <= 'z')
                            || (c >= '0' && c <= '9')
                            || c = '.'
                            || c = '_'
                            || c = '-'
                        then
                            c
                        else
                            '_')
                )

            // Guarantee the `.fsx` extension FSAC keys script analysis on,
            // even for a caller uri whose own suffix was mangled above or
            // never had one.
            let safeName =
                if sanitized.EndsWith(".fsx", StringComparison.OrdinalIgnoreCase) then
                    sanitized
                else
                    sanitized + ".fsx"

            let fullPath = Path.Combine(untitledUriRoot, safeName).Replace('\\', '/')
            $"file:///{fullPath.TrimStart('/')}"

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

    let private originalUriByFsacUri = ConcurrentDictionary<string, string>()

    let private latestExpectedVersionByUri = ConcurrentDictionary<string, int>()

    /// The most recent `publishDiagnostics` push for each (canonicalized uri, version).
    let private latestDiagnosticsByUriAndVersion =
        ConcurrentDictionary<struct (string * int), Diagnostic list>()

    let private mapSeverity (severity: int option) : DiagnosticSeverity =
        match severity with
        | Some 1 -> DiagnosticSeverity.Error
        | Some 2 -> DiagnosticSeverity.Warning
        | _ -> DiagnosticSeverity.Information

    let private parseDiagnostics (originalDocUri: string) (paramsEl: JsonElement) : Diagnostic list =
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
              StartLine = Prologue.shiftDownForDocument originalDocUri (start.GetProperty("line").GetInt32() + 1) - 1
              StartColumn = start.GetProperty("character").GetInt32()
              EndLine = Prologue.shiftDownForDocument originalDocUri (end_.GetProperty("line").GetInt32() + 1) - 1
              EndColumn = end_.GetProperty("character").GetInt32()
              Code = code })
        |> List.ofSeq

    /// Registered once per live `LspClient` — see `isNewClient`/
    /// `syncDocument` below, which are what actually decide when a
    /// (re-)registration is needed, since `FsacProcess.EnsureClient()`
    /// can hand back a brand new `LspClient` instance (with its own
    /// private `notificationHandlers`, unrelated to any previous one's)
    /// after a respawn.
    let private registerDiagnosticsHandler (client: LspClient) =
        client.OnNotification(
            "textDocument/publishDiagnostics",
            fun paramsEl ->
                let uri = canonicalizeUri (paramsEl.GetProperty("uri").GetString())
                let originalDocUri =
                    match originalUriByFsacUri.TryGetValue(uri) with
                    | true, orig -> orig
                    | false, _ -> uri
                let diagnostics = parseDiagnostics originalDocUri paramsEl

                let version =
                    match paramsEl.TryGetProperty("version") with
                    | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
                    | _ ->
                        match latestExpectedVersionByUri.TryGetValue(uri) with
                        | true, v -> v
                        | false, _ -> 1

                let key = struct (uri, version)
                latestDiagnosticsByUriAndVersion.[key] <- diagnostics
        )

        client.OnNotification(
            "fsharp/documentAnalyzed",
            fun paramsEl ->
                let textDocument = paramsEl.GetProperty("textDocument")
                let uri = canonicalizeUri (textDocument.GetProperty("uri").GetString())
                let version = textDocument.GetProperty("version").GetInt32()
                let key = struct (uri, version)

                match diagnosticsPending.TryRemove(key) with
                | false, _ -> ()
                | true, waiters ->
                    // FSAC fires documentAnalyzed when compiler AST analysis completes for this version.
                    // Post-analysis publishDiagnostics (including linter/analyzers) land on the wire
                    // within ~10-20ms. Delay 100ms so that the final diagnostics for this version
                    // overwrite any in-flight trailing diagnostics from earlier versions:
                    Task.Delay(100).ContinueWith(fun (_: Task) ->
                        let finalDiags =
                            match latestDiagnosticsByUriAndVersion.TryGetValue(key) with
                            | true, d -> d
                            | false, _ -> []
                        waiters |> List.iter (fun tcs -> tcs.TrySetResult(finalDiags) |> ignore)
                    ) |> ignore
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
        // `openDocuments` is keyed on the caller's own `documentUri`, not
        // the FSAC-facing `wireUri` - `fsacUriFor` is a pure, deterministic
        // function of `documentUri`, so tracking "have we opened *this*
        // caller identity yet" this way still correctly decides
        // didOpen-vs-didChange without needing to track the synthetic
        // identity separately.
        let wireUri = fsacUriFor documentUri
        let canonicalUri = canonicalizeUri wireUri

        let isFirstOpenForThisDocument =
            lock syncLock (fun () ->
                if isNewClient client then
                    openDocuments.Clear()
                    latestExpectedVersionByUri.Clear()
                    latestDiagnosticsByUriAndVersion.Clear()
                    registerDiagnosticsHandler client
                    lastKnownClient <- Some client

                latestExpectedVersionByUri.[canonicalUri] <- version
                latestDiagnosticsByUriAndVersion.[struct (canonicalUri, version)] <- []
                openDocuments.TryAdd(documentUri, true))

        if isFirstOpenForThisDocument then
            client.SendNotification(
                "textDocument/didOpen",
                {| textDocument =
                    {| uri = wireUri
                       languageId = "fsharp"
                       version = version
                       text = checkedSource |} |}
            )
        else
            client.SendNotification(
                "textDocument/didChange",
                {| textDocument = {| uri = wireUri; version = version |}
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
            // Keyed on the FSAC-facing identity (`fsacUriFor`), not the
            // caller's own `documentUri` - `documentAnalyzed`'s own uri
            // always canonicalizes to the same thing, so the two must
            // agree here or this wait can never resolve except by timeout.
            let canonical = canonicalizeUri (fsacUriFor documentUri)
            originalUriByFsacUri.[canonical] <- documentUri
            let pendingKey = struct (canonical, version)
            registerWaiter pendingKey tcs

            do! syncDocument client documentUri version checkedSource

            let! winner = Task.WhenAny(tcs.Task :> Task, Task.Delay(TimeSpan.FromSeconds 10.0))

            if winner = (tcs.Task :> Task) then
                let! diagnostics = tcs.Task
                // Allow any trailing analyzer notification from FSAC for this parse
                // to settle so it does not bleed into a subsequent check for the same uri:
                do! Task.Delay(50)
                return Some diagnostics
            else
                // Logged here rather than at each of this function's callers
                // (`check` reads the `None` back directly; `hover`/
                // `definition`/`signatureHelp`/`complete`/`rename` only use
                // this as a readiness gate and discard the result) so every
                // caller's silent "nothing came back" gets one warning, not
                // zero or five duplicated ones.
                Log.Warning(
                    "Timed out after 10s waiting for FSAC to finish parsing {DocumentUri} (version {Version})",
                    documentUri,
                    version
                )

                return None
        }

    /// A reasonable upper bound on how long a live-but-stuck `fsautocomplete`
    /// (one that hasn't crashed — `HasExited` still `false` — but also isn't
    /// answering) is allowed to leave a request pending, consistent with
    /// `awaitDocumentAnalyzed`'s own 10s bound and `FsacProcess.EnsureClient`'s
    /// 15s handshake bound just above it in the same pipeline. A crashed
    /// process is already handled without this (the reader loop detects
    /// stdout EOF and resolves every pending request with `LspFail` as soon
    /// as it happens); this bound only ever matters for a process that's
    /// alive but unresponsive.
    let private requestTimeout = TimeSpan.FromSeconds 15.0

    /// Wraps `client.SendRequest` with `requestTimeout` so a hung-but-alive
    /// FSAC can't leave a caller waiting forever - the five methods below
    /// that call an actual `textDocument/*` request (`check` doesn't; its
    /// only wait is `awaitDocumentAnalyzed`'s own bound above) all go
    /// through this rather than calling `client.SendRequest` directly.
    let private sendRequestWithTimeout
        (client: LspClient)
        (method_: string)
        (requestParams: 'TParams)
        : Task<LspResult> =
        task {
            let requestTask = client.SendRequest(method_, requestParams)
            let! winner = Task.WhenAny(requestTask, Task.Delay(requestTimeout))

            if winner = (requestTask :> Task) then
                return! requestTask
            else
                Log.Warning(
                    "FSAC request {Method} timed out after {TimeoutSeconds}s",
                    method_,
                    requestTimeout.TotalSeconds
                )

                return LspFail { Code = -1; Message = $"FSAC request '{method_}' timed out after {requestTimeout.TotalSeconds}s" }
        }

    let check (documentUri: string) (version: int) (source: string) : Task<Diagnostic list> =
        task {
            let! client = sharedProcess.EnsureClient()
            let! diagnostics = awaitDocumentAnalyzed client documentUri version (Prologue.sourceForCheckForDocument documentUri source)
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
            let! _ = awaitDocumentAnalyzed client documentUri version (Prologue.sourceForCheckForDocument documentUri source)

            let! result =
                sendRequestWithTimeout
                    client
                    "textDocument/hover"
                    {| textDocument = {| uri = fsacUriFor documentUri |}
                       position = {| line = Prologue.shiftUpForDocument documentUri line - 1; character = column |} |}

            match result with
            | LspFail err ->
                Log.Warning(
                    "FSAC hover failed for {DocumentUri} at ({Line},{Column}): [{Code}] {Message}",
                    documentUri,
                    line,
                    column,
                    err.Code,
                    err.Message
                )

                return None
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
            let! _ = awaitDocumentAnalyzed client documentUri version (Prologue.sourceForCheckForDocument documentUri source)

            let! result =
                sendRequestWithTimeout
                    client
                    "textDocument/definition"
                    {| textDocument = {| uri = fsacUriFor documentUri |}
                       position = {| line = Prologue.shiftUpForDocument documentUri line - 1; character = column |} |}

            match result with
            | LspFail err ->
                Log.Warning(
                    "FSAC definition lookup failed for {DocumentUri} at ({Line},{Column}): [{Code}] {Message}",
                    documentUri,
                    line,
                    column,
                    err.Code,
                    err.Message
                )

                return None
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
                        if String.Equals(canonicalizeUri resolvedUri, canonicalizeUri (fsacUriFor documentUri), StringComparison.OrdinalIgnoreCase) then
                            documentUri, true
                        else
                            resolvedUri, false

                    let shift l = if lineAdjustment then Prologue.shiftDownForDocument documentUri (l + 1) - 1 else l

                    return
                        Some
                            { FilePath = filePath
                              StartLine = shift (start.GetProperty("line").GetInt32())
                              StartColumn = start.GetProperty("character").GetInt32()
                              EndLine = shift (end_.GetProperty("line").GetInt32())
                              EndColumn = end_.GetProperty("character").GetInt32() }
        }

    /// Splits `text` on a top-level `"->"` only - one not nested inside
    /// parentheses - rather than every literal `"->"` occurrence. A naive
    /// split breaks on *every* `"->"`, which fragments a parameter that is
    /// itself a function type: for `apply`'s real signature-help label
    /// `"f: (int -> int) -> x: int -> int"`, a plain
    /// `Split([|"->"|], ...)` produces `["f: (int"; "int)"; "x: int";
    /// "int"]` - the higher-order parameter `f: (int -> int)` torn in two
    /// - rather than the correct `["f: (int -> int)"; "x: int"; "int"]`.
    /// This is not an exotic case: any callback-taking API (`List.map`,
    /// `Seq.filter`, `Array.iter`, ...) has exactly this shape. Tracking
    /// paren depth while scanning left to right and only treating `"->"`
    /// as a separator at depth 0 keeps a parenthesized function-type
    /// parameter intact as one segment.
    let private splitTopLevelArrows (text: string) : string list =
        let segments = ResizeArray<string>()
        let current = System.Text.StringBuilder()
        let mutable depth = 0
        let mutable i = 0

        while i < text.Length do
            if text.[i] = '(' then
                depth <- depth + 1
                current.Append(text.[i]) |> ignore
                i <- i + 1
            elif text.[i] = ')' then
                depth <- depth - 1
                current.Append(text.[i]) |> ignore
                i <- i + 1
            elif depth = 0 && i + 1 < text.Length && text.[i] = '-' && text.[i + 1] = '>' then
                segments.Add(current.ToString())
                current.Clear() |> ignore
                i <- i + 2
            else
                current.Append(text.[i]) |> ignore
                i <- i + 1

        segments.Add(current.ToString())
        segments |> Seq.map (fun s -> s.Trim()) |> List.ofSeq

    let signatureHelp
        (documentUri: string)
        (version: int)
        (source: string)
        (line: int)
        (column: int)
        : Task<SignatureHelpInfo option> =
        task {
            let! client = sharedProcess.EnsureClient()

            // Same race as `hover`/`definition` (see their own comments
            // above): a `textDocument/signatureHelp` sent right after
            // `syncDocument` can land before FSAC's background parse of
            // the just-synced document finishes. `awaitDocumentAnalyzed`
            // is reused here purely as that readiness gate - the
            // diagnostics it returns are discarded.
            let! _ = awaitDocumentAnalyzed client documentUri version (Prologue.sourceForCheckForDocument documentUri source)

            let! result =
                sendRequestWithTimeout
                    client
                    "textDocument/signatureHelp"
                    {| textDocument = {| uri = fsacUriFor documentUri |}
                       position = {| line = Prologue.shiftUpForDocument documentUri line - 1; character = column |} |}

            match result with
            | LspFail err ->
                Log.Warning(
                    "FSAC signatureHelp failed for {DocumentUri} at ({Line},{Column}): [{Code}] {Message}",
                    documentUri,
                    line,
                    column,
                    err.Code,
                    err.Message
                )

                return None
            | LspOk resultEl when resultEl.ValueKind = JsonValueKind.Null -> return None
            | LspOk resultEl ->
                let signatures = resultEl.GetProperty("signatures")

                if signatures.GetArrayLength() = 0 then
                    return None
                else
                    let sig_ = signatures.[0]
                    let label = sig_.GetProperty("label").GetString()

                    // FSAC's `label` for a function/value signature is its
                    // full curried type text - confirmed against real
                    // fsautocomplete 0.83.0: querying inside a call to
                    // `let add (x: int) (y: int) : int = x + y` returns
                    // `label = "val add: x: int -> y: int -> int"`,
                    // keyword-prefixed the same way `hover`'s signature
                    // text is (`stripSymbolKind`), not the parenthesized
                    // `name(p1: T1, p2: T2) -> TReturn` shape this module
                    // originally assumed. The response's own structured
                    // `parameters` array turned out to only ever carry the
                    // single argument position closest to the request's
                    // cursor (e.g. querying right after `add ` returned
                    // just `[{label = "x"}]`, never `y` too, even though
                    // `add` takes two) rather than every parameter the
                    // function takes, so it's unusable for a "give me the
                    // whole signature" result - every parameter is instead
                    // recovered by splitting `label`'s curried arrow-type
                    // text on top-level `"->"` ourselves
                    // (`splitTopLevelArrows` above - a plain `Split` breaks
                    // on a higher-order parameter's own `"->"`, e.g.
                    // `apply`'s real `"f: (int -> int) -> x: int -> int"`).
                    let stripped = stripSymbolKind label
                    let nameTypeSplit = stripped.Split([| ':' |], 2)

                    let displayName =
                        if nameTypeSplit.Length = 2 then
                            nameTypeSplit.[0].Trim()
                        else
                            stripped.Trim()

                    let typeText =
                        if nameTypeSplit.Length = 2 then
                            nameTypeSplit.[1].Trim()
                        else
                            stripped.Trim()

                    let arrowSegments = splitTopLevelArrows typeText

                    if arrowSegments.Length < 2 then
                        // A resolved-but-not-callable symbol (its type has
                        // no top-level `->` at all) would land here - but
                        // real fsautocomplete 0.83.0 appears to never
                        // actually produce that shape: it only returns a
                        // non-null `signatureHelp` result at all once the
                        // callee genuinely resolves to a function/method
                        // type at that call site. Confirmed against two
                        // real non-callable-application attempts during
                        // this task - a plain value applied like a call
                        // (`total 1` on `let total = 42`) and an
                        // over-application of an already-fully-applied
                        // curried function (`add 1 2 3` on a two-arg
                        // `add`) - both came back `null` (caught by the
                        // branch above), never a non-null response with an
                        // arrow-less `label`. This branch is kept purely
                        // as defensive coding against that assumption ever
                        // turning out wrong in some FSAC version/scenario
                        // not exercised here.
                        return None
                    else
                        let parameters = arrowSegments |> List.take (arrowSegments.Length - 1)
                        let returnType = arrowSegments |> List.last

                        return
                            Some
                                { DisplayName = displayName
                                  Parameters = parameters
                                  ReturnType = returnType }
        }

    /// LSP's `textDocument/completion` result is `CompletionItem[] |
    /// CompletionList | null` - parses defensively and extracts both `label`
    /// and `detail` (type/signature).
    let private simplifyTypeName (t: string) : string =
        let trimmed = t.Trim()
        match trimmed with
        | "System.String" | "String" -> "string"
        | "System.Int32" | "Int32" -> "int"
        | "System.Int64" | "Int64" -> "int64"
        | "System.Int16" | "Int16" -> "int16"
        | "System.Boolean" | "Boolean" -> "bool"
        | "System.Decimal" | "Decimal" -> "decimal"
        | "System.Double" | "Double" -> "float"
        | "System.Single" | "Single" -> "float32"
        | "System.Byte" | "Byte" -> "byte"
        | "System.DateTime" -> "DateTime"
        | "System.Guid" -> "Guid"
        | "System.Object" | "Object" -> "obj"
        | other -> other

    let private extractTypeFromDetail (detail: string) : string =
        let clean =
            let idx = detail.LastIndexOf(':')
            if idx >= 0 && idx + 1 < detail.Length then
                let afterColon = detail.Substring(idx + 1).Trim()
                let withIdx = afterColon.IndexOf("with", StringComparison.OrdinalIgnoreCase)
                let withoutWith =
                    if withIdx > 0 then afterColon.Substring(0, withIdx).Trim()
                    else afterColon
                withoutWith
            else
                detail.Trim()
        let finalType = if String.IsNullOrWhiteSpace clean then detail else clean
        simplifyTypeName finalType

    let private parseItem (el: JsonElement) : AnalysisCompletionItem option =
        let labelOpt =
            match el.TryGetProperty("label") with
            | true, l when l.ValueKind = JsonValueKind.String -> Some(l.GetString())
            | true, l when l.ValueKind = JsonValueKind.Object ->
                match l.TryGetProperty("label") with
                | true, inner when inner.ValueKind = JsonValueKind.String -> Some(inner.GetString())
                | _ -> None
            | _ -> None

        match labelOpt with
        | None -> None
        | Some label ->
            let kindOpt =
                match el.TryGetProperty("kind") with
                | true, k when k.ValueKind = JsonValueKind.Number -> Some(k.GetInt32())
                | _ -> None

            let rawDetailOpt =
                match el.TryGetProperty("detail") with
                | true, d when d.ValueKind = JsonValueKind.String ->
                    let s = d.GetString()
                    if String.IsNullOrWhiteSpace s then None else Some(s.Trim())
                | _ ->
                    match el.TryGetProperty("label") with
                    | true, l when l.ValueKind = JsonValueKind.Object ->
                        match l.TryGetProperty("description") with
                        | true, desc when desc.ValueKind = JsonValueKind.String ->
                            let s = desc.GetString()
                            if String.IsNullOrWhiteSpace s then None else Some(s.Trim())
                        | _ -> None
                    | _ -> None

            let detail =
                match rawDetailOpt with
                | Some rawDetail ->
                    match kindOpt with
                    | Some 5 | Some 10 | Some 6 | Some 12 ->
                        Some(extractTypeFromDetail rawDetail)
                    | Some 2 ->
                        Some "method"
                    | _ ->
                        if rawDetail.Contains(":") then
                            Some(extractTypeFromDetail rawDetail)
                        else
                            Some rawDetail
                | None ->
                    match kindOpt with
                    | Some 1 -> Some "text"
                    | Some 2 -> Some "method"
                    | Some 3 -> Some "function"
                    | Some 4 -> Some "constructor"
                    | Some 5 -> Some "field"
                    | Some 6 -> Some "variable"
                    | Some 7 -> Some "class"
                    | Some 8 -> Some "interface"
                    | Some 9 -> Some "module"
                    | Some 10 -> Some "property"
                    | Some 11 -> Some "unit"
                    | Some 12 -> Some "value"
                    | Some 13 -> Some "enum"
                    | Some 14 -> Some "keyword"
                    | Some 15 -> Some "snippet"
                    | Some 21 -> Some "constant"
                    | Some 22 -> Some "struct"
                    | Some 23 -> Some "event"
                    | Some 24 -> Some "operator"
                    | Some 25 -> Some "type"
                    | _ -> None
            Some { Label = label; Detail = detail }

    /// LSP's `textDocument/completion` result is `CompletionItem[] |
    /// CompletionList | null` - parses defensively and extracts both `label`
    /// and `detail` (type/signature).
    let private parseCompletionItems (resultEl: JsonElement) : AnalysisCompletionItem list =
        let raw =
            match resultEl.ValueKind with
            | JsonValueKind.Null -> []
            | JsonValueKind.Array ->
                resultEl.EnumerateArray()
                |> Seq.choose parseItem
                |> List.ofSeq
            | JsonValueKind.Object ->
                match resultEl.TryGetProperty("items") with
                | true, itemsEl when itemsEl.ValueKind = JsonValueKind.Array ->
                    itemsEl.EnumerateArray()
                    |> Seq.choose parseItem
                    |> List.ofSeq
                | _ -> []
            | _ -> []

        raw |> List.distinctBy (fun i -> i.Label)

    let complete (documentUri: string) (version: int) (source: string) (line: int) (column: int) : Task<AnalysisCompletionItem list> =
        task {
            let! client = sharedProcess.EnsureClient()

            // Same race as `hover`/`definition`/`signatureHelp` (see their
            // own comments above): a `textDocument/completion` sent right
            // after `syncDocument` can land before FSAC's background parse
            // of the just-synced document finishes. Unlike the
            // position-based methods above, this doesn't come back as an
            // outright LSP error - it comes back as a *thin* or empty
            // completion list (confirmed against real fsautocomplete
            // 0.83.0 during this task) rather than the full member set,
            // which is silently wrong instead of loudly failing.
            // `awaitDocumentAnalyzed` is reused here purely as that
            // readiness gate - the diagnostics it returns are discarded.
            let! _ = awaitDocumentAnalyzed client documentUri version (Prologue.sourceForCheckForDocument documentUri source)

            let! result =
                sendRequestWithTimeout
                    client
                    "textDocument/completion"
                    {| textDocument = {| uri = fsacUriFor documentUri |}
                       position = {| line = Prologue.shiftUpForDocument documentUri line - 1; character = column |} |}

            match result with
            | LspFail err ->
                Log.Warning(
                    "FSAC completion failed for {DocumentUri} at ({Line},{Column}): [{Code}] {Message}",
                    documentUri,
                    line,
                    column,
                    err.Code,
                    err.Message
                )

                return []
            | LspOk resultEl ->
                let itemsList =
                    match resultEl.ValueKind with
                    | JsonValueKind.Array -> resultEl.EnumerateArray() |> List.ofSeq
                    | JsonValueKind.Object ->
                        match resultEl.TryGetProperty("items") with
                        | true, itemsEl when itemsEl.ValueKind = JsonValueKind.Array ->
                            itemsEl.EnumerateArray() |> List.ofSeq
                        | _ -> []
                    | _ -> []

                if List.isEmpty itemsList then
                    return []
                else
                    // Concurrently resolve up to 80 items so fields, properties, variables, and values
                    // have their real types extracted from completionItem/resolve.
                    let toResolve = itemsList |> Seq.truncate 80 |> Seq.toList
                    let remainder = itemsList |> Seq.skip toResolve.Length |> Seq.toList

                    let resolveTasks =
                        toResolve
                        |> List.map (fun el ->
                            task {
                                let kindOpt =
                                    match el.TryGetProperty("kind") with
                                    | true, k when k.ValueKind = JsonValueKind.Number -> Some(k.GetInt32())
                                    | _ -> None

                                let shouldResolve =
                                    match kindOpt with
                                    | Some 5 | Some 10 | Some 6 | Some 12 -> true
                                    | _ -> itemsList.Length <= 40

                                if shouldResolve then
                                    let! res = sendRequestWithTimeout client "completionItem/resolve" el
                                    match res with
                                    | LspOk resolvedEl -> return parseItem resolvedEl
                                    | _ -> return parseItem el
                                else
                                    return parseItem el
                            })

                    let! resolvedParsed = Task.WhenAll resolveTasks
                    let remainderParsed = remainder |> List.choose parseItem

                    return
                        (resolvedParsed |> Seq.choose id |> Seq.toList) @ remainderParsed
                        |> List.distinctBy (fun i -> i.Label)
        }

    let rename (documentUri: string) (version: int) (source: string) (line: int) (column: int) : Task<RenameEdit list option> =
        task {
            let! client = sharedProcess.EnsureClient()

            // Same race as `hover`/`definition`/`signatureHelp`/`complete`
            // (see their own comments above): a `textDocument/references`
            // sent right after `syncDocument` can land before FSAC's
            // background parse of the just-synced document finishes -
            // confirmed as a real (not just theoretical) flake during this
            // task: calling `syncDocument` directly here (skipping this
            // readiness wait, as the brief's own snippet does) made the
            // "returns every use" test intermittently fail with "expected
            // reference edits" (a `None` result) on roughly 1 in 3 runs.
            // `awaitDocumentAnalyzed` is reused here purely as that
            // readiness gate - the diagnostics it returns are discarded.
            let! _ = awaitDocumentAnalyzed client documentUri version (Prologue.sourceForCheckForDocument documentUri source)

            let! result =
                sendRequestWithTimeout
                    client
                    "textDocument/references"
                    {| textDocument = {| uri = fsacUriFor documentUri |}
                       position = {| line = Prologue.shiftUpForDocument documentUri line - 1; character = column |}
                       context = {| includeDeclaration = true |} |}

            match result with
            | LspFail err ->
                Log.Warning(
                    "FSAC rename (references) lookup failed for {DocumentUri} at ({Line},{Column}): [{Code}] {Message}",
                    documentUri,
                    line,
                    column,
                    err.Code,
                    err.Message
                )

                return None
            | LspOk resultEl when resultEl.ValueKind = JsonValueKind.Null -> return None
            | LspOk resultEl when resultEl.GetArrayLength() = 0 -> return None
            | LspOk resultEl ->
                // Each returned location's `uri` is FSAC's own echoed form
                // of whatever uri we `didOpen`'d it under, not necessarily
                // the exact string this module passed in - same as
                // `definition`'s `resolvedUri` (see its own comment above),
                // confirmed against real fsautocomplete during this task:
                // a driveless `documentUri` (`file:///foo.fsx`) comes back
                // drive-letter-qualified and percent-encoded
                // (`file:///d%3A/foo.fsx`) in every location's `uri`. A raw
                // string comparison against `documentUri` here would always
                // fail this same-file filter, silently producing an empty
                // (`Some []`, not even caught by the `Null`/empty-array
                // guards above since the top-level array is non-empty)
                // result instead of the real per-file reference set. Both
                // sides go through `canonicalizeUri` so they compare equal
                // regardless of which form either happens to use.
                let canonicalDocumentUri = canonicalizeUri (fsacUriFor documentUri)

                let edits =
                    resultEl.EnumerateArray()
                    |> Seq.filter (fun loc -> canonicalizeUri (loc.GetProperty("uri").GetString()) = canonicalDocumentUri)
                    |> Seq.map (fun loc ->
                        let range = loc.GetProperty("range")
                        let start = range.GetProperty("start")
                        let end_ = range.GetProperty("end")

                        { StartLine = Prologue.shiftDownForDocument documentUri (start.GetProperty("line").GetInt32() + 1) - 1
                          StartColumn = start.GetProperty("character").GetInt32()
                          EndLine = Prologue.shiftDownForDocument documentUri (end_.GetProperty("line").GetInt32() + 1) - 1
                          EndColumn = end_.GetProperty("character").GetInt32() })
                    |> List.ofSeq

                return Some edits
        }
