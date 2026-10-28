module ConnectorCallLoggingTests

open System.Threading
open System.Threading.Tasks
open Serilog
open Serilog.Core
open Serilog.Events
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors

/// Captures every emitted `LogEvent` in memory instead of writing
/// anywhere real, so a test can inspect exactly what got logged.
type private CapturingSink() =
    let events = ResizeArray<LogEvent>()
    member _.Events = events |> List.ofSeq

    interface ILogEventSink with
        member _.Emit(logEvent: LogEvent) = events.Add(logEvent)

type private FakeQueryableConnector(rows: obj array list) =
    interface IConnector with
        member _.Kind = ConnectorKind.Sqlite
        member _.TestConnectionAsync(_ct: CancellationToken) = Task.FromResult true
        member _.DiscoverSchemaAsync(_ct: CancellationToken) = Task.FromResult []

    interface IQueryableConnector with
        member _.ConnectionString = "Data Source=:memory:"
        member _.ExecuteQuery (_sql: string) (_parameters: (string * obj) list) : ConnectorQueryResult =
            { Columns = [ "Name" ]; Rows = rows }

type private FakeResolver(connector: IConnector) =
    interface IConnectionResolver with
        member _.Resolve(_name: string) = connector

[<Fact>]
let ``Connections.sql logs one event with Kind, ElapsedMs, and RowCount, and never the parameter values`` () =
    let sink = CapturingSink()
    let previousLogger = Log.Logger
    Log.Logger <- LoggerConfiguration().WriteTo.Sink(sink).CreateLogger()

    try
        let connector = FakeQueryableConnector([ [| box "Alice" |]; [| box "Bob" |]; [| box "Carol" |] ])
        Connections.configure(FakeResolver(connector))

        let db = Connections.getDatabaseConnection "Sales"
        let result =
            db.rawQuery("select Name from Orders where Ssn = @ssn", [ "@ssn", box "123-45-6789" ])

        // Force full enumeration - the log fires once the caller has
        // actually consumed the (lazily streamed) rows, not eagerly on
        // the call itself. See Connections.sql's own comment for why.
        result.Rows |> Seq.toList |> ignore

        Assert.Equal(1, sink.Events.Length)
        let event = sink.Events.[0]

        let propertyValue (name: string) =
            match event.Properties.TryGetValue(name) with
            | true, v -> Some(v.ToString())
            | false, _ -> None

        Assert.Equal(Some "\"Sqlite\"", propertyValue "Kind")
        Assert.Equal(Some "3", propertyValue "RowCount")
        Assert.True((propertyValue "ElapsedMs").IsSome)

        let allPropertyText =
            event.Properties |> Seq.map (fun kvp -> kvp.Value.ToString()) |> String.concat " "

        Assert.DoesNotContain("123-45-6789", allPropertyText)
        Assert.DoesNotContain("123-45-6789", event.RenderMessage())
    finally
        Log.Logger <- previousLogger
