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

[<Fact>]
let ``syncDataSourcesAsync pre-stages all worksheets of Excel connection into SQLite engine`` () =
    task {
        BuiltinConnectors.registerDefaults ()
        let tmp = Path.Combine(Path.GetTempPath(), "fqs-excel-sync-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory tmp |> ignore
        try
            let xlsxPath = Path.Combine(tmp, "multi_sheet.xlsx")
            do
                use wb = new ClosedXML.Excel.XLWorkbook()
                let ws1 = wb.Worksheets.Add("Targets")
                ws1.Cell(1, 1).Value <- "Id"
                ws1.Cell(1, 2).Value <- "Title"
                ws1.Cell(2, 1).Value <- 1
                ws1.Cell(2, 2).Value <- "Target Alpha"

                let ws2 = wb.Worksheets.Add("Budgets")
                ws2.Cell(1, 1).Value <- "Id"
                ws2.Cell(1, 2).Value <- "Title"
                ws2.Cell(2, 1).Value <- 10
                ws2.Cell(2, 2).Value <- "Budget 2026"
                wb.SaveAs(xlsxPath)

            let connDef: ConnectionDefinition =
                { Id = Guid.NewGuid()
                  Name = "CompanyData"
                  Kind = ConnectorKind.Excel
                  Configuration = System.Text.Json.JsonDocument.Parse($"""{{"filePath": "{xlsxPath.Replace("\\", "/")}", "hasHeader": true}}""").RootElement
                  SecretReference = None
                  IsTrusted = true }

            let secretStore = FSharpQueryStudio.Security.InMemorySecretStore() :> FSharpQueryStudio.Security.ISecretStore

            let! summary = Connections.syncDataSourcesAsync tmp [ connDef ] secretStore
            Assert.True(summary.StagedCount >= 2, $"Expected at least 2 staged sheets, got {summary.StagedCount}")

            let cacheDb = Path.Combine(tmp, ".fsqs", "cache", "engine.db")
            Assert.True(File.Exists cacheDb)

            let eng = FileSqliteEngine(cacheDb)
            // Only prefixed worksheet tables should be staged; the connection name itself acts as a schema prefix, not a table
            Assert.True(eng.IsStagedAndCurrent("CompanyData_Targets", xlsxPath))
            Assert.True(eng.IsStagedAndCurrent("CompanyData_Budgets", xlsxPath))
            Assert.False(eng.IsStagedAndCurrent("CompanyData", xlsxPath))

            // Re-syncing without file changes skips all tables
            let! summary2 = Connections.syncDataSourcesAsync tmp [ connDef ] secretStore
            Assert.Equal(0, summary2.StagedCount)
            Assert.Equal(2, summary2.SkippedCount)
        finally
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
            try Directory.Delete(tmp, true) with _ -> ()
    }

type CalendarRow = { Id: int; EventName: string }
type NamesRow = { Id: int; PersonName: string }
type CoursesRow = { Id: int; CourseTitle: string }

[<Fact>]
let ``Excel connection teste with calendar, names and courses stages only prefixed tables and translates LINQ to exact sheet table`` () =
    task {
        BuiltinConnectors.registerDefaults ()
        let tmp = Path.Combine(Path.GetTempPath(), "fqs-excel-teste-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory tmp |> ignore
        try
            let xlsxPath = Path.Combine(tmp, "teste.xlsx")
            do
                use wb = new ClosedXML.Excel.XLWorkbook()
                let wsCal = wb.Worksheets.Add("calendar")
                wsCal.Cell(1, 1).Value <- "Id"
                wsCal.Cell(1, 2).Value <- "EventName"
                wsCal.Cell(2, 1).Value <- 1
                wsCal.Cell(2, 2).Value <- "Semester Start"

                let wsNames = wb.Worksheets.Add("names")
                wsNames.Cell(1, 1).Value <- "Id"
                wsNames.Cell(1, 2).Value <- "PersonName"
                wsNames.Cell(2, 1).Value <- 10
                wsNames.Cell(2, 2).Value <- "Maria"

                let wsCourses = wb.Worksheets.Add("courses")
                wsCourses.Cell(1, 1).Value <- "Id"
                wsCourses.Cell(1, 2).Value <- "CourseTitle"
                wsCourses.Cell(2, 1).Value <- 100
                wsCourses.Cell(2, 2).Value <- "F# Programming"

                wb.SaveAs(xlsxPath)

            let connDef: ConnectionDefinition =
                { Id = Guid.NewGuid()
                  Name = "teste"
                  Kind = ConnectorKind.Excel
                  Configuration = System.Text.Json.JsonDocument.Parse($"""{{"filePath": "{xlsxPath.Replace("\\", "/")}", "hasHeader": true}}""").RootElement
                  SecretReference = None
                  IsTrusted = true }

            let secretStore = FSharpQueryStudio.Security.InMemorySecretStore() :> FSharpQueryStudio.Security.ISecretStore
            let cacheDb = Path.Combine(tmp, ".fsqs", "cache", "engine.db")
            Directory.CreateDirectory(Path.GetDirectoryName(cacheDb)) |> ignore
            let eng = FileSqliteEngine(cacheDb)

            // Simulate a legacy unprefixed table "teste" existing before sync
            let excelConnector = ExcelConnector(xlsxPath)
            eng.EnsureStaged("teste", xlsxPath, (fun () -> excelConnector.ReadRows(sheetName = "calendar"))) |> ignore
            Assert.True(eng.IsStagedAndCurrent("teste", xlsxPath))

            // Sync data sources should drop the stale "teste" table and stage only teste_calendar, teste_names, teste_courses
            let! summary = Connections.syncDataSourcesAsync tmp [ connDef ] secretStore
            Assert.Equal(3, summary.StagedCount)
            Assert.False(eng.IsStagedAndCurrent("teste", xlsxPath))
            Assert.True(eng.IsStagedAndCurrent("teste_calendar", xlsxPath))
            Assert.True(eng.IsStagedAndCurrent("teste_names", xlsxPath))
            Assert.True(eng.IsStagedAndCurrent("teste_courses", xlsxPath))

            Connections.setEngine eng
            let excelConn = Connections.ExcelConnection("teste", excelConnector)

            let qCalendar =
                query {
                    for x in excelConn.SheetPushdownQueryable<CalendarRow>("calendar") do
                    select x
                }
            let qNames =
                query {
                    for x in excelConn.SheetPushdownQueryable<NamesRow>("names") do
                    select {| Id = x.Id; Name = x.PersonName |}
                }
            let qCourses =
                query {
                    for x in excelConn.SheetPushdownQueryable<CoursesRow>("courses") do
                    select x
                }

            Assert.Equal("SELECT * FROM teste_calendar", ResultKind.toRawSql qCalendar)
            Assert.Equal("SELECT Id AS Id, PersonName AS Name FROM teste_names", ResultKind.toRawSql qNames)
            Assert.Equal("SELECT * FROM teste_courses", ResultKind.toRawSql qCourses)

            let calRows = qCalendar |> Seq.toList
            let nameRows = qNames |> Seq.toList
            let courseRows = qCourses |> Seq.toList
            Assert.Equal(1, calRows.Length)
            Assert.Equal("Semester Start", calRows.[0].EventName)
            Assert.Equal(1, nameRows.Length)
            Assert.Equal("Maria", nameRows.[0].Name)
            Assert.Equal(1, courseRows.Length)
            Assert.Equal("F# Programming", courseRows.[0].CourseTitle)
        finally
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
            try Directory.Delete(tmp, true) with _ -> ()
    }

// =========================================================================
// Comprehensive tests for Connections, typed wrappers, errors, and pivot
// =========================================================================

type private DummyTestConnector(kind: ConnectorKind) =
    interface IConnector with
        member _.Kind = kind
        member _.TestConnectionAsync(_ct) = System.Threading.Tasks.Task.FromResult true
        member _.DiscoverSchemaAsync(_ct) = System.Threading.Tasks.Task.FromResult []

[<Fact>]
let ``Connections throws unsupported exception when connector does not match operation`` () =
    let dummyRest = DummyTestConnector(ConnectorKind.RestJson) :> IConnector
    let dummySql = DummyTestConnector(ConnectorKind.Sqlite) :> IConnector

    let resolver = FakeResolver(Map.ofList [
        "RestOnly", dummyRest
        "SqlOnly", dummySql
    ])
    Connections.configure resolver

    // getConnectionString requires IQueryableConnector
    let ex1 = Assert.Throws<Exception>(fun () -> Connections.getConnectionString "RestOnly" |> ignore)
    Assert.Contains("does not support Connections.getConnectionString", ex1.Message)

    // executeSqlRaw / getDatabaseConnection requires IQueryableConnector
    let ex2 = Assert.Throws<Exception>(fun () -> Connections.executeSqlRaw "RestOnly" "SELECT 1" [] |> ignore)
    Assert.Contains("does not support Connections.sql", ex2.Message)

    let ex3 = Assert.Throws<Exception>(fun () -> Connections.getDatabaseConnection "RestOnly" |> ignore)
    Assert.Contains("does not support Connections.sql", ex3.Message)

    // readCsvRaw / getCsvConnection requires CsvConnector
    let ex4 = Assert.Throws<Exception>(fun () -> Connections.readCsvRaw "SqlOnly" |> ignore)
    Assert.Contains("does not support Connections.csv", ex4.Message)

    let ex5 = Assert.Throws<Exception>(fun () -> Connections.getCsvConnection "SqlOnly" |> ignore)
    Assert.Contains("does not support Connections.csv", ex5.Message)

    // readExcelRaw / getExcelConnection / queryExcelSheet requires ExcelConnector
    let ex6 = Assert.Throws<Exception>(fun () -> Connections.readExcelRaw "SqlOnly" None |> ignore)
    Assert.Contains("does not support Connections.excel", ex6.Message)

    let ex7 = Assert.Throws<Exception>(fun () -> Connections.getExcelConnection "SqlOnly" |> ignore)
    Assert.Contains("does not support Connections.excel", ex7.Message)

    let ex8 = Assert.Throws<Exception>(fun () -> Connections.queryExcelSheet<CalendarRow> "SqlOnly" None None |> Seq.toList |> ignore)
    Assert.Contains("does not support Connections.queryExcelSheet", ex8.Message)

    // readJsonFileRaw / getJsonConnection / queryJsonFileTable requires JsonConnector
    let ex9 = Assert.Throws<Exception>(fun () -> Connections.readJsonFileRaw "SqlOnly" None |> ignore)
    Assert.Contains("does not support Connections.json", ex9.Message)

    let ex10 = Assert.Throws<Exception>(fun () -> Connections.getJsonConnection "SqlOnly" |> ignore)
    Assert.Contains("does not support Connections.json", ex10.Message)

    let ex11 = Assert.Throws<Exception>(fun () -> Connections.queryJsonFileTable<CalendarRow> "SqlOnly" None None |> Seq.toList |> ignore)
    Assert.Contains("does not support Connections.queryJsonFileTable", ex11.Message)

    // readXmlRaw / getXmlConnection / queryXmlTable requires XmlConnector
    let ex12 = Assert.Throws<Exception>(fun () -> Connections.readXmlRaw "SqlOnly" None |> ignore)
    Assert.Contains("does not support Connections.xml", ex12.Message)

    let ex13 = Assert.Throws<Exception>(fun () -> Connections.getXmlConnection "SqlOnly" |> ignore)
    Assert.Contains("does not support Connections.xml", ex13.Message)

    let ex14 = Assert.Throws<Exception>(fun () -> Connections.queryXmlTable<CalendarRow> "SqlOnly" None None |> Seq.toList |> ignore)
    Assert.Contains("does not support Connections.queryXmlTable", ex14.Message)

    // sendRestAsync / getRestConnection / queryJsonWithOptions requires RestConnector
    let ex15 = Assert.Throws<Exception>(fun () -> Connections.sendRestAsync "SqlOnly" (RestRequestOptions.Get "http://localhost") CancellationToken.None |> ignore)
    Assert.Contains("does not support Connections.rest", ex15.Message)

    let ex16 = Assert.Throws<Exception>(fun () -> Connections.getRestConnection "SqlOnly" |> ignore)
    Assert.Contains("does not support Connections.rest", ex16.Message)

    let ex17 = Assert.Throws<Exception>(fun () -> Connections.queryJsonWithOptions<CalendarRow> "SqlOnly" None |> Seq.toList |> ignore)
    Assert.Contains("does not support Connections.queryJson", ex17.Message)

type JsonUserItem = { Id: int; Name: string }
type JsonLogItem = { Code: string; Message: string }

[<Fact>]
let ``Connections queryJsonFileTable and queryXmlTable query specific collections successfully`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), "fqs-file-tables-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory tmpDir |> ignore
    try
        let jsonPath = Path.Combine(tmpDir, "data.json")
        File.WriteAllText(jsonPath, """
        {
            "Users": [
                { "Id": 1, "Name": "Alice" },
                { "Id": 2, "Name": "Bob" }
            ],
            "Logs": [
                { "Code": "E01", "Message": "Init" },
                { "Code": "E02", "Message": "Done" }
            ]
        }
        """)

        let xmlPath = Path.Combine(tmpDir, "data.xml")
        File.WriteAllText(xmlPath, """
        <Root>
            <Users>
                <User><Id>1</Id><Name>Alice</Name></User>
                <User><Id>2</Id><Name>Bob</Name></User>
            </Users>
        </Root>
        """)

        let jsonConnector = JsonConnector(jsonPath)
        let xmlConnector = XmlConnector(xmlPath)

        Connections.configure(FakeResolver(Map.ofList [
            "LocalJson", jsonConnector :> IConnector
            "LocalXml", xmlConnector :> IConnector
        ]))

        // Test queryJsonFileTable with specific tables
        let users = Connections.queryJsonFileTable<JsonUserItem> "LocalJson" (Some "Users") None |> Seq.toList
        Assert.Equal(2, users.Length)
        Assert.Equal(1, users.[0].Id)
        Assert.Equal("Alice", users.[0].Name)

        let logs = Connections.queryJsonFileTable<JsonLogItem> "LocalJson" (Some "Logs") (Some 1) |> Seq.toList
        Assert.Equal(1, logs.Length)
        Assert.Equal("E01", logs.[0].Code)

        // Test queryXmlTable
        let xmlUsers = Connections.queryXmlTable<JsonUserItem> "LocalXml" (Some "User") None |> Seq.toList
        Assert.Equal(2, xmlUsers.Length)
        Assert.Equal("Bob", xmlUsers.[1].Name)
    finally
        try Directory.Delete(tmpDir, true) with _ -> ()

type SimpleItem = { Id: int; Title: string }

[<Fact>]
let ``Typed connections expose all expected metadata and querying properties`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), "fqs-typed-conns-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory tmpDir |> ignore
    let cacheDb = Path.Combine(tmpDir, "engine.db")
    let engine = FileSqliteEngine(cacheDb)
    Connections.setEngine engine

    try
        // CSV
        let csvPath = Path.Combine(tmpDir, "items.csv")
        File.WriteAllLines(csvPath, [| "Id,Title"; "1,Alpha"; "2,Beta" |])
        let csv = CsvConnector(csvPath)

        // Excel
        let xlsxPath = Path.Combine(tmpDir, "items.xlsx")
        do
            use wb = new ClosedXML.Excel.XLWorkbook()
            let ws = wb.Worksheets.Add("Sheet1")
            ws.Cell(1, 1).Value <- "Id"
            ws.Cell(1, 2).Value <- "Title"
            ws.Cell(2, 1).Value <- 10
            ws.Cell(2, 2).Value <- "Gamma"
            wb.SaveAs(xlsxPath)
        let excel = ExcelConnector(xlsxPath)

        // JSON
        let jsonPath = Path.Combine(tmpDir, "items.json")
        File.WriteAllText(jsonPath, """[ { "Id": 100, "Title": "Delta" } ]""")
        let json = JsonConnector(jsonPath)

        // XML
        let xmlPath = Path.Combine(tmpDir, "items.xml")
        File.WriteAllText(xmlPath, """<Items><Item><Id>200</Id><Title>Epsilon</Title></Item></Items>""")
        let xml = XmlConnector(xmlPath)

        // SQLite
        let dbPath = Path.Combine(tmpDir, "data.db")
        let sqlite = SqliteConnector(dbPath)
        sqlite.ExecuteQuery "CREATE TABLE Items (Id INTEGER PRIMARY KEY, Title TEXT);" [] |> ignore
        sqlite.ExecuteQuery "INSERT INTO Items VALUES (300, 'Zeta');" [] |> ignore

        Connections.configure(FakeResolver(Map.ofList [
            "MyCsv", csv :> IConnector
            "MyExcel", excel :> IConnector
            "MyJson", json :> IConnector
            "MyXml", xml :> IConnector
            "MySqlite", sqlite :> IConnector
        ]))

        // 1. CsvConnection
        let csvConn = Connections.getCsvConnection "MyCsv"
        Assert.Equal("MyCsv", csvConn.Name)
        Assert.Same(csv, csvConn.Connector)
        Assert.Equal(csvPath, csvConn.FilePath)
        let csvRows = csvConn.Rows<SimpleItem>() |> Seq.toList
        Assert.Equal(2, csvRows.Length)
        let csvQ = csvConn.AsQueryable<SimpleItem>()
        Assert.NotNull(csvQ)
        let csvRaw = csvConn.Raw()
        Assert.Equal(2, csvRaw.Rows |> Seq.length)

        // 2. ExcelConnection
        let excelConn = Connections.getExcelConnection "MyExcel"
        Assert.Equal("MyExcel", excelConn.Name)
        Assert.Same(excel, excelConn.Connector)
        Assert.Equal(xlsxPath, excelConn.FilePath)
        let excelSheetRows = excelConn.Sheet<SimpleItem>("Sheet1") |> Seq.toList
        Assert.Equal(1, excelSheetRows.Length)
        let excelTableRows = excelConn.Table<SimpleItem>("Sheet1") |> Seq.toList
        Assert.Equal(1, excelTableRows.Length)
        let excelQ = excelConn.AsQueryable<SimpleItem>()
        Assert.NotNull(excelQ)
        let excelRaw = excelConn.Raw()
        Assert.Equal(1, excelRaw.Rows |> Seq.length)

        // 3. JsonConnection
        let jsonConn = Connections.getJsonConnection "MyJson"
        Assert.Equal("MyJson", jsonConn.Name)
        Assert.Same(json, jsonConn.Connector)
        Assert.Equal(jsonPath, jsonConn.FilePath)
        let jsonRows = jsonConn.Rows<SimpleItem>() |> Seq.toList
        Assert.Equal(1, jsonRows.Length)
        let jsonTableRows = jsonConn.Table<SimpleItem>("Items") |> Seq.toList
        Assert.Equal(1, jsonTableRows.Length)
        let jsonQ = jsonConn.AsQueryable<SimpleItem>()
        Assert.NotNull(jsonQ)
        let jsonRaw = jsonConn.Raw()
        Assert.Equal(1, jsonRaw.Rows |> Seq.length)

        // 4. XmlConnection
        let xmlConn = Connections.getXmlConnection "MyXml"
        Assert.Equal("MyXml", xmlConn.Name)
        Assert.Same(xml, xmlConn.Connector)
        Assert.Equal(xmlPath, xmlConn.FilePath)
        let xmlRows = xmlConn.Rows<SimpleItem>() |> Seq.toList
        Assert.Equal(1, xmlRows.Length)
        let xmlTableRows = xmlConn.Table<SimpleItem>("Item") |> Seq.toList
        Assert.Equal(1, xmlTableRows.Length)
        let xmlQ = xmlConn.AsQueryable<SimpleItem>()
        Assert.NotNull(xmlQ)
        let xmlRaw = xmlConn.Raw()
        Assert.Equal(1, xmlRaw.Rows |> Seq.length)

        // 5. DatabaseConnection
        let dbConn = Connections.getDatabaseConnection "MySqlite"
        Assert.Equal("MySqlite", dbConn.Name)
        Assert.Contains(dbPath, dbConn.ConnectionString)
        let execRes = dbConn.Execute("SELECT * FROM Items;")
        Assert.Equal(1, execRes.Rows |> Seq.length)
        let dbRows = dbConn.Query<SimpleItem>("SELECT * FROM Items;") |> Seq.toList
        Assert.Equal(1, dbRows.Length)
        Assert.Equal(300, dbRows.[0].Id)
        let dbQ = dbConn.AsQueryable<SimpleItem>("SELECT * FROM Items;")
        Assert.NotNull(dbQ)
        let dbQAll = dbConn.AsQueryable<SimpleItem>()
        Assert.NotNull(dbQAll)
        let rawRes = dbConn.rawQuery("SELECT * FROM Items;")
        Assert.Equal(1, rawRes.Rows |> Seq.length)
        Assert.Contains("Items", dbConn.Tables)
        Assert.True(dbConn.Schema.Length > 0)
        let entities = dbConn.GenerateEntities()
        Assert.Contains("module MySqliteDb", entities)
        let sqlProvider = dbConn.GenerateSqlProvider()
        Assert.Contains("SqlDataProvider", sqlProvider)

        // Test toRawSql, toRawQuery, and toRawSqlWithParameters
        let pushdownQ = dbConn.Table<SimpleItem>("Items")
        let rawSql = Connections.toRawSql pushdownQ
        Assert.Equal("SELECT * FROM Items", rawSql)

        let formattedQuery = Connections.toRawQuery pushdownQ
        Assert.Contains("SELECT", formattedQuery)

        let (sqlWithParams, pList) = Connections.toRawSqlWithParameters pushdownQ
        Assert.Equal("SELECT * FROM Items", sqlWithParams)
        Assert.Empty(pList)
    finally
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
        try Directory.Delete(tmpDir, true) with _ -> ()

[<Fact>]
let ``Connections sql category buildPivotSql and pivot execute cross-tabulation on staged SQLite engine`` () =
    let tmpDbPath = Path.Combine(Path.GetTempPath(), "fqs-pivot-test-" + Guid.NewGuid().ToString("N") + ".db")
    try
        let engine = FileSqliteEngine(tmpDbPath)
        Connections.setEngine engine

        // 1. Test buildPivotSql statically and via instance
        let sql1 = Connections.sql.buildPivotSql("Sales", [ "Region" ], "Quarter", "Revenue", [ "Q1"; "Q2" ])
        Assert.Contains("SUM(CASE WHEN Quarter = 'Q1' THEN Revenue END) AS \"Q1\"", sql1)
        Assert.Contains("SUM(CASE WHEN Quarter = 'Q2' THEN Revenue END) AS \"Q2\"", sql1)
        Assert.Contains("GROUP BY Region", sql1)

        // With empty pivotVals
        let sqlEmpty = Connections.sql.buildPivotSql("Sales", [ "Region" ], "Quarter", "Revenue", [])
        Assert.Equal("SELECT Region FROM Sales GROUP BY Region;", sqlEmpty)

        // With custom aggFunc
        let sqlAvg = Connections.sql.buildPivotSql("Sales", [ "Region" ], "Quarter", "Revenue", [ "Q1" ], aggFunc = "AVG")
        Assert.Contains("AVG(CASE WHEN Quarter = 'Q1' THEN Revenue END) AS \"Q1\"", sqlAvg)

        // 2. Test pivot directly against staged data in engine
        engine.ExecuteQuery("""
            CREATE TABLE RegionalSales (
                Region TEXT,
                Quarter TEXT,
                Amount REAL
            );
        """) |> ignore

        engine.ExecuteQuery("""
            INSERT INTO RegionalSales VALUES
                ('North', 'Q1', 100.0),
                ('North', 'Q2', 150.0),
                ('South', 'Q1', 200.0),
                ('South', 'Q2', 250.0);
        """) |> ignore

        let pivotResult = Connections.sql.pivot("RegionalSales", [ "Region" ], "Quarter", "Amount")
        Assert.Contains("Region", pivotResult.Columns)
        Assert.Contains("Q1", pivotResult.Columns)
        Assert.Contains("Q2", pivotResult.Columns)

        let rows = pivotResult.Rows |> Seq.toList
        Assert.Equal(2, rows.Length)
    finally
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
        try File.Delete tmpDbPath with _ -> ()

type JsonRecordPayload = { Id: int; Name: string }

[<Fact>]
let ``Connections queryJson handles root array, nested array object, and single object payloads`` () =
    let testWithServer (jsonBody: string) (action: RestConnector -> unit) =
        let tcp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)
        tcp.Start()
        let port = (tcp.LocalEndpoint :?> System.Net.IPEndPoint).Port
        tcp.Stop()

        let listener = new System.Net.HttpListener()
        listener.Prefixes.Add($"http://127.0.0.1:{port}/")
        listener.Start()

        let serverTask =
            System.Threading.Tasks.Task.Run(fun () ->
                try
                    let ctx = listener.GetContext()
                    let bytes = Encoding.UTF8.GetBytes(jsonBody)
                    ctx.Response.ContentType <- "application/json"
                    ctx.Response.ContentLength64 <- int64 bytes.Length
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length)
                    ctx.Response.OutputStream.Flush()
                    ctx.Response.Close()
                finally
                    listener.Stop()
            )

        let connector = RestConnector(RestRequestOptions.Get($"http://127.0.0.1:{port}/test"))
        try
            action connector
            serverTask.Wait()
        with _ ->
            listener.Stop()
            reraise ()

    // 1. Root array of objects mapped to F# records
    testWithServer """[ { "Id": 1, "Name": "First" }, { "Id": 2, "Name": "Second" } ]""" (fun rest ->
        Connections.configure(FakeResolver(Map.ofList [ "Api", rest :> IConnector ]))
        let items = Connections.queryJson<JsonRecordPayload> "Api" |> Seq.toList
        Assert.Equal(2, items.Length)
        Assert.Equal(1, items.[0].Id)
        Assert.Equal("First", items.[0].Name)
    )

    // 2. Root object containing a nested array of objects
    testWithServer """{ "status": "ok", "items": [ { "Id": 10, "Name": "Nested" } ] }""" (fun rest ->
        Connections.configure(FakeResolver(Map.ofList [ "Api", rest :> IConnector ]))
        let items = Connections.queryJson<JsonRecordPayload> "Api" |> Seq.toList
        Assert.Equal(1, items.Length)
        Assert.Equal(10, items.[0].Id)
        Assert.Equal("Nested", items.[0].Name)
    )

    // 3. Single root object mapped to F# record
    testWithServer """{ "Id": 99, "Name": "Solo" }""" (fun rest ->
        Connections.configure(FakeResolver(Map.ofList [ "Api", rest :> IConnector ]))
        let items = Connections.queryJson<JsonRecordPayload> "Api" |> Seq.toList
        Assert.Equal(1, items.Length)
        Assert.Equal(99, items.[0].Id)
        Assert.Equal("Solo", items.[0].Name)
    )

