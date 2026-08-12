# Results Export (and Full-Result Retention) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Retain a script's `Results.table`-pushed result in full (never truncated) in the Worker's memory for as long as its tab stays open, add an "Export CSV…" toolbar button that writes that retained result straight to a file, evict a tab's retained entry when it closes, and let the Results grid show the entire retained result (already virtualized by the DataGrid — no truncation left to hide behind).

**Architecture:** New `ResultRetention` module (`FSharpQueryStudio.Results`) is a `ConcurrentDictionary<string, RetainedResult>` keyed by document URI, living in Worker-process memory, independent of the existing per-evaluation `Results.explicit` state. `ScriptResults.table` stops truncating and fully materializes `query.Rows`. `EvaluateParams` gains a required `DocumentUri` field so `runEvaluate` knows which tab to commit retention under. Two new Worker RPCs — `results/exportCsv` (writes the retained result via the already-existing, already-tested `CsvExport.write`) and `results/discard` (evicts a tab's entry, called when its tab closes). `App.ExportService` is a thin wrapper over both; `Ui` gets a new toolbar button and a `ShellViewModel.CloseDocument` eviction hook.

**Tech Stack:** F# / .NET 10, `FSharpQueryStudio.Results`/`.Worker`/`.App`/`.Ui`/`.Protocol` projects, xUnit, `System.Collections.Concurrent.ConcurrentDictionary`, Avalonia `IStorageProvider` save dialog.

## Global Constraints

- Qualified `ResultKind.Table`/`Result.Error` everywhere — an unqualified `Error` case on `ResultKind` shadows `FSharp.Core`'s `Result.Error` wherever `open FSharpQueryStudio.Protocol` is in scope. This has caused real bugs earlier in this codebase's history; always qualify.
- `EvaluateParams.DocumentUri` is a **required, always-populated** field (not `string option`) — both Isolated and Persistent modes send the running tab's own `Uri`. This is a breaking change to every existing `EvaluateParams` record-literal construction site; Task 3 fixes all of them in the same task that adds the field, so the solution compiles at every task boundary.
- Retention is committed **only** inside `runEvaluate`'s `Some explicitEnvelope ->` arm (checking `Results.currentEnvelope`'s own `Some`/`None`, not `envelope.Kind` after the generic path also runs) and only when `explicitEnvelope.Kind = ResultKind.Table`. The generic FSI-tail-value projection path (`Projection.projectWithCursor`, `results/page`, `currentPaging`) is untouched — do not extend or reuse it for this feature.
- A run that doesn't call `Results.table` (a bare `printfn`, a `Results.text` push, an error) must leave that tab's **existing** retained entry untouched, not clear it.
- No new truncation/paging UI work is needed for the Results grid — `ResultsView.axaml`'s `DataGrid` already binds `ItemsSource="{Binding Result.Rows}"` directly and already row-virtualizes (built for C.12). Once `ScriptResults.table` stops truncating, the grid shows everything with no code change of its own.
- `CsvExport.write` (`FSharpQueryStudio.Results/CsvExport.fs`) already exists, is already fully tested, and needs no changes — it already streams every row from an `obj array seq` with no truncation.
- TDD per file, matching this codebase's established per-layer test conventions (module name = filename in `Results.Tests`, `[<Fact>]` with descriptive names, real I/O wrapped in `try`/`with` where the app must never crash).

---

### Task 1: `ResultRetention` module

**Files:**
- Create: `src/FSharpQueryStudio.Results/ResultRetention.fs`
- Modify: `src/FSharpQueryStudio.Results/FSharpQueryStudio.Results.fsproj` (add the new file to `<ItemGroup>`, after `CsvExport.fs`)
- Test: `tests/FSharpQueryStudio.Results.Tests/ResultRetentionTests.fs`
- Modify: `tests/FSharpQueryStudio.Results.Tests/FSharpQueryStudio.Results.Tests.fsproj` (add the new test file, after `CsvExportTests.fs`)

**Interfaces:**
- Produces: `RetainedResult = { Columns: string list; Rows: obj array array }`, `ResultRetention.set (documentUri: string) (result: RetainedResult) : unit`, `ResultRetention.tryGet (documentUri: string) : RetainedResult option`, `ResultRetention.discard (documentUri: string) : unit`. Task 4 (Worker) is the sole consumer.

- [ ] **Step 1: Write the failing tests**

```fsharp
module ResultRetentionTests

open Xunit
open FSharpQueryStudio.Results

[<Fact>]
let ``tryGet on a key nothing was ever set under returns None`` () =
    Assert.True((ResultRetention.tryGet "untitled:NeverSet.fsx").IsNone)

[<Fact>]
let ``set then tryGet round-trips the same columns and rows`` () =
    let result: RetainedResult =
        { Columns = [ "id"; "name" ]
          Rows = [| [| box 1; box "Ada" |]; [| box 2; box "Alan" |] |] }

    ResultRetention.set "untitled:Script1.fsx" result

    let retrieved = (ResultRetention.tryGet "untitled:Script1.fsx").Value
    Assert.Equal<string list>(result.Columns, retrieved.Columns)
    Assert.Equal<obj array array>(result.Rows, retrieved.Rows)

[<Fact>]
let ``set overwrites an existing entry for the same key`` () =
    let first: RetainedResult = { Columns = [ "a" ]; Rows = [| [| box 1 |] |] }
    let second: RetainedResult = { Columns = [ "b" ]; Rows = [| [| box 2 |] |] }

    ResultRetention.set "untitled:Overwrite.fsx" first
    ResultRetention.set "untitled:Overwrite.fsx" second

    let retrieved = (ResultRetention.tryGet "untitled:Overwrite.fsx").Value
    Assert.Equal<string list>([ "b" ], retrieved.Columns)

[<Fact>]
let ``discard removes a previously-set entry`` () =
    ResultRetention.set "untitled:ToDiscard.fsx" { Columns = [ "a" ]; Rows = [| [| box 1 |] |] }

    ResultRetention.discard "untitled:ToDiscard.fsx"

    Assert.True((ResultRetention.tryGet "untitled:ToDiscard.fsx").IsNone)

[<Fact>]
let ``discard on a key nothing was ever set under does not throw`` () =
    ResultRetention.discard "untitled:NeverSetEither.fsx" // Must not throw.
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FSharpQueryStudio.Results.Tests --filter FullyQualifiedName~ResultRetentionTests`
Expected: FAIL to compile — `ResultRetention`/`RetainedResult` don't exist yet.

- [ ] **Step 3: Write the module**

