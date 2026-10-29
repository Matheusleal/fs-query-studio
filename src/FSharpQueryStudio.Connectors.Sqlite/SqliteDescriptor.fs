namespace FSharpQueryStudio.Connectors

open System
open System.Text.Json
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security

type SqliteConnectorFactory() =
    interface IConnectorFactory with
        member _.Create(configuration: JsonElement, _: ISecretStore, _: string option) : IConnector =
            let filePath =
                match JsonElementHelpers.tryGetString "filePath" configuration with
                | Some fp -> fp
                | None ->
                    if configuration.ValueKind = JsonValueKind.String then configuration.GetString()
                    else configuration.GetRawText().Trim('"', '\'')
            let cleanPath = if String.IsNullOrWhiteSpace filePath then "" else filePath.Trim().Trim('"', '\'')
            SqliteConnector(cleanPath) :> IConnector

type SqliteDescriptor() =
    static let instance = SqliteDescriptor()
    static member Instance = instance

    interface IConnectorDescriptor with
        member _.KindId = "Sqlite"
        member _.DisplayName = "SQLite"
        member _.Category = ConnectorCategory.Sql
        member _.Icon = "database"
        member _.FormFields =
            [ { Name = "filePath"
                Label = "Database File Path"
                Type = ConnectorFieldType.File
                Required = true
                Placeholder = Some "/path/to/database.db"
                DefaultValue = None
                SecretHandling = SecretHandling.None } ]
        member _.Factory = SqliteConnectorFactory() :> IConnectorFactory
        member _.EntityGenerator = None
