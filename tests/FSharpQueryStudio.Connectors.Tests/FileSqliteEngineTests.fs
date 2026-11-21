module FileSqliteEngineTests

open System
open System.IO
open System.Linq
open System.Diagnostics
open Xunit
open ClosedXML.Excel
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors

[<CLIMutable>]
type CustomerCsvRecord = {
    Id: int
    Name: string
    City: string
}

[<CLIMutable>]
type OrderCsvRecord = {
    Id: int
    customer_id: int
    Amount: decimal
}

[<CLIMutable>]
type ProductExcelRecord = {
    Id: int
    Title: string
    Price: decimal
}

[<CLIMutable>]
type OrderItemCsvRecord = {
    Id: int
    product_id: int
    Quantity: int
}

[<CLIMutable>]
type StoreTxCsvRecord = {
    TxId: int
    StoreId: int
    Region: string
    Category: string
    Sku: string
    Amount: decimal
    TxDate: string
}

[<CLIMutable>]
type TargetCsvRecord = {
    TargetId: int
    StoreId: int
    Region: string
    TargetAmount: decimal
    Manager: string
}

type private TempFile() =
    let path = Path.Combine(Path.GetTempPath(), $"fqs-test-{Guid.NewGuid():N}.tmp")
    member _.Path = path
    interface IDisposable with
        member _.Dispose() =
            try if File.Exists path then File.Delete path with _ -> ()

type private TempDb() =
    let path = Path.Combine(Path.GetTempPath(), $"fqs-test-engine-{Guid.NewGuid():N}.db")
    member _.Path = path
    interface IDisposable with
        member _.Dispose() =
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
            try if File.Exists path then File.Delete path with _ -> ()

[<Fact>]
let ``FileSqliteEngine stages CSV and reuses cache without reloading when file is unchanged`` () =
    use db = new TempDb()
    use csvFile = new TempFile()
    let csvPath = csvFile.Path + ".csv"
    try
        File.WriteAllLines(csvPath, [|
            "Id,Name,City"
            "1,Alice,Sao Paulo"
            "2,Bob,Rio de Janeiro"
            "3,Charlie,Curitiba"
        |])

        let engine = FileSqliteEngine(db.Path)
        let connector = CsvConnector(csvPath)

        // First access: stages into SQLite
        let stagedTable = engine.EnsureStaged("Customers", csvPath, (fun () -> connector.ReadRows()))
        Assert.Equal("Customers", stagedTable)
        Assert.True(engine.IsStagedAndCurrent("Customers", csvPath))

        // Query staged data directly
        let res = engine.ExecuteQuery("SELECT count(*) FROM Customers;")
        let count = Convert.ToInt32((res.Rows |> Seq.head).[0])
        Assert.Equal(3, count)

        // Second access: cache hit, should be instant
        let sw = Stopwatch.StartNew()
        let cachedTable = engine.EnsureStaged("Customers", csvPath, (fun () -> failwith "Should not reload!"))
        sw.Stop()

        Assert.Equal("Customers", cachedTable)
        Assert.True(sw.ElapsedMilliseconds < 50L)
    finally
        try if File.Exists csvPath then File.Delete csvPath with _ -> ()

[<Fact>]
let ``FileSqliteEngine automatically creates B-tree indexes on Id and *_id columns`` () =
    use db = new TempDb()
    use csvFile = new TempFile()
    let csvPath = csvFile.Path + ".csv"
    try
        File.WriteAllLines(csvPath, [|
            "Id,customer_id,Amount"
            "1,100,50.25"
            "2,200,75.50"
        |])

        let engine = FileSqliteEngine(db.Path)
        let connector = CsvConnector(csvPath)
        engine.EnsureStaged("Orders", csvPath, (fun () -> connector.ReadRows())) |> ignore

        let res = engine.ExecuteQuery("SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'Orders';")
        let indexNames = res.Rows |> Seq.map (fun r -> string r.[0]) |> Seq.toList

        Assert.Contains("ix_Orders_Id", indexNames)
        Assert.Contains("ix_Orders_customer_id", indexNames)
    finally
        try if File.Exists csvPath then File.Delete csvPath with _ -> ()

[<Fact>]
let ``Connections.sql.query and Connections.queryEngine execute SQL directly on FileSqliteEngine`` () =
    use db = new TempDb()
    use csvFile = new TempFile()
    let csvPath = csvFile.Path + ".csv"
    try
        File.WriteAllLines(csvPath, [|
            "Id,Name,Score"
            "1,Alpha,95"
            "2,Beta,88"
            "3,Gamma,92"
        |])

        let engine = FileSqliteEngine(db.Path)
        Connections.setEngine(engine)

        let connector = CsvConnector(csvPath)
        let csvConn = Connections.CsvConnection("Players", connector)
        csvConn.Queryable<obj>() |> ignore // triggers staging

        // 1. Via Connections.sql.query
        let queryRes = Connections.sql.query("SELECT Name, Score FROM Players WHERE Score > @minScore ORDER BY Score DESC", [ "@minScore", box 90 ])
        Assert.Equal(2, queryRes.Columns.Length)
        let rows = queryRes.Rows |> Seq.toList
        Assert.Equal(2, rows.Length)
        Assert.Equal("Alpha", string rows.[0].[0])

        // 2. Via Connections.queryEngine
        let allRes = Connections.queryEngine("SELECT count(*) as Total FROM Players")
        let total = Convert.ToInt32((allRes.Rows |> Seq.head).[0])
        Assert.Equal(3, total)
    finally
        try if File.Exists csvPath then File.Delete csvPath with _ -> ()

