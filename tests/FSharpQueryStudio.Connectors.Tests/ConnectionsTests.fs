module ConnectionsTests

open System
open System.IO
open System.Text
open System.Threading
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors

type private FakeResolver(connections: Map<string, IConnector>) =
    interface IConnectionResolver with
        member _.Resolve(name: string) =
            match connections.TryFind name with
            | Some connector -> connector
            | None -> failwith $"No connection named '{name}' is configured."

let private tempSqliteWithOrders () : SqliteConnector =
    let path = Path.Combine(Path.GetTempPath(), "fqs-connections-" + Guid.NewGuid().ToString("N") + ".db")
    let connector = SqliteConnector(path)

    connector.ExecuteQuery "create table Orders (Id integer primary key, Name text)" []
    |> ignore

    connector.ExecuteQuery "insert into Orders (Id, Name) values (1, 'Alice'), (2, 'Bob')" []
    |> ignore

    connector

[<Fact>]
let ``Connections.sql binds an OR-1-equals-1-shaped parameter as literal data, never as SQL syntax`` () =
    let sqlite = tempSqliteWithOrders ()
    Connections.configure(FakeResolver(Map.ofList [ "Sales", sqlite :> IConnector ]))

    // If this parameter were ever concatenated into the SQL text instead
    // of bound, this classic injection payload would make the WHERE
    // clause always true and return every row.
    let result =
        Connections.sql "Sales" "select * from Orders where Name = @name" [ "@name", box "x' OR '1'='1" ]

    Assert.Empty(result.Rows)

[<Fact>]
let ``Connections.sql binds a statement-terminator-shaped parameter as literal data, never executing it`` () =
    let sqlite = tempSqliteWithOrders ()
    Connections.configure(FakeResolver(Map.ofList [ "Sales", sqlite :> IConnector ]))

    Connections.sql "Sales" "select * from Orders where Name = @name" [
        "@name", box "x'; DROP TABLE Orders; --"
    ]
    |> ignore

    // The table must still exist and still hold its original two rows -
    // a real DROP TABLE would have removed it.
    let survivingRows = sqlite.ExecuteQuery "select * from Orders" [] |> fun r -> r.Rows |> Seq.toList
    Assert.Equal(2, survivingRows.Length)

[<Fact>]
let ``Connections.sql routes to the resolved connector and returns its rows for a normal query`` () =
    let sqlite = tempSqliteWithOrders ()
    Connections.configure(FakeResolver(Map.ofList [ "Sales", sqlite :> IConnector ]))

    let result = Connections.sql "Sales" "select Name from Orders where Id = @id" [ "@id", box 1 ]

    Assert.Equal(box "Alice", (result.Rows |> Seq.exactlyOne).[0])

[<Fact>]
let ``Connections.sql fails clearly when the named connection isn't a SQL-capable connector`` () =
    let csvPath = Path.Combine(Path.GetTempPath(), "fqs-connections-" + Guid.NewGuid().ToString("N") + ".csv")
    File.WriteAllText(csvPath, "Id\n1\n")
    let csv = CsvConnector(csvPath)
    Connections.configure(FakeResolver(Map.ofList [ "Feed", csv :> IConnector ]))

    Assert.Throws<Exception>(fun () -> Connections.sql "Feed" "select 1" [] |> ignore)
    |> ignore

[<Fact>]
let ``Connections.csv routes to the resolved CSV connector and reads its rows`` () =
    let path = Path.Combine(Path.GetTempPath(), "fqs-connections-" + Guid.NewGuid().ToString("N") + ".csv")
    File.WriteAllText(path, "Id,Name\n1,Sales\n")
    let csv = CsvConnector(path)
    Connections.configure(FakeResolver(Map.ofList [ "Feed", csv :> IConnector ]))

    let result = Connections.csv "Feed"

    Assert.Equal<string list>([ "Id"; "Name" ], result.Columns)
    let rows = result.Rows |> Seq.toList
    Assert.Equal(1, rows.Length)
    Assert.Equal(box "1", rows.[0].[0])
    Assert.Equal(box "Sales", rows.[0].[1])

