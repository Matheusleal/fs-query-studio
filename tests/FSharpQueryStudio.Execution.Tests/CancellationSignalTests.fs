module CancellationSignalTests

open System
open System.IO
open Xunit
open FSharpQueryStudio.Execution

let private tempDir () =
    let dir = Path.Combine(Path.GetTempPath(), "fqs-cancel-tests-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    dir

[<Fact>]
let ``requestCancel sets isCancelled to true`` () =
    let dir = tempDir ()

    try
        CancellationSignal.reset dir
        Assert.False(CancellationSignal.isCancelled dir)
        CancellationSignal.requestCancel dir
        Assert.True(CancellationSignal.isCancelled dir)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``reset clears a previously requested cancellation`` () =
    let dir = tempDir ()

    try
        CancellationSignal.requestCancel dir
        Assert.True(CancellationSignal.isCancelled dir)
        CancellationSignal.reset dir
        Assert.False(CancellationSignal.isCancelled dir)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``requestCancel and isCancelled are independent per working directory`` () =
    let dirA = tempDir ()
    let dirB = tempDir ()

    try
        CancellationSignal.requestCancel dirA
        Assert.True(CancellationSignal.isCancelled dirA)
        Assert.False(CancellationSignal.isCancelled dirB)
    finally
        Directory.Delete(dirA, true)
        Directory.Delete(dirB, true)
