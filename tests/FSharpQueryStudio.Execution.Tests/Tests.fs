module Tests

open System
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Execution
open TestHelpers

[<Fact>]
let ``create sets the current directory to the one requested`` () =
    let workDir = tempWorkingDirectory ()

    try
        FsiSession.create workDir |> ignore
        Assert.Equal(IO.Path.GetFullPath(workDir), IO.Path.GetFullPath(Environment.CurrentDirectory))
    finally
        cleanupWorkingDirectory workDir

[<Fact>]
let ``create returns a session that evaluates a trivial expression without hanging on stdin`` () =
    task {
        let workDir = tempWorkingDirectory ()

        try
            let work =
                task {
                    let session = FsiSession.create workDir
                    return Evaluation.evaluate session "1 + 1"
                }

            let! outcome = work |> withTimeout 30.0

            Assert.True(outcome.Exception.IsNone)
            match outcome.Value with
            | Some v -> Assert.Equal(box 2, v.ReflectionValue)
            | None -> failwith "expected a value, got none"

            Assert.Empty(outcome.Diagnostics)
        finally
            cleanupWorkingDirectory workDir
    }

[<Fact>]
let ``refreshGeneratedEntities resolves generated/GeneratedEntities.fs and throws on invalid syntax`` () =
    let workDir = tempWorkingDirectory ()
    let wsDir = tempWorkingDirectory ()
    try
        let session = FsiSession.createInWorkspace workDir ""
        // 1. Test local generated/GeneratedEntities.fs (line 49)
        let localGenDir = IO.Path.Combine(workDir, "generated")
        IO.Directory.CreateDirectory(localGenDir) |> ignore
        let localGenFile = IO.Path.Combine(localGenDir, "GeneratedEntities.fs")
        IO.File.WriteAllText(localGenFile, "namespace FSharpQueryStudio.Generated\nmodule TestMod =\n    let v = 123\n")
        FsiSession.refreshGeneratedEntities workDir "" session
        Assert.Contains("let v = 123", session.GeneratedContent)

        // 2. Test wsGenerated with /C:/ prefix and syntax error (lines 26, 45, 70-71)
        let wsGenDir = IO.Path.Combine(wsDir, "generated")
        IO.Directory.CreateDirectory(wsGenDir) |> ignore
        let wsGenFile = IO.Path.Combine(wsGenDir, "GeneratedEntities.fs")
        IO.File.WriteAllText(wsGenFile, "namespace FSharpQueryStudio.Generated\nlet = syntax error\n")
        let prefixedWs = "/" + wsDir.Replace("\\", "/")
        session.GeneratedTimestamp <- DateTime.MinValue
        let ex = Assert.Throws<Exception>(fun () -> FsiSession.refreshGeneratedEntities workDir prefixedWs session)
        Assert.Contains("#load of", ex.Message)
    finally
        cleanupWorkingDirectory workDir
        cleanupWorkingDirectory wsDir

