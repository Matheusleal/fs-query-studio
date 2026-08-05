namespace FSharpQueryStudio.Results

open System
open System.Collections
open System.Reflection
open Microsoft.FSharp.Reflection
open FSharpQueryStudio.Protocol

/// Converts a raw FSI value into the bounded, protocol-neutral
/// `ResultEnvelope` shape from Section 11. Scoped to `Scalar`/`Text`/
/// `Table` for the vertical slice — `Object`/`Error`/`NoValue` land with
/// their consumers in later tasks.
module Projection =

    [<Literal>]
    let PreviewRowLimit = 500

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

    /// Bounds a sequence to at most `PreviewRowLimit` items, reporting
    /// whether it had to cut anything off. Enumerates only once.
    let private boundedRows (items: IEnumerable) : obj array * bool =
        let taken =
            items |> Seq.cast<obj> |> Seq.truncate (PreviewRowLimit + 1) |> Seq.toArray

        if taken.Length > PreviewRowLimit then
            taken.[.. PreviewRowLimit - 1], true
        else
            taken, false

    let project (value: obj) (elapsedMs: int64) : ResultEnvelope =
        match value with
        | null -> { emptyEnvelope ResultKind.Text elapsedMs with DisplayText = Some "null" }
        | :? string as s -> { emptyEnvelope ResultKind.Text elapsedMs with DisplayText = Some s }
        | v when isSimplePrimitive (v.GetType()) ->
            { emptyEnvelope ResultKind.Scalar elapsedMs with
                DisplayText = Some(string v) }
        | v when FSharpType.IsRecord(v.GetType(), recordBindingFlags) ->
            let t = v.GetType()

            { emptyEnvelope ResultKind.Table elapsedMs with
                Schema = recordSchema t
                Rows = [| recordRow t v |] }
        | :? IEnumerable as items ->
            let rows, truncated = boundedRows items

            { emptyEnvelope ResultKind.Table elapsedMs with
                Schema = [ { Name = "Value"; DisplayType = "obj" } ]
                Rows = rows |> Array.map (fun r -> [| r |])
                IsTruncated = truncated }
        | v -> { emptyEnvelope ResultKind.Text elapsedMs with DisplayText = Some(string v) }

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
