namespace FSharpQueryStudio.Connectors

open System
open System.Text.Json
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security

type PostgreSqlConnectorFactory() =
    interface IConnectorFactory with
        member _.Create(configuration: JsonElement, secretStore: ISecretStore, secretReference: string option) : IConnector =
            let rawConnStr =
                match JsonElementHelpers.tryGetString "connectionString" configuration with
                | Some cs -> cs
                | None ->
                    if configuration.ValueKind = JsonValueKind.String then configuration.GetString()
                    else configuration.GetRawText().Trim('"', '\'')
            let resolvedConnStr = ConnectionStringSecrets.resolve secretStore secretReference rawConnStr
            PostgreSqlConnector(resolvedConnStr) :> IConnector

type PostgreSqlDescriptor() =
    static let instance = PostgreSqlDescriptor()
    static member Instance = instance

    interface IConnectorDescriptor with
        member _.KindId = "PostgreSql"
        member _.DisplayName = "PostgreSQL"
        member _.Category = ConnectorCategory.Sql
        member _.Icon = "database"
        member _.FormFields =
            [ { Name = "connectionString"
                Label = "Connection String"
                Type = ConnectorFieldType.String
                Required = true
                Placeholder = Some "Host=localhost;Database=postgres;Username=postgres;Password={password}"
                DefaultValue = None
                SecretHandling = SecretHandling.EmbedInConnectionString "{password}" }
              { Name = "password"
                Label = "Password"
                Type = ConnectorFieldType.Password
                Required = false
                Placeholder = Some "Password for PostgreSQL user"
                DefaultValue = None
                SecretHandling = SecretHandling.IsolatedCredential } ]
        member _.Factory = PostgreSqlConnectorFactory() :> IConnectorFactory
        member _.EntityGenerator = None
