namespace FSharpQueryStudio.Connectors

open System
open System.Text.Json
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security

type OracleConnectorFactory() =
    interface IConnectorFactory with
        member _.Create(configuration: JsonElement, secretStore: ISecretStore, secretReference: string option) : IConnector =
            let rawConnStr =
                match JsonElementHelpers.tryGetString "connectionString" configuration with
                | Some cs -> cs
                | None ->
                    if configuration.ValueKind = JsonValueKind.String then configuration.GetString()
                    else configuration.GetRawText().Trim('"', '\'')
            let resolvedConnStr = ConnectionStringSecrets.resolve secretStore secretReference rawConnStr
            OracleConnector(resolvedConnStr) :> IConnector

type OracleDescriptor() =
    static let instance = OracleDescriptor()
    static let formFields =
        [ { Name = "connectionString"
            Label = "Connection String"
            Type = ConnectorFieldType.String
            Required = true
            Placeholder = Some "Data Source=localhost:1521/FREEPDB1;User Id=system;Password={password};"
            DefaultValue = None
            SecretHandling = SecretHandling.EmbedInConnectionString "{password}" }
          { Name = "password"
            Label = "Password"
            Type = ConnectorFieldType.Password
            Required = false
            Placeholder = Some "Password for Oracle user"
            DefaultValue = None
            SecretHandling = SecretHandling.IsolatedCredential } ]
    static member Instance = instance

    interface IConnectorDescriptor with
        member _.KindId = "Oracle"
        member _.DisplayName = "Oracle"
        member _.Category = ConnectorCategory.Sql
        member _.Icon = "database"
        member _.FormFields = formFields
        member _.ValidateConfiguration(configuration: JsonElement) : Result<unit, string> =
            ConnectorDescriptor.validateRequiredFields formFields configuration
        member _.Factory = OracleConnectorFactory() :> IConnectorFactory
        member _.EntityGenerator = None