```fsharp
namespace FSharpQueryStudio.Results

open System.Collections.Concurrent

/// A tab's full, never-truncated `Results.table` push, retained for as
/// long as the app runs — see docs/superpowers/specs/2026-08-12-results-export-design.md.
type RetainedResult = { Columns: string list; Rows: obj array array }

/// Keyed by document URI, one entry per open tab that has ever pushed a
/// table result. Lives in the Worker process's own memory, independent of
/// `Results.explicit` (which `Results.reset()` clears before every single
/// evaluation) — nothing here is ever cleared except by an explicit `set`
/// (a later run of the same tab overwrites it) or `discard` (the tab
/// closes). Never persisted to disk; gone on process exit by design.
module ResultRetention =
    let private store = ConcurrentDictionary<string, RetainedResult>()

    let set (documentUri: string) (result: RetainedResult) : unit = store.[documentUri] <- result

    let tryGet (documentUri: string) : RetainedResult option =
        match store.TryGetValue(documentUri) with
        | true, result -> Some result
        | false, _ -> None

    let discard (documentUri: string) : unit = store.TryRemove(documentUri) |> ignore
```

Add to `FSharpQueryStudio.Results.fsproj`:

```xml
<ItemGroup>
  <Compile Include="PagingCursor.fs" />
  <Compile Include="Projection.fs" />
  <Compile Include="ScriptResults.fs" />
  <Compile Include="CsvExport.fs" />
  <Compile Include="ResultRetention.fs" />
</ItemGroup>
```

Add to `FSharpQueryStudio.Results.Tests.fsproj`:

```xml
<ItemGroup>
  <Compile Include="PagingCursorTests.fs" />
  <Compile Include="CsvExportTests.fs" />
  <Compile Include="ResultRetentionTests.fs" />
  <Compile Include="ScriptResultsTests.fs" />
  <Compile Include="Tests.fs" />
</ItemGroup>
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FSharpQueryStudio.Results.Tests --filter FullyQualifiedName~ResultRetentionTests`
Expected: PASS, all 5 tests.

- [ ] **Step 5: Commit**

```bash
git add src/FSharpQueryStudio.Results/ResultRetention.fs src/FSharpQueryStudio.Results/FSharpQueryStudio.Results.fsproj tests/FSharpQueryStudio.Results.Tests/ResultRetentionTests.fs tests/FSharpQueryStudio.Results.Tests/FSharpQueryStudio.Results.Tests.fsproj
git commit -m "feat: add ResultRetention module for per-tab full-result retention"
```

---

### Task 2: `ScriptResults.table` fully materializes instead of truncating

**Files:**
- Modify: `src/FSharpQueryStudio.Results/ScriptResults.fs`
- Modify: `tests/FSharpQueryStudio.Results.Tests/ScriptResultsTests.fs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `Results.table`'s `currentEnvelope` `Table` case now carries every row, `IsTruncated` always `false` for this path. Task 4 reads `ResultEnvelope.Schema`/`Rows` from this same `currentEnvelope` result to commit retention.

The existing test `` table truncates to the preview row limit and marks IsTruncated, same bound as generic Table projection `` (`tests/FSharpQueryStudio.Results.Tests/ScriptResultsTests.fs`) asserts the *old*, truncating behavior and must be replaced, not left in place — its assertions directly contradict this task's change.

- [ ] **Step 1: Replace the outdated test with one asserting full materialization**

In `tests/FSharpQueryStudio.Results.Tests/ScriptResultsTests.fs`, replace the `` table truncates to the preview row limit and marks IsTruncated, same bound as generic Table projection `` test with:

```fsharp
[<Fact>]
let ``table fully materializes every row, even past the preview row limit, and never marks IsTruncated`` () =
    Results.reset ()

    let rowCount = Projection.PreviewRowLimit + 10
    let manyRows = seq { for i in 1..rowCount -> [| box i |] }

    let query: ConnectorQueryResult = { Columns = [ "n" ]; Rows = manyRows }

    let returned = Results.table query
    Assert.Same(query, returned)

    let envelope = (Results.currentEnvelope 0L []).Value

    Assert.Equal(rowCount, envelope.Rows.Length)
    Assert.False(envelope.IsTruncated)
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/FSharpQueryStudio.Results.Tests --filter FullyQualifiedName~ScriptResultsTests`
Expected: FAIL — the new assertion (`Equal(rowCount, envelope.Rows.Length)`) fails against the still-truncating implementation (`envelope.Rows.Length` is `Projection.PreviewRowLimit`, not `rowCount`).

- [ ] **Step 3: Change `ScriptResults.table` to fully materialize**

In `src/FSharpQueryStudio.Results/ScriptResults.fs`, replace:

```fsharp
    type private Explicit =
        | Table of Column list * obj array array * isTruncated: bool
        | Text of string
```

with:

```fsharp
    type private Explicit =
        | Table of Column list * obj array array
        | Text of string
```

Replace the `table` function body:

```fsharp
    let table (query: ConnectorQueryResult) : ConnectorQueryResult =
        let columns =
            query.Columns |> List.map (fun name -> { Name = name; DisplayType = "obj" })

        let sampled = query.Rows |> Seq.truncate (Projection.PreviewRowLimit + 1) |> Array.ofSeq
        let isTruncated = sampled.Length > Projection.PreviewRowLimit

        let rows =
            if isTruncated then
                sampled.[.. Projection.PreviewRowLimit - 1]
            else
                sampled

        explicit <- Some(Table(columns, rows, isTruncated))
        query
```

with:

```fsharp
    /// Fully materializes `query.Rows` — a deliberate memory trade-off:
    /// every `Results.table`-produced result now sits entirely in the
    /// Worker's memory for as long as its tab stays open (via
    /// `ResultRetention`, committed by `runEvaluate`), where before only
    /// `Projection.PreviewRowLimit + 1` rows were ever even read off the
    /// connector's stream. See docs/superpowers/specs/2026-08-12-results-export-design.md.
    let table (query: ConnectorQueryResult) : ConnectorQueryResult =
        let columns =
            query.Columns |> List.map (fun name -> { Name = name; DisplayType = "obj" })

        let allRows = query.Rows |> Array.ofSeq

        explicit <- Some(Table(columns, allRows))
        query
```

Update `currentEnvelope`'s match arm:

```fsharp
            | Table(columns, rows, isTruncated) ->
                { Kind = ResultKind.Table
                  DisplayText = None
                  Schema = columns
                  Rows = rows
                  IsTruncated = isTruncated
                  ContinuationToken = None
                  ElapsedMs = elapsedMs
                  Output = output }
