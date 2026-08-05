module Tests

open System.Collections.Generic
open Serilog
open Serilog.Core
open Serilog.Events
open Xunit
open FSharpQueryStudio.Logging

type private CollectingSink(events: List<LogEvent>) =
    interface ILogEventSink with
        member _.Emit(logEvent) = events.Add(logEvent)

let private scalarValue (evt: LogEvent) (propertyName: string) : obj =
    match evt.Properties.[propertyName] with
    | :? ScalarValue as sv -> sv.Value
    | other -> failwithf "expected a ScalarValue for %s, got %A" propertyName other

[<Fact>]
let ``pushCorrelationId attaches CorrelationId to events emitted within its scope`` () =
    let events = List<LogEvent>()
    let logger =
        LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(CollectingSink(events))
            .CreateLogger()

    use _ = Correlation.pushCorrelationId "abc-123"
    logger.Information("inside scope")

    let evt = Assert.Single(events)
    Assert.Equal(box "abc-123", scalarValue evt Correlation.CorrelationIdPropertyName)

[<Fact>]
let ``events emitted outside a pushCorrelationId scope carry no CorrelationId`` () =
    let events = List<LogEvent>()
    let logger =
        LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(CollectingSink(events))
            .CreateLogger()

    (use _scope = Correlation.pushCorrelationId "abc-123" in ())
    logger.Information("outside scope")

    let evt = Assert.Single(events)
    Assert.False(evt.Properties.ContainsKey(Correlation.CorrelationIdPropertyName))

[<Fact>]
let ``redacts a property whose name is a known secret key`` () =
    let events = List<LogEvent>()
    let logger =
        LoggerConfiguration()
            .Enrich.With(Redaction.RedactingEnricher())
            .WriteTo.Sink(CollectingSink(events))
            .CreateLogger()

    logger.Information("Calling API with {Authorization}", "Bearer super-secret-token")

    let evt = Assert.Single(events)
    Assert.Equal(box Redaction.RedactedPlaceholder, scalarValue evt "Authorization")

[<Fact>]
let ``redacts a value shaped like a connection string with an embedded password`` () =
    let events = List<LogEvent>()
    let logger =
        LoggerConfiguration()
            .Enrich.With(Redaction.RedactingEnricher())
            .WriteTo.Sink(CollectingSink(events))
            .CreateLogger()

    logger.Information("Connecting with {ConnString}", "Server=db;User Id=sa;Password=Sup3rSecret;")

    let evt = Assert.Single(events)
    Assert.Equal(box Redaction.RedactedPlaceholder, scalarValue evt "ConnString")
    Assert.DoesNotContain("Sup3rSecret", evt.RenderMessage())

[<Fact>]
let ``leaves ordinary properties untouched`` () =
    let events = List<LogEvent>()
    let logger =
        LoggerConfiguration()
            .Enrich.With(Redaction.RedactingEnricher())
            .WriteTo.Sink(CollectingSink(events))
            .CreateLogger()

    logger.Information("Row count {RowCount}", 42)

    let evt = Assert.Single(events)
    Assert.Equal(box 42, scalarValue evt "RowCount")
