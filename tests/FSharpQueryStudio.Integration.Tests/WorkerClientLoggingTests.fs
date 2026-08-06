module WorkerClientLoggingTests

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading.Tasks
open Serilog
open Serilog.Core
open Serilog.Events
open Xunit
open FSharpQueryStudio.App
open FSharpQueryStudio.Protocol
open TestHelpers

type private CollectingSink(events: List<LogEvent>) =
    interface ILogEventSink with
        member _.Emit(logEvent) = events.Add(logEvent)

/// `WorkerClient` logs through Serilog's static ambient `Log.Logger`, same
/// rationale/safety note as `HandlersLoggingTests.withCollectingLogger` in
/// Worker.Tests - safe here only because this assembly also disables
/// cross-test parallelism (see AssemblyInfo.fs).
let private withCollectingLogger (test: List<LogEvent> -> Task) : Task =
    task {
        let events = List<LogEvent>()

        let testLogger =
            LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(CollectingSink(events)).CreateLogger()

        let original = Log.Logger
        Log.Logger <- testLogger

        try
            do! test events
        finally
            Log.Logger <- original
    }

[<Fact>]
let ``starting a worker process logs a start event with its process id`` () =
    withCollectingLogger (fun events ->
        task {
            let client = WorkerClient(workerDllPath ())
            let! _ = client.Send(request "w1" "worker/health" "{}") |> withTimeout 30.0

            let startEvent =
                events |> Seq.tryFind (fun e -> e.MessageTemplate.Text.Contains("Worker process started"))

            match startEvent with
            | None -> failwith "expected a worker-process-started log event"
            | Some evt -> Assert.True(evt.Properties.ContainsKey("Pid"))
        })

[<Fact>]
let ``a worker process exiting unexpectedly logs a crash warning`` () =
    withCollectingLogger (fun events ->
        task {
            let client = WorkerClient(workerDllPath ())
            let! _ = client.Send(request "w1" "worker/health" "{}") |> withTimeout 30.0
            let inFlight = client.Send(request "w2" "worker/health" "{}")

            match client.CurrentProcess with
            | Some p -> p.Kill()
            | None -> failwith "expected a running worker process to kill"

            let! _crashed = inFlight |> withTimeout 30.0

            let crashEvent =
                events
                |> Seq.tryFind (fun e -> e.MessageTemplate.Text.Contains("Worker process exited unexpectedly"))

            Assert.True(crashEvent.IsSome, "expected a worker-process-crashed log event")
        })

let private evaluateRequest (id: string) (source: string) : WorkerRequestFrame =
    { Id = id
      Method = "execution/evaluate"
      Params =
        JsonSerializer.SerializeToElement<EvaluateParams>(
            { Source = source; SessionId = None; Mode = Isolated },
            Serialization.options
        ) }

[<Fact>]
let ``forcibly terminating a non-cooperative script during Cancel logs a forced-termination event`` () =
    withCollectingLogger (fun events ->
        task {
            let client = WorkerClient(workerDllPath ())
            let inFlight = client.Send(evaluateRequest "forced-log-1" "while true do ()")
            do! Task.Delay(200)

            do! client.Cancel("forced-log-1", TimeSpan.FromMilliseconds(500.0))
            let! _ = inFlight |> withTimeout 60.0

            let forcedEvent =
                events
                |> Seq.tryFind (fun e -> e.MessageTemplate.Text.Contains("did not stop cooperatively"))

            Assert.True(forcedEvent.IsSome, "expected a forced-termination log event")
        })
