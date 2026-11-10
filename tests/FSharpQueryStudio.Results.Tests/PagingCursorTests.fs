module PagingCursorTests

open System.Collections
open Xunit
open FSharpQueryStudio.Results

let private cursorOver (items: int list) : PagingCursor =
    new PagingCursor((items |> List.map box :> IEnumerable).GetEnumerator())

[<Fact>]
let ``TakePage returns every item and no-more when the source has fewer items than the page size`` () =
    let cursor = cursorOver [ 1; 2; 3 ]

    let page, hasMore = cursor.TakePage(10)

    Assert.Equal<obj array>([| box 1; box 2; box 3 |], page)
    Assert.False(hasMore)

[<Fact>]
let ``TakePage reports no-more when the source has exactly the page size`` () =
    let cursor = cursorOver [ 1; 2; 3 ]

    let page, hasMore = cursor.TakePage(3)

    Assert.Equal<obj array>([| box 1; box 2; box 3 |], page)
    Assert.False(hasMore)

[<Fact>]
let ``TakePage reports more without consuming the next item, and a second call resumes from it`` () =
    let cursor = cursorOver [ 1; 2; 3; 4; 5 ]

    let firstPage, hasMoreAfterFirst = cursor.TakePage(3)
    let secondPage, hasMoreAfterSecond = cursor.TakePage(3)

    Assert.Equal<obj array>([| box 1; box 2; box 3 |], firstPage)
    Assert.True(hasMoreAfterFirst)
    Assert.Equal<obj array>([| box 4; box 5 |], secondPage)
    Assert.False(hasMoreAfterSecond)

[<Fact>]
let ``repeated TakePage calls drain a long source exactly once per item, in order`` () =
    let cursor = cursorOver [ 1..11 ]
    let collected = ResizeArray<obj>()

    let mutable hasMore = true

    while hasMore do
        let page, more = cursor.TakePage(4)
        collected.AddRange(page)
        hasMore <- more

    Assert.Equal<obj list>([ 1..11 ] |> List.map box, collected |> List.ofSeq)