```

to:

```fsharp
            | Table(columns, rows) ->
                { Kind = ResultKind.Table
                  DisplayText = None
                  Schema = columns
                  Rows = rows
                  IsTruncated = false
                  ContinuationToken = None
                  ElapsedMs = elapsedMs
                  Output = output }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FSharpQueryStudio.Results.Tests`
Expected: PASS, full `Results.Tests` suite (including the other pre-existing `` table pushes... `` test, which asserts on a 1-row result and is unaffected by this change).

- [ ] **Step 5: Commit**

```bash
git add src/FSharpQueryStudio.Results/ScriptResults.fs tests/FSharpQueryStudio.Results.Tests/ScriptResultsTests.fs
git commit -m "feat: Results.table fully materializes rows instead of truncating"
```

---

### Task 3: Protocol — `EvaluateParams.DocumentUri`, `ExportCsvParams`, `ResultsDiscardParams`

This task adds a **required** field to `EvaluateParams`, which is a breaking change to every existing construction site in the solution. To keep the solution compiling at the end of this task (the Global Constraint above), this task also mechanically fixes all seven sites — six of them with a fixed placeholder value (since these are pre-existing tests unrelated to retention, which gets its own dedicated test helper in Task 4), one (the real production site, `ShellViewModel.RunAsync`) with the real value already in scope.

**Files:**
- Modify: `src/FSharpQueryStudio.Protocol/Payloads.fs`
- Modify: `src/FSharpQueryStudio.Ui/ShellViewModel.fs` (the `evalParams` construction inside `RunAsync`)
- Modify: `tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs` (`evaluateRequest`, `persistentEvaluateRequest`)
- Modify: `tests/FSharpQueryStudio.Worker.Tests/HandlersLoggingTests.fs` (`evaluateRequest`)
- Modify: `tests/FSharpQueryStudio.Integration.Tests/WorkerClientTests.fs` (`evaluateRequest`, `persistentEvaluateRequest`)
- Modify: `tests/FSharpQueryStudio.Integration.Tests/WorkerClientLoggingTests.fs` (`evaluateRequest`)
- Modify: `tests/FSharpQueryStudio.Protocol.Tests/Tests.fs` (the `EvaluateParams round-trips...` test's two literals)

**Interfaces:**
- Produces: `EvaluateParams.DocumentUri: string` (required), `ExportCsvParams = { DocumentUri: string; FilePath: string }`, `ResultsDiscardParams = { DocumentUri: string }`. Task 4 (Worker) reads `evalParams.DocumentUri` and deserializes `ExportCsvParams`/`ResultsDiscardParams`. Task 5 (Ui) is the real producer of `EvaluateParams.DocumentUri` in production.

- [ ] **Step 1: Add the new field and types to `Payloads.fs`**

In `src/FSharpQueryStudio.Protocol/Payloads.fs`, replace:

```fsharp
type EvaluateParams =
    { Source: string
      SessionId: string option
      Mode: SessionMode }
```

with:

```fsharp
/// `DocumentUri` is always populated (unlike `SessionId`, which is `None`
/// for Isolated mode) — it names the tab that's running, independent of
/// session semantics, so `runEvaluate` knows which tab's retained result
/// (see `FSharpQueryStudio.Results.ResultRetention`) to commit into when
/// the script pushes a table. Additive: `SessionId`'s own meaning is
/// untouched.
type EvaluateParams =
    { Source: string
      SessionId: string option
      Mode: SessionMode
      DocumentUri: string }
```

At the end of the file, after `ResultPage`, add:

```fsharp
/// `results/exportCsv` request payload. `FilePath` is a real filesystem
/// path chosen by the user via the UI's own Save File dialog — the worker
/// never picks or prompts for a path itself.
type ExportCsvParams = { DocumentUri: string; FilePath: string }

/// `results/discard` request payload — evicts `DocumentUri`'s retained
/// result, if any (see `ResultRetention.discard`). Always succeeds, even
/// for a `DocumentUri` nothing is retained under.
type ResultsDiscardParams = { DocumentUri: string }
```

- [ ] **Step 2: Fix every existing `EvaluateParams` construction site**

In `tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs`, change:

```fsharp
let private evaluateRequest (id: string) (source: string) : WorkerRequestFrame =
    { Id = id
      Method = "execution/evaluate"
      Params =
        JsonSerializer.SerializeToElement<EvaluateParams>(
            { Source = source; SessionId = None; Mode = Isolated },
            Serialization.options
        ) }

let private persistentEvaluateRequest (id: string) (source: string) (sessionId: string) : WorkerRequestFrame =
    { Id = id
      Method = "execution/evaluate"
      Params =
        JsonSerializer.SerializeToElement<EvaluateParams>(
            { Source = source
              SessionId = Some sessionId
              Mode = Persistent },
            Serialization.options
        ) }
```

to:

```fsharp
let private evaluateRequest (id: string) (source: string) : WorkerRequestFrame =
    { Id = id
      Method = "execution/evaluate"
      Params =
        JsonSerializer.SerializeToElement<EvaluateParams>(
            { Source = source
              SessionId = None
              Mode = Isolated
              DocumentUri = "untitled:Test.fsx" },
            Serialization.options
        ) }

let private persistentEvaluateRequest (id: string) (source: string) (sessionId: string) : WorkerRequestFrame =
    { Id = id
      Method = "execution/evaluate"
      Params =
        JsonSerializer.SerializeToElement<EvaluateParams>(
            { Source = source
              SessionId = Some sessionId
              Mode = Persistent
              DocumentUri = "untitled:Test.fsx" },
            Serialization.options
        ) }
```

In `tests/FSharpQueryStudio.Worker.Tests/HandlersLoggingTests.fs`, change:

```fsharp
let private evaluateRequest (id: string) (source: string) : WorkerRequestFrame =
    { Id = id
      Method = "execution/evaluate"
      Params =
        JsonSerializer.SerializeToElement<EvaluateParams>(
            { Source = source; SessionId = None; Mode = Isolated },
            Serialization.options
        ) }
```

to:

```fsharp
let private evaluateRequest (id: string) (source: string) : WorkerRequestFrame =
    { Id = id
      Method = "execution/evaluate"
      Params =
        JsonSerializer.SerializeToElement<EvaluateParams>(
            { Source = source
              SessionId = None
              Mode = Isolated
              DocumentUri = "untitled:Test.fsx" },
            Serialization.options
        ) }
```

In `tests/FSharpQueryStudio.Integration.Tests/WorkerClientTests.fs`, apply the same two-helper fix as `HandlersTests.fs` (identical bodies, same `"untitled:Test.fsx"` placeholder):

```fsharp
let private evaluateRequest (id: string) (source: string) : WorkerRequestFrame =
    { Id = id
      Method = "execution/evaluate"
      Params =
        JsonSerializer.SerializeToElement<EvaluateParams>(
            { Source = source
              SessionId = None
              Mode = Isolated
              DocumentUri = "untitled:Test.fsx" },
            Serialization.options
        ) }

let private persistentEvaluateRequest (id: string) (source: string) (sessionId: string) : WorkerRequestFrame =
    { Id = id
      Method = "execution/evaluate"
      Params =
        JsonSerializer.SerializeToElement<EvaluateParams>(
            { Source = source
              SessionId = Some sessionId
              Mode = Persistent
              DocumentUri = "untitled:Test.fsx" },
            Serialization.options
        ) }
