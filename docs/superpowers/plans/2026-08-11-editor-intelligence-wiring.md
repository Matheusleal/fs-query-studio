# Editor Intelligence (LSP) Wiring Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make hover, go-to-definition, signature help, completions, and rename actually work in the running app. Investigation found the backend for every one of these is real and correct (`FSharpQueryStudio.Analysis`'s `Hover`/`GoToDefinition`/`SignatureHelp`/`Completions`/`Rename` modules, the Worker's `analysis/hover`/`analysis/definition`/`analysis/signatureHelp` dispatch cases, and `App.AnalysisService`'s host-side callers for those three) — but `MainWindow.axaml.fs`'s bridge message handler has exactly one real case (`TextChanged`); every other incoming `EditorRequest` — `HoverRequested`, `GoToDefinitionRequested`, `SignatureHelpRequested`, `CompletionRequested` — falls into a catch-all `Ok _otherRequest -> ()` no-op (`MainWindow.axaml.fs:114`). A hover request from Monaco is sent, and never answered — the popup spins forever. Separately, `editor-host.html` never registers a Monaco completion/definition/signature-help/rename provider at all (only hover's is registered), and completions have no backend wiring whatsoever — no protocol types, no worker dispatch case, nothing.

**Architecture:** For hover/definition/signature-help, the backend and host-service layers already exist and are correct — this plan only adds the missing middle: a `ShellViewModel` hook (matching the existing `ExecuteAsync`/`CheckAsync` no-op-when-unconfigured pattern) wired in `App.axaml.fs` to the existing `AnalysisService` function, and a real case in `MainWindow.axaml.fs`'s bridge dispatch that calls the hook and posts the `*Resolved` notification back. For completions, the same shape needs building from scratch at the protocol/worker layer first (mirroring `analysis/hover`'s exact pattern) before the same UI wiring applies. For rename, every layer is missing — `EditorRequest`/`HostNotification` need a new case each, `Analysis.Rename` (real, already implemented, currently unused) needs a Worker handler, and Monaco's `registerRenameProvider` needs to exist at all. Each Monaco-side provider registration in `editor-host.html` follows the exact pattern `registerHoverProvider` already establishes: a pending-promise map keyed by request id, a `postToHostWithId` call, and a new case in the incoming-message `switch`.

**Tech Stack:** F#, Avalonia 12.1.1, Monaco Editor (vendored, `EditorAssets/vs/`), WebView2 (`Avalonia.Controls.WebView`), xUnit.

## Global Constraints

- TDD throughout, same as every prior milestone task in `docs/DEVLOG.md`.
- Coordinate convention, already established and must not be broken: FCS/`Analysis` results are 0-based on line and column (`DiagnosticMapping`'s convention); Monaco positions are 1-based on both. Every existing bridge payload already follows "0-based on the wire, 1-based in Monaco" — new payloads must match, and `editor-host.html`'s new provider code must convert at the boundary exactly like `registerHoverProvider`'s existing `column: position.column - 1` already does.
- `MainWindow.axaml.fs` is not unit-testable in this codebase (needs a real `Window`/WebView) — every task's automated coverage stops at `ShellViewModelTests`/`Worker.Tests`/`Editor.Tests`, and `editor-host.html`'s own JS changes are manual-verify-only, exactly like every prior `editor-host.html` task (B.6, B.9, E.6, E.7 in `docs/DEVLOG.md` all hit this same wall — this plan doesn't reopen that limitation, just inherits it).
- `dotnet build` clean and `dotnet test` green (excluding the pre-existing `RequiresDocker` failures) after every task.
- Update `docs/DEVLOG.md` per task, matching this repo's established entry format.

---

### Task 1: Wire hover end to end

**Files:**
- Modify: `src/FSharpQueryStudio.Ui/ShellViewModel.fs`
- Modify: `src/FSharpQueryStudio.Ui/MainWindow.axaml.fs`
- Modify: `src/FSharpQueryStudio.App/App.axaml.fs`
- Test: `tests/FSharpQueryStudio.Ui.Tests/ShellViewModelTests.fs`

**Interfaces:**
- Consumes: `FSharpQueryStudio.App.AnalysisService.hover` (existing, currently uncalled outside its own composition-root wiring gap).
- Produces: `ShellViewModel.HoverAsync: (string * int * string * int * int -> Task<HoverInfo option>) option with get, set` — Task 2/3 reuse this exact hook shape for `DefinitionAsync`/`SignatureHelpAsync`.

- [ ] **Step 1: Write the failing `ShellViewModel` test**

Add to `tests/FSharpQueryStudio.Ui.Tests/ShellViewModelTests.fs`:
```fsharp
[<Fact>]
let ``HoverAsync is settable and reachable as a plain hook, no-op when unconfigured`` () =
    task {
        let vm = ShellViewModel()
        Assert.Equal(None, vm.HoverAsync)

        let mutable received = None

        vm.HoverAsync <-
            Some(fun (uri, version, source, line, column) ->
                received <- Some(uri, version, source, line, column)
                Task.FromResult(Some { DisplayName = "x"; Type = "int"; Signature = None; XmlDoc = None }))

        let! result = vm.HoverAsync.Value ("untitled:Script1.fsx", 1, "let x = 1", 1, 5)

        Assert.Equal(Some("untitled:Script1.fsx", 1, "let x = 1", 1, 5), received)
        Assert.Equal("x", result.Value.DisplayName)
    }
```
This is a thin contract test — the hook is a plain settable option, the same shape `ExecuteAsync`/`CheckAsync` already have and are tested the same minimal way. The real end-to-end proof is `Worker.Tests`' existing `analysis/hover` coverage (already green, untouched by this task) plus `AnalysisService.hover`'s own correctness (also existing, untouched) — this task only adds the missing wire between them and the UI.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/FSharpQueryStudio.Ui.Tests --filter "FullyQualifiedName~HoverAsync is settable"`
Expected: FAIL to compile — `ShellViewModel` has no `HoverAsync` member.

- [ ] **Step 3: Add the hook to `ShellViewModel`**

In `src/FSharpQueryStudio.Ui/ShellViewModel.fs`, add near `CheckAsync`:
```fsharp
    /// `analysis/hover` — set by the composition root, same
    /// no-op-when-unconfigured shape as `ExecuteAsync`/`CheckAsync`.
    /// Takes `(documentUri, version, source, line, column)`; `MainWindow`
    /// calls it in response to a bridge `hoverRequested` message and
    /// posts the result back as `HoverResolved`.
    member val HoverAsync: ((string * int * string * int * int) -> Task<HoverInfo option>) option =
        None with get, set
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/FSharpQueryStudio.Ui.Tests --filter "FullyQualifiedName~HoverAsync is settable"`
Expected: PASS.

- [ ] **Step 5: Wire `MainWindow.axaml.fs`'s bridge dispatch**

In `src/FSharpQueryStudio.Ui/MainWindow.axaml.fs`, find:
```fsharp
            match EditorBridge.decode envelope with
            | Ok(TextChanged(documentUri, version, source)) ->
                shellViewModel.RouteTextChanged(documentUri, version, source)
            | Ok _otherRequest -> () // Wired as each lands in its own later task.
            | Result.Error _err -> ()) // Malformed message from the page - never crashes the host.
```
Replace with:
```fsharp
            match EditorBridge.decode envelope with
            | Ok(TextChanged(documentUri, version, source)) ->
                shellViewModel.RouteTextChanged(documentUri, version, source)
            | Ok(HoverRequested(documentUri, version, line, column)) ->
                match
                    shellViewModel.Documents |> List.tryFind (fun d -> d.Uri = documentUri), shellViewModel.HoverAsync
                with
                | Some doc, Some hover ->
                    (task {
                        let! info = hover (documentUri, version, doc.Text, line, column)

                        postToEditor
                            webView
                            (EditorBridge.encodeNotification (HoverResolved(envelope.Id, documentUri, info)))
                    }
                    :> Task)
                    |> ignore
                | _ -> ()
            | Ok _otherRequest -> () // Wired as each lands in its own later task.
            | Result.Error _err -> ()) // Malformed message from the page - never crashes the host.
