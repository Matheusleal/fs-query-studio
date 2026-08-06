module SessionStoreTests

open Xunit
open FSharpQueryStudio.Execution
open TestHelpers

[<Fact>]
let ``getOrCreate returns the same session instance for the same key`` () =
    let workDir = tempWorkingDirectory ()
    let key = "store-" + System.Guid.NewGuid().ToString("N")

    try
        let first = SessionStore.getOrCreate key workDir
        let second = SessionStore.getOrCreate key workDir
        Assert.Same(first, second)
    finally
        SessionStore.reset key
        cleanupWorkingDirectory workDir

[<Fact>]
let ``getOrCreate returns different session instances for different keys`` () =
    let workDir = tempWorkingDirectory ()
    let keyA = "store-" + System.Guid.NewGuid().ToString("N")
    let keyB = "store-" + System.Guid.NewGuid().ToString("N")

    try
        let sessionA = SessionStore.getOrCreate keyA workDir
        let sessionB = SessionStore.getOrCreate keyB workDir
        Assert.NotSame(sessionA, sessionB)
    finally
        SessionStore.reset keyA
        SessionStore.reset keyB
        cleanupWorkingDirectory workDir

[<Fact>]
let ``reset drops the tracked session so the next getOrCreate starts fresh`` () =
    let workDir = tempWorkingDirectory ()
    let key = "store-" + System.Guid.NewGuid().ToString("N")

    try
        let first = SessionStore.getOrCreate key workDir
        SessionStore.reset key
        let second = SessionStore.getOrCreate key workDir
        Assert.NotSame(first, second)
    finally
        SessionStore.reset key
        cleanupWorkingDirectory workDir
