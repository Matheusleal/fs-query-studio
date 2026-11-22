module SqlServerConnectorTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Testcontainers.MsSql
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors
open Microsoft.Data.SqlClient

type SqlServerOrderRecord =
    {
        Id: int
        CustomerId: int
        Total: decimal
    }

/// One SQL Server container shared across every test in this file (via
/// `SqlServerCollection` below) - Section 10's "test container", started
/// once per test run rather than per test.
type MsSqlContainerFixture() =
    let container = MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build()

    member _.ConnectionString = container.GetConnectionString()

    interface IAsyncLifetime with
        member _.InitializeAsync() = container.StartAsync()
        member _.DisposeAsync() = container.DisposeAsync().AsTask()

[<CollectionDefinition("SqlServer container")>]
type SqlServerCollection() =
    interface ICollectionFixture<MsSqlContainerFixture>

[<Collection("SqlServer container")>]
[<Trait("Category", "RequiresDocker")>]
type SqlServerConnectorTests(fixture: MsSqlContainerFixture) =

    let seedOrders () =
        task {
            let connector = SqlServerConnector(fixture.ConnectionString)

            connector.ExecuteQuery
                "if object_id('Orders', 'U') is not null drop table Orders; create table Orders (Id int primary key, CustomerId int, Total decimal(10,2))"
                []
            |> ignore

            connector.ExecuteQuery "insert into Orders (Id, CustomerId, Total) values (1, 42, 100.0), (2, 43, 50.0)" []
            |> ignore

            return connector
        }

    [<Fact>]
    member _.``ExecuteQuery returns only the rows matching a bound parameter, reading through a live SqlDataReader``() =
        task {
            let! connector = seedOrders ()

            let result =
                connector.ExecuteQuery
                    "select Id, CustomerId, Total from Orders where CustomerId = @customerId"
                    [ "@customerId", box 42 ]

            Assert.Equal<string list>([ "Id"; "CustomerId"; "Total" ], result.Columns)
            let rows = result.Rows |> Seq.toList
            Assert.Single(rows) |> ignore
            Assert.Equal(box 1, (List.exactlyOne rows).[0])
            Assert.Equal(box 42, (List.exactlyOne rows).[1])
        }

    [<Fact>]
    member _.``TestConnectionAsync returns true for the running container``() =
        task {
            let connector = SqlServerConnector(fixture.ConnectionString) :> IConnector

            let! connected = connector.TestConnectionAsync(CancellationToken.None)

            Assert.True(connected)
        }

    [<Fact>]
    member _.``TestConnectionAsync returns false for an invalid connection string``() =
        task {
            let connector =
                SqlServerConnector(
                    "Server=127.0.0.1,9999;Database=NonExistent;User Id=sa;Password=WrongPassword123!;Connect Timeout=1;"
                )
                :> IConnector

            let! connected = connector.TestConnectionAsync(CancellationToken.None)
            Assert.False(connected)
        }

    [<Fact>]
    member _.``DiscoverSchemaAsync reports the Orders table and its columns``() =
        task {
            let! connector = seedOrders ()
            let asInterface = connector :> IConnector

            let! tables = asInterface.DiscoverSchemaAsync(CancellationToken.None)

            let orders =
                tables |> List.find (fun t -> t.Name = "Orders" || t.Name = "master.Orders")

            let columnNames = orders.Columns |> List.map (fun c -> c.Name) |> List.sort
            Assert.Equal<string list>([ "CustomerId"; "Id"; "Total" ], columnNames)
        }

    [<Fact>]
    member _.``DiscoverSchemaAsync discovers tables in non-dbo schemas with schema prefix``() =
        task {
            let connector = SqlServerConnector(fixture.ConnectionString)

            connector.ExecuteQuery
                "if not exists (select * from sys.schemas where name = 'custom') exec('create schema custom'); \
                 if object_id('custom.Items', 'U') is not null drop table custom.Items; \
                 create table custom.Items (ItemId int primary key, Title nvarchar(50))"
                []
            |> ignore

            let! tables = (connector :> IConnector).DiscoverSchemaAsync(CancellationToken.None)

            let customItem =
                tables
                |> List.tryFind (fun t -> t.Name = "custom.Items" || t.Name = "master.custom.Items")

            Assert.True(customItem.IsSome)
            let colNames = customItem.Value.Columns |> List.map (fun c -> c.Name) |> List.sort
            Assert.Equal<string list>([ "ItemId"; "Title" ], colNames)
            let idCol = customItem.Value.Columns |> List.find (fun c -> c.Name = "ItemId")
            Assert.Equal("int", idCol.DataType)
        }

    [<Fact>]
    member _.``SqlServerConnector reports its Kind as SqlServer``() =
        let connector = SqlServerConnector(fixture.ConnectionString) :> IConnector

        Assert.Equal(ConnectorKind.SqlServer, connector.Kind)

    [<Fact>]
    member _.``ConnectionString returns the configured connection string directly and via IQueryableConnector``() =
        let connector = SqlServerConnector(fixture.ConnectionString)
        Assert.Equal(fixture.ConnectionString, connector.ConnectionString)
        let asQueryable = connector :> IQueryableConnector
        Assert.Equal(fixture.ConnectionString, asQueryable.ConnectionString)

    [<Fact>]
    member _.``BindParameters handles null parameter values as DBNull``() =
        use cmd = new SqlCommand()
        SqlServerConnector.BindParameters(cmd, [ "@nullParam", null; "@validParam", box 123 ])
        Assert.Equal(2, cmd.Parameters.Count)
        Assert.Equal(box DBNull.Value, cmd.Parameters.["@nullParam"].Value)
        Assert.Equal(box 123, cmd.Parameters.["@validParam"].Value)

    [<Fact>]
    member _.``ExecuteQuery rows sequence can be re-enumerated multiple times``() =
        task {
            let! connector = seedOrders ()

            let result =
                connector.ExecuteQuery "select Id, CustomerId, Total from Orders order by Id" []

            let pass1 = result.Rows |> Seq.toList
            let pass2 = result.Rows |> Seq.toList
            Assert.Equal(2, pass1.Length)
            Assert.Equal(pass1.Length, pass2.Length)
            Assert.Equal(box 1, pass1.[0].[0])
            Assert.Equal(box 1, pass2.[0].[0])
        }

    [<Fact>]
    member _.``ExecuteReaderStreamWithColumns and ExecuteReaderStream map rows directly to typed records``() =
        task {
            let! connector = seedOrders ()

            let rowsWithCols =
                connector.ExecuteReaderStreamWithColumns
                    "select Id, CustomerId, Total from Orders order by Id"
                    []
                    (fun cols record ->
                        Assert.Equal<string list>([ "Id"; "CustomerId"; "Total" ], cols)

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
                    "select Id, CustomerId, Total from Orders where CustomerId = @cid"
                    [ "@cid", box 43 ]
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
                streaming.ExecuteReaderStreamWithColumns "select Id from Orders where Id = 1" [] (fun _ record ->
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
                queryable.ExecuteQuery "select Total from Orders where Id = @id" [ "@id", box 1 ]

            let rows = result.Rows |> Seq.toList
            Assert.Single(rows) |> ignore
            Assert.Equal(100.0M, Convert.ToDecimal(rows.[0].[0]))
        }
