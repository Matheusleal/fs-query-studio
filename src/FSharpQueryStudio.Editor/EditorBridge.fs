namespace FSharpQueryStudio.Editor

open System
open System.Text.Json
open FSharpQueryStudio.Protocol

/// Encodes/decodes `EditorRequest`s to/from the `EditorEnvelope` wire
/// shape — see Section 6. `decode` never throws: a malformed envelope
/// (unknown `Kind`, or a payload missing required fields) produces a
/// typed `BridgeError` instead of an exception escaping into the caller.
module EditorBridge =

    type BridgeError = { Message: string }

    let private kindOf (request: EditorRequest) : string =
        match request with
        | TextChanged _ -> "textChanged"
        | CompletionRequested _ -> "completionRequested"
        | HoverRequested _ -> "hoverRequested"
        | GoToDefinitionRequested _ -> "goToDefinitionRequested"
        | ExecuteRequested _ -> "executeRequested"
        | CancelRequested -> "cancelRequested"

    let private encodePayload (request: EditorRequest) : JsonElement =
        match request with
        | TextChanged(uri, version, source) ->
            JsonSerializer.SerializeToElement<TextChangedPayload>(
                ({ DocumentUri = uri
                   Version = version
                   Source = source }: TextChangedPayload),
                Serialization.options
            )
        | CompletionRequested(uri, version, line, column) ->
            JsonSerializer.SerializeToElement<CompletionRequestedPayload>(
                ({ DocumentUri = uri
                   Version = version
                   Line = line
                   Column = column }: CompletionRequestedPayload),
                Serialization.options
            )
        | HoverRequested(uri, version, line, column) ->
            JsonSerializer.SerializeToElement<HoverRequestedPayload>(
                ({ DocumentUri = uri
                   Version = version
                   Line = line
                   Column = column }: HoverRequestedPayload),
                Serialization.options
            )
        | GoToDefinitionRequested(uri, version, line, column) ->
            JsonSerializer.SerializeToElement<GoToDefinitionRequestedPayload>(
                ({ DocumentUri = uri
                   Version = version
                   Line = line
                   Column = column }: GoToDefinitionRequestedPayload),
                Serialization.options
            )
        | ExecuteRequested(uri, version, selection) ->
            JsonSerializer.SerializeToElement<ExecuteRequestedPayload>(
                { DocumentUri = uri
                  Version = version
                  Selection = selection },
                Serialization.options
            )
        | CancelRequested -> JsonDocument.Parse("{}").RootElement

    let encode (request: EditorRequest) : EditorEnvelope =
        { Id = Guid.NewGuid().ToString()
          Kind = kindOf request
          Payload = encodePayload request }

    /// Encodes a host -> Monaco notification. There is no matching
    /// `decode` here — the JS side in `editor-host.html` is the only
    /// consumer of this direction.
    let encodeNotification (notification: HostNotification) : EditorEnvelope =
        match notification with
        | DiagnosticsUpdated(uri, diagnostics) ->
            { Id = Guid.NewGuid().ToString()
              Kind = "diagnosticsUpdated"
              Payload =
                JsonSerializer.SerializeToElement<DiagnosticsUpdatedPayload>(
                    { DocumentUri = uri
                      Diagnostics = diagnostics },
                    Serialization.options
                ) }
        | CaretMoved(uri, line, column) ->
            { Id = Guid.NewGuid().ToString()
              Kind = "caretMoved"
              Payload =
                JsonSerializer.SerializeToElement<CaretMovedPayload>(
                    { DocumentUri = uri
                      Line = line
                      Column = column },
                    Serialization.options
                ) }
        | HoverResolved(requestId, uri, hover) ->
            { Id = requestId
              Kind = "hoverResolved"
              Payload =
                JsonSerializer.SerializeToElement<HoverResolvedPayload>(
                    { DocumentUri = uri
                      Hover = hover },
                    Serialization.options
                ) }
        | DefinitionResolved(requestId, uri, location) ->
            { Id = requestId
              Kind = "definitionResolved"
              Payload =
                JsonSerializer.SerializeToElement<DefinitionResolvedPayload>(
                    { DocumentUri = uri
                      Location = location },
                    Serialization.options
                ) }

    let decode (envelope: EditorEnvelope) : Result<EditorRequest, BridgeError> =
        try
            match envelope.Kind with
            | "textChanged" ->
                let p =
                    JsonSerializer.Deserialize<TextChangedPayload>(envelope.Payload, Serialization.options)

                Ok(TextChanged(p.DocumentUri, p.Version, p.Source))
            | "completionRequested" ->
                let p =
                    JsonSerializer.Deserialize<CompletionRequestedPayload>(envelope.Payload, Serialization.options)

                Ok(CompletionRequested(p.DocumentUri, p.Version, p.Line, p.Column))
            | "hoverRequested" ->
                let p =
                    JsonSerializer.Deserialize<HoverRequestedPayload>(envelope.Payload, Serialization.options)

                Ok(HoverRequested(p.DocumentUri, p.Version, p.Line, p.Column))
            | "goToDefinitionRequested" ->
                let p =
                    JsonSerializer.Deserialize<GoToDefinitionRequestedPayload>(envelope.Payload, Serialization.options)

                Ok(GoToDefinitionRequested(p.DocumentUri, p.Version, p.Line, p.Column))
            | "executeRequested" ->
                let p =
                    JsonSerializer.Deserialize<ExecuteRequestedPayload>(envelope.Payload, Serialization.options)

                Ok(ExecuteRequested(p.DocumentUri, p.Version, p.Selection))
            | "cancelRequested" -> Ok CancelRequested
            | unknown -> Result.Error { Message = $"Unknown envelope kind: {unknown}" }
        with ex ->
            Result.Error { Message = $"Malformed payload for kind '{envelope.Kind}': {ex.Message}" }
