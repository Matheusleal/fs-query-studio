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
