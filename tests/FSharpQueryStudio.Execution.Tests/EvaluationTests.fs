module EvaluationTests

open Xunit
open FSharpQueryStudio.Execution
open TestHelpers

[<Fact>]
let ``evaluate captures printed output and returns the final expression's value`` () =
    task {
        let workDir = tempWorkingDirectory ()

        try
            let work =
                task {
                    let session = FsiSession.create workDir
                    return Evaluation.evaluate session """printfn "hi"; 1 + 1"""
                }

            let! outcome = work |> withTimeout 30.0

            Assert.Contains("hi", outcome.Output)
            Assert.True(outcome.Exception.IsNone)

            match outcome.Value with
            | Some fsiValue -> Assert.Equal(box 2, fsiValue.ReflectionValue)
            | None -> failwith "expected a value, got none"
        finally
            cleanupWorkingDirectory workDir
    }

[<Fact>]
let ``evaluate captures stderr-worthy diagnostics without throwing`` () =
    task {
        let workDir = tempWorkingDirectory ()

        try
            let work =
                task {
                    let session = FsiSession.create workDir
                    return Evaluation.evaluate session "let x: int = \"not an int\""
                }

            let! outcome = work |> withTimeout 30.0

            Assert.NotEmpty(outcome.Diagnostics)
        finally
            cleanupWorkingDirectory workDir
    }
