module CompletionsTests

open Xunit
open FSharpQueryStudio.Analysis

[<Fact>]
let ``a known keyword and a known connector helper name both appear in the completion list at an arbitrary cursor position`` () =
    let result = Completions.complete []

    Assert.Contains("let", result)
    Assert.Contains("Connections.sql", result)

[<Fact>]
let ``names from the most recent successful check are included in the completion list`` () =
    let result = Completions.complete [ "totalSales" ]

    Assert.Contains("totalSales", result)
