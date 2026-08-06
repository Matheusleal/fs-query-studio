module PostgreSqlConnectorTests

open System.Threading
open Xunit
open Testcontainers.PostgreSql
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors

/// One PostgreSQL container shared across every test in this file (via
/// `PostgreSqlCollection` below) - Section 10's "test container", started
/// once per test run rather than per test.
type PostgreSqlContainerFixture() =
    let container = PostgreSqlBuilder("postgres:16-alpine").Build()

    member _.ConnectionString = container.GetConnectionString()

    interface IAsyncLifetime with
        member _.InitializeAsync() = container.StartAsync()
        member _.DisposeAsync() = container.DisposeAsync().AsTask()

[<CollectionDefinition("PostgreSql container")>]
type PostgreSqlCollection() =
    interface ICollectionFixture<PostgreSqlContainerFixture>

[<Collection("PostgreSql container")>]
[<Trait("Category", "RequiresDocker")>]
type PostgreSqlConnectorTests(fixture: PostgreSqlContainerFixture) =

    let seedOrders () =
        task {
            let connector = PostgreSqlConnector(fixture.ConnectionString)

            connector.ExecuteQuery
                "drop table if exists orders; create table orders (id int primary key, customer_id int, total numeric(10,2))"
                []
            |> ignore

            connector.ExecuteQuery
                "insert into orders (id, customer_id, total) values (1, 42, 100.0), (2, 43, 50.0)"
                []
            |> ignore

            return connector
        }

    [<Fact>]
    member _.``ExecuteQuery returns only the rows matching a bound parameter, reading through a live NpgsqlDataReader``
        ()
        =
        task {
            let! connector = seedOrders ()

            let result =
                connector.ExecuteQuery "select id, customer_id, total from orders where customer_id = @customerId" [
                    "@customerId", box 42
                ]

            Assert.Equal<string list>([ "id"; "customer_id"; "total" ], result.Columns)
            let rows = result.Rows |> Seq.toList
            Assert.Single(rows) |> ignore
            Assert.Equal(box 1, (List.exactlyOne rows).[0])
            Assert.Equal(box 42, (List.exactlyOne rows).[1])
        }

    [<Fact>]
    member _.``TestConnectionAsync returns true for the running container`` () =
        task {
            let connector = PostgreSqlConnector(fixture.ConnectionString) :> IConnector

            let! connected = connector.TestConnectionAsync(CancellationToken.None)

            Assert.True(connected)
        }

    [<Fact>]
    member _.``DiscoverSchemaAsync reports the orders table and its columns`` () =
        task {
            let! connector = seedOrders ()
            let asInterface = connector :> IConnector

            let! tables = asInterface.DiscoverSchemaAsync(CancellationToken.None)

            let orders = tables |> List.find (fun t -> t.Name = "orders")
            let columnNames = orders.Columns |> List.map (fun c -> c.Name) |> List.sort
            Assert.Equal<string list>([ "customer_id"; "id"; "total" ], columnNames)
        }

    [<Fact>]
    member _.``PostgreSqlConnector reports its Kind as PostgreSql`` () =
        let connector = PostgreSqlConnector(fixture.ConnectionString) :> IConnector

        Assert.Equal(ConnectorKind.PostgreSql, connector.Kind)
