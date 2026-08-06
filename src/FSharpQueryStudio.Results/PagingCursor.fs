namespace FSharpQueryStudio.Results

open System.Collections

/// A resumable cursor over a live `IEnumerator` — Section 7's
/// `results/page`. Unlike `Seq.skip`/`Seq.truncate`, this never restarts
/// enumeration from scratch: it holds the same enumerator across calls,
/// which matters for a connector-backed sequence where "starting over"
/// would mean silently re-running the underlying query.
///
/// Peeks one item past every page to answer "is there more" without the
/// caller needing an extra round trip, then hands that peeked item back
/// first on the next `TakePage` call instead of dropping it.
type PagingCursor(enumerator: IEnumerator) =
    let mutable peeked: obj option = None

    /// Returns up to `pageSize` items and whether at least one more
    /// remains beyond them.
    member _.TakePage(pageSize: int) : obj array * bool =
        let items = ResizeArray<obj>()

        match peeked with
        | Some item when pageSize > 0 ->
            items.Add(item)
            peeked <- None
        | _ -> ()

        while items.Count < pageSize && enumerator.MoveNext() do
            items.Add(enumerator.Current)

        let hasMore = enumerator.MoveNext()

        if hasMore then
            peeked <- Some enumerator.Current

        items.ToArray(), hasMore
