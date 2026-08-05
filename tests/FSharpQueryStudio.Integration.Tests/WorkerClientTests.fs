module WorkerClientTests

open Xunit
open FSharpQueryStudio.App
open TestHelpers

[<Fact>]
let ``killing the worker process mid-request surfaces a crashed error, and the next request starts a fresh worker`` () =
    task {
        let client = WorkerClient(workerDllPath ())

        // Start a live process via a harmless request.
        let! healthResponse = client.Send(request "w1" "worker/health" "{}") |> withTimeout 10.0
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

        let! crashedResponse = inFlight |> withTimeout 10.0
        Assert.True(crashedResponse.Result.IsNone)

        match crashedResponse.Error with
        | Some err -> Assert.Equal("worker/crashed", err.Code)
        | None -> failwith "expected a crash error"

        // The next request must start a brand-new process, not retry the
        // failed one or reuse the dead handle.
        let! recovered = client.Send(request "w3" "worker/health" "{}") |> withTimeout 10.0
        Assert.True(recovered.Result.IsSome)

        match client.CurrentProcess with
        | Some p -> Assert.NotEqual(firstPid, p.Id)
        | None -> failwith "expected a new running worker process"
    }