[<Fact>]
let ``Connections convertValue converts numeric, boolean, date, and sentinel types across all branches`` () =
    // 1. Null, DBNull, and Sentinel checks
    Assert.Null(Connections.convertValue null typeof<int>)
    Assert.Null(Connections.convertValue DBNull.Value typeof<string>)
    Assert.Null(Connections.convertValue (box "N/A") typeof<int>)
    Assert.Null(Connections.convertValue (box "-") typeof<decimal>)
    Assert.Null(Connections.convertValue (box "null") typeof<double>)

    // 2. Non-finite floating point checks
    Assert.Null(Connections.convertValue (box Double.NaN) typeof<double>)
    Assert.Null(Connections.convertValue (box Double.PositiveInfinity) typeof<double>)
    Assert.Null(Connections.convertValue (box Single.NaN) typeof<single>)
    Assert.Null(Connections.convertValue (box Single.NegativeInfinity) typeof<single>)

    // 3. Option type conversions
    let optSome = Connections.convertValue (box "42") typeof<int option>
    Assert.Equal(box (Some 42), optSome)
    let optSentinel = Connections.convertValue (box "none") typeof<int option>
    Assert.Null(optSentinel)

    // 4. String to Boolean variations
    Assert.Equal(box true, Connections.convertValue (box "yes") typeof<bool>)
    Assert.Equal(box true, Connections.convertValue (box "y") typeof<bool>)
    Assert.Equal(box true, Connections.convertValue (box "sim") typeof<bool>)
    Assert.Equal(box true, Connections.convertValue (box "s") typeof<bool>)
    Assert.Equal(box true, Connections.convertValue (box "1") typeof<bool>)
    Assert.Equal(box false, Connections.convertValue (box "no") typeof<bool>)
    Assert.Equal(box false, Connections.convertValue (box "n") typeof<bool>)
    Assert.Equal(box false, Connections.convertValue (box "nao") typeof<bool>)
    Assert.Equal(box false, Connections.convertValue (box "não") typeof<bool>)
    Assert.Equal(box false, Connections.convertValue (box "0") typeof<bool>)

    // 5. String to int16, int64, single
    Assert.Equal(box 123s, Connections.convertValue (box "123") typeof<int16>)
    Assert.Equal(box 9876543210L, Connections.convertValue (box "9876543210") typeof<int64>)
    Assert.Equal(box 12.5f, Connections.convertValue (box "12.5") typeof<single>)

    // 6. String to DateTime and DateOnly (including OADate)
    let dtStr = Connections.convertValue (box "2026-11-16 14:30:00") typeof<DateTime>
    Assert.Equal(box (DateTime(2026, 11, 16, 14, 30, 0)), dtStr)
    let dOnlyStr = Connections.convertValue (box "2026-11-16") typeof<DateOnly>
    Assert.Equal(box (DateOnly(2026, 11, 16)), dOnlyStr)
    let oaDate = DateTime(2026, 11, 16).ToOADate()
    let dtFromOa = Connections.convertValue (box (string oaDate)) typeof<DateTime>
    Assert.Equal(box (DateTime(2026, 11, 16)), dtFromOa)

    // 7. String to Guid
    let g = Guid.NewGuid()
    let gParsed = Connections.convertValue (box (string g)) typeof<Guid>
    Assert.Equal(box g, gParsed)

    // 8. Non-string conversions: DateOnly, DateTimeOffset, OADate to DateTime and DateOnly
    let dto = DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero)
    let dtFromDto = Connections.convertValue (box dto) typeof<DateTime>
    Assert.Equal(box dto.DateTime, dtFromDto)
    let dOnlyFromDto = Connections.convertValue (box dto) typeof<DateOnly>
    Assert.Equal(box (DateOnly(2026, 11, 16)), dOnlyFromDto)

    let dOnly = DateOnly(2026, 11, 16)
    let dtFromDOnly = Connections.convertValue (box dOnly) typeof<DateTime>
    Assert.Equal(box (DateTime(2026, 11, 16, 0, 0, 0)), dtFromDOnly)
    let dOnlyFromDt = Connections.convertValue (box (DateTime(2026, 11, 16))) typeof<DateOnly>
    Assert.Equal(box dOnly, dOnlyFromDt)

    // 9. Numeric conversions (decimal, double, int, int64, int16)
    Assert.Equal(box 42.0M, Connections.convertValue (box 42) typeof<decimal>)
    Assert.Equal(box 42.0M, Connections.convertValue (box 42L) typeof<decimal>)
    Assert.Equal(box 42.0M, Connections.convertValue (box 42s) typeof<decimal>)
    Assert.Equal(box 42.5M, Connections.convertValue (box 42.5) typeof<decimal>)
    Assert.Equal(box 42.5M, Connections.convertValue (box 42.5f) typeof<decimal>)
    Assert.Equal(box 100.0, Connections.convertValue (box 100M) typeof<double>)
    Assert.Equal(box 100.0, Connections.convertValue (box 100f) typeof<double>)
    Assert.Equal(box 100.0, Connections.convertValue (box 100L) typeof<double>)
    Assert.Equal(box 100, Connections.convertValue (box 99.9) typeof<int>)
    Assert.Equal(box 100L, Connections.convertValue (box 99.9) typeof<int64>)

    // 10. String numeric fallbacks and clamping
    Assert.Equal(box 1000M, Connections.convertValue (box "1e3") typeof<decimal>)
    Assert.Null(Connections.convertValue (box "invalid_double") typeof<double>)
    Assert.Null(Connections.convertValue (box "1e40") typeof<single>)
    Assert.Equal(box Int32.MaxValue, Connections.convertValue (box "99999999999999") typeof<int>)
    Assert.Equal(box Int32.MinValue, Connections.convertValue (box "-99999999999999") typeof<int>)
    Assert.Equal(box Int64.MaxValue, Connections.convertValue (box "99999999999999999999") typeof<int64>)
    Assert.Equal(box Int64.MinValue, Connections.convertValue (box "-99999999999999999999") typeof<int64>)
    Assert.Equal(box Int16.MaxValue, Connections.convertValue (box "999999999") typeof<int16>)
    Assert.Equal(box Int16.MinValue, Connections.convertValue (box "-999999999") typeof<int16>)

    // 11. String parsing failure fallbacks
    Assert.Null(Connections.convertValue (box "maybe") typeof<bool>)
    Assert.Null(Connections.convertValue (box "bad_date") typeof<DateTime>)
    Assert.Null(Connections.convertValue (box "bad_date") typeof<DateOnly>)
    Assert.Null(Connections.convertValue (box "bad_guid") typeof<Guid>)
    Assert.Null(Connections.convertValue (box "not_convertible") typeof<Uri>)

    // 12. Non-string conversions across types and clamps
    Assert.Equal(box "123", Connections.convertValue (box 123) typeof<string>)
    Assert.Equal(box 50M, Connections.convertValue (box "50") typeof<decimal>)
    Assert.Null(Connections.convertValue (box Single.PositiveInfinity) typeof<double>)
    Assert.Null(Connections.convertValue (box Double.PositiveInfinity) typeof<int>)
    Assert.Equal(box 123, Connections.convertValue (box 123.4M) typeof<int>)
    Assert.Null(Connections.convertValue (box Double.NegativeInfinity) typeof<int64>)
    Assert.Equal(box 457L, Connections.convertValue (box 456.7M) typeof<int64>)
    Assert.Equal(box (DateTime(2026, 11, 16)), Connections.convertValue (box oaDate) typeof<DateTime>)
    Assert.Equal(box (DateOnly(2026, 11, 16)), Connections.convertValue (box oaDate) typeof<DateOnly>)
    Assert.Equal(box (DateOnly(2026, 11, 16)), Connections.convertValue (box "2026-11-16") typeof<DateOnly>)