[<Fact>]
let ``Cross-file relational join between two CSV files with 200,000+ rows executes natively in SQLite in sub-second time`` () =
    use db = new TempDb()
    use custFile = new TempFile()
    use ordFile = new TempFile()
    let custPath = custFile.Path + ".csv"
    let ordPath = ordFile.Path + ".csv"
    try
        let customerCount = 10_000
        let orderCount = 200_000

        // Create Customers CSV (10,000 rows)
        do
            use writer = new StreamWriter(custPath)
            writer.WriteLine("Id,Name,City")
            for i in 1 .. customerCount do
                writer.WriteLine($"{i},Customer_{i},City_{i % 50}")

        // Create Orders CSV (200,000 rows)
        do
            use writer = new StreamWriter(ordPath)
            writer.WriteLine("Id,customer_id,Amount")
            for i in 1 .. orderCount do
                let custId = (i % customerCount) + 1
                let amount = (decimal (i % 1000)) + 0.99M
                writer.WriteLine($"{i},{custId},{amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}")

        let engine = FileSqliteEngine(db.Path)
        Connections.setEngine(engine)

        let custConnector = CsvConnector(custPath)
        let ordConnector = CsvConnector(ordPath)
        let custConn = Connections.CsvConnection("Customers", custConnector)
        let ordConn = Connections.CsvConnection("Orders", ordConnector)

        let customers = custConn.Queryable<CustomerCsvRecord>()
        let orders = ordConn.Queryable<OrderCsvRecord>()

        let q =
            query {
                for o in orders do
                join c in customers on (o.customer_id = c.Id)
                where (c.Id = 42)
                sortByDescending o.Amount
                select {| OrderId = o.Id; CustomerName = c.Name; OrderAmount = o.Amount |}
            }

        let expr = (q :> IQueryable).Expression
        let plan = ExpressionPushdownTranslator.extractPlanFromExpression expr "Orders" [] SqlDialect.Sqlite
        let sql = plan.BuildSql(SqlDialect.Sqlite)

        // Verify native SQL join pushdown
        Assert.Contains("INNER JOIN Customers _t1 ON _t0.customer_id = _t1.Id", sql)
        Assert.Contains("WHERE (_t1.Id = @p0)", sql)
        Assert.Contains("ORDER BY _t0.Amount DESC", sql)
        Assert.Contains("_t0.Id AS OrderId", sql)
        Assert.Contains("_t1.Name AS CustomerName", sql)
        Assert.Contains("_t0.Amount AS OrderAmount", sql)

        // Execute query on the 200,000 rows
        let sw = Stopwatch.StartNew()
        let results = q |> Seq.toList
        sw.Stop()

        // 200,000 / 10,000 = exactly 20 orders for customer 42
        Assert.Equal(20, results.Length)
        Assert.Equal("Customer_42", results.[0].CustomerName)
        Assert.True(results.[0].OrderAmount >= results.[1].OrderAmount) // verify descending sort
        Assert.True(sw.ElapsedMilliseconds < 1500L, $"Relational join on 200k rows took {sw.ElapsedMilliseconds}ms, expected sub-second execution!")
    finally
        try if File.Exists custPath then File.Delete custPath with _ -> ()
        try if File.Exists ordPath then File.Delete ordPath with _ -> ()

[<Fact>]
let ``Cross-file relational join between CSV and Excel worksheet executes natively via SQLite pushdown`` () =
    use db = new TempDb()
    use csvFile = new TempFile()
    use xlsxFile = new TempFile()
    let csvPath = csvFile.Path + ".csv"
    let xlsxPath = xlsxFile.Path + ".xlsx"
    try
        // 1. Create Items CSV (500 rows)
        File.WriteAllLines(csvPath, [|
            yield "Id,product_id,Quantity"
            for i in 1 .. 500 do
                let prodId = (i % 50) + 1
                yield $"{i},{prodId},{i * 2}"
        |])

        // 2. Create Products Excel workbook (50 rows)
        do
            use wb = new XLWorkbook()
            let ws = wb.Worksheets.Add("Products")
            ws.Cell(1, 1).Value <- "Id"
            ws.Cell(1, 2).Value <- "Title"
            ws.Cell(1, 3).Value <- "Price"
            for i in 1 .. 50 do
                let row = i + 1
                ws.Cell(row, 1).Value <- i
                ws.Cell(row, 2).Value <- $"Product_{i}"
                ws.Cell(row, 3).Value <- XLCellValue.op_Implicit((decimal i) * 10.5M)
            wb.SaveAs(xlsxPath)

        let engine = FileSqliteEngine(db.Path)
        Connections.setEngine(engine)

        let csvConnector = CsvConnector(csvPath)
        let excelConnector = ExcelConnector(xlsxPath)

        let csvConn = Connections.CsvConnection("OrderItems", csvConnector)
        let excelConn = Connections.ExcelConnection("Catalog", excelConnector)

        let items = csvConn.Queryable<OrderItemCsvRecord>()
        let products = excelConn.SheetQueryable<ProductExcelRecord>("Products")

        let q =
            query {
                for it in items do
                join p in products on (it.product_id = p.Id)
                where (p.Id = 7)
                select {| ItemId = it.Id; ProductTitle = p.Title; Qty = it.Quantity; Price = p.Price |}
            }

        let expr = (q :> IQueryable).Expression
        let plan = ExpressionPushdownTranslator.extractPlanFromExpression expr "OrderItems" [] SqlDialect.Sqlite
        let sql = plan.BuildSql(SqlDialect.Sqlite)

        Assert.Contains("INNER JOIN Catalog_Products _t1 ON _t0.product_id = _t1.Id", sql)
        Assert.Contains("WHERE (_t1.Id = @p0)", sql)

        let results = q |> Seq.toList
        let (item0: {| ItemId: int; ProductTitle: string; Qty: int; Price: decimal |}) = results.[0]
        Assert.Equal(10, results.Length) // 500 / 50 = 10 items
        Assert.Equal("Product_7", item0.ProductTitle)
        Assert.Equal(73.5M, item0.Price)
    finally
        try if File.Exists csvPath then File.Delete csvPath with _ -> ()
        try if File.Exists xlsxPath then File.Delete xlsxPath with _ -> ()

