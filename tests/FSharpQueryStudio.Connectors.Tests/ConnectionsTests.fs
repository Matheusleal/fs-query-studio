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

type LargeCsvRecord =
    { Id: int
      Customer: string
      Quantity: int
      Amount: decimal
      Active: bool
      OrderDate: DateTime }

[<Fact>]
let ``Connections.queryCsv streams >100,000 rows with messy cells past row 200 without throwing or losing rows`` () =
    let path = Path.Combine(Path.GetTempPath(), "fqs-large-100k-" + Guid.NewGuid().ToString("N") + ".csv")
    try
        do
            use fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536)
            use writer = new StreamWriter(fs, Encoding.UTF8, 65536)
            writer.WriteLine("Id,Customer,Quantity,Amount,Active,OrderDate")
            for i = 1 to 105_000 do
                if i <= 200 then
                    writer.WriteLine($"{i},Customer_{i},2,150.25,true,2026-03-15")
                elif i = 250 then
                    // Empty cells in non-option value-type columns after the 200-row inference sample
                    writer.WriteLine($"{i},Customer_{i},,,,")
                elif i = 300 then
                    // Sentinels N/A, -, null, #N/A in non-option value-type columns
                    writer.WriteLine($"{i},Customer_{i},N/A,-,null,#N/A")
                elif i = 350 then
                    // Decimal string in an int-inferred column + comma decimal
                    writer.WriteLine($"{i},Customer_{i},12.8,\"1250,50\",yes,2026-04-01")
                else
                    writer.WriteLine($"{i},Customer_{i},5,200.00,false,2026-03-20")
            // Trailing blank line
            writer.WriteLine()

        let csv = CsvConnector(path)
        Connections.configure(FakeResolver(Map.ofList [ "LargeFeed", csv :> IConnector ]))

        let conn = Connections.getCsvConnection "LargeFeed"
        let queryable = conn.Queryable<LargeCsvRecord>()

        let allRows = queryable |> Seq.toArray
        Assert.Equal(105_000, allRows.Length)
        Assert.Equal(1, allRows.[0].Id)
        Assert.Equal(150.25m, allRows.[0].Amount)
        // Row 250 (index 249): empty cells fall back safely to default values
        Assert.Equal(250, allRows.[249].Id)
        Assert.Equal(0, allRows.[249].Quantity)
        Assert.Equal(0m, allRows.[249].Amount)
        // Row 350 (index 349): 12.8 rounded to 13, "1250,50" parsed as 1250.50m
        Assert.Equal(13, allRows.[349].Quantity)
        Assert.Equal(1250.50m, allRows.[349].Amount)
        Assert.True(allRows.[349].Active)
        Assert.Equal(105_000, allRows.[104_999].Id)
    finally
        try File.Delete path with _ -> ()


// ---------------------------------------------------------------------------
// Large SQLite / Excel sources with messy values past the first 200 rows
// ---------------------------------------------------------------------------

type LargeSqlRecord =
    { Id: int
      Customer: string
      Quantity: int
      Amount: decimal
      Active: bool
      OrderDate: DateTime }

/// Creates a SQLite table with `rowCount` rows. SQLite is dynamically typed, so
/// text sentinels, NULLs, REALs and comma-decimals can land in INTEGER/NUMERIC
/// columns — exactly the dirty data that used to break the typed reader.
let private createMessySqlite (path: string) (rowCount: int) =
    use conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}")
    conn.Open()
    use create = conn.CreateCommand()
    create.CommandText <-
        "create table BigOrders (Id INTEGER, Customer TEXT, Quantity INTEGER, Amount NUMERIC, Active BOOLEAN, OrderDate DATETIME)"
    create.ExecuteNonQuery() |> ignore
    use tx = conn.BeginTransaction()
    use insert = conn.CreateCommand()
    insert.Transaction <- tx
    insert.CommandText <- "insert into BigOrders values ($id, $c, $q, $a, $act, $d)"
    let pId = insert.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer)
    let pC = insert.Parameters.Add("$c", Microsoft.Data.Sqlite.SqliteType.Text)
    let pQ = insert.Parameters.Add("$q", Microsoft.Data.Sqlite.SqliteType.Text)
    let pA = insert.Parameters.Add("$a", Microsoft.Data.Sqlite.SqliteType.Text)
    let pAct = insert.Parameters.Add("$act", Microsoft.Data.Sqlite.SqliteType.Text)
    let pD = insert.Parameters.Add("$d", Microsoft.Data.Sqlite.SqliteType.Text)
    let set (p: Microsoft.Data.Sqlite.SqliteParameter) (v: obj) = p.Value <- (if isNull v then box DBNull.Value else v)
    for i = 1 to rowCount do
        set pId (box i)
        set pC (box $"Customer_{i}")
        if i <= 200 then
            set pQ (box 3); set pA (box 199.90); set pAct (box 1); set pD (box "2026-03-01")
        elif i % 1000 = 0 then
            // SQL NULLs in non-option value-type columns
            set pQ null; set pA null; set pAct null; set pD null
        elif i % 1000 = 1 then
            // Text sentinels in typed columns
            set pQ (box "N/A"); set pA (box "-"); set pAct (box "null"); set pD (box "#N/A")
        elif i % 1000 = 2 then
            // REAL in INTEGER column, comma decimal, 'yes' boolean, garbage date
            set pQ (box 4.7); set pA (box "350,75"); set pAct (box "yes"); set pD (box "invalid")
        else
            set pQ (box 2); set pA (box 120.00); set pAct (box 0); set pD (box "2026-03-10")
        insert.ExecuteNonQuery() |> ignore
    tx.Commit()

