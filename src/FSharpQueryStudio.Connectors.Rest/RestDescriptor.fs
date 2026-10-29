namespace FSharpQueryStudio.Connectors

open System
open System.Text.Json
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security

type RestConnectorFactory() =
    interface IConnectorFactory with
        member _.Create(configuration: JsonElement, _: ISecretStore, _: string option) : IConnector =
            let url =
                if configuration.ValueKind = JsonValueKind.Object then
                    let mutable urlProp = Unchecked.defaultof<JsonElement>
                    if configuration.TryGetProperty("url", &urlProp) then
                        urlProp.GetString()
                    else
                        ""
                else
                    configuration.GetRawText().Trim('"', '\'')
            let cleanUrl = if String.IsNullOrWhiteSpace url then "" else url.Trim().Trim('"', '\'')
            let options =
                try
                    JsonSerializer.Deserialize<RestRequestOptions>(configuration, Serialization.options)
                with _ ->
                    RestRequestOptions.Get(cleanUrl)
            RestConnector(options) :> IConnector

type RestDescriptor() =
    static let instance = RestDescriptor()
    static let formFields =
        [ { Name = "url"
            Label = "REST Endpoint URL"
            Type = ConnectorFieldType.String
            Required = true
            Placeholder = Some "https://api.example.com/data"
            DefaultValue = None
            SecretHandling = SecretHandling.None } ]
    static member Instance = instance

    interface IConnectorDescriptor with
        member _.KindId = "RestJson"
        member _.DisplayName = "REST (JSON)"
        member _.Category = ConnectorCategory.Rest
        member _.Icon = "cloud"
        member _.FormFields = formFields
        member _.ValidateConfiguration(configuration: JsonElement) : Result<unit, string> =
            ConnectorDescriptor.validateRequiredFields formFields configuration
        member _.Factory = RestConnectorFactory() :> IConnectorFactory
        member _.EntityGenerator = None
