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
    proc.ErrorDataReceived.Add(fun _ -> ())
    proc.Start() |> ignore
    proc.BeginErrorReadLine()
    proc

let private roundTrip (proc: Process) (req: WorkerRequestFrame) : Task<WorkerResponseFrame option> =
    task {
        do! NdjsonStream.writeFrame proc.StandardInput req
        let mutable targetResponse: WorkerResponseFrame option = None
        let mutable eof = false
        use cts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds 120.0)
        while targetResponse.IsNone && not eof && not proc.HasExited && not cts.IsCancellationRequested do
            let! line = proc.StandardOutput.ReadLineAsync(cts.Token)
            if isNull line then
                eof <- true
            else
                try
                    use doc = System.Text.Json.JsonDocument.Parse(line)
                    let root = doc.RootElement
                    let mutable methodProp = Unchecked.defaultof<System.Text.Json.JsonElement>
                    if root.TryGetProperty("method", &methodProp) then
                        let reqId = root.GetProperty("id").GetString()
                        let reply = $"{{\"id\":\"{reqId}\",\"result\":{{\"value\":\"\"}},\"error\":null}}"
                        do! proc.StandardInput.WriteLineAsync(reply)
                    else
                        let frame = System.Text.Json.JsonSerializer.Deserialize<WorkerResponseFrame>(line, Serialization.options)
                        if frame.Id = req.Id then
                            targetResponse <- Some frame
                with _ -> ()
        return targetResponse
    }

let private stopWorker (proc: Process) =
    try
        if not proc.HasExited then
            try proc.StandardInput.Close() with _ -> ()
            if not (proc.WaitForExit(2000)) then
                proc.Kill(entireProcessTree = true)
                proc.WaitForExit(5000) |> ignore
    with _ -> ()

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
            stopWorker proc
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
            let evaluateParams =
                """{"source":"(failwith \"boom\" : int)","sessionId":null,"mode":{"Case":"Isolated"},"documentUri":"untitled:Test.fsx","isNotebookCell":false}"""
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
            stopWorker proc
    }
