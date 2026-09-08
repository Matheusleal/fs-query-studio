# Notebook Tail-Value Results Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a notebook cell's last expression (the FSI `it` value) the result that renders — polyglot-notebook style — and delete the `Results.raw/table/tableWithColumns/text` module entirely.

**Architecture:** The shape inference that lives in `Results.dispatchRows` moves into `Projection.projectWithCursor`, which grows a `ConnectorQueryResult` branch and a third return element — an optional fully-materialized table for Export CSV retention. `Handlers.runEvaluate` drops the `Results.currentEnvelope` pre-check and always projects the tail value. `ScriptResults.fs` and its tests are deleted; the two FSI/FSAC prologues drop their `open FSharpQueryStudio.Results` line.

**Tech Stack:** F# 8 / .NET 10, xUnit, FSharp.Compiler.Service (FSI), System.Text.Json, the VS Code notebook API (TypeScript, unchanged in this plan).

**Spec:** `docs/superpowers/specs/2026-09-08-notebook-tail-value-results-design.md`

## Global Constraints

- Target framework: `net10.0`. Build with `dotnet build FSharpQueryStudio.sln`.
- Run a single test project with `dotnet test tests/<Project>/<Project>.fsproj`.
- F# compile order is significant: a type must be declared in a file that
  compiles before any file that uses it. `FSharpQueryStudio.Results.fsproj`
  compiles `PagingCursor.fs` → `Projection.fs` → `ScriptResults.fs` →
  `CsvExport.fs` → `ResultRetention.fs`. `Projection.fs` therefore **cannot**
  reference `RetainedResult` (declared in `ResultRetention.fs`) — it declares
  its own `RetainedTable`.
- `ResultKind` is `[<RequireQualifiedAccess>]` — always write `ResultKind.Table`, etc.
- Commit after every task (the repo convention: commit after each completed
  plan task without asking). End every commit message with:
  ```
  Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8
  ```
- Update `docs/DEVLOG.md` as part of this plan (Task 6), not deferred.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `src/FSharpQueryStudio.Results/Projection.fs` | Raw FSI value → `ResultEnvelope` (+ paging cursor + retention payload). Owns ALL shape inference after this plan. | Heavy: new `RetainedTable` type, 3-tuple signature, `ConnectorQueryResult` branch, ported record/tuple/dict/nested shape dispatch, top-level try/with. |
| `src/FSharpQueryStudio.Results/ScriptResults.fs` | The `Results.*` script surface + `explicit` mutable slot. | **Deleted.** |
| `src/FSharpQueryStudio.Results/FSharpQueryStudio.Results.fsproj` | Compile manifest. | Drop `ScriptResults.fs`. |
| `src/FSharpQueryStudio.Worker/Handlers.fs` | JSON-RPC method dispatch; `runEvaluate` builds the response envelope. | Collapse the `runEvaluate` success branch; remove `Results.reset` / `Results.currentEnvelope`; wire retention off the new 3rd return value. |
| `src/FSharpQueryStudio.Execution/FsiSession.fs` | Creates the isolated FSI session + injects the prologue. | Drop `open FSharpQueryStudio.Results` from the prologue string; fix the stale `Results.table` comment. |
| `src/FSharpQueryStudio.Fsac/Prologue.fs` | The `#r`/`open` prologue FSAC prepends before type-checking. | Drop `open FSharpQueryStudio.Results`; fix the stale `Results.table` comment. |
| `tests/FSharpQueryStudio.Results.Tests/ProjectionTests.fs` | Unit tests for every shape `projectWithCursor` dispatches on. | **New** — replaces `ScriptResultsTests.fs`. |
| `tests/FSharpQueryStudio.Results.Tests/ScriptResultsTests.fs` | Tests for the deleted module. | **Deleted.** |
| `tests/FSharpQueryStudio.Results.Tests/FSharpQueryStudio.Results.Tests.fsproj` | Compile manifest. | Swap `ScriptResultsTests.fs` → `ProjectionTests.fs`. |
| `tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs` | Worker method integration tests. | Rewrite the `Results.raw`/`Results.table`-seeded eval tests to end their source in a bare expression. |
| `tests/FSharpQueryStudio.Fsac.Tests/PrologueTests.fs` | Prologue text + line-offset math. | Update the two tests that assert the `Results` `open` line / a 4-line prologue. |
| `editors/vscode/README.md`, `docs/TUTORIAL.md` | User docs. | Replace the `Results.*` tables with the tail-value model. |
| `editors/vscode/src/notebook/result.ts`, `table-payload.ts` | Envelope → notebook output items. | Comment-only touch-ups (`Results.table` mentions). No code change. |
| `docs/DEVLOG.md` | Running work log. | New dated entry. |

---

## Task 1: `Projection` — `RetainedTable` type, 3-tuple signature, `ConnectorQueryResult` branch

**Files:**
- Modify: `src/FSharpQueryStudio.Results/Projection.fs`
- Modify: `src/FSharpQueryStudio.Worker/Handlers.fs:194` (call site — keep compiling, minimal change)
- Modify: `tests/FSharpQueryStudio.Results.Tests/FSharpQueryStudio.Results.Tests.fsproj`
- Create: `tests/FSharpQueryStudio.Results.Tests/ProjectionTests.fs`

**Interfaces:**
- Produces:
  ```fsharp
  // in module Projection
  type RetainedTable = { Columns: string list; Rows: obj array array }
  val projectWithCursor : obj -> int64 -> ResultEnvelope * PagingCursor option * RetainedTable option
  val project           : obj -> int64 -> ResultEnvelope   // unchanged public shape; = (fun v ms -> let e,_,_ = projectWithCursor v ms in e)
  ```
- Consumes: `ConnectorQueryResult` from `FSharpQueryStudio.Connectors` (`{ Columns: string list; Rows: obj array seq }`), already a `ProjectReference` of this project.

- [ ] **Step 1: Add the `ProjectionTests.fs` compile entry**

In `tests/FSharpQueryStudio.Results.Tests/FSharpQueryStudio.Results.Tests.fsproj`, directly **above** the `ScriptResultsTests.fs` line, add:
```xml
    <Compile Include="ProjectionTests.fs" />
```
Leave `ScriptResultsTests.fs` in place for now (deleted in Task 5).

- [ ] **Step 2: Write the failing test — connector result becomes a retained Table**

