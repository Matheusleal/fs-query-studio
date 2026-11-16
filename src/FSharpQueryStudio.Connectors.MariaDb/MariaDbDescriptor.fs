namespace FSharpQueryStudio.Connectors

open System
open System.Text.Json
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security

type MariaDbConnectorFactory() =
    interface IConnectorFactory with
        member _.Create(configuration: JsonElement, secretStore: ISecretStore, secretReference: string option) : IConnector =
            let rawConnStr =
                match JsonElementHelpers.tryGetString "connectionString" configuration with
                | Some cs -> cs
                | None ->
                    if configuration.ValueKind = JsonValueKind.String then configuration.GetString()
                    else configuration.GetRawText().Trim('"', '\'')
            let resolvedConnStr = ConnectionStringSecrets.resolve secretStore secretReference rawConnStr
            MariaDbConnector(resolvedConnStr) :> IConnector

type MariaDbDescriptor() =
    static let instance = MariaDbDescriptor()
    static let formFields =
        [ { Name = "connectionString"
            Label = "Connection String"
            Type = ConnectorFieldType.String
            Required = true
            Placeholder = Some "Server=localhost;Port=3307;Database=mydb;User ID=root;Password={password};"
            DefaultValue = None
            SecretHandling = SecretHandling.EmbedInConnectionString "{password}" }
          { Name = "password"
            Label = "Password"
            Type = ConnectorFieldType.Password
            Required = false
            Placeholder = Some "Password for MariaDB user"
            DefaultValue = None
            SecretHandling = SecretHandling.IsolatedCredential } ]
    static member Instance = instance

    interface IConnectorDescriptor with
        member _.KindId = "MariaDb"
        member _.DisplayName = "MariaDB"
        member _.Category = ConnectorCategory.Sql
        member _.Icon = "database"
        member _.FormFields = formFields
        member _.ValidateConfiguration(configuration: JsonElement) : Result<unit, string> =
            ConnectorDescriptor.validateRequiredFields formFields configuration
        member _.Factory = MariaDbConnectorFactory() :> IConnectorFactory
        member _.EntityGenerator = None
