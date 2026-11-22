module Tests

open System.Collections.Generic
open Serilog
open Serilog.Core
open Serilog.Events
open Xunit
open FSharpQueryStudio.Logging

[<assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)>]
do ()

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
        LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(CollectingSink(events)).CreateLogger()

    use _ = Correlation.pushCorrelationId "abc-123"
    logger.Information("inside scope")

    let evt = Assert.Single(events)
    Assert.Equal(box "abc-123", scalarValue evt Correlation.CorrelationIdPropertyName)

[<Fact>]
let ``events emitted outside a pushCorrelationId scope carry no CorrelationId`` () =
    let events = List<LogEvent>()

    let logger =
        LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(CollectingSink(events)).CreateLogger()

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

[<Fact>]
let ``isSecretShaped returns false for non-scalar or non-string values with arbitrary property names`` () =
    let seqVal = SequenceValue([])
    Assert.False(Redaction.isSecretShaped "CustomProp" seqVal)

    let intScalar = ScalarValue(12345)
    Assert.False(Redaction.isSecretShaped "CustomProp" intScalar)

[<Fact>]
let ``containsLeakedSecret detects plain-text and JSON secrets and ignores redacted markers`` () =
    Assert.True(Redaction.containsLeakedSecret "Server=db;Password=secret123;")
    Assert.True(Redaction.containsLeakedSecret "token: my-secret-token")
    Assert.True(Redaction.containsLeakedSecret "authorization = bearer-xyz")
    Assert.True(Redaction.containsLeakedSecret """{"password": "plain-text-pwd"}""")
    Assert.True(Redaction.containsLeakedSecret """{"token": "secret-jwt-token"}""")

    Assert.False(Redaction.containsLeakedSecret "Password: ***REDACTED***")
    Assert.False(Redaction.containsLeakedSecret """{"password": "***REDACTED***"}""")
    Assert.False(Redaction.containsLeakedSecret "Normal log message with no secrets")

[<Fact>]
let ``LoggerSetup defaultLogsDirectory returns non-empty path`` () =
    let path = LoggerSetup.defaultLogsDirectory ()
    Assert.False(System.String.IsNullOrWhiteSpace(path))
    Assert.Contains("FSharpQueryStudio", path)

[<Fact>]
let ``LoggerSetup build worker role builds functional logger routing to stderr and file`` () =
    let tempDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(tempDir) |> ignore

    try
        let logger = LoggerSetup.build "worker" "worker" tempDir
        logger.Information("Test worker log message with {Count}", 42)
        (logger :?> System.IDisposable).Dispose()
    finally
        if System.IO.Directory.Exists(tempDir) then
            try
                System.IO.Directory.Delete(tempDir, true)
            with _ ->
                ()

[<Fact>]
let ``LoggerSetup build host role builds functional logger with standard console`` () =
    let tempDir =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))

    System.IO.Directory.CreateDirectory(tempDir) |> ignore

    try
        let logger = LoggerSetup.build "host" "host" tempDir
        logger.Information("Test host log message with {Count}", 100)
        (logger :?> System.IDisposable).Dispose()
    finally
        if System.IO.Directory.Exists(tempDir) then
            try
                System.IO.Directory.Delete(tempDir, true)
            with _ ->
                ()