```
Add `open System.Threading.Tasks` to the file's `open` list if not already present (check first — `Task` is referenced elsewhere in this file already via `System.Threading.Tasks.Task` fully-qualified in `postToEditor`; either qualify `Task` the same way here or add the `open`, matching whichever style keeps the diff smallest).

- [ ] **Step 6: Wire the composition root, `App.axaml.fs`**

Add alongside the existing `checkAsync`/`mainWindow.ShellViewModel.CheckAsync <- Some checkAsync` block:
```fsharp
            let hoverAsync (documentUri: string, version: int, source: string, line: int, column: int) =
                AnalysisService.hover client documentUri version source line column

            mainWindow.ShellViewModel.HoverAsync <- Some hoverAsync
```

- [ ] **Step 7: Build and run the full solution test suite**

Run: `dotnet build && dotnet test`
Expected: clean build, all green except pre-existing `RequiresDocker` failures.

- [ ] **Step 8: Commit**

```bash
git add src/FSharpQueryStudio.Ui/ShellViewModel.fs src/FSharpQueryStudio.Ui/MainWindow.axaml.fs src/FSharpQueryStudio.App/App.axaml.fs tests/FSharpQueryStudio.Ui.Tests/ShellViewModelTests.fs
git commit -m "Fix: hover requests from Monaco now actually reach the worker and resolve"
```

---

### Task 2: Wire go-to-definition end to end, including the Monaco provider

**Files:**
- Modify: `src/FSharpQueryStudio.Ui/ShellViewModel.fs`
- Modify: `src/FSharpQueryStudio.Ui/MainWindow.axaml.fs`
- Modify: `src/FSharpQueryStudio.App/App.axaml.fs`
- Modify: `src/FSharpQueryStudio.Editor/EditorAssets/editor-host.html`
- Test: `tests/FSharpQueryStudio.Ui.Tests/ShellViewModelTests.fs`

**Interfaces:**
- Consumes: `FSharpQueryStudio.App.AnalysisService.definition` (existing), Task 1's `HoverAsync` hook shape as the template.
- Produces: `ShellViewModel.DefinitionAsync`, the second real bridge case in `MainWindow.axaml.fs`, and Monaco's first `registerDefinitionProvider` call — nothing later depends on this beyond Task 6's DEVLOG entry.

- [ ] **Step 1: Write the failing `ShellViewModel` test**

```fsharp
[<Fact>]
let ``DefinitionAsync is settable and reachable as a plain hook, no-op when unconfigured`` () =
    task {
        let vm = ShellViewModel()
        Assert.Equal(None, vm.DefinitionAsync)

        vm.DefinitionAsync <-
            Some(fun (_uri, _version, _source, _line, _column) ->
                Task.FromResult(
                    Some
                        { FilePath = "script.fsx"
                          StartLine = 0
                          StartColumn = 4
                          EndLine = 0
                          EndColumn = 5 }
                ))

        let! result = vm.DefinitionAsync.Value ("untitled:Script1.fsx", 1, "let x = 1\nx", 2, 0)

        Assert.Equal(0, result.Value.StartLine)
    }
```

- [ ] **Step 2: Run to verify it fails, then add the hook**

Run: `dotnet test tests/FSharpQueryStudio.Ui.Tests --filter "FullyQualifiedName~DefinitionAsync is settable"` → FAIL to compile.

In `src/FSharpQueryStudio.Ui/ShellViewModel.fs`, add next to `HoverAsync`:
```fsharp
    /// `analysis/definition` — same shape/rationale as `HoverAsync`.
    member val DefinitionAsync: ((string * int * string * int * int) -> Task<DefinitionLocation option>) option =
        None with get, set
```

- [ ] **Step 3: Run to verify it passes**

Run: `dotnet test tests/FSharpQueryStudio.Ui.Tests --filter "FullyQualifiedName~DefinitionAsync is settable"` → PASS.

- [ ] **Step 4: Extend `MainWindow.axaml.fs`'s bridge dispatch**

Add a case right after the `HoverRequested` case from Task 1, same `match`:
```fsharp
            | Ok(GoToDefinitionRequested(documentUri, version, line, column)) ->
                match
                    shellViewModel.Documents |> List.tryFind (fun d -> d.Uri = documentUri),
                    shellViewModel.DefinitionAsync
                with
                | Some doc, Some definition ->
                    (task {
                        let! location = definition (documentUri, version, doc.Text, line, column)

                        postToEditor
                            webView
                            (EditorBridge.encodeNotification (DefinitionResolved(envelope.Id, documentUri, location)))
                    }
                    :> Task)
                    |> ignore
                | _ -> ()
