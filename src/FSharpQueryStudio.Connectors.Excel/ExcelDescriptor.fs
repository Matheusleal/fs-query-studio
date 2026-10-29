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
            ExcelConnector(cleanPath) :> IConnector

type ExcelDescriptor() =
    static let instance = ExcelDescriptor()
    static member Instance = instance

    interface IConnectorDescriptor with
        member _.KindId = "Excel"
        member _.DisplayName = "Excel"
        member _.Category = ConnectorCategory.Excel
        member _.Icon = "table"
        member _.FormFields =
            [ { Name = "filePath"
                Label = "Excel File Path (.xlsx)"
                Type = ConnectorFieldType.File
                Required = true
                Placeholder = Some "/path/to/data.xlsx"
                DefaultValue = None
                SecretHandling = SecretHandling.None } ]
        member _.Factory = ExcelConnectorFactory() :> IConnectorFactory
        member _.EntityGenerator = None
