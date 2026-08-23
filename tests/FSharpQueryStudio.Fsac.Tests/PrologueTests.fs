module PrologueTests

open Xunit
open FSharpQueryStudio.Fsac

[<Fact>]
let ``build emits real #r and open lines for Connectors and Results`` () =
    let text = Prologue.build ()
    Assert.Contains("#r \"", text)
    Assert.Contains("open FSharpQueryStudio.Connectors", text)
    Assert.Contains("open FSharpQueryStudio.Results", text)

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
    // build() is 4 real lines: two #r lines, two open lines. A caller's
    // real line 1 should land on line 5 in the prologue-prefixed
    // document, and an FSAC-reported line 5 should shift back to 1.
    Assert.Equal(5, Prologue.shiftUp 1)
    Assert.Equal(1, Prologue.shiftDown 5)
