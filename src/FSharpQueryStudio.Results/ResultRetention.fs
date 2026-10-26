namespace FSharpQueryStudio.Results

open System.Collections.Concurrent

/// The full, materialized rows of a cell's connector-query or materialized-table
/// tail value, retained so **Export CSV** can write the whole result set — not
/// just the on-screen page.
type RetainedResult = { Columns: string list; Rows: obj array array }

/// Keyed by document URI: the most recent exportable grid for each notebook.
/// `Handlers.runEvaluate` calls `set` when `Projection.projectWithCursor`
/// returns a retained payload (a `ConnectorQueryResult`, or a materialized
/// generic table). A later non-table run (`printfn`, a scalar) leaves the
/// previous entry in place; `discard` removes it when the document closes.
/// Lives in the Worker process's own memory — never persisted to disk, gone
/// on process exit by design.
module ResultRetention =
    let private store = ConcurrentDictionary<string, RetainedResult>()

    let set (documentUri: string) (result: RetainedResult) : unit = store.[documentUri] <- result

    let tryGet (documentUri: string) : RetainedResult option =
        match store.TryGetValue(documentUri) with
        | true, result -> Some result
        | false, _ -> None

    let discard (documentUri: string) : unit = store.TryRemove(documentUri) |> ignore
