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
    Assert.Equal(4, (text.Split('\n') |> Array.filter (fun l -> l.StartsWith("#r ")) |> Array.length))

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

[<Fact>]
let ``resolveWorkspaceForDocument resolves vscode-notebook-cell URIs and finds workspace`` () =
    let tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fqs_test_ws_" + System.Guid.NewGuid().ToString("N"))
    let fsqsDir = System.IO.Path.Combine(tempDir, ".fsqs")
    System.IO.Directory.CreateDirectory(fsqsDir) |> ignore
    try
        let notebookPath = System.IO.Path.Combine(tempDir, "test.fsqnb").Replace('\\', '/')
        let p = if notebookPath.StartsWith("/") then notebookPath else "/" + notebookPath
        let cellUri = $"vscode-notebook-cell:{p}#cell1.fsx"
        let resolved = Prologue.resolveWorkspaceForDocument cellUri
        Assert.True(resolved.IsSome)
        Assert.Equal(tempDir, resolved.Value)
    finally
        if System.IO.Directory.Exists tempDir then
            System.IO.Directory.Delete(tempDir, true)

[<Fact>]
let ``buildForDocument loads GeneratedEntities from resolved workspace`` () =
    let tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fqs_test_ws_" + System.Guid.NewGuid().ToString("N"))
    let genDir = System.IO.Path.Combine(tempDir, ".fsqs", "generated")
    System.IO.Directory.CreateDirectory(genDir) |> ignore
    let genFile = System.IO.Path.Combine(genDir, "GeneratedEntities.fs")
    System.IO.File.WriteAllText(genFile, "// mock generated")
    try
        let notebookPath = System.IO.Path.Combine(tempDir, "test.fsqnb").Replace('\\', '/')
        let p = if notebookPath.StartsWith("/") then notebookPath else "/" + notebookPath
        let cellUri = $"vscode-notebook-cell:{p}#cell1.fsx"
        let prologueText = Prologue.buildForDocument cellUri
        Assert.Contains("#load \"", prologueText)
        Assert.Contains("open FSharpQueryStudio.Generated", prologueText)
    finally
        if System.IO.Directory.Exists tempDir then
            System.IO.Directory.Delete(tempDir, true)

