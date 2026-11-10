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
    let private store = ConcurrentDictionary<string, RetainedResult>(System.StringComparer.OrdinalIgnoreCase)

    let private normalizeKey (uri: string) =
        if System.String.IsNullOrEmpty uri then ""
        else
            let unescaped = System.Uri.UnescapeDataString(uri).Replace('\\', '/')
            unescaped.ToLowerInvariant()

    let set (documentUri: string) (result: RetainedResult) : unit =
        store.[documentUri] <- result
        let norm = normalizeKey documentUri
        if not (System.String.IsNullOrEmpty norm) then
            store.[norm] <- result

    let tryGet (documentUri: string) : RetainedResult option =
        match store.TryGetValue(documentUri) with
        | true, result -> Some result
        | false, _ ->
            let norm = normalizeKey documentUri
            match store.TryGetValue(norm) with
            | true, result -> Some result
            | false, _ -> None

    let discard (documentUri: string) : unit =
        let norm = normalizeKey documentUri
        store.TryRemove(documentUri) |> ignore
        if not (System.String.IsNullOrEmpty norm) then
            store.TryRemove(norm) |> ignore
            for kv in store.ToArray() do
                if normalizeKey kv.Key = norm then
                    store.TryRemove(kv.Key) |> ignore
