namespace FSharpQueryStudio.Connectors

open System
open System.Text.Json
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security

type MySqlConnectorFactory() =
    interface IConnectorFactory with
        member _.Create(configuration: JsonElement, secretStore: ISecretStore, secretReference: string option) : IConnector =
            let rawConnStr =
                match JsonElementHelpers.tryGetString "connectionString" configuration with
                | Some cs -> cs
                | None ->
                    if configuration.ValueKind = JsonValueKind.String then configuration.GetString()
                    else configuration.GetRawText().Trim('"', '\'')
            let resolvedConnStr = ConnectionStringSecrets.resolve secretStore secretReference rawConnStr
            MySqlConnector(resolvedConnStr) :> IConnector

type MySqlDescriptor() =
    static let instance = MySqlDescriptor()
    static let formFields =
        [ { Name = "connectionString"
            Label = "Connection String"
            Type = ConnectorFieldType.String
            Required = true
            Placeholder = Some "Server=localhost;Port=3306;Database=mydb;User ID=root;Password={password};"
            DefaultValue = None
            SecretHandling = SecretHandling.EmbedInConnectionString "{password}" }
          { Name = "password"
            Label = "Password"
            Type = ConnectorFieldType.Password
            Required = false
            Placeholder = Some "Password for MySQL user"
            DefaultValue = None
            SecretHandling = SecretHandling.IsolatedCredential } ]
    static member Instance = instance

    interface IConnectorDescriptor with
        member _.KindId = "MySql"
        member _.DisplayName = "MySQL"
        member _.Category = ConnectorCategory.Sql
        member _.Icon = "database"
        member _.FormFields = formFields
        member _.ValidateConfiguration(configuration: JsonElement) : Result<unit, string> =
            ConnectorDescriptor.validateRequiredFields formFields configuration
        member _.Factory = MySqlConnectorFactory() :> IConnectorFactory
        member _.EntityGenerator = None
