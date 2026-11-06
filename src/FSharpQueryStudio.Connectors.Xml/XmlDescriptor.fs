namespace FSharpQueryStudio.Connectors

open System
open System.Text.Json
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security

type XmlConnectorFactory() =
    interface IConnectorFactory with
        member _.Create(configuration: JsonElement, _: ISecretStore, _: string option) : IConnector =
            let filePath =
                match JsonElementHelpers.tryGetString "filePath" configuration with
                | Some fp -> fp
                | None ->
                    if configuration.ValueKind = JsonValueKind.String then configuration.GetString()
                    else configuration.GetRawText().Trim('"', '\'')
            let cleanPath = if String.IsNullOrWhiteSpace filePath then "" else filePath.Trim().Trim('"', '\'')
            let rowElement =
                JsonElementHelpers.tryGetString "rowElement" configuration
                |> Option.filter (fun s -> not (String.IsNullOrWhiteSpace s))
            let includeAttributes =
                match JsonElementHelpers.tryGetBool "includeAttributes" configuration with
                | Some b -> b
                | None -> true
            let flattenNested =
                match JsonElementHelpers.tryGetBool "flattenNested" configuration with
                | Some b -> b
                | None -> true
            let columnTypes = JsonElementHelpers.tryGetMap "columnTypes" configuration
            let columnTransforms = JsonElementHelpers.tryGetMap "columnTransforms" configuration
            let opts =
                { RowElement = rowElement
                  IncludeAttributes = includeAttributes
                  FlattenNested = flattenNested
                  ColumnTypes = columnTypes
                  ColumnTransforms = columnTransforms }
            XmlConnector(cleanPath, opts) :> IConnector

type XmlDescriptor() =
    static let instance = XmlDescriptor()
    static let formFields =
        [ { Name = "filePath"
            Label = "XML File Path (.xml)"
            Type = ConnectorFieldType.File
            Required = true
            Placeholder = Some "/path/to/data.xml"
            DefaultValue = None
            SecretHandling = SecretHandling.None }
          { Name = "rowElement"
            Label = "Row Element Tag (optional)"
            Type = ConnectorFieldType.String
            Required = false
            Placeholder = Some "Leave blank to auto-discover all collections"
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
        member _.KindId = "Xml"
        member _.DisplayName = "XML File"
        member _.Category = ConnectorCategory.Xml
        member _.Icon = "code"
        member _.FormFields = formFields
        member _.ValidateConfiguration(configuration: JsonElement) : Result<unit, string> =
            ConnectorDescriptor.validateRequiredFields formFields configuration
        member _.Factory = XmlConnectorFactory() :> IConnectorFactory
        member _.EntityGenerator = None