```

- [ ] **Step 5: Wire the composition root**

In `src/FSharpQueryStudio.App/App.axaml.fs`, alongside `hoverAsync`:
```fsharp
            let definitionAsync (documentUri: string, version: int, source: string, line: int, column: int) =
                AnalysisService.definition client documentUri version source line column

            mainWindow.ShellViewModel.DefinitionAsync <- Some definitionAsync
```

- [ ] **Step 6: Register Monaco's definition provider in `editor-host.html`**

Add near the existing `var pendingHoverResolvers = {};` declaration:
```javascript
        var pendingDefinitionResolvers = {};
```
Add right after the existing `monaco.languages.registerHoverProvider(...)` block:
```javascript
        monaco.languages.registerDefinitionProvider("fsharp", {
          provideDefinition: function (model, position) {
            return new Promise(function (resolve) {
              var id = crypto.randomUUID();
              pendingDefinitionResolvers[id] = { resolve: resolve, model: model };

              postToHostWithId(id, "goToDefinitionRequested", {
                documentUri: documentUri,
                version: currentVersion,
                line: position.lineNumber,
                column: position.column - 1
              });
            });
          }
        });
```
Add a case to the incoming-message `switch`, right after the existing `case "hoverResolved":` block:
```javascript
              case "definitionResolved": {
                var pending = pendingDefinitionResolvers[envelope.id];

                if (pending) {
                  delete pendingDefinitionResolvers[envelope.id];
                  var location = envelope.payload.location;

                  // Cross-file jumps (a resolved declaration outside the
                  // one open document/model) aren't supported yet - this
                  // editor hosts exactly one model at a time, with no
                  // multi-file project view for Monaco to jump into. Same
                  // documentUri is the only case that can resolve to
                  // anything real; anything else resolves null (Monaco
                  // treats that as "no definition found", not an error).
                  if (location && location.filePath === documentUri) {
                    pending.resolve({
                      uri: pending.model.uri,
                      range: {
                        startLineNumber: location.startLine + 1,
                        startColumn: location.startColumn + 1,
                        endLineNumber: location.endLine + 1,
                        endColumn: location.endColumn + 1
                      }
                    });
                  } else {
                    pending.resolve(null);
                  }
                }

                break;
              }
```

- [ ] **Step 7: Build and run the full solution test suite**

Run: `dotnet build && dotnet test`
Expected: clean build, all green except pre-existing `RequiresDocker` failures. (`editor-host.html`'s JS change isn't covered by an automated test — same standing limitation noted in Global Constraints.)

- [ ] **Step 8: Commit**

```bash
git add src/FSharpQueryStudio.Ui/ShellViewModel.fs src/FSharpQueryStudio.Ui/MainWindow.axaml.fs src/FSharpQueryStudio.App/App.axaml.fs src/FSharpQueryStudio.Editor/EditorAssets/editor-host.html tests/FSharpQueryStudio.Ui.Tests/ShellViewModelTests.fs
git commit -m "Fix: go-to-definition wired end to end, including a real Monaco definition provider"
```

---

### Task 3: Wire signature help end to end, including the Monaco provider

**Files:** same shape as Task 2 — `ShellViewModel.fs`, `MainWindow.axaml.fs`, `App.axaml.fs`, `editor-host.html`, `ShellViewModelTests.fs`.

**Interfaces:**
- Consumes: `FSharpQueryStudio.App.AnalysisService.signatureHelp` (existing).
- Produces: `ShellViewModel.SignatureHelpAsync`, the third bridge case, Monaco's `registerSignatureHelpProvider`.

- [ ] **Step 1: Write the failing test**

```fsharp
[<Fact>]
let ``SignatureHelpAsync is settable and reachable as a plain hook, no-op when unconfigured`` () =
    task {
        let vm = ShellViewModel()
        Assert.Equal(None, vm.SignatureHelpAsync)

        vm.SignatureHelpAsync <-
            Some(fun (_uri, _version, _source, _line, _column) ->
                Task.FromResult(Some { DisplayName = "printfn"; Parameters = [ "format: Printf.TextWriterFormat<'T>" ]; ReturnType = "'T" }))

        let! result = vm.SignatureHelpAsync.Value ("untitled:Script1.fsx", 1, "printfn \"%s\"", 1, 9)

        Assert.Equal("printfn", result.Value.DisplayName)
    }
```

- [ ] **Step 2: Run to verify it fails, then add the hook**

Run: `dotnet test tests/FSharpQueryStudio.Ui.Tests --filter "FullyQualifiedName~SignatureHelpAsync is settable"` → FAIL to compile.

```fsharp
    /// `analysis/signatureHelp` — same shape/rationale as `HoverAsync`.
    member val SignatureHelpAsync: ((string * int * string * int * int) -> Task<SignatureHelpInfo option>) option =
        None with get, set
```

- [ ] **Step 3: Run to verify it passes** — same command as Step 2, PASS.

- [ ] **Step 4: Extend `MainWindow.axaml.fs`'s bridge dispatch**

```fsharp
            | Ok(SignatureHelpRequested(documentUri, version, line, column)) ->
                match
                    shellViewModel.Documents |> List.tryFind (fun d -> d.Uri = documentUri),
                    shellViewModel.SignatureHelpAsync
                with
                | Some doc, Some signatureHelp ->
                    (task {
                        let! info = signatureHelp (documentUri, version, doc.Text, line, column)

                        postToEditor
                            webView
                            (EditorBridge.encodeNotification (
                                SignatureHelpResolved(envelope.Id, documentUri, info)
                            ))
                    }
                    :> Task)
                    |> ignore
                | _ -> ()
```

- [ ] **Step 5: Wire the composition root**

```fsharp
            let signatureHelpAsync (documentUri: string, version: int, source: string, line: int, column: int) =
                AnalysisService.signatureHelp client documentUri version source line column

            mainWindow.ShellViewModel.SignatureHelpAsync <- Some signatureHelpAsync
```

- [ ] **Step 6: Register Monaco's signature help provider in `editor-host.html`**

```javascript
        var pendingSignatureHelpResolvers = {};
```
```javascript
        monaco.languages.registerSignatureHelpProvider("fsharp", {
          signatureHelpTriggerCharacters: ["(", ","],
          provideSignatureHelp: function (model, position) {
            return new Promise(function (resolve) {
              var id = crypto.randomUUID();
              pendingSignatureHelpResolvers[id] = resolve;

              postToHostWithId(id, "signatureHelpRequested", {
                documentUri: documentUri,
                version: currentVersion,
                line: position.lineNumber,
                column: position.column - 1
              });
            });
          }
        });