[<Fact>]
let ``DatabaseConnection.GenerateEntities emits clean Table references and projects strict column subsets`` () =
    use db = new TempDb()
    use conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db.Path}")
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <- """
    CREATE TABLE Products (Id INTEGER PRIMARY KEY, Name TEXT, Price REAL, Category TEXT);
    INSERT INTO Products VALUES (1, 'Phone', 699.99, 'Tech');
    """
    cmd.ExecuteNonQuery() |> ignore

    let connector = SqliteConnector(db.Path)
    let dbConn = Connections.DatabaseConnection("Shop", connector)
    let generatedCode = dbConn.GenerateEntities()

    // Assert that the generated code references conn.Table<...>("Products") instead of hardcoded SELECT *
    Assert.Contains("conn.Table<``public``.Products>(\"Products\")", generatedCode)
    Assert.DoesNotContain("SELECT Id, Name, Price, Category FROM Products", generatedCode)

[<Fact>]
let ``FileSqliteEngine executes multi-property tuple join on CSVs with 5000 rows in milliseconds using pushdown`` () =
    use db = new TempDb()
    use txFile = new TempFile()
    use targetFile = new TempFile()
    let txCsvPath = txFile.Path + ".csv"
    let targetCsvPath = targetFile.Path + ".csv"

    try
        // Generate 5,000 transactions across 10 stores and 4 regions
        let regions = [| "North"; "South"; "East"; "West" |]
        let categories = [| "Electronics"; "Apparel"; "Home"; "Grocery" |]
        let txLines = System.Collections.Generic.List<string>()
        txLines.Add("TxId,StoreId,Region,Category,Sku,Amount,TxDate")
        for i in 1 .. 5000 do
            let storeId = (i % 10) + 1
            let region = regions.[i % regions.Length]
            let cat = categories.[i % categories.Length]
            let sku = $"SKU-{cat.Substring(0, 3).ToUpper()}-{storeId}"
            let amount = decimal (10 + (i % 500)) + 0.50M
            let date = $"2026-03-{((i % 28) + 1):D2}"
            txLines.Add($"{i},{storeId},{region},{cat},{sku},{amount},{date}")
        File.WriteAllLines(txCsvPath, txLines)

        // Generate 40 targets (10 stores x 4 regions)
        let targetLines = System.Collections.Generic.List<string>()
        targetLines.Add("TargetId,StoreId,Region,TargetAmount,Manager")
        let mutable targetId = 1
        for storeId in 1 .. 10 do
            for region in regions do
                let targetAmount = decimal (storeId * 10000)
                let manager = $"Manager_{region}_{storeId}"
                targetLines.Add($"{targetId},{storeId},{region},{targetAmount},{manager}")
                targetId <- targetId + 1
        File.WriteAllLines(targetCsvPath, targetLines)

        let engine = FileSqliteEngine(db.Path)
        Connections.setEngine(engine)

        let txConnector = CsvConnector(txCsvPath)
        let targetConnector = CsvConnector(targetCsvPath)

        let txConn = Connections.CsvConnection("Transactions", txConnector)
        let targetConn = Connections.CsvConnection("Targets", targetConnector)

        let txs = txConn.Queryable<StoreTxCsvRecord>()
        let targets = targetConn.Queryable<TargetCsvRecord>()

        // F# tuple multi-property join
        let q =
            query {
                for tx in txs do
                join tgt in targets on ((tx.StoreId, tx.Region) = (tgt.StoreId, tgt.Region))
                where (tx.Amount >= 250.0M)
                select {| TxId = tx.TxId; Store = tx.StoreId; Region = tx.Region; Amount = tx.Amount; Target = tgt.TargetAmount; Manager = tgt.Manager |}
            }

        // 1. Verify pushdown SQL translation without Cartesian product
        let rawSql = ResultKind.toRawSql q
        Assert.Contains("INNER JOIN Targets _t1 ON _t0.StoreId = _t1.StoreId AND _t0.Region = _t1.Region", rawSql)
        Assert.Contains("_t0.Amount >= @p0", rawSql)

        // 2. Measure execution time with pushdown and composite index
        let sw = Stopwatch.StartNew()
        let results = q |> Seq.toList
        sw.Stop()

        // 5000 rows join must complete in well under 1500ms (typically < 100ms)
        Assert.True(sw.ElapsedMilliseconds < 1500L, $"Multi-property join took too long: {sw.ElapsedMilliseconds}ms")
        Assert.True(results.Length > 0, "Expected matched rows from join")
        for r in results |> Seq.truncate 10 do
            Assert.True(r.Amount >= 250.0M)
            Assert.Equal($"Manager_{r.Region}_{r.Store}", r.Manager)
    finally
        try if File.Exists txCsvPath then File.Delete txCsvPath with _ -> ()
        try if File.Exists targetCsvPath then File.Delete targetCsvPath with _ -> ()

