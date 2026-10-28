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

let private withTempSqlite (action: SqliteConnector -> string -> unit) =
    let path = Path.Combine(Path.GetTempPath(), "fqs-connections-" + Guid.NewGuid().ToString("N") + ".db")
    try
        let connector = SqliteConnector(path)
        connector.ExecuteQuery "create table Orders (Id integer primary key, Name text)" [] |> ignore
        connector.ExecuteQuery "insert into Orders (Id, Name) values (1, 'Alice'), (2, 'Bob')" [] |> ignore
        action connector path
    finally
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
        try File.Delete path with _ -> ()

[<Fact>]
let ``DatabaseConnection.rawQuery binds an OR-1-equals-1-shaped parameter as literal data, never as SQL syntax`` () =
    withTempSqlite (fun sqlite _ ->
        Connections.configure(FakeResolver(Map.ofList [ "Sales", sqlite :> IConnector ]))

        // If this parameter were ever concatenated into the SQL text instead
        // of bound, this classic injection payload would make the WHERE
        // clause always true and return every row.
        let db = Connections.getDatabaseConnection "Sales"
        let result =
            db.rawQuery("select * from Orders where Name = @name", [ "@name", box "x' OR '1'='1" ])

        Assert.Empty(result.Rows)
    )

[<Fact>]
let ``DatabaseConnection.rawQuery binds a statement-terminator-shaped parameter as literal data, never executing it`` () =
    withTempSqlite (fun sqlite _ ->
        Connections.configure(FakeResolver(Map.ofList [ "Sales", sqlite :> IConnector ]))

        let db = Connections.getDatabaseConnection "Sales"
        db.rawQuery("select * from Orders where Name = @name", [
            "@name", box "x'; DROP TABLE Orders; --"
        ])
        |> ignore

        // The table must still exist and still hold its original two rows -
        // a real DROP TABLE would have removed it.
        let survivingRows = sqlite.ExecuteQuery "select * from Orders" [] |> fun r -> r.Rows |> Seq.toList
        Assert.Equal(2, survivingRows.Length)
    )

[<Fact>]
let ``DatabaseConnection.rawQuery routes to the resolved connector and returns its rows for a normal query`` () =
    withTempSqlite (fun sqlite _ ->
        Connections.configure(FakeResolver(Map.ofList [ "Sales", sqlite :> IConnector ]))

        let db = Connections.getDatabaseConnection "Sales"
        let result = db.rawQuery("select Name from Orders where Id = @id", [ "@id", box 1 ])

        Assert.Equal(box "Alice", (result.Rows |> Seq.exactlyOne).[0])
    )

[<Fact>]
let ``Connections.getDatabaseConnection fails clearly when the named connection isn't a SQL-capable connector`` () =
    let csvPath = Path.Combine(Path.GetTempPath(), "fqs-connections-" + Guid.NewGuid().ToString("N") + ".csv")
    try
        File.WriteAllText(csvPath, "Id\n1\n")
        let csv = CsvConnector(csvPath)
        Connections.configure(FakeResolver(Map.ofList [ "Feed", csv :> IConnector ]))

        Assert.Throws<Exception>(fun () -> Connections.getDatabaseConnection "Feed" |> ignore)
        |> ignore
    finally
        try File.Delete csvPath with _ -> ()

[<Fact>]
let ``CsvConnection.Raw routes to the resolved CSV connector and reads its rows`` () =
    let path = Path.Combine(Path.GetTempPath(), "fqs-connections-" + Guid.NewGuid().ToString("N") + ".csv")
    try
        File.WriteAllText(path, "Id,Name\n1,Sales\n")
        let csv = CsvConnector(path)
        Connections.configure(FakeResolver(Map.ofList [ "Feed", csv :> IConnector ]))

        let conn = Connections.getCsvConnection "Feed"
        let result = conn.Raw()

        Assert.Equal<string list>([ "Id"; "Name" ], result.Columns)
        let rows = result.Rows |> Seq.toList
        Assert.Equal(1, rows.Length)
        Assert.Equal(box "1", rows.[0].[0])
        Assert.Equal(box "Sales", rows.[0].[1])
    finally
        try File.Delete path with _ -> ()

[<Fact>]
let ``ExcelConnection.Raw routes to the resolved Excel connector and reads its rows`` () =
    let path = Path.Combine(Path.GetTempPath(), "fqs-connections-" + Guid.NewGuid().ToString("N") + ".xlsx")
    try
        do
            use workbook = new ClosedXML.Excel.XLWorkbook()
            let sheet = workbook.Worksheets.Add("Sheet1")
            sheet.Cell(1, 1).Value <- "Id"
            sheet.Cell(2, 1).Value <- 1
            workbook.SaveAs(path)

        let excel = ExcelConnector(path)
        Connections.configure(FakeResolver(Map.ofList [ "Book", excel :> IConnector ]))

        let conn = Connections.getExcelConnection "Book"
        let result = conn.Raw()

        Assert.Equal<string list>([ "Id" ], result.Columns)
        let rows = result.Rows |> Seq.toList
        Assert.Equal(1, rows.Length)
        Assert.Equal(box 1.0, rows.[0].[0])
    finally
        try File.Delete path with _ -> ()

