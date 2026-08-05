namespace FSharpQueryStudio.Ui.Tests

open System
open FSharpQueryStudio.Ui

/// A virtual clock, not a real timer: `Advance` moves simulated time
/// forward and fires the single pending action if its due time has been
/// reached. Every `Schedule` call cancels whatever was previously pending,
/// matching `Debouncer`'s own cancel-on-reschedule contract - there is
/// never more than one pending action at a time.
type FakeDebounceScheduler() =
    let mutable now = TimeSpan.Zero
    let mutable pending: (TimeSpan * (unit -> unit)) option = None

    interface IDebounceScheduler with
        member _.Schedule(delay, action) =
            pending <- Some(now + delay, action)

            { new IDisposable with
                member _.Dispose() = pending <- None }

    member _.Advance(by: TimeSpan) =
        now <- now + by

        match pending with
        | Some(dueTime, action) when dueTime <= now ->
            pending <- None
            action ()
        | _ -> ()