[<Fact>]
let ``Connections queryEngine, queryEngineWith, and queryExcel operate as expected`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), "fqs-eng-excel-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory tmpDir |> ignore
    let dbPath = Path.Combine(tmpDir, "engine.db")
    let engine = FileSqliteEngine(dbPath)
    Connections.setEngine engine

    try
        let resEngine = Connections.queryEngine "SELECT 1 AS Num;"
        Assert.Equal(1, resEngine.Rows |> Seq.length)

        let resEngineWith = Connections.queryEngineWith "SELECT @x AS Val;" [ "@x", box 42 ]
        Assert.Equal(box 42L, (resEngineWith.Rows |> Seq.head).[0])

        // queryExcel
        let xlsxPath = Path.Combine(tmpDir, "simple.xlsx")
        do
            use wb = new ClosedXML.Excel.XLWorkbook()
            let ws = wb.Worksheets.Add("Sheet1")
            ws.Cell(1, 1).Value <- "Id"
            ws.Cell(1, 2).Value <- "Title"
            ws.Cell(2, 1).Value <- 5
            ws.Cell(2, 2).Value <- "Item 5"
            wb.SaveAs(xlsxPath)

        let excelConn = ExcelConnector(xlsxPath)
        Connections.configure(FakeResolver(Map.ofList [ "ExcelConn", excelConn :> IConnector ]))

        let items = Connections.queryExcel<SimpleItem> "ExcelConn" None |> Seq.toList
        Assert.Equal(1, items.Length)
        Assert.Equal(5, items.[0].Id)
        Assert.Equal("Item 5", items.[0].Title)
    finally
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
        try Directory.Delete(tmpDir, true) with _ -> ()

type NonStreamingMockConnector(kind: ConnectorKind, connStr: string, queryResult: ConnectorQueryResult) =
    interface IQueryableConnector with
        member _.Kind = kind
        member _.ConnectionString = connStr
        member _.TestConnectionAsync(_ct) = System.Threading.Tasks.Task.FromResult true
        member _.DiscoverSchemaAsync(_ct) = System.Threading.Tasks.Task.FromResult []
        member _.ExecuteQuery (sql: string) (parameters: (string * obj) list) = queryResult

[<Fact>]
let ``Connections query and DatabaseConnection fall back when connector is non-streaming or resolver is absent`` () =
    let qRes = {
        Columns = [ "Id"; "Title" ]
        Rows = [| [| box 10; box "NonStreamingItem" |] |]
    }
    let mockSql = NonStreamingMockConnector(ConnectorKind.Sqlite, "Data Source=:memory:", qRes)

    // With resolver: query falls back to executeSqlRaw + getMapper
    Connections.configure(FakeResolver(Map.ofList [ "MockSql", mockSql :> IConnector ]))
    let rows = Connections.query<SimpleItem> "MockSql" "SELECT * FROM Table" [] |> Seq.toList
    Assert.Equal(1, rows.Length)
    Assert.Equal(10, rows.[0].Id)
    Assert.Equal("NonStreamingItem", rows.[0].Title)

    // DatabaseConnection with resolver = None
    let dbConn = Connections.DatabaseConnection("DirectConn", mockSql)
    // Clear resolver
    Connections.reset()
    let execRes = dbConn.Execute("SELECT 1;")
    Assert.Equal(1, execRes.Rows |> Seq.length)

    let rawRes = dbConn.rawQuery("SELECT 1;")
    Assert.Equal(1, rawRes.Rows |> Seq.length)

    let directRows = dbConn.Query<SimpleItem>("SELECT * FROM Table;") |> Seq.toList
    Assert.Equal(1, directRows.Length)
    Assert.Equal(10, directRows.[0].Id)

[<Fact>]
let ``Connections syncDataSourcesAsyncWithProgress stages Json and Xml files and background sync completes`` () =
    task {
        BuiltinConnectors.registerDefaults ()
        let tmp = Path.Combine(Path.GetTempPath(), "fqs-json-xml-sync-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory tmp |> ignore
        try
            let jsonPath = Path.Combine(tmp, "catalog.json")
            File.WriteAllText(jsonPath, """{ "Products": [ { "Id": 1, "Name": "Apples" } ] }""")

            let xmlPath = Path.Combine(tmp, "inventory.xml")
            File.WriteAllText(xmlPath, """<Inventory><Stock><Id>10</Id><Qty>100</Qty></Stock></Inventory>""")

            let jsonDef: ConnectionDefinition =
                { Id = Guid.NewGuid()
                  Name = "CatalogJson"
                  Kind = ConnectorKind.Json
                  Configuration = System.Text.Json.JsonDocument.Parse($"""{{"filePath": "{jsonPath.Replace("\\", "/")}"}}""").RootElement
                  SecretReference = None
                  IsTrusted = true }

            let xmlDef: ConnectionDefinition =
                { Id = Guid.NewGuid()
                  Name = "InventoryXml"
                  Kind = ConnectorKind.Xml
                  Configuration = System.Text.Json.JsonDocument.Parse($"""{{"filePath": "{xmlPath.Replace("\\", "/")}"}}""").RootElement
                  SecretReference = None
                  IsTrusted = true }

            let secretStore = FSharpQueryStudio.Security.InMemorySecretStore() :> FSharpQueryStudio.Security.ISecretStore
            let events = System.Collections.Concurrent.ConcurrentBag<StagingProgressEvent>()

            // 1. Initial sync with progress
            let! summary = Connections.syncDataSourcesAsyncWithProgress tmp [ jsonDef; xmlDef ] secretStore (Some events.Add)
            Assert.True(summary.StagedCount >= 2)
            Assert.Contains(events, fun e -> e.Phase = "Completed")

            // 2. Second sync - tables are skipped
            let skipEvents = System.Collections.Concurrent.ConcurrentBag<StagingProgressEvent>()
            let! summarySkip = Connections.syncDataSourcesAsyncWithProgress tmp [ jsonDef; xmlDef ] secretStore (Some skipEvents.Add)
            Assert.Equal(0, summarySkip.StagedCount)
            Assert.True(summarySkip.SkippedCount >= 2)
            Assert.Contains(skipEvents, fun e -> e.Phase = "Skipped")

            // 3. triggerBackgroundSync and waitForPendingSync
            Connections.triggerBackgroundSync tmp [ jsonDef; xmlDef ] secretStore
            Connections.waitForPendingSync ()
        finally
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
            try Directory.Delete(tmp, true) with _ -> ()
    }

[<Fact>]
let ``SequencePivotExtensions Seq pivot performs in-memory grouping and cross-tabulation`` () =
    let sales = [
        ("North", "Q1", 100)
        ("North", "Q2", 150)
        ("South", "Q1", 200)
        ("South", "Q2", 300)
    ]

    let pivoted =
        sales
        |> Seq.pivot (fun (region, _, _) -> region) (fun (_, quarter, _) -> quarter) (fun (_, _, amount) -> amount) Seq.sum
        |> Map.ofSeq

    Assert.Equal(100, pivoted.["North"].["Q1"])
    Assert.Equal(150, pivoted.["North"].["Q2"])
    Assert.Equal(200, pivoted.["South"].["Q1"])
    Assert.Equal(300, pivoted.["South"].["Q2"])

[<Fact>]
let ``Connections queryJsonWithOptions deserializes non-record types across various JSON payloads`` () =
    let testWithServer (jsonBody: string) (action: RestConnector -> unit) =
        let tcp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)
        tcp.Start()
        let port = (tcp.LocalEndpoint :?> System.Net.IPEndPoint).Port
        tcp.Stop()

        let listener = new System.Net.HttpListener()
        listener.Prefixes.Add($"http://127.0.0.1:{port}/")
        listener.Start()

        let serverTask =
            System.Threading.Tasks.Task.Run(fun () ->
                try
                    let ctx = listener.GetContext()
                    let bytes = Encoding.UTF8.GetBytes(jsonBody)
                    ctx.Response.ContentType <- "application/json"
                    ctx.Response.ContentLength64 <- int64 bytes.Length
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length)
                    ctx.Response.OutputStream.Flush()
                    ctx.Response.Close()
                finally
                    listener.Stop()
            )

        let connector = RestConnector(RestRequestOptions.Get($"http://127.0.0.1:{port}/test"))
        try
            action connector
            serverTask.Wait()
        with _ ->
            listener.Stop()
            reraise ()

    // 1. Array of primitives into int seq
    testWithServer "[ 10, 20, 30 ]" (fun rest ->
        Connections.configure(FakeResolver(Map.ofList [ "Api", rest :> IConnector ]))
        let items = Connections.queryJsonWithOptions<int> "Api" None |> Seq.toList
        Assert.Equal<int list>([ 10; 20; 30 ], items)
    )

    // 2. Object with nested array of primitives into int seq
    testWithServer """{ "values": [ 1, 2, 3 ] }""" (fun rest ->
        Connections.configure(FakeResolver(Map.ofList [ "Api", rest :> IConnector ]))
        let items = Connections.queryJsonWithOptions<int> "Api" None |> Seq.toList
        Assert.Equal<int list>([ 1; 2; 3 ], items)
    )

    // 3. Single object into non-record dictionary
    testWithServer """{ "key": "value" }""" (fun rest ->
        Connections.configure(FakeResolver(Map.ofList [ "Api", rest :> IConnector ]))
        let items = Connections.queryJsonWithOptions<System.Collections.Generic.Dictionary<string, string>> "Api" None |> Seq.toList
        Assert.Equal(1, items.Length)
        Assert.Equal("value", items.[0].["key"])
    )

    // 4. Primitive scalar response into int seq
    testWithServer "42" (fun rest ->
        Connections.configure(FakeResolver(Map.ofList [ "Api", rest :> IConnector ]))
        let items = Connections.queryJsonWithOptions<int> "Api" None |> Seq.toList
        Assert.Empty(items)
    )

    Assert.Equal(box 100.0, Connections.convertValue (box 100M) typeof<double>)
    Assert.Equal(box 100.0, Connections.convertValue (box 100f) typeof<double>)
    Assert.Equal(box 100.0, Connections.convertValue (box 100L) typeof<double>)
    Assert.Equal(box 100, Connections.convertValue (box 99.9) typeof<int>)
    Assert.Equal(box 100L, Connections.convertValue (box 99.9) typeof<int64>)

type private TestWorkspaceResolver(baseDir: string option, connections: Map<string, IConnector>) =
    interface IConnectionResolver with
        member _.Resolve(name: string) =
            match connections.TryFind name with
            | Some c -> c
            | None -> failwith $"No connection named '{name}' is configured."
    interface IWorkspaceAwareResolver with
        member _.BaseDirectory = baseDir

type ComplexJsonRecord = {
    StrVal: string
    IntVal: int
    LongVal: int64
    DecVal: decimal
    DblVal: double
    BoolTrue: bool
    BoolFalse: bool
    NullVal: string
    RawArr: string
}

[<Fact>]
let ``Connections workspace resolver and reset behaviour works as expected`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), "fqs-ws-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory tmpDir |> ignore
    try
        // 1. Configure with IWorkspaceAwareResolver
        let wsResolver = TestWorkspaceResolver(Some tmpDir, Map.empty)
        Connections.configure wsResolver
        let engWs = Connections.getEngine ()
        Assert.Contains(tmpDir, engWs.DatabasePath)

        // 2. Reset clears resolver and engine
        Connections.reset ()
        let exUnconf = Assert.Throws<Exception>(fun () -> Connections.getDatabaseConnection "Any" |> ignore)
        Assert.Contains("Connections has not been configured for this session.", exUnconf.Message)

        // 3. getEngine with resolver = None defaults to temp path
        let engDefault = Connections.getEngine ()
        Assert.Contains("fsqs_cache", engDefault.DatabasePath)
    finally
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
        try Directory.Delete(tmpDir, true) with _ -> ()

