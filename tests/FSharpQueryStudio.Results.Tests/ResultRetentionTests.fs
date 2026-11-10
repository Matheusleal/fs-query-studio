module ResultRetentionTests

open Xunit
open FSharpQueryStudio.Results

[<Fact>]
let ``tryGet on a key nothing was ever set under returns None`` () =
    Assert.True((ResultRetention.tryGet "untitled:NeverSet.fsx").IsNone)

[<Fact>]
let ``set then tryGet round-trips the same columns and rows`` () =
    let result: RetainedResult =
        { Columns = [ "id"; "name" ]
          Rows = [| [| box 1; box "Ada" |]; [| box 2; box "Alan" |] |] }

    ResultRetention.set "untitled:Script1.fsx" result

    let retrieved = (ResultRetention.tryGet "untitled:Script1.fsx").Value
    Assert.Equal<string list>(result.Columns, retrieved.Columns)
    Assert.Equal<obj array array>(result.Rows, retrieved.Rows)

[<Fact>]
let ``set overwrites an existing entry for the same key`` () =
    let first: RetainedResult = { Columns = [ "a" ]; Rows = [| [| box 1 |] |] }
    let second: RetainedResult = { Columns = [ "b" ]; Rows = [| [| box 2 |] |] }

    ResultRetention.set "untitled:Overwrite.fsx" first
    ResultRetention.set "untitled:Overwrite.fsx" second

    let retrieved = (ResultRetention.tryGet "untitled:Overwrite.fsx").Value
    Assert.Equal<string list>([ "b" ], retrieved.Columns)

[<Fact>]
let ``discard removes a previously-set entry`` () =
    ResultRetention.set "untitled:ToDiscard.fsx" { Columns = [ "a" ]; Rows = [| [| box 1 |] |] }

    ResultRetention.discard "untitled:ToDiscard.fsx"

    Assert.True((ResultRetention.tryGet "untitled:ToDiscard.fsx").IsNone)

[<Fact>]
let ``tryGet matches case-insensitively and across URL-encoded URI variants`` () =
    let result: RetainedResult = { Columns = [ "col1" ]; Rows = [| [| box 42 |] |] }
    ResultRetention.set "file:///d%3A/Projects/test.fsqnb" result

    let retrieved1 = ResultRetention.tryGet "file:///D:/Projects/test.fsqnb"
    Assert.True(retrieved1.IsSome)
    Assert.Equal<string list>([ "col1" ], retrieved1.Value.Columns)

    let retrieved2 = ResultRetention.tryGet "FILE:///d%3A/projects/test.fsqnb"
    Assert.True(retrieved2.IsSome)

    ResultRetention.discard "file:///D:/Projects/test.fsqnb"
    Assert.True((ResultRetention.tryGet "file:///d%3A/Projects/test.fsqnb").IsNone)

[<Fact>]
let ``discard on a key nothing was ever set under does not throw`` () =
    ResultRetention.discard "untitled:NeverSetEither.fsx" // Must not throw.
