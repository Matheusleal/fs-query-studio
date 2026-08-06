module SessionStoreTests

open Xunit
open FSharpQueryStudio.Execution
open TestHelpers

[<Theory>]
[<InlineData("let x = 1", "let x = 1")>] // unchanged - a plain re-run
[<InlineData("let x = 1", "let x = 1\nlet y = 2")>] // pure append
[<InlineData("", "let x = 1")>] // first-ever run for this key
let ``invalidates is false when the new source is unchanged or a pure append`` (lastSource: string, newSource: string) =
    Assert.False(SessionStore.invalidates lastSource newSource)

[<Theory>]
[<InlineData("let x = 1", "let x = 2")>] // edited already-run text
[<InlineData("let x = 1\nlet y = 2", "let x = 1")>] // deleted already-run text
[<InlineData("let x = 1", "let y = 2")>] // unrelated replacement
let ``invalidates is true when the new source is not a pure append of the last-executed one`` (lastSource: string, newSource: string) =
    Assert.True(SessionStore.invalidates lastSource newSource)

[<Fact>]
let ``getOrCreate returns the same session instance for the same key when the source is unchanged`` () =
    let workDir = tempWorkingDirectory ()
    let key = "store-" + System.Guid.NewGuid().ToString("N")

    try
        let first = SessionStore.getOrCreate key workDir "let x = 1"
        let second = SessionStore.getOrCreate key workDir "let x = 1"
        Assert.Same(first, second)
    finally
        SessionStore.reset key
        cleanupWorkingDirectory workDir

[<Fact>]
let ``getOrCreate reuses the tracked session when the new source is a pure append of the last-executed one`` () =
    let workDir = tempWorkingDirectory ()
    let key = "store-" + System.Guid.NewGuid().ToString("N")

    try
        let first = SessionStore.getOrCreate key workDir "let x = 1"
        let second = SessionStore.getOrCreate key workDir "let x = 1\nlet y = 2"
        Assert.Same(first, second)
    finally
        SessionStore.reset key
        cleanupWorkingDirectory workDir

[<Fact>]
let ``getOrCreate starts a fresh session when the new source edits or removes already-executed text`` () =
    let workDir = tempWorkingDirectory ()
    let key = "store-" + System.Guid.NewGuid().ToString("N")

    try
        let first = SessionStore.getOrCreate key workDir "let x = 1"
        let second = SessionStore.getOrCreate key workDir "let x = 2"
        Assert.NotSame(first, second)
    finally
        SessionStore.reset key
        cleanupWorkingDirectory workDir

[<Fact>]
let ``getOrCreate returns different session instances for different keys`` () =
    let workDir = tempWorkingDirectory ()
    let keyA = "store-" + System.Guid.NewGuid().ToString("N")
    let keyB = "store-" + System.Guid.NewGuid().ToString("N")

    try
        let sessionA = SessionStore.getOrCreate keyA workDir "let x = 1"
        let sessionB = SessionStore.getOrCreate keyB workDir "let x = 1"
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
        let first = SessionStore.getOrCreate key workDir "let x = 1"
        SessionStore.reset key
        let second = SessionStore.getOrCreate key workDir "let x = 1"
        Assert.NotSame(first, second)
    finally
        SessionStore.reset key
        cleanupWorkingDirectory workDir