```

In `tests/FSharpQueryStudio.Integration.Tests/WorkerClientLoggingTests.fs`, apply the same single-helper fix as `HandlersLoggingTests.fs`:

```fsharp
let private evaluateRequest (id: string) (source: string) : WorkerRequestFrame =
    { Id = id
      Method = "execution/evaluate"
      Params =
        JsonSerializer.SerializeToElement<EvaluateParams>(
            { Source = source
              SessionId = None
              Mode = Isolated
              DocumentUri = "untitled:Test.fsx" },
            Serialization.options
        ) }
```

In `tests/FSharpQueryStudio.Protocol.Tests/Tests.fs`, change the `` EvaluateParams round-trips through JSON for both Isolated and Persistent modes `` test:

```fsharp
[<Fact>]
let ``EvaluateParams round-trips through JSON for both Isolated and Persistent modes`` () =
    let isolated: EvaluateParams =
        { Source = "1 + 1"; SessionId = None; Mode = Isolated }

    let isolatedJson = JsonSerializer.Serialize(isolated, Serialization.options)
    let roundTrippedIsolated = JsonSerializer.Deserialize<EvaluateParams>(isolatedJson, Serialization.options)
    Assert.Equal(isolated, roundTrippedIsolated)

    let persistent: EvaluateParams =
        { Source = "x + 1"
          SessionId = Some "sess-1"
          Mode = Persistent }

    let persistentJson = JsonSerializer.Serialize(persistent, Serialization.options)
    let roundTrippedPersistent = JsonSerializer.Deserialize<EvaluateParams>(persistentJson, Serialization.options)
    Assert.Equal(persistent, roundTrippedPersistent)
```

to:

```fsharp
[<Fact>]
let ``EvaluateParams round-trips through JSON for both Isolated and Persistent modes`` () =
    let isolated: EvaluateParams =
        { Source = "1 + 1"
          SessionId = None
          Mode = Isolated
          DocumentUri = "untitled:Script1.fsx" }

    let isolatedJson = JsonSerializer.Serialize(isolated, Serialization.options)
    let roundTrippedIsolated = JsonSerializer.Deserialize<EvaluateParams>(isolatedJson, Serialization.options)
    Assert.Equal(isolated, roundTrippedIsolated)

    let persistent: EvaluateParams =
        { Source = "x + 1"
          SessionId = Some "sess-1"
          Mode = Persistent
          DocumentUri = "file:///workspace/sales.fsx" }

    let persistentJson = JsonSerializer.Serialize(persistent, Serialization.options)
    let roundTrippedPersistent = JsonSerializer.Deserialize<EvaluateParams>(persistentJson, Serialization.options)
    Assert.Equal(persistent, roundTrippedPersistent)
```

In `src/FSharpQueryStudio.Ui/ShellViewModel.fs`, inside `RunAsync`, change:

```fsharp
                    let evalParams: EvaluateParams =
                        { Source = doc.Text
                          SessionId = (match doc.SessionMode with
                                       | Persistent -> Some doc.Uri
                                       | Isolated -> None)
                          Mode = doc.SessionMode }
```

to:

```fsharp
                    let evalParams: EvaluateParams =
                        { Source = doc.Text
                          SessionId = (match doc.SessionMode with
                                       | Persistent -> Some doc.Uri
                                       | Isolated -> None)
                          Mode = doc.SessionMode
                          DocumentUri = doc.Uri }
```

- [ ] **Step 3: Build the whole solution to verify it compiles**

Run: `dotnet build`
Expected: Build succeeds, 0 errors — every construction site above is fixed.

- [ ] **Step 4: Run the full test suite to verify nothing broke**

Run: `dotnet test`
Expected: PASS, full suite (these are all mechanical value additions; no behavior changed).

- [ ] **Step 5: Commit**

```bash
git add src/FSharpQueryStudio.Protocol/Payloads.fs src/FSharpQueryStudio.Ui/ShellViewModel.fs tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs tests/FSharpQueryStudio.Worker.Tests/HandlersLoggingTests.fs tests/FSharpQueryStudio.Integration.Tests/WorkerClientTests.fs tests/FSharpQueryStudio.Integration.Tests/WorkerClientLoggingTests.fs tests/FSharpQueryStudio.Protocol.Tests/Tests.fs
git commit -m "feat: add EvaluateParams.DocumentUri, ExportCsvParams, ResultsDiscardParams"
```

---

### Task 4: Worker — commit retention, `results/exportCsv`, `results/discard`

**Files:**
- Modify: `src/FSharpQueryStudio.Worker/Handlers.fs`
- Modify: `tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs`

**Interfaces:**
- Consumes: `ResultRetention.set`/`tryGet`/`discard` (Task 1), `EvaluateParams.DocumentUri`/`ExportCsvParams`/`ResultsDiscardParams` (Task 3), `CsvExport.write` (pre-existing).
- Produces: `Handlers.handleResultsExportCsv`, `Handlers.handleResultsDiscard`, both `WorkerRequestFrame -> WorkerResponseFrame`; `dispatch` cases `"results/exportCsv"` and `"results/discard"`. Task 5 (`App.ExportService`) is the sole real caller, over the wire.

- [ ] **Step 1: Write the failing tests**

Add a new helper and these tests to `tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs`, near the other request-builder helpers (after `evaluateRequest`/`persistentEvaluateRequest`):

```fsharp
let private evaluateRequestFor (id: string) (documentUri: string) (source: string) : WorkerRequestFrame =
    { Id = id
      Method = "execution/evaluate"
      Params =
        JsonSerializer.SerializeToElement<EvaluateParams>(
            { Source = source
              SessionId = None
              Mode = Isolated
              DocumentUri = documentUri },
            Serialization.options
        ) }

let private exportCsvRequest (id: string) (documentUri: string) (filePath: string) : WorkerRequestFrame =
    { Id = id
      Method = "results/exportCsv"
      Params =
        JsonSerializer.SerializeToElement<ExportCsvParams>(
            { DocumentUri = documentUri; FilePath = filePath },
            Serialization.options
        ) }

let private resultsDiscardRequest (id: string) (documentUri: string) : WorkerRequestFrame =
    { Id = id
      Method = "results/discard"
      Params =
        JsonSerializer.SerializeToElement<ResultsDiscardParams>(
            { DocumentUri = documentUri },
            Serialization.options
        ) }
