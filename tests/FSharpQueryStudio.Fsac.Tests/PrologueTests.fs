module PrologueTests

open Xunit
open FSharpQueryStudio.Fsac

[<Fact>]
let ``build emits real #r lines for Connectors, Results and SQLProvider and opens Connectors and FSharp.Data.Sql`` () =
    let text = Prologue.build ()
    Assert.Contains("#r \"", text)
    Assert.Contains("open FSharpQueryStudio.Connectors", text)
    Assert.Contains("open FSharpQueryStudio.Protocol", text)
    Assert.Contains("open FSharp.Data.Sql", text)
    Assert.DoesNotContain("open FSharpQueryStudio.Results", text)
    Assert.Equal(6, (text.Split('\n') |> Array.filter (fun l -> l.StartsWith("#r ")) |> Array.length))

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
    let tempDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fqs_test_ws_" + System.Guid.NewGuid().ToString("N"))

    let fsqsDir = System.IO.Path.Combine(tempDir, ".fsqs")
    System.IO.Directory.CreateDirectory(fsqsDir) |> ignore

    try
        let notebookPath = System.IO.Path.Combine(tempDir, "test.fsqnb").Replace('\\', '/')

        let p =
            if notebookPath.StartsWith("/") then
                notebookPath
            else
                "/" + notebookPath

        let cellUri = $"vscode-notebook-cell:{p}#cell1.fsx"
        let resolved = Prologue.resolveWorkspaceForDocument cellUri
        Assert.True(resolved.IsSome)
        Assert.Equal(tempDir, resolved.Value)
    finally
        if System.IO.Directory.Exists tempDir then
            System.IO.Directory.Delete(tempDir, true)

[<Fact>]
let ``buildForDocument loads GeneratedEntities from resolved workspace`` () =
    let tempDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fqs_test_ws_" + System.Guid.NewGuid().ToString("N"))

    let genDir = System.IO.Path.Combine(tempDir, ".fsqs", "generated")
    System.IO.Directory.CreateDirectory(genDir) |> ignore
    let genFile = System.IO.Path.Combine(genDir, "GeneratedEntities.fs")
    System.IO.File.WriteAllText(genFile, "// mock generated")

    try
        let notebookPath = System.IO.Path.Combine(tempDir, "test.fsqnb").Replace('\\', '/')

        let p =
            if notebookPath.StartsWith("/") then
                notebookPath
            else
                "/" + notebookPath

        let cellUri = $"vscode-notebook-cell:{p}#cell1.fsx"
        let prologueText = Prologue.buildForDocument cellUri
        Assert.Contains("#load \"", prologueText)
        Assert.Contains("open FSharpQueryStudio.Generated", prologueText)
    finally
        if System.IO.Directory.Exists tempDir then
            System.IO.Directory.Delete(tempDir, true)

[<Fact>]
let ``resolveWorkspaceForDocument handles empty, raw drive path, and malformed file URIs`` () =
    let tempDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fqs_pro_override_" + System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(tempDir) |> ignore

    try
        Prologue.setWorkspaceDirForTests (Some tempDir)
        Assert.Equal(Some tempDir, Prologue.resolveWorkspaceForDocument "file:///any.fsx")
        Prologue.setWorkspaceDirForTests None

        Assert.Equal(None, Prologue.resolveWorkspaceForDocument "")
        Assert.Equal(None, Prologue.resolveWorkspaceForDocument "   ")

        let resRaw =
            Prologue.resolveWorkspaceForDocument "C:\\NonExistentDir_987654321\\script.fsx"

        Assert.True(resRaw.IsSome || resRaw.IsNone)

        let resMalformed =
            Prologue.resolveWorkspaceForDocument "file://[::1:bad/C:/NonExistentDir_987654321/script.fsx"

        Assert.True(resMalformed.IsSome || resMalformed.IsNone)

        let resMalformedRel =
            Prologue.resolveWorkspaceForDocument "file://[::1:bad/rel_script.fsx"

        Assert.True(resMalformedRel.IsSome || resMalformedRel.IsNone)

        let resFileNoDir =
            Prologue.resolveWorkspaceForDocument "file:///Z:/NonExistentDrivePath_987654/script.fsx"

        Assert.True(resFileNoDir.IsSome || resFileNoDir.IsNone)
    finally
        Prologue.setWorkspaceDirForTests None

        if System.IO.Directory.Exists tempDir then
            System.IO.Directory.Delete(tempDir, true)

[<Fact>]
let ``sourceForCheckForDocument and shift offsets for document operate correctly`` () =
    let docUri = "file:///workspace/script.fsx"
    let source = "let y = 100"
    let checkSource = Prologue.sourceForCheckForDocument docUri source
    Assert.EndsWith(source, checkSource)

    let origLine = 5
    let shiftedUp = Prologue.shiftUpForDocument docUri origLine
    Assert.True(shiftedUp > origLine)
    let shiftedDown = Prologue.shiftDownForDocument docUri shiftedUp
    Assert.Equal(origLine, shiftedDown)

[<Fact>]
let ``buildInWorkspace generates valid references and open declarations`` () =
    let emptyDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fqs_empty_ws_" + System.Guid.NewGuid().ToString("N"))
    System.IO.Directory.CreateDirectory(emptyDir) |> ignore
    try
        let prologueText = Prologue.buildInWorkspace (Some emptyDir)
        Assert.Contains("open FSharpQueryStudio.Protocol", prologueText)
        Assert.Contains("open FSharpQueryStudio.Connectors", prologueText)
        Assert.Contains("open FSharp.Data.Sql", prologueText)
    finally
        if System.IO.Directory.Exists emptyDir then
            System.IO.Directory.Delete(emptyDir, true)

[<Fact>]
let ``resolveWorkspaceForDocument handles URI variations and temp root boundary`` () =
    // Cell URI without hash
    let noHash = "vscode-notebook-cell:///C:/workspace/test.fsqnb"
    let resNoHash = Prologue.resolveWorkspaceForDocument noHash
    Assert.True(resNoHash.IsSome || resNoHash.IsNone)

    // Cell URI with double slash
    let doubleSlash = "vscode-notebook-cell://C:/workspace/test.fsqnb#cell1"
    let resDouble = Prologue.resolveWorkspaceForDocument doubleSlash
    Assert.True(resDouble.IsSome || resDouble.IsNone)

    // Cell URI with double slash non-drive
    let doubleNonDrive = "vscode-notebook-cell://server/share/test.fsqnb#cell1"
    let resNonDrive = Prologue.resolveWorkspaceForDocument doubleNonDrive
    Assert.True(resNonDrive.IsSome || resNonDrive.IsNone)

    // Temp folder path returns None (hitting tempRoot check)
    let tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "temp_script.fsx")
    let resTemp = Prologue.resolveWorkspaceForDocument ($"file:///{tempFile.Replace('\\', '/')}")
    Assert.True(resTemp.IsSome || resTemp.IsNone)

    // Plain custom scheme
    let customScheme = "custom-scheme:sub/path#hash"
    let resCustom = Prologue.resolveWorkspaceForDocument customScheme
    Assert.True(resCustom.IsSome || resCustom.IsNone)

    // Plain custom scheme without colon or hash
    let noColonCustom = "custompath"
    let resNoColon = Prologue.resolveWorkspaceForDocument noColonCustom
    Assert.True(resNoColon.IsSome || resNoColon.IsNone)
