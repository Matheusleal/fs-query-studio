module OracleConnectorTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Testcontainers.Oracle
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors
open Oracle.ManagedDataAccess.Client

type OracleOrderRecord = { Id: int; CustomerId: int; Total: decimal }

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
            Assert.Equal(1, Convert.ToInt32((List.exactlyOne rows).[0]))
            Assert.Equal(42, Convert.ToInt32((List.exactlyOne rows).[1]))
        }

    [<Fact>]
    member _.``ExecuteQuery automatically prepares sql with at-parameters to colon-parameters`` () =
        task {
            let! connector = seedOrders ()

            let result =
                connector.ExecuteQuery "select id, customer_id, total from orders where customer_id = @customerId" [
                    "@customerId", box 42
                ]

            let rows = result.Rows |> Seq.toList
            Assert.Single(rows) |> ignore
            Assert.Equal(1, Convert.ToInt32((List.exactlyOne rows).[0]))
        }

    [<Fact>]
    member _.``TestConnectionAsync returns true for the running container`` () =
        task {
            let connector = OracleConnector(fixture.ConnectionString) :> IConnector

            let! connected = connector.TestConnectionAsync(CancellationToken.None)

            Assert.True(connected)
        }

    [<Fact>]
    member _.``TestConnectionAsync returns false for an invalid connection string`` () =
        task {
            let connector = OracleConnector("Data Source=127.0.0.1:9999/invalid;User Id=invalid;Password=invalid;Connection Timeout=1;") :> IConnector
            let! connected = connector.TestConnectionAsync(CancellationToken.None)
            Assert.False(connected)
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
    member _.``DiscoverSchemaAsync resolves diverse Oracle data types correctly`` () =
        task {
            let connector = OracleConnector(fixture.ConnectionString)
            try
                connector.ExecuteQuery "drop table type_cases purge" [] |> ignore
            with _ -> ()

            connector.ExecuteQuery
                "create table type_cases (c_int number(5), c_big number(15), c_huge number(38), c_dec number(10,2), c_txt varchar2(20))"
                []
            |> ignore

            let! tables = (connector :> IConnector).DiscoverSchemaAsync(CancellationToken.None)
            let typeTable = tables |> List.find (fun t -> t.Name.ToUpperInvariant() = "TYPE_CASES")
            let findType colName =
                (typeTable.Columns |> List.find (fun c -> String.Equals(c.Name, colName, StringComparison.OrdinalIgnoreCase))).DataType

            Assert.Equal("int", findType "c_int")
            Assert.Equal("bigint", findType "c_big")
            Assert.Equal("bigint", findType "c_huge")
            Assert.Equal("decimal", findType "c_dec")
            Assert.Equal("VARCHAR2", findType "c_txt")
        }

    [<Fact>]
    member _.``OracleConnector reports its Kind as Oracle`` () =
        let connector = OracleConnector(fixture.ConnectionString) :> IConnector

        Assert.Equal(ConnectorKind.Oracle, connector.Kind)

    [<Fact>]
    member _.``ConnectionString returns the configured connection string directly and via IQueryableConnector`` () =
        let connector = OracleConnector(fixture.ConnectionString)
        Assert.Equal(fixture.ConnectionString, connector.ConnectionString)
        let asQueryable = connector :> IQueryableConnector
        Assert.Equal(fixture.ConnectionString, asQueryable.ConnectionString)

    [<Fact>]
    member _.``PrepareSql correctly handles null empty plain and at-prefixed sql`` () =
        Assert.Null(OracleConnector.PrepareSql(null))
        Assert.Equal("", OracleConnector.PrepareSql(""))
        Assert.Equal("select 1 from dual", OracleConnector.PrepareSql("select 1 from dual"))
        Assert.Equal("select :a, :b from dual", OracleConnector.PrepareSql("select @a, @b from dual"))

    [<Fact>]
    member _.``BindParameters handles parameters with at-colon-or-no prefix and null value as DBNull`` () =
        use cmd = new OracleCommand()
        OracleConnector.BindParameters(cmd, [ "@atParam", box 1; ":colonParam", box 2; "plainParam", box 3; "nullParam", null ])
        Assert.Equal(4, cmd.Parameters.Count)
        Assert.Equal(box 1, cmd.Parameters.["atParam"].Value)
        Assert.Equal(box 2, cmd.Parameters.["colonParam"].Value)
        Assert.Equal(box 3, cmd.Parameters.["plainParam"].Value)
        Assert.True(isNull cmd.Parameters.["nullParam"].Value || obj.Equals(box DBNull.Value, cmd.Parameters.["nullParam"].Value))

    [<Fact>]
    member _.``ExecuteQuery rows sequence can be re-enumerated multiple times`` () =
        task {
            let! connector = seedOrders ()
            let result = connector.ExecuteQuery "select id, customer_id, total from orders order by id" []
            let pass1 = result.Rows |> Seq.toList
            let pass2 = result.Rows |> Seq.toList
            Assert.Equal(2, pass1.Length)
            Assert.Equal(pass1.Length, pass2.Length)
            Assert.Equal(1, Convert.ToInt32(pass1.[0].[0]))
            Assert.Equal(1, Convert.ToInt32(pass2.[0].[0]))
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
                        Assert.Equal<string list>([ "ID"; "CUSTOMER_ID"; "TOTAL" ], cols |> List.map (fun c -> c.ToUpperInvariant()))
                        { Id = Convert.ToInt32(record.GetValue(0))
                          CustomerId = Convert.ToInt32(record.GetValue(1))
                          Total = Convert.ToDecimal(record.GetValue(2)) })
                |> Seq.toList

            Assert.Equal(2, rowsWithCols.Length)
            Assert.Equal(1, rowsWithCols.[0].Id)
            Assert.Equal(42, rowsWithCols.[0].CustomerId)
            Assert.Equal(100.0M, rowsWithCols.[0].Total)

            let streaming = connector :> IStreamingQueryableConnector
            let rowsFromInterface =
                streaming.ExecuteReaderStream
                    "select id, customer_id, total from orders where customer_id = :cid"
                    [ "cid", box 43 ]
                    (fun record ->
                        { Id = Convert.ToInt32(record.GetValue(0))
                          CustomerId = Convert.ToInt32(record.GetValue(1))
                          Total = Convert.ToDecimal(record.GetValue(2)) })
                |> Seq.toList

            Assert.Single(rowsFromInterface) |> ignore
            Assert.Equal(2, rowsFromInterface.[0].Id)
            Assert.Equal(43, rowsFromInterface.[0].CustomerId)

            let streamWithColsInterface =
                streaming.ExecuteReaderStreamWithColumns
                    "select id from orders where id = 1"
                    []
                    (fun _ record -> Convert.ToInt32(record.GetValue(0)))
                |> Seq.toList
            Assert.Equal<int list>([ 1 ], streamWithColsInterface)
        }

    [<Fact>]
    member _.``IQueryableConnector executes query through interface`` () =
        task {
            let! connector = seedOrders ()
            let queryable = connector :> IQueryableConnector
            let result = queryable.ExecuteQuery "select total from orders where id = :id" [ "id", box 1 ]
            let rows = result.Rows |> Seq.toList
            Assert.Single(rows) |> ignore
            Assert.Equal(100.0M, Convert.ToDecimal(rows.[0].[0]))
        }
