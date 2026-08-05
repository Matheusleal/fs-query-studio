namespace FSharpQueryStudio.Ui

open System
open System.Threading

/// Abstraction over "run this after a delay, cancelling any previous
/// pending run" — see Section 8's analysis pipeline, step 2 (debounce
/// source-change requests 300-500ms before sending `analysis/check`).
/// Tests inject a fake implementation driven by a virtual clock instead of
/// waiting on a real timer.
type IDebounceScheduler =
    abstract Schedule: delay: TimeSpan * action: (unit -> unit) -> IDisposable

/// Production scheduler backed by a real `System.Threading.Timer`.
type RealDebounceScheduler() =
    interface IDebounceScheduler with
        member _.Schedule(delay, action) =
            let timer = new Timer((fun _ -> action ()), null, delay, Timeout.InfiniteTimeSpan)
            timer :> IDisposable

/// Cancels any previously-scheduled action on every `Notify` call, so only
/// the last notification within `delay` of the next one ever actually
/// runs — rapid successive keystrokes collapse into a single fire.
type Debouncer(delay: TimeSpan, scheduler: IDebounceScheduler) =
    let mutable pending: IDisposable option = None

    member _.Notify(action: unit -> unit) =
        pending |> Option.iter (fun d -> d.Dispose())
        pending <- Some(scheduler.Schedule(delay, action))