[<Fact>]
let ``RestConnection.SendAsync routes to the resolved REST connector and sends the request`` () =
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

        let conn = Connections.getRestConnection "Api"
        let! result = conn.SendAsync(RestRequestOptions.Get($"http://127.0.0.1:{port}/health"), CancellationToken.None)
        do! serveTask
        listener.Stop()
        listener.Close()

        Assert.True(result.GetProperty("ok").GetBoolean())
    }

type OrderRecord = { Id: int; Name: string }
type OrderWithOptions = { Id: int; Name: string; Notes: string option }
type CsvRecordWithOptions = { Id: int; Price: decimal option; Notes: string option }

[<Fact>]
let ``Connections.getConnectionString returns the ADO.NET connection string for a SQL connector`` () =
    withTempSqlite (fun sqlite _ ->
        Connections.configure(FakeResolver(Map.ofList [ "Sales", sqlite :> IConnector ]))

        let connStr = Connections.getConnectionString "Sales"
        Assert.Contains("Data Source=", connStr)
    )

[<Fact>]
let ``Connections.query maps rows to strongly-typed records and supports F# query LINQ expressions`` () =
    withTempSqlite (fun sqlite _ ->
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
    )

[<Fact>]
let ``ConnectorQueryResult.As maps null/DBNull values to F# None and values to Some`` () =
    withTempSqlite (fun sqlite _ ->
        sqlite.ExecuteQuery "alter table Orders add column Notes text" [] |> ignore
        sqlite.ExecuteQuery "update Orders set Notes = 'Urgent' where Id = 1" [] |> ignore
        Connections.configure(FakeResolver(Map.ofList [ "Sales", sqlite :> IConnector ]))

        let db = Connections.getDatabaseConnection "Sales"
        let orders: OrderWithOptions list =
            (db.rawQuery("select Id, Name, Notes from Orders order by Id", [])).As<OrderWithOptions>()
            |> Seq.toList

        Assert.Equal(2, orders.Length)
        Assert.Equal(Some "Urgent", orders.[0].Notes)
        Assert.Equal(None, orders.[1].Notes)
    )

[<Fact>]
let ``Connections.queryCsv streams CSV rows into strongly-typed records and allows LINQ filtering`` () =
    let path = Path.Combine(Path.GetTempPath(), "fqs-connections-" + Guid.NewGuid().ToString("N") + ".csv")
    try
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
    finally
        try File.Delete path with _ -> ()

[<Fact>]
let ``Connections.queryCsv maps sentinel strings N/A, -, null and empty to None for option fields`` () =
    let path = Path.Combine(Path.GetTempPath(), "fqs-sentinels-" + Guid.NewGuid().ToString("N") + ".csv")
    try
        File.WriteAllText(path, "Id,Price,Notes\n1,100.50,Special\n2,N/A,-\n3,-,null\n4,250.75,Normal\n5,300.00,\n")
        let csv = CsvConnector(path)
        Connections.configure(FakeResolver(Map.ofList [ "SentinelsFeed", csv :> IConnector ]))

        let items = Connections.queryCsv<CsvRecordWithOptions> "SentinelsFeed" |> Seq.toList

        Assert.Equal(5, items.Length)
        Assert.Equal(Some 100.50m, items.[0].Price)
        Assert.Equal(Some "Special", items.[0].Notes)

        Assert.Equal(None, items.[1].Price)
        Assert.Equal(None, items.[1].Notes)

        Assert.Equal(None, items.[2].Price)
        Assert.Equal(None, items.[2].Notes)

        Assert.Equal(Some 250.75m, items.[3].Price)
        Assert.Equal(Some "Normal", items.[3].Notes)

        Assert.Equal(Some 300.00m, items.[4].Price)
        Assert.Equal(None, items.[4].Notes)
    finally
        try File.Delete path with _ -> ()

[<Fact>]
let ``Connections.getDatabaseConnection returns DatabaseConnection exposing Query, Queryable, Schema and Tables`` () =
    withTempSqlite (fun sqlite _ ->
        Connections.configure(FakeResolver(Map.ofList [ "Sales", sqlite :> IConnector ]))

        let conn = Connections.getDatabaseConnection "Sales"
        Assert.Equal("Sales", conn.Name)
        Assert.NotEmpty(conn.ConnectionString)
        Assert.Contains("Orders", conn.Tables)
        Assert.NotEmpty(conn.Schema)

        let items = conn.Query<OrderRecord>("select Id, Name from Orders where Id = @id", [ "@id", box 1 ])
        let filtered =
            query {
                for (x: OrderRecord) in items do
                where (x.Id = 1)
                select x.Name
            }
            |> Seq.toList

        Assert.Equal<string list>([ "Alice" ], filtered)

        let execResult = conn.Execute("select count(*) from Orders")
        Assert.Equal(1, Seq.length execResult.Rows)
        let rawResult = conn.rawQuery("select count(*) from Orders")
        Assert.Equal(1, Seq.length rawResult.Rows)

        let entitiesCode = conn.GenerateEntities()
        Assert.Contains("module SalesDb =", entitiesCode)
        Assert.Contains("type Orders =", entitiesCode)
        Assert.Contains("Id: int", entitiesCode)
        Assert.Contains("Name: string", entitiesCode)
        Assert.Contains("type Database", entitiesCode)
        Assert.Contains("member _.Orders", entitiesCode)

        let sqlProviderCode = conn.GenerateSqlProvider()
        Assert.Contains("SqlDataProvider<", sqlProviderCode)
    )
