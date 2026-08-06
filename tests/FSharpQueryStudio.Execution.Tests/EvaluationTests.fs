module EvaluationTests

open System.Threading.Tasks
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

            let! outcome = work |> withTimeout 60.0

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

            let! outcome = work |> withTimeout 60.0

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

            let! outcome = work |> withTimeout 60.0

            match outcome.Exception with
            | Some ex -> Assert.NotEqual<string>("boom", ex.Message)
            | None -> failwith "expected an exception, got none"

            Assert.NotEmpty(outcome.Diagnostics)
        finally
            cleanupWorkingDirectory workDir
    }

/// A script observes cancellation via a plain `System.IO.File.Exists`
/// check against a sentinel file in its own working directory (relative
/// path — `FsiSession.create` already points CWD there) rather than a
/// host-injected value or a call back into a host assembly. Both of
/// those were tried first and both failed for real reasons: binding a
/// BCL value via `AddBoundValue` doesn't unify (FSI type-checks against
/// reference assemblies, distinct from the runtime assemblies a host
/// object comes from, so even `bool` fails); and an earlier
/// self-referencing-assembly design (a static field, script calling back
/// into `FSharpQueryStudio.Execution` via `--reference:`) was
/// *intermittently* broken — `FsiEvaluationSession`'s collectible
/// AssemblyLoadContext sometimes loaded its own separate copy of the
/// explicitly-referenced assembly instead of reusing the one already
/// loaded in the process, giving the script an independent static field
/// disconnected from the one the host mutated. A file has no assembly
/// identity to get out of sync.
[<Fact>]
let ``evaluate stops a cooperative loop early once CancellationSignal is requested`` () =
    task {
        let workDir = tempWorkingDirectory ()

        try
            CancellationSignal.reset workDir

            let script =
                """
let mutable i = 0
while i < 2_000_000_000 && not (System.IO.File.Exists(".cancel-requested")) do
    i <- i + 1
i
"""

            let evalTask =
                Task.Run(fun () ->
                    let session = FsiSession.create workDir
                    Evaluation.evaluate session script)

            do! Task.Delay(200)
            CancellationSignal.requestCancel workDir

            let! outcome = evalTask |> withTimeout 60.0

            Assert.True(outcome.Exception.IsNone)

            match outcome.Value with
            | Some fsiValue ->
                match fsiValue.ReflectionValue with
                | :? int as i -> Assert.True(i < 2_000_000_000, $"expected the loop to stop early, got i={i}")
                | other -> failwithf "expected an int, got %A" other
            | None -> failwith "expected a value, got none"
        finally
            cleanupWorkingDirectory workDir
    }

/// Paired with the isolated-mode case right below it: same assertion
/// shape, opposite outcome, so a regression on either the new Persistent
/// path or Milestone A's Isolated default shows up as a single failing
/// test rather than a subtle difference in test setup.
[<Fact>]
let ``persistent mode: bindings survive across two sequential evaluations under the same session key`` () =
    task {
        let workDir = tempWorkingDirectory ()
        let key = "persistent-" + System.Guid.NewGuid().ToString("N")

        try
            let first =
                task {
                    let session = SessionStore.getOrCreate key workDir
                    return Evaluation.evaluate session "let x = 41"
                }

            let! firstOutcome = first |> withTimeout 60.0
            Assert.True(firstOutcome.Exception.IsNone)

            let second =
                task {
                    let session = SessionStore.getOrCreate key workDir
                    return Evaluation.evaluate session "x + 1"
                }

            let! secondOutcome = second |> withTimeout 60.0

            match secondOutcome.Value with
            | Some fsiValue -> Assert.Equal(box 42, fsiValue.ReflectionValue)
            | None -> failwith "expected a value, got none"
        finally
            SessionStore.reset key
            cleanupWorkingDirectory workDir
    }

[<Fact>]
let ``isolated mode: bindings do not survive across two fresh sessions`` () =
    task {
        let workDir = tempWorkingDirectory ()

        try
            let first =
                task {
                    let session = FsiSession.create workDir
                    return Evaluation.evaluate session "let x = 41"
                }

            let! firstOutcome = first |> withTimeout 60.0
            Assert.True(firstOutcome.Exception.IsNone)

            let second =
                task {
                    let session = FsiSession.create workDir
                    return Evaluation.evaluate session "x + 1"
                }

            let! secondOutcome = second |> withTimeout 60.0

            Assert.True(secondOutcome.Exception.IsSome)
            Assert.NotEmpty(secondOutcome.Diagnostics)
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

            let! outcome = work |> withTimeout 60.0

            Assert.NotEmpty(outcome.Diagnostics)
        finally
            cleanupWorkingDirectory workDir
    }
