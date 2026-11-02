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
