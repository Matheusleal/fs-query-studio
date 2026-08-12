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