Create `tests/FSharpQueryStudio.Results.Tests/ProjectionTests.fs`:
```fsharp
module ProjectionTests

open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors
open FSharpQueryStudio.Results

let private envOf (value: obj) =
    let env, _, _ = Projection.projectWithCursor value 0L
    env

let private retainedOf (value: obj) =
    let _, _, retained = Projection.projectWithCursor value 0L
    retained

[<Fact>]
let ``a ConnectorQueryResult tail value projects to a Table with the connector's own columns`` () =
    let query: ConnectorQueryResult =
        { Columns = [ "id"; "name" ]
          Rows = seq { yield [| box 1; box "Ada" |]; yield [| box 2; box "Grace" |] } }

    let env = envOf (box query)

    Assert.Equal(ResultKind.Table, env.Kind)
    Assert.Equal<string list>([ "id"; "name" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal(2, env.Rows.Length)
    Assert.Equal<obj array>([| box 1; box "Ada" |], env.Rows.[0])
    Assert.False(env.IsTruncated)

[<Fact>]
let ``a ConnectorQueryResult tail value is fully retained for export, even past the preview limit`` () =
    let rowCount = Projection.PreviewRowLimit + 10
    let query: ConnectorQueryResult =
        { Columns = [ "n" ]; Rows = seq { for i in 1..rowCount -> [| box i |] } }

    let env, cursor, retained = Projection.projectWithCursor (box query) 0L

    Assert.True(env.IsTruncated)
    Assert.Equal(Projection.PreviewRowLimit, env.Rows.Length)
    Assert.True(cursor.IsNone)                       // materialized, not streamed
    Assert.True(retained.IsSome)
    Assert.Equal(rowCount, retained.Value.Rows.Length)
    Assert.Equal<string list>([ "n" ], retained.Value.Columns)
```

- [ ] **Step 3: Run the tests — verify they fail to compile**

Run: `dotnet test tests/FSharpQueryStudio.Results.Tests/FSharpQueryStudio.Results.Tests.fsproj`
Expected: FAIL — `projectWithCursor` returns a 2-tuple, not a 3-tuple; `ConnectorQueryResult` currently falls through to the single-`Value`-column `IEnumerable` branch.

- [ ] **Step 4: Add `RetainedTable` and rework the signature in `Projection.fs`**

At the top of `module Projection`, under `PreviewRowLimit`:
```fsharp
    /// A fully-materialized table the worker retains for Export CSV — see
    /// docs/superpowers/specs/2026-09-08-notebook-tail-value-results-design.md.
    /// `None` for every non-materialized result (scalars, text, a streamed
    /// scalar `seq`). Shaped to match `ResultRetention.RetainedResult`,
    /// which this file compiles too early to reference directly.
    type RetainedTable = { Columns: string list; Rows: obj array array }
```

Add near the other `open`s:
```fsharp
open FSharpQueryStudio.Connectors
```

Change every existing arm of `projectWithCursor` to return a 3-tuple `(envelope, cursor, None)` instead of `(envelope, cursor)` / `(envelope, None)`. Update the return type annotation to:
```fsharp
    let projectWithCursor (value: obj) (elapsedMs: int64) : ResultEnvelope * PagingCursor option * RetainedTable option =
```
Update `project`:
```fsharp
    let project (value: obj) (elapsedMs: int64) : ResultEnvelope =
        let env, _, _ = projectWithCursor value elapsedMs
        env
```

Add a `ConnectorQueryResult` arm **before** the `:? IEnumerable` arm (a `ConnectorQueryResult` is a record, so it would otherwise be caught by the `FSharpType.IsRecord` arm — put it first):
```fsharp
        | :? ConnectorQueryResult as query ->
            let columns =
                query.Columns |> List.map (fun name -> { Name = name; DisplayType = "obj" })
            let allRows = query.Rows |> Array.ofSeq
            let preview = allRows |> Array.truncate PreviewRowLimit
            let envelope =
                { emptyEnvelope ResultKind.Table elapsedMs with
                    Schema = columns
                    Rows = preview
                    IsTruncated = allRows.Length > PreviewRowLimit }
            envelope, None, Some { Columns = query.Columns; Rows = allRows }
```

- [ ] **Step 5: Update the `Handlers.fs` call site so the solution compiles**

In `src/FSharpQueryStudio.Worker/Handlers.fs`, the `| None ->` fallback arm currently reads `let projected, cursor = Projection.projectWithCursor value stopwatch.ElapsedMilliseconds`. Change to:
```fsharp
                        let projected, cursor, _retained = Projection.projectWithCursor value stopwatch.ElapsedMilliseconds
```
(Full rewrite of this arm is Task 3 — for now the underscore keeps it compiling with behavior unchanged.)

- [ ] **Step 6: Run the tests — verify pass**

Run: `dotnet test tests/FSharpQueryStudio.Results.Tests/FSharpQueryStudio.Results.Tests.fsproj`
Expected: PASS (the two new tests, plus every existing `ScriptResultsTests` case still green).

- [ ] **Step 7: Build the whole solution**

Run: `dotnet build FSharpQueryStudio.sln`
Expected: succeeds. If `Handlers.fs` warns about the now-unused `_retained`, that is fine — it goes away in Task 3.

- [ ] **Step 8: Commit**

```bash
git add src/FSharpQueryStudio.Results/Projection.fs src/FSharpQueryStudio.Worker/Handlers.fs \
        tests/FSharpQueryStudio.Results.Tests/ProjectionTests.fs \
        tests/FSharpQueryStudio.Results.Tests/FSharpQueryStudio.Results.Tests.fsproj
git commit -m "feat(results): project ConnectorQueryResult tail values, add retention payload

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8"
```

---

## Task 2: `Projection` — port record / tuple / dict / nested shape dispatch

**Files:**
- Modify: `src/FSharpQueryStudio.Results/Projection.fs`
- Modify: `tests/FSharpQueryStudio.Results.Tests/ProjectionTests.fs`

**Interfaces:**
- Consumes: `Projection.projectWithCursor` (3-tuple, from Task 1), `Projection.RetainedTable`.
- Produces: no signature change — only the set of shapes `projectWithCursor` recognizes grows.