[<Fact>]
let ``DatabaseConnection GenerateEntities and GenerateSqlProvider output valid F# source snippets`` () =
    withTempSqlite (fun sqlite _ ->
        let dbConn = Connections.DatabaseConnection("Sales", sqlite)
        let entities = dbConn.GenerateEntities()
        Assert.Contains("module SalesDb =", entities)
        Assert.Contains("type Orders =", entities)
        Assert.Contains("type Database", entities)

        let sqlProvider = dbConn.GenerateSqlProvider("SalesDb")
        Assert.Contains("type SalesDb = SqlDataProvider<", sqlProvider)
        Assert.Contains("Common.DatabaseProviderTypes.SQLITE", sqlProvider)
    )

[<Fact>]
let ``Typed connections pushdown and table queryables stage and execute queries`` () =
    let tmpDir = Path.Combine(Path.GetTempPath(), "fqs-pushdown-test-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory tmpDir |> ignore
    let dbPath = Path.Combine(tmpDir, "engine.db")
    let engine = FileSqliteEngine(dbPath)
    Connections.setEngine engine

    try
        // 1. Prepare files
        let csvPath = Path.Combine(tmpDir, "items.csv")
        File.WriteAllLines(csvPath, [| "Id,Title"; "1,Apple"; "2,Banana" |])
        let csvConn = CsvConnector(csvPath)

        let xlsxPath = Path.Combine(tmpDir, "items.xlsx")
        do
            use wb = new ClosedXML.Excel.XLWorkbook()
            let ws = wb.Worksheets.Add("Products")
            ws.Cell(1, 1).Value <- "Id"
            ws.Cell(1, 2).Value <- "Title"
            ws.Cell(2, 1).Value <- 10
            ws.Cell(2, 2).Value <- "Peach"
            wb.SaveAs(xlsxPath)
        let excelConn = ExcelConnector(xlsxPath)

        let jsonPath = Path.Combine(tmpDir, "data.json")
        File.WriteAllText(jsonPath, """{ "Items": [ { "Id": 100, "Title": "Grape" } ] }""")
        let jsonConn = JsonConnector(jsonPath)

        let xmlPath = Path.Combine(tmpDir, "data.xml")
        File.WriteAllText(xmlPath, """<Catalog><Items><Id>200</Id><Title>Melon</Title></Items></Catalog>""")
        let xmlConn = XmlConnector(xmlPath)

        Connections.configure(FakeResolver(Map.ofList [
            "LocalCsv", csvConn :> IConnector
            "LocalExcel", excelConn :> IConnector
            "LocalJson", jsonConn :> IConnector
            "LocalXml", xmlConn :> IConnector
        ]))

        // CSV
        let typedCsv = Connections.getCsvConnection "LocalCsv"
        let csvPush = typedCsv.PushdownQueryable<SimpleItem>()
        Assert.Equal(2, csvPush |> Seq.length)
        let csvRows = typedCsv.Rows<SimpleItem>() |> Seq.toList
        Assert.Equal(2, csvRows.Length)

        // Excel
        let typedExcel = Connections.getExcelConnection "LocalExcel"
        let excelPush = typedExcel.PushdownQueryable<SimpleItem>()
        Assert.Equal(1, excelPush |> Seq.length)
        let excelSheetQ = typedExcel.SheetQueryable<SimpleItem>("Products")
        Assert.Equal(1, excelSheetQ |> Seq.length)
        let excelTableQ = typedExcel.TableQueryable<SimpleItem>("Products")
        Assert.Equal(1, excelTableQ |> Seq.length)
        let excelTablePush = typedExcel.TablePushdownQueryable<SimpleItem>("Products")
        Assert.Equal(1, excelTablePush |> Seq.length)
        let excelRows = typedExcel.Rows<SimpleItem>() |> Seq.toList
        Assert.Equal(1, excelRows.Length)

        // JSON
        let typedJson = Connections.getJsonConnection "LocalJson"
        let jsonPush = typedJson.PushdownQueryable<SimpleItem>()
        Assert.Equal(1, jsonPush |> Seq.length)
        let jsonTableQ = typedJson.TableQueryable<SimpleItem>("Items")
        Assert.Equal(1, jsonTableQ |> Seq.length)
        let jsonTablePush = typedJson.TablePushdownQueryable<SimpleItem>("Items")
        Assert.Equal(1, jsonTablePush |> Seq.length)
        let jsonRows = typedJson.Rows<SimpleItem>() |> Seq.toList
        Assert.Equal(1, jsonRows.Length)

        // XML
        let typedXml = Connections.getXmlConnection "LocalXml"
        let xmlPush = typedXml.PushdownQueryable<SimpleItem>()
        Assert.Equal(1, xmlPush |> Seq.length)
        let xmlTableQ = typedXml.TableQueryable<SimpleItem>("Items")
        Assert.Equal(1, xmlTableQ |> Seq.length)
        let xmlTablePush = typedXml.TablePushdownQueryable<SimpleItem>("Items")
        Assert.Equal(1, xmlTablePush |> Seq.length)
        let xmlRows = typedXml.Rows<SimpleItem>() |> Seq.toList
        Assert.Equal(1, xmlRows.Length)

        // 5. ConnectorQueryResult extensions As<'T> and AsQueryable<'T>
        let rawRes = typedCsv.Raw()
        let itemsAs = rawRes.As<SimpleItem>() |> Seq.toList
        Assert.Equal(2, itemsAs.Length)
        let itemsAsQ = rawRes.AsQueryable<SimpleItem>()
        Assert.Equal(2, itemsAsQ |> Seq.length)

        // 6. syncDataSources non-progress alias
        let secretStore = FSharpQueryStudio.Security.InMemorySecretStore() :> FSharpQueryStudio.Security.ISecretStore
        let summary = (Connections.syncDataSources tmpDir [] secretStore).GetAwaiter().GetResult()
        Assert.Equal(0, summary.StagedCount)
    finally
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
        try Directory.Delete(tmpDir, true) with _ -> ()

[<Fact>]
let ``Connections queryJsonWithOptions deserializes complex JSON records covering all JsonValueKinds`` () =
    let jsonPayload = """
    [
        {
            "StrVal": "Hello",
            "IntVal": 42,
            "LongVal": 9999999999,
            "DecVal": 123.456,
            "DblVal": 1e20,
            "BoolTrue": true,
            "BoolFalse": false,
            "NullVal": null,
            "RawArr": [ 1, 2, 3 ]
        }
    ]
    """
    let tcp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)
    tcp.Start()
    let port = (tcp.LocalEndpoint :?> System.Net.IPEndPoint).Port
    tcp.Stop()

    let listener = new System.Net.HttpListener()
    listener.Prefixes.Add($"http://127.0.0.1:{port}/")
    listener.Start()

    let serverTask =
        System.Threading.Tasks.Task.Run(fun () ->
            try
                let ctx = listener.GetContext()
                let bytes = Encoding.UTF8.GetBytes(jsonPayload)
                ctx.Response.ContentType <- "application/json"
                ctx.Response.ContentLength64 <- int64 bytes.Length
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length)
                ctx.Response.OutputStream.Flush()
                ctx.Response.Close()
            finally
                listener.Stop()
        )

    let connector = RestConnector(RestRequestOptions.Get($"http://127.0.0.1:{port}/complex"))
    try
        Connections.configure(FakeResolver(Map.ofList [ "ComplexRest", connector :> IConnector ]))
        let typedRest = Connections.getRestConnection "ComplexRest"
        let queryable = typedRest.Queryable<ComplexJsonRecord>()
        let items = queryable |> Seq.toList
        serverTask.Wait()

        Assert.Equal(1, items.Length)
        let item = items.[0]
        Assert.Equal("Hello", item.StrVal)
        Assert.Equal(42, item.IntVal)
        Assert.Equal(9999999999L, item.LongVal)
        Assert.Equal(123.456M, item.DecVal)
        Assert.True(item.BoolTrue)
        Assert.False(item.BoolFalse)
        Assert.Equal("", item.NullVal)
        Assert.Equal("[ 1, 2, 3 ]", item.RawArr)
    with _ ->
        listener.Stop()
        reraise ()