[<Fact>]
let ``FileSqliteEngine executes multi-property SelectMany join with where condition on large CSVs`` () =
    use db = new TempDb()
    use txFile = new TempFile()
    use targetFile = new TempFile()
    let txCsvPath = txFile.Path + ".csv"
    let targetCsvPath = targetFile.Path + ".csv"

    try
        let regions = [| "North"; "South"; "East"; "West" |]
        let txLines = System.Collections.Generic.List<string>()
        txLines.Add("TxId,StoreId,Region,Category,Sku,Amount,TxDate")
        for i in 1 .. 2000 do
            let storeId = (i % 5) + 1
            let region = regions.[i % regions.Length]
            let amount = decimal (50 + (i % 100))
            txLines.Add($"{i},{storeId},{region},Cat,SKU,{amount},2026-03-01")
        File.WriteAllLines(txCsvPath, txLines)

        let targetLines = System.Collections.Generic.List<string>()
        targetLines.Add("TargetId,StoreId,Region,TargetAmount,Manager")
        let mutable targetId = 1
        for storeId in 1 .. 5 do
            for region in regions do
                targetLines.Add($"{targetId},{storeId},{region},50000.0,Boss_{storeId}")
                targetId <- targetId + 1
        File.WriteAllLines(targetCsvPath, targetLines)

        let engine = FileSqliteEngine(db.Path)
        Connections.setEngine(engine)

        let txConnector = CsvConnector(txCsvPath)
        let targetConnector = CsvConnector(targetCsvPath)

        let txConn = Connections.CsvConnection("Transactions", txConnector)
        let targetConn = Connections.CsvConnection("Targets", targetConnector)

        let txs = txConn.Queryable<StoreTxCsvRecord>()
        let targets = targetConn.Queryable<TargetCsvRecord>()

        // SelectMany with composite Where
        let q =
            query {
                for tx in txs do
                for tgt in targets do
                where ((tx.StoreId = tgt.StoreId) && (tx.Region = tgt.Region) && (tx.Amount >= 100.0M))
                select {| TxId = tx.TxId; Store = tx.StoreId; Manager = tgt.Manager |}
            }

        let rawSql = ResultKind.toRawSql q
        Assert.Contains("INNER JOIN Targets _t1 ON 1=1", rawSql)
        Assert.Contains("_t0.StoreId = _t1.StoreId", rawSql)
        Assert.Contains("_t0.Region = _t1.Region", rawSql)

        let sw = Stopwatch.StartNew()
        let results = q |> Seq.toList
        sw.Stop()

        Assert.True(sw.ElapsedMilliseconds < 1500L, $"SelectMany join took too long: {sw.ElapsedMilliseconds}ms")
        Assert.True(results.Length > 0)
    finally
        try if File.Exists txCsvPath then File.Delete txCsvPath with _ -> ()
        try if File.Exists targetCsvPath then File.Delete targetCsvPath with _ -> ()

[<CLIMutable>]
type ThreeWayOrderRecord = {
    Id: int
    customer_id: int
    product_id: int
    Amount: decimal
}

[<Fact>]
let ``Cross-file relational join between two CSVs and an Excel worksheet executes natively via SQLite pushdown`` () =
    use db = new TempDb()
    use ordersCsv = new TempFile()
    use customersCsv = new TempFile()
    use catalogXlsx = new TempFile()
    let ordersPath = ordersCsv.Path + ".csv"
    let customersPath = customersCsv.Path + ".csv"
    let catalogPath = catalogXlsx.Path + ".xlsx"

    try
        // 1. Customers CSV
        File.WriteAllLines(customersPath, [|
            "Id,Name,City"
            "1,Alice,Sao Paulo"
            "2,Bob,Rio de Janeiro"
            "3,Charlie,Curitiba"
        |])

        // 2. Orders CSV
        File.WriteAllLines(ordersPath, [|
            "Id,customer_id,product_id,Amount"
            "101,1,10,150.0"
            "102,2,20,250.0"
            "103,1,30,350.0"
            "104,3,10,5.0"
        |])

        // 3. Products Excel workbook
        do
            use wb = new XLWorkbook()
            let ws = wb.Worksheets.Add("Products")
            ws.Cell(1, 1).Value <- "Id"
            ws.Cell(1, 2).Value <- "Title"
            ws.Cell(1, 3).Value <- "Price"
            ws.Cell(2, 1).Value <- 10
            ws.Cell(2, 2).Value <- "Laptop"
            ws.Cell(2, 3).Value <- XLCellValue.op_Implicit 3000.0M
            ws.Cell(3, 1).Value <- 20
            ws.Cell(3, 2).Value <- "Mouse"
            ws.Cell(3, 3).Value <- XLCellValue.op_Implicit 50.0M
            ws.Cell(4, 1).Value <- 30
            ws.Cell(4, 2).Value <- "Keyboard"
            ws.Cell(4, 3).Value <- XLCellValue.op_Implicit 120.0M
            wb.SaveAs(catalogPath)

        let engine = FileSqliteEngine(db.Path)
        Connections.setEngine(engine)

        let ordersConnector = CsvConnector(ordersPath)
        let customersConnector = CsvConnector(customersPath)
        let catalogConnector = ExcelConnector(catalogPath)

        let ordersConn = Connections.CsvConnection("Orders", ordersConnector)
        let customersConn = Connections.CsvConnection("Customers", customersConnector)
        let catalogConn = Connections.ExcelConnection("Catalog", catalogConnector)

        let orders = ordersConn.Queryable<ThreeWayOrderRecord>()
        let customers = customersConn.Queryable<CustomerCsvRecord>()
        let products = catalogConn.SheetQueryable<ProductExcelRecord>("Products")

        let q =
            query {
                for o in orders do
                join c in customers on (o.customer_id = c.Id)
                join p in products on (o.product_id = p.Id)
                where (o.Amount > 10.0M)
                select {| OrderId = o.Id; CustomerName = c.Name; Product = p.Title; Amount = o.Amount |}
            }

        let expr = (q :> IQueryable).Expression
        let plan = ExpressionPushdownTranslator.extractPlanFromExpression expr "Orders" [] SqlDialect.Sqlite
        let sql = plan.BuildSql(SqlDialect.Sqlite)

        // Verify native SQL join pushdown on 3 tables without nested loop fallback
        Assert.Contains("INNER JOIN Customers _t1 ON _t0.customer_id = _t1.Id", sql)
        Assert.Contains("INNER JOIN Catalog_Products _t2 ON _t0.product_id = _t2.Id", sql)
        Assert.Contains("WHERE (_t0.Amount > @p0)", sql)
        Assert.Contains("_t0.Id AS OrderId", sql)
        Assert.Contains("_t1.Name AS CustomerName", sql)
        Assert.Contains("_t2.Title AS Product", sql)
        Assert.Contains("_t0.Amount AS Amount", sql)

        let sw = Stopwatch.StartNew()
        let results = q |> Seq.toList
        sw.Stop()

        Assert.True(sw.ElapsedMilliseconds < 1000L, $"3-way join pushdown took {sw.ElapsedMilliseconds}ms, expected fast execution")
        Assert.Equal(3, results.Length) // Orders 101, 102, 103 (104 has Amount 5.0 <= 10.0)

        let o101 = results |> List.find (fun r -> r.OrderId = 101)
        Assert.Equal("Alice", o101.CustomerName)
        Assert.Equal("Laptop", o101.Product)
        Assert.Equal(150.0M, o101.Amount)

        let o102 = results |> List.find (fun r -> r.OrderId = 102)
        Assert.Equal("Bob", o102.CustomerName)
        Assert.Equal("Mouse", o102.Product)
        Assert.Equal(250.0M, o102.Amount)

        let o103 = results |> List.find (fun r -> r.OrderId = 103)
        Assert.Equal("Alice", o103.CustomerName)
        Assert.Equal("Keyboard", o103.Product)
        Assert.Equal(350.0M, o103.Amount)
    finally
        try if File.Exists ordersPath then File.Delete ordersPath with _ -> ()
        try if File.Exists customersPath then File.Delete customersPath with _ -> ()
        try if File.Exists catalogPath then File.Delete catalogPath with _ -> ()

