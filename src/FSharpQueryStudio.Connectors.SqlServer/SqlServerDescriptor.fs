namespace FSharpQueryStudio.Connectors

open System
open System.Text.Json
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security

type SqlServerConnectorFactory() =
    interface IConnectorFactory with
        member _.Create(configuration: JsonElement, secretStore: ISecretStore, secretReference: string option) : IConnector =
            let rawConnStr =
                match JsonElementHelpers.tryGetString "connectionString" configuration with
                | Some cs -> cs
                | None ->
                    if configuration.ValueKind = JsonValueKind.String then configuration.GetString()
                    else configuration.GetRawText().Trim('"', '\'')
            let resolvedConnStr = ConnectionStringSecrets.resolve secretStore secretReference rawConnStr
            SqlServerConnector(resolvedConnStr) :> IConnector

type SqlServerDescriptor() =
    static let instance = SqlServerDescriptor()
    static member Instance = instance

    interface IConnectorDescriptor with
        member _.KindId = "SqlServer"
        member _.DisplayName = "SQL Server"
        member _.Category = ConnectorCategory.Sql
        member _.Icon = "database"
        member _.FormFields =
            [ { Name = "connectionString"
                Label = "Connection String"
                Type = ConnectorFieldType.String
                Required = true
                Placeholder = Some "Server=localhost;Database=master;User Id=sa;Password={password};TrustServerCertificate=True"
                DefaultValue = None
                SecretHandling = SecretHandling.EmbedInConnectionString "{password}" }
              { Name = "authType"
                Label = "Authentication Type"
                Type = ConnectorFieldType.Select [ "sql"; "windows" ]
                Required = false
                Placeholder = Some "sql"
                DefaultValue = Some "sql"
                SecretHandling = SecretHandling.None }
              { Name = "password"
                Label = "Password"
                Type = ConnectorFieldType.Password
                Required = false
                Placeholder = Some "Password for SQL Authentication"
                DefaultValue = None
                SecretHandling = SecretHandling.IsolatedCredential } ]
        member _.Factory = SqlServerConnectorFactory() :> IConnectorFactory
        member _.EntityGenerator = None
