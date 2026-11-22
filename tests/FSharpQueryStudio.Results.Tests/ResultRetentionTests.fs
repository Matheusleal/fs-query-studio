module ResultRetentionTests

open Xunit
open FSharpQueryStudio.Results

[<Fact>]
let ``tryGet on a key nothing was ever set under returns None`` () =
    Assert.True((ResultRetention.tryGet "untitled:NeverSet.fsx").IsNone)

[<Fact>]
let ``set then tryGet round-trips the same columns and rows`` () =
    let result: RetainedResult =
        {
            Columns = [ "id"; "name" ]
            Rows = [| [| box 1; box "Ada" |]; [| box 2; box "Alan" |] |]
        }

    ResultRetention.set "untitled:Script1.fsx" result

    let retrieved = (ResultRetention.tryGet "untitled:Script1.fsx").Value
    Assert.Equal<string list>(result.Columns, retrieved.Columns)
    Assert.Equal<obj array array>(result.Rows, retrieved.Rows)

[<Fact>]
let ``set overwrites an existing entry for the same key`` () =
    let first: RetainedResult =
        {
            Columns = [ "a" ]
            Rows = [| [| box 1 |] |]
        }

    let second: RetainedResult =
        {
            Columns = [ "b" ]
            Rows = [| [| box 2 |] |]
        }

    ResultRetention.set "untitled:Overwrite.fsx" first
    ResultRetention.set "untitled:Overwrite.fsx" second

    let retrieved = (ResultRetention.tryGet "untitled:Overwrite.fsx").Value
    Assert.Equal<string list>([ "b" ], retrieved.Columns)

[<Fact>]
let ``discard removes a previously-set entry`` () =
    ResultRetention.set
        "untitled:ToDiscard.fsx"
        {
            Columns = [ "a" ]
            Rows = [| [| box 1 |] |]
        }

    ResultRetention.discard "untitled:ToDiscard.fsx"

    Assert.True((ResultRetention.tryGet "untitled:ToDiscard.fsx").IsNone)

[<Fact>]
let ``tryGet matches case-insensitively and across URL-encoded URI variants`` () =
    let result: RetainedResult =
        {
            Columns = [ "col1" ]
            Rows = [| [| box 42 |] |]
        }

    ResultRetention.set "file:///d%3A/Projects/test.fsqnb" result

    let retrieved1 = ResultRetention.tryGet "file:///D:/Projects/test.fsqnb"
    Assert.True(retrieved1.IsSome)
    Assert.Equal<string list>([ "col1" ], retrieved1.Value.Columns)

    let retrieved2 = ResultRetention.tryGet "FILE:///d%3A/projects/test.fsqnb"
    Assert.True(retrieved2.IsSome)

    ResultRetention.discard "file:///D:/Projects/test.fsqnb"
    Assert.True((ResultRetention.tryGet "file:///d%3A/Projects/test.fsqnb").IsNone)

[<Fact>]
let ``tryGet hits normalized key fallback when query key is URL encoded scenario 1`` () =
    let result: RetainedResult =
        {
            Columns = [ "id" ]
            Rows = [| [| box 101 |] |]
        }
    // Store under raw unencoded path
    ResultRetention.set "file:///C:/Workspace/doc1.fsqnb" result
    // Query with URL-encoded colon (%3A) which misses direct match and hits normalized fallback
    let retrieved = ResultRetention.tryGet "file:///C%3A/Workspace/doc1.fsqnb"
    Assert.True(retrieved.IsSome)
    Assert.Equal<string list>([ "id" ], retrieved.Value.Columns)
    Assert.Equal(box 101, retrieved.Value.Rows.[0].[0])
    ResultRetention.discard "file:///C:/Workspace/doc1.fsqnb"

[<Fact>]
let ``tryGet hits normalized key fallback when query key has backslashes and encoded chars scenario 2`` () =
    let result: RetainedResult =
        {
            Columns = [ "colA" ]
            Rows = [| [| box "data" |] |]
        }
    // Store under clean forward-slash URI
    ResultRetention.set "file:///D:/MyProject/notebook.fsqnb" result
    // Query with backslash and encoded drive letter
    let retrieved = ResultRetention.tryGet "file:///D%3A\\MyProject\\notebook.fsqnb"
    Assert.True(retrieved.IsSome)
    Assert.Equal<string list>([ "colA" ], retrieved.Value.Columns)
    ResultRetention.discard "file:///D:/MyProject/notebook.fsqnb"

[<Fact>]
let ``tryGet and set with empty or null URI returns None and handles gracefully scenario 1`` () =
    Assert.True((ResultRetention.tryGet "").IsNone)
    Assert.True((ResultRetention.tryGet null).IsNone)
    ResultRetention.discard ""
    ResultRetention.discard null

[<Fact>]
let ``set and discard with empty or null URI does not throw or corrupt store scenario 2`` () =
    let result: RetainedResult =
        {
            Columns = [ "x" ]
            Rows = [| [| box 1 |] |]
        }

    ResultRetention.set "" result
    ResultRetention.set null result
    Assert.True((ResultRetention.tryGet "").IsNone)
    Assert.True((ResultRetention.tryGet null).IsNone)
    ResultRetention.discard ""
    ResultRetention.discard null

[<Fact>]
let ``discard on a key nothing was ever set under does not throw`` () =
    ResultRetention.discard "untitled:NeverSetEither.fsx" // Must not throw.
