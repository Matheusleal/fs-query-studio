module MySqlConnectorTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Testcontainers.MySql
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors
open MySqlConnector

type MySqlOrderRecord =
    {
        Id: int
        CustomerId: int
        Total: decimal
    }

type MySqlContainerFixture() =
    let container = MySqlBuilder("mysql:8.4").Build()

    member _.ConnectionString = container.GetConnectionString()

    interface IAsyncLifetime with
        member _.InitializeAsync() = container.StartAsync()
        member _.DisposeAsync() = container.DisposeAsync().AsTask()

[<CollectionDefinition("MySql container")>]
type MySqlCollection() =
    interface ICollectionFixture<MySqlContainerFixture>

[<Collection("MySql container")>]
[<Trait("Category", "RequiresDocker")>]
type MySqlConnectorTests(fixture: MySqlContainerFixture) =

    let seedOrders () =
        task {
            let connector = MySqlConnector(fixture.ConnectionString)

            connector.ExecuteQuery
                "drop table if exists orders; create table orders (id int primary key, customer_id int, total decimal(10,2))"
                []
            |> ignore

            connector.ExecuteQuery "insert into orders (id, customer_id, total) values (1, 42, 100.0), (2, 43, 50.0)" []
            |> ignore

            return connector
        }

    [<Fact>]
    member _.``ExecuteQuery returns only the rows matching a bound parameter, reading through a live MySqlDataReader``
        ()
        =
        task {
            let! connector = seedOrders ()

            let result =
                connector.ExecuteQuery
                    "select id, customer_id, total from orders where customer_id = @customerId"
                    [ "@customerId", box 42 ]

            Assert.Equal<string list>([ "id"; "customer_id"; "total" ], result.Columns)
            let rows = result.Rows |> Seq.toList
            Assert.Single(rows) |> ignore
            Assert.Equal(1, Convert.ToInt32((List.exactlyOne rows).[0]))
            Assert.Equal(42, Convert.ToInt32((List.exactlyOne rows).[1]))
        }

    [<Fact>]
    member _.``TestConnectionAsync returns true for the running container``() =
        task {
            let connector = MySqlConnector(fixture.ConnectionString) :> IConnector

            let! connected = connector.TestConnectionAsync(CancellationToken.None)

            Assert.True(connected)
        }

    [<Fact>]
    member _.``TestConnectionAsync returns false for an invalid connection string``() =
        task {
            let connector =
                MySqlConnector("Server=127.0.0.1;Port=9999;Database=invalid;Uid=root;Pwd=invalid;ConnectionTimeout=1;")
                :> IConnector

            let! connected = connector.TestConnectionAsync(CancellationToken.None)
            Assert.False(connected)
        }

    [<Fact>]
    member _.``DiscoverSchemaAsync reports the orders table and its columns``() =
        task {
            let! connector = seedOrders ()
            let asInterface = connector :> IConnector

            let! tables = asInterface.DiscoverSchemaAsync(CancellationToken.None)

            let orders =
                tables |> List.find (fun t -> t.Name = "orders" || t.Name.EndsWith(".orders"))

            let columnNames = orders.Columns |> List.map (fun c -> c.Name) |> List.sort
            Assert.Equal<string list>([ "customer_id"; "id"; "total" ], columnNames)
            let totalCol = orders.Columns |> List.find (fun c -> c.Name = "total")
            Assert.Equal("decimal", totalCol.DataType.ToLowerInvariant())
        }

    [<Fact>]
    member _.``MySqlConnector reports its Kind as MySql``() =
        let connector = MySqlConnector(fixture.ConnectionString) :> IConnector

        Assert.Equal(ConnectorKind.MySql, connector.Kind)

    [<Fact>]
    member _.``ConnectionString returns the configured connection string directly and via IQueryableConnector``() =
        let connector = MySqlConnector(fixture.ConnectionString)
        Assert.Equal(fixture.ConnectionString, connector.ConnectionString)
        let asQueryable = connector :> IQueryableConnector
        Assert.Equal(fixture.ConnectionString, asQueryable.ConnectionString)

    [<Fact>]
    member _.``BindParameters handles parameters with and without at-prefix and null value as DBNull``() =
        use cmd = new MySqlCommand()
        MySqlConnector.BindParameters(cmd, [ "noAt", box 1; "@withAt", box 2; "nullParam", null ])
        Assert.Equal(3, cmd.Parameters.Count)
        Assert.Equal(box 1, cmd.Parameters.["@noAt"].Value)
        Assert.Equal(box 2, cmd.Parameters.["@withAt"].Value)
        Assert.Equal(box DBNull.Value, cmd.Parameters.["@nullParam"].Value)

    [<Fact>]
    member _.``ExecuteQuery rows sequence can be re-enumerated multiple times``() =
        task {
            let! connector = seedOrders ()

            let result =
                connector.ExecuteQuery "select id, customer_id, total from orders order by id" []

            let pass1 = result.Rows |> Seq.toList
            let pass2 = result.Rows |> Seq.toList
            Assert.Equal(2, pass1.Length)
            Assert.Equal(pass1.Length, pass2.Length)
            Assert.Equal(1, Convert.ToInt32(pass1.[0].[0]))
            Assert.Equal(1, Convert.ToInt32(pass2.[0].[0]))
        }

    [<Fact>]
    member _.``ExecuteReaderStreamWithColumns and ExecuteReaderStream map rows directly to typed records``() =
        task {
            let! connector = seedOrders ()

            let rowsWithCols =
                connector.ExecuteReaderStreamWithColumns
                    "select id, customer_id, total from orders order by id"
                    []
                    (fun cols record ->
                        Assert.Equal<string list>([ "id"; "customer_id"; "total" ], cols)

                        {
                            Id = record.GetInt32(0)
                            CustomerId = record.GetInt32(1)
                            Total = record.GetDecimal(2)
                        })
                |> Seq.toList

            Assert.Equal(2, rowsWithCols.Length)
            Assert.Equal(1, rowsWithCols.[0].Id)
            Assert.Equal(42, rowsWithCols.[0].CustomerId)
            Assert.Equal(100.0M, rowsWithCols.[0].Total)

            let streaming = connector :> IStreamingQueryableConnector

            let rowsFromInterface =
                streaming.ExecuteReaderStream
                    "select id, customer_id, total from orders where customer_id = @cid"
                    [ "cid", box 43 ]
                    (fun record ->
                        {
                            Id = record.GetInt32(0)
                            CustomerId = record.GetInt32(1)
                            Total = record.GetDecimal(2)
                        })
                |> Seq.toList

            Assert.Single(rowsFromInterface) |> ignore
            Assert.Equal(2, rowsFromInterface.[0].Id)
            Assert.Equal(43, rowsFromInterface.[0].CustomerId)

            let streamWithColsInterface =
                streaming.ExecuteReaderStreamWithColumns "select id from orders where id = 1" [] (fun _ record ->
                    record.GetInt32(0))
                |> Seq.toList

            Assert.Equal<int list>([ 1 ], streamWithColsInterface)
        }

    [<Fact>]
    member _.``IQueryableConnector executes query through interface``() =
        task {
            let! connector = seedOrders ()
            let queryable = connector :> IQueryableConnector

            let result =
                queryable.ExecuteQuery "select total from orders where id = @id" [ "id", box 1 ]

            let rows = result.Rows |> Seq.toList
            Assert.Single(rows) |> ignore
            Assert.Equal(100.0M, Convert.ToDecimal(rows.[0].[0]))
        }
