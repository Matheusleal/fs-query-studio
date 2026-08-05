module HostNotificationTests

open System.Text.Json
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Editor

[<Fact>]
let ``encoding a DiagnosticsUpdated notification produces the diagnosticsUpdated envelope kind`` () =
    let envelope = EditorBridge.encodeNotification (DiagnosticsUpdated("file:///a.fsx", []))

    Assert.Equal("diagnosticsUpdated", envelope.Kind)

[<Fact>]
let ``encoding a DiagnosticsUpdated notification carries the document URI and diagnostics in the payload`` () =
    let diagnostics =
        [ { Message = "type error"
            Severity = DiagnosticSeverity.Error
            StartLine = 0
            StartColumn = 0
            EndLine = 0
            EndColumn = 5
            Code = Some "FS0001" } ]

    let envelope = EditorBridge.encodeNotification (DiagnosticsUpdated("file:///a.fsx", diagnostics))

    let payload =
        JsonSerializer.Deserialize<DiagnosticsUpdatedPayload>(envelope.Payload, Serialization.options)

    Assert.Equal("file:///a.fsx", payload.DocumentUri)
    Assert.Equal<Diagnostic list>(diagnostics, payload.Diagnostics)

[<Fact>]
let ``encoding a CaretMoved notification produces the caretMoved envelope kind and payload`` () =
    let envelope = EditorBridge.encodeNotification (CaretMoved("file:///a.fsx", 3, 7))

    Assert.Equal("caretMoved", envelope.Kind)
    let payload = JsonSerializer.Deserialize<CaretMovedPayload>(envelope.Payload, Serialization.options)
    Assert.Equal("file:///a.fsx", payload.DocumentUri)
    Assert.Equal(3, payload.Line)
    Assert.Equal(7, payload.Column)