[<Fact>]
let ``Connections.excel routes to the resolved Excel connector and reads its rows`` () =
    let path = Path.Combine(Path.GetTempPath(), "fqs-connections-" + Guid.NewGuid().ToString("N") + ".xlsx")

    do
        use workbook = new ClosedXML.Excel.XLWorkbook()
        let sheet = workbook.Worksheets.Add("Sheet1")
        sheet.Cell(1, 1).Value <- "Id"
        sheet.Cell(2, 1).Value <- 1
        workbook.SaveAs(path)

    let excel = ExcelConnector(path)
    Connections.configure(FakeResolver(Map.ofList [ "Book", excel :> IConnector ]))

    let result = Connections.excel "Book" None

    Assert.Equal<string list>([ "Id" ], result.Columns)
    let rows = result.Rows |> Seq.toList
    Assert.Equal(1, rows.Length)
    Assert.Equal(box 1.0, rows.[0].[0])

[<Fact>]
let ``Connections.rest routes to the resolved REST connector and sends the request`` () =
    task {
        let listener = new System.Net.HttpListener()
        let port = 38471
        listener.Prefixes.Add($"http://127.0.0.1:{port}/")
        listener.Start()

        let serveTask =
            task {
                let! ctx = listener.GetContextAsync()
                let bytes = Encoding.UTF8.GetBytes("""{"ok":true}""")
                ctx.Response.ContentType <- "application/json"
                do! ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length)
                ctx.Response.Close()
            }

        let rest = RestConnector(RestRequestOptions.Get($"http://127.0.0.1:{port}/health"))
        Connections.configure(FakeResolver(Map.ofList [ "Api", rest :> IConnector ]))

        let! result = Connections.rest "Api" (RestRequestOptions.Get($"http://127.0.0.1:{port}/health")) CancellationToken.None
        do! serveTask
        listener.Stop()
        listener.Close()

        Assert.True(result.GetProperty("ok").GetBoolean())
    }

type OrderRecord = { Id: int; Name: string }
type OrderWithOptions = { Id: int; Name: string; Notes: string option }

[<Fact>]
let ``Connections.getConnectionString returns the ADO.NET connection string for a SQL connector`` () =
    let sqlite = tempSqliteWithOrders ()
    Connections.configure(FakeResolver(Map.ofList [ "Sales", sqlite :> IConnector ]))

    let connStr = Connections.getConnectionString "Sales"
    Assert.Contains("Data Source=", connStr)

[<Fact>]
let ``Connections.query maps rows to strongly-typed records and supports F# query LINQ expressions`` () =
    let sqlite = tempSqliteWithOrders ()
    Connections.configure(FakeResolver(Map.ofList [ "Sales", sqlite :> IConnector ]))

    let orders = Connections.query<OrderRecord> "Sales" "select Id, Name from Orders" []

    let filtered =
        query {
            for o in orders do
            where (o.Id = 2)
            select o.Name
        }
        |> Seq.toList

    Assert.Equal<string list>([ "Bob" ], filtered)

[<Fact>]
let ``ConnectorQueryResult.As maps null/DBNull values to F# None and values to Some`` () =
    let sqlite = tempSqliteWithOrders ()
    sqlite.ExecuteQuery "alter table Orders add column Notes text" [] |> ignore
    sqlite.ExecuteQuery "update Orders set Notes = 'Urgent' where Id = 1" [] |> ignore
    Connections.configure(FakeResolver(Map.ofList [ "Sales", sqlite :> IConnector ]))

    let orders =
        (Connections.sql "Sales" "select Id, Name, Notes from Orders order by Id" []).As<OrderWithOptions>()
        |> Seq.toList

    Assert.Equal(2, orders.Length)
    Assert.Equal(Some "Urgent", orders.[0].Notes)
    Assert.Equal(None, orders.[1].Notes)

