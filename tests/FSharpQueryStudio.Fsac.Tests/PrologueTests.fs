module PrologueTests

open Xunit
open FSharpQueryStudio.Fsac

[<Fact>]
let ``build emits real #r lines for Connectors, Results and SQLProvider and opens Connectors and FSharp.Data.Sql`` () =
    let text = Prologue.build ()
    Assert.Contains("#r \"", text)
    Assert.Contains("open FSharpQueryStudio.Connectors", text)
    Assert.Contains("open FSharp.Data.Sql", text)
    Assert.DoesNotContain("open FSharpQueryStudio.Results", text)
    Assert.Equal(3, (text.Split('\n') |> Array.filter (fun l -> l.StartsWith("#r ")) |> Array.length))

[<Fact>]
let ``lineCount matches the number of real lines build() occupies`` () =
    let text = Prologue.build ()
    let expected = text.Split('\n').Length - 1 // build() ends with a trailing newline, matching AnalysisContext's own prologue
    Assert.Equal(expected, Prologue.lineCount text)

[<Fact>]
let ``sourceForCheck prepends the prologue ahead of the real source`` () =
    let source = "let x = 1"
    let checked_ = Prologue.sourceForCheck source
    Assert.StartsWith(Prologue.build (), checked_)
    Assert.EndsWith(source, checked_)

[<Fact>]
let ``shiftUp then shiftDown is the identity`` () =
    let original = 4
    Assert.Equal(original, Prologue.shiftDown (Prologue.shiftUp original))

[<Fact>]
let ``shiftUp and shiftDown use the real prologue's actual line count`` () =
    let expectedShift = 1 + Prologue.lineCount (Prologue.build ())
    Assert.Equal(expectedShift, Prologue.shiftUp 1)
    Assert.Equal(1, Prologue.shiftDown expectedShift)
