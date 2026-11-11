namespace FSharpQueryStudio.Results

open System
open System.Collections

/// A resumable cursor over a live `IEnumerator` — Section 7's
/// `results/page`. Unlike `Seq.skip`/`Seq.truncate`, this never restarts
/// enumeration from scratch: it holds the same enumerator across calls,
/// which matters for a connector-backed sequence where "starting over"
/// would mean silently re-running the underlying query.
///
/// Implements `IDisposable` so when pages are completed, superseded, or the
/// session is reset, underlying file streams, readers, and database connections
/// are promptly closed and released rather than locked until garbage collection.
type PagingCursor(enumerator: IEnumerator) =
    let mutable peeked: obj option = None
    let mutable isDisposed = false

    let disposeEnumerator () =
        if not isDisposed then
            isDisposed <- true
            peeked <- None
            match enumerator with
            | :? IDisposable as d ->
                try d.Dispose() with _ -> ()
            | _ -> ()

    /// Returns up to `pageSize` items and whether at least one more
    /// remains beyond them. Automatically disposes the enumerator when
    /// the end of the sequence is reached.
    member _.TakePage(pageSize: int) : obj array * bool =
        if isDisposed then
            [||], false
        else
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
            else
                disposeEnumerator ()

            items.ToArray(), hasMore

    member _.Dispose() : unit = disposeEnumerator ()

    interface IDisposable with
        member this.Dispose() = this.Dispose()