[<Fact>]
let ``FileSqliteEngine handles concurrent EnsureStaged operations across threads on the same database without SQLite locks`` () =
    use db = new TempDb()
    let fileCount = 6
    let tempFiles = [ for _ in 1 .. fileCount -> new TempFile() ]
    try
        let filePaths =
            tempFiles
            |> List.mapi (fun i tf ->
                let p = tf.Path + $"-{i}.csv"
                File.WriteAllLines(p, [|
                    yield "Id,Code,Value"
                    for j in 1 .. 100 -> $"{j},CODE_{i}_{j},{j * 10}"
                |])
                p
            )

        // Launch concurrent staging across multiple threads using separate FileSqliteEngine instances
        let tasks =
            [ for i in 0 .. filePaths.Length - 1 ->
                let path = filePaths.[i]
                let tableName = $"Table_{i}"
                System.Threading.Tasks.Task.Run(fun () ->
                    let engine = FileSqliteEngine(db.Path)
                    let connector = CsvConnector(path)
                    let res = engine.EnsureStaged(tableName, path, (fun () -> connector.ReadRows()), currentIndex = (i + 1), totalCount = fileCount)
                    Assert.Equal(tableName, res)
                )
            ]

        System.Threading.Tasks.Task.WaitAll(tasks |> List.toArray)

        // Verify all 6 tables exist and have 100 rows each in the staging database
        let verifyEngine = FileSqliteEngine(db.Path)
        for i in 0 .. filePaths.Length - 1 do
            let tableName = $"Table_{i}"
            let queryRes = verifyEngine.ExecuteQuery($"SELECT count(*) FROM \"{tableName}\";")
            let count = Convert.ToInt32((queryRes.Rows |> Seq.head).[0])
            Assert.Equal(100, count)
    finally
        for tf in tempFiles do
            (tf :> IDisposable).Dispose()

[<Fact>]
let ``FileSqliteEngine emits granular staging progress events for Reading, Writing, Indexing, and Completed`` () =
    use db = new TempDb()
    use csvFile = new TempFile()
    let csvPath = csvFile.Path + ".csv"
    try
        File.WriteAllLines(csvPath, [|
            "Id,Name,Score"
            "1,Alice,95"
            "2,Bob,88"
            "3,Charlie,92"
        |])

        let engine = FileSqliteEngine(db.Path)
        let connector = CsvConnector(csvPath)
        let events = System.Collections.Concurrent.ConcurrentBag<StagingProgressEvent>()

        let staged =
            engine.EnsureStaged(
                "Students",
                csvPath,
                (fun () -> connector.ReadRows()),
                currentIndex = 1,
                totalCount = 1,
                onProgress = events.Add
            )

        Assert.Equal("Students", staged)

        let eventList = events |> Seq.toList
        let phases = eventList |> List.map (fun e -> e.Phase)

        Assert.Contains("Reading", phases)
        Assert.Contains("Writing", phases)
        Assert.Contains("Indexing", phases)
        Assert.Contains("Completed", phases)

        let writingEv = eventList |> List.find (fun e -> e.Phase = "Writing")
        Assert.Equal(Some 3, writingEv.RowCount)
        Assert.Equal(Some 1, writingEv.CurrentIndex)
        Assert.Equal(Some 1, writingEv.TotalCount)

        let completedEv = eventList |> List.find (fun e -> e.Phase = "Completed")
        Assert.Equal(Some 3, completedEv.RowCount)
        Assert.True(completedEv.ElapsedMs.IsSome)
    finally
        try if File.Exists csvPath then File.Delete csvPath with _ -> ()

