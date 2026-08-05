module Tests

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Worker

let private workerDllPath () =
    Path.Combine(AppContext.BaseDirectory, "FSharpQueryStudio.Worker.dll")

let private withTimeout (seconds: float) (work: Task<'T>) : Task<'T> =
    task {
        let! winner = Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds seconds))

        if winner = (work :> Task) then
            return! work
        else
            return failwith $"timed out after {seconds}s waiting for the worker process"
    }

[<Fact>]
let ``starting the real worker process and requesting worker/health returns a healthy response`` () =
    task {
        let psi =
            ProcessStartInfo(
                FileName = "dotnet",
                Arguments = $"\"{workerDllPath ()}\"",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            )

        use proc = new Process(StartInfo = psi)
        proc.Start() |> ignore

        try
            let request: WorkerRequestFrame =
                { Id = "it-1"
                  Method = "worker/health"
                  Params = Text.Json.JsonDocument.Parse("{}").RootElement }

            do! NdjsonStream.writeFrame proc.StandardInput request
            let! response = NdjsonStream.readFrame<WorkerResponseFrame> proc.StandardOutput |> withTimeout 10.0

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
