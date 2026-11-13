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
    /// docs/archive/specs/2026-09-08-notebook-tail-value-results-design.md.
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

    let private optionValuePropCache = ConcurrentDictionary<Type, PropertyInfo option>()

    let rec private sanitizeCell (v: obj) : obj =
        if isNull v || Convert.IsDBNull(v) then
            null
        else
            match v with
            | :? double as d -> if Double.IsFinite d then v else null
            | :? single as f -> if Single.IsFinite f then v else null
            | _ ->
                let t = v.GetType()
                if t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<option<_>> then
                    let propOpt =
                        optionValuePropCache.GetOrAdd(
                            t,
                            fun typ ->
                                let p = typ.GetProperty("Value", recordBindingFlags)
                                if isNull p then None else Some p
                        )
                    match propOpt with
                    | Some p -> sanitizeCell (p.GetValue(v))
                    | None -> v
                else
                    v

    let private recordReaderCache = ConcurrentDictionary<Type, obj -> obj array>()

    let private getRecordReader (t: Type) : (obj -> obj array) =
        recordReaderCache.GetOrAdd(
            t,
            Func<Type, obj -> obj array>(fun typ ->
                let rawReader = FSharpValue.PreComputeRecordReader(typ, recordBindingFlags)
                let fields = FSharpType.GetRecordFields(typ, recordBindingFlags)
                let needsSanitize =
                    fields
                    |> Array.exists (fun f ->
                        let pt = f.PropertyType
                        pt = typeof<double>
                        || pt = typeof<single>
                        || pt = typeof<obj>
                        || (pt.IsGenericType && pt.GetGenericTypeDefinition() = typedefof<option<_>>))
                if not needsSanitize then
                    rawReader
                else
                    fun (instance: obj) ->
                        let arr = rawReader instance
                        for i = 0 to arr.Length - 1 do
                            arr.[i] <- sanitizeCell arr.[i]
                        arr)
        )

    let private tupleReaderCache = ConcurrentDictionary<Type, obj -> obj array>()

    let private getTupleReader (t: Type) : (obj -> obj array) =
        tupleReaderCache.GetOrAdd(
            t,
            Func<Type, obj -> obj array>(fun typ ->
                let rawReader = FSharpValue.PreComputeTupleReader(typ)
                fun (instance: obj) ->
                    let arr = rawReader instance
                    for i = 0 to arr.Length - 1 do
                        arr.[i] <- sanitizeCell arr.[i]
                    arr)
        )

    let private recordRow (t: Type) (value: obj) : obj array =
        (getRecordReader t) value

    /// Global cache to preserve declared anonymous record property orders across notebook cells.
    let private anonymousRecordFieldSetCache = ConcurrentDictionary<Set<string>, string list>()

    /// Fast lexical scanner that extracts all `{| ... |}` blocks from F# source code
    /// and returns the property names declared at depth 0 of each block in order.
    let extractAnonymousRecordBlocks (source: string) : string list list =
        if String.IsNullOrWhiteSpace(source) then []
        else
            let len = source.Length
            let blocks = ResizeArray<string list>()

            let isKeyword (word: string) =
                match word with
                | "let" | "let!" | "use" | "use!" | "do" | "do!"
                | "if" | "then" | "else" | "elif"
                | "match" | "with" | "fun" | "function"
                | "type" | "val" | "member" | "static" | "override" | "default"
                | "open" | "module" | "namespace"
                | "for" | "in" | "while" | "to" | "downto"
                | "try" | "finally" | "yield" | "yield!" | "return" | "return!"
                | "select" | "where" | "join" | "on" | "into" | "groupBy" | "sortBy"
                | "true" | "false" | "null" | "struct" | "base" -> true
                | _ -> false

            let isIdentStart (c: char) =
                System.Char.IsLetter(c) || c = '_'

            let isIdentChar (c: char) =
                System.Char.IsLetterOrDigit(c) || c = '_' || c = '\''

            let mutable i = 0

            let skipLineComment () =
                while i < len && source.[i] <> '\n' do
                    i <- i + 1

            let skipBlockComment () =
                let mutable depth = 1
                i <- i + 2
                while i < len && depth > 0 do
                    if i + 1 < len && source.[i] = '(' && source.[i + 1] = '*' then
                        depth <- depth + 1
                        i <- i + 2
                    elif i + 1 < len && source.[i] = '*' && source.[i + 1] = ')' then
                        depth <- depth - 1
                        i <- i + 2
                    else
                        i <- i + 1

            let skipTripleQuoteString () =
                i <- i + 3
                while i + 2 < len && not (source.[i] = '"' && source.[i + 1] = '"' && source.[i + 2] = '"') do
                    i <- i + 1
                if i + 2 < len then i <- i + 3

            let skipRegularString () =
                i <- i + 1
                while i < len && source.[i] <> '"' do
                    if source.[i] = '\\' && i + 1 < len then
                        i <- i + 2
                    else
                        i <- i + 1
                if i < len then i <- i + 1

            let skipVerbatimString () =
                i <- i + 2
                while i < len do
                    if source.[i] = '"' then
                        if i + 1 < len && source.[i + 1] = '"' then
                            i <- i + 2
                        else
                            i <- i + 1
                    else
                        i <- i + 1

            let recordStack = ResizeArray<ResizeArray<string>>()
            let mutable parenDepth = 0
            let mutable bracketDepth = 0
            let mutable braceDepth = 0
            let mutable canStartProp = false

            while i < len do
                if i + 1 < len && source.[i] = '/' && source.[i + 1] = '/' then
                    skipLineComment ()
                    if recordStack.Count > 0 && parenDepth = 0 && bracketDepth = 0 && braceDepth = 0 then
                        canStartProp <- true
                elif i + 1 < len && source.[i] = '(' && source.[i + 1] = '*' then
                    skipBlockComment ()
                elif i + 2 < len && source.[i] = '"' && source.[i + 1] = '"' && source.[i + 2] = '"' then
                    skipTripleQuoteString ()
                elif i + 1 < len && source.[i] = '@' && source.[i + 1] = '"' then
                    skipVerbatimString ()
                elif source.[i] = '"' then
                    skipRegularString ()
                elif i + 1 < len && source.[i] = '{' && source.[i + 1] = '|' then
                    recordStack.Add(ResizeArray<string>())
                    canStartProp <- true
                    i <- i + 2
                elif i + 1 < len && source.[i] = '|' && source.[i + 1] = '}' then
                    if recordStack.Count > 0 then
                        let completed = recordStack.[recordStack.Count - 1]
                        recordStack.RemoveAt(recordStack.Count - 1)
                        if completed.Count > 0 then
                            blocks.Add(completed |> Seq.toList)
                    canStartProp <- false
                    i <- i + 2
                elif recordStack.Count > 0 then
                    if source.[i] = '(' then
                        parenDepth <- parenDepth + 1
                        i <- i + 1
                    elif source.[i] = ')' then
                        if parenDepth > 0 then parenDepth <- parenDepth - 1
                        i <- i + 1
                    elif source.[i] = '[' then
                        bracketDepth <- bracketDepth + 1
                        i <- i + 1
                    elif source.[i] = ']' then
                        if bracketDepth > 0 then bracketDepth <- bracketDepth - 1
                        i <- i + 1
                    elif source.[i] = '{' then
                        braceDepth <- braceDepth + 1
                        i <- i + 1
                    elif source.[i] = '}' then
                        if braceDepth > 0 then braceDepth <- braceDepth - 1
                        i <- i + 1
                    elif source.[i] = '\n' || source.[i] = ';' || source.[i] = ',' then
                        if parenDepth = 0 && bracketDepth = 0 && braceDepth = 0 then
                            canStartProp <- true
                        i <- i + 1
                    elif System.Char.IsWhiteSpace(source.[i]) then
                        i <- i + 1
                    else
                        if parenDepth = 0 && bracketDepth = 0 && braceDepth = 0 then
                            if i + 1 < len && source.[i] = '`' && source.[i + 1] = '`' then
                                let startIdx = i + 2
                                let endIdx = source.IndexOf("``", startIdx, StringComparison.Ordinal)
                                if endIdx > 0 then
                                    let name = source.Substring(startIdx, endIdx - startIdx)
                                    i <- endIdx + 2
                                    let saveI = i
                                    while i < len && System.Char.IsWhiteSpace(source.[i]) && source.[i] <> '\n' do
                                        i <- i + 1
                                    if canStartProp && i < len && source.[i] = '=' && (i + 1 = len || (source.[i + 1] <> '=' && source.[i + 1] <> '>')) then
                                        recordStack.[recordStack.Count - 1].Add(name)
                                        canStartProp <- false
                                        i <- i + 1
                                    else
                                        i <- saveI
                                        canStartProp <- false
                                else
                                    i <- i + 2
                            elif isIdentStart source.[i] then
                                let startIdx = i
                                while i < len && isIdentChar source.[i] do
                                    i <- i + 1
                                let ident = source.Substring(startIdx, i - startIdx)
                                if isKeyword ident then
                                    canStartProp <- false
                                elif canStartProp then
                                    let saveI = i
                                    while i < len && System.Char.IsWhiteSpace(source.[i]) && source.[i] <> '\n' do
                                        i <- i + 1
                                    if i < len && source.[i] = '=' && (i + 1 = len || (source.[i + 1] <> '=' && source.[i + 1] <> '>')) then
                                        recordStack.[recordStack.Count - 1].Add(ident)
                                        canStartProp <- false
                                        i <- i + 1
                                    else
                                        i <- saveI
                                        canStartProp <- false
                                else
                                    ()
                            else
                                canStartProp <- false
                                i <- i + 1
                        else
                            i <- i + 1
                else
                    i <- i + 1

            blocks |> Seq.toList

    let tryFindMatchingPropertyOrder (blocks: string list list) (targetFields: Set<string>) : string list option =
        if targetFields.IsEmpty || blocks.IsEmpty then None
        else
            let exactMatch =
                blocks
                |> List.rev
                |> List.tryFind (fun block ->
                    let bSet = Set.ofList block
                    bSet.Count = targetFields.Count && Set.forall targetFields.Contains bSet)
            match exactMatch with
            | Some block -> Some block
            | None ->
                let partialMatch =
                    blocks
                    |> List.rev
                    |> List.tryFind (fun block ->
                        let bSet = Set.ofList block
                        Set.forall (fun f -> bSet.Contains f) targetFields)
                match partialMatch with
                | Some block ->
                    let ordered = block |> List.filter targetFields.Contains
                    Some ordered
                | None -> None

    let registerSourceCode (source: string) : unit =
        if not (String.IsNullOrWhiteSpace source) then
            try
                let blocks = extractAnonymousRecordBlocks source
                for b in blocks do
                    let bSet = Set.ofList b
                    if bSet.Count = b.Length && b.Length > 0 then
                        anonymousRecordFieldSetCache.[bSet] <- b
            with _ -> ()

    let tryGetOrExtractRecordOrder (t: Type) (sourceCodeOpt: string option) : string list option =
        let fields =
            FSharpType.GetRecordFields(t, recordBindingFlags)
            |> Array.map (fun p -> p.Name)
            |> Set.ofArray

        let mutable found = Unchecked.defaultof<string list>
        if anonymousRecordFieldSetCache.TryGetValue(fields, &found) then
            Some found
        else
            match sourceCodeOpt with
            | Some src when not (String.IsNullOrWhiteSpace src) ->
                let blocks = extractAnonymousRecordBlocks src
                for b in blocks do
                    let bSet = Set.ofList b
                    if bSet.Count = b.Length && b.Length > 0 then
                        anonymousRecordFieldSetCache.[bSet] <- b

                match tryFindMatchingPropertyOrder blocks fields with
                | Some order ->
                    anonymousRecordFieldSetCache.[fields] <- order
                    Some order
                | None ->
                    if anonymousRecordFieldSetCache.TryGetValue(fields, &found) then
                        Some found
                    else
                        None
            | _ -> None

    let private reorderTable (columns: Column list) (dataRows: obj array array) (preferredOrder: string list) : Column list * obj array array =
        let colMap =
            columns
            |> List.indexed
            |> List.map (fun (i, c) -> (c.Name, (i, c)))
            |> dict

        let seenIndices = HashSet<int>()
        let reorderedCols = ResizeArray<Column>()
        let indexMapping = ResizeArray<int>()

        for name in preferredOrder do
            let mutable found = Unchecked.defaultof<int * Column>
            let matched =
                if colMap.TryGetValue(name, &found) then
                    Some found
                else
                    columns
                    |> List.indexed
                    |> List.tryFind (fun (_, c) -> String.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
            match matched with
            | Some (oldIdx, col) ->
                if seenIndices.Add(oldIdx) then
                    reorderedCols.Add(col)
                    indexMapping.Add(oldIdx)
            | None -> ()

        columns
        |> List.iteri (fun oldIdx col ->
            if seenIndices.Add(oldIdx) then
                reorderedCols.Add(col)
                indexMapping.Add(oldIdx))

        if indexMapping.Count = columns.Length && columns.Length > 0 then
            let newCols = reorderedCols |> Seq.toList
            let mapArr = indexMapping.ToArray()
            let newRows =
                dataRows
                |> Array.map (fun row ->
                    let newRow = Array.zeroCreate mapArr.Length
                    for newIdx in 0 .. mapArr.Length - 1 do
                        let oldIdx = mapArr.[newIdx]
                        newRow.[newIdx] <- if oldIdx < row.Length then row.[oldIdx] else null
                    newRow)
            newCols, newRows
        else
            columns, dataRows

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
    let projectWithCursorDetailed (value: obj) (elapsedMs: int64) (sourceCode: string option) (preferredColumnOrder: string list option) : ResultEnvelope * PagingCursor option * RetainedTable option =
        // Shared builder for the four materialized shapes (record / tuple /
        // dictionary / nested): the preview envelope is truncated to
        // `PreviewRowLimit`, but the full row set is always retained for
        // Export CSV, and the cursor is `None` (nothing to stream).
        let materializedTable (columns: Column list) (dataRows: obj array array) (preferredOrder: string list option) : ResultEnvelope * PagingCursor option * RetainedTable option =
            let cols, rows =
                match preferredOrder with
                | Some order when not order.IsEmpty -> reorderTable columns dataRows order
                | _ -> columns, dataRows

            // Every materialized shape is made rectangular here, by
            // construction: a row matching the schema width passes through,
            // a longer one is trimmed, a shorter one is null-padded. The
            // tuple / dict / record arms feed rows that are already the
            // right width; the nested arm's rows are genuinely ragged.
            let width = cols.Length
            let padOrTruncate (row: obj array) : obj array =
                if row.Length = width then row
                elif row.Length > width then row.[0 .. width - 1]
                else Array.append row (Array.create (width - row.Length) null)

            let rectangular = rows |> Array.map padOrTruncate
            let preview = rectangular |> Array.truncate PreviewRowLimit
            let envelope =
                { emptyEnvelope ResultKind.Table elapsedMs with
                    Schema = cols
                    Rows = preview
                    IsTruncated = rectangular.Length > PreviewRowLimit }
            envelope, None, Some { Columns = cols |> List.map (fun c -> c.Name); Rows = rectangular }

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
                let pref = preferredColumnOrder |> Option.orElseWith (fun () -> tryGetOrExtractRecordOrder t sourceCode)
                materializedTable (recordSchema t) [| recordRow t v |] pref
            | :? IEnumerable as items when not (value :? string) ->
                // Peek only element 0. A flat scalar `seq` (possibly large
                // or infinite) must stay on the lazy `PagingCursor` path —
                // realizing it up front would hang/OOM the worker before
                // the streaming arm is reached, and `try/with` can't
                // rescue a non-terminating enumeration. Only the
                // materialized shapes below pull the whole sequence.
                let e = items.GetEnumerator()
                let disposeEnumerator () =
                    match box e with
                    | :? IDisposable as d -> try d.Dispose() with _ -> ()
                    | _ -> ()

                if not (e.MoveNext()) then
                    disposeEnumerator ()
                    // A cell ending in `[]` must not overwrite the notebook's
                    // exportable grid with an empty table — return the empty
                    // Table envelope but retain nothing.
                    emptyEnvelope ResultKind.Table elapsedMs, None, None
                else
                    let first = e.Current
                    // The rest of the sequence, still lazy, with element 0 re-prepended.
                    let full =
                        seq {
                            try
                                yield first
                                while e.MoveNext() do
                                    yield e.Current
                            finally
                                disposeEnumerator ()
                        }

                    match first with
                    | :? ConnectorQueryResult ->
                        disposeEnumerator ()
                        failwith "a sequence of ConnectorQueryResult is not supported; enumerate .Rows"
                    | _ when not (isNull first) && FSharpType.IsRecord(first.GetType(), recordBindingFlags) ->
                        let recordType = first.GetType()
                        let pref = preferredColumnOrder |> Option.orElseWith (fun () -> tryGetOrExtractRecordOrder recordType sourceCode)
                        let reader = getRecordReader recordType
                        let dataRows = ResizeArray<obj array>()
                        dataRows.Add(reader first)
                        let mutable keepReading = true
                        try
                            while keepReading do
                                let hasNext =
                                    try
                                        e.MoveNext()
                                    with _ ->
                                        false
                                if not hasNext then
                                    keepReading <- false
                                else
                                    let curr = e.Current
                                    if isNull curr then
                                        ()
                                    elif curr.GetType() <> recordType then
                                        failwith "Mixed-shape sequence cannot be projected as a uniform record table."
                                    else
                                        dataRows.Add(reader curr)
                        finally
                            disposeEnumerator ()

                        materializedTable (recordSchema recordType) (dataRows.ToArray()) pref
                    | _ when not (isNull first) && FSharpType.IsTuple(first.GetType()) ->
                        let tupleType = first.GetType()
                        let reader = getTupleReader tupleType
                        let firstFields = reader first
                        let width = firstFields.Length
                        let names =
                            [ for i in 1..width -> { Name = sprintf "Item%d" i; DisplayType = "obj" } ]
                        let dataRows = ResizeArray<obj array>()
                        dataRows.Add(firstFields)
                        try
                            while e.MoveNext() do
                                let curr = e.Current
                                if isNull curr || curr.GetType() <> tupleType then
                                    failwith "Mixed-shape sequence cannot be projected as a uniform tuple table."
                                dataRows.Add(reader curr)
                        finally
                            disposeEnumerator ()
                        materializedTable names (dataRows.ToArray()) None
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

                        materializedTable columns dataRows None
                    | :? IEnumerable when not (first :? string) ->
                        let rows = full |> Array.ofSeq

                        let rawRows =
                            rows
                            |> Array.map (fun r -> (r :?> IEnumerable) |> Seq.cast<obj> |> Array.ofSeq)

                        let headers = rawRows.[0] |> Array.map string |> Array.toList
                        let columns = headers |> List.map (fun n -> { Name = n; DisplayType = "obj" })
                        // Rows 1.. can be ragged — `materializedTable` squares
                        // them to the header width.
                        materializedTable columns rawRows.[1..] None
                    | _ ->
                        // scalar / string sequence: keep the lazy streaming
                        // path — feed the *lazy* `full` enumerator to
                        // `PagingCursor`, so only ~PreviewRowLimit+1
                        // elements are ever pulled.
                        let cursor = new PagingCursor(full.GetEnumerator())
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

    let projectWithCursor (value: obj) (elapsedMs: int64) : ResultEnvelope * PagingCursor option * RetainedTable option =
        projectWithCursorDetailed value elapsedMs None None

    let project (value: obj) (elapsedMs: int64) : ResultEnvelope =
        let env, _, _ = projectWithCursor value elapsedMs
        env

    let rec unwrapException (ex: exn) : exn =
        match ex with
        | :? System.Reflection.TargetInvocationException as tie when not (isNull tie.InnerException) ->
            unwrapException tie.InnerException
        | :? System.AggregateException as ae when not (isNull ae.InnerException) ->
            unwrapException ae.InnerException
        | other -> other

    /// An uncaught script exception becomes a structured Error result —
    /// see Section 9 — never an unhandled worker crash.
    let projectError (ex: exn) (elapsedMs: int64) : ResultEnvelope =
        let unwrapped = unwrapException ex
        let msg =
            if not (isNull unwrapped.InnerException) && not (String.IsNullOrWhiteSpace unwrapped.InnerException.Message) then
                $"{unwrapped.Message}\nCause: {unwrapped.InnerException.Message}"
            else
                unwrapped.Message
        { emptyEnvelope ResultKind.Error elapsedMs with
            DisplayText = Some msg }

    let projectErrorWithDetails (ex: exn) (details: string option) (elapsedMs: int64) : ResultEnvelope =
        let unwrapped = unwrapException ex
        let msg =
            match details with
            | Some d when not (String.IsNullOrWhiteSpace d) && (ex.Message.Contains("Operation could not be completed") || unwrapped.Message.Contains("Operation could not be completed")) -> d
            | Some d when not (String.IsNullOrWhiteSpace d) -> $"{unwrapped.Message}\n\nDetails:\n{d}"
            | _ ->
                if not (isNull unwrapped.InnerException) && not (String.IsNullOrWhiteSpace unwrapped.InnerException.Message) then
                    $"{unwrapped.Message}\nCause: {unwrapped.InnerException.Message}"
                else
                    unwrapped.Message
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
