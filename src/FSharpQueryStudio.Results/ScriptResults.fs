namespace FSharpQueryStudio.Results

open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors

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
        | Table of Column list * obj array array * isTruncated: bool
        | Text of string

    let mutable private explicit: Explicit option = None

    /// Called once by the worker before every evaluation (Section 9) —
    /// clears whatever a previous run (or, in Persistent mode, an
    /// earlier turn of the same session) explicitly pushed here, so it
    /// never leaks into an evaluation that didn't call `table`/`text`
    /// itself.
    let reset () = explicit <- None

    /// Explicitly shows `query`'s rows in the Results pane as a table
    /// with its own real column names — `Connections.sql`/`csv`/`excel`'s
    /// shared `ConnectorQueryResult` shape (C.5-C.10). Bypasses generic
    /// result projection entirely, which has no way to know a raw
    /// `obj array seq`'s column names on its own — that's exactly the gap
    /// this closes. Bounded to the same `Projection.PreviewRowLimit` as
    /// every other Table result; a pushed table doesn't support paging
    /// past that bound (`results/page` pages the FSI-tail-value path
    /// only) — an accepted limitation, not a silent truncation surprise
    /// since `IsTruncated` is still set correctly.
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
            | Table(columns, rows, isTruncated) ->
                { Kind = ResultKind.Table
                  DisplayText = None
                  Schema = columns
                  Rows = rows
                  IsTruncated = isTruncated
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
