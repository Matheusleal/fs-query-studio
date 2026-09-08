namespace FSharpQueryStudio.Results

open System
open System.Collections
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

    /// Same shape as `project`, plus the live `PagingCursor` a truncated
    /// Table result was cut off at — Section 7's `results/page`. `None`
    /// whenever nothing was truncated (every other kind, or a sequence
    /// that already fit within `PreviewRowLimit`). The cursor holds the
    /// same enumerator `project` would have discarded, so a caller that
    /// wants to keep paging never re-runs whatever produced `value`.
    let projectWithCursor (value: obj) (elapsedMs: int64) : ResultEnvelope * PagingCursor option * RetainedTable option =
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

            { emptyEnvelope ResultKind.Table elapsedMs with
                Schema = recordSchema t
                Rows = [| recordRow t v |] },
            None,
            None
        | :? IEnumerable as items ->
            let cursor = PagingCursor(items.GetEnumerator())
            let rows, hasMore = cursor.TakePage(PreviewRowLimit)

            let envelope =
                { emptyEnvelope ResultKind.Table elapsedMs with
                    Schema = [ { Name = "Value"; DisplayType = "obj" } ]
                    Rows = rows |> Array.map (fun r -> [| r |])
                    IsTruncated = hasMore }

            envelope, (if hasMore then Some cursor else None), None
        | v -> { emptyEnvelope ResultKind.Text elapsedMs with DisplayText = Some(string v) }, None, None

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
