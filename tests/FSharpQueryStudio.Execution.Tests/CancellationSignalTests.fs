module CancellationSignalTests

open Xunit
open FSharpQueryStudio.Execution

[<Fact>]
let ``requestCancel sets isCancelled to true`` () =
    CancellationSignal.reset ()
    Assert.False(CancellationSignal.isCancelled ())
    CancellationSignal.requestCancel ()
    Assert.True(CancellationSignal.isCancelled ())

[<Fact>]
let ``reset clears a previously requested cancellation`` () =
    CancellationSignal.requestCancel ()
    Assert.True(CancellationSignal.isCancelled ())
    CancellationSignal.reset ()
    Assert.False(CancellationSignal.isCancelled ())
