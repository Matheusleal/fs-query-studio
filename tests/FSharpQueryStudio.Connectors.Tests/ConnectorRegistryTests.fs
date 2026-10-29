namespace FSharpQueryStudio.Connectors.Tests

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Connectors
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security

type DummyConnector(name: string) =
    interface IConnector with
        member _.Kind = ConnectorKind.Sqlite
        member _.TestConnectionAsync(_: CancellationToken) = Task.FromResult true
        member _.DiscoverSchemaAsync(_: CancellationToken) = Task.FromResult []

type DummyFactory() =
    interface IConnectorFactory with
        member _.Create(config: JsonElement, _: ISecretStore, _: string option) =
            let doc = config.GetRawText()
            DummyConnector(doc) :> IConnector

type DummyDescriptor(kindId: string, displayName: string) =
    interface IConnectorDescriptor with
        member _.KindId = kindId
        member _.DisplayName = displayName
        member _.Category = ConnectorCategory.Custom "dummy"
        member _.Icon = "database"
        member _.FormFields =
            [ { Name = "path"
                Label = "Database Path"
                Type = ConnectorFieldType.File
                Required = true
                Placeholder = Some "/path/to/db"
                DefaultValue = None
                SecretHandling = SecretHandling.None } ]
        member _.Factory = DummyFactory() :> IConnectorFactory
        member _.EntityGenerator = None

type ConnectorRegistryTests() =

    [<Fact>]
    member _.``Registers and retrieves descriptor case-insensitively`` () =
        let registry = ConnectorRegistry()
        let desc = DummyDescriptor("DuckDb", "DuckDB In-Memory") :> IConnectorDescriptor
        registry.Register desc

        let found = registry.TryGet "duckdb"
        Assert.True found.IsSome
        Assert.Equal("DuckDB In-Memory", found.Value.DisplayName)
        Assert.Equal("DuckDb", found.Value.KindId)

    [<Fact>]
    member _.``AllDescriptors returns all registered items`` () =
        let registry = ConnectorRegistry()
        registry.Register (DummyDescriptor("A", "Connector A"))
        registry.Register (DummyDescriptor("B", "Connector B"))

        let all = registry.AllDescriptors()
        Assert.Equal(2, all.Length)

    [<Fact>]
    member _.``Resolves registered connection definition successfully`` () =
        let registry = ConnectorRegistry()
        registry.Register (DummyDescriptor("Sqlite", "SQLite"))

        use doc = JsonDocument.Parse("""{"filePath": "memory.db"}""")
        let def: ConnectionDefinition =
            { Id = Guid.NewGuid()
              Name = "TestSqlite"
              Kind = ConnectorKind.Sqlite
              Configuration = doc.RootElement.Clone()
              SecretReference = None
              IsTrusted = true }

        let secretStore = InMemorySecretStore() :> ISecretStore
        let connector = registry.Resolve(def, secretStore)
        Assert.NotNull connector
        Assert.IsAssignableFrom<IConnector>(connector)

    [<Fact>]
    member _.``Throws helpful exception when resolving unregistered kind`` () =
        let registry = ConnectorRegistry()
        use doc = JsonDocument.Parse("{}")
        let def: ConnectionDefinition =
            { Id = Guid.NewGuid()
              Name = "UnregisteredConn"
              Kind = ConnectorKind.PostgreSql
              Configuration = doc.RootElement.Clone()
              SecretReference = None
              IsTrusted = true }

        let secretStore = InMemorySecretStore() :> ISecretStore
        let ex = Assert.Throws<Exception>(fun () -> registry.Resolve(def, secretStore) |> ignore)
        Assert.Contains("No connector descriptor registered for kind 'PostgreSql'", ex.Message)