```

Add these tests, near the other `execution/evaluate`-driven CSV/table tests:

```fsharp
[<Fact>]
let ``execution/evaluate committing a Results.table push, then results/exportCsv, writes the full retained CSV`` () =
    task {
        let tempDir = Path.Combine(Path.GetTempPath(), "fqs-workspace-" + Guid.NewGuid().ToString("N"))
        let csvPath = Path.Combine(tempDir, "test1.csv")
        let exportPath = Path.Combine(tempDir, "exported.csv")
        Handlers.setWorkspaceDirForTests (Some tempDir)

        try
            Directory.CreateDirectory tempDir |> ignore
            File.WriteAllText(csvPath, "id,name\n1,Ada\n2,Alan\n")

            let registerRequest =
                registerConnectionRequest
                    "reg1"
                    { Id = Guid.NewGuid()
                      Name = "test1"
                      Kind = Csv
                      Configuration =
                        JsonSerializer.SerializeToElement({| FilePath = csvPath |}, Serialization.options)
                      SecretReference = None
                      IsTrusted = false }

            let! _ = Handlers.dispatch registerRequest

            let script = "Connections.csv \"test1\" |> Results.table |> ignore"

            let! evalResponse = Handlers.dispatch (evaluateRequestFor "eval1" "untitled:Export1.fsx" script)
            Assert.True(evalResponse.Error.IsNone)

            let! exportResponse = Handlers.dispatch (exportCsvRequest "export1" "untitled:Export1.fsx" exportPath)
            Assert.True(exportResponse.Error.IsNone)

            let written = File.ReadAllText(exportPath)
            Assert.Equal("id,name\r\n1,Ada\r\n2,Alan\r\n", written)
        finally
            Handlers.setWorkspaceDirForTests None

            if Directory.Exists tempDir then
                Directory.Delete(tempDir, true)
    }

[<Fact>]
let ``a second execution/evaluate under the same DocumentUri overwrites the tab's retained result`` () =
    task {
        let tempDir = Path.Combine(Path.GetTempPath(), "fqs-workspace-" + Guid.NewGuid().ToString("N"))
        let exportPath = Path.Combine(tempDir, "exported.csv")
        Handlers.setWorkspaceDirForTests (Some tempDir)

        try
            Directory.CreateDirectory tempDir |> ignore

            let firstScript =
                "({ Columns = [ \"n\" ]; Rows = seq { yield [| box 1 |] } }: ConnectorQueryResult) |> Results.table |> ignore"

            let secondScript =
                "({ Columns = [ \"n\" ]; Rows = seq { yield [| box 2 |]; yield [| box 3 |] } }: ConnectorQueryResult) |> Results.table |> ignore"

            let! _ = Handlers.dispatch (evaluateRequestFor "eval1" "untitled:Overwrite1.fsx" firstScript)
            let! _ = Handlers.dispatch (evaluateRequestFor "eval2" "untitled:Overwrite1.fsx" secondScript)

            let! exportResponse = Handlers.dispatch (exportCsvRequest "export1" "untitled:Overwrite1.fsx" exportPath)
            Assert.True(exportResponse.Error.IsNone)

            let written = File.ReadAllText(exportPath)
            Assert.Equal("n\r\n2\r\n3\r\n", written)
        finally
            Handlers.setWorkspaceDirForTests None

            if Directory.Exists tempDir then
                Directory.Delete(tempDir, true)
    }

[<Fact>]
let ``an evaluate that doesn't push a table leaves the tab's existing retained result untouched`` () =
    task {
        let tempDir = Path.Combine(Path.GetTempPath(), "fqs-workspace-" + Guid.NewGuid().ToString("N"))
        let exportPath = Path.Combine(tempDir, "exported.csv")
        Handlers.setWorkspaceDirForTests (Some tempDir)

        try
            Directory.CreateDirectory tempDir |> ignore

            let tableScript =
                "({ Columns = [ \"n\" ]; Rows = seq { yield [| box 1 |] } }: ConnectorQueryResult) |> Results.table |> ignore"

            let! _ = Handlers.dispatch (evaluateRequestFor "eval1" "untitled:Untouched1.fsx" tableScript)
            let! _ = Handlers.dispatch (evaluateRequestFor "eval2" "untitled:Untouched1.fsx" "printfn \"no table here\"")

            let! exportResponse = Handlers.dispatch (exportCsvRequest "export1" "untitled:Untouched1.fsx" exportPath)
            Assert.True(exportResponse.Error.IsNone)

            let written = File.ReadAllText(exportPath)
            Assert.Equal("n\r\n1\r\n", written)
        finally
            Handlers.setWorkspaceDirForTests None

            if Directory.Exists tempDir then
                Directory.Delete(tempDir, true)
    }

[<Fact>]
let ``results/exportCsv against a DocumentUri with nothing retained returns a nothingToExport error`` () =
    task {
        let! response = Handlers.dispatch (exportCsvRequest "export1" "untitled:NeverRun.fsx" "irrelevant.csv")

        Assert.True(response.Result.IsNone)

        match response.Error with
        | Some err -> Assert.Equal("results/nothingToExport", err.Code)
        | None -> failwith "expected a nothingToExport error"
    }