[<Fact>]
let ``Connections syncDataSourcesAsyncWithProgress stages and skips Csv and Excel connections`` () =
    task {
        BuiltinConnectors.registerDefaults ()
        let tmp = Path.Combine(Path.GetTempPath(), "fqs-csv-excel-sync-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory tmp |> ignore
        try
            let csvPath = Path.Combine(tmp, "orders.csv")
            File.WriteAllLines(csvPath, [| "Id,Title"; "1,Alpha"; "2,Beta" |])

            let xlsxPath = Path.Combine(tmp, "orders.xlsx")
            do
                use wb = new ClosedXML.Excel.XLWorkbook()
                let ws = wb.Worksheets.Add("Orders")
                ws.Cell(1, 1).Value <- "Id"
                ws.Cell(1, 2).Value <- "Title"
                ws.Cell(2, 1).Value <- 100
                ws.Cell(2, 2).Value <- "Widget"
                wb.SaveAs(xlsxPath)

            let csvDef: ConnectionDefinition = {
                Id = Guid.NewGuid()
                Name = "OrdersCsv"
                Kind = ConnectorKind.Csv
                Configuration = System.Text.Json.JsonDocument.Parse($"""{{"filePath": "{csvPath.Replace("\\", "/")}"}}""").RootElement
                SecretReference = None
                IsTrusted = true
            }

            let excelDef: ConnectionDefinition = {
                Id = Guid.NewGuid()
                Name = "OrdersExcel"
                Kind = ConnectorKind.Excel
                Configuration = System.Text.Json.JsonDocument.Parse($"""{{"filePath": "{xlsxPath.Replace("\\", "/")}"}}""").RootElement
                SecretReference = None
                IsTrusted = true
            }

            let secretStore = FSharpQueryStudio.Security.InMemorySecretStore() :> FSharpQueryStudio.Security.ISecretStore
            let events = System.Collections.Concurrent.ConcurrentBag<StagingProgressEvent>()

            // 1. Initial sync with progress
            let! summary = Connections.syncDataSourcesAsyncWithProgress tmp [ csvDef; excelDef ] secretStore (Some events.Add)
            Assert.True(summary.StagedCount >= 2)
            Assert.Contains(events, fun e -> e.Phase = "Completed")

            // 2. Second sync - tables are skipped
            let skipEvents = System.Collections.Concurrent.ConcurrentBag<StagingProgressEvent>()
            let! summarySkip = Connections.syncDataSourcesAsyncWithProgress tmp [ csvDef; excelDef ] secretStore (Some skipEvents.Add)
            Assert.Equal(0, summarySkip.StagedCount)
            Assert.True(summarySkip.SkippedCount >= 2)
            Assert.Contains(skipEvents, fun e -> e.Phase = "Skipped")
        finally
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
            try Directory.Delete(tmp, true) with _ -> ()
    }

[<Fact>]
let ``Connections getMapper and sql category coverage helpers`` () =
    // 1. getMapper with obj array
    let arrMapper = Connections.getMapper<obj array> [ "Col1"; "Col2" ]
    let inputArr = [| box 1; box "text" |]
    Assert.Same(inputArr, arrMapper inputArr)

    // 2. getMapper with scalar int and string
    let intMapper = Connections.getMapper<int> [ "Col" ]
    Assert.Equal(42, intMapper [| box 42 |])
    Assert.Equal(0, intMapper [||])
    Assert.Equal(0, intMapper [| null |])

    let strMapper = Connections.getMapper<string> [ "Col" ]
    Assert.Equal("val", strMapper [| box "val" |])
    Assert.Equal("", strMapper [||])
    Assert.Equal("", strMapper [| null |])

    // 3. sql.rawQuery
    let rawQ = Connections.sql.rawQuery("SELECT 1 AS X;")
    Assert.Equal(1, rawQ.Rows |> Seq.length)

    // 4. QueryInspectionExtensions
    let mockSql = NonStreamingMockConnector(ConnectorKind.Sqlite, "Data Source=:memory:", { Columns = [ "Id" ]; Rows = [||] })
    let qObj = SqlPushdownQueryable<SimpleItem>(mockSql, "SimpleItem")
    let sqlVal = QueryInspectionExtensions.toRawSql qObj
    Assert.NotNull(sqlVal)
    let qVal = QueryInspectionExtensions.toRawQuery qObj
    Assert.NotNull(qVal)
    let (s, p) = QueryInspectionExtensions.toRawSqlWithParameters qObj
    Assert.NotNull(s)

    // 5. GenerateSqlProvider dialects
    let pgMock = NonStreamingMockConnector(ConnectorKind.PostgreSql, "Host=localhost", { Columns = []; Rows = [||] })
    let pgConn = Connections.DatabaseConnection("PgDb", pgMock)
    Assert.Contains("POSTGRESQL", pgConn.GenerateSqlProvider())

    let msMock = NonStreamingMockConnector(ConnectorKind.SqlServer, "Server=localhost", { Columns = []; Rows = [||] })
    let msConn = Connections.DatabaseConnection("MsDb", msMock)
    Assert.Contains("MSSQLSERVER", msConn.GenerateSqlProvider())

    let myMock = NonStreamingMockConnector(ConnectorKind.MySql, "Server=localhost", { Columns = []; Rows = [||] })
    let myConn = Connections.DatabaseConnection("MyDb", myMock)
    Assert.Contains("POSTGRESQL", myConn.GenerateSqlProvider())

[<Fact>]
let ``ConnectionStringSecrets, ConnectorRegistry, Descriptor, TabularDataMapping, and EntityGenerators full coverage`` () =
    // 1. ConnectionStringSecrets.resolve edge cases (lines 25, 27-28, 31-32, 39-41)
    let store = FSharpQueryStudio.Security.InMemorySecretStore() :> FSharpQueryStudio.Security.ISecretStore
    store.Save "ref1" "s3cr3t"
    Assert.Equal("postgres://localhost:5432/db", ConnectionStringSecrets.resolve store (Some "ref1") "postgres://localhost:5432/db")
    Assert.Equal("postgres://admin:s3cr3t@localhost:5432/db", ConnectionStringSecrets.resolve store (Some "ref1") "postgres://admin:old@localhost:5432/db")
    Assert.Equal("http://[::1:bad", ConnectionStringSecrets.resolve store (Some "ref1") "http://[::1:bad")
    Assert.Equal("Server=localhost;=invalid;Password=s3cr3t", ConnectionStringSecrets.resolve store (Some "ref1") "Server=localhost;=invalid;")

    // 2. ConnectorRegistry & IConnectorRegistry & WorkspaceConnectionResolver & SqliteDescriptor
    let reg = ConnectorRegistry()
    let ireg = reg :> IConnectorRegistry
    ireg.Register(SqliteDescriptor.Instance)
    Assert.True((ireg.TryGet "Sqlite").IsSome)
    Assert.NotEmpty(ireg.AllDescriptors())

    let unknownDef: ConnectionDefinition =
        { Id = Guid.NewGuid()
          Name = "UnknownConn"
          Kind = ConnectorKind("NoSuchKind")
          Configuration = System.Text.Json.JsonDocument.Parse("{}").RootElement
          SecretReference = None
          IsTrusted = true }
    Assert.True((ireg.Validate unknownDef).IsError)

    let invalidSqliteDef: ConnectionDefinition =
        { Id = Guid.NewGuid()
          Name = "BadSqlite"
          Kind = ConnectorKind.Sqlite
          Configuration = System.Text.Json.JsonDocument.Parse("{}").RootElement
          SecretReference = None
          IsTrusted = true }
    Assert.Throws<Exception>(fun () -> ireg.Resolve(invalidSqliteDef, store) |> ignore) |> ignore

    let validSqliteDef: ConnectionDefinition =
        { Id = Guid.NewGuid()
          Name = "GoodSqlite"
          Kind = ConnectorKind.Sqlite
          Configuration = System.Text.Json.JsonDocument.Parse("""{"filePath":":memory:"}""").RootElement
          SecretReference = None
          IsTrusted = true }
    Assert.NotNull(ireg.Resolve(validSqliteDef, store))

    let wsResolver = WorkspaceConnectionResolver([ unknownDef ], store, "C:/workspace")
    Assert.Equal(Some "C:/workspace", wsResolver.BaseDirectory)
    Assert.Equal(Some "C:/workspace", (wsResolver :> IWorkspaceAwareResolver).BaseDirectory)
    Assert.Throws<Exception>(fun () -> (wsResolver :> IConnectionResolver).Resolve("UnknownConn") |> ignore) |> ignore

    let sqliteDesc = SqliteDescriptor.Instance :> IConnectorDescriptor
    Assert.True(sqliteDesc.EntityGenerator.IsNone)
    let strJson = System.Text.Json.JsonDocument.Parse("\":memory:\"").RootElement
    let numJson = System.Text.Json.JsonDocument.Parse("123").RootElement
    Assert.NotNull(sqliteDesc.Factory.Create(strJson, store, None))
    Assert.NotNull(sqliteDesc.Factory.Create(numJson, store, None))

    // 3. ColumnTransformation & TabularTypeInference (TabularDataMapping.fs)
    Assert.Equal(box "84", ColumnTransformation.applyTransform "multiply(2)" (box 42))
    Assert.Equal(box "25", ColumnTransformation.applyTransform "multiply(2.5)" (box "10"))
    Assert.Equal(box "10", ColumnTransformation.applyTransform "multiply(bad)" (box "10"))
    Assert.Equal(box "abc", ColumnTransformation.applyTransform "multiply(2)" (box "abc"))
    Assert.Equal(box "10", ColumnTransformation.applyTransform "multiply(" (box "10"))
    Assert.Equal(box "5", ColumnTransformation.applyTransform "divide(2)" (box "10"))
    Assert.Equal(box "10", ColumnTransformation.applyTransform "divide(0)" (box "10"))
    Assert.Equal(box "abc", ColumnTransformation.applyTransform "divide(2)" (box "abc"))
    Assert.Equal(box "10", ColumnTransformation.applyTransform "divide(" (box "10"))
    Assert.Equal(box "1.5", ColumnTransformation.applyTransform "decimal" (box "1,5"))
    Assert.Equal(box "1.5", ColumnTransformation.applyTransform "double" (box "1,5"))
    Assert.Equal(box "hi", ColumnTransformation.applyTransform "toString" (box "hi"))
    Assert.Equal(box "hi", ColumnTransformation.applyTransform "string" (box "hi"))
    Assert.Equal(box "hi", ColumnTransformation.applyTransform "unknownOp" (box "hi"))
    Assert.Equal(box "x", ColumnTransformation.applyTransform "replace('a\"b', 'x')" (box "a\"b"))
    Assert.Equal(box "hi", ColumnTransformation.applyTransform "replace(" (box "hi"))

    Assert.Equal("1234.56", TabularTypeInference.normalizeNumeric "1,234.56")
    Assert.Equal("1234.56", TabularTypeInference.normalizeNumeric "1.234,56")
    Assert.Equal("string option", TabularTypeInference.inferType [ "N/A"; "-" ])
    Assert.Equal("string", TabularTypeInference.inferType [ "" ])

    // 4. Descriptor.fs
    let cat1 = ConnectorCategory.Custom("mycat")
    let cat2 = ConnectorCategory.Custom("MYCAT")
    Assert.Equal("mycat", cat1.ToString())
    Assert.True(cat1.Equals(box cat2))
    Assert.False(cat1.Equals(box "not-a-cat"))
    Assert.Equal(cat1.GetHashCode(), cat2.GetHashCode())

    let helpersDoc =
        System.Text.Json.JsonDocument.Parse(
            """{"nullProp":null,"bTrue":"true","bFalse":"false","bMaybe":"maybe","bNum":123,"mapObj":{"k":123},"mapStr":"a=1;b:2;bad","mapArr":[1,2]}"""
        ).RootElement
    Assert.Equal(None, JsonElementHelpers.tryGetString "nullProp" helpersDoc)
    Assert.Equal(Some true, JsonElementHelpers.tryGetBool "bTrue" helpersDoc)
    Assert.Equal(Some false, JsonElementHelpers.tryGetBool "bFalse" helpersDoc)
    Assert.Equal(None, JsonElementHelpers.tryGetBool "bMaybe" helpersDoc)
    Assert.Equal(None, JsonElementHelpers.tryGetBool "bNum" helpersDoc)
    Assert.Equal("123", (JsonElementHelpers.tryGetMap "mapObj" helpersDoc).["k"])
    Assert.Equal("2", (JsonElementHelpers.tryGetMap "mapStr" helpersDoc).["b"])
    Assert.True((JsonElementHelpers.tryGetMap "mapArr" helpersDoc).IsEmpty)

    let fileFields =
        [ { Name = "filePath"
            Label = "File"
            Type = ConnectorFieldType.File
            Required = true
            Placeholder = None
            DefaultValue = None
            SecretHandling = SecretHandling.None }
          { Name = "flag"
            Label = "Flag"
            Type = ConnectorFieldType.Boolean
            Required = false
            Placeholder = None
            DefaultValue = None
            SecretHandling = SecretHandling.IsolatedCredential } ]
    Assert.True((ConnectorDescriptor.validateRequiredFields fileFields strJson).IsOk)
    Assert.Equal("", ConnectorDescriptor.projectRootOf "")
    Assert.Equal("", ConnectorDescriptor.resolvePath "" "")
    Assert.NotEmpty(ConnectorDescriptor.resolvePath "/base" "~/file.csv")
    Assert.Equal("rel.csv", ConnectorDescriptor.resolvePath "" "rel.csv")
    Assert.Equal( System.Text.Json.JsonValueKind.String, (ConnectorDescriptor.resolveRelativeFilePaths fileFields strJson "C:/base").ValueKind)
    Assert.Equal( System.Text.Json.JsonValueKind.Number, (ConnectorDescriptor.resolveRelativeFilePaths fileFields numJson "C:/base").ValueKind)

    let customDesc =
        { new IConnectorDescriptor with
            member _.KindId = "Custom"
            member _.DisplayName = "Custom"
            member _.Category = cat1
            member _.Icon = "db"
            member _.FormFields = fileFields
            member _.ValidateConfiguration _ = Result.Ok ()
            member _.Factory = sqliteDesc.Factory
            member _.EntityGenerator = None }
    let dto = ConnectorDescriptor.toDto customDesc
    Assert.Equal(2, dto.FormFields.Length)

    // 5. EntityBackgroundGenerator & ModularEntityGenerators
    Assert.EndsWith("generated", EntityBackgroundGenerator.generatedDir "C:/proj/.fsqs")
    Assert.EndsWith("generated", EntityBackgroundGenerator.generatedDir "C:/Users/me/AppData/Local/FSharpQueryStudio")
    Assert.NotEmpty(EntityBackgroundGenerator.escapeIdent "let")
    Assert.Equal("a", EntityBackgroundGenerator.cleanSample " 'a' ")
    Assert.Equal("int", EntityBackgroundGenerator.inferType [ "1"; "2" ])
    Assert.Equal<string list>([ "A" ], EntityBackgroundGenerator.sanitizeColumns [ "A" ])

    let jsonTypesElem =
        System.Text.Json.JsonDocument.Parse(
            """{"items":[{"i":1,"l":999999999999,"d":1.25,"b":true,"dt":"2025-01-01T00:00:00","g":"550e8400-e29b-41d4-a716-446655440000","n":null}]}"""
        ).RootElement
    let inferredProps = EntityBackgroundGenerator.inferJsonTypes jsonTypesElem
    Assert.Equal(7, inferredProps.Length)
    Assert.Empty(JsonTypeInference.inferJsonTypes numJson)
    let singleObjElem = System.Text.Json.JsonDocument.Parse("""{"x": 1.5}""").RootElement
    Assert.NotEmpty(JsonTypeInference.inferJsonTypes singleObjElem)

    let dummyNonMatchingConn =
        { new IConnector with
            member _.Kind = ConnectorKind("Dummy")
            member _.TestConnectionAsync _ = System.Threading.Tasks.Task.FromResult true
            member _.DiscoverSchemaAsync _ = System.Threading.Tasks.Task.FromResult [] }
    Assert.Equal("", (SqlEntityGenerator() :> IConnectorEntityGenerator).EmitModuleCode("c", dummyNonMatchingConn, "M"))
    Assert.Equal("", (FSharpQueryStudio.Connectors.CsvEntityGenerator() :> IConnectorEntityGenerator).EmitModuleCode("c", dummyNonMatchingConn, "M"))
    Assert.Equal("", (ExcelEntityGenerator() :> IConnectorEntityGenerator).EmitModuleCode("c", dummyNonMatchingConn, "M"))
    Assert.Equal("", (JsonEntityGenerator() :> IConnectorEntityGenerator).EmitModuleCode("c", dummyNonMatchingConn, "M"))
    Assert.Equal("", (XmlEntityGenerator() :> IConnectorEntityGenerator).EmitModuleCode("c", dummyNonMatchingConn, "M"))
    Assert.Equal("", (RestEntityGenerator() :> IConnectorEntityGenerator).EmitModuleCode("c", dummyNonMatchingConn, "M"))

    let tmpWs = Path.Combine(Path.GetTempPath(), $"fqs_ebg_{Guid.NewGuid():N}")
    Directory.CreateDirectory tmpWs |> ignore
    try
        let csvFile = Path.Combine(tmpWs, "data.csv")
        File.WriteAllText(csvFile, "Id,Name\n1,Alpha\n")
        let c1: ConnectionDefinition =
            { Id = Guid.NewGuid()
              Name = "sales-data"
              Kind = ConnectorKind.Csv
              Configuration = System.Text.Json.JsonDocument.Parse($"""{{"filePath":"{csvFile.Replace("\\", "/")}"}}""").RootElement
              SecretReference = None
              IsTrusted = true }
        let c2 = { c1 with Id = Guid.NewGuid(); Name = "sales_data" }
        let cBad = { c1 with Id = Guid.NewGuid(); Name = "bad_conn"; Kind = ConnectorKind("Unregistered") }

        let changed = (EntityBackgroundGenerator.generateForConnectionsAsync tmpWs [ c1; c2; cBad ] store).Result
        Assert.True(changed)
        let _ = (EntityBackgroundGenerator.generateForConnectionsAsync tmpWs [ cBad ] store).Result
        EntityBackgroundGenerator.ensureGenerated tmpWs [] store
        EntityBackgroundGenerator.waitForPendingGeneration ()
    finally
        try Directory.Delete(tmpWs, true) with _ -> ()

[<Fact>]
let ``Direct SQLite queries work on mapped CSV and XML connections without prior LINQ staging using connection, file, or element names`` () =
    BuiltinConnectors.registerDefaults ()
    let tmpWs = Path.Combine(Path.GetTempPath(), $"fqs_direct_sqlite_{Guid.NewGuid():N}")
    Directory.CreateDirectory tmpWs |> ignore
    try
        let csvPath = Path.Combine(tmpWs, "sales_feed.csv")
        File.WriteAllLines(csvPath, [|
            "OrderId,Customer,Amount"
            "10,Alice,250.50"
            "20,Bob,99.00"
        |])

        let xmlPath = Path.Combine(tmpWs, "logistics_hub.xml")
        File.WriteAllText(xmlPath, """<?xml version="1.0" encoding="utf-8"?>
<LogisticsHub>
  <Carriers>
    <Carrier CarrierId="1" Name="FastExpress" Country="Brazil" />
    <Carrier CarrierId="2" Name="SkyCargo" Country="USA" />
  </Carriers>
  <Shipments>
    <Shipment ShipmentId="100" CarrierId="1" Destination="Sao Paulo" Cost="45.00" />
    <Shipment ShipmentId="200" CarrierId="2" Destination="Miami" Cost="120.00" />
  </Shipments>
</LogisticsHub>""")

        let sqlitePath = Path.Combine(tmpWs, "local_store.db")
        do
            use sConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={sqlitePath}")
            sConn.Open()
            use sCmd = sConn.CreateCommand()
            sCmd.CommandText <- "CREATE TABLE StoreCustomers (Name TEXT PRIMARY KEY, Tier TEXT); INSERT INTO StoreCustomers VALUES ('Alice', 'Gold');"
            sCmd.ExecuteNonQuery() |> ignore

        let csvDef: ConnectionDefinition =
            { Id = Guid.NewGuid()
              Name = "SalesFeed"
              Kind = ConnectorKind.Csv
              Configuration = System.Text.Json.JsonDocument.Parse($"""{{"filePath":"{csvPath.Replace("\\", "/")}"}}""").RootElement
              SecretReference = None
              IsTrusted = true }

        let xmlDef: ConnectionDefinition =
            { Id = Guid.NewGuid()
              Name = "LogisticsHub"
              Kind = ConnectorKind.Xml
              Configuration = System.Text.Json.JsonDocument.Parse($"""{{"filePath":"{xmlPath.Replace("\\", "/")}","includeAttributes":true,"flattenNested":true}}""").RootElement
              SecretReference = None
              IsTrusted = true }

        let sqliteDef: ConnectionDefinition =
            { Id = Guid.NewGuid()
              Name = "LocalStore"
              Kind = ConnectorKind.Sqlite
              Configuration = System.Text.Json.JsonDocument.Parse($"""{{"filePath":"{sqlitePath.Replace("\\", "/")}"}}""").RootElement
              SecretReference = None
              IsTrusted = true }

        let store = FSharpQueryStudio.Security.InMemorySecretStore() :> FSharpQueryStudio.Security.ISecretStore
        Connections.configure (WorkspaceConnectionResolver([ csvDef; xmlDef; sqliteDef ], store, tmpWs))

        // 1. Direct SQLite query via Connections.sql.rawQuery on CSV by file base name ('sales_feed') and connection name ('SalesFeed') BEFORE any LINQ query
        let resCsvByFile = Connections.sql.rawQuery("SELECT Customer, Amount FROM sales_feed WHERE OrderId = 10")
        Assert.Equal(1, resCsvByFile.Rows |> Seq.length)
        Assert.Equal("Alice", string (resCsvByFile.Rows |> Seq.head).[0])

        let resCsvByConn = Connections.sql.query("SELECT Customer FROM SalesFeed WHERE OrderId = 20")
        Assert.Equal("Bob", string (resCsvByConn.Rows |> Seq.head).[0])

        // 2. Direct SQLite query via Connections.sql.rawQuery on XML by element table name ('Carriers', 'Shipments'), prefixed name ('LogisticsHub_Carriers'), and connection name ('LogisticsHub')
        let resXmlCarriers = Connections.sql.rawQuery("SELECT Name, Country FROM Carriers ORDER BY CarrierId")
        Assert.Equal(2, resXmlCarriers.Rows |> Seq.length)
        Assert.Equal("FastExpress", string (resXmlCarriers.Rows |> Seq.head).[0])

        let resXmlShipments = Connections.queryEngine("SELECT Destination FROM Shipments WHERE ShipmentId = 200")
        Assert.Equal("Miami", string (resXmlShipments.Rows |> Seq.head).[0])

        let resXmlConnName = Connections.queryEngineWith "SELECT Name FROM LogisticsHub WHERE CarrierId = @id" [ "@id", box 2 ]
        Assert.Equal("SkyCargo", string (resXmlConnName.Rows |> Seq.head).[0])

        // 3. Direct SQLite query via CsvConnection.rawQuery and XmlConnection.rawQuery
        let csvConn = Connections.getCsvConnection "SalesFeed"
        let resCsvDirect = csvConn.rawQuery("SELECT COUNT(*) FROM sales_feed")
        Assert.Equal(2L, Convert.ToInt64((resCsvDirect.Rows |> Seq.head).[0]))

        let xmlConn = Connections.getXmlConnection "LogisticsHub"
        let resXmlDirect = xmlConn.rawQuery("SELECT s.Destination, c.Name FROM Shipments s JOIN Carriers c ON s.CarrierId = c.CarrierId ORDER BY s.ShipmentId")
        Assert.Equal(2, resXmlDirect.Rows |> Seq.length)

        // 4. Direct SQLite query on a registered SQLite connection (LocalStore) joining its own table with mapped CSV and XML tables!
        let dbConn = Connections.getDatabaseConnection "LocalStore"
        let resCrossJoin = dbConn.rawQuery("SELECT sc.Name, sc.Tier, sf.Amount, c.Name AS Carrier FROM StoreCustomers sc JOIN sales_feed sf ON sc.Name = sf.Customer JOIN Carriers c ON c.CarrierId = 1")
        let crossRows = resCrossJoin.Rows |> Seq.toList
        Assert.Single(crossRows) |> ignore
        Assert.Equal("Alice", string crossRows.[0].[0])
        Assert.Equal("Gold", string crossRows.[0].[1])
        Assert.Equal("FastExpress", string crossRows.[0].[3])
    finally
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
        try Directory.Delete(tmpWs, true) with _ -> ()

[<Fact>]
let ``DatabaseConnection.GenerateEntities maps multi-database and multi-schema tables into hierarchical tree structure while preserving default database top-level access`` () =
    let fakeMultiDbConnector =
        { new IQueryableConnector with
            member _.ConnectionString = "Server=localhost;Database=EcommerceDb;User Id=sa;Password=secret;"
            member _.ExecuteQuery _ _ = { Columns = []; Rows = Seq.empty }
          interface IConnector with
            member _.Kind = ConnectorKind.SqlServer
            member _.TestConnectionAsync _ = System.Threading.Tasks.Task.FromResult true
            member _.DiscoverSchemaAsync _ =
                System.Threading.Tasks.Task.FromResult [
                    { Name = "EcommerceDb.Customers"
                      Columns = [ { Name = "Id"; DataType = "int" }; { Name = "Email"; DataType = "nvarchar" } ] }
                    { Name = "EcommerceDb.Orders"
                      Columns = [ { Name = "OrderId"; DataType = "int" }; { Name = "Total"; DataType = "decimal" } ] }
                    { Name = "EcommerceDb.sales.Invoices"
                      Columns = [ { Name = "InvoiceId"; DataType = "int" }; { Name = "Amount"; DataType = "decimal" } ] }
                    { Name = "AnalyticsDb.Events"
                      Columns = [ { Name = "EventId"; DataType = "bigint" }; { Name = "Payload"; DataType = "nvarchar" } ] }
                    { Name = "AnalyticsDb.reporting.DailyMetrics"
                      Columns = [ { Name = "MetricDate"; DataType = "date" }; { Name = "Visits"; DataType = "int" } ] }
                ] }

    let dbConn = Connections.DatabaseConnection("EnterpriseServer", fakeMultiDbConnector)
    let code = dbConn.GenerateEntities()

    // 1. Both databases have their own modules and nested schema modules
    Assert.Contains("module EcommerceDb =", code)
    Assert.Contains("module AnalyticsDb =", code)
    Assert.Contains("module sales =", code)
    Assert.Contains("module reporting =", code)

    // 2. Default database ('EcommerceDb') tables are exposed at top level for backwards compatibility
    Assert.Contains("member _.Customers : FSharpQueryStudio.Connectors.SqlPushdownQueryable<EcommerceDb.Customers> = conn.Table<EcommerceDb.Customers>(\"Customers\")", code)
    Assert.Contains("member _.Orders : FSharpQueryStudio.Connectors.SqlPushdownQueryable<EcommerceDb.Orders> = conn.Table<EcommerceDb.Orders>(\"Orders\")", code)

    // 3. Tree structure on Database: conn.EcommerceDb.Customers, conn.EcommerceDb.sales.Invoices, conn.AnalyticsDb.Events, conn.AnalyticsDb.reporting.DailyMetrics
    Assert.Contains("member _.EcommerceDb =", code)
    Assert.Contains("Customers = conn.Table<EcommerceDb.Customers>(\"EcommerceDb.dbo.Customers\")", code)
    Assert.Contains("Invoices = conn.Table<EcommerceDb.sales.Invoices>(\"EcommerceDb.sales.Invoices\")", code)
    Assert.Contains("member _.AnalyticsDb =", code)
    Assert.Contains("Events = conn.Table<AnalyticsDb.Events>(\"AnalyticsDb.dbo.Events\")", code)
    Assert.Contains("DailyMetrics = conn.Table<AnalyticsDb.reporting.DailyMetrics>(\"AnalyticsDb.reporting.DailyMetrics\")", code)

[<Fact>]
let ``Direct SQLite queries via pivot, query, and generated entity code work for mapped CSV, XML, JSON and Excel connections`` () =
    BuiltinConnectors.registerDefaults ()
    let tmpWs = Path.Combine(Path.GetTempPath(), $"fqs_direct_sqlite2_{Guid.NewGuid():N}")
    Directory.CreateDirectory tmpWs |> ignore
    try
        let csvPath = Path.Combine(tmpWs, "customers_feed.csv")
        File.WriteAllLines(csvPath, [|
            "CustomerId,Region,Spend"
            "1,North,100"
            "2,South,200"
            "3,North,150"
        |])

        let xmlPath = Path.Combine(tmpWs, "catalog_items.xml")
        File.WriteAllText(xmlPath, """<?xml version="1.0" encoding="utf-8"?>
<Catalog>
  <Products>
    <Product Sku="P1" Category="Tech" Price="50" />
    <Product Sku="P2" Category="Home" Price="30" />
  </Products>
</Catalog>""")

        let csvDef: ConnectionDefinition =
            { Id = Guid.NewGuid()
              Name = "CustomersFeed"
              Kind = ConnectorKind.Csv
              Configuration = System.Text.Json.JsonDocument.Parse($"""{{"filePath":"{csvPath.Replace("\\", "/")}"}}""").RootElement
              SecretReference = None
              IsTrusted = true }

        let xmlDef: ConnectionDefinition =
            { Id = Guid.NewGuid()
              Name = "CatalogXml"
              Kind = ConnectorKind.Xml
              Configuration = System.Text.Json.JsonDocument.Parse($"""{{"filePath":"{xmlPath.Replace("\\", "/")}","includeAttributes":true,"flattenNested":true}}""").RootElement
              SecretReference = None
              IsTrusted = true }

        let store = FSharpQueryStudio.Security.InMemorySecretStore() :> FSharpQueryStudio.Security.ISecretStore
        Connections.configure (WorkspaceConnectionResolver([ csvDef; xmlDef ], store, tmpWs))

        // 1. Pivot directly on unstaged CSV by file base name ('customers_feed')
        let pivotRes = Connections.sql.pivot("customers_feed", [ "Region" ], "CustomerId", "Spend")
        Assert.Equal(2, pivotRes.Rows |> Seq.length)

        // 2. Query XML and CSV via .query and .Execute methods on CsvConnection and XmlConnection
        let csvConn = Connections.getCsvConnection "CustomersFeed"
        let qCsv = csvConn.query("SELECT SUM(Spend) FROM customers_feed WHERE Region = @r", [ "@r", box "North" ])
        Assert.Equal(250L, Convert.ToInt64((qCsv.Rows |> Seq.head).[0]))

        let xmlConn = Connections.getXmlConnection "CatalogXml"
        let qXml = xmlConn. query("SELECT Sku FROM Products WHERE Category = @c", [ "@c", box "Tech" ])
        Assert.Equal("P1", string (qXml.Rows |> Seq.head).[0])

        // 3. Verify generated entities include rawQuery, query, Execute, ISqlPushdownQueryable, and file base name property
        let _ = (EntityBackgroundGenerator.generateForConnectionsAsync tmpWs [ csvDef; xmlDef ] store).Result
        let genCode = File.ReadAllText(EntityBackgroundGenerator.generatedFilePath tmpWs)
        Assert.Contains("member _.rawQuery(", genCode)
        Assert.Contains("member _.customers_feed : FSharpQueryStudio.Connectors.SqlPushdownQueryable<Row> = queryable", genCode)
        Assert.Contains("interface FSharpQueryStudio.Connectors.ISqlPushdownQueryable with", genCode)
    finally
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
        try Directory.Delete(tmpWs, true) with _ -> ()

[<Fact>]
let ``DatabaseConnection.GenerateEntities supports PostgreSQL, MySQL, MariaDB and Oracle multi-database schemas`` () =
    let makeConnector (kind: ConnectorKind) (connStr: string) (tables: ConnectorTable list) =
        { new IQueryableConnector with
            member _.ConnectionString = connStr
            member _.ExecuteQuery _ _ = { Columns = []; Rows = Seq.empty }
          interface IConnector with
            member _.Kind = kind
            member _.TestConnectionAsync _ = System.Threading.Tasks.Task.FromResult true
            member _.DiscoverSchemaAsync _ = System.Threading.Tasks.Task.FromResult tables }

    let pgTables = [
        { Name = "fsqs_demo.products"; Columns = [ { Name = "id"; DataType = "int4" }; { Name = "name"; DataType = "text" } ] }
        { Name = "fsqs_demo.audit.logs"; Columns = [ { Name = "id"; DataType = "int8" } ] }
        { Name = "warehouse_db.inventory"; Columns = [ { Name = "sku"; DataType = "varchar" } ] }
    ]
    let pgConn = Connections.DatabaseConnection("PgCluster", makeConnector ConnectorKind.PostgreSql "Host=localhost;Database=fsqs_demo;Username=postgres;" pgTables)
    let pgCode = pgConn.GenerateEntities()

    Assert.Contains("module fsqs_demo =", pgCode)
    Assert.Contains("module warehouse_db =", pgCode)
    Assert.Contains("member _.products : FSharpQueryStudio.Connectors.SqlPushdownQueryable<fsqs_demo.products> = conn.Table<fsqs_demo.products>(\"products\")", pgCode)
    Assert.Contains("inventory = conn.Table<warehouse_db.inventory>(\"warehouse_db.inventory\")", pgCode)

    let myTables = [
        { Name = "shop_db.users"; Columns = [ { Name = "id"; DataType = "int" } ] }
        { Name = "billing_db.payments"; Columns = [ { Name = "id"; DataType = "bigint" } ] }
    ]
    let myConn = Connections.DatabaseConnection("MyCluster", makeConnector ConnectorKind.MySql "Server=localhost;Database=shop_db;Uid=root;" myTables)
    let myCode = myConn.GenerateEntities()

    Assert.Contains("module shop_db =", myCode)
    Assert.Contains("module billing_db =", myCode)
    Assert.Contains("member _.users : FSharpQueryStudio.Connectors.SqlPushdownQueryable<shop_db.users> = conn.Table<shop_db.users>(\"users\")", myCode)
    Assert.Contains("payments = conn.Table<billing_db.payments>(\"billing_db.payments\")", myCode)