```
```javascript
              case "signatureHelpResolved": {
                var resolveSig = pendingSignatureHelpResolvers[envelope.id];

                if (resolveSig) {
                  delete pendingSignatureHelpResolvers[envelope.id];
                  var help = envelope.payload.signatureHelp;

                  if (help) {
                    resolveSig({
                      value: {
                        signatures: [
                          {
                            label: help.displayName + "(" + help.parameters.join(", ") + "): " + help.returnType,
                            parameters: help.parameters.map(function (p) { return { label: p }; })
                          }
                        ],
                        activeSignature: 0,
                        activeParameter: 0
                      },
                      dispose: function () {}
                    });
                  } else {
                    resolveSig(null);
                  }
                }

                break;
              }
```

- [ ] **Step 7: Build and run the full solution test suite**

Run: `dotnet build && dotnet test` → clean, all green except pre-existing `RequiresDocker` failures.

- [ ] **Step 8: Commit**

```bash
git add src/FSharpQueryStudio.Ui/ShellViewModel.fs src/FSharpQueryStudio.Ui/MainWindow.axaml.fs src/FSharpQueryStudio.App/App.axaml.fs src/FSharpQueryStudio.Editor/EditorAssets/editor-host.html tests/FSharpQueryStudio.Ui.Tests/ShellViewModelTests.fs
git commit -m "Fix: signature help wired end to end, including a real Monaco provider"
```

---

### Task 4: Completions — build the missing backend wiring, then wire the UI same as Tasks 1–3

**Files:**
- Modify: `src/FSharpQueryStudio.Protocol/Payloads.fs`
- Modify: `src/FSharpQueryStudio.Worker/Handlers.fs`
- Modify: `src/FSharpQueryStudio.App/AnalysisService.fs`
- Modify: `src/FSharpQueryStudio.Ui/ShellViewModel.fs`
- Modify: `src/FSharpQueryStudio.Ui/MainWindow.axaml.fs`
- Modify: `src/FSharpQueryStudio.App/App.axaml.fs`
- Modify: `src/FSharpQueryStudio.Editor/EditorAssets/editor-host.html`
- Test: `tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs`, `tests/FSharpQueryStudio.Ui.Tests/ShellViewModelTests.fs`

**Interfaces:**
- Consumes: `FSharpQueryStudio.Analysis.Completions.complete`/`completeAtPosition` (existing, real, currently only reachable from `Analysis.Tests` — nothing above it ever calls either).
- Produces: `AnalysisCompletionParams`/`AnalysisCompletionResult` (Protocol), `Handlers.handleAnalysisComplete` + `"analysis/complete"` dispatch case (Worker), `AnalysisService.complete` (App), `ShellViewModel.CompleteAsync` (Ui), a Monaco `registerCompletionItemProvider`.

- [ ] **Step 1: Write the failing Worker test**

Add to `tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs`:
```fsharp
let private completionRequest (id: string) (source: string) (line: int) (column: int) : WorkerRequestFrame =
    { Id = id
      Method = "analysis/complete"
      Params =
        JsonSerializer.SerializeToElement<AnalysisCompletionParams>(
            { DocumentUri = "untitled:Script1.fsx"
              Version = 1
              Source = source
              Line = line
              Column = column },
            Serialization.options
        ) }

[<Fact>]
let ``dispatch on analysis/complete returns tier-1 keyword and connector-helper names regardless of cursor position`` () =
    task {
        let! response = Handlers.dispatch (completionRequest "cmp1" "" 1 0)

        Assert.True(response.Error.IsNone)
        let result = JsonSerializer.Deserialize<AnalysisCompletionResult>(response.Result.Value, Serialization.options)
        Assert.Contains("let", result.Items)
        Assert.Contains("Connections.sql", result.Items)
    }

