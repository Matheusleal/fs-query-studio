namespace FSharpQueryStudio.Results

open System
open System.Collections
open System.Collections.Concurrent
open System.Collections.Generic
open System.Reflection
open Microsoft.FSharp.Reflection
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors

/// Converts a raw FSI value into the bounded, protocol-neutral
/// `ResultEnvelope` shape from Section 11. Scoped to `Scalar`/`Text`/
/// `Table` for the vertical slice — `Object`/`Error`/`NoValue` land with
/// their consumers in later tasks.
module Projection =

    [<Literal>]
    let PreviewRowLimit = 500

    /// A fully-materialized table the worker retains for Export CSV — see
    /// docs/superpowers/specs/2026-09-08-notebook-tail-value-results-design.md.
    /// `None` for every non-materialized result (scalars, text, a streamed
    /// scalar `seq`). Shaped to match `ResultRetention.RetainedResult`,
    /// which this file compiles too early to reference directly.
    type RetainedTable = { Columns: string list; Rows: obj array array }

    /// A script can legitimately declare `type private Foo = { ... }`.
    /// `FSharpType`'s reflection helpers default to public-only members,
    /// which would silently miss such a record's fields — pass this
    /// explicitly everywhere a record is inspected.
    let private recordBindingFlags =
        BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Instance

    let private isSimplePrimitive (t: Type) =
        t.IsPrimitive
        || t = typeof<decimal>
        || t = typeof<DateTime>
        || t = typeof<DateTimeOffset>
        || t = typeof<Guid>
        || t = typeof<TimeSpan>

    let private emptyEnvelope (kind: ResultKind) (elapsedMs: int64) : ResultEnvelope =
        { Kind = kind
          DisplayText = None
          Schema = []
          Rows = [||]
          IsTruncated = false
          ContinuationToken = None
          ElapsedMs = elapsedMs
          Output = [] }

    let private recordSchema (t: Type) : Column list =
        FSharpType.GetRecordFields(t, recordBindingFlags)
        |> Array.map (fun p -> { Name = p.Name; DisplayType = p.PropertyType.Name })
        |> Array.toList

    let private recordRow (t: Type) (value: obj) : obj array =
        FSharpType.GetRecordFields(t, recordBindingFlags)
        |> Array.map (fun p -> p.GetValue(value))

    type private IDictionaryAccessor =
        abstract GetKeys: obj -> string list
        abstract GetValue: obj -> string -> obj

    let private dictionaryCache = ConcurrentDictionary<Type, IDictionaryAccessor option>()

    let private createDictionaryAccessor (typ: Type) : IDictionaryAccessor option =
        if isNull typ then None
        elif typeof<IDictionary>.IsAssignableFrom(typ) then
            Some
                { new IDictionaryAccessor with
                    member _.GetKeys(obj: obj) =
                        match obj with
                        | :? IDictionary as d ->
                            let list = ResizeArray<string>()
                            for k in d.Keys do
                                if not (isNull k) then list.Add(string k)
                            list |> List.ofSeq
                        | _ -> []

                    member _.GetValue(obj: obj) (key: string) =
                        match obj with
                        | :? IDictionary as d ->
                            if d.Contains(key) then d.[key] else null
                        | _ -> null }
        else
            // Check for generic IDictionary<K,V>, IReadOnlyDictionary<K,V>, or FSharpMap
            let interfaces = typ.GetInterfaces()
            let genericDictInterface =
                interfaces
                |> Array.tryFind (fun i ->
                    i.IsGenericType &&
                    (i.GetGenericTypeDefinition() = typedefof<System.Collections.Generic.IDictionary<_, _>> ||
                     i.GetGenericTypeDefinition() = typedefof<System.Collections.Generic.IReadOnlyDictionary<_, _>>))

            let isFSharpMap =
                typ.IsGenericType && typ.GetGenericTypeDefinition().Name = "FSharpMap`2"

            match genericDictInterface with
            | Some gi ->
                let keysProperty = gi.GetProperty("Keys")
                let containsKeyMethod = gi.GetMethod("ContainsKey")
                let itemProperty = gi.GetProperty("Item")

                Some
                    { new IDictionaryAccessor with
                        member _.GetKeys(obj: obj) =
                            if isNull obj || isNull keysProperty then []
                            else
                                match keysProperty.GetValue(obj) with
                                | :? IEnumerable as seq ->
                                    let list = ResizeArray<string>()
                                    for k in seq do
                                        if not (isNull k) then list.Add(string k)
                                    list |> List.ofSeq
                                | _ -> []

                        member _.GetValue(obj: obj) (key: string) =
                            if isNull obj || isNull containsKeyMethod || isNull itemProperty then null
                            else
                                try
                                    let contains = containsKeyMethod.Invoke(obj, [| box key |]) :?> bool
                                    if contains then itemProperty.GetValue(obj, [| box key |])
                                    else null
                                with _ -> null }
            | None when isFSharpMap ->
                let keysProperty = typ.GetProperty("Keys")
                let containsKeyMethod = typ.GetMethod("ContainsKey")
                let itemProperty = typ.GetProperty("Item")

                Some
                    { new IDictionaryAccessor with
                        member _.GetKeys(obj: obj) =
                            if isNull obj || isNull keysProperty then []
                            else
                                match keysProperty.GetValue(obj) with
                                | :? IEnumerable as seq ->
                                    let list = ResizeArray<string>()
                                    for k in seq do
                                        if not (isNull k) then list.Add(string k)
                                    list |> List.ofSeq
                                | _ -> []

                        member _.GetValue(obj: obj) (key: string) =
                            if isNull obj || isNull containsKeyMethod || isNull itemProperty then null
                            else
                                try
                                    let contains = containsKeyMethod.Invoke(obj, [| box key |]) :?> bool
                                    if contains then itemProperty.GetValue(obj, [| box key |])
                                    else null
                                with _ -> null }
            | None -> None

    let private getDictionaryAccessor (typ: Type) : IDictionaryAccessor option =
        if isNull typ then None
        else dictionaryCache.GetOrAdd(typ, createDictionaryAccessor)

    /// Check if a type is dictionary-like (implements IDictionary<K,V> for any K,V,
    /// or is a non-generic IDictionary, or is an F# Map)
    let private isDictionary (typ: System.Type) : bool =
        (getDictionaryAccessor typ).IsSome

    /// Extract all keys from a dictionary-like object as strings
    let private getDictionaryKeys (obj: obj) : string list =
        if isNull obj then []
        else
            match getDictionaryAccessor (obj.GetType()) with
            | Some acc -> acc.GetKeys(obj)
            | None -> []

    /// Get a value from a dictionary-like object, or null if key not found
    let private getDictionaryValue (obj: obj) (key: string) : obj =
        if isNull obj then null
        else
            match getDictionaryAccessor (obj.GetType()) with
            | Some acc -> acc.GetValue obj key
            | None -> null

    /// Same shape as `project`, plus the live `PagingCursor` a truncated
    /// Table result was cut off at — Section 7's `results/page`. `None`
    /// whenever nothing was truncated (every other kind, or a sequence
    /// that already fit within `PreviewRowLimit`). The cursor holds the
    /// same enumerator `project` would have discarded, so a caller that
    /// wants to keep paging never re-runs whatever produced `value`.
    let projectWithCursor (value: obj) (elapsedMs: int64) : ResultEnvelope * PagingCursor option * RetainedTable option =
        // Shared builder for the four materialized shapes (record / tuple /
        // dictionary / nested): the preview envelope is truncated to
        // `PreviewRowLimit`, but the full row set is always retained for
        // Export CSV, and the cursor is `None` (nothing to stream).
        let materializedTable (columns: Column list) (dataRows: obj array array) : ResultEnvelope * PagingCursor option * RetainedTable option =
            // Every materialized shape is made rectangular here, by
            // construction: a row matching the schema width passes through,
            // a longer one is trimmed, a shorter one is null-padded. The
            // tuple / dict / record arms feed rows that are already the
            // right width; the nested arm's rows are genuinely ragged.
            let width = columns.Length
            let padOrTruncate (row: obj array) : obj array =
                if row.Length = width then row
                elif row.Length > width then row.[0 .. width - 1]
                else Array.append row (Array.create (width - row.Length) null)

            let rectangular = dataRows |> Array.map padOrTruncate
            let preview = rectangular |> Array.truncate PreviewRowLimit
            let envelope =
                { emptyEnvelope ResultKind.Table elapsedMs with
                    Schema = columns
                    Rows = preview
                    IsTruncated = rectangular.Length > PreviewRowLimit }
            envelope, None, Some { Columns = columns |> List.map (fun c -> c.Name); Rows = rectangular }

        try
            match value with
            | null -> emptyEnvelope ResultKind.NoValue elapsedMs, None, None
            | :? string as s -> { emptyEnvelope ResultKind.Text elapsedMs with DisplayText = Some s }, None, None
            | v when isSimplePrimitive (v.GetType()) ->
                { emptyEnvelope ResultKind.Scalar elapsedMs with
                    DisplayText = Some(string v) },
                None,
                None
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
            | v when FSharpType.IsRecord(v.GetType(), recordBindingFlags) ->
                let t = v.GetType()
                materializedTable (recordSchema t) [| recordRow t v |]
            | :? IEnumerable as items when not (value :? string) ->
                // Peek only element 0. A flat scalar `seq` (possibly large
                // or infinite) must stay on the lazy `PagingCursor` path —
                // realizing it up front would hang/OOM the worker before
                // the streaming arm is reached, and `try/with` can't
                // rescue a non-terminating enumeration. Only the
                // materialized shapes below pull the whole sequence.
                let e = items.GetEnumerator()

                if not (e.MoveNext()) then
                    // A cell ending in `[]` must not overwrite the notebook's
                    // exportable grid with an empty table — return the empty
                    // Table envelope but retain nothing.
                    emptyEnvelope ResultKind.Table elapsedMs, None, None
                else
                    let first = e.Current
                    // The rest of the sequence, still lazy, with element 0 re-prepended.
                    let full =
                        seq {
                            yield first
                            while e.MoveNext() do
                                yield e.Current
                        }

                    match first with
                    | :? ConnectorQueryResult ->
                        failwith "a sequence of ConnectorQueryResult is not supported; enumerate .Rows"
                    | _ when not (isNull first) && FSharpType.IsRecord(first.GetType(), recordBindingFlags) ->
                        let rows = full |> Array.ofSeq
                        let recordType = first.GetType()

                        let dataRows =
                            rows |> Array.map (fun r -> FSharpValue.GetRecordFields(r, recordBindingFlags))

                        materializedTable (recordSchema recordType) dataRows
                    | _ when not (isNull first) && FSharpType.IsTuple(first.GetType()) ->
                        let rows = full |> Array.ofSeq
                        let width = (FSharpValue.GetTupleFields first).Length
                        let names =
                            [ for i in 1..width -> { Name = sprintf "Item%d" i; DisplayType = "obj" } ]
                        let dataRows = rows |> Array.map (fun r -> FSharpValue.GetTupleFields r)
                        materializedTable names dataRows
                    | _ when not (isNull first) && isDictionary (first.GetType()) ->
                        let rows = full |> Array.ofSeq

                        let keysInFirstSeenOrder =
                            let seen = HashSet<string>()
                            let ordered = ResizeArray<string>()

                            for row in rows do
                                let keys = getDictionaryKeys row
                                for key in keys do
                                    if seen.Add(key) then
                                        ordered.Add(key)

                            ordered |> List.ofSeq

                        let dataRows =
                            rows
                            |> Array.map (fun row ->
                                keysInFirstSeenOrder
                                |> List.map (fun k -> getDictionaryValue row k)
                                |> Array.ofList)

                        let columns =
                            keysInFirstSeenOrder |> List.map (fun k -> { Name = k; DisplayType = "obj" })

                        materializedTable columns dataRows
                    | :? IEnumerable when not (first :? string) ->
                        let rows = full |> Array.ofSeq

                        let rawRows =
                            rows
                            |> Array.map (fun r -> (r :?> IEnumerable) |> Seq.cast<obj> |> Array.ofSeq)

                        let headers = rawRows.[0] |> Array.map string |> Array.toList
                        let columns = headers |> List.map (fun n -> { Name = n; DisplayType = "obj" })
                        // Rows 1.. can be ragged — `materializedTable` squares
                        // them to the header width.
                        materializedTable columns rawRows.[1..]
                    | _ ->
                        // scalar / string sequence: keep the lazy streaming
                        // path — feed the *lazy* `full` enumerator to
                        // `PagingCursor`, so only ~PreviewRowLimit+1
                        // elements are ever pulled.
                        let cursor = PagingCursor(full.GetEnumerator())
                        let page, hasMore = cursor.TakePage(PreviewRowLimit)

                        let envelope =
                            { emptyEnvelope ResultKind.Table elapsedMs with
                                Schema = [ { Name = "Value"; DisplayType = "obj" } ]
                                Rows = page |> Array.map (fun r -> [| r |])
                                IsTruncated = hasMore }

                        envelope, (if hasMore then Some cursor else None), None
            | v -> { emptyEnvelope ResultKind.Text elapsedMs with DisplayText = Some(sprintf "%A" v) }, None, None
        with _ ->
            // `sprintf "%A" value` re-enumerates `value`; a one-shot `seq`
            // that failed mid-enumeration above throws again here, and this
            // handler is the last line of defence — it must never itself
            // escape `projectWithCursor`.
            let text =
                try
                    sprintf "%A" value
                with _ ->
                    "<unprintable value>"

            { emptyEnvelope ResultKind.Text elapsedMs with DisplayText = Some text }, None, None

    let project (value: obj) (elapsedMs: int64) : ResultEnvelope =
        let env, _, _ = projectWithCursor value elapsedMs
        env

    /// An uncaught script exception becomes a structured Error result —
    /// see Section 9 — never an unhandled worker crash.
    let projectError (ex: exn) (elapsedMs: int64) : ResultEnvelope =
        { emptyEnvelope ResultKind.Error elapsedMs with
            DisplayText = Some ex.Message }

    let projectErrorWithDetails (ex: exn) (details: string option) (elapsedMs: int64) : ResultEnvelope =
        let msg =
            match details with
            | Some d when not (String.IsNullOrWhiteSpace d) && ex.Message.Contains("Operation could not be completed due to earlier error") -> d
            | _ -> ex.Message
        { emptyEnvelope ResultKind.Error elapsedMs with
            DisplayText = Some msg }

    /// A worker-level error (crash, busy) — see A.8/A.9's `WorkerErrorInfo`
    /// — reads the same as a script-level Error to whatever's displaying
    /// results; the caller didn't get to run at all, but the UI still
    /// needs one shape to show.
    let projectWorkerError (error: WorkerErrorInfo) : ResultEnvelope =
        { emptyEnvelope ResultKind.Error 0L with
            DisplayText = Some error.Message }

    let private linesOf (stream: OutputStream) (text: string) : OutputLine list =
        if String.IsNullOrEmpty(text) then
            []
        else
            let raw = text.Split('\n')
            // A trailing "\n" produces one trailing empty split segment
            // that isn't a real printed line - drop just that one.
            let segments =
                if raw.Length > 0 && raw.[raw.Length - 1] = "" then
                    raw.[.. raw.Length - 2]
                else
                    raw

            segments |> Array.map (fun line -> { Stream = stream; Text = line.TrimEnd('\r') }) |> Array.toList

    /// Captured stdout/stderr (Section 15's Evaluation outcome) become the
    /// `ResultEnvelope.Output` lines the UI's output console binds to —
    /// see A.16. Order is stdout-then-stderr, not interleaved by time:
    /// `Evaluation` captures them as two separate buffers, not a single
    /// timestamped stream.
    let toOutputLines (stdout: string) (stderr: string) : OutputLine list =
        linesOf OutputStream.Stdout stdout @ linesOf OutputStream.Stderr stderr