**Behavior to implement** (ported from `ScriptResults.fs`'s `dispatchRows`, with every `columnNames` parameter and `resolveHeaders` override removed):

| Tail value (a non-string `IEnumerable`, dispatch on element 0) | Columns | Rows | Cursor | Retained |
|---|---|---|---|---|
| element 0 is an F# **record** | reflected field names, declaration order | one `obj[]` per record via `FSharpValue.GetRecordFields` | none | `Some` (full) |
| element 0 is a **tuple** | `Item1 .. ItemN` (N = `FSharpValue.GetTupleFields(x).Length`) | `FSharpValue.GetTupleFields` per row | none | `Some` (full) |
| element 0 is a **`Map` / `IDictionary` / generic `IDictionary<_,_>`** | union of keys across all rows, first-seen order | one cell per key column; missing key → `null` | none | `Some` (full) |
| element 0 is a non-string **`IEnumerable`** (nested collection) | `string`-projected values of **row 0** | rows 1.. , each padded with `null` / truncated to the header width | none | `Some` (full) |
| element 0 is a **string** or a **primitive** (anything else) | single `"Value"` column | one cell per element | `PagingCursor` when `> PreviewRowLimit` | **`None`** |
| the sequence is **empty** | `[]` | `[||]` | none | `Some { Columns = []; Rows = [||] }` |
| element 0 is a **`ConnectorQueryResult`** | — | — | — | throw `"a sequence of ConnectorQueryResult is not supported; enumerate .Rows"` (caught by Step 8's wrapper → Text) |

Non-`IEnumerable` values keep their current arms unchanged: `null` → `NoValue`; `string` → `Text`; primitive → `Scalar`; single F# record → one-row `Table` **now with `Some` retained**; anything else → `Text` via `sprintf "%A"`.

- [ ] **Step 1: Move the dictionary reflection helpers verbatim**

Cut these four `private` helpers from `ScriptResults.fs` and paste them into `Projection.fs` (above `projectWithCursor`), unchanged: `isDictionary`, `getDictionaryKeys`, `getDictionaryValue`, and the `resolveHeaders`-free parts you need. (Leave `resolveHeaders` behind — it dies with `ScriptResults.fs` in Task 5.) These compile as-is; they only use `System.Collections`, `System.Collections.Generic`, `System.Reflection`, `Microsoft.FSharp.Reflection`, all already `open`ed or referenced in `Projection.fs` (add `open System.Collections.Generic` if missing).

- [ ] **Step 2: Write the failing tests — port every shape case**

Append to `ProjectionTests.fs`. Mechanical port rule from the old `ScriptResultsTests.fs`: replace `Results.reset (); Results.table (box X) |> ignore; let envelope = (Results.currentEnvelope 0L []).Value` with `let envelope = envOf (box X)`. Drop every `tableWithColumns`, every `Assert.Throws` "wrap it" / "columnNames" / "Results.raw" case, and every `Assert.Same` pass-through case (there is no pass-through any more). Keep these, one `[<Fact>]` each:

```fsharp
type private Person = { Id: int; Name: string }

[<Fact>]
let ``a record sequence infers columns from reflected field names in declaration order`` () =
    let people = [ { Id = 1; Name = "Ada" }; { Id = 2; Name = "Grace" } ]
    let env = envOf (box people)
    Assert.Equal(ResultKind.Table, env.Kind)
    Assert.Equal<string list>([ "Id"; "Name" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box 1; box "Ada" |], env.Rows.[0])
    Assert.Equal<obj array>([| box 2; box "Grace" |], env.Rows.[1])
    Assert.True((retainedOf (box people)).IsSome)

[<Fact>]
let ``a tuple sequence maps each element to a positional Item column`` () =
    let pairs = [ (1, "Ada"); (2, "Grace") ]
    let env = envOf (box pairs)
    Assert.Equal<string list>([ "Item1"; "Item2" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box 1; box "Ada" |], env.Rows.[0])
    Assert.Equal<obj array>([| box 2; box "Grace" |], env.Rows.[1])
    Assert.True((retainedOf (box pairs)).IsSome)

[<Fact>]
let ``a Map sequence infers the union of keys in first-seen order`` () =
    let rows =
        [ Map.ofList [ ("id", box 1); ("name", box "Ada") ]
          Map.ofList [ ("id", box 2); ("extra", box "x") ] ]
    let env = envOf (box rows)
    Assert.Equal<string list>([ "id"; "name"; "extra" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box 1; box "Ada"; null |], env.Rows.[0])
    Assert.Equal<obj array>([| box 2; null; box "x" |], env.Rows.[1])

[<Fact>]
let ``a Map of string,int sequence keeps sorted keys but first-seen column order`` () =
    let rows =
        [ Map.ofList [ ("id", 1); ("count", 5) ]
          Map.ofList [ ("id", 2); ("extra", 10) ] ]
    let env = envOf (box rows)
    Assert.Equal<string list>([ "count"; "id"; "extra" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box 5; box 1; null |], env.Rows.[0])
    Assert.Equal<obj array>([| null; box 2; box 10 |], env.Rows.[1])

[<Fact>]
let ``a Dictionary<string,obj> sequence infers union-of-keys columns`` () =
    let d1 = Dictionary<string, obj>()
    d1.Add("id", box 1); d1.Add("name", box "Alice")
    let d2 = Dictionary<string, obj>()
    d2.Add("id", box 2); d2.Add("age", box 30)
    let env = envOf (box [ d1; d2 ])
    Assert.Equal<string list>([ "id"; "name"; "age" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box 1; box "Alice"; null |], env.Rows.[0])
    Assert.Equal<obj array>([| box 2; null; box 30 |], env.Rows.[1])

[<Fact>]
let ``a nested collection consumes row 0 as headers`` () =
    let grid = [ [| "id"; "name" |]; [| "1"; "Ada" |]; [| "2"; "Grace" |] ]
    let env = envOf (box grid)
    Assert.Equal<string list>([ "id"; "name" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal(2, env.Rows.Length)
    Assert.Equal<obj array>([| box "1"; box "Ada" |], env.Rows.[0])

[<Fact>]
let ``ragged nested rows pad short rows with null and truncate long rows`` () =
    let grid: obj array list =
        [ [| box "a"; box "b"; box "c" |]; [| box 1 |]; [| box 2; box 3; box 4; box 5 |] ]
    let env = envOf (box grid)
    Assert.Equal<string list>([ "a"; "b"; "c" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box 1; null; null |], env.Rows.[0])
    Assert.Equal<obj array>([| box 2; box 3; box 4 |], env.Rows.[1])

[<Fact>]
let ``a sequence of strings renders as one Value column, not exploded into characters`` () =
    let env = envOf (box [ "Ada"; "Grace" ])
    Assert.Equal<string list>([ "Value" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal(2, env.Rows.Length)
    Assert.Equal<obj array>([| box "Ada" |], env.Rows.[0])

[<Fact>]
let ``a scalar sequence within the preview limit is one Value column, no cursor, not retained`` () =
    let env, cursor, retained = Projection.projectWithCursor (box [ 1; 2; 3 ]) 0L
    Assert.Equal<string list>([ "Value" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal(3, env.Rows.Length)
    Assert.Equal<obj array>([| box 1 |], env.Rows.[0])
    Assert.True(cursor.IsNone)
    Assert.True(retained.IsNone)

[<Fact>]
let ``a scalar sequence past the preview limit keeps a streaming cursor and is not retained`` () =
    let env, cursor, retained = Projection.projectWithCursor (box [ 1 .. Projection.PreviewRowLimit + 10 ]) 0L
    Assert.True(env.IsTruncated)
    Assert.Equal(Projection.PreviewRowLimit, env.Rows.Length)
    Assert.True(cursor.IsSome)
    Assert.True(retained.IsNone)

[<Fact>]
let ``an empty sequence is an empty Table`` () =
    let env, _, retained = Projection.projectWithCursor (box (List.empty<Person>)) 0L
    Assert.Equal(ResultKind.Table, env.Kind)
    Assert.Empty(env.Schema)
    Assert.Empty(env.Rows)
    Assert.True(retained.IsSome)

[<Fact>]
let ``a single record is a one-row Table and is retained`` () =
    let env, _, retained = Projection.projectWithCursor (box { Id = 1; Name = "Ada" }) 0L
    Assert.Equal(ResultKind.Table, env.Kind)
    Assert.Equal<string list>([ "Id"; "Name" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box 1; box "Ada" |], env.Rows.[0])
    Assert.True(retained.IsSome)

[<Fact>]
let ``a bare int is a Scalar, not a Table`` () =
    let env = envOf (box 42)
    Assert.Equal(ResultKind.Scalar, env.Kind)
    Assert.Equal(Some "42", env.DisplayText)

[<Fact>]
let ``a bare string is Text`` () =
    let env = envOf (box "hello")
    Assert.Equal(ResultKind.Text, env.Kind)
    Assert.Equal(Some "hello", env.DisplayText)

[<Fact>]
let ``null is NoValue`` () =
    let env = envOf null
    Assert.Equal(ResultKind.NoValue, env.Kind)

[<Fact>]
let ``a mixed-shape sequence falls back to a Text envelope instead of throwing`` () =
    let mixed: obj list = [ box (1, "Ada"); box { Id = 2; Name = "Grace" } ]
    let env, cursor, retained = Projection.projectWithCursor (box mixed) 0L
    Assert.Equal(ResultKind.Text, env.Kind)
    Assert.True(cursor.IsNone)
    Assert.True(retained.IsNone)
```

- [ ] **Step 3: Run the tests — verify they fail**

Run: `dotnet test tests/FSharpQueryStudio.Results.Tests/FSharpQueryStudio.Results.Tests.fsproj`
Expected: FAIL — tuple/dict/nested tail values currently hit the single-`Value`-column arm; the mixed-shape test currently throws or mis-projects.

- [ ] **Step 4: Implement the shape dispatch**

Replace the current `| :? IEnumerable as items ->` arm of `projectWithCursor` with a shape-dispatching block. Structure:

```fsharp
        | :? IEnumerable as items when not (value :? string) ->
            let rows = items |> Seq.cast<obj> |> Array.ofSeq
            match rows with
            | [||] ->
                emptyEnvelope ResultKind.Table elapsedMs, None, Some { Columns = []; Rows = [||] }
            | _ ->
                let first = rows.[0]
                let allFlags = BindingFlags.Public ||| BindingFlags.NonPublic
                match first with
                | :? ConnectorQueryResult ->
                    failwith "a sequence of ConnectorQueryResult is not supported; enumerate .Rows"
                | _ when first <> null && FSharpType.IsRecord(first.GetType(), allFlags) ->
                    // reflected field names + FSharpValue.GetRecordFields per row  → materializedTable cols rows
                | _ when first <> null && FSharpType.IsTuple(first.GetType()) ->
                    let width = (FSharpValue.GetTupleFields first).Length
                    let names = [ for i in 1..width -> sprintf "Item%d" i ]
                    // FSharpValue.GetTupleFields per row → materializedTable names rows
                | _ when first <> null && isDictionary (first.GetType()) ->
                    // union-of-keys (first-seen) + getDictionaryValue per row → materializedTable keys rows
                | :? IEnumerable when not (first :? string) ->
                    // row 0 → headers (string-projected); rows 1.. padded/truncated → materializedTable headers dataRows
                | _ ->
                    // scalar / string sequence → the EXISTING streaming path:
                    let cursor = PagingCursor(items.GetEnumerator())
                    let page, hasMore = cursor.TakePage(PreviewRowLimit)
                    let envelope =
                        { emptyEnvelope ResultKind.Table elapsedMs with
                            Schema = [ { Name = "Value"; DisplayType = "obj" } ]
                            Rows = page |> Array.map (fun r -> [| r |])
                            IsTruncated = hasMore }
                    envelope, (if hasMore then Some cursor else None), None
```

Add one shared local helper for the four materialized branches:
```fsharp
        let materializedTable (columnNames: string list) (dataRows: obj array array) : ResultEnvelope * PagingCursor option * RetainedTable option =
            let schema = columnNames |> List.map (fun n -> { Name = n; DisplayType = "obj" })
            let preview = dataRows |> Array.truncate PreviewRowLimit
            let envelope =
                { emptyEnvelope ResultKind.Table elapsedMs with
                    Schema = schema
                    Rows = preview
                    IsTruncated = dataRows.Length > PreviewRowLimit }
            envelope, None, Some { Columns = columnNames; Rows = dataRows }
```

Port the record / dict / nested row-building bodies verbatim from `ScriptResults.fs`'s `dispatchRows` (the `columnNames = None` path of each), feeding the result into `materializedTable`. For the nested branch, the header row is `rawRows.[0] |> Array.map string |> Array.toList` and data is `rawRows.[1..]` padded/truncated to the header width with `null` — exactly the current `dispatchRows` `None` path.

The non-`string`, non-`IEnumerable` single-record arm keeps its current body but now returns `Some` retained:
```fsharp
        | v when FSharpType.IsRecord(v.GetType(), recordBindingFlags) ->
            let t = v.GetType()
            let cols = recordSchema t |> List.map (fun c -> c.Name)
            materializedTable cols [| recordRow t v |]
```
(Reuse the existing `recordSchema` / `recordRow` helpers; `recordSchema` returns `Column list`, so map to names.)

- [ ] **Step 5: Wrap the whole dispatch body in a fallback `try/with`**

The shape dispatch runs in the worker after FSI has returned — an exception here has no `EvalInteractionNonThrowing` to catch it and would hang the request. Wrap the entire `match value with … ` body of `projectWithCursor`:
```fsharp
    let projectWithCursor (value: obj) (elapsedMs: int64) : ResultEnvelope * PagingCursor option * RetainedTable option =
        try
            match value with
            | ... // all the arms above
        with _ ->
            { emptyEnvelope ResultKind.Text elapsedMs with DisplayText = Some(sprintf "%A" value) }, None, None
```

- [ ] **Step 6: Run the tests — verify pass**

Run: `dotnet test tests/FSharpQueryStudio.Results.Tests/FSharpQueryStudio.Results.Tests.fsproj`
Expected: PASS — all new `ProjectionTests` cases and the two from Task 1. `ScriptResultsTests.fs` also still passes (untouched module).

- [ ] **Step 7: Build the solution**

Run: `dotnet build FSharpQueryStudio.sln`
Expected: succeeds.

- [ ] **Step 8: Commit**

```bash
git add src/FSharpQueryStudio.Results/Projection.fs tests/FSharpQueryStudio.Results.Tests/ProjectionTests.fs
git commit -m "feat(results): port record/tuple/dict/nested shape inference into Projection

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8"
```

---

## Task 3: Worker — collapse `runEvaluate`, drop `Results.reset` / `Results.currentEnvelope`

**Files:**
- Modify: `src/FSharpQueryStudio.Worker/Handlers.fs:129-236` (the `runEvaluate` function)
- Modify: `tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs`

**Interfaces:**
- Consumes: `Projection.projectWithCursor : obj -> int64 -> ResultEnvelope * PagingCursor option * Projection.RetainedTable option` (Tasks 1–2), `ResultRetention.set : string -> RetainedResult -> unit`, `RetainedResult = { Columns: string list; Rows: obj array array }`.
- Produces: no new API. `runEvaluate` still returns `WorkerResponseFrame`; the envelope wire shape is unchanged.

- [ ] **Step 1: Rewrite the `HandlersTests` cases that seed results via `Results.*`**

Apply these edits to `tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs`:

1. **`handleEvaluate captures printed stdout into the envelope's Output lines`** — unchanged.

2. **Delete** `execution/evaluate shows Results.table's push even when the script's own last statement is a printfn` — the scenario it tests (an override surviving a trailing `printfn`) is deliberately gone.

3. **Add** in its place:
```fsharp
[<Fact>]
let ``execution/evaluate renders a record-list tail expression as a Table with real columns`` () =
    let script = "[ {| Id = 1; Name = \"Ada\" |}; {| Id = 2; Name = \"Grace\" |} ]"
    let response = Handlers.handleEvaluate (evaluateRequest "rec1" script)

    Assert.True(response.Error.IsNone)
    let env = JsonSerializer.Deserialize<ResultEnvelope>(response.Result.Value, Serialization.options)
    Assert.Equal(ResultKind.Table, env.Kind)
    Assert.Equal<string list>([ "Id"; "Name" ], env.Schema |> List.map (fun c -> c.Name))
    Assert.Equal(2, env.Rows.Length)

[<Fact>]
let ``execution/evaluate on a cell that ends in printfn is NoValue plus captured stdout`` () =
    let response = Handlers.handleEvaluate (evaluateRequest "nv1" "printfn \"just a side effect\"")

    Assert.True(response.Error.IsNone)
    let env = JsonSerializer.Deserialize<ResultEnvelope>(response.Result.Value, Serialization.options)
    Assert.Equal(ResultKind.NoValue, env.Kind)
    Assert.Contains(env.Output, (fun (l: OutputLine) -> l.Text.Contains("just a side effect")))
```

4. **`execution/evaluate committing a Results.table push, then results/exportCsv, writes the full retained CSV`** — rename to `... a connector-query tail expression, then results/exportCsv ...`; change the script from `"Connections.csv \"test1\" |> Results.raw |> ignore"` to `"Connections.csv \"test1\""`. Assertions unchanged.

5. **`a second execution/evaluate under the same DocumentUri overwrites the tab's retained result`** — change both scripts from
   `"({ Columns = [ \"n\" ]; Rows = seq { yield [| box 1 |] } }: ConnectorQueryResult) |> Results.raw |> ignore"`
   to
   `"({ Columns = [ \"n\" ]; Rows = seq { yield [| box 1 |] } }: ConnectorQueryResult)"` (drop `|> Results.raw |> ignore` from each). Assertions unchanged.

6. **`an evaluate that doesn't push a table leaves the tab's existing retained result untouched`** — change the first script to a bare record list `"[ {| n = 1 |} ]"` (materialized → retained, header `n`); keep the second run as `"printfn \"no table here\""`. The exported CSV assertion becomes `Assert.Equal("n\r\n1\r\n", written)`.

7. **`results/discard removes a retained result, and a repeat call is a no-op`** — change `tableScript` from the `... |> Results.raw |> ignore` form to the bare `"({ Columns = [ \"n\" ]; Rows = seq { yield [| box 1 |] } }: ConnectorQueryResult)"`.

8. **Delete** `a later generic-projection Table run under the same DocumentUri discards the tab's earlier retained result` — its premise (a generic Table must *discard*) is reversed by this change: a materialized generic table now legitimately *becomes* the retained result. Coverage of "a non-table run leaves retention alone" is kept by case 6.

9. **`execution/evaluate resolves Connections.sql against a connection registered via connection/register`** — unchanged (tail value is a scalar).

10. Paging tests (`[1 .. 510]`), `NoValue` test, persistent-session test — unchanged.

- [ ] **Step 2: Run the worker tests — verify the rewritten/added cases fail**

Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests/FSharpQueryStudio.Worker.Tests.fsproj`
Expected: FAIL — the bare-expression scripts don't retain anything yet (the `| None ->` arm still discards on `Kind = Table` and never calls `ResultRetention.set` off the projector), and the new record-list test may still pass by luck — the export-related ones will not.

- [ ] **Step 3: Rewrite the `runEvaluate` success path**

In `src/FSharpQueryStudio.Worker/Handlers.fs`:

- Delete the `Results.reset ()` line (currently `Handlers.fs:150`).
- Replace the whole `let envelope = match outcome.Exception with … ` expression (currently `Handlers.fs:157-223`) with:

```fsharp
            let envelope =
                match outcome.Exception with
                | Some ex -> { Projection.projectError ex stopwatch.ElapsedMilliseconds with Output = output }
                | None ->
                    let value =
                        outcome.Value |> Option.map (fun v -> v.ReflectionValue) |> Option.toObj

                    let projected, cursor, retained =
                        Projection.projectWithCursor value stopwatch.ElapsedMilliseconds

                    // Export CSV targets the notebook's most recent MATERIALIZED
                    // grid. A run that produced no such table (a scalar, a
                    // streamed scalar seq, a printfn) leaves any earlier
                    // retained result in place — it does not clear it.
                    if not (System.String.IsNullOrEmpty evalParams.DocumentUri) then
                        match retained with
                        | Some t ->
                            ResultRetention.set
                                evalParams.DocumentUri
                                { Columns = t.Columns; Rows = t.Rows }
                        | None -> ()

                    let continuationToken =
                        cursor
                        |> Option.map (fun c ->
                            let token = Guid.NewGuid().ToString("N")
                            currentPaging <- Some(token, c)
                            token)

                    { projected with
                        Output = output
                        ContinuationToken = continuationToken }
```

- Remove the now-unused `open FSharpQueryStudio.Results` from `Handlers.fs` **only if** nothing else in the file references the `Results` module (it still needs `ResultRetention`, `CsvExport`, `Projection`, `PagingCursor` — all in the same namespace — so keep the `open`).

- [ ] **Step 4: Run the worker tests — verify pass**

Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests/FSharpQueryStudio.Worker.Tests.fsproj`
Expected: PASS.

- [ ] **Step 5: Build the solution**

Run: `dotnet build FSharpQueryStudio.sln`
Expected: succeeds. `ScriptResults.fs` still compiles but is now unreferenced by `Handlers.fs`.

- [ ] **Step 6: Commit**

```bash
git add src/FSharpQueryStudio.Worker/Handlers.fs tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs
git commit -m "feat(worker): project the FSI tail value directly, drop the Results side-channel

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8"
```

---

## Task 4: Delete `ScriptResults.fs` + drop `open FSharpQueryStudio.Results` from both prologues

**Files:**
- Delete: `src/FSharpQueryStudio.Results/ScriptResults.fs`
- Delete: `tests/FSharpQueryStudio.Results.Tests/ScriptResultsTests.fs`
- Modify: `src/FSharpQueryStudio.Results/FSharpQueryStudio.Results.fsproj`
- Modify: `tests/FSharpQueryStudio.Results.Tests/FSharpQueryStudio.Results.Tests.fsproj`
- Modify: `src/FSharpQueryStudio.Execution/FsiSession.fs`
- Modify: `src/FSharpQueryStudio.Fsac/Prologue.fs`
- Modify: `tests/FSharpQueryStudio.Fsac.Tests/PrologueTests.fs`

**Interfaces:**
- Consumes: nothing new.
- Produces: the `FSharpQueryStudio.Results` *namespace* still exists (via `PagingCursor.fs`, `Projection.fs`, `CsvExport.fs`, `ResultRetention.fs`) — only the `Results` *module* is gone. `open FSharpQueryStudio.Results` remains valid everywhere it appears; the two prologue strings simply no longer need it.

- [ ] **Step 1: Update `PrologueTests.fs` first (red)**

In `tests/FSharpQueryStudio.Fsac.Tests/PrologueTests.fs`:

- `build emits real #r and open lines for Connectors and Results` → rename to `build emits real #r lines for Connectors and Results and opens Connectors` and change the body to:
  ```fsharp
      let text = Prologue.build ()
      Assert.Contains("#r \"", text)
      Assert.Contains("open FSharpQueryStudio.Connectors", text)
      Assert.DoesNotContain("open FSharpQueryStudio.Results", text)
      Assert.Equal(2, (text.Split('\n') |> Array.filter (fun l -> l.StartsWith("#r ")) |> Array.length))
  ```
- `shiftUp and shiftDown use the real prologue's actual line count` → update the comment to "build() is 3 real lines: two #r lines, one open line" and change the assertions to:
  ```fsharp
      Assert.Equal(4, Prologue.shiftUp 1)
      Assert.Equal(1, Prologue.shiftDown 4)
  ```
- The other three tests (`lineCount matches…`, `sourceForCheck prepends…`, `shiftUp then shiftDown is the identity`) are self-recomputing — leave them.

- [ ] **Step 2: Run the Fsac tests — verify the two updated cases fail**

Run: `dotnet test tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj`
Expected: FAIL on the two edited tests (`build()` still emits the `Results` `open`; `shiftUp 1` still = 5).

- [ ] **Step 3: Drop the `open` from `Prologue.fs`**

In `src/FSharpQueryStudio.Fsac/Prologue.fs`, `build ()`:
```fsharp
        $"#r \"{escapeForStringLiteral connectorsPath}\"\n#r \"{escapeForStringLiteral resultsPath}\"\nopen FSharpQueryStudio.Connectors\n"
```
(Remove the trailing `open FSharpQueryStudio.Results\n`; keep both `#r` lines and the `resultsPath` binding — the assembly stays referenced.) Fix the module-level doc comment: `so `Connections.sql`/`Results.table` resolve` → `so `Connections.sql` resolves`.

- [ ] **Step 4: Drop the `open` from `FsiSession.fs`**

In `src/FSharpQueryStudio.Execution/FsiSession.fs`, `create`:
```fsharp
        let prologue =
            $"#r @\"{connectorsAssemblyPath}\"\n#r @\"{resultsAssemblyPath}\"\nopen FSharpQueryStudio.Connectors"
```
(Remove `\nopen FSharpQueryStudio.Results`; keep both `#r` lines.) Fix the comment block above it: `the `Results.table`/ `Results.text` results-pane override (Section 11)` → drop that clause; leave the `Connections.*` half.

- [ ] **Step 5: Delete `ScriptResults.fs` and its test, update both `.fsproj`s**

```bash
git rm src/FSharpQueryStudio.Results/ScriptResults.fs tests/FSharpQueryStudio.Results.Tests/ScriptResultsTests.fs
```
- `src/FSharpQueryStudio.Results/FSharpQueryStudio.Results.fsproj` — remove `<Compile Include="ScriptResults.fs" />`.
- `tests/FSharpQueryStudio.Results.Tests/FSharpQueryStudio.Results.Tests.fsproj` — remove `<Compile Include="ScriptResultsTests.fs" />` (the `ProjectionTests.fs` line added in Task 1 stays).

- [ ] **Step 6: Build the solution**

Run: `dotnet build FSharpQueryStudio.sln`
Expected: succeeds. If the compiler flags a leftover reference to the `Results` module anywhere, that file was missed in Tasks 1–3 — fix it before continuing.

- [ ] **Step 7: Run the full test suite**

Run: `dotnet test FSharpQueryStudio.sln`
Expected: all green. Pay attention to `FSharpQueryStudio.Execution.Tests`, `FSharpQueryStudio.Fsac.Tests`, `FSharpQueryStudio.Worker.Tests`, `FSharpQueryStudio.Results.Tests`.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "refactor: delete the Results module; prologues no longer open it

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8"
```

---

## Task 5: TypeScript notebook layer — verify no behavior change, tidy comments

**Files:**
- Modify: `editors/vscode/src/notebook/result.ts` (comment only)
- Modify: `editors/vscode/src/notebook/table-payload.ts` (comment only)

**Interfaces:**
- Consumes: `ResultEnvelope` over the wire — shape unchanged by this plan (`Kind`, `Schema`, `Rows`, `IsTruncated`, `ContinuationToken`, `DisplayText`, `Output`). No code change needed.

- [ ] **Step 1: Run the extension test suite as the baseline**

Run: `cd editors/vscode && npm test`
Expected: PASS (nothing changed yet).

- [ ] **Step 2: Update the two stale comments**

- `editors/vscode/src/notebook/table-payload.ts` — the doc comment says
  "Works for both worker paths unchanged: `Results.table` (real columns, full
  rows, `continuationToken = null`) and an FSI tail-value `seq` …". Replace with:
  ```
   * Works for every tail-value shape the worker projects: a materialized
   * table (real columns, full rows retained for Export CSV, no continuation
   * token) and a streamed scalar `seq` (single `Value` column, preview rows,
   * a token when truncated). Rows are passed through positionally.
  ```
- `editors/vscode/src/notebook/result.ts:47` — the inline comment
  "a huge `rows` (Results.table pushes the full set with `isTruncated: false`)"
  → "a huge `rows` (a materialized table sends the full preview set)".

- [ ] **Step 3: Re-run the extension tests**

Run: `cd editors/vscode && npm test`
Expected: PASS, unchanged count.

- [ ] **Step 4: Commit**

```bash
git add editors/vscode/src/notebook/result.ts editors/vscode/src/notebook/table-payload.ts
git commit -m "docs(vscode): update notebook-output comments for the tail-value model

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8"
```

---

## Task 6: User docs + DEVLOG

**Files:**
- Modify: `editors/vscode/README.md`
- Modify: `docs/TUTORIAL.md`
- Modify: `docs/DEVLOG.md`

**Interfaces:** none — prose only.

- [ ] **Step 1: `editors/vscode/README.md`**

Find the cell-execution section containing the four-row table
`| Results.raw query | … |`, `| Results.table coll | … |`,
`| Results.tableWithColumns coll names | … |`, `| Results.text value | … |`
(around line 71). Replace the table and its lead-in with:

```markdown
### What a cell shows

A cell renders the value of its **last expression** — the same value F#
Interactive would echo. You don't call anything special.

| Last expression | Renders as |
|---|---|
| A connector query (`Connections.sql …`, `Connections.csv …`, …) | A grid, with a working **Export CSV** |
| A sequence of records | A grid; columns are the record fields |
| A sequence of tuples | A grid; columns are `Item1`, `Item2`, … |
| A sequence of `Map`s / dictionaries | A grid; columns are the union of keys |
| A sequence of scalars | A one-column grid, streamed with **Load more** |
| A single record | A one-row grid |
| A scalar (`int`, `string`, `DateTime`, …) | Plain text |
| A `let` binding or a `printfn` (no value) | Just the printed output |

To force plain structured text, end the cell with `sprintf "%A" value`.
```

Also remove the phrase "auto-`open` of `Connections` / `Results`" wherever it
appears in that file — it is now just `Connections`.

- [ ] **Step 2: `docs/TUTORIAL.md`**

- The row table around line 203 (`| Results.raw q | … |` … `| Results.text value | … |`)
  → replace with the same "What a cell shows" table as Step 1.
- The example around line 213:
  ```fsharp
  Results.tableWithColumns [ "alice", 3; "bob", 7; "carol", 1 ] [ "user"; "score" ]
  ```
  → replace with:
  ```fsharp
  // a computed collection — a tuple list renders with Item1 / Item2 headers
  [ "alice", 3; "bob", 7; "carol", 1 ]
  ```
  and adjust the surrounding sentence (it currently explains that tuples need
  explicit headers) to: "A tuple list renders with positional `Item1` /
  `Item2` headers — project it to a record if you want named columns."
- Search the rest of the file for `Results.raw` / `Results.table` / `Results.text`
  and rewrite each mention to the tail-value equivalent (e.g. the Export CSV
  step: "run a cell whose last line is `Connections.sql …`, then **Export CSV**").
- Fix any "the `Results.*` variants" step in the step list — drop it or fold it
  into the "What a cell shows" explanation.

- [ ] **Step 3: `docs/DEVLOG.md` — new entry at end of file**

```markdown
## 2026-09-08 — Notebook results: capture the FSI tail value, retire the Results module

A cell now renders the value of its **last expression**, polyglot-notebook
style — no `Results.raw` / `table` / `tableWithColumns` / `text` call, and the
`Results` module is deleted (`src/FSharpQueryStudio.Results/ScriptResults.fs`,
~380 lines, plus `ScriptResultsTests.fs`).

**Where the logic went.** `Results.dispatchRows`' shape inference — record
seq → reflected field names, tuple seq → `Item1..ItemN`, `Map`/dictionary seq
→ first-seen key union, nested seq → row-0 headers with ragged pad/truncate —
moved verbatim into `Projection.projectWithCursor`, minus every `columnNames`
override (there is no override surface any more). `projectWithCursor` gained a
`ConnectorQueryResult` arm (ported from `Results.raw`) and a third return
value, `RetainedTable option`, so the worker still knows which results to hold
for **Export CSV**.

**Tuple headers.** `Results.tableWithColumns` used to be *required* for tuple
sequences (tuples carry no field names). They now auto-render with positional
`Item1` / `Item2` / … columns; project to a record for named columns.

**No-value cells.** A cell ending in a `let` or a `printfn` now shows only its
stdout plus a quiet `NoValue` — the exact `.NET Interactive` behavior. The
`Results` module's original reason for existing (surfacing *something* when the
last line produced no `it`) is intentionally gone.

**Retention rule.** `Handlers.runEvaluate` no longer checks
`Results.currentEnvelope` first — it always projects the tail value.
`ResultRetention.set` fires only when the projector returns
`RetainedTable.Some` (a materialized grid). A later non-table run
(`printfn`, a scalar, a streamed scalar seq) leaves the notebook's last
exportable grid in place; the old "discard a stale generic Table" guard is
deleted, because with one projection path a materialized generic table *is*
the result worth retaining.

**Robustness.** The shape dispatch runs in the worker after FSI returns, so a
reflection failure (e.g. a `seq` whose first element is a tuple but whose rest
are not) can't be caught by `EvalInteractionNonThrowing`. The whole dispatch
body is wrapped: on any exception it falls back to a `Text` envelope of
`sprintf "%A" value`.

**Prologues.** Both the FSI (`FsiSession.create`) and FSAC (`Prologue.build`)
prologues drop `open FSharpQueryStudio.Results` — no script-facing surface
remains in that namespace. The `#r` on the assembly stays (the worker still
projects its types). The FSAC prologue is now 3 lines; `PrologueTests` updated.

**Docs.** `editors/vscode/README.md` and `docs/TUTORIAL.md` replace the
`Results.*` tables with a "what a cell shows" table keyed on the last
expression's shape.

Files: `src/FSharpQueryStudio.Results/Projection.fs`,
`src/FSharpQueryStudio.Results/ScriptResults.fs` (deleted),
`src/FSharpQueryStudio.Results/FSharpQueryStudio.Results.fsproj`,
`src/FSharpQueryStudio.Worker/Handlers.fs`,
`src/FSharpQueryStudio.Execution/FsiSession.fs`,
`src/FSharpQueryStudio.Fsac/Prologue.fs`,
`tests/FSharpQueryStudio.Results.Tests/ProjectionTests.fs` (new),
`tests/FSharpQueryStudio.Results.Tests/ScriptResultsTests.fs` (deleted),
`tests/FSharpQueryStudio.Results.Tests/FSharpQueryStudio.Results.Tests.fsproj`,
`tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs`,
`tests/FSharpQueryStudio.Fsac.Tests/PrologueTests.fs`,
`editors/vscode/src/notebook/result.ts`,
`editors/vscode/src/notebook/table-payload.ts`,
`editors/vscode/README.md`, `docs/TUTORIAL.md`,
`docs/superpowers/specs/2026-09-08-notebook-tail-value-results-design.md` (new),
`docs/superpowers/plans/2026-09-08-notebook-tail-value-results.md` (new).
```

- [ ] **Step 4: Sanity-check the docs render**

Run: `git diff --stat` and eyeball `editors/vscode/README.md` / `docs/TUTORIAL.md`
for a stray `Results.raw` / `Results.table` / `Results.tableWithColumns` /
`Results.text`:
```bash
grep -rn "Results\.\(raw\|table\|tableWithColumns\|text\)" editors/vscode/README.md docs/TUTORIAL.md
```
Expected: no matches.

- [ ] **Step 5: Commit**

```bash
git add editors/vscode/README.md docs/TUTORIAL.md docs/DEVLOG.md
git commit -m "docs: rewrite the cell-results docs around the FSI tail value

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8"
```

---

## Task 7: Full-suite verification

**Files:** none.

- [ ] **Step 1: Clean build**

Run: `dotnet build FSharpQueryStudio.sln -c Release`
Expected: 0 warnings about unused `open FSharpQueryStudio.Results`, 0 errors.

- [ ] **Step 2: Full .NET test run**

Run: `dotnet test FSharpQueryStudio.sln`
Expected: all projects green. Spot-check the counts for
`FSharpQueryStudio.Results.Tests` (down by the deleted `ScriptResultsTests`,
up by `ProjectionTests`) and `FSharpQueryStudio.Worker.Tests`.

- [ ] **Step 3: Extension test run**

Run: `cd editors/vscode && npm test`
Expected: green, count unchanged from the Task 5 baseline.

- [ ] **Step 4: Manual smoke (if a dev host is available)**

Open a `.fsqnb`, run these cells, confirm each renders as the table says:
```fsharp
[ {| Id = 1; Name = "Ada" |}; {| Id = 2; Name = "Grace" |} ]   // grid, columns Id / Name
```
```fsharp
[ "alice", 3; "bob", 7 ]                                        // grid, columns Item1 / Item2
```
```fsharp
let x = 41
printfn "no value here"                                         // just the printed line, NoValue
```
```fsharp
42                                                              // plain text "42"
```
A connector cell + **Export CSV** still writes the file.

- [ ] **Step 5: Final commit (only if Step 4 surfaced a fix)**

```bash
git add -A
git commit -m "fix: address tail-value smoke-test findings

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8"
```

---

## Self-Review

**Spec coverage:**

| Spec item | Task |
|---|---|
| `Results` module removed entirely | Task 4 |
| Shape dispatch (record/tuple/dict/nested/scalar-seq/single-record) moved to `Projection` | Task 2 |
| `ConnectorQueryResult` tail value → grid + retention | Task 1 |
| Tuple seq → `Item1..ItemN` | Task 2 (dispatch), Task 6 (docs) |
| No-value cell → stdout + `NoValue` | Task 3 (test), already `Projection` behavior |
| `projectWithCursor` 3-tuple with `RetainedTable option` | Task 1 |
| `Handlers.runEvaluate` collapse, retention keyed on `retained.IsSome` | Task 3 |
| Non-materialized run leaves retention alone (no discard) | Task 3 (rule + test case 6) |
| Old stale-generic-Table discard guard deleted | Task 3 (delete test + code path) |
| Robustness try/with → Text fallback | Task 2 Step 5 |
| Scalar-seq keeps streaming `PagingCursor` + `continuationToken` | Task 2 (dispatch `_` arm), existing paging tests |
| Prologues drop `open FSharpQueryStudio.Results`, keep `#r` | Task 4 |
| `PrologueTests` line-count + `open` assertions updated | Task 4 |
| `ScriptResultsTests.fs` → `ProjectionTests.fs` | Tasks 1–2 (create), Task 4 (delete old) |
| `HandlersTests` `Results.*`-seeded cases rewritten | Task 3 |
| README / TUTORIAL rewrite | Task 6 |
| DEVLOG entry | Task 6 |
| `result.ts` / `table-payload.ts` comment touch-ups, no code change | Task 5 |

**Type consistency:** `Projection.RetainedTable = { Columns: string list; Rows: obj array array }` (Task 1) is mapped to `ResultRetention.RetainedResult` (identical shape) in `Handlers.runEvaluate` (Task 3). `projectWithCursor : obj -> int64 -> ResultEnvelope * PagingCursor option * RetainedTable option` is written the same in Tasks 1, 2, 3. `materializedTable` helper (Task 2) returns exactly that 3-tuple.

**Placeholder scan:** the record/dict/nested branch bodies in Task 2 Step 4 are described as "port verbatim from `dispatchRows`'s `columnNames = None` path" rather than re-typed — this is a move of existing, in-repo code shown in full in `ScriptResults.fs`, with the exact transformation (drop `columnNames`, feed `materializedTable`) and the full surrounding `match` skeleton given. Acceptable per the skill's "moving an existing block" allowance; every new-code fragment (the connector arm, `materializedTable`, the try/with, every test) is written out in full.

---

## Execution Handoff

Two execution options:

**1. Subagent-Driven (recommended)** — a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — tasks run in this session via `executing-plans`, batched with checkpoints.

Which approach?
