module SqlServerConnectorTests

open System.Threading
open System.Threading.Tasks
open Xunit
open Testcontainers.MsSql
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors

/// One SQL Server container shared across every test in this file (via
/// `SqlServerCollection` below) - Section 10's "test container", started
/// once per test run rather than per test.
type MsSqlContainerFixture() =
    let container =
        MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build()

    member _.ConnectionString = container.GetConnectionString()

    interface IAsyncLifetime with
        member _.InitializeAsync() = container.StartAsync()
        member _.DisposeAsync() = container.DisposeAsync().AsTask()

[<CollectionDefinition("SqlServer container")>]
type SqlServerCollection() =
    interface ICollectionFixture<MsSqlContainerFixture>

[<Collection("SqlServer container")>]
type SqlServerConnectorTests(fixture: MsSqlContainerFixture) =

    let seedOrders () =
        task {
            let connector = SqlServerConnector(fixture.ConnectionString)

            connector.ExecuteQuery
                "if object_id('Orders', 'U') is not null drop table Orders; create table Orders (Id int primary key, CustomerId int, Total decimal(10,2))"
                []
            |> ignore

            connector.ExecuteQuery
                "insert into Orders (Id, CustomerId, Total) values (1, 42, 100.0), (2, 43, 50.0)"
                []
            |> ignore

            return connector
        }

    [<Fact>]
    member _.``ExecuteQuery returns only the rows matching a bound parameter, reading through a live SqlDataReader``
        ()
        =
        task {
            let! connector = seedOrders ()

            let result =
                connector.ExecuteQuery "select Id, CustomerId, Total from Orders where CustomerId = @customerId" [
                    "@customerId", box 42
                ]

            Assert.Equal<string list>([ "Id"; "CustomerId"; "Total" ], result.Columns)
            let rows = result.Rows |> Seq.toList
            Assert.Single(rows) |> ignore
            Assert.Equal(box 1, (List.exactlyOne rows).[0])
            Assert.Equal(box 42, (List.exactlyOne rows).[1])
        }

    [<Fact>]
    member _.``TestConnectionAsync returns true for the running container`` () =
        task {
            let connector = SqlServerConnector(fixture.ConnectionString) :> IConnector

            let! connected = connector.TestConnectionAsync(CancellationToken.None)

            Assert.True(connected)
        }

    [<Fact>]
    member _.``DiscoverSchemaAsync reports the Orders table and its columns`` () =
        task {
            let! connector = seedOrders ()
            let asInterface = connector :> IConnector

            let! tables = asInterface.DiscoverSchemaAsync(CancellationToken.None)

            let orders = tables |> List.find (fun t -> t.Name = "Orders")
            let columnNames = orders.Columns |> List.map (fun c -> c.Name) |> List.sort
            Assert.Equal<string list>([ "CustomerId"; "Id"; "Total" ], columnNames)
        }

    [<Fact>]
    member _.``SqlServerConnector reports its Kind as SqlServer`` () =
        let connector = SqlServerConnector(fixture.ConnectionString) :> IConnector

        Assert.Equal(ConnectorKind.SqlServer, connector.Kind)
