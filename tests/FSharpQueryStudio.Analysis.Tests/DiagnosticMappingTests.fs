module DiagnosticMappingTests

open System.IO
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Analysis

let private firstDiagnostic (source: string) : Diagnostic =
    let dir = TestHelpers.tempWorkingDirectory ()

    try
        let context = AnalysisContext.build dir
        let diagnostics = Checker.check context source |> Async.RunSynchronously
        Assert.NotEmpty(diagnostics)
        DiagnosticMapping.map diagnostics.[0]
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``a multiline span converts FCS's 1-based line to a 0-based protocol line`` () =
    // Error is on the second physical line; FCS reports StartLine=2/EndLine=2.
    let diagnostic = firstDiagnostic "let a = 1\nlet b : int = \"oops\""

    Assert.Equal(1, diagnostic.StartLine)
    Assert.Equal(1, diagnostic.EndLine)
    Assert.Equal(14, diagnostic.StartColumn)
    Assert.Equal(20, diagnostic.EndColumn)

[<Fact>]
let ``leading empty lines still land on the correct 0-based line`` () =
    // Error is on the third physical line (two blank lines precede it).
    let diagnostic = firstDiagnostic "\n\nlet x : int = \"oops\""

    Assert.Equal(2, diagnostic.StartLine)

[<Fact>]
let ``an end-of-file span still converts to a valid 0-based line`` () =
    let diagnostic = firstDiagnostic "let x : int ="

    Assert.Equal(0, diagnostic.StartLine)
    Assert.Equal(0, diagnostic.EndLine)

[<Fact>]
let ``Unicode source before the error does not throw off the column`` () =
    let diagnostic = firstDiagnostic "let café : int = \"öops\""

    Assert.Equal(0, diagnostic.StartLine)
    Assert.Equal(17, diagnostic.StartColumn)
    Assert.Equal(23, diagnostic.EndColumn)

[<Fact>]
let ``maps FCS Error severity and error-number code`` () =
    let diagnostic = firstDiagnostic "let a = 1\nlet b : int = \"oops\""

    Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity)
    Assert.Equal(Some "FS0001", diagnostic.Code)
