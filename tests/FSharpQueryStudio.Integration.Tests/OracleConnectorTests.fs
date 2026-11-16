module OracleConnectorTests

open System.Threading
open Xunit
open Testcontainers.Oracle
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors

type OracleContainerFixture() =
    let container = OracleBuilder("gvenzl/oracle-free:23-slim").Build()

    member _.ConnectionString = container.GetConnectionString()

    interface IAsyncLifetime with
        member _.InitializeAsync() = container.StartAsync()
        member _.DisposeAsync() = container.DisposeAsync().AsTask()

[<CollectionDefinition("Oracle container")>]
type OracleCollection() =
    interface ICollectionFixture<OracleContainerFixture>

[<Collection("Oracle container")>]
[<Trait("Category", "RequiresDocker")>]
type OracleConnectorTests(fixture: OracleContainerFixture) =

    let seedOrders () =
        task {
            let connector = OracleConnector(fixture.ConnectionString)

            try
                connector.ExecuteQuery "drop table orders purge" [] |> ignore
            with _ -> ()

            connector.ExecuteQuery
                "create table orders (id number(10) primary key, customer_id number(10), total number(10,2))"
                []
            |> ignore

            connector.ExecuteQuery
                "insert into orders (id, customer_id, total) values (1, 42, 100.0)"
                []
            |> ignore

            connector.ExecuteQuery
                "insert into orders (id, customer_id, total) values (2, 43, 50.0)"
                []
            |> ignore

            return connector
        }

    [<Fact>]
    member _.``ExecuteQuery returns only the rows matching a bound parameter, reading through a live OracleDataReader``
        ()
        =
        task {
            let! connector = seedOrders ()

            let result =
                connector.ExecuteQuery "select id, customer_id, total from orders where customer_id = :customerId" [
                    ":customerId", box 42
                ]

            Assert.Equal<string list>([ "ID"; "CUSTOMER_ID"; "TOTAL" ], result.Columns |> List.map (fun c -> c.ToUpperInvariant()))
            let rows = result.Rows |> Seq.toList
            Assert.Single(rows) |> ignore
            Assert.Equal(1, System.Convert.ToInt32((List.exactlyOne rows).[0]))
            Assert.Equal(42, System.Convert.ToInt32((List.exactlyOne rows).[1]))
        }

    [<Fact>]
    member _.``TestConnectionAsync returns true for the running container`` () =
        task {
            let connector = OracleConnector(fixture.ConnectionString) :> IConnector

            let! connected = connector.TestConnectionAsync(CancellationToken.None)

            Assert.True(connected)
        }

    [<Fact>]
    member _.``DiscoverSchemaAsync reports the orders table and its columns`` () =
        task {
            let! connector = seedOrders ()
            let asInterface = connector :> IConnector

            let! tables = asInterface.DiscoverSchemaAsync(CancellationToken.None)

            let orders = tables |> List.find (fun t -> t.Name.ToUpperInvariant() = "ORDERS")
            let columnNames = orders.Columns |> List.map (fun c -> c.Name.ToUpperInvariant()) |> List.sort
            Assert.Equal<string list>([ "CUSTOMER_ID"; "ID"; "TOTAL" ], columnNames)
        }

    [<Fact>]
    member _.``OracleConnector reports its Kind as Oracle`` () =
        let connector = OracleConnector(fixture.ConnectionString) :> IConnector

        Assert.Equal(ConnectorKind.Oracle, connector.Kind)