[<Fact>]
let ``dispatch on analysis/complete includes tier-2 semantic completions at the cursor position`` () =
    task {
        let source = "let myLocalValue = 1\nmyLocal"
        let! response = Handlers.dispatch (completionRequest "cmp2" source 2 8)

        Assert.True(response.Error.IsNone)
        let result = JsonSerializer.Deserialize<AnalysisCompletionResult>(response.Result.Value, Serialization.options)
        Assert.Contains("myLocalValue", result.Items)
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests --filter "FullyQualifiedName~analysis/complete"`
Expected: FAIL to compile — `AnalysisCompletionParams`/`AnalysisCompletionResult` don't exist, `"analysis/complete"` isn't dispatched.

- [ ] **Step 3: Add the Protocol types**

In `src/FSharpQueryStudio.Protocol/Payloads.fs`, add after `AnalysisSignatureHelpResult`:
```fsharp
/// `analysis/complete` request payload — Milestone B.10/B.11's
/// completions. Same shape/rationale as `AnalysisHoverParams`.
type AnalysisCompletionParams =
    { DocumentUri: string
      Version: int
      Source: string
      Line: int
      Column: int }

/// Tier 1 (keywords/snippets/connector helper names) and tier 2
/// (semantic, FCS-derived at the cursor position) merged and
/// de-duplicated — see `Completions.complete`/`completeAtPosition`. A
/// flat name list, not yet Monaco's richer `CompletionItem` shape
/// (kind/detail/documentation) — `editor-host.html` maps each name to a
/// plain-text completion item; a later task can enrich this if
/// per-item kind/detail turns out to matter in practice.
type AnalysisCompletionResult = { Items: string list }
```

- [ ] **Step 4: Add the Worker handler and dispatch case**

In `src/FSharpQueryStudio.Worker/Handlers.fs`, add after `handleAnalysisSignatureHelp`:
```fsharp
    /// `analysis/complete` — Milestone B.10/B.11. Merges tier 1 (static,
    /// position-insensitive) and tier 2 (semantic, FCS-derived at the
    /// cursor) into one de-duplicated list. `recentNames` (tier 1's
    /// "names from the most recent successful check") is `[]` here —
    /// nothing upstream tracks a document's last-checked declared names
    /// yet; tracked as a known, accepted limitation, not silently
    /// dropped scope (see this plan's Self-Review Notes).
    let handleAnalysisComplete (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            let completionParams =
                JsonSerializer.Deserialize<AnalysisCompletionParams>(request.Params, Serialization.options)

            let workDir =
                Path.Combine(Path.GetTempPath(), "fqs-analysis-" + Guid.NewGuid().ToString("N"))

            Directory.CreateDirectory(workDir) |> ignore
            let context = AnalysisContext.build workDir

            let! semantic =
                Completions.completeAtPosition context completionParams.Source completionParams.Line completionParams.Column

            let items = (Completions.complete [] @ semantic) |> List.distinct
            let result: AnalysisCompletionResult = { Items = items }

            return
                { Id = request.Id
                  Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
                  Error = None }
        }
```
Add to `dispatch`:
```fsharp
            | "analysis/complete" -> return! handleAnalysisComplete request
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests --filter "FullyQualifiedName~analysis/complete"`
Expected: PASS, both tests.

- [ ] **Step 6: Add `AnalysisService.complete`**

In `src/FSharpQueryStudio.App/AnalysisService.fs`, add after `signatureHelp`:
```fsharp
    /// `analysis/complete` — same shape/rationale as `hover`.
    let complete
        (client: WorkerClient)
        (documentUri: string)
        (version: int)
        (source: string)
        (line: int)
        (column: int)
        : Task<string list> =
        task {
            let request: WorkerRequestFrame =
                { Id = Guid.NewGuid().ToString()
                  Method = "analysis/complete"
                  Params =
                    JsonSerializer.SerializeToElement<AnalysisCompletionParams>(
                        ({ DocumentUri = documentUri
                           Version = version
                           Source = source
                           Line = line
                           Column = column }: AnalysisCompletionParams),
                        Serialization.options
                    ) }

            let! response = client.Send(request)

            match response.Result with
            | Some resultJson ->
                let result =
                    JsonSerializer.Deserialize<AnalysisCompletionResult>(resultJson, Serialization.options)

                return result.Items
            | None -> return []
        }
```

- [ ] **Step 7: Write the failing `ShellViewModel` test, then add the hook**

```fsharp
[<Fact>]
let ``CompleteAsync is settable and reachable as a plain hook, no-op when unconfigured`` () =
    task {
        let vm = ShellViewModel()
        Assert.Equal(None, vm.CompleteAsync)

        vm.CompleteAsync <- Some(fun (_uri, _version, _source, _line, _column) -> Task.FromResult([ "let"; "Connections.sql" ]))

        let! result = vm.CompleteAsync.Value ("untitled:Script1.fsx", 1, "", 1, 0)

        Assert.Contains("Connections.sql", result)
    }
```
Run to verify it fails, then in `src/FSharpQueryStudio.Ui/ShellViewModel.fs` add next to `SignatureHelpAsync`:
```fsharp
    /// `analysis/complete` — same shape/rationale as `HoverAsync`.
    member val CompleteAsync: ((string * int * string * int * int) -> Task<string list>) option =
        None with get, set
```
Run to verify it passes.

- [ ] **Step 8: Extend `MainWindow.axaml.fs`'s bridge dispatch**

```fsharp
            | Ok(CompletionRequested(documentUri, version, line, column)) ->
                match
                    shellViewModel.Documents |> List.tryFind (fun d -> d.Uri = documentUri), shellViewModel.CompleteAsync
                with
                | Some doc, Some complete ->
                    (task {
                        let! items = complete (documentUri, version, doc.Text, line, column)

                        postToEditor
                            webView
                            (EditorBridge.encodeNotification (CompletionResolved(envelope.Id, documentUri, items)))
                    }
                    :> Task)
                    |> ignore
                | _ -> ()
```
This references a new `HostNotification.CompletionResolved` case — add it now. In `src/FSharpQueryStudio.Protocol/HostNotification.fs`, add a case and payload type next to `SignatureHelpResolved`:
```fsharp
    | CompletionResolved of requestId: string * documentUri: string * items: string list
```
```fsharp
type CompletionResolvedPayload = { DocumentUri: string; Items: string list }
```
In `src/FSharpQueryStudio.Editor/EditorBridge.fs`'s `encodeNotification`, add a case next to `SignatureHelpResolved`:
```fsharp
        | CompletionResolved(requestId, uri, items) ->
            { Id = requestId
              Kind = "completionResolved"
              Payload =
                JsonSerializer.SerializeToElement<CompletionResolvedPayload>(
                    { DocumentUri = uri; Items = items },
                    Serialization.options
                ) }
```

- [ ] **Step 9: Wire the composition root**

```fsharp
            let completeAsync (documentUri: string, version: int, source: string, line: int, column: int) =
                AnalysisService.complete client documentUri version source line column

            mainWindow.ShellViewModel.CompleteAsync <- Some completeAsync
```

- [ ] **Step 10: Register Monaco's completion provider in `editor-host.html`**

```javascript
        var pendingCompletionResolvers = {};
```
```javascript
        monaco.languages.registerCompletionItemProvider("fsharp", {
          provideCompletionItems: function (model, position) {
            return new Promise(function (resolve) {
              var id = crypto.randomUUID();
              pendingCompletionResolvers[id] = { resolve: resolve, position: position };

              postToHostWithId(id, "completionRequested", {
                documentUri: documentUri,
                version: currentVersion,
                line: position.lineNumber,
                column: position.column - 1
              });
            });
          }
        });
```
```javascript
              case "completionResolved": {
                var pendingCompletion = pendingCompletionResolvers[envelope.id];

                if (pendingCompletion) {
                  delete pendingCompletionResolvers[envelope.id];
                  var pos = pendingCompletion.position;

                  var suggestions = envelope.payload.items.map(function (name) {
                    return {
                      label: name,
                      kind: monaco.languages.CompletionItemKind.Text,
                      insertText: name,
                      range: {
                        startLineNumber: pos.lineNumber,
                        endLineNumber: pos.lineNumber,
                        startColumn: pos.column,
                        endColumn: pos.column
                      }
                    };
                  });

                  pendingCompletion.resolve({ suggestions: suggestions });
                }

                break;
              }
```

- [ ] **Step 11: Build and run the full solution test suite**

Run: `dotnet build && dotnet test`
Expected: clean build, all green except pre-existing `RequiresDocker` failures.

- [ ] **Step 12: Commit**

```bash
git add src/FSharpQueryStudio.Protocol/Payloads.fs src/FSharpQueryStudio.Protocol/HostNotification.fs src/FSharpQueryStudio.Editor/EditorBridge.fs src/FSharpQueryStudio.Worker/Handlers.fs src/FSharpQueryStudio.App/AnalysisService.fs src/FSharpQueryStudio.Ui/ShellViewModel.fs src/FSharpQueryStudio.Ui/MainWindow.axaml.fs src/FSharpQueryStudio.App/App.axaml.fs src/FSharpQueryStudio.Editor/EditorAssets/editor-host.html tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs tests/FSharpQueryStudio.Ui.Tests/ShellViewModelTests.fs
git commit -m "Feature: completions wired end to end — worker dispatch, host service, and a real Monaco provider"
```

---

### Task 5: Rename — the one feature missing at every layer

**Files:**
- Modify: `src/FSharpQueryStudio.Protocol/Payloads.fs`, `src/FSharpQueryStudio.Protocol/EditorRequest.fs`, `src/FSharpQueryStudio.Protocol/HostNotification.fs`
- Modify: `src/FSharpQueryStudio.Editor/EditorBridge.fs`
- Modify: `src/FSharpQueryStudio.Worker/Handlers.fs`
- Modify: `src/FSharpQueryStudio.App/AnalysisService.fs`, `src/FSharpQueryStudio.App/App.axaml.fs`
- Modify: `src/FSharpQueryStudio.Ui/ShellViewModel.fs`, `src/FSharpQueryStudio.Ui/MainWindow.axaml.fs`
- Modify: `src/FSharpQueryStudio.Editor/EditorAssets/editor-host.html`
- Test: `tests/FSharpQueryStudio.Editor.Tests/*` (bridge contract), `tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs`, `tests/FSharpQueryStudio.Ui.Tests/ShellViewModelTests.fs`

**Interfaces:**
- Consumes: `FSharpQueryStudio.Analysis.Rename.findReferences` (existing, real, currently called by nothing — see `src/FSharpQueryStudio.Analysis/Rename.fs`; its own local `RenameEdit` type is mapped to a new `Protocol.RenameEdit` in the Worker handler, the same separation `DiagnosticMapping.map` already establishes for `analysis/check`).
- Produces: `EditorRequest.RenameRequested`, `HostNotification.RenameResolved`, `"analysis/rename"` dispatch, `AnalysisService.rename`, `ShellViewModel.RenameAsync`, a Monaco `registerRenameProvider`. Nothing later depends on this task.

- [ ] **Step 1: Add the Protocol types**

In `src/FSharpQueryStudio.Protocol/Payloads.fs`, add after `AnalysisCompletionResult`:
```fsharp
/// One reference's span to rewrite as part of a rename — Milestone
/// E.8. 0-based lines, matching `Diagnostic`/`DefinitionLocation`.
/// Mirrors `Analysis.Rename.RenameEdit` exactly; kept as a separate
/// Protocol-level type rather than reused directly so `Analysis`'s
/// internal representation can change without becoming a wire-format
/// change, the same separation `DiagnosticMapping` already keeps
/// between FCS's own diagnostic type and `Protocol.Diagnostic`.
type RenameEdit =
    { StartLine: int
      StartColumn: int
      EndLine: int
      EndColumn: int }

/// `analysis/rename` request payload.
type AnalysisRenameParams =
    { DocumentUri: string
      Version: int
      Source: string
      Line: int
      Column: int }

/// `None` means nothing resolvable at that position — same convention
/// as `AnalysisDefinitionResult`/`AnalysisSignatureHelpResult`.
type AnalysisRenameResult = { Edits: RenameEdit list option }
```

In `src/FSharpQueryStudio.Protocol/EditorRequest.fs`, add a case to `EditorRequest` and its payload type:
```fsharp
    | RenameRequested of documentUri: string * version: int * line: int * column: int * newName: string
```
```fsharp
type RenameRequestedPayload =
    { DocumentUri: string
      Version: int
      Line: int
      Column: int
      NewName: string }
```

In `src/FSharpQueryStudio.Protocol/HostNotification.fs`, add a case and payload type:
```fsharp
    | RenameResolved of requestId: string * documentUri: string * edits: RenameEdit list option * newName: string
```
```fsharp
type RenameResolvedPayload =
    { DocumentUri: string
      Edits: RenameEdit list option
      NewName: string }
```

- [ ] **Step 2: Write the failing `EditorBridge` contract tests**

Add to `tests/FSharpQueryStudio.Editor.Tests/EditorBridgeTests.fs` (find the file's existing `hoverRequested`/`hoverResolved` round-trip tests and mirror their exact shape):
```fsharp
[<Fact>]
let ``encode then decode round-trips a RenameRequested request`` () =
    let request = RenameRequested("untitled:Script1.fsx", 3, 1, 4, "newName")
    let envelope = EditorBridge.encode request

    Assert.Equal("renameRequested", envelope.Kind)
    Assert.Equal(Ok request, EditorBridge.decode envelope)

[<Fact>]
let ``encodeNotification produces a well-formed renameResolved envelope`` () =
    let edits =
        Some
            [ { StartLine = 0
                StartColumn = 4
                EndLine = 0
                EndColumn = 5 } ]

    let envelope =
        EditorBridge.encodeNotification (RenameResolved("req1", "untitled:Script1.fsx", edits, "newName"))

    Assert.Equal("req1", envelope.Id)
    Assert.Equal("renameResolved", envelope.Kind)

    let payload =
        JsonSerializer.Deserialize<RenameResolvedPayload>(envelope.Payload, Serialization.options)

    Assert.Equal(edits, payload.Edits)
    Assert.Equal("newName", payload.NewName)
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test tests/FSharpQueryStudio.Editor.Tests --filter "FullyQualifiedName~RenameRequested|FullyQualifiedName~renameResolved"`
Expected: FAIL to compile — `EditorBridge` doesn't handle `RenameRequested`/`RenameResolved` yet.

- [ ] **Step 4: Implement `EditorBridge`'s encode/decode**

In `src/FSharpQueryStudio.Editor/EditorBridge.fs`:

Add to `kindOf`:
```fsharp
        | RenameRequested _ -> "renameRequested"
```
Add to `encodePayload`:
```fsharp
        | RenameRequested(uri, version, line, column, newName) ->
            JsonSerializer.SerializeToElement<RenameRequestedPayload>(
                ({ DocumentUri = uri
                   Version = version
                   Line = line
                   Column = column
                   NewName = newName }: RenameRequestedPayload),
                Serialization.options
            )
```
Add to `encodeNotification`'s match:
```fsharp
        | RenameResolved(requestId, uri, edits, newName) ->
            { Id = requestId
              Kind = "renameResolved"
              Payload =
                JsonSerializer.SerializeToElement<RenameResolvedPayload>(
                    { DocumentUri = uri
                      Edits = edits
                      NewName = newName },
                    Serialization.options
                ) }
```
Add to `decode`'s match:
```fsharp
            | "renameRequested" ->
                let p =
                    JsonSerializer.Deserialize<RenameRequestedPayload>(envelope.Payload, Serialization.options)

                Ok(RenameRequested(p.DocumentUri, p.Version, p.Line, p.Column, p.NewName))
```

- [ ] **Step 5: Run to verify they pass, then run the full Editor.Tests project**

Run: `dotnet test tests/FSharpQueryStudio.Editor.Tests` → all green.

- [ ] **Step 6: Write the failing Worker test**

Add to `tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs`:
```fsharp
let private renameRequest (id: string) (source: string) (line: int) (column: int) : WorkerRequestFrame =
    { Id = id
      Method = "analysis/rename"
      Params =
        JsonSerializer.SerializeToElement<AnalysisRenameParams>(
            { DocumentUri = "untitled:Script1.fsx"
              Version = 1
              Source = source
              Line = line
              Column = column },
            Serialization.options
        ) }

[<Fact>]
let ``dispatch on analysis/rename finds every reference to the symbol at the given position`` () =
    task {
        let source = "let value = 1\nlet total = value + value"
        let! response = Handlers.dispatch (renameRequest "rn1" source 1 4)

        Assert.True(response.Error.IsNone)
        let result = JsonSerializer.Deserialize<AnalysisRenameResult>(response.Result.Value, Serialization.options)
        Assert.True(result.Edits.IsSome)
        Assert.Equal(3, result.Edits.Value.Length) // the declaration plus two uses
    }

[<Fact>]
let ``dispatch on analysis/rename returns no edits for a position with no resolvable symbol`` () =
    task {
        let! response = Handlers.dispatch (renameRequest "rn2" "// just a comment" 1 5)

        Assert.True(response.Error.IsNone)
        let result = JsonSerializer.Deserialize<AnalysisRenameResult>(response.Result.Value, Serialization.options)
        Assert.True(result.Edits.IsNone)
    }
```

- [ ] **Step 7: Run to verify it fails, then implement**

Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests --filter "FullyQualifiedName~analysis/rename"` → FAIL (compile error, no such method/dispatch case).

In `src/FSharpQueryStudio.Worker/Handlers.fs`, add after `handleAnalysisComplete`:
```fsharp
    /// `analysis/rename` — Milestone E.8. `Analysis.Rename` only locates
    /// references; this handler's own job is mapping its internal
    /// `RenameEdit` to the protocol-level one, the same separation
    /// `DiagnosticMapping` keeps for `analysis/check`.
    let private toProtocolRenameEdit (edit: FSharpQueryStudio.Analysis.Rename.RenameEdit) : RenameEdit =
        { StartLine = edit.StartLine
          StartColumn = edit.StartColumn
          EndLine = edit.EndLine
          EndColumn = edit.EndColumn }

    let handleAnalysisRename (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            let renameParams =
                JsonSerializer.Deserialize<AnalysisRenameParams>(request.Params, Serialization.options)

            let workDir =
                Path.Combine(Path.GetTempPath(), "fqs-analysis-" + Guid.NewGuid().ToString("N"))

            Directory.CreateDirectory(workDir) |> ignore
            let context = AnalysisContext.build workDir

            let! edits =
                Rename.findReferences context renameParams.Source renameParams.Line renameParams.Column

            let result: AnalysisRenameResult =
                { Edits = edits |> Option.map (List.map toProtocolRenameEdit) }

            return
                { Id = request.Id
                  Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
                  Error = None }
        }
```
Add to `dispatch`:
```fsharp
            | "analysis/rename" -> return! handleAnalysisRename request
```

- [ ] **Step 8: Run to verify it passes**

Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests --filter "FullyQualifiedName~analysis/rename"` → PASS, both tests.

- [ ] **Step 9: Add `AnalysisService.rename`**

In `src/FSharpQueryStudio.App/AnalysisService.fs`:
```fsharp
    /// `analysis/rename` — Milestone E.8. Same shape/rationale as `hover`.
    let rename
        (client: WorkerClient)
        (documentUri: string)
        (version: int)
        (source: string)
        (line: int)
        (column: int)
        : Task<RenameEdit list option> =
        task {
            let request: WorkerRequestFrame =
                { Id = Guid.NewGuid().ToString()
                  Method = "analysis/rename"
                  Params =
                    JsonSerializer.SerializeToElement<AnalysisRenameParams>(
                        ({ DocumentUri = documentUri
                           Version = version
                           Source = source
                           Line = line
                           Column = column }: AnalysisRenameParams),
                        Serialization.options
                    ) }

            let! response = client.Send(request)

            match response.Result with
            | Some resultJson ->
                let result = JsonSerializer.Deserialize<AnalysisRenameResult>(resultJson, Serialization.options)
                return result.Edits
            | None -> return None
        }
```

- [ ] **Step 10: Write the failing `ShellViewModel` test, then add the hook**

```fsharp
[<Fact>]
let ``RenameAsync is settable and reachable as a plain hook, no-op when unconfigured`` () =
    task {
        let vm = ShellViewModel()
        Assert.Equal(None, vm.RenameAsync)

        vm.RenameAsync <-
            Some(fun (_uri, _version, _source, _line, _column) ->
                Task.FromResult(Some [ { StartLine = 0; StartColumn = 4; EndLine = 0; EndColumn = 5 } ]))

        let! result = vm.RenameAsync.Value ("untitled:Script1.fsx", 1, "let x = 1", 1, 4)

        Assert.Equal(1, result.Value.Length)
    }
```
Run to verify it fails, then add to `ShellViewModel.fs`:
```fsharp
    /// `analysis/rename` — same shape/rationale as `HoverAsync`. The
    /// requested `newName` isn't part of this hook's own signature — the
    /// bridge case in `MainWindow` carries it separately, since Monaco's
    /// own rename UI collects it and this hook only ever needs to locate
    /// the edits; applying the actual rewrite with `newName` substituted
    /// is `editor-host.html`'s job once `RenameResolved` comes back.
    member val RenameAsync: ((string * int * string * int * int) -> Task<RenameEdit list option>) option =
        None with get, set
```
Run to verify it passes.

- [ ] **Step 11: Extend `MainWindow.axaml.fs`'s bridge dispatch**

```fsharp
            | Ok(RenameRequested(documentUri, version, line, column, newName)) ->
                match
                    shellViewModel.Documents |> List.tryFind (fun d -> d.Uri = documentUri), shellViewModel.RenameAsync
                with
                | Some doc, Some rename ->
                    (task {
                        let! edits = rename (documentUri, version, doc.Text, line, column)

                        postToEditor
                            webView
                            (EditorBridge.encodeNotification (
                                RenameResolved(envelope.Id, documentUri, edits, newName)
                            ))
                    }
                    :> Task)
                    |> ignore
                | _ -> ()
```

- [ ] **Step 12: Wire the composition root**

```fsharp
            let renameAsync (documentUri: string, version: int, source: string, line: int, column: int) =
                AnalysisService.rename client documentUri version source line column

            mainWindow.ShellViewModel.RenameAsync <- Some renameAsync
```

- [ ] **Step 13: Register Monaco's rename provider in `editor-host.html`**

```javascript
        var pendingRenameResolvers = {};
```
```javascript
        monaco.languages.registerRenameProvider("fsharp", {
          provideRenameEdits: function (model, position, newName) {
            return new Promise(function (resolve) {
              var id = crypto.randomUUID();
              pendingRenameResolvers[id] = resolve;

              postToHostWithId(id, "renameRequested", {
                documentUri: documentUri,
                version: currentVersion,
                line: position.lineNumber,
                column: position.column - 1,
                newName: newName
              });
            });
          }
        });
```
```javascript
              case "renameResolved": {
                var resolveRename = pendingRenameResolvers[envelope.id];

                if (resolveRename) {
                  delete pendingRenameResolvers[envelope.id];
                  var edits = envelope.payload.edits;

                  if (edits) {
                    resolveRename({
                      edits: edits.map(function (e) {
                        return {
                          resource: editor.getModel().uri,
                          textEdit: {
                            range: {
                              startLineNumber: e.startLine + 1,
                              startColumn: e.startColumn + 1,
                              endLineNumber: e.endLine + 1,
                              endColumn: e.endColumn + 1
                            },
                            text: envelope.payload.newName
                          }
                        };
                      })
                    });
                  } else {
                    resolveRename({ edits: [] });
                  }
                }

                break;
              }
```

- [ ] **Step 14: Build and run the full solution test suite**

Run: `dotnet build && dotnet test`
Expected: clean build, all green except pre-existing `RequiresDocker` failures.

- [ ] **Step 15: Commit**

```bash
git add src/FSharpQueryStudio.Protocol/Payloads.fs src/FSharpQueryStudio.Protocol/EditorRequest.fs src/FSharpQueryStudio.Protocol/HostNotification.fs src/FSharpQueryStudio.Editor/EditorBridge.fs src/FSharpQueryStudio.Worker/Handlers.fs src/FSharpQueryStudio.App/AnalysisService.fs src/FSharpQueryStudio.App/App.axaml.fs src/FSharpQueryStudio.Ui/ShellViewModel.fs src/FSharpQueryStudio.Ui/MainWindow.axaml.fs src/FSharpQueryStudio.Editor/EditorAssets/editor-host.html tests/FSharpQueryStudio.Editor.Tests/EditorBridgeTests.fs tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs tests/FSharpQueryStudio.Ui.Tests/ShellViewModelTests.fs
git commit -m "Feature: rename wired end to end — the last E.8 gap, built at every layer from scratch"
```

---

### Task 6: DEVLOG entry for the whole plan

**Files:**
- Modify: `docs/DEVLOG.md`

- [ ] **Step 1: Write one entry summarizing Tasks 1–5**

Per this repo's established convention: what was actually broken (Monaco sent hover/definition/signature-help/completion requests that were never answered — `MainWindow.axaml.fs`'s bridge dispatch had exactly one real case, `TextChanged`; completions and rename had no backend at all despite B.10/B.11/E.8 being marked done), what now exists end to end for each of the five features, test counts before/after per project, and the same manual-verify caveat every prior `editor-host.html` task in this log carries — the JS-side behavior (does the hover popup/definition jump/signature tooltip/completion list/rename widget actually appear correctly on screen) is not verifiable without a display in this environment, same as B.6/B.9/E.6/E.7 before it.

- [ ] **Step 2: Commit**

```bash
git add docs/DEVLOG.md
git commit -m "Docs: DEVLOG entry for the editor intelligence wiring plan"
```

## Self-Review Notes

- **Spec coverage:** every dead-on-arrival request found in the investigation (`Hover`/`GoToDefinition`/`SignatureHelp`/`Completion`/`Rename`Requested, all falling into `MainWindow.axaml.fs`'s catch-all no-op) now has a real path from Monaco to the worker and back. `AnalysisService.hover`/`definition`/`signatureHelp` (previously dead code, uncalled by anything except their own removed-from-this-plan unit tests) are now actually reachable.
- **Explicitly out of scope, not silently dropped:** tier-1 completions' `recentNames` (the "most recent successful check's declared names" the architecture spec describes) is passed as `[]` — nothing upstream tracks that yet, and building that tracking is a separate, independently-sized feature (a cache keyed by document, invalidated on the next `analysis/check`), not a one-line addition to this plan. Cross-file go-to-definition (jumping to a location outside the currently-open document) resolves `null` rather than erroring — this editor hosts one Monaco model at a time with no multi-file project view to jump into yet (that's Milestone E.5's `.fsproj` workspace support, a different subsystem). Monaco `CompletionItemKind` is always `Text` — no per-kind icons (function vs. keyword vs. connector helper) — a real but small enrichment left for later. Rename's Monaco-side `provideRenameEdits` doesn't validate `newName` (e.g. reject an F# keyword or invalid identifier) before sending it — the backend has no such validation either; flagged, not fixed here.
- **Type consistency check:** every new hook on `ShellViewModel` (`HoverAsync`, `DefinitionAsync`, `SignatureHelpAsync`, `CompleteAsync`, `RenameAsync`) uses the identical `(string * int * string * int * int)` tuple shape (`documentUri, version, source, line, column`) so `MainWindow.axaml.fs`'s five bridge cases read uniformly — verified against each task's own code block above, not just asserted here.
