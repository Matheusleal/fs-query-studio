module DebouncerTests

open System
open Xunit
open FSharpQueryStudio.Ui
open FSharpQueryStudio.Ui.Tests

[<Fact>]
let ``rapid successive notifications produce exactly one action after the debounce window`` () =
    let scheduler = FakeDebounceScheduler()
    let debouncer = Debouncer(TimeSpan.FromMilliseconds(400.0), scheduler)
    let mutable fireCount = 0
    let notify () = debouncer.Notify(fun () -> fireCount <- fireCount + 1)

    notify ()
    scheduler.Advance(TimeSpan.FromMilliseconds(100.0))
    notify ()
    scheduler.Advance(TimeSpan.FromMilliseconds(100.0))
    notify ()
    scheduler.Advance(TimeSpan.FromMilliseconds(400.0))

    Assert.Equal(1, fireCount)

[<Fact>]
let ``a notification that never reaches the full debounce window never fires`` () =
    let scheduler = FakeDebounceScheduler()
    let debouncer = Debouncer(TimeSpan.FromMilliseconds(400.0), scheduler)
    let mutable fireCount = 0

    debouncer.Notify(fun () -> fireCount <- fireCount + 1)
    scheduler.Advance(TimeSpan.FromMilliseconds(399.0))

    Assert.Equal(0, fireCount)