[<Fact>]
let ``Connections.queryCsv streams CSV rows into strongly-typed records and allows LINQ filtering`` () =
    let path = Path.Combine(Path.GetTempPath(), "fqs-connections-" + Guid.NewGuid().ToString("N") + ".csv")
    File.WriteAllText(path, "Id,Name\n1,Alpha\n2,Beta\n")
    let csv = CsvConnector(path)
    Connections.configure(FakeResolver(Map.ofList [ "Feed", csv :> IConnector ]))

    let items = Connections.queryCsv<OrderRecord> "Feed"

    let names =
        query {
            for item in items do
            where (item.Id = 1)
            select item.Name
        }
        |> Seq.toList

    Assert.Equal<string list>([ "Alpha" ], names)

[<Fact>]
let ``Connections.get and Connections.connect return DatabaseConnection exposing Query, Queryable, Schema and Tables`` () =
    let sqlite = tempSqliteWithOrders ()
    Connections.configure(FakeResolver(Map.ofList [ "Sales", sqlite :> IConnector ]))

    let conn = Connections.get "Sales"
    Assert.Equal("Sales", conn.Name)
    Assert.NotEmpty(conn.ConnectionString)
    Assert.Contains("Orders", conn.Tables)
    Assert.NotEmpty(conn.Schema)

    let items = conn.Query<OrderRecord>("select Id, Name from Orders where Id = @id", [ "@id", box 1 ])
    let filtered =
        query {
            for x in items do
            where (x.Id = 1)
            select x.Name
        }
        |> Seq.toList

    Assert.Equal<string list>([ "Alice" ], filtered)

    let connAlias = Connections.connect "Sales"
    Assert.Equal("Sales", connAlias.Name)
    let execResult = connAlias.Execute("select count(*) from Orders")
    Assert.Equal(1, Seq.length execResult.Rows)

    let entitiesCode = conn.GenerateEntities()
    Assert.Contains("module SalesDb =", entitiesCode)
    Assert.Contains("type Orders =", entitiesCode)
    Assert.Contains("Id: int", entitiesCode)
    Assert.Contains("Name: string", entitiesCode)
    Assert.Contains("type Database", entitiesCode)
    Assert.Contains("member _.Orders", entitiesCode)

    let sqlProviderCode = conn.GenerateSqlProvider()
    Assert.Contains("SqlDataProvider<", sqlProviderCode)
    Assert.Contains("DatabaseVendor = Common.DatabaseProviderTypes.SQLITE", sqlProviderCode)

type FinancialRecord =
    { Id: int
      Amount: decimal
      Tax: decimal option
      Description: string }

[<Fact>]
let ``Connections.getMapper correctly parses decimal values with dot and comma under pt-BR culture`` () =
    let prevCulture = System.Threading.Thread.CurrentThread.CurrentCulture
    try
        System.Threading.Thread.CurrentThread.CurrentCulture <- System.Globalization.CultureInfo("pt-BR")

        let mapper = Connections.getMapper<FinancialRecord> [ "Id"; "Amount"; "Tax"; "Description" ]

        // Test with dot decimal: 355272835.53
        let row1 = mapper [| box "1"; box "355272835.53"; box "123.45"; box "Wire transfer" |]
        Assert.Equal(1, row1.Id)
        Assert.Equal(355272835.53M, row1.Amount)
        Assert.Equal(Some 123.45M, row1.Tax)

        // Test with comma decimal: 355272835,53
        let row2 = mapper [| box "2"; box "355272835,53"; null; box "Invoice payment" |]
        Assert.Equal(2, row2.Id)
        Assert.Equal(355272835.53M, row2.Amount)
        Assert.Equal(None, row2.Tax)

        // Test with quoted string: "\"355272835.53\""
        let row3 = mapper [| box "3"; box "\"355272835.53\""; box "0.00"; box "Quoted" |]
        Assert.Equal(355272835.53M, row3.Amount)
        Assert.Equal(Some 0.0M, row3.Tax)
    finally
        System.Threading.Thread.CurrentThread.CurrentCulture <- prevCulture



