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
let ``evaluate captures the exception's own message when a concretely-typed script throws`` () =
    task {
        let workDir = tempWorkingDirectory ()

        try
            let work =
                task {
                    let session = FsiSession.create workDir
                    return Evaluation.evaluate session "(failwith \"boom\" : int)"
                }

            let! outcome = work |> withTimeout 30.0

            match outcome.Exception with
            | Some ex -> Assert.Equal("boom", ex.Message)
            | None -> failwith "expected an exception, got none"
        finally
            cleanupWorkingDirectory workDir
    }

/// Characterizes a real FSI gotcha, not a bug in `Evaluation`: `failwith`
/// has type `string -> 'a`, so a bare top-level `failwith "boom"` never
/// gets a concrete type. F#'s value restriction rejects the interaction
/// at compile time before it can run, so no runtime exception is ever
/// raised — FSI reports a generic completion failure instead of "boom".
/// A script needs a concrete type context (a type annotation, or being
/// consumed by something concretely typed) for its own exception message
/// to surface. Worth remembering for Milestone B's diagnostics work: this
/// case needs to read as a compile diagnostic to the user, not "the script
/// crashed".
[<Fact>]
let ``a bare generic failwith never runs, so its own message never surfaces`` () =
    task {
        let workDir = tempWorkingDirectory ()

        try
            let work =
                task {
                    let session = FsiSession.create workDir
                    return Evaluation.evaluate session "failwith \"boom\""
                }

            let! outcome = work |> withTimeout 30.0

            match outcome.Exception with
            | Some ex -> Assert.NotEqual<string>("boom", ex.Message)
            | None -> failwith "expected an exception, got none"

            Assert.NotEmpty(outcome.Diagnostics)
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