[<Fact>]
let ``FileSqliteEngine DropStaleTableIfExists drops table and cache metadata when file matches`` () =
    use db = new TempDb()
    use csvFile = new TempFile()
    let csvPath = csvFile.Path + ".csv"
    try
        File.WriteAllLines(csvPath, [| "Id,Title"; "1,Alpha" |])
        let engine = FileSqliteEngine(db.Path)
        let connector = CsvConnector(csvPath)

        let staged = engine.EnsureStaged("StaleTable", csvPath, (fun () -> connector.ReadRows()))
        Assert.Equal("StaleTable", staged)
        Assert.True(engine.IsStagedAndCurrent("StaleTable", csvPath))

        // Call DropStaleTableIfExists with matching file path
        engine.DropStaleTableIfExists("StaleTable", csvPath)
        Assert.False(engine.IsStagedAndCurrent("StaleTable", csvPath))

        // Dropping a non-existent table or with mismatched path does not throw
        engine.DropStaleTableIfExists("StaleTable", csvPath)
        engine.DropStaleTableIfExists("NonExistent", "/some/other/path.csv")
    finally
        try if File.Exists csvPath then File.Delete csvPath with _ -> ()

[<Fact>]
let ``FileSqliteEngine EnsureStaged verifies in-memory cache hit vs disk database cache hit`` () =
    use db = new TempDb()
    use csvFile = new TempFile()
    let csvPath = csvFile.Path + ".csv"
    try
        File.WriteAllLines(csvPath, [| "Id,Val"; "1,100" |])
        let connector = CsvConnector(csvPath)
        let engine1 = FileSqliteEngine(db.Path)

        // 1. Initial stage (cache miss)
        let evs1 = System.Collections.Concurrent.ConcurrentBag<StagingProgressEvent>()
        let t1 = engine1.EnsureStaged("CacheTest", csvPath, (fun () -> connector.ReadRows()), onProgress = evs1.Add)
        Assert.Equal("CacheTest", t1)
        Assert.Contains(evs1, fun e -> e.Phase = "Completed")

        // 2. Second stage on same engine instance (in-memory hit)
        let evs2 = System.Collections.Concurrent.ConcurrentBag<StagingProgressEvent>()
        let t2 = engine1.EnsureStaged("CacheTest", csvPath, (fun () -> connector.ReadRows()), onProgress = evs2.Add)
        Assert.Equal("CacheTest", t2)
        Assert.Contains(evs2, fun e -> e.Phase = "Skipped")

        // 3. Third stage on fresh engine instance (disk database cache hit, empty in-memory dictionary)
        let engine2 = FileSqliteEngine(db.Path)
        let evs3 = System.Collections.Concurrent.ConcurrentBag<StagingProgressEvent>()
        let t3 = engine2.EnsureStaged("CacheTest", csvPath, (fun () -> connector.ReadRows()), onProgress = evs3.Add)
        Assert.Equal("CacheTest", t3)
        Assert.Contains(evs3, fun e -> e.Phase = "Skipped")
    finally
        try if File.Exists csvPath then File.Delete csvPath with _ -> ()

[<Fact>]
let ``FileSqliteEngine uses instance ProgressCallback and GlobalProgressCallback as fallbacks`` () =
    use db = new TempDb()
    use csvFile = new TempFile()
    let csvPath = csvFile.Path + ".csv"
    try
        File.WriteAllLines(csvPath, [| "Id,Val"; "1,999" |])
        let connector = CsvConnector(csvPath)

        // 1. Instance ProgressCallback
        let engine = FileSqliteEngine(db.Path)
        let instanceEvents = System.Collections.Concurrent.ConcurrentBag<StagingProgressEvent>()
        engine.ProgressCallback <- Some instanceEvents.Add

        let t1 = engine.EnsureStaged("InstanceCallbackTest", csvPath, (fun () -> connector.ReadRows()))
        Assert.Equal("InstanceCallbackTest", t1)
        Assert.Contains(instanceEvents, fun e -> e.Phase = "Completed")

        // 2. GlobalProgressCallback fallback
        use db2 = new TempDb()
        let engineGlobal = FileSqliteEngine(db2.Path)
        let globalEvents = System.Collections.Concurrent.ConcurrentBag<StagingProgressEvent>()
        FileSqliteEngine.GlobalProgressCallback <- Some globalEvents.Add
        try
            let t2 = engineGlobal.EnsureStaged("GlobalCallbackTest", csvPath, (fun () -> connector.ReadRows()))
            Assert.Equal("GlobalCallbackTest", t2)
            Assert.Contains(globalEvents, fun e -> e.Phase = "Completed")
        finally
            FileSqliteEngine.GlobalProgressCallback <- None

        // 3. Callback throwing exception should be safely swallowed without failing staging
        use db3 = new TempDb()
        let engineFaulty = FileSqliteEngine(db3.Path)
        let t3 =
            engineFaulty.EnsureStaged(
                "FaultyCallbackTest",
                csvPath,
                (fun () -> connector.ReadRows()),
                onProgress = (fun _ -> failwith "Intentional callback error")
            )
        Assert.Equal("FaultyCallbackTest", t3)
    finally
        try if File.Exists csvPath then File.Delete csvPath with _ -> ()

