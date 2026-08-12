# Results.table Generalization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rename the current `ConnectorQueryResult`-only `Results.table` to `Results.raw` (unchanged behavior), and add a new `Results.table` that accepts any collection (records, tuples, nested collections, `Map`/`IDictionary`, or flat scalars), inferring columns from the data's shape with an optional explicit column-names override, both still pass-through.

**Architecture:** All work is in `src/FSharpQueryStudio.Results/ScriptResults.fs` and its tests. `raw` keeps the existing typed `ConnectorQueryResult -> ConnectorQueryResult` signature. The new `table` takes `obj -> string list option -> obj`, materializes the input to `obj array`, and dispatches on the runtime shape of the first element via `Microsoft.FSharp.Reflection` (`FSharpType.IsRecord`/`IsTuple`, `FSharpValue.GetRecordFields`/`GetTupleFields`). Both push onto the same private `Explicit` union consumed by `currentEnvelope`, so no changes are needed anywhere downstream of `ScriptResults.fs` (retention, export, worker, UI already key off `Kind`/`Schema`/`Rows`).

**Tech Stack:** F#, `FSharp.Core` reflection (`Microsoft.FSharp.Reflection`), xUnit (`Xunit.Assert`).

## Global Constraints

- `Results.table` does not accept `ConnectorQueryResult` — must throw a clear error naming `Results.raw`, checked before the generic `IEnumerable`/record dispatch (a `ConnectorQueryResult` is itself an F# record, so this check must run first or it silently misdispatches).
- `seq<ConnectorQueryResult>` is explicitly out of scope — not guarded against, falls through the generic record path. Do not add a guard for it.
- No change to `Results.text`, `Results.reset`, `currentEnvelope`'s shape, or anything in `ResultRetention`/Worker/App/Ui layers.
- No deduplication of column names on collision — positional binding (`ResultGridColumns.build` binds by `[i]`) makes duplicates cosmetic only.
- `string` is `IEnumerable<char>` in .NET and must be excluded from both the "is this a collection" check and the "nested collection element" check — `seq<string>` is one column of scalars, never exploded into characters.
- Every error path is a normal F# exception (`failwith`/`invalidArg`); no new error-handling plumbing — `EvalInteractionNonThrowing` → `Evaluation.evaluate` → `Projection.projectError` already turns exceptions into `ResultKind.Error` envelopes.
- `columnNames`, when `Some`, always wins as the final headers, applied positionally, and its count must match exactly or `table` throws naming both counts.

---

## File Structure

- Modify: `src/FSharpQueryStudio.Results/ScriptResults.fs` — rename current `table` to `raw`; add new `table` (dispatch, column inference, error paths) and its private helper functions (materialize-to-rows, shape dispatch, header/row builder).
- Modify: `tests/FSharpQueryStudio.Results.Tests/ScriptResultsTests.fs` — rename existing `table`-referencing tests to call `raw`; add new tests for every shape/error/edge case listed in the spec's Testing section.

No other files change (per Global Constraints and the spec's Non-goals).

---

## Task 1: Rename `table` to `raw`

**Files:**
- Modify: `src/FSharpQueryStudio.Results/ScriptResults.fs:37-44`
- Modify: `tests/FSharpQueryStudio.Results.Tests/ScriptResultsTests.fs:14-49`

**Interfaces:**
- Consumes: nothing new — `ConnectorQueryResult`, `Column`, `Explicit.Table` already exist.
- Produces: `Results.raw (query: ConnectorQueryResult) : ConnectorQueryResult`, behaviorally identical to the old `Results.table`. Later tasks' new `table` function must not collide with this name in the same module.

- [ ] **Step 1: Rename the two existing `table`-calling tests to `raw`, and update their calls**

Edit `tests/FSharpQueryStudio.Results.Tests/ScriptResultsTests.fs`. Rename the two test names and replace `Results.table query` with `Results.raw query`:

```fsharp
[<Fact>]
let ``raw pushes a real Table envelope with the connector's own column names, and returns its input unchanged`` () =
    Results.reset ()

    let query: ConnectorQueryResult =
        { Columns = [ "id"; "name" ]
          Rows = seq { yield [| box 1; box "Ada" |] } }

    let returned = Results.raw query
    Assert.Same(query, returned)

    let envelope = (Results.currentEnvelope 5L []).Value

    Assert.Equal(ResultKind.Table, envelope.Kind)
    Assert.Equal<string list>([ "id"; "name" ], envelope.Schema |> List.map (fun c -> c.Name))
    Assert.Single(envelope.Rows) |> ignore
    Assert.Equal<obj array>([| box 1; box "Ada" |], envelope.Rows.[0])
    Assert.False(envelope.IsTruncated)
    Assert.Equal(5L, envelope.ElapsedMs)

[<Fact>]
let ``raw fully materializes every row, even past the preview row limit, and never marks IsTruncated`` () =
    Results.reset ()

    let rowCount = Projection.PreviewRowLimit + 10
    let manyRows = seq { for i in 1..rowCount -> [| box i |] }

    let query: ConnectorQueryResult = { Columns = [ "n" ]; Rows = manyRows }

    let returned = Results.raw query
    Assert.Same(query, returned)

    let envelope = (Results.currentEnvelope 0L []).Value

    Assert.Equal(rowCount, envelope.Rows.Length)
    Assert.False(envelope.IsTruncated)
```

- [ ] **Step 2: Run the test project to confirm it fails to compile (function `raw` doesn't exist yet)**

Run: `dotnet test tests/FSharpQueryStudio.Results.Tests`
Expected: build error, `raw` not defined on `Results`.

- [ ] **Step 3: Rename `table` to `raw` in `ScriptResults.fs`**

Edit `src/FSharpQueryStudio.Results/ScriptResults.fs:37-44`, replacing:

```fsharp
    let table (query: ConnectorQueryResult) : ConnectorQueryResult =
        let columns =
            query.Columns |> List.map (fun name -> { Name = name; DisplayType = "obj" })

        let allRows = query.Rows |> Array.ofSeq

        explicit <- Some(Table(columns, allRows))
        query
```

with:

```fsharp
    let raw (query: ConnectorQueryResult) : ConnectorQueryResult =
        let columns =
            query.Columns |> List.map (fun name -> { Name = name; DisplayType = "obj" })

        let allRows = query.Rows |> Array.ofSeq

        explicit <- Some(Table(columns, allRows))
        query
```

Also update the doc comment above it (`ScriptResults.fs:31-36`) that says "every `Results.table`-produced result" — leave the general module-level doc comment (`ScriptResults.fs:6-15`) as-is for now since Task 2 will revise it once the new `table` exists.

- [ ] **Step 4: Run tests, confirm the renamed tests pass and nothing else broke**

Run: `dotnet test tests/FSharpQueryStudio.Results.Tests`
Expected: PASS, all existing tests green under new names.

- [ ] **Step 5: Commit**

```bash
git add src/FSharpQueryStudio.Results/ScriptResults.fs tests/FSharpQueryStudio.Results.Tests/ScriptResultsTests.fs
git commit -m "refactor: rename Results.table to Results.raw"
```

---

## Task 2: Scaffold new `table`, record-sequence dispatch, and the `ConnectorQueryResult` guard

**Files:**
- Modify: `src/FSharpQueryStudio.Results/ScriptResults.fs`
- Test: `tests/FSharpQueryStudio.Results.Tests/ScriptResultsTests.fs`

**Interfaces:**
- Consumes: `Explicit.Table of Column list * obj array array` (existing private union case), `Column = { Name: string; DisplayType: string }` (`FSharpQueryStudio.Protocol`).
- Produces: `Results.table (data: obj) (columnNames: string list option) : obj`. Later tasks (3-6) extend the same function's internal dispatch match — they must not change this signature.

This task builds the skeleton every later shape plugs into: materialization, the `ConnectorQueryResult` guard (checked first, before any other dispatch), the record-sequence branch, the `columnNames`-count-mismatch check, the non-collection error, the empty-sequence case, and the final header-application step. Tasks 3-6 each add one more branch to the same `match` inside `dispatchRows`.

- [ ] **Step 1: Write failing tests for record dispatch, the `ConnectorQueryResult` guard, non-collection input, and empty sequence**

Add to `tests/FSharpQueryStudio.Results.Tests/ScriptResultsTests.fs`:

```fsharp
type private Person = { Id: int; Name: string }

[<Fact>]
let ``table on a record sequence infers columns from reflected field names in declaration order`` () =
    Results.reset ()

    let people = [ { Id = 1; Name = "Ada" }; { Id = 2; Name = "Grace" } ]

    let returned = Results.table (box people) None
    Assert.Same(box people, returned)

    let envelope = (Results.currentEnvelope 0L []).Value

    Assert.Equal(ResultKind.Table, envelope.Kind)
    Assert.Equal<string list>([ "Id"; "Name" ], envelope.Schema |> List.map (fun c -> c.Name))
    Assert.Equal(2, envelope.Rows.Length)
    Assert.Equal<obj array>([| box 1; box "Ada" |], envelope.Rows.[0])
    Assert.Equal<obj array>([| box 2; box "Grace" |], envelope.Rows.[1])

[<Fact>]
let ``table on a record sequence with columnNames overrides the headers positionally`` () =
    Results.reset ()

    let people = [ { Id = 1; Name = "Ada" } ]

    Results.table (box people) (Some [ "identifier"; "full name" ]) |> ignore

    let envelope = (Results.currentEnvelope 0L []).Value
    Assert.Equal<string list>([ "identifier"; "full name" ], envelope.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box 1; box "Ada" |], envelope.Rows.[0])

[<Fact>]
let ``table throws naming both counts when columnNames count does not match inferred column count`` () =
    Results.reset ()

    let people = [ { Id = 1; Name = "Ada" } ]

    let ex =
        Assert.Throws<exn>(fun () -> Results.table (box people) (Some [ "only-one" ]) |> ignore)

    Assert.Contains("1", ex.Message)
    Assert.Contains("2", ex.Message)

[<Fact>]
let ``table throws referencing Results.raw when passed a ConnectorQueryResult directly`` () =
    Results.reset ()

    let query: ConnectorQueryResult = { Columns = [ "id" ]; Rows = Seq.empty }

    let ex = Assert.Throws<exn>(fun () -> Results.table (box query) None |> ignore)

    Assert.Contains("Results.raw", ex.Message)

[<Fact>]
let ``table throws a wrap-it error when data is not a collection at all`` () =
    Results.reset ()

    let ex = Assert.Throws<exn>(fun () -> Results.table (box 42) None |> ignore)

    Assert.Contains("wrap it", ex.Message)

[<Fact>]
let ``table on an empty sequence produces zero rows and columns from columnNames if given`` () =
    Results.reset ()

    Results.table (box (List.empty<Person>)) (Some [ "a"; "b" ]) |> ignore
    let withNames = (Results.currentEnvelope 0L []).Value
    Assert.Equal<string list>([ "a"; "b" ], withNames.Schema |> List.map (fun c -> c.Name))
    Assert.Empty(withNames.Rows)

    Results.reset ()
    Results.table (box (List.empty<Person>)) None |> ignore
    let noNames = (Results.currentEnvelope 0L []).Value
    Assert.Empty(noNames.Schema)
    Assert.Empty(noNames.Rows)

[<Fact>]
let ``table returns its input unchanged (pass-through)`` () =
    Results.reset ()

    let people = [ { Id = 1; Name = "Ada" } ]
    let input = box people

    let returned = Results.table input None

    Assert.Same(input, returned)
```

- [ ] **Step 2: Run tests to verify they fail to compile (`Results.table` with this new signature doesn't exist yet — the 2-arg `obj -> string list option -> obj` overload isn't defined)**

Run: `dotnet test tests/FSharpQueryStudio.Results.Tests`
Expected: build error — `table` still has the old 1-arg `ConnectorQueryResult -> ConnectorQueryResult` signature (now renamed to `raw`, so `table` is undefined).

- [ ] **Step 3: Implement the new `table` scaffold with record dispatch**

Add to `src/FSharpQueryStudio.Results/ScriptResults.fs`, after the `raw` function and before `text`:

```fsharp
    open Microsoft.FSharp.Reflection
    open System.Collections
    open System.Collections.Generic

    /// Applies an explicit `columnNames` override positionally over
    /// `inferredNames`, throwing if the counts disagree — the one rule
    /// that holds across every shape `table` dispatches on.
    let private resolveHeaders (inferredNames: string list) (columnNames: string list option) : string list =
        match columnNames with
        | None -> inferredNames
        | Some names when names.Length = inferredNames.Length -> names
        | Some names ->
            failwithf
                "columnNames has %d entries but rows have %d values"
                names.Length
                inferredNames.Length

    /// Row 0 of the materialized input decides every other row's shape;
    /// see the design doc's dispatch table for the full shape list. This
    /// task only implements the F# record branch — tuple, dictionary,
    /// nested-collection, and scalar branches are added in later tasks.
    let private dispatchRows (rows: obj array) (columnNames: string list option) : Column list * obj array array =
        match rows with
        | [||] ->
            let headers = columnNames |> Option.defaultValue []
            (headers |> List.map (fun n -> { Name = n; DisplayType = "obj" })), [||]
        | _ ->
            let first = rows.[0]

            match first with
            | :? ConnectorQueryResult ->
                failwith "Results.table does not accept ConnectorQueryResult — use Results.raw."
            | _ when first <> null && FSharpType.IsRecord(first.GetType()) ->
                let recordType = first.GetType()
                let fieldNames = FSharpType.GetRecordFields(recordType) |> Array.map (fun p -> p.Name) |> Array.toList
                let headers = resolveHeaders fieldNames columnNames

                let dataRows =
                    rows
                    |> Array.map (fun r -> FSharpValue.GetRecordFields(r))

                (headers |> List.map (fun n -> { Name = n; DisplayType = "obj" })), dataRows
            | _ -> failwith "Results.table: unrecognized element shape."

    /// New generalized entry point — accepts any collection (records,
    /// tuples, nested collections, Map/IDictionary, or flat scalars) and
    /// renders it as a real table, inferring columns from the data's own
    /// shape wherever possible. `columnNames`, when given, always wins as
    /// the final headers, applied positionally. See
    /// docs/superpowers/specs/2026-08-12-results-table-generalization-design.md.
    let table (data: obj) (columnNames: string list option) : obj =
        match data with
        | :? string ->
            failwith "Results.table expects a collection; got a single value — wrap it, e.g. [value]."
        | :? IEnumerable as enumerable ->
            let materialized = enumerable |> Seq.cast<obj> |> Array.ofSeq
            let columns, dataRows = dispatchRows materialized columnNames
            explicit <- Some(Table(columns, dataRows))
            data
        | _ ->
            failwith "Results.table expects a collection; got a single value — wrap it, e.g. [value]."
```

Place the `open Microsoft.FSharp.Reflection`, `open System.Collections`, `open System.Collections.Generic` lines at the top of the file alongside the existing `open` statements instead if the codebase's style prefers file-level opens — check `ScriptResults.fs:1-4` for the existing convention and match it (file currently opens `FSharpQueryStudio.Protocol`/`FSharpQueryStudio.Connectors` at file level, so move these three there too).

- [ ] **Step 4: Run tests, confirm all six new tests pass and Task 1's renamed tests still pass**

Run: `dotnet test tests/FSharpQueryStudio.Results.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FSharpQueryStudio.Results/ScriptResults.fs tests/FSharpQueryStudio.Results.Tests/ScriptResultsTests.fs
git commit -m "feat: add generalized Results.table with record-sequence dispatch"
```

---

## Task 3: Tuple-sequence dispatch

**Files:**
- Modify: `src/FSharpQueryStudio.Results/ScriptResults.fs` (`dispatchRows`'s `match first with` — add one branch)
- Test: `tests/FSharpQueryStudio.Results.Tests/ScriptResultsTests.fs`

**Interfaces:**
- Consumes: `dispatchRows`, `resolveHeaders` from Task 2 (unchanged signatures).
- Produces: no new public surface — extends `table`'s existing dispatch.

- [ ] **Step 1: Write failing tests for tuple sequences**

```fsharp
[<Fact>]
let ``table on a tuple sequence with columnNames maps each element to its own column positionally`` () =
    Results.reset ()

    let pairs = [ (1, "Ada"); (2, "Grace") ]

    Results.table (box pairs) (Some [ "id"; "name" ]) |> ignore

    let envelope = (Results.currentEnvelope 0L []).Value
    Assert.Equal<string list>([ "id"; "name" ], envelope.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box 1; box "Ada" |], envelope.Rows.[0])
    Assert.Equal<obj array>([| box 2; box "Grace" |], envelope.Rows.[1])

[<Fact>]
let ``table on a tuple sequence without columnNames throws the tuple-specific error`` () =
    Results.reset ()

    let pairs = [ (1, "Ada") ]

    let ex = Assert.Throws<exn>(fun () -> Results.table (box pairs) None |> ignore)

    Assert.Contains("columnNames", ex.Message)
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FSharpQueryStudio.Results.Tests`
Expected: FAIL — tuples currently hit the `dispatchRows` fallthrough `"Results.table: unrecognized element shape."` (or, for the no-`columnNames` case, would wrongly not throw the tuple-specific message).

- [ ] **Step 3: Add the tuple branch to `dispatchRows`**

Insert into the `match first with` in `dispatchRows` (`src/FSharpQueryStudio.Results/ScriptResults.fs`), directly after the record branch and before the final fallthrough:

```fsharp
            | _ when first <> null && FSharpType.IsTuple(first.GetType()) ->
                match columnNames with
                | None -> failwith "Results.table requires columnNames for tuple sequences."
                | Some names ->
                    let dataRows =
                        rows |> Array.map (fun r -> FSharpValue.GetTupleFields(r))

                    let width = if dataRows.Length = 0 then names.Length else dataRows.[0].Length
                    let headers = resolveHeaders (List.replicate width "") (Some names)
                    (headers |> List.map (fun n -> { Name = n; DisplayType = "obj" })), dataRows
```

- [ ] **Step 4: Run tests, confirm both new tests pass and all prior tests still pass**

Run: `dotnet test tests/FSharpQueryStudio.Results.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FSharpQueryStudio.Results/ScriptResults.fs tests/FSharpQueryStudio.Results.Tests/ScriptResultsTests.fs
git commit -m "feat: add tuple-sequence dispatch to Results.table"
```

---

## Task 4: `Map`/`IDictionary`-sequence dispatch

**Files:**
- Modify: `src/FSharpQueryStudio.Results/ScriptResults.fs` (`dispatchRows` — add one branch)
- Test: `tests/FSharpQueryStudio.Results.Tests/ScriptResultsTests.fs`

**Interfaces:**
- Consumes: `dispatchRows`, `resolveHeaders` from Task 2.
- Produces: no new public surface.

- [ ] **Step 1: Write failing tests for `Map`/`IDictionary` sequences, including a row missing a key**

```fsharp
[<Fact>]
let ``table on a Map sequence infers union-of-keys columns in first-seen order`` () =
    Results.reset ()

    let rows =
        [ Map.ofList [ ("id", box 1); ("name", box "Ada") ]
          Map.ofList [ ("id", box 2); ("extra", box "x") ] ]

    Results.table (box rows) None |> ignore

    let envelope = (Results.currentEnvelope 0L []).Value
    Assert.Equal<string list>([ "id"; "name"; "extra" ], envelope.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box 1; box "Ada"; null |], envelope.Rows.[0])
    Assert.Equal<obj array>([| box 2; null; box "x" |], envelope.Rows.[1])

[<Fact>]
let ``table on a Map sequence with columnNames overrides the headers positionally`` () =
    Results.reset ()

    let rows = [ Map.ofList [ ("id", box 1); ("name", box "Ada") ] ]

    Results.table (box rows) (Some [ "identifier"; "full name" ]) |> ignore

    let envelope = (Results.currentEnvelope 0L []).Value
    Assert.Equal<string list>([ "identifier"; "full name" ], envelope.Schema |> List.map (fun c -> c.Name))
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FSharpQueryStudio.Results.Tests`
Expected: FAIL — `Map` currently falls into the record check (`FSharpType.IsRecord` is false for `Map`, since `Map` is a class, not a record) and hits the fallthrough error.

- [ ] **Step 3: Add the `IDictionary` branch to `dispatchRows`**

`Map<'k,'v>` implements `IDictionary<'k,'v>`, which implements non-generic `IDictionary` — checking for `IDictionary` (from `System.Collections`) covers both `Map` and plain `IDictionary`/`Dictionary`. Insert into the `match first with`, after the tuple branch:

```fsharp
            | :? IDictionary ->
                let dicts = rows |> Array.map (fun r -> r :?> IDictionary)

                let keysInFirstSeenOrder =
                    let seen = HashSet<string>()
                    let ordered = ResizeArray<string>()

                    for d in dicts do
                        for key in d.Keys do
                            let k = string key

                            if seen.Add(k) then
                                ordered.Add(k)

                    ordered |> List.ofSeq

                let headers = resolveHeaders keysInFirstSeenOrder columnNames

                let dataRows =
                    dicts
                    |> Array.map (fun d ->
                        keysInFirstSeenOrder
                        |> List.map (fun k -> if d.Contains(k) then d.[k] else null)
                        |> Array.ofList)

                (headers |> List.map (fun n -> { Name = n; DisplayType = "obj" })), dataRows
```

- [ ] **Step 4: Run tests, confirm both new tests pass and all prior tests still pass**

Run: `dotnet test tests/FSharpQueryStudio.Results.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FSharpQueryStudio.Results/ScriptResults.fs tests/FSharpQueryStudio.Results.Tests/ScriptResultsTests.fs
git commit -m "feat: add Map/IDictionary-sequence dispatch to Results.table"
```

---

## Task 5: Nested-collection dispatch, ragged rows, and `seq<string>` exclusion

**Files:**
- Modify: `src/FSharpQueryStudio.Results/ScriptResults.fs` (`dispatchRows` — add one branch)
- Test: `tests/FSharpQueryStudio.Results.Tests/ScriptResultsTests.fs`

**Interfaces:**
- Consumes: `dispatchRows`, `resolveHeaders` from Task 2.
- Produces: no new public surface. This is the last shape branch — after this task, the only remaining branch is the scalar fallback (Task 6).

- [ ] **Step 1: Write failing tests for nested collections, ragged rows, and `seq<string>`**

```fsharp
[<Fact>]
let ``table on a nested collection without columnNames consumes row 0 as headers`` () =
    Results.reset ()

    let grid = [ [| "id"; "name" |]; [| "1"; "Ada" |]; [| "2"; "Grace" |] ]

    Results.table (box grid) None |> ignore

    let envelope = (Results.currentEnvelope 0L []).Value
    Assert.Equal<string list>([ "id"; "name" ], envelope.Schema |> List.map (fun c -> c.Name))
    Assert.Equal(2, envelope.Rows.Length)
    Assert.Equal<obj array>([| box "1"; box "Ada" |], envelope.Rows.[0])

[<Fact>]
let ``table on a nested collection with columnNames treats every row as data`` () =
    Results.reset ()

    let grid = [ [| "1"; "Ada" |]; [| "2"; "Grace" |] ]

    Results.table (box grid) (Some [ "id"; "name" ]) |> ignore

    let envelope = (Results.currentEnvelope 0L []).Value
    Assert.Equal(2, envelope.Rows.Length)
    Assert.Equal<obj array>([| box "1"; box "Ada" |], envelope.Rows.[0])

[<Fact>]
let ``table on ragged nested-collection rows pads short rows blank and truncates long rows`` () =
    Results.reset ()

    let grid: obj array list =
        [ [| box "a"; box "b"; box "c" |]
          [| box 1 |]
          [| box 2; box 3; box 4; box 5 |] ]

    Results.table (box grid) None |> ignore

    let envelope = (Results.currentEnvelope 0L []).Value
    Assert.Equal<string list>([ "a"; "b"; "c" ], envelope.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box 1; null; null |], envelope.Rows.[0])
    Assert.Equal<obj array>([| box 2; box 3; box 4 |], envelope.Rows.[0 + 1])

[<Fact>]
let ``table on a seq of strings renders as scalars, one column, not exploded into characters`` () =
    Results.reset ()

    let names = [ "Ada"; "Grace" ]

    Results.table (box names) None |> ignore

    let envelope = (Results.currentEnvelope 0L []).Value
    Assert.Equal<string list>([ "Value" ], envelope.Schema |> List.map (fun c -> c.Name))
    Assert.Equal(2, envelope.Rows.Length)
    Assert.Equal<obj array>([| box "Ada" |], envelope.Rows.[0])
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FSharpQueryStudio.Results.Tests`
Expected: FAIL — nested-collection rows currently hit the fallthrough error; `seq<string>` test will be revisited in Task 6 (scalar fallback) if it still fails after this task's branch, since a `string`-typed first element must NOT match the nested-collection check.

- [ ] **Step 3: Add the nested-collection branch to `dispatchRows`, ensuring `string` is excluded**

Insert into the `match first with`, after the `IDictionary` branch, and before the fallthrough:

```fsharp
            | :? IEnumerable as elementEnumerable when not (first :? string) ->
                let rawRows =
                    rows
                    |> Array.map (fun r -> (r :?> IEnumerable) |> Seq.cast<obj> |> Array.ofSeq)

                let headerRow, dataRawRows =
                    match columnNames with
                    | Some _ -> None, rawRows
                    | None ->
                        match rawRows with
                        | [||] -> None, rawRows
                        | _ -> Some(rawRows.[0] |> Array.map string |> Array.toList), rawRows.[1..]

                let width =
                    match columnNames with
                    | Some names -> names.Length
                    | None ->
                        match headerRow with
                        | Some h -> h.Length
                        | None -> if dataRawRows.Length = 0 then 0 else dataRawRows.[0].Length

                let inferredNames = headerRow |> Option.defaultValue (List.replicate width "")
                let headers = resolveHeaders inferredNames columnNames

                let padOrTruncate (row: obj array) : obj array =
                    if row.Length = width then
                        row
                    elif row.Length > width then
                        row.[0 .. width - 1]
                    else
                        Array.append row (Array.create (width - row.Length) null)

                let dataRows = dataRawRows |> Array.map padOrTruncate

                (headers |> List.map (fun n -> { Name = n; DisplayType = "obj" })), dataRows
```

Note the `when not (first :? string)` guard on this branch — without it, a `seq<string>` (each element itself `IEnumerable<char>`) would be misdispatched here.

- [ ] **Step 4: Run tests, confirm the three nested-collection/ragged tests pass**

Run: `dotnet test tests/FSharpQueryStudio.Results.Tests`
Expected: nested-collection and ragged-row tests PASS. The `seq<string>` test still FAILs (falls through to `"Results.table: unrecognized element shape."` since no scalar branch exists yet) — expected at this point, resolved in Task 6.

- [ ] **Step 5: Commit**

```bash
git add src/FSharpQueryStudio.Results/ScriptResults.fs tests/FSharpQueryStudio.Results.Tests/ScriptResultsTests.fs
git commit -m "feat: add nested-collection dispatch to Results.table, with ragged-row padding"
```

---

## Task 6: Scalar fallback dispatch

**Files:**
- Modify: `src/FSharpQueryStudio.Results/ScriptResults.fs` (`dispatchRows` — replace the fallthrough error with the scalar branch)
- Test: `tests/FSharpQueryStudio.Results.Tests/ScriptResultsTests.fs`

**Interfaces:**
- Consumes: `dispatchRows`, `resolveHeaders` from Task 2.
- Produces: no new public surface. This is the last branch in `dispatchRows` — after this task the function has no unhandled shape (everything that isn't a record, tuple, `IDictionary`, or non-string `IEnumerable` is a scalar).

- [ ] **Step 1: Write failing tests for scalar sequences (including the `seq<string>` test carried over from Task 5, since it depends on this branch existing)**

```fsharp
[<Fact>]
let ``table on a scalar sequence defaults to a single Value column`` () =
    Results.reset ()

    let numbers = [ 1; 2; 3 ]

    Results.table (box numbers) None |> ignore

    let envelope = (Results.currentEnvelope 0L []).Value
    Assert.Equal<string list>([ "Value" ], envelope.Schema |> List.map (fun c -> c.Name))
    Assert.Equal(3, envelope.Rows.Length)
    Assert.Equal<obj array>([| box 1 |], envelope.Rows.[0])
    Assert.Equal<obj array>([| box 2 |], envelope.Rows.[1])
    Assert.Equal<obj array>([| box 3 |], envelope.Rows.[2])

[<Fact>]
let ``table on a scalar sequence with columnNames uses it as the single header`` () =
    Results.reset ()

    let numbers = [ 1; 2 ]

    Results.table (box numbers) (Some [ "n" ]) |> ignore

    let envelope = (Results.currentEnvelope 0L []).Value
    Assert.Equal<string list>([ "n" ], envelope.Schema |> List.map (fun c -> c.Name))
```

(The `seq<string>` test from Task 5, `` table on a seq of strings renders as scalars, one column, not exploded into characters ``, is not re-added here — it already exists in the file from Task 5's Step 1 and simply starts passing once this task's branch lands.)

- [ ] **Step 2: Run tests to verify the two new scalar tests, and the carried-over `seq<string>` test, all fail**

Run: `dotnet test tests/FSharpQueryStudio.Results.Tests`
Expected: FAIL — all three currently hit `"Results.table: unrecognized element shape."`.

- [ ] **Step 3: Replace `dispatchRows`'s fallthrough with the scalar branch**

In `src/FSharpQueryStudio.Results/ScriptResults.fs`, replace this line at the end of `dispatchRows`'s `match first with`:

```fsharp
            | _ -> failwith "Results.table: unrecognized element shape."
```

with:

```fsharp
            | _ ->
                let headers = resolveHeaders [ "Value" ] columnNames
                let dataRows = rows |> Array.map (fun r -> [| r |])
                (headers |> List.map (fun n -> { Name = n; DisplayType = "obj" })), dataRows
```

- [ ] **Step 4: Run tests, confirm all scalar and `seq<string>` tests pass, and the full suite is green**

Run: `dotnet test tests/FSharpQueryStudio.Results.Tests`
Expected: PASS, all tests in the file green.

- [ ] **Step 5: Commit**

```bash
git add src/FSharpQueryStudio.Results/ScriptResults.fs tests/FSharpQueryStudio.Results.Tests/ScriptResultsTests.fs
git commit -m "feat: add scalar fallback dispatch to Results.table, completing all shapes"
```

---

## Task 7: Mixed-shape-sequence surfacing check, module doc-comment update, and full-suite verification

**Files:**
- Modify: `src/FSharpQueryStudio.Results/ScriptResults.fs:6-15` (module-level doc comment)
- Test: `tests/FSharpQueryStudio.Results.Tests/ScriptResultsTests.fs`

**Interfaces:**
- Consumes: everything from Tasks 1-6 — no code changes to `dispatchRows`/`table` in this task, only a doc comment and a test confirming existing behavior (dispatch-on-first-element already means a later differently-shaped row surfaces as a runtime error naming that row, per the design doc — this task verifies that's actually true, it doesn't add new handling).
- Produces: nothing new — this is the final task, no later task depends on it.

- [ ] **Step 1: Write a failing (well, currently-passing-by-accident-or-not-yet-verified) test for a mixed-shape sequence surfacing an error naming the failing row**

```fsharp
[<Fact>]
let ``table dispatches on the first element's shape; a later row of a different shape fails with a row-naming error`` () =
    Results.reset ()

    let mixed: obj list = [ box (1, "Ada"); box { Id = 2; Name = "Grace" } ]

    let ex =
        Assert.Throws<exn>(fun () -> Results.table (box mixed) (Some [ "id"; "name" ]) |> ignore)

    Assert.NotNull(ex.Message)
```

Note: this test intentionally asserts only that an exception is thrown (not a specific row-number message), since `FSharpValue.GetTupleFields` on a non-tuple record value throws its own `System.ArgumentException` from `FSharp.Core`, not a message this codebase composes — the design doc only promises the error "surfaces as a runtime error naming the row," which the existing `EvalInteractionNonThrowing` pipeline (per this plan's Global Constraints) already turns into a structured `ResultKind.Error` envelope regardless of the exact exception type. If a stronger row-naming guarantee is wanted later, that's a separate, explicit enhancement — not scoped here.

- [ ] **Step 2: Run the test**

Run: `dotnet test tests/FSharpQueryStudio.Results.Tests`
Expected: PASS already (dispatch naturally throws on the shape mismatch via `FSharpValue.GetTupleFields`/`GetRecordFields` failing on the wrong-shaped element) — confirms Non-goal/edge-case behavior from the design doc without needing new code.

- [ ] **Step 3: Update the module-level doc comment to describe both functions**

In `src/FSharpQueryStudio.Results/ScriptResults.fs:6-15`, replace:

```fsharp
/// The controlled surface `.fsx` scripts use to explicitly choose how a
/// value shows up in the Results pane, instead of relying solely on F#
/// Interactive's own last-expression-value convention — a script whose
/// last line is a `let` binding or a side-effecting call (like `printfn`)
/// produces no "it" value at all, which used to render as a bare,
/// confusing "null" (previously "Some(null)" — see `ResultViewModel`'s
/// own fix) in the Results pane no matter what the script actually
/// computed earlier. `table`/`text` both return their input unchanged,
/// so they compose in a pipeline without disturbing the script's own
/// logic; whichever was called most recently wins for this evaluation.
```

with:

```fsharp
/// The controlled surface `.fsx` scripts use to explicitly choose how a
/// value shows up in the Results pane, instead of relying solely on F#
/// Interactive's own last-expression-value convention — a script whose
/// last line is a `let` binding or a side-effecting call (like `printfn`)
/// produces no "it" value at all, which used to render as a bare,
/// confusing "null" (previously "Some(null)" — see `ResultViewModel`'s
/// own fix) in the Results pane no matter what the script actually
/// computed earlier. `raw`/`table`/`text` all return their input
/// unchanged, so they compose in a pipeline without disturbing the
/// script's own logic; whichever was called most recently wins for this
/// evaluation. `raw` accepts only a `ConnectorQueryResult`; `table`
/// accepts any other collection (records, tuples, nested collections,
/// Map/IDictionary, or flat scalars) and infers columns from its shape.
```

- [ ] **Step 4: Run the full `FSharpQueryStudio.Results.Tests` suite one final time**

Run: `dotnet test tests/FSharpQueryStudio.Results.Tests`
Expected: PASS, every test in `ScriptResultsTests.fs` green (renamed `raw` tests, and every `table` shape/error/edge-case test from Tasks 2-7).

- [ ] **Step 5: Run the solution-wide test suite to confirm no downstream breakage in Worker/Ui/Integration tests**

Run: `dotnet test`
Expected: PASS — per the design doc's Non-goals, `Worker.Tests`/`Ui.Tests`/`Integration.Tests` need no changes since `runEvaluate`'s retention-commit logic keys off `Results.currentEnvelope`'s `Kind`/`Schema`/`Rows`, not which function produced them.

- [ ] **Step 6: Commit**

```bash
git add src/FSharpQueryStudio.Results/ScriptResults.fs tests/FSharpQueryStudio.Results.Tests/ScriptResultsTests.fs
git commit -m "docs: update ScriptResults module doc comment for raw/table split"
```
