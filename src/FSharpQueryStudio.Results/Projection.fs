namespace FSharpQueryStudio.Results

open System
open System.Collections
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

    /// Check if a type is dictionary-like (implements IDictionary<K,V> for any K,V,
    /// or is a non-generic IDictionary, or is an F# Map)
    let private isDictionary (typ: System.Type) : bool =
        if typ = null then false
        else
            let interfaces = typ.GetInterfaces()
            // Check for non-generic IDictionary
            let hasNonGenericIDictionary =
                interfaces
                |> Array.exists (fun i -> i = typeof<IDictionary>)
            // Check for any IDictionary<K,V> generic interface
            let hasGenericIDictionary =
                interfaces
                |> Array.exists (fun i ->
                    i.IsGenericType &&
                    i.GetGenericTypeDefinition() = typedefof<System.Collections.Generic.IDictionary<_, _>>)
            // Check if it's an F# Map type
            let isFSharpMap =
                typ.IsGenericType &&
                typ.GetGenericTypeDefinition().Name = "FSharpMap`2"

            hasNonGenericIDictionary || hasGenericIDictionary || isFSharpMap

    /// Extract all keys from a dictionary-like object as strings
    let private getDictionaryKeys (obj: obj) : string list =
        if obj = null then []
        else
            let typ = obj.GetType()
            let interfaces = typ.GetInterfaces()

            // Try non-generic IDictionary first
            let nonGenericInterface = interfaces |> Array.tryFind (fun i -> i = typeof<IDictionary>)
            match nonGenericInterface with
            | Some _ ->
                try
                    let dict = obj :?> IDictionary
                    dict.Keys
                    |> Seq.cast<obj>
                    |> Seq.map string
                    |> List.ofSeq
                with _ ->
                    // If casting fails, try generic approach
                    let genericInterface =
                        interfaces
                        |> Array.tryFind (fun i ->
                            i.IsGenericType &&
                            i.GetGenericTypeDefinition() = typedefof<System.Collections.Generic.IDictionary<_, _>>)
                    match genericInterface with
                    | Some gi ->
                        let keysProperty = gi.GetProperty("Keys")
                        if keysProperty <> null then
                            let keys = keysProperty.GetValue(obj) :?> System.Collections.IEnumerable
                            keys
                            |> Seq.cast<obj>
                            |> Seq.map string
                            |> List.ofSeq
                        else []
                    | None -> []
            | None ->
                // Try generic IDictionary<K,V>
                let genericInterface =
                    interfaces
                    |> Array.tryFind (fun i ->
                        i.IsGenericType &&
                        i.GetGenericTypeDefinition() = typedefof<System.Collections.Generic.IDictionary<_, _>>)
                match genericInterface with
                | Some gi ->
                    let keysProperty = gi.GetProperty("Keys")
                    if keysProperty <> null then
                        let keys = keysProperty.GetValue(obj) :?> System.Collections.IEnumerable
                        keys
                        |> Seq.cast<obj>
                        |> Seq.map string
                        |> List.ofSeq
                    else []
                | None -> []

    /// Get a value from a dictionary-like object, or null if key not found
    let private getDictionaryValue (obj: obj) (key: string) : obj =
        if obj = null then null
        else
            let typ = obj.GetType()
            let interfaces = typ.GetInterfaces()

            // Try non-generic IDictionary first
            let nonGenericInterface = interfaces |> Array.tryFind (fun i -> i = typeof<IDictionary>)
            match nonGenericInterface with
            | Some _ ->
                try
                    let dict = obj :?> IDictionary
                    if dict.Contains(key) then dict.[key] else null
                with _ ->
                    // If casting fails, try generic approach
                    let genericInterface =
                        interfaces
                        |> Array.tryFind (fun i ->
                            i.IsGenericType &&
                            i.GetGenericTypeDefinition() = typedefof<System.Collections.Generic.IDictionary<_, _>>)
                    match genericInterface with
                    | Some gi ->
                        let containsKeyMethod = gi.GetMethod("ContainsKey")
                        let itemProperty = gi.GetProperty("Item")
                        if containsKeyMethod <> null && itemProperty <> null then
                            let containsKey = containsKeyMethod.Invoke(obj, [| key |]) :?> bool
                            if containsKey then
                                itemProperty.GetValue(obj, [| key |])
                            else null
                        else null
                    | None -> null
            | None ->
                // Try generic IDictionary<K,V>
                let genericInterface =
                    interfaces
                    |> Array.tryFind (fun i ->
                        i.IsGenericType &&
                        i.GetGenericTypeDefinition() = typedefof<System.Collections.Generic.IDictionary<_, _>>)
                match genericInterface with
                | Some gi ->
                    let containsKeyMethod = gi.GetMethod("ContainsKey")
                    let itemProperty = gi.GetProperty("Item")
                    if containsKeyMethod <> null && itemProperty <> null then
                        let containsKey = containsKeyMethod.Invoke(obj, [| key |]) :?> bool
                        if containsKey then
                            itemProperty.GetValue(obj, [| key |])
                        else null
                    else null
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
        let materializedTable (columnNames: string list) (dataRows: obj array array) : ResultEnvelope * PagingCursor option * RetainedTable option =
            let schema = columnNames |> List.map (fun n -> { Name = n; DisplayType = "obj" })
            let preview = dataRows |> Array.truncate PreviewRowLimit
            let envelope =
                { emptyEnvelope ResultKind.Table elapsedMs with
                    Schema = schema
                    Rows = preview
                    IsTruncated = dataRows.Length > PreviewRowLimit }
            envelope, None, Some { Columns = columnNames; Rows = dataRows }

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
                let cols = recordSchema t |> List.map (fun c -> c.Name)
                materializedTable cols [| recordRow t v |]
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
                        let recordType = first.GetType()

                        let fieldNames =
                            FSharpType.GetRecordFields(recordType, allFlags)
                            |> Array.map (fun p -> p.Name)
                            |> Array.toList

                        let dataRows =
                            rows |> Array.map (fun r -> FSharpValue.GetRecordFields(r, allFlags))

                        materializedTable fieldNames dataRows
                    | _ when first <> null && FSharpType.IsTuple(first.GetType()) ->
                        let width = (FSharpValue.GetTupleFields first).Length
                        let names = [ for i in 1..width -> sprintf "Item%d" i ]
                        let dataRows = rows |> Array.map (fun r -> FSharpValue.GetTupleFields r)
                        materializedTable names dataRows
                    | _ when first <> null && isDictionary (first.GetType()) ->
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
                                |> List.map (fun k ->
                                    let value = getDictionaryValue row k
                                    if value <> null then value else null)
                                |> Array.ofList)

                        materializedTable keysInFirstSeenOrder dataRows
                    | :? IEnumerable when not (first :? string) ->
                        let rawRows =
                            rows
                            |> Array.map (fun r -> (r :?> IEnumerable) |> Seq.cast<obj> |> Array.ofSeq)

                        let headers = rawRows.[0] |> Array.map string |> Array.toList
                        let dataRawRows = rawRows.[1..]
                        let width = headers.Length

                        let padOrTruncate (row: obj array) : obj array =
                            if row.Length = width then row
                            elif row.Length > width then row.[0 .. width - 1]
                            else Array.append row (Array.create (width - row.Length) null)

                        let dataRows = dataRawRows |> Array.map padOrTruncate
                        materializedTable headers dataRows
                    | _ ->
                        let cursor = PagingCursor(items.GetEnumerator())
                        let page, hasMore = cursor.TakePage(PreviewRowLimit)

                        let envelope =
                            { emptyEnvelope ResultKind.Table elapsedMs with
                                Schema = [ { Name = "Value"; DisplayType = "obj" } ]
                                Rows = page |> Array.map (fun r -> [| r |])
                                IsTruncated = hasMore }

                        envelope, (if hasMore then Some cursor else None), None
            | v -> { emptyEnvelope ResultKind.Text elapsedMs with DisplayText = Some(string v) }, None, None
        with _ ->
            { emptyEnvelope ResultKind.Text elapsedMs with
                DisplayText = Some(sprintf "%A" value) },
            None,
            None

    let project (value: obj) (elapsedMs: int64) : ResultEnvelope =
        let env, _, _ = projectWithCursor value elapsedMs
        env

    /// An uncaught script exception becomes a structured Error result —
    /// see Section 9 — never an unhandled worker crash.
    let projectError (ex: exn) (elapsedMs: int64) : ResultEnvelope =
        { emptyEnvelope ResultKind.Error elapsedMs with
            DisplayText = Some ex.Message }

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
