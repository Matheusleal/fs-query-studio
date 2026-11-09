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

            let! outcome = work |> withTimeout 45.0

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

            let! outcome = work |> withTimeout 45.0

            match outcome.Exception with
            | Some ex -> Assert.Equal("boom", ex.Message)
            | None -> failwith "expected an exception, got none"
        finally
            cleanupWorkingDirectory workDir
    }

[<Fact>]
let ``evaluate trims large StringBuilder output buffer capacity after large printing`` () =
    task {
        let workDir = tempWorkingDirectory ()

        try
            let work =
                task {
                    let session = FsiSession.create workDir
                    let largeOutputScript = "for i in 1 .. 2000 do printfn \"Large output row number %d\" i"
                    let _ = Evaluation.evaluate session largeOutputScript
                    Assert.True(session.Output.GetStringBuilder().Capacity <= 4096)
                    Assert.True(session.Errors.GetStringBuilder().Capacity <= 4096)
                }

            do! work |> withTimeout 45.0
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

            let! outcome = work |> withTimeout 45.0

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
                async {
                    let session = FsiSession.create workDir
                    return Evaluation.evaluate session script
                }
                |> Async.StartAsTask

            do! Task.Delay(200)
            CancellationSignal.requestCancel workDir

            let! outcome = evalTask |> withTimeout 45.0

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
/// test rather than a subtle difference in test setup. The second
/// evaluation's source is the first's full text plus an appended line —
/// E.3's invalidation rule (`SessionStore.invalidates`) requires a pure
/// append to reuse the session, matching how `ShellViewModel.RunAsync`
/// actually resends the whole growing document on every run rather than
/// independent snippets.
[<Fact>]
let ``persistent mode: bindings survive across two sequential evaluations under the same session key`` () =
    task {
        let workDir = tempWorkingDirectory ()
        let key = "persistent-" + System.Guid.NewGuid().ToString("N")

        try
            let first =
                task {
                    let session = SessionStore.getOrCreate key workDir "let x = 41"
                    return Evaluation.evaluate session "let x = 41"
                }

            let! firstOutcome = first |> withTimeout 45.0
            Assert.True(firstOutcome.Exception.IsNone)

            let second =
                task {
                    let session = SessionStore.getOrCreate key workDir "let x = 41\nx + 1"
                    return Evaluation.evaluate session "let x = 41\nx + 1"
                }

            let! secondOutcome = second |> withTimeout 45.0

            match secondOutcome.Value with
            | Some fsiValue -> Assert.Equal(box 42, fsiValue.ReflectionValue)
            | None -> failwith "expected a value, got none"
        finally
            SessionStore.reset key
            cleanupWorkingDirectory workDir
    }

/// E.3's invalidation rule in action at the `SessionStore`/`Evaluation`
/// integration level, not just as a pure `SessionStore.invalidates`
/// check (see `SessionStoreTests.fs`): editing already-executed text (not
/// a pure append) must actually drop the old session's bindings, not
/// just log that it would. Proven in three rounds rather than two, since
/// a two-round version can't tell "invalidated" apart from "reused" when
/// the edited text alone is self-consistent either way: round 1 defines
/// `y`; round 2's edit removes it (not an append of round 1 - the rule
/// invalidates); round 3 appends a bare reference to `y` onto round 2's
/// text. If invalidation actually happened, round 3 runs against the
/// fresh session round 2 started, which never saw `y` - undefined. If a
/// bug silently reused round 1's session instead, `y` would still resolve
/// to 59, and this test would catch that regression.
[<Fact>]
let ``persistent mode: editing already-executed text invalidates the session, dropping its bindings`` () =
    task {
        let workDir = tempWorkingDirectory ()
        let key = "persistent-" + System.Guid.NewGuid().ToString("N")

        try
            let round1 =
                task {
                    let session = SessionStore.getOrCreate key workDir "let x = 41\nlet y = 59"
                    return Evaluation.evaluate session "let x = 41\nlet y = 59"
                }

            let! round1Outcome = round1 |> withTimeout 45.0
            Assert.True(round1Outcome.Exception.IsNone)

            // Not a pure append of round 1's text: the "let y = 59" line
            // is gone, not merely followed by more text.
            let round2 =
                task {
                    let session = SessionStore.getOrCreate key workDir "let x = 41"
                    return Evaluation.evaluate session "let x = 41"
                }

            let! round2Outcome = round2 |> withTimeout 45.0
            Assert.True(round2Outcome.Exception.IsNone)

            // A pure append of round 2's (post-invalidation) text.
            let round3 =
                task {
                    let session = SessionStore.getOrCreate key workDir "let x = 41\ny"
                    return Evaluation.evaluate session "let x = 41\ny"
                }

            let! round3Outcome = round3 |> withTimeout 45.0

            Assert.True(round3Outcome.Exception.IsSome, "expected 'y' to be undefined against the fresh, post-invalidation session")
            Assert.NotEmpty(round3Outcome.Diagnostics)
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

            let! firstOutcome = first |> withTimeout 45.0
            Assert.True(firstOutcome.Exception.IsNone)

            let second =
                task {
                    let session = FsiSession.create workDir
                    return Evaluation.evaluate session "x + 1"
                }

            let! secondOutcome = second |> withTimeout 45.0

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

            let! outcome = work |> withTimeout 45.0

            Assert.NotEmpty(outcome.Diagnostics)
        finally
            cleanupWorkingDirectory workDir
    }

[<Fact>]
let ``evaluate clears previous stdout and stderr between consecutive evaluations in the same session`` () =
    task {
        let workDir = tempWorkingDirectory ()

        try
            let work =
                task {
                    let session = FsiSession.create workDir
                    let firstOutcome = Evaluation.evaluate session """printfn "first_output"; 1"""
                    let secondOutcome = Evaluation.evaluate session """printfn "second_output"; 2"""
                    return (firstOutcome, secondOutcome)
                }

            let! (firstOutcome, secondOutcome) = work |> withTimeout 45.0

            Assert.Contains("first_output", firstOutcome.Output)
            Assert.DoesNotContain("second_output", firstOutcome.Output)

            Assert.Contains("second_output", secondOutcome.Output)
            Assert.DoesNotContain("first_output", secondOutcome.Output)
        finally
            cleanupWorkingDirectory workDir
    }
