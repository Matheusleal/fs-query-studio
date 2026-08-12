namespace FSharpQueryStudio.Results

open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors
open Microsoft.FSharp.Reflection
open System.Collections
open System.Collections.Generic
open System.Reflection

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
module Results =

    type private Explicit =
        | Table of Column list * obj array array
        | Text of string

    let mutable private explicit: Explicit option = None

    /// Called once by the worker before every evaluation (Section 9) —
    /// clears whatever a previous run (or, in Persistent mode, an
    /// earlier turn of the same session) explicitly pushed here, so it
    /// never leaks into an evaluation that didn't call `table`/`text`
    /// itself.
    let reset () = explicit <- None

    /// Fully materializes `query.Rows` — a deliberate memory trade-off:
    /// every `Results.raw`-produced result now sits entirely in the
    /// Worker's memory for as long as its tab stays open (via
    /// `ResultRetention`, committed by `runEvaluate`), where before only
    /// `Projection.PreviewRowLimit + 1` rows were ever even read off the
    /// connector's stream. See docs/superpowers/specs/2026-08-12-results-export-design.md.
    let raw (query: ConnectorQueryResult) : ConnectorQueryResult =
        let columns =
            query.Columns |> List.map (fun name -> { Name = name; DisplayType = "obj" })

        let allRows = query.Rows |> Array.ofSeq

        explicit <- Some(Table(columns, allRows))
        query

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

            let allFlags = BindingFlags.Public ||| BindingFlags.NonPublic

            match first with
            | :? ConnectorQueryResult ->
                failwith "Results.table does not accept ConnectorQueryResult — use Results.raw."
            | _ when first <> null && FSharpType.IsRecord(first.GetType(), allFlags) ->
                let recordType = first.GetType()

                let fieldNames =
                    FSharpType.GetRecordFields(recordType, allFlags)
                    |> Array.map (fun p -> p.Name)
                    |> Array.toList

                let headers = resolveHeaders fieldNames columnNames

                let dataRows =
                    rows
                    |> Array.map (fun r -> FSharpValue.GetRecordFields(r, allFlags))

                (headers |> List.map (fun n -> { Name = n; DisplayType = "obj" })), dataRows
            | _ when first <> null && FSharpType.IsTuple(first.GetType()) ->
                match columnNames with
                | None -> failwith "Results.table requires columnNames for tuple sequences."
                | Some names ->
                    let dataRows =
                        rows |> Array.map (fun r -> FSharpValue.GetTupleFields(r))

                    let width = if dataRows.Length = 0 then names.Length else dataRows.[0].Length
                    let headers = resolveHeaders (List.replicate width "") (Some names)
                    (headers |> List.map (fun n -> { Name = n; DisplayType = "obj" })), dataRows
            | _ when first <> null && isDictionary (first.GetType()) ->
                // Handle any dictionary-like type: Map<K,V>, Dictionary<K,V>, IDictionary, etc.
                // via reflection to extract keys and values regardless of type parameters
                let keysInFirstSeenOrder =
                    let seen = HashSet<string>()
                    let ordered = ResizeArray<string>()

                    for row in rows do
                        let keys = getDictionaryKeys row
                        for key in keys do
                            if seen.Add(key) then
                                ordered.Add(key)

                    ordered |> List.ofSeq

                let headers = resolveHeaders keysInFirstSeenOrder columnNames

                let dataRows =
                    rows
                    |> Array.map (fun row ->
                        keysInFirstSeenOrder
                        |> List.map (fun k ->
                            let value = getDictionaryValue row k
                            if value <> null then value else null)
                        |> Array.ofList)

                (headers |> List.map (fun n -> { Name = n; DisplayType = "obj" })), dataRows
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
            | _ -> failwith "Results.table: unrecognized element shape."

    /// New generalized entry point — accepts any collection (records,
    /// tuples, nested collections, Map/IDictionary, or flat scalars) and
    /// renders it as a real table, inferring columns from the data's own
    /// shape wherever possible. `columnNames`, when given, always wins as
    /// the final headers, applied positionally. See
    /// docs/superpowers/specs/2026-08-12-results-table-generalization-design.md.
    let table (data: obj) (columnNames: string list option) : obj =
        match data with
        | :? ConnectorQueryResult ->
            failwith "Results.table does not accept ConnectorQueryResult — use Results.raw."
        | :? string ->
            failwith "Results.table expects a collection; got a single value — wrap it, e.g. [value]."
        | :? IEnumerable as enumerable ->
            let materialized = enumerable |> Seq.cast<obj> |> Array.ofSeq
            let columns, dataRows = dispatchRows materialized columnNames
            explicit <- Some(Table(columns, dataRows))
            data
        | _ ->
            failwith "Results.table expects a collection; got a single value — wrap it, e.g. [value]."

    /// Explicitly shows `value`'s structured-printed form (F#'s own `%A`,
    /// the same formatting FSI's own REPL echo uses) as text in the
    /// Results pane — useful for anything that isn't a
    /// `ConnectorQueryResult`, or as an explicit override for a value
    /// that isn't the script's own last expression.
    let text (value: 'a) : 'a =
        explicit <- Some(Text(sprintf "%A" value))
        value

    /// The worker's own integration point — not part of the script-facing
    /// surface. Builds the full `ResultEnvelope` for whatever was
    /// explicitly pushed this evaluation, if anything. `None` means
    /// nothing was pushed; the caller falls back to FSI's own
    /// tail-value projection.
    let currentEnvelope (elapsedMs: int64) (output: OutputLine list) : ResultEnvelope option =
        explicit
        |> Option.map (fun e ->
            match e with
            | Table(columns, rows) ->
                { Kind = ResultKind.Table
                  DisplayText = None
                  Schema = columns
                  Rows = rows
                  IsTruncated = false
                  ContinuationToken = None
                  ElapsedMs = elapsedMs
                  Output = output }
            | Text text ->
                { Kind = ResultKind.Text
                  DisplayText = Some text
                  Schema = []
                  Rows = [||]
                  IsTruncated = false
                  ContinuationToken = None
                  ElapsedMs = elapsedMs
                  Output = output })
