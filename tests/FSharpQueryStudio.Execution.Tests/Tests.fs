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
