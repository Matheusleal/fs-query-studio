module MariaDbConnectorTests

open System.Threading
open Xunit
open Testcontainers.MariaDb
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors

type MariaDbContainerFixture() =
    let container = MariaDbBuilder("mariadb:11.4").Build()

    member _.ConnectionString = container.GetConnectionString()

    interface IAsyncLifetime with
        member _.InitializeAsync() = container.StartAsync()
        member _.DisposeAsync() = container.DisposeAsync().AsTask()

[<CollectionDefinition("MariaDb container")>]
type MariaDbCollection() =
    interface ICollectionFixture<MariaDbContainerFixture>

[<Collection("MariaDb container")>]
[<Trait("Category", "RequiresDocker")>]
type MariaDbConnectorTests(fixture: MariaDbContainerFixture) =

    let seedOrders () =
        task {
            let connector = MariaDbConnector(fixture.ConnectionString)

            connector.ExecuteQuery
                "drop table if exists orders; create table orders (id int primary key, customer_id int, total decimal(10,2))"
                []
            |> ignore

            connector.ExecuteQuery
                "insert into orders (id, customer_id, total) values (1, 42, 100.0), (2, 43, 50.0)"
                []
            |> ignore

            return connector
        }

    [<Fact>]
    member _.``ExecuteQuery returns only the rows matching a bound parameter, reading through a live MariaDbDataReader``
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
            Assert.Equal(1, System.Convert.ToInt32((List.exactlyOne rows).[0]))
            Assert.Equal(42, System.Convert.ToInt32((List.exactlyOne rows).[1]))
        }

    [<Fact>]
    member _.``TestConnectionAsync returns true for the running container`` () =
        task {
            let connector = MariaDbConnector(fixture.ConnectionString) :> IConnector

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
    member _.``MariaDbConnector reports its Kind as MariaDb`` () =
        let connector = MariaDbConnector(fixture.ConnectionString) :> IConnector

        Assert.Equal(ConnectorKind.MariaDb, connector.Kind)