[<Fact>]
let ``results/discard removes a retained result, and a repeat call is a no-op`` () =
    task {
        let tempDir = Path.Combine(Path.GetTempPath(), "fqs-workspace-" + Guid.NewGuid().ToString("N"))
        Handlers.setWorkspaceDirForTests (Some tempDir)

        try
            Directory.CreateDirectory tempDir |> ignore

            let tableScript =
                "({ Columns = [ \"n\" ]; Rows = seq { yield [| box 1 |] } }: ConnectorQueryResult) |> Results.table |> ignore"

            let! _ = Handlers.dispatch (evaluateRequestFor "eval1" "untitled:Discard1.fsx" tableScript)

            let! discardResponse = Handlers.dispatch (resultsDiscardRequest "discard1" "untitled:Discard1.fsx")
            Assert.True(discardResponse.Error.IsNone)

            let! exportResponse =
                Handlers.dispatch (exportCsvRequest "export1" "untitled:Discard1.fsx" (Path.Combine(tempDir, "x.csv")))

            match exportResponse.Error with
            | Some err -> Assert.Equal("results/nothingToExport", err.Code)
            | None -> failwith "expected a nothingToExport error after discard"

            // Repeating the discard must not throw.
            let! secondDiscardResponse = Handlers.dispatch (resultsDiscardRequest "discard2" "untitled:Discard1.fsx")
            Assert.True(secondDiscardResponse.Error.IsNone)
        finally
            Handlers.setWorkspaceDirForTests None

            if Directory.Exists tempDir then
                Directory.Delete(tempDir, true)
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests --filter FullyQualifiedName~HandlersTests`
Expected: FAIL to compile — `results/exportCsv`/`results/discard` aren't dispatched yet (`Handlers.dispatch` returns `method/unknown`), and `ExportCsvParams`/`ResultsDiscardParams` need `open FSharpQueryStudio.Protocol` (already present in this file).

- [ ] **Step 3: Implement retention-commit, the two handlers, and dispatch wiring**

In `src/FSharpQueryStudio.Worker/Handlers.fs`, inside `runEvaluate`, change:

```fsharp
                    match Results.currentEnvelope stopwatch.ElapsedMilliseconds output with
                    | Some explicitEnvelope -> explicitEnvelope
                    | None ->
```

to:

```fsharp
                    match Results.currentEnvelope stopwatch.ElapsedMilliseconds output with
                    | Some explicitEnvelope ->
                        // Retention is committed here, not on `envelope.Kind`
                        // after the fact — this keeps the generic FSI-tail-
                        // value path (below) untouched even when it also
                        // happens to produce a Table-kind envelope. See
                        // docs/superpowers/specs/2026-08-12-results-export-design.md.
                        if explicitEnvelope.Kind = ResultKind.Table then
                            ResultRetention.set
                                evalParams.DocumentUri
                                { Columns = explicitEnvelope.Schema |> List.map (fun c -> c.Name)
                                  Rows = explicitEnvelope.Rows }

                        explicitEnvelope
                    | None ->
```

Add two new handlers, after `handleResultsPage` and before `handleConnectionRegister`:

```fsharp
    /// `results/exportCsv` — writes `documentUri`'s retained full result
    /// (see `ResultRetention`) straight to `filePath` via the existing,
    /// already-tested `CsvExport.write`. No re-query, no connector: the
    /// retained columns/rows are already fully in memory by this point.
    let handleResultsExportCsv (request: WorkerRequestFrame) : WorkerResponseFrame =
        let exportParams =
            JsonSerializer.Deserialize<ExportCsvParams>(request.Params, Serialization.options)

        match ResultRetention.tryGet exportParams.DocumentUri with
        | None ->
            { Id = request.Id
              Result = None
              Error =
                Some
                    { Code = "results/nothingToExport"
                      Message = $"No retained table result for '{exportParams.DocumentUri}'." } }
        | Some retained ->
            try
                use writer = new StreamWriter(exportParams.FilePath)
                CsvExport.write writer retained.Columns retained.Rows

                { Id = request.Id
                  Result = Some(JsonSerializer.SerializeToElement({| acknowledged = true |}, Serialization.options))
                  Error = None }
            with ex ->
                { Id = request.Id
                  Result = None
                  Error = Some { Code = "results/exportFailed"; Message = ex.Message } }

    /// `results/discard` — evicts `documentUri`'s retained result, if any
    /// (Section "Eviction on tab close"). Always succeeds, even for a
    /// `documentUri` nothing is retained under, matching
    /// `Workspace.removeConnection`'s established no-op-on-unknown-key
    /// convention.
    let handleResultsDiscard (request: WorkerRequestFrame) : WorkerResponseFrame =
        let discardParams =
            JsonSerializer.Deserialize<ResultsDiscardParams>(request.Params, Serialization.options)

        ResultRetention.discard discardParams.DocumentUri

        { Id = request.Id
          Result = Some(JsonSerializer.SerializeToElement({| acknowledged = true |}, Serialization.options))
          Error = None }
```

In `dispatch`, add two new cases right after `"results/page" -> return handleResultsPage request`:

```fsharp
            | "results/page" -> return handleResultsPage request
            | "results/exportCsv" -> return handleResultsExportCsv request
            | "results/discard" -> return handleResultsDiscard request
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests`
Expected: PASS, full `Worker.Tests` suite.

- [ ] **Step 5: Commit**

```bash
git add src/FSharpQueryStudio.Worker/Handlers.fs tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs
git commit -m "feat: commit result retention in runEvaluate, add results/exportCsv and results/discard"
```

---

### Task 5: App `ExportService` + `Ui` `ShellViewModel` wiring

**Files:**
- Create: `src/FSharpQueryStudio.App/ExportService.fs`
- Modify: `src/FSharpQueryStudio.App/FSharpQueryStudio.App.fsproj` (add the new file to `<ItemGroup>`, after `ConnectionService.fs`)
- Modify: `src/FSharpQueryStudio.Ui/ShellViewModel.fs`
- Modify: `tests/FSharpQueryStudio.Ui.Tests/ShellViewModelTests.fs`

**Interfaces:**
- Consumes: `WorkerClient` (pre-existing), `ExportCsvParams`/`ResultsDiscardParams` (Task 3), `results/exportCsv`/`results/discard` (Task 4).
- Produces: `ExportService.exportCsv (client: WorkerClient) (documentUri: string) (filePath: string) : Task<Result<unit, string>>`, `ExportService.discardResults (client: WorkerClient) (documentUri: string) : Task<unit>`; `ShellViewModel.ExportCsvToFile: (string * string -> Task<Result<unit, string>>) option`, `ShellViewModel.ExportCsvAsync(filePath: string) : Task<Result<unit, string>>`, `ShellViewModel.DiscardResultsAsync: (string -> Task<unit>) option`. Task 6 (`Ui`) is the sole real caller of `ExportCsvAsync`; `CloseDocument` is the sole caller of `DiscardResultsAsync`.

- [ ] **Step 1: Write `ExportService.fs`**

No dedicated `App`-layer test project exists in this codebase (confirmed: no `FSharpQueryStudio.App.Tests` directory under `tests/`) — `ConnectionService.fs`'s own thin wrappers have no unit tests either, only the `Worker.Tests`/`Integration.Tests` coverage of the RPCs they call (Task 4). This task follows the same convention: `ExportService.fs` itself is untested directly; its behavior is covered by Task 4's Worker-layer tests plus this task's `ShellViewModel` hook tests (Step 4 below).

```fsharp
namespace FSharpQueryStudio.App

open System
open System.Text.Json
open System.Threading.Tasks
open FSharpQueryStudio.Protocol

/// The "host service" for `results/exportCsv`/`results/discard` — same
/// shape as `ConnectionService`. See
/// docs/superpowers/specs/2026-08-12-results-export-design.md.
module ExportService =

    /// Surfaces the worker's own structured error message (e.g.
    /// `results/nothingToExport`, `results/exportFailed`) as `Error`
    /// rather than swallowing it — same convention `ConnectionService.schema`
    /// already established for a comparable "real I/O can fail" case.
    let exportCsv (client: WorkerClient) (documentUri: string) (filePath: string) : Task<Result<unit, string>> =
        task {
            let request: WorkerRequestFrame =
                { Id = Guid.NewGuid().ToString()
                  Method = "results/exportCsv"
                  Params =
                    JsonSerializer.SerializeToElement<ExportCsvParams>(
                        ({ DocumentUri = documentUri; FilePath = filePath }: ExportCsvParams),
                        Serialization.options
                    ) }

            let! response = client.Send(request)

            match response.Result, response.Error with
            | Some _, _ -> return Ok()
            | None, Some workerError -> return Result.Error workerError.Message
            | None, None -> return Result.Error "The worker returned no response."
        }

    /// `results/discard` never errors (Task 4's handler always succeeds),
    /// so this is a fire-and-forget `Task<unit>`, not a `Result` — matching
    /// how `ShellViewModel.CloseDocument` calls it (see `DiscardResultsAsync`).
    let discardResults (client: WorkerClient) (documentUri: string) : Task<unit> =
        task {
            let request: WorkerRequestFrame =
                { Id = Guid.NewGuid().ToString()
                  Method = "results/discard"
                  Params =
                    JsonSerializer.SerializeToElement<ResultsDiscardParams>(
                        ({ DocumentUri = documentUri }: ResultsDiscardParams),
                        Serialization.options
                    ) }

            let! _response = client.Send(request)
            ()
        }
```

In `FSharpQueryStudio.App.fsproj`, `ExportService.fs` must compile before `App.axaml.fs` (which calls into it) — insert it right after `ConnectionService.fs`:

```xml
<ItemGroup>
  <Compile Include="WorkerClient.fs" />
  <Compile Include="ExecutionService.fs" />
  <Compile Include="AnalysisService.fs" />
  <Compile Include="ConnectionService.fs" />
  <Compile Include="ExportService.fs" />
  <Compile Include="App.axaml.fs" />
  <Compile Include="Program.fs" />
</ItemGroup>
```

- [ ] **Step 2: Write the failing `ShellViewModel` hook tests**

Add to `tests/FSharpQueryStudio.Ui.Tests/ShellViewModelTests.fs`, near the existing `CopyResultAsync` tests:

```fsharp
[<Fact>]
let ``ExportCsvAsync sends the active document's Uri and the given file path through the configured hook`` () =
    task {
        let vm = ShellViewModel()
        let mutable captured = None
        vm.ExportCsvToFile <- Some(fun (documentUri, filePath) ->
            captured <- Some(documentUri, filePath)
            Task.FromResult(Ok()))

        let! result = vm.ExportCsvAsync("C:\\export.csv")

        Assert.Equal(Ok(), result)
        Assert.Equal(Some(vm.ActiveDocument.Uri, "C:\\export.csv"), captured)
    }

[<Fact>]
let ``ExportCsvAsync returns an error result when ExportCsvToFile has not been configured`` () =
    task {
        let vm = ShellViewModel()

        let! result = vm.ExportCsvAsync("C:\\export.csv")

        match result with
        | Error _ -> ()
        | Ok() -> failwith "expected an Error result when the hook is unconfigured"
    }

[<Fact>]
let ``CloseDocument fires DiscardResultsAsync with the closed document's Uri`` () =
    let vm = ShellViewModel()
    let firstDoc = vm.ActiveDocument
    let secondDoc = vm.OpenDocument("untitled:Script2.fsx")
    let mutable discardedUri = None
    vm.DiscardResultsAsync <- Some(fun uri ->
        discardedUri <- Some uri
        Task.CompletedTask)

    vm.CloseDocument(secondDoc.Uri) |> ignore

    Assert.Equal(Some secondDoc.Uri, discardedUri)
    ignore firstDoc

[<Fact>]
let ``CloseDocument does not fire DiscardResultsAsync when the close is refused`` () =
    let vm = ShellViewModel()
    let firstDoc = vm.ActiveDocument
    firstDoc.ApplyTextChanged(1, "unsaved")
    vm.ConfirmDiscardDirtyDocument <- Some(fun _ -> false)
    let mutable discardCalled = false
    vm.DiscardResultsAsync <- Some(fun _ ->
        discardCalled <- true
        Task.CompletedTask)

    vm.CloseDocument(firstDoc.Uri) |> ignore

    Assert.False(discardCalled)

[<Fact>]
let ``CloseDocument is a no-op-safe close when DiscardResultsAsync has not been configured`` () =
    let vm = ShellViewModel()
    let firstDoc = vm.ActiveDocument
    let secondDoc = vm.OpenDocument("untitled:Script3.fsx")

    let closed = vm.CloseDocument(secondDoc.Uri) // Must not throw.

    Assert.True(closed)
    ignore firstDoc
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/FSharpQueryStudio.Ui.Tests --filter FullyQualifiedName~ShellViewModelTests`
Expected: FAIL to compile — `ExportCsvToFile`/`ExportCsvAsync`/`DiscardResultsAsync` don't exist yet.

- [ ] **Step 4: Add the hooks to `ShellViewModel`**

In `src/FSharpQueryStudio.Ui/ShellViewModel.fs`, add two new hook members right after `CopyResultToClipboard` (after line ~404):

```fsharp
    /// The composition root's hook into `results/exportCsv` — see
    /// `ExportService.exportCsv`. Takes `(documentUri, filePath)`; a no-op
    /// (`Error`) if never configured, so `ExportCsvAsync` stays callable
    /// without a worker process behind it, same rationale as every other
    /// hook here.
    member val ExportCsvToFile: ((string * string) -> Task<Result<unit, string>>) option = None with get, set

    /// The composition root's hook into `results/discard` — see
    /// `ExportService.discardResults`. Called fire-and-forget from
    /// `CloseDocument` right after a tab is actually removed, so a
    /// closed tab's retained result doesn't leak in the Worker's memory.
    /// A no-op if never configured, same rationale as every other hook.
    member val DiscardResultsAsync: (string -> Task<unit>) option = None with get, set
```

Add `ExportCsvAsync` right after `CopyResultAsync` (after line ~599):

```fsharp
    /// Exports the active document's retained full result to `filePath` —
    /// see docs/superpowers/specs/2026-08-12-results-export-design.md.
    /// Returns an `Error` (not a thrown exception) both for a real worker-
    /// side failure and for `ExportCsvToFile` never having been configured
    /// — the caller (`MainWindow`'s click handler) surfaces either the same
    /// way.
    member this.ExportCsvAsync(filePath: string) : Task<Result<unit, string>> =
        task {
            match this.ExportCsvToFile with
            | None -> return Result.Error "Export is not available."
            | Some export -> return! export (this.ActiveDocument.Uri, filePath)
        }
```

In `CloseDocument`, change:

```fsharp
            if canDiscard then
                documents.Remove(doc) |> ignore

                if documents.Count = 0 then
```

to:

```fsharp
            if canDiscard then
                documents.Remove(doc) |> ignore

                match this.DiscardResultsAsync with
                | None -> ()
                | Some discard -> discard doc.Uri |> ignore

                if documents.Count = 0 then
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/FSharpQueryStudio.Ui.Tests`
Expected: PASS, full `Ui.Tests` suite.

- [ ] **Step 6: Commit**

```bash
git add src/FSharpQueryStudio.App/ExportService.fs src/FSharpQueryStudio.App/FSharpQueryStudio.App.fsproj src/FSharpQueryStudio.Ui/ShellViewModel.fs tests/FSharpQueryStudio.Ui.Tests/ShellViewModelTests.fs
git commit -m "feat: add ExportService and ShellViewModel export/discard hooks"
```

---

### Task 6: Ui — "Export CSV…" toolbar button + composition-root wiring

No TDD — XAML/composition-root only, matching every other UI-only task in this codebase's prior sub-projects (Log Viewer, Welcome Page). Verified by a clean `dotnet build` (button/click-handler names resolve against real members at build time) plus Task 4/5's tests covering everything this wiring delegates to.

**Files:**
- Modify: `src/FSharpQueryStudio.Ui/MainWindow.axaml`
- Modify: `src/FSharpQueryStudio.Ui/MainWindow.axaml.fs`
- Modify: `src/FSharpQueryStudio.App/App.axaml.fs`

**Interfaces:**
- Consumes: `ShellViewModel.ExportCsvAsync`/`DiscardResultsAsync`/`ExportCsvToFile` (Task 5), `ExportService.exportCsv`/`discardResults` (Task 5), `ResultViewModel.IsTable` (pre-existing), `WorkerClient` (pre-existing, already in scope in `App.axaml.fs`).
- Produces: nothing consumed by a later task — this is the final wiring task.

- [ ] **Step 1: Add the toolbar button**

In `src/FSharpQueryStudio.Ui/MainWindow.axaml`, change:

```xml
        <Button Name="CopyResultButton" ToolTip.Tip="Copy Result" Margin="4,0,0,0">
          <mi:MaterialIcon Kind="ContentCopy" Width="16" Height="16" />
        </Button>
        <Button Name="ResetSessionButton" ToolTip.Tip="Reset Session" Margin="4,0,0,0">
          <mi:MaterialIcon Kind="Restore" Width="16" Height="16" />
        </Button>
```

to:

```xml
        <Button Name="CopyResultButton" ToolTip.Tip="Copy Result" Margin="4,0,0,0">
          <mi:MaterialIcon Kind="ContentCopy" Width="16" Height="16" />
        </Button>
        <Button Name="ExportCsvButton"
                ToolTip.Tip="Export CSV…"
                Margin="4,0,0,0"
                IsEnabled="{Binding Result.IsTable}">
          <mi:MaterialIcon Kind="FileExport" Width="16" Height="16" />
        </Button>
        <Button Name="ResetSessionButton" ToolTip.Tip="Reset Session" Margin="4,0,0,0">
          <mi:MaterialIcon Kind="Restore" Width="16" Height="16" />
        </Button>
```

- [ ] **Step 2: Wire the click handler in `MainWindow.axaml.fs`**

In `src/FSharpQueryStudio.Ui/MainWindow.axaml.fs`, add a `csvFilter` alongside the existing `openFsxFilter` (near line 770):

```fsharp
        let openFsxFilter = FilePickerFileType("F# Script", Patterns = [ "*.fsx" ])
        let csvFilter = FilePickerFileType("CSV", Patterns = [ "*.csv" ])
```

Add the click handler right after the `copyResultButton` wiring (near line 735), following the same Save-dialog pattern `saveActiveDocument` already uses:

```fsharp
        let copyResultButton = this.FindControl<Button>("CopyResultButton")
        copyResultButton.Click.Add(fun (_: RoutedEventArgs) -> shellViewModel.CopyResultAsync() |> ignore)

        let exportCsvButton = this.FindControl<Button>("ExportCsvButton")

        exportCsvButton.Click.Add(fun (_: RoutedEventArgs) ->
            task {
                let options =
                    FilePickerSaveOptions(FileTypeChoices = [ csvFilter ], SuggestedFileName = "Results.csv")

                let! file = this.StorageProvider.SaveFilePickerAsync(options)

                if not (isNull file) then
                    let! result = shellViewModel.ExportCsvAsync(file.Path.LocalPath)

                    match result with
                    | Ok() -> ()
                    | Result.Error message -> Log.Warning("CSV export failed: {Message}", message)
            }
            |> ignore)
```

- [ ] **Step 3: Wire the composition root in `App.axaml.fs`**

In `src/FSharpQueryStudio.App/App.axaml.fs`, add right after the existing `mainWindow.ShellViewModel.LoadConnectionSchemaAsync <- Some(fun id -> ConnectionService.schema client id)` line (line 118):

```fsharp
            mainWindow.ShellViewModel.LoadConnectionSchemaAsync <- Some(fun id -> ConnectionService.schema client id)

            mainWindow.ShellViewModel.ExportCsvToFile <-
                Some(fun (documentUri, filePath) -> ExportService.exportCsv client documentUri filePath)

            mainWindow.ShellViewModel.DiscardResultsAsync <-
                Some(fun documentUri -> ExportService.discardResults client documentUri)
```

- [ ] **Step 4: Build the whole solution to verify it compiles**

Run: `dotnet build`
Expected: Build succeeds, 0 errors — `ExportCsvButton`, `csvFilter`, and both composition-root hooks resolve against real members.

- [ ] **Step 5: Run the full test suite to verify nothing broke**

Run: `dotnet test`
Expected: PASS, full solution.

- [ ] **Step 6: Commit**

```bash
git add src/FSharpQueryStudio.Ui/MainWindow.axaml src/FSharpQueryStudio.Ui/MainWindow.axaml.fs src/FSharpQueryStudio.App/App.axaml.fs
git commit -m "feat: add Export CSV toolbar button and wire it to ExportService"
```

---

### Task 7: DEVLOG entry + final verification

**Files:**
- Modify: `docs/DEVLOG.md`

- [ ] **Step 1: Append a DEVLOG entry**

Read the tail of `docs/DEVLOG.md` first to match its established entry format (heading style, date, bullet structure) exactly as the Log Viewer and Welcome Page entries did. Summarize: per-tab full-result retention (`ResultRetention`, Worker-process memory, never persisted), `Results.table` now fully materializes instead of truncating, new `results/exportCsv`/`results/discard` RPCs, new "Export CSV…" toolbar button enabled when the active result is a table, retained results evicted on tab close (`DiscardResultsAsync`), the Results grid now shows the complete retained set (no new virtualization code needed — the DataGrid already row-virtualized from C.12), and the deliberate non-goal of leaving `results/page`/`currentPaging`/the generic FSI-tail-value path untouched.

- [ ] **Step 2: Run the full solution build and test suite**

Run: `dotnet build && dotnet test`
Expected: Build succeeds, all tests pass, 0 failures, 0 warnings of note.

- [ ] **Step 3: Commit**

```bash
git add docs/DEVLOG.md
git commit -m "docs: add DEVLOG entry for results export and full-result retention"
```
