namespace FSharpQueryStudio.Connectors

open System
open System.Text.Json
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security

type JsonConnectorFactory() =
    interface IConnectorFactory with
        member _.Create(configuration: JsonElement, _: ISecretStore, _: string option) : IConnector =
            let filePath =
                match JsonElementHelpers.tryGetString "filePath" configuration with
                | Some fp -> fp
                | None ->
                    if configuration.ValueKind = JsonValueKind.String then configuration.GetString()
                    else configuration.GetRawText().Trim('"', '\'')
            let cleanPath = if String.IsNullOrWhiteSpace filePath then "" else filePath.Trim().Trim('"', '\'')
            let jsonPath =
                JsonElementHelpers.tryGetString "jsonPath" configuration
                |> Option.filter (fun s -> not (String.IsNullOrWhiteSpace s))
            let tableName =
                JsonElementHelpers.tryGetString "tableName" configuration
                |> Option.filter (fun s -> not (String.IsNullOrWhiteSpace s))
            let flattenNested =
                match JsonElementHelpers.tryGetBool "flattenNested" configuration with
                | Some b -> b
                | None -> true
            let columnTypes = JsonElementHelpers.tryGetMap "columnTypes" configuration
            let columnTransforms = JsonElementHelpers.tryGetMap "columnTransforms" configuration
            let opts =
                { TableName = tableName
                  JsonPath = jsonPath
                  FlattenNested = flattenNested
                  ColumnTypes = columnTypes
                  ColumnTransforms = columnTransforms }
            JsonConnector(cleanPath, opts) :> IConnector

type JsonDescriptor() =
    static let instance = JsonDescriptor()
    static let formFields =
        [ { Name = "filePath"
            Label = "JSON File Path (.json)"
            Type = ConnectorFieldType.File
            Required = true
            Placeholder = Some "/path/to/data.json"
            DefaultValue = None
            SecretHandling = SecretHandling.None }
          { Name = "jsonPath"
            Label = "JSON Property Path (optional)"
            Type = ConnectorFieldType.String
            Required = false
            Placeholder = Some "Leave blank to auto-discover all collections (or e.g. data.items)"
            DefaultValue = None
            SecretHandling = SecretHandling.None }
          { Name = "columnTypes"
            Label = "Column Types Override"
            Type = ConnectorFieldType.String
            Required = false
            Placeholder = Some "Col1=decimal, Col2=int"
            DefaultValue = None
            SecretHandling = SecretHandling.None }
          { Name = "columnTransforms"
            Label = "Column Transforms / Formulas"
            Type = ConnectorFieldType.String
            Required = false
            Placeholder = Some "Col1=replace(',', '.'), Col2=trim"
            DefaultValue = None
            SecretHandling = SecretHandling.None } ]
    static member Instance = instance

    interface IConnectorDescriptor with
        member _.KindId = "Json"
        member _.DisplayName = "JSON File"
        member _.Category = ConnectorCategory.Json
        member _.Icon = "json"
        member _.FormFields = formFields
        member _.ValidateConfiguration(configuration: JsonElement) : Result<unit, string> =
            ConnectorDescriptor.validateRequiredFields formFields configuration
        member _.Factory = JsonConnectorFactory() :> IConnectorFactory
        member _.EntityGenerator = None