[<Fact>]
let ``FileSqliteEngine EnsureStaged handles column type overrides and row value variations`` () =
    use db = new TempDb()
    let engine = FileSqliteEngine(db.Path)

    let colTypes = Map.ofList [
        "ColInt", "int"
        "ColLong", "long"
        "ColDec", "decimal"
        "ColDoub", "double"
        "ColFloat", "float"
        "ColBool", "bool"
        "ColDate", "datetime"
        "ColCustom", "custom_type"
    ]

    let qResult = {
        Columns = [ "ColInt"; "ColLong"; "ColDec"; "ColDoub"; "ColFloat"; "ColBool"; "ColDate"; "ColCustom" ]
        Rows = [|
            [|
                box "true"              // string boolean into INTEGER affinity
                box "123456789"         // string long into INTEGER affinity
                box "12.34"             // string decimal into NUMERIC affinity
                box Double.NaN          // non-finite double into NUMERIC
                box Single.PositiveInfinity // non-finite single into NUMERIC
                box 1                   // int into INTEGER
                box "2026-11-16"        // text
                box null                // null cell
            |]
            [|
                box "42.75"             // float string into INTEGER (keeps decimal, doesn't truncate)
                box 999L                // int64
                box 50.0M               // decimal integer
                box 100.5               // finite double
                box 25.5f               // finite single
                box false               // bool
                box "Text"              // text
                DBNull.Value            // DBNull cell
            |]
        |]
    }

    let fakePath = Path.Combine(Path.GetTempPath(), $"fake-data-{Guid.NewGuid():N}.dat")
    try
        File.WriteAllText(fakePath, "dummy")
        let staged =
            engine.EnsureStaged(
                "TypeAffinityTest",
                fakePath,
                (fun () -> qResult),
                columnTypes = colTypes
            )
        Assert.Equal("TypeAffinityTest", staged)

        let res = engine.ExecuteQuery("SELECT * FROM TypeAffinityTest;")
        Assert.Equal(2, res.Rows |> Seq.length)
    finally
        try if File.Exists fakePath then File.Delete fakePath with _ -> ()

[<Fact>]
let ``FileSqliteEngine EnsureStaged reports Failed and reraises when readRows throws exception`` () =
    use db = new TempDb()
    let engine = FileSqliteEngine(db.Path)
    let fakePath = Path.Combine(Path.GetTempPath(), $"fake-fail-{Guid.NewGuid():N}.dat")
    let events = System.Collections.Concurrent.ConcurrentBag<StagingProgressEvent>()

    try
        File.WriteAllText(fakePath, "dummy")
        let ex =
            Assert.Throws<Exception>(fun () ->
                engine.EnsureStaged(
                    "FailingStaging",
                    fakePath,
                    (fun () -> failwith "Staging read failed!"),
                    onProgress = events.Add
                ) |> ignore
            )
        Assert.Contains("Staging read failed!", ex.Message)

        let failedEv = events |> Seq.tryFind (fun e -> e.Phase = "Failed")
        Assert.True(failedEv.IsSome)
    finally
        try if File.Exists fakePath then File.Delete fakePath with _ -> ()

[<Fact>]
let ``FileSqliteEngine IsStagedAndCurrent and DropStaleTableIfExists handle edge cases`` () =
    use db = new TempDb()
    let engine = FileSqliteEngine(db.Path)

    // 1. File does not exist
    Assert.False(engine.IsStagedAndCurrent("NonExistentTable", @"C:\path\does\not\exist\file.csv"))

    // 2. DropStaleTableIfExists when table is not in cache
    engine.DropStaleTableIfExists("UncachedTable", @"C:\some\file.csv")

    // 3. Staged file, then modify file to force mtime mismatch
    let tmpFile = Path.Combine(Path.GetTempPath(), $"fqs-staged-{Guid.NewGuid():N}.csv")
    try
        File.WriteAllText(tmpFile, "Id,Name\n1,Alpha\n")
        let stagedName = engine.EnsureStaged("StagedEdge", tmpFile, (fun () -> { Columns = [ "Id"; "Name" ]; Rows = [| [| box "1"; box "Alpha" |] |] }))
        Assert.Equal("StagedEdge", stagedName)
        Assert.True(engine.IsStagedAndCurrent("StagedEdge", tmpFile))

        // Modify file content/mtime
        File.AppendAllText(tmpFile, "2,Beta\n")
        File.SetLastWriteTimeUtc(tmpFile, DateTime.UtcNow.AddMinutes(5.0))
        Assert.False(engine.IsStagedAndCurrent("StagedEdge", tmpFile))

        // Drop table when file path matches
        engine.DropStaleTableIfExists("StagedEdge", tmpFile)
        Assert.False(engine.IsStagedAndCurrent("StagedEdge", tmpFile))

        // Drop table with tableName = "" (sanitizes to StagedFile)
        engine.DropStaleTableIfExists("", tmpFile)
    finally
        try if File.Exists tmpFile then File.Delete tmpFile with _ -> ()

