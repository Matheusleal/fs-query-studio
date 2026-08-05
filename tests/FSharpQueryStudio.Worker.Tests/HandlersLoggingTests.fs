module HandlersLoggingTests

open System.Collections.Generic
open System.Text.Json
open Serilog
open Serilog.Core
open Serilog.Events
open Xunit
open FSharpQueryStudio.Logging
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Worker

type private CollectingSink(events: List<LogEvent>) =
    interface ILogEventSink with
        member _.Emit(logEvent) = events.Add(logEvent)

/// `Handlers` logs through Serilog's static ambient `Log.Logger` (same as
/// `Program.fs`'s real read loop), so these tests swap it for a collecting
/// one and restore the original afterward - safe only because this
/// assembly disables cross-test parallelism (see AssemblyInfo.fs).
let private withCollectingLogger (test: List<LogEvent> -> unit) =
    let events = List<LogEvent>()

    let testLogger =
        LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(CollectingSink(events))
            .CreateLogger()

    let original = Log.Logger
    Log.Logger <- testLogger

    try
        test events
    finally
        Log.Logger <- original

let private scalarValue (evt: LogEvent) (propertyName: string) : obj =
    match evt.Properties.[propertyName] with
    | :? ScalarValue as sv -> sv.Value
    | other -> failwithf "expected a ScalarValue for %s, got %A" propertyName other

let private evaluateRequest (id: string) (source: string) : WorkerRequestFrame =
    { Id = id
      Method = "execution/evaluate"
      Params = JsonSerializer.SerializeToElement<EvaluateParams>({ Source = source }, Serialization.options) }

[<Fact>]
let ``runEvaluate logs session creation and execution outcome, both carrying the request's CorrelationId`` () =
    withCollectingLogger (fun events ->
        use _ = Correlation.pushCorrelationId "corr-eval-1"

        match Handlers.beginEvaluate (evaluateRequest "corr-eval-1" "1 + 1") with
        | Choice2Of2 _ -> failwith "expected to reserve the evaluation slot"
        | Choice1Of2 workDir -> Handlers.runEvaluate (evaluateRequest "corr-eval-1" "1 + 1") workDir |> ignore

        let sessionEvent =
            events |> Seq.tryFind (fun e -> e.MessageTemplate.Text.Contains("Creating FSI session"))

        match sessionEvent with
        | None -> failwith "expected a session-creation log event"
        | Some evt -> Assert.Equal(box "corr-eval-1", scalarValue evt Correlation.CorrelationIdPropertyName)

        let outcomeEvent =
            events |> Seq.tryFind (fun e -> e.MessageTemplate.Text.Contains("Execution completed"))

        match outcomeEvent with
        | None -> failwith "expected an execution-completed log event"
        | Some evt ->
            Assert.Equal(box "corr-eval-1", scalarValue evt Correlation.CorrelationIdPropertyName)
            Assert.True(evt.Properties.ContainsKey("ElapsedMs")))

[<Fact>]
let ``handleCancel logs a cancellation-requested event carrying the request's CorrelationId`` () =
    withCollectingLogger (fun events ->
        use _ = Correlation.pushCorrelationId "corr-cancel-1"

        Handlers.handleCancel
            { Id = "corr-cancel-1"
              Method = "execution/cancel"
              Params = JsonDocument.Parse("{}").RootElement }
        |> ignore

        let cancelEvent =
            events |> Seq.tryFind (fun e -> e.MessageTemplate.Text.Contains("Cancellation requested"))

        match cancelEvent with
        | None -> failwith "expected a cancellation-requested log event"
        | Some evt -> Assert.Equal(box "corr-cancel-1", scalarValue evt Correlation.CorrelationIdPropertyName))
