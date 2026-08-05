module Tests

open System.Diagnostics
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Worker
open TestHelpers

let private startWorker () =
    let psi =
        ProcessStartInfo(
            FileName = "dotnet",
            Arguments = $"\"{workerDllPath ()}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        )

    let proc = new Process(StartInfo = psi)
    proc.Start() |> ignore
    proc

let private roundTrip (proc: Process) (req: WorkerRequestFrame) : Task<WorkerResponseFrame option> =
    task {
        do! NdjsonStream.writeFrame proc.StandardInput req
        return! NdjsonStream.readFrame<WorkerResponseFrame> proc.StandardOutput |> withTimeout 10.0
    }

[<Fact>]
let ``starting the real worker process and requesting worker/health returns a healthy response`` () =
    task {
        use proc = startWorker ()

        try
            let! response = roundTrip proc (request "it-1" "worker/health" "{}")

            match response with
            | Some resp ->
                Assert.Equal("it-1", resp.Id)
                Assert.True(resp.Result.IsSome)
                Assert.True(resp.Error.IsNone)
            | None -> failwith "expected a health response, got end of stream"
        finally
            if not proc.HasExited then
                proc.Kill()
    }

[<Fact>]
let ``a throwing script returns a structured Error result and the worker survives to handle the next request`` () =
    task {
        use proc = startWorker ()

        try
            // A bare `failwith "boom"` never gets a concrete type, so F#'s
            // value restriction stops it before it runs (see the matching
            // characterization test in Execution.Tests) - annotate the type
            // so this actually exercises a runtime exception.
            let evaluateParams = """{"source":"(failwith \"boom\" : int)"}"""
            let! errorResponse = roundTrip proc (request "it-2" "execution/evaluate" evaluateParams)

            match errorResponse with
            | Some resp ->
                Assert.True(resp.Result.IsSome)
                let envelope = System.Text.Json.JsonSerializer.Deserialize<ResultEnvelope>(resp.Result.Value, Serialization.options)
                Assert.Equal(ResultKind.Error, envelope.Kind)
                Assert.Contains("boom", envelope.DisplayText |> Option.defaultValue "")
            | None -> failwith "expected an error result, got end of stream"

            let! healthResponse = roundTrip proc (request "it-3" "worker/health" "{}")

            match healthResponse with
            | Some resp -> Assert.True(resp.Result.IsSome)
            | None -> failwith "worker did not respond to a follow-up request - process likely died"
        finally
            if not proc.HasExited then
                proc.Kill()
    }
