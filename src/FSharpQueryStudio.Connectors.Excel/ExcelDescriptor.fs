namespace FSharpQueryStudio.Connectors

open System
open System.Text.Json
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security

type ExcelConnectorFactory() =
    interface IConnectorFactory with
        member _.Create(configuration: JsonElement, _: ISecretStore, _: string option) : IConnector =
            let filePath =
                match JsonElementHelpers.tryGetString "filePath" configuration with
                | Some fp -> fp
                | None ->
                    if configuration.ValueKind = JsonValueKind.String then configuration.GetString()
                    else configuration.GetRawText().Trim('"', '\'')
            let cleanPath = if String.IsNullOrWhiteSpace filePath then "" else filePath.Trim().Trim('"', '\'')
            let hasHeader =
                match JsonElementHelpers.tryGetBool "hasHeader" configuration with
                | Some b -> b
                | None -> true
            let sheetName = JsonElementHelpers.tryGetString "sheetName" configuration
            let columnTypes = JsonElementHelpers.tryGetMap "columnTypes" configuration
            let columnTransforms = JsonElementHelpers.tryGetMap "columnTransforms" configuration
            let opts =
                { SheetName = sheetName
                  HasHeader = hasHeader
                  ColumnTypes = columnTypes
                  ColumnTransforms = columnTransforms }
            ExcelConnector(cleanPath, opts) :> IConnector

type ExcelDescriptor() =
    static let instance = ExcelDescriptor()
    static let formFields =
        [ { Name = "filePath"
            Label = "Excel File Path (.xlsx)"
            Type = ConnectorFieldType.File
            Required = true
            Placeholder = Some "/path/to/data.xlsx"
            DefaultValue = None
            SecretHandling = SecretHandling.None }
          { Name = "sheetName"
            Label = "Sheet Name"
            Type = ConnectorFieldType.String
            Required = false
            Placeholder = Some "Leave blank for first sheet"
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
        member _.KindId = "Excel"
        member _.DisplayName = "Excel"
        member _.Category = ConnectorCategory.Excel
        member _.Icon = "table"
        member _.FormFields = formFields
        member _.ValidateConfiguration(configuration: JsonElement) : Result<unit, string> =
            ConnectorDescriptor.validateRequiredFields formFields configuration
        member _.Factory = ExcelConnectorFactory() :> IConnectorFactory
        member _.EntityGenerator = None
