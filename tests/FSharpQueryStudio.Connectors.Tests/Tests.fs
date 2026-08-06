module Tests

open System.Threading
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors

/// A minimal fake proving `IConnector` is satisfiable by any connector
/// kind without pulling in a real driver — the contract every C.5-C.10
/// connector implementation has to meet.
type private FakeConnector(kind: ConnectorKind, tables: ConnectorTable list) =
    interface IConnector with
        member _.Kind = kind
        member _.TestConnectionAsync(_ct: CancellationToken) = Task.FromResult true
        member _.DiscoverSchemaAsync(_ct: CancellationToken) = Task.FromResult tables

[<Fact>]
let ``a fake connector satisfies the IConnector contract`` () =
    task {
        let tables = [ { Name = "Orders"; Columns = [ { Name = "Id"; DataType = "int" } ] } ]
        let connector = FakeConnector(ConnectorKind.Sqlite, tables) :> IConnector

        Assert.Equal(ConnectorKind.Sqlite, connector.Kind)

        let! connected = connector.TestConnectionAsync(CancellationToken.None)
        Assert.True(connected)

        let! schema = connector.DiscoverSchemaAsync(CancellationToken.None)
        Assert.Equal<ConnectorTable list>(tables, schema)
    }