[<Fact>]
let ``FileSqliteEngine EnsureStaged handles empty columns, inferred affinities, and throwing progress callback`` () =
    use db = new TempDb()
    let engine = FileSqliteEngine(db.Path)
    let tmpFile = Path.Combine(Path.GetTempPath(), $"fqs-inferred-{Guid.NewGuid():N}.csv")
    try
        File.WriteAllText(tmpFile, "dummy")

        // 1. Empty columns -> defaults to [ "Value" ]
        // 2. Empty tableName -> defaults to "StagedFile"
        // 3. Throwing onProgress callback -> caught and logged without failure
        let throwingCb (_: StagingProgressEvent) = failwith "Callback error"
        let stagedEmpty =
            engine.EnsureStaged(
                "",
                tmpFile,
                (fun () -> { Columns = []; Rows = [| [| box "single_val" |] |] }),
                onProgress = throwingCb
            )
        Assert.Equal("Item", stagedEmpty)

        // 4. Inferred affinities: integer, bool, numeric, text, key columns
        let qResult = {
            Columns = [ "store_id"; "is_active"; "score"; "product_code"; "client_key"; "Region"; "Notes"; "empty_col" ]
            Rows = [|
                [| box "101"; box "true"; box "98.5"; box "SKU-A"; box "K1"; box "North"; box "Sample note"; null |]
                [| box "102"; box "false"; box "72,4"; box "SKU-B"; box "K2"; box "South"; box "Another note"; DBNull.Value |]
                [| box "103"; box "true"; box "-15.2"; box "SKU-C"; box "K3"; box "East"; box "-"; null |]
            |]
        }
        let stagedInferred =
            engine.EnsureStaged(
                "InferredData",
                tmpFile,
                (fun () -> qResult)
            )
        Assert.Equal("InferredData", stagedInferred)

        let res = engine.ExecuteQuery("SELECT * FROM InferredData;")
        Assert.Equal(3, res.Rows |> Seq.length)
    finally
        try if File.Exists tmpFile then File.Delete tmpFile with _ -> ()

[<Fact>]
let ``FileSqliteEngine EnsureAliasView creates view aliases and never overwrites physical tables`` () =
    use db = new TempDb()
    let engine = FileSqliteEngine(db.Path)
    let tmpFile = Path.Combine(Path.GetTempPath(), $"fqs-alias-{Guid.NewGuid():N}.csv")
    try
        File.WriteAllLines(tmpFile, [| "Id,Name"; "1,Alice"; "2,Bob" |])
        let connector = CsvConnector(tmpFile)
        let staged = engine.EnsureStaged("SalesFeed", tmpFile, (fun () -> connector.ReadRows()))
        Assert.Equal("SalesFeed", staged)

        // Create view alias 'sales_feed' pointing to 'SalesFeed'
        engine.EnsureAliasView("sales_feed", "SalesFeed")
        let aliasRes = engine.ExecuteQuery("SELECT Name FROM sales_feed ORDER BY Id;")
        let names = aliasRes.Rows |> Seq.map (fun r -> string r.[0]) |> Seq.toList
        Assert.Equal<string list>([ "Alice"; "Bob" ], names)

        // Attempting to create an alias view with the same name as a physical table ('SalesFeed') is a safe no-op
        engine.EnsureAliasView("SalesFeed", "sales_feed")
        Assert.True(engine.IsStagedAndCurrent("SalesFeed", tmpFile))

        // If a physical table is later staged over 'sales_feed', EnsureStaged drops the view cleanly and creates the table
        let stagedOverView = engine.EnsureStaged("sales_feed", tmpFile, (fun () -> connector.ReadRows()))
        Assert.Equal("sales_feed", stagedOverView)
        Assert.True(engine.IsStagedAndCurrent("sales_feed", tmpFile))
    finally
        try if File.Exists tmpFile then File.Delete tmpFile with _ -> ()

[<Fact>]
let ``FileSqliteEngine EnsureAliasView handles blank identifiers, self-aliases, and view updates cleanly`` () =
    use db = new TempDb()
    let engine = FileSqliteEngine(db.Path)
    let tmpFile = Path.Combine(Path.GetTempPath(), $"fqs-alias2-{Guid.NewGuid():N}.csv")
    try
        File.WriteAllLines(tmpFile, [| "Code,Val"; "A,10"; "B,20"; "C,30" |])
        let connector = CsvConnector(tmpFile)
        engine.EnsureStaged("TableOne", tmpFile, (fun () -> connector.ReadRows())) |> ignore

        // Blank view name or self-alias are safe no-ops
        engine.EnsureAliasView("", "TableOne")
        engine.EnsureAliasView("   ", "TableOne")
        engine.EnsureAliasView("TableOne", "")
        engine.EnsureAliasView("TableOne", "TableOne")

        // Create view alias with special characters that get sanitized
        engine.EnsureAliasView("my-alias.view", "TableOne")
        let res1 = engine.ExecuteQuery("SELECT COUNT(*) FROM my_alias_view;")
        Assert.Equal(3L, Convert.ToInt64((res1.Rows |> Seq.head).[0]))

        // Re-pointing an existing view to another staged table updates the view
        let tmpFile2 = Path.Combine(Path.GetTempPath(), $"fqs-alias3-{Guid.NewGuid():N}.csv")
        try
            File.WriteAllLines(tmpFile2, [| "Code,Val"; "Z,99" |])
            let conn2 = CsvConnector(tmpFile2)
            engine.EnsureStaged("TableTwo", tmpFile2, (fun () -> conn2.ReadRows())) |> ignore
            engine.EnsureAliasView("my_alias_view", "TableTwo")
            let res2 = engine.ExecuteQuery("SELECT Code FROM my_alias_view;")
            Assert.Equal("Z", string (res2.Rows |> Seq.head).[0])
        finally
            try if File.Exists tmpFile2 then File.Delete tmpFile2 with _ -> ()
    finally
        try if File.Exists tmpFile then File.Delete tmpFile with _ -> ()





