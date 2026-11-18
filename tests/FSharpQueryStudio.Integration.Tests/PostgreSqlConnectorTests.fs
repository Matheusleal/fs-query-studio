module PostgreSqlConnectorTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Testcontainers.PostgreSql
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors
open Npgsql

type PostgreSqlOrderRecord = { Id: int; CustomerId: int; Total: decimal }

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
    member _.``TestConnectionAsync returns false for an invalid connection string`` () =
        task {
            let connector = PostgreSqlConnector("Host=127.0.0.1;Port=9999;Database=invalid;Username=invalid;Password=invalid;Timeout=1;") :> IConnector
            let! connected = connector.TestConnectionAsync(CancellationToken.None)
            Assert.False(connected)
        }

    [<Fact>]
    member _.``DiscoverSchemaAsync reports the orders table and its columns`` () =
        task {
            let! connector = seedOrders ()
            let asInterface = connector :> IConnector

            let! tables = asInterface.DiscoverSchemaAsync(CancellationToken.None)

            let orders = tables |> List.find (fun t -> t.Name = "orders" || t.Name = "postgres.orders")
            let columnNames = orders.Columns |> List.map (fun c -> c.Name) |> List.sort
            Assert.Equal<string list>([ "customer_id"; "id"; "total" ], columnNames)
        }

    [<Fact>]
    member _.``DiscoverSchemaAsync discovers tables in non-public schemas with schema prefix`` () =
        task {
            let connector = PostgreSqlConnector(fixture.ConnectionString)
            connector.ExecuteQuery
                "create schema if not exists custom; \
                 drop table if exists custom.items; \
                 create table custom.items (item_id int primary key, title text);"
                []
            |> ignore

            let! tables = (connector :> IConnector).DiscoverSchemaAsync(CancellationToken.None)
            let customItem = tables |> List.tryFind (fun t -> t.Name = "custom.items" || t.Name = "postgres.custom.items")
            Assert.True(customItem.IsSome)
            let colNames = customItem.Value.Columns |> List.map (fun c -> c.Name) |> List.sort
            Assert.Equal<string list>([ "item_id"; "title" ], colNames)
            let idCol = customItem.Value.Columns |> List.find (fun c -> c.Name = "item_id")
            Assert.Equal("integer", idCol.DataType)
        }

    [<Fact>]
    member _.``PostgreSqlConnector reports its Kind as PostgreSql`` () =
        let connector = PostgreSqlConnector(fixture.ConnectionString) :> IConnector

        Assert.Equal(ConnectorKind.PostgreSql, connector.Kind)

    [<Fact>]
    member _.``ConnectionString returns the configured connection string directly and via IQueryableConnector`` () =
        let connector = PostgreSqlConnector(fixture.ConnectionString)
        Assert.Equal(fixture.ConnectionString, connector.ConnectionString)
        let asQueryable = connector :> IQueryableConnector
        Assert.Equal(fixture.ConnectionString, asQueryable.ConnectionString)

    [<Fact>]
    member _.``BindParameters handles null parameter values as DBNull`` () =
        use cmd = new NpgsqlCommand()
        PostgreSqlConnector.BindParameters(cmd, [ "@nullParam", null; "@validParam", box 123 ])
        Assert.Equal(2, cmd.Parameters.Count)
        Assert.Equal(box DBNull.Value, cmd.Parameters.["@nullParam"].Value)
        Assert.Equal(box 123, cmd.Parameters.["@validParam"].Value)

    [<Fact>]
    member _.``ExecuteQuery rows sequence can be re-enumerated multiple times`` () =
        task {
            let! connector = seedOrders ()
            let result = connector.ExecuteQuery "select id, customer_id, total from orders order by id" []
            let pass1 = result.Rows |> Seq.toList
            let pass2 = result.Rows |> Seq.toList
            Assert.Equal(2, pass1.Length)
            Assert.Equal(pass1.Length, pass2.Length)
            Assert.Equal(box 1, pass1.[0].[0])
            Assert.Equal(box 1, pass2.[0].[0])
        }

    [<Fact>]
    member _.``ExecuteReaderStreamWithColumns and ExecuteReaderStream map rows directly to typed records`` () =
        task {
            let! connector = seedOrders ()
            let rowsWithCols =
                connector.ExecuteReaderStreamWithColumns
                    "select id, customer_id, total from orders order by id"
                    []
                    (fun cols record ->
                        Assert.Equal<string list>([ "id"; "customer_id"; "total" ], cols)
                        { Id = record.GetInt32(0); CustomerId = record.GetInt32(1); Total = record.GetDecimal(2) })
                |> Seq.toList

            Assert.Equal(2, rowsWithCols.Length)
            Assert.Equal(1, rowsWithCols.[0].Id)
            Assert.Equal(42, rowsWithCols.[0].CustomerId)
            Assert.Equal(100.0M, rowsWithCols.[0].Total)

            let streaming = connector :> IStreamingQueryableConnector
            let rowsFromInterface =
                streaming.ExecuteReaderStream
                    "select id, customer_id, total from orders where customer_id = @cid"
                    [ "@cid", box 43 ]
                    (fun record -> { Id = record.GetInt32(0); CustomerId = record.GetInt32(1); Total = record.GetDecimal(2) })
                |> Seq.toList

            Assert.Single(rowsFromInterface) |> ignore
            Assert.Equal(2, rowsFromInterface.[0].Id)
            Assert.Equal(43, rowsFromInterface.[0].CustomerId)

            let streamWithColsInterface =
                streaming.ExecuteReaderStreamWithColumns
                    "select id from orders where id = 1"
                    []
                    (fun _ record -> record.GetInt32(0))
                |> Seq.toList
            Assert.Equal<int list>([ 1 ], streamWithColsInterface)
        }

    [<Fact>]
    member _.``IQueryableConnector executes query through interface`` () =
        task {
            let! connector = seedOrders ()
            let queryable = connector :> IQueryableConnector
            let result = queryable.ExecuteQuery "select total from orders where id = @id" [ "@id", box 1 ]
            let rows = result.Rows |> Seq.toList
            Assert.Single(rows) |> ignore
            Assert.Equal(100.0M, Convert.ToDecimal(rows.[0].[0]))
        }

    [<Fact>]
    member _.``DiscoverSchemaAsync discovers tables across multiple databases on the same server`` () =
        task {
            use adminConn = new NpgsqlConnection(fixture.ConnectionString)
            do! adminConn.OpenAsync()
            try
                use createDb = adminConn.CreateCommand()
                createDb.CommandText <- "CREATE DATABASE extra_pg_db"
                createDb.ExecuteNonQuery() |> ignore
            with _ -> ()

            let csb = NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            csb.Database <- "extra_pg_db"
            let extraConnector = PostgreSqlConnector(csb.ToString())
            extraConnector.ExecuteQuery
                "create table if not exists branch_orders (id int primary key, customer_id int not null, total numeric(10, 2) not null); \
                 truncate table branch_orders; \
                 insert into branch_orders (id, customer_id, total) values (10, 99, 500.00); \
                 create schema if not exists sales; \
                 create table if not exists sales.invoices (id int primary key, customer_id int not null, total numeric(10, 2) not null); \
                 truncate table sales.invoices; \
                 insert into sales.invoices (id, customer_id, total) values (20, 88, 750.00);"
                []
            |> ignore

            let mainConnector = PostgreSqlConnector(fixture.ConnectionString)
            let! tables = (mainConnector :> IConnector).DiscoverSchemaAsync(CancellationToken.None)
            Assert.Contains(tables, fun t -> t.Name = "extra_pg_db.branch_orders")
            Assert.Contains(tables, fun t -> t.Name = "extra_pg_db.sales.invoices")

            // Cross-database 2-part and 3-part query routing from main connection
            let res2Part = mainConnector.ExecuteQuery "SELECT id, customer_id, total FROM extra_pg_db.branch_orders" []
            let rows2 = res2Part.Rows |> Seq.toList
            Assert.Single(rows2) |> ignore

            let res3Part = mainConnector.ExecuteQuery "SELECT id, customer_id, total FROM extra_pg_db.sales.invoices" []
            let rows3 = res3Part.Rows |> Seq.toList
            Assert.Single(rows3) |> ignore

            let streamed2 =
                mainConnector.ExecuteReaderStreamWithColumns
                    "SELECT id, customer_id, total FROM extra_pg_db.branch_orders"
                    []
                    (fun _ r -> { Id = r.GetInt32(0); CustomerId = r.GetInt32(1); Total = r.GetDecimal(2) })
                |> Seq.toList
            Assert.Single(streamed2) |> ignore
            Assert.Equal(500.00M, streamed2.[0].Total)

            let streamed3 =
                mainConnector.ExecuteReaderStreamWithColumns
                    "SELECT id, customer_id, total FROM extra_pg_db.sales.invoices"
                    []
                    (fun _ r -> { Id = r.GetInt32(0); CustomerId = r.GetInt32(1); Total = r.GetDecimal(2) })
                |> Seq.toList
            Assert.Single(streamed3) |> ignore
            Assert.Equal(750.00M, streamed3.[0].Total)
        }
