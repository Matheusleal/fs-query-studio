module Tests

open System
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
                    return session.Fsi.EvalInteractionNonThrowing("1 + 1")
                }

            let! (result, diagnostics) = work |> withTimeout 30.0

            match result with
            | Choice1Of2(Some _) -> ()
            | Choice1Of2 None -> failwith "expected a value, got none"
            | Choice2Of2 ex -> failwithf "evaluation failed: %O" ex

            Assert.Empty(diagnostics)
        finally
            cleanupWorkingDirectory workDir
    }
