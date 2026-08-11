module CompletionsTests

open System.IO
open Xunit
open FSharpQueryStudio.Analysis

let private completeAt (source: string) (line: int) (column: int) : string list =
    let dir = TestHelpers.tempWorkingDirectory ()

    try
        let context = AnalysisContext.build dir
        Completions.completeAtPosition context source line column |> Async.RunSynchronously
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``a known keyword and a known connector helper name both appear in the completion list at an arbitrary cursor position`` () =
    let result = Completions.complete []

    Assert.Contains("let", result)
    Assert.Contains("Connections.sql", result)

[<Fact>]
let ``names from the most recent successful check are included in the completion list`` () =
    let result = Completions.complete [ "totalSales" ]

    Assert.Contains("totalSales", result)

[<Fact>]
let ``member access on a local binding triggers semantic member completions`` () =
    // "foo." ends at column 4 (0-based), right after the dot.
    let names = completeAt "let foo = \"hello\"\nfoo." 2 4

    Assert.Contains("Length", names)

[<Fact>]
let ``completions for a value of a known standard-library type include its well-known members`` () =
    // "sb.A" ends at column 3 (0-based), the index of 'A' itself.
    let names = completeAt "let sb = System.Text.StringBuilder()\nsb.A" 2 3

    Assert.Contains("Append", names)

[<Fact>]
let ``member access on Connections resolves real semantic completions, not just the static tier-1 name list`` () =
    // "Connections." ends at column 12 (0-based), right after the dot -
    // no #r/open needed, same as real execution: AnalysisContext.build
    // references the Connectors assembly and Connections is
    // [<AutoOpen>]. Semantic (tier-2) completions here, unlike tier-1's
    // static "Connections.sql" string, prove FCS actually resolved the
    // module and can enumerate its real members.
    let names = completeAt "Connections." 1 12

    Assert.Contains("sql", names)
    Assert.Contains("csv", names)

[<Fact>]
let ``member access on Results resolves real semantic completions`` () =
    // "Results." ends at column 8 (0-based), right after the dot.
    let names = completeAt "Results." 1 8

    Assert.Contains("table", names)
    Assert.Contains("text", names)
