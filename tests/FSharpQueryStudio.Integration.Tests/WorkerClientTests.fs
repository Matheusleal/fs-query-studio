module WorkerClientTests

open System
open System.Text.Json
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.App
open FSharpQueryStudio.Protocol
open TestHelpers

[<Fact>]
let ``killing the worker process mid-request surfaces a crashed error, and the next request starts a fresh worker`` () =
    task {
        let client = WorkerClient(workerDllPath ())

        // Start a live process via a harmless request.
        let! healthResponse = client.Send(request "w1" "worker/health" "{}") |> withTimeout 30.0
        Assert.True(healthResponse.Result.IsSome)

        let firstPid =
            match client.CurrentProcess with
            | Some p -> p.Id
            | None -> failwith "expected a running worker process"

        // Send another request, then kill the process out from under it.
        let inFlight = client.Send(request "w2" "worker/health" "{}")

        match client.CurrentProcess with
        | Some p -> p.Kill()
        | None -> failwith "expected a running worker process to kill"

        let! crashedResponse = inFlight |> withTimeout 30.0
        Assert.True(crashedResponse.Result.IsNone)

        match crashedResponse.Error with
        | Some err -> Assert.Equal("worker/crashed", err.Code)
        | None -> failwith "expected a crash error"

        // The next request must start a brand-new process, not retry the
        // failed one or reuse the dead handle.
        let! recovered = client.Send(request "w3" "worker/health" "{}") |> withTimeout 30.0
        Assert.True(recovered.Result.IsSome)

        match client.CurrentProcess with
        | Some p -> Assert.NotEqual(firstPid, p.Id)
        | None -> failwith "expected a new running worker process"
    }

let private evaluateRequest (id: string) (source: string) : WorkerRequestFrame =
    { Id = id
      Method = "execution/evaluate"
      Params =
        JsonSerializer.SerializeToElement<EvaluateParams>(
            { Source = source; SessionId = None; Mode = Isolated },
            Serialization.options
        ) }

let private persistentEvaluateRequest (id: string) (source: string) (sessionId: string) : WorkerRequestFrame =
    { Id = id
      Method = "execution/evaluate"
      Params =
        JsonSerializer.SerializeToElement<EvaluateParams>(
            { Source = source
              SessionId = Some sessionId
              Mode = Persistent },
            Serialization.options
        ) }

[<Fact>]
let ``cancelling a cooperative script lets it stop early without killing the worker`` () =
    task {
        let client = WorkerClient(workerDllPath ())

        let script =
            "let mutable i = 0 in (while i < 2_000_000_000 && not (System.IO.File.Exists(\".cancel-requested\")) do i <- i + 1); i"

        let inFlight = client.Send(evaluateRequest "coop-1" script)
        do! Task.Delay(200)

        do! client.Cancel("coop-1", TimeSpan.FromSeconds(15.0))

        let! response = inFlight |> withTimeout 60.0

        Assert.True(response.Result.IsSome)
        Assert.True(response.Error.IsNone)

        let envelope =
            JsonSerializer.Deserialize<ResultEnvelope>(response.Result.Value, Serialization.options)

        Assert.Equal(ResultKind.Scalar, envelope.Kind)

        match envelope.DisplayText with
        | Some text -> Assert.True(int text < 2_000_000_000, $"expected the loop to stop early, got {text}")
        | None -> failwith "expected a display value"

        match client.CurrentProcess with
        | Some p -> Assert.False(p.HasExited, "a cooperative stop should not kill the worker")
        | None -> failwith "expected the worker to still be running"
    }

[<Fact>]
let ``a persistent-mode session's bindings survive across two evaluate calls to a real worker`` () =
    task {
        let client = WorkerClient(workerDllPath ())
        let sessionId = "sess-" + Guid.NewGuid().ToString("N")

        let! firstResponse =
            client.Send(persistentEvaluateRequest "pers-int-1" "let x = 41" sessionId)
            |> withTimeout 30.0

        Assert.True(firstResponse.Result.IsSome)

        // A pure append of round 1's text - E.3's invalidation rule
        // requires this to keep the session (and x's binding) alive.
        let! secondResponse =
            client.Send(persistentEvaluateRequest "pers-int-2" "let x = 41\nx + 1" sessionId)
            |> withTimeout 30.0

        Assert.True(secondResponse.Result.IsSome)

        let envelope =
            JsonSerializer.Deserialize<ResultEnvelope>(secondResponse.Result.Value, Serialization.options)

        Assert.Equal(ResultKind.Scalar, envelope.Kind)
        Assert.Equal(Some "42", envelope.DisplayText)
    }

let private resultsPageRequest (id: string) (sessionId: string) (token: string) : WorkerRequestFrame =
    { Id = id
      Method = "results/page"
      Params =
        JsonSerializer.SerializeToElement<ResultPageRequest>(
            { SessionId = sessionId; ContinuationToken = token },
            Serialization.options
        ) }

[<Fact>]
let ``a large sequence truncates at the preview-row limit, then pages further via the token`` () =
    task {
        let client = WorkerClient(workerDllPath ())

        let! evalResponse = client.Send(evaluateRequest "big1" "[1 .. 510]") |> withTimeout 60.0

        Assert.True(evalResponse.Result.IsSome)
        let envelope = JsonSerializer.Deserialize<ResultEnvelope>(evalResponse.Result.Value, Serialization.options)

        Assert.True(envelope.IsTruncated)
        Assert.Equal(500, envelope.Rows.Length)

        let token =
            match envelope.ContinuationToken with
            | Some t -> t
            | None -> failwith "expected a continuation token from the worker"

        let! pageResponse =
            client.Send(resultsPageRequest "big1-page1" "s1" token) |> withTimeout 60.0

        Assert.True(pageResponse.Result.IsSome)
        let page = JsonSerializer.Deserialize<ResultPage>(pageResponse.Result.Value, Serialization.options)

        Assert.Equal(10, page.Rows.Length)
        Assert.False(page.IsTruncated)
        Assert.True(page.ContinuationToken.IsNone)
    }

[<Fact>]
let ``a results/page request with an unknown or expired token returns a structured error, not a silent re-query`` () =
    task {
        let client = WorkerClient(workerDllPath ())

        let! response =
            client.Send(resultsPageRequest "unknown1" "s1" "not-a-real-token")
            |> withTimeout 60.0

        Assert.True(response.Result.IsNone)

        match response.Error with
        | Some err -> Assert.Equal("results/unknownToken", err.Code)
        | None -> failwith "expected a structured error"
    }

[<Fact>]
let ``cancelling a non-cooperative script forces termination after the grace period`` () =
    task {
        let client = WorkerClient(workerDllPath ())

        let inFlight = client.Send(evaluateRequest "forced-1" "while true do ()")
        do! Task.Delay(200)

        do! client.Cancel("forced-1", TimeSpan.FromMilliseconds(500.0))

        let! response = inFlight |> withTimeout 60.0

        Assert.True(response.Result.IsNone)

        match response.Error with
        | Some err -> Assert.Equal("execution/forciblyStopped", err.Code)
        | None -> failwith "expected a forcibly-stopped error"
    }