[<Fact>]
let ``SQLite pushdown queryable streams >100,000 rows with NULLs, sentinels and mismatched types past row 200 without throwing`` () =
    let path = Path.Combine(Path.GetTempPath(), "fqs-large-sqlite-" + Guid.NewGuid().ToString("N") + ".db")
    try
        createMessySqlite path 105_000
        let sqlite = SqliteConnector(path)
        Connections.configure(FakeResolver(Map.ofList [ "BigDb", sqlite :> IConnector ]))
        let db = Connections.getDatabaseConnection "BigDb"

        let all =
            db.PushdownQueryable<LargeSqlRecord>("SELECT Id, Customer, Quantity, Amount, Active, OrderDate FROM BigOrders")
            |> Seq.toArray

        Assert.Equal(105_000, all.Length)
        Assert.Equal(199.90m, all.[0].Amount)
        // Row 1000 (index 999): SQL NULLs -> safe defaults
        Assert.Equal(0, all.[999].Quantity)
        Assert.Equal(0m, all.[999].Amount)
        Assert.False(all.[999].Active)
        Assert.Equal(DateTime.MinValue, all.[999].OrderDate)
        // Row 1001 (index 1000): text sentinels -> safe defaults
        Assert.Equal(0, all.[1000].Quantity)
        Assert.Equal(0m, all.[1000].Amount)
        // Row 1002 (index 1001): 4.7 -> 4 (SQLite integer truncation) or 5, "350,75" -> 350.75, "yes" -> true, "invalid" -> MinValue
        Assert.True(all.[1001].Quantity = 4 || all.[1001].Quantity = 5)
        Assert.Equal(350.75m, all.[1001].Amount)
        Assert.True(all.[1001].Active)
        Assert.Equal(DateTime.MinValue, all.[1001].OrderDate)

        // LINQ predicate pushed down to SQL still works over the messy table
        let filtered =
            query {
                for o in db.PushdownQueryable<LargeSqlRecord>("SELECT Id, Customer, Quantity, Amount, Active, OrderDate FROM BigOrders") do
                where (o.Id > 104_990)
                select o
            }
            |> Seq.toArray
        Assert.Equal(10, filtered.Length)
    finally
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
        try File.Delete path with _ -> ()

type LargeExcelRecord =
    { Id: int
      Region: string
      Units: int
      Revenue: double }

[<Fact>]
let ``Connections.queryExcelSheet streams >100,000 rows with blanks, error-like text and mismatched types past row 200`` () =
    let path = Path.Combine(Path.GetTempPath(), "fqs-large-xlsx-" + Guid.NewGuid().ToString("N") + ".xlsx")
    let rowCount = 100_500
    try
        do
            use wb = new ClosedXML.Excel.XLWorkbook()
            let ws = wb.Worksheets.Add("Data")
            ws.Cell(1, 1).Value <- "Id"
            ws.Cell(1, 2).Value <- "Region"
            ws.Cell(1, 3).Value <- "Units"
            ws.Cell(1, 4).Value <- "Revenue"
            for i = 1 to rowCount do
                let r = i + 1
                ws.Cell(r, 1).Value <- i
                ws.Cell(r, 2).Value <- $"Region_{i % 5}"
                if i <= 200 then
                    ws.Cell(r, 3).Value <- 10
                    ws.Cell(r, 4).Value <- 250.50
                elif i % 1000 = 0 then
                    () // blank Units / Revenue
                elif i % 1000 = 1 then
                    ws.Cell(r, 3).Value <- "N/A"
                    ws.Cell(r, 4).Value <- "#DIV/0!"
                elif i % 1000 = 2 then
                    ws.Cell(r, 3).Value <- 7.6
                    ws.Cell(r, 4).Value <- "1250,50"
                else
                    ws.Cell(r, 3).Value <- 20
                    ws.Cell(r, 4).Value <- 500.75
            wb.SaveAs(path)

        let excel = ExcelConnector(path)
        Connections.configure(FakeResolver(Map.ofList [ "BigBook", excel :> IConnector ]))

        let rows = Connections.queryExcelSheet<LargeExcelRecord> "BigBook" (Some "Data") None |> Seq.toArray

        Assert.Equal(rowCount, rows.Length)
        Assert.Equal(250.50, rows.[0].Revenue)
        Assert.Equal(0, rows.[999].Units)
        Assert.Equal(0.0, rows.[999].Revenue)
        Assert.Equal(0, rows.[1000].Units)
        Assert.Equal(0.0, rows.[1000].Revenue)
        Assert.Equal(8, rows.[1001].Units)
        Assert.Equal(1250.50, rows.[1001].Revenue)
        Assert.Equal(rowCount, rows.[rowCount - 1].Id)
    finally
        try File.Delete path with _ -> ()
