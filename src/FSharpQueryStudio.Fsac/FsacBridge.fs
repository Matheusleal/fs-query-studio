namespace FSharpQueryStudio.Fsac

open System
open System.Collections.Concurrent
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Serilog
open FSharpQueryStudio.Protocol

/// Translates the Worker's `analysis/*` requests into LSP calls against a shared
/// FsacProcess, and maps LSP responses back into Protocol result models.
module FsacBridge =

    let private fsacDllPath () =
        Path.Combine(AppContext.BaseDirectory, "FsacTool", "fsautocomplete.dll")

    /// Shared long-lived FsacProcess instance for the lifetime of the Worker process.
    let sharedProcess = new FsacProcess(fsacDllPath ())

    /// Converts caller document URIs to safe file:// URIs accepted by FSAC.
    let fsacUriFor = FsacUri.fsacUriFor

    /// Pending waiters for (canonicalizedUri, version) completion.
    let private diagnosticsPending =
        ConcurrentDictionary<struct (string * int), TaskCompletionSource<Diagnostic list> list>()

    let private registerWaiter (key: struct (string * int)) (tcs: TaskCompletionSource<Diagnostic list>) : unit =
        diagnosticsPending.AddOrUpdate(key, [ tcs ], (fun _ existing -> tcs :: existing)) |> ignore

    let private originalUriByFsacUri = ConcurrentDictionary<string, string>()
    let private latestExpectedVersionByUri = ConcurrentDictionary<string, int>()
    let private latestDiagnosticsByUriAndVersion = ConcurrentDictionary<struct (string * int), Diagnostic list>()

    let private registerDiagnosticsHandler (client: LspClient) =
        client.OnNotification(
            "textDocument/publishDiagnostics",
            fun paramsEl ->
                let uri = FsacUri.canonicalizeUri (paramsEl.GetProperty("uri").GetString())
                let originalDocUri =
                    match originalUriByFsacUri.TryGetValue(uri) with
                    | true, orig -> orig
                    | false, _ -> uri
                let diagnostics = FsacParsing.parseDiagnostics originalDocUri paramsEl

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
                let uri = FsacUri.canonicalizeUri (textDocument.GetProperty("uri").GetString())
                let version = textDocument.GetProperty("version").GetInt32()
                let key = struct (uri, version)

                match diagnosticsPending.TryRemove(key) with
                | false, _ -> ()
                | true, waiters ->
                    // Delay slightly so that post-analysis publishDiagnostics settle before resolving
                    Task.Delay(100).ContinueWith(fun (_: Task) ->
                        let finalDiags =
                            match latestDiagnosticsByUriAndVersion.TryGetValue(key) with
                            | true, d -> d
                            | false, _ -> []
                        waiters |> List.iter (fun tcs -> tcs.TrySetResult(finalDiags) |> ignore)
                    ) |> ignore
        )

    let private openDocuments = ConcurrentDictionary<string, bool>()
    let mutable private lastKnownClient: LspClient option = None
    let private syncLock = obj ()

    let private pruneOlderVersions (uri: string) (currentVersion: int) =
        for kv in latestDiagnosticsByUriAndVersion do
            let struct (u, v) = kv.Key
            if u = uri && v < currentVersion then
                latestDiagnosticsByUriAndVersion.TryRemove(kv.Key) |> ignore
        for kv in diagnosticsPending do
            let struct (u, v) = kv.Key
            if u = uri && v < currentVersion then
                match diagnosticsPending.TryRemove(kv.Key) with
                | true, waiters ->
                    for tcs in waiters do
                        tcs.TrySetResult([]) |> ignore
                | _ -> ()

    let private isNewClient (client: LspClient) : bool =
        match lastKnownClient with
        | Some c -> not (obj.ReferenceEquals(box c, box client))
        | None -> true

    /// Synchronizes document state with FSAC via didOpen (first time) or didChange.
    let syncDocument (client: LspClient) (documentUri: string) (version: int) (checkedSource: string) : Task =
        let wireUri = fsacUriFor documentUri
        let canonicalUri = FsacUri.canonicalizeUri wireUri

        let isFirstOpenForThisDocument =
            lock syncLock (fun () ->
                if isNewClient client then
                    openDocuments.Clear()
                    latestExpectedVersionByUri.Clear()
                    latestDiagnosticsByUriAndVersion.Clear()
                    registerDiagnosticsHandler client
                    lastKnownClient <- Some client

                pruneOlderVersions canonicalUri version
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

    /// Awaits completion of document analysis, returning diagnostics or None on timeout.
    let private awaitDocumentAnalyzed
        (client: LspClient)
        (documentUri: string)
        (version: int)
        (checkedSource: string)
        : Task<Diagnostic list option> =
        task {
            let tcs = TaskCompletionSource<Diagnostic list>(TaskCreationOptions.RunContinuationsAsynchronously)
            let canonical = FsacUri.canonicalizeUri (fsacUriFor documentUri)
            originalUriByFsacUri.[canonical] <- documentUri
            let pendingKey = struct (canonical, version)
            registerWaiter pendingKey tcs

            do! syncDocument client documentUri version checkedSource

            let! winner = Task.WhenAny(tcs.Task :> Task, Task.Delay(TimeSpan.FromSeconds 30.0))

            if winner = (tcs.Task :> Task) then
                let! diagnostics = tcs.Task
                do! Task.Delay(50)
                return Some diagnostics
            else
                Log.Warning(
                    "Timed out after 30s waiting for FSAC to finish parsing {DocumentUri} (version {Version})",
                    documentUri,
                    version
                )
                return None
        }

    let private requestTimeout = TimeSpan.FromSeconds 30.0

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
                Log.Warning("FSAC request {Method} timed out after {TimeoutSeconds}s", method_, requestTimeout.TotalSeconds)
                return LspFail { Code = -1; Message = $"FSAC request '{method_}' timed out after {requestTimeout.TotalSeconds}s" }
        }

    /// Checks document for diagnostics.
    let check (documentUri: string) (version: int) (source: string) : Task<Diagnostic list> =
        task {
            let! client = sharedProcess.EnsureClient()
            let! diagnostics = awaitDocumentAnalyzed client documentUri version (Prologue.sourceForCheckForDocument documentUri source)
            return diagnostics |> Option.defaultValue []
        }

    /// Retrieves hover information for a symbol at a specific line and column.
    let hover (documentUri: string) (version: int) (source: string) (line: int) (column: int) : Task<HoverInfo option> =
        task {
            let! client = sharedProcess.EnsureClient()
            let! _ = awaitDocumentAnalyzed client documentUri version (Prologue.sourceForCheckForDocument documentUri source)

            let! result =
                sendRequestWithTimeout
                    client
                    "textDocument/hover"
                    {| textDocument = {| uri = fsacUriFor documentUri |}
                       position = {| line = Prologue.shiftUpForDocument documentUri line - 1; character = column |} |}

            match result with
            | LspFail err ->
                Log.Warning("FSAC hover failed for {DocumentUri} at ({Line},{Column}): [{Code}] {Message}", documentUri, line, column, err.Code, err.Message)
                return None
            | LspOk resultEl when resultEl.ValueKind = JsonValueKind.Null -> return None
            | LspOk resultEl -> return Some(FsacParsing.parseHoverMarkdown (resultEl.GetProperty("contents")))
        }

    /// Resolves symbol definition location.
    let definition
        (documentUri: string)
        (version: int)
        (source: string)
        (line: int)
        (column: int)
        : Task<DefinitionLocation option> =
        task {
            let! client = sharedProcess.EnsureClient()
            let! _ = awaitDocumentAnalyzed client documentUri version (Prologue.sourceForCheckForDocument documentUri source)

            let! result =
                sendRequestWithTimeout
                    client
                    "textDocument/definition"
                    {| textDocument = {| uri = fsacUriFor documentUri |}
                       position = {| line = Prologue.shiftUpForDocument documentUri line - 1; character = column |} |}

            match result with
            | LspFail err ->
                Log.Warning("FSAC definition lookup failed for {DocumentUri} at ({Line},{Column}): [{Code}] {Message}", documentUri, line, column, err.Code, err.Message)
                return None
            | LspOk resultEl ->
                match FsacParsing.parseFirstLocation resultEl with
                | None -> return None
                | Some(resolvedUri, range) ->
                    let start = range.GetProperty("start")
                    let end_ = range.GetProperty("end")

                    let filePath, lineAdjustment =
                        if String.Equals(FsacUri.canonicalizeUri resolvedUri, FsacUri.canonicalizeUri (fsacUriFor documentUri), StringComparison.OrdinalIgnoreCase) then
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

    /// Retrieves signature help information for a callable symbol invocation site.
    let signatureHelp
        (documentUri: string)
        (version: int)
        (source: string)
        (line: int)
        (column: int)
        : Task<SignatureHelpInfo option> =
        task {
            let! client = sharedProcess.EnsureClient()
            let! _ = awaitDocumentAnalyzed client documentUri version (Prologue.sourceForCheckForDocument documentUri source)

            let! result =
                sendRequestWithTimeout
                    client
                    "textDocument/signatureHelp"
                    {| textDocument = {| uri = fsacUriFor documentUri |}
                       position = {| line = Prologue.shiftUpForDocument documentUri line - 1; character = column |} |}

            match result with
            | LspFail err ->
                Log.Warning("FSAC signatureHelp failed for {DocumentUri} at ({Line},{Column}): [{Code}] {Message}", documentUri, line, column, err.Code, err.Message)
                return None
            | LspOk resultEl when resultEl.ValueKind = JsonValueKind.Null -> return None
            | LspOk resultEl ->
                let signatures = resultEl.GetProperty("signatures")

                if signatures.GetArrayLength() = 0 then
                    return None
                else
                    let sig_ = signatures.[0]
                    let label = sig_.GetProperty("label").GetString()
                    let stripped = FsacParsing.stripSymbolKind label
                    let nameTypeSplit = stripped.Split([| ':' |], 2)

                    let displayName =
                        if nameTypeSplit.Length = 2 then nameTypeSplit.[0].Trim() else stripped.Trim()

                    let typeText =
                        if nameTypeSplit.Length = 2 then nameTypeSplit.[1].Trim() else stripped.Trim()

                    let arrowSegments = FsacParsing.splitTopLevelArrows typeText

                    if arrowSegments.Length < 2 then
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

    /// Provides autocompletion items for a cursor position.
    let complete (documentUri: string) (version: int) (source: string) (line: int) (column: int) : Task<AnalysisCompletionItem list> =
        task {
            let! client = sharedProcess.EnsureClient()
            let! _ = awaitDocumentAnalyzed client documentUri version (Prologue.sourceForCheckForDocument documentUri source)

            let! result =
                sendRequestWithTimeout
                    client
                    "textDocument/completion"
                    {| textDocument = {| uri = fsacUriFor documentUri |}
                       position = {| line = Prologue.shiftUpForDocument documentUri line - 1; character = column |} |}

            match result with
            | LspFail err ->
                Log.Warning("FSAC completion failed for {DocumentUri} at ({Line},{Column}): [{Code}] {Message}", documentUri, line, column, err.Code, err.Message)
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
                                    | LspOk resolvedEl -> return FsacParsing.parseItem resolvedEl
                                    | _ -> return FsacParsing.parseItem el
                                else
                                    return FsacParsing.parseItem el
                            })

                    let! resolvedParsed = Task.WhenAll resolveTasks
                    let remainderParsed = remainder |> List.choose FsacParsing.parseItem

                    return
                        (resolvedParsed |> Seq.choose id |> Seq.toList) @ remainderParsed
                        |> List.distinctBy (fun i -> i.Label)
        }

    /// Finds symbol references for rename refactoring across the document.
    let rename (documentUri: string) (version: int) (source: string) (line: int) (column: int) : Task<RenameEdit list option> =
        task {
            let! client = sharedProcess.EnsureClient()
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
                Log.Warning("FSAC rename (references) lookup failed for {DocumentUri} at ({Line},{Column}): [{Code}] {Message}", documentUri, line, column, err.Code, err.Message)
                return None
            | LspOk resultEl when resultEl.ValueKind = JsonValueKind.Null -> return None
            | LspOk resultEl when resultEl.GetArrayLength() = 0 -> return None
            | LspOk resultEl ->
                let canonicalDocumentUri = FsacUri.canonicalizeUri (fsacUriFor documentUri)

                let edits =
                    resultEl.EnumerateArray()
                    |> Seq.filter (fun loc -> FsacUri.canonicalizeUri (loc.GetProperty("uri").GetString()) = canonicalDocumentUri)
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
