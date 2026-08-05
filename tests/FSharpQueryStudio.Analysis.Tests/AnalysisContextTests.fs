module AnalysisContextTests

open System.IO
open Xunit
open FSharpQueryStudio.Analysis

[<Fact>]
let ``build resolves a non-empty baseline reference set including FSharp Core`` () =
    let dir = TestHelpers.tempWorkingDirectory ()

    try
        let context = AnalysisContext.build dir

        Assert.NotEmpty(context.References)
        Assert.Contains(context.References, fun (r: string) -> r.Contains("FSharp.Core"))
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``build records the working directory it was given`` () =
    let dir = TestHelpers.tempWorkingDirectory ()

    try
        let context = AnalysisContext.build dir

        Assert.Equal(dir, context.WorkingDirectory)
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``building the context twice for the same working directory produces byte-identical reference sets`` () =
    let dir = TestHelpers.tempWorkingDirectory ()

    try
        let first = AnalysisContext.build dir
        let second = AnalysisContext.build dir

        Assert.Equal<string list>(first.References, second.References)
    finally
        Directory.Delete(dir, true)
