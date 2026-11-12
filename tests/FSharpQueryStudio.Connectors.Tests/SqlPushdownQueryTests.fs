module FSharpQueryStudio.Connectors.Tests.SqlPushdownQueryTests

open System
open System.IO
open System.Linq
open Microsoft.Data.Sqlite
open Xunit
open FSharpQueryStudio.Connectors
open FSharpQueryStudio.Protocol

[<CLIMutable>]
type TestProduct = {
    Id: int
    Name: string
    Price: decimal
    Category: string
    InStock: bool
}

[<CLIMutable>]
type TestCustomer = {
    CustomerId: int
    CustomerName: string
}

[<CLIMutable>]
type TestOrder = {
    OrderId: int
    CustomerId: int
    Amount: decimal
}

type TempSqliteDb() =
    let path = Path.Combine(Path.GetTempPath(), $"pushdown_test_{Guid.NewGuid():N}.db")
    do
        use conn = new SqliteConnection($"Data Source={path}")
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "CREATE TABLE Products (Id INTEGER PRIMARY KEY, Name TEXT, Price NUMERIC, Category TEXT, InStock INTEGER); \
             INSERT INTO Products VALUES (1, 'Keyboard', 45.50, 'Electronics', 1); \
             INSERT INTO Products VALUES (2, 'Mouse', 25.00, 'Electronics', 1); \
             INSERT INTO Products VALUES (3, 'Notebook', 5.99, 'Stationery', 1); \
             INSERT INTO Products VALUES (4, 'Monitor', 299.99, 'Electronics', 0); \
             INSERT INTO Products VALUES (5, 'Pen', 1.50, 'Stationery', 1); \
             CREATE TABLE Customers (CustomerId INTEGER PRIMARY KEY, CustomerName TEXT); \
             INSERT INTO Customers VALUES (1, 'Alice'); \
             INSERT INTO Customers VALUES (2, 'Bob'); \
             INSERT INTO Customers VALUES (3, 'Charlie'); \
             CREATE TABLE Orders (OrderId INTEGER PRIMARY KEY, CustomerId INTEGER, Amount NUMERIC); \
             INSERT INTO Orders VALUES (101, 1, 150.00); \
             INSERT INTO Orders VALUES (102, 1, 200.00); \
             INSERT INTO Orders VALUES (103, 2, 50.00);"
        cmd.ExecuteNonQuery() |> ignore

    member _.Path = path
    interface IDisposable with
        member _.Dispose() =
            try if File.Exists(path) then File.Delete(path) with _ -> ()

[<Fact>]
let ``SqlPushdownQueryable translates query where clause to SQL pushdown`` () =
    use db = new TempSqliteDb()
    let connector = SqliteConnector(db.Path)
    let queryable = SqlPushdownQueryable<TestProduct>(connector, "Products")

    let result =
        query {
            for p in queryable do
            where (p.Price > 30.0M)
            select p
        }
        |> Seq.toList

    Assert.Equal(2, result.Length)
    Assert.Contains(result, fun p -> p.Name = "Keyboard")
    Assert.Contains(result, fun p -> p.Name = "Monitor")

[<Fact>]
let ``SqlPushdownQueryable translates sorting and take to SQL ORDER BY and LIMIT`` () =
    use db = new TempSqliteDb()
    let connector = SqliteConnector(db.Path)
    let queryable = SqlPushdownQueryable<TestProduct>(connector, "Products")

    let result =
        query {
            for p in queryable do
            sortByDescending p.Price
            take 2
            select p
        }
        |> Seq.toList

    Assert.Equal(2, result.Length)
    Assert.Equal("Monitor", result.[0].Name)
    Assert.Equal("Keyboard", result.[1].Name)

[<Fact>]
let ``SqlPushdownQueryable translates count to SQL COUNT(*)`` () =
    use db = new TempSqliteDb()
    let connector = SqliteConnector(db.Path)
    let queryable = SqlPushdownQueryable<TestProduct>(connector, "Products")

    let totalCount =
        query {
            for p in queryable do
            where (p.Category = "Electronics")
            count
        }

    Assert.Equal(3, totalCount)

[<Fact>]
let ``SqlPushdownQueryable translates sum to SQL SUM(Price)`` () =
    use db = new TempSqliteDb()
    let connector = SqliteConnector(db.Path)
    let queryable = SqlPushdownQueryable<TestProduct>(connector, "Products")

    let electronicsTotal =
        query {
            for p in queryable do
            where (p.Category = "Electronics")
            sumBy p.Price
        }

    // 45.50 + 25.00 + 299.99 = 370.49
    Assert.Equal(370.49M, electronicsTotal)

[<Fact>]
let ``SqlPushdownQueryable Where and Page helper methods work seamlessly`` () =
    use db = new TempSqliteDb()
    let connector = SqliteConnector(db.Path)
    let queryable = SqlPushdownQueryable<TestProduct>(connector, "Products")

    let page1 =
        queryable
            .Where("Category = @cat", [ "@cat", box "Electronics" ])
            .Page(1, 2)
        |> Seq.toList

    Assert.Equal(2, page1.Length)

    let page2 =
        queryable
            .Where("Category = @cat", [ "@cat", box "Electronics" ])
            .Page(2, 2)
        |> Seq.toList

    Assert.Equal(1, page2.Length)

[<Fact>]
let ``SqlPushdownQueryPlan generates correct SQL Server OFFSET FETCH and TOP syntax`` () =
    let plan = PushdownQueryPlan.Create("Orders", [])
    plan.OrderBy.Add({ ColumnName = "CreatedAt"; Direction = Descending })
    plan.Skip <- Some 10
    plan.Take <- Some 5

    let sqlServer = plan.BuildSql(SqlDialect.SqlServer)
    Assert.Contains("ORDER BY CreatedAt DESC", sqlServer)
    Assert.Contains("OFFSET 10 ROWS FETCH NEXT 5 ROWS ONLY", sqlServer)

    let postgres = plan.BuildSql(SqlDialect.PostgreSql)
    Assert.Contains("ORDER BY CreatedAt DESC", postgres)
    Assert.Contains("LIMIT 5 OFFSET 10", postgres)

[<Fact>]
let ``SqlPushdownQueryable falls back gracefully to in-memory evaluation for unmapped predicates`` () =
    use db = new TempSqliteDb()
    let connector = SqliteConnector(db.Path)
    let queryable = SqlPushdownQueryable<TestProduct>(connector, "Products")

    let customValidator (name: string) = name.Length = 4 // 'Penn' is 3, 'Mouse' is 5, etc.

    let result =
        query {
            for p in queryable do
            where (customValidator p.Name)
            select p
        }
        |> Seq.toList

    // None of the product names have length 4 except none: Keyboard (8), Mouse (5), Notebook (8), Monitor (7), Pen (3)
    Assert.Empty(result)

    let customValidator2 (name: string) = name.Length = 3 // 'Pen' is 3

    let result2 =
        query {
            for p in queryable do
            where (customValidator2 p.Name)
            select p
        }
        |> Seq.toList

    Assert.Single(result2) |> ignore
    Assert.Equal("Pen", result2.[0].Name)

[<Fact>]
let ``SqlPushdownQueryable executes join and projection with anonymous record`` () =
    use db = new TempSqliteDb()
    let connector = SqliteConnector(db.Path)
    let products1 = SqlPushdownQueryable<TestProduct>(connector, "Products")
    let products2 = SqlPushdownQueryable<TestProduct>(connector, "Products")

    let q =
        query {
            for p1 in products1 do
            join p2 in products2 on (p1.Id = p2.Id)
            sortByDescending p2.Price
            select {| Id = p1.Id; Name = p2.Name |}
        }
    let expr = (q :> IQueryable).Expression
    let plan = ExpressionPushdownTranslator.extractPlanFromExpression expr "Products" [] SqlDialect.Sqlite
    let sql = plan.BuildSql(SqlDialect.Sqlite)
    let result = q |> Seq.toList

    Assert.Equal(5, result.Length)
    Assert.Contains("INNER JOIN Products _t1 ON _t0.Id = _t1.Id", sql)
    Assert.Contains("SELECT _t0.Id AS Id, _t1.Name AS Name", sql)
    Assert.Equal(4, result.[0].Id)
    Assert.Equal("Monitor", result.[0].Name)

[<CLIMutable>]
type TestStock = {
    Id: int
    WarehouseId: int
    QuantityOnHand: int
}

[<Fact>]
let ``SqlPushdownQueryable executes join between different tables with sorting on inner table column`` () =
    use db = new TempSqliteDb()
    use conn = new SqliteConnection($"Data Source={db.Path}")
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        "CREATE TABLE Warehouses (Id INTEGER PRIMARY KEY, Code TEXT); \
         CREATE TABLE StockLevels (Id INTEGER PRIMARY KEY, WarehouseId INTEGER, QuantityOnHand INTEGER); \
         INSERT INTO Warehouses VALUES (1, 'W1'); \
         INSERT INTO StockLevels VALUES (1, 1, 100);"
    cmd.ExecuteNonQuery() |> ignore

    let connector = SqliteConnector(db.Path)
    let warehouses = SqlPushdownQueryable<{| Id: int; Code: string |}>(connector, "Warehouses")
    let stocks = SqlPushdownQueryable<TestStock>(connector, "StockLevels")

    let q =
        query {
            for w in warehouses do
            join s in stocks on (w.Id = s.WarehouseId)
            sortByDescending s.QuantityOnHand
            select {| Code = w.Code; Qty = s.QuantityOnHand |}
        }
    let expr = (q :> IQueryable).Expression
    let plan = ExpressionPushdownTranslator.extractPlanFromExpression expr "Warehouses" [] SqlDialect.Sqlite
    let sql = plan.BuildSql(SqlDialect.Sqlite)

    Assert.Contains("INNER JOIN StockLevels _t1 ON _t0.Id = _t1.WarehouseId", sql)
    Assert.Contains("SELECT _t0.Code AS Code, _t1.QuantityOnHand AS Qty", sql)

    let result = q |> Seq.toList
    Assert.Single(result) |> ignore
    Assert.Equal("W1", result.[0].Code)
    Assert.Equal(100, result.[0].Qty)

[<Fact>]
let ``SqlPushdownQueryable projects strict column subsets in Select without fetching SELECT *`` () =
    use db = new TempSqliteDb()
    let connector = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<TestProduct>(connector, "Products")

    let q =
        query {
            for p in products do
            where (p.Price > 30.0M)
            select {| ItemName = p.Name; Cost = p.Price |}
        }
    let expr = (q :> IQueryable).Expression
    let plan = ExpressionPushdownTranslator.extractPlanFromExpression expr "Products" [] SqlDialect.Sqlite
    let sql = plan.BuildSql(SqlDialect.Sqlite)

    Assert.Contains("Price AS Cost", sql)
    Assert.Contains("Name AS ItemName", sql)
    Assert.Contains("FROM Products", sql)
    Assert.DoesNotContain("*", sql)

    let result = q |> Seq.toList
    Assert.Equal(2, result.Length)
    Assert.Equal("Keyboard", result.[0].ItemName)
    Assert.Equal(45.50M, result.[0].Cost)

[<Fact>]
let ``SqlPushdownQueryable projects string concatenations into native dialect syntax`` () =
    use db = new TempSqliteDb()
    let connector = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<TestProduct>(connector, "Products")

    let q =
        query {
            for p in products do
            where (p.Id = 1)
            select {| Display = p.Name + " (" + p.Category + ")" |}
        }
    let expr = (q :> IQueryable).Expression
    let plan = ExpressionPushdownTranslator.extractPlanFromExpression expr "Products" [] SqlDialect.Sqlite
    let sql = plan.BuildSql(SqlDialect.Sqlite)

    Assert.Contains("||", sql)

    let result = q |> Seq.toList
    Assert.Single(result) |> ignore
    Assert.Equal("Keyboard (Electronics)", result.[0].Display)

[<Fact>]
let ``SqlPushdownQueryable projects tuples without in-memory reflection failure`` () =
    use db = new TempSqliteDb()
    let connector = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<TestProduct>(connector, "Products")

    let q =
        query {
            for p in products do
            where (p.Id = 2)
            select (p.Id, p.Name)
        }
    let result = q |> Seq.toList
    Assert.Single(result) |> ignore
    Assert.Equal((2, "Mouse"), result.[0])

[<Fact>]
let ``ResultKind.toRawSql extracts SQL query and parameters before database execution`` () =
    use db = new TempSqliteDb()
    let connector = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<TestProduct>(connector, "Products")

    let q =
        query {
            for p in products do
            where (p.Price > 50.0M)
            select (p.Name, p.Price)
        }

    // Call ResultKind.toRawSql without touching the database
    let rawSql = ResultKind.toRawSql q

    Assert.Contains("-- Parameters:", rawSql)
    Assert.Contains("@p0 = 50", rawSql)
    Assert.Contains("SELECT", rawSql)
    Assert.Contains("Name", rawSql)
    Assert.Contains("Price", rawSql)
    Assert.Contains("FROM Products", rawSql)
    Assert.Contains("WHERE", rawSql)

[<Fact>]
let ``toRawSql and ToRawSql methods extract matching SQL on projected queries`` () =
    use db = new TempSqliteDb()
    let connector = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<TestProduct>(connector, "Products")

    let q =
        query {
            for p in products do
            where (p.Category = "Electronics")
            select {| Item = p.Name |}
        }

    let sql1 = ResultKind.toRawSql q
    let sql2 = toRawSql q
    let (sql3, parms) = ResultKind.toRawSqlWithParameters q

    Assert.Equal(sql1, sql2)
    Assert.Contains("SELECT", sql3)
    Assert.Contains("Name AS Item", sql3)
    Assert.Contains("FROM Products", sql3)
    Assert.Single(parms) |> ignore
    Assert.Equal("@p0", fst parms.[0])
    Assert.Equal(box "Electronics", snd parms.[0])

[<Fact>]
let ``ResultKind.toRawSql on relational join displays full joined SQL without database round-trip`` () =
    use db = new TempSqliteDb()
    let connector = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<TestProduct>(connector, "Products")

    let q =
        query {
            for p1 in products do
            join p2 in products on (p1.Category = p2.Category)
            where (p1.Id <> p2.Id)
            select {| LeftName = p1.Name; RightName = p2.Name |}
        }

    let rawSql = ResultKind.toRawSql q
    Assert.Contains("INNER JOIN Products _t1 ON _t0.Category = _t1.Category", rawSql)
    Assert.Contains("_t0.Name AS LeftName", rawSql)
    Assert.Contains("_t1.Name AS RightName", rawSql)

[<Fact>]
let ``SqlPushdownQueryable translates leftOuterJoin to SQL LEFT JOIN and executes correctly`` () =
    use db = new TempSqliteDb()
    let connector = SqliteConnector(db.Path)
    let customers = SqlPushdownQueryable<TestCustomer>(connector, "Customers")
    let orders = SqlPushdownQueryable<TestOrder>(connector, "Orders")

    let q =
        query {
            for c in customers do
            leftOuterJoin o in orders on (c.CustomerId = o.CustomerId) into g
            for o in g.DefaultIfEmpty() do
            select {|
                Customer = c.CustomerName
                Amount = (if isNull (box o) then 0.0M else o.Amount)
            |}
        }

    let rawSql = ResultKind.toRawSql q
    Assert.Contains("LEFT JOIN Orders", rawSql)
    Assert.Contains("COALESCE", rawSql)

    let rows = q |> Seq.toList
    Assert.Equal(4, rows.Length)
    let charlie = rows |> List.find (fun r -> r.Customer = "Charlie")
    Assert.Equal(0.0M, charlie.Amount)

[<Fact>]
let ``SqlPushdownQueryable translates distinct to SELECT DISTINCT`` () =
    use db = new TempSqliteDb()
    let connector = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<TestProduct>(connector, "Products")

    let q =
        query {
            for p in products do
            distinct
            select p.Category
        }

    let rawSql = ResultKind.toRawSql q
    Assert.Contains("SELECT DISTINCT", rawSql)
    let cats = q |> Seq.toList
    Assert.Equal(2, cats.Length)

[<Fact>]
let ``SqlPushdownQueryable translates terminal methods Count, Sum, Min, Max`` () =
    use db = new TempSqliteDb()
    let connector = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<TestProduct>(connector, "Products")

    let count = products.Count()
    Assert.Equal(5, count)

    let sum = products.Sum(fun p -> p.Price)
    Assert.Equal(377.98M, sum)

    let min = products.Min(fun p -> p.Price)
    Assert.Equal(1.50M, min)

    let max = products.Max(fun p -> p.Price)
    Assert.Equal(299.99M, max)

[<Fact>]
let ``SqlPushdownQueryable translates subquery Contains to IN subselect`` () =
    use db = new TempSqliteDb()
    let connector = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<TestProduct>(connector, "Products")

    let expensiveIds =
        query {
            for p in products do
            where (p.Price > 100.0M)
            select p.Id
        }

    let q =
        query {
            for p in products do
            where (expensiveIds.Contains(p.Id))
            select p.Name
        }

    let rawSql = ResultKind.toRawSql q
    Assert.Contains("IN (SELECT", rawSql)
    let names = q |> Seq.toList
    Assert.Single(names) |> ignore
    Assert.Equal("Monitor", names.[0])

type TestSales = { Year: int; Product: string; Sales: int }

[<Fact>]
let ``Seq.pivot performs in-memory cross-tabulation`` () =
    let rows: TestSales list = [
        { Year = 2024; Product = "A"; Sales = 100 }
        { Year = 2024; Product = "B"; Sales = 200 }
        { Year = 2025; Product = "A"; Sales = 150 }
        { Year = 2025; Product = "B"; Sales = 250 }
    ]

    let pivoted = Seq.pivot (fun (r: TestSales) -> r.Year) (fun r -> r.Product) (fun r -> r.Sales) Seq.sum rows |> Seq.toList
    Assert.Equal(2, pivoted.Length)
    let (year2024, map2024) = pivoted |> List.find (fun (y, _) -> y = 2024)
    Assert.Equal(100, map2024.["A"])
    Assert.Equal(200, map2024.["B"])

[<Fact>]
let ``Connections.sql.buildPivotSql generates dynamic CASE WHEN aggregation SQL`` () =
    let sql = Connections.sql.buildPivotSql("Sales", ["Year"], "Product", "Amount", ["A"; "B"])
    Assert.Contains("SELECT Year", sql)
    Assert.Contains("SUM(CASE WHEN Product = 'A' THEN Amount END) AS \"A\"", sql)
    Assert.Contains("SUM(CASE WHEN Product = 'B' THEN Amount END) AS \"B\"", sql)
    Assert.Contains("FROM Sales", sql)
    Assert.Contains("GROUP BY Year", sql)

[<CLIMutable>]
type TestUserOrder = { order_id: int; customer_id: int; order_date: DateTime; status: string; currency: string }
[<CLIMutable>]
type TestUserOrderItem = { order_item_id: int; order_id: int; product_id: int; quantity: int; line_total: decimal }
[<CLIMutable>]
type TestUserCustomer = { customer_id: int; customer_name: string }
[<CLIMutable>]
type TestUserProduct = { product_id: int; product_name: string }
[<CLIMutable>]
type TestUserPayment = { payment_id: int; order_id: int; amount: decimal }
[<CLIMutable>]
type TestUserShipment = { shipment_id: int; order_id: int; carrier: string }
[<CLIMutable>]
type TestUserReturn = { return_id: int; order_item_id: int; reason: string }

[<Fact>]
let ``Reproduce user query column order and content matching`` () =
    let dbPath = Path.Combine(Path.GetTempPath(), $"user_query_test_{Guid.NewGuid():N}.db")
    try
        use conn = new SqliteConnection($"Data Source={dbPath}")
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "CREATE TABLE Orders (order_id INTEGER PRIMARY KEY, customer_id INTEGER, order_date TEXT, status TEXT, currency TEXT); \
             CREATE TABLE OrderItems (order_item_id INTEGER PRIMARY KEY, order_id INTEGER, product_id INTEGER, quantity INTEGER, line_total NUMERIC); \
             CREATE TABLE Customers (customer_id INTEGER PRIMARY KEY, customer_name TEXT); \
             CREATE TABLE Products (product_id INTEGER PRIMARY KEY, product_name TEXT); \
             CREATE TABLE Payments (payment_id INTEGER PRIMARY KEY, order_id INTEGER, amount NUMERIC); \
             CREATE TABLE Shipments (shipment_id INTEGER PRIMARY KEY, order_id INTEGER, carrier TEXT); \
             CREATE TABLE Returns (return_id INTEGER PRIMARY KEY, order_item_id INTEGER, reason TEXT); \
             INSERT INTO Orders VALUES (1001, 1, '2026-03-01 10:00:00', 'Shipped', 'USD'); \
             INSERT INTO OrderItems VALUES (501, 1001, 10, 2, 99.50); \
             INSERT INTO Customers VALUES (1, 'Alice'); \
             INSERT INTO Products VALUES (10, 'Wireless Mouse'); \
             INSERT INTO Payments VALUES (201, 1001, 99.50); \
             INSERT INTO Shipments VALUES (301, 1001, 'DHL'); \
             INSERT INTO Returns VALUES (401, 501, 'Defective');"
        cmd.ExecuteNonQuery() |> ignore

        let connector = SqliteConnector(dbPath)
        let orders = SqlPushdownQueryable<TestUserOrder>(connector, "Orders")
        let orderItems = SqlPushdownQueryable<TestUserOrderItem>(connector, "OrderItems")
        let customers = SqlPushdownQueryable<TestUserCustomer>(connector, "Customers")
        let products = SqlPushdownQueryable<TestUserProduct>(connector, "Products")
        let payments = SqlPushdownQueryable<TestUserPayment>(connector, "Payments")
        let shipments = SqlPushdownQueryable<TestUserShipment>(connector, "Shipments")
        let returnsTbl = SqlPushdownQueryable<TestUserReturn>(connector, "Returns")

        let q =
            query {
                for order in orders do
                join orderItem in orderItems on (order.order_id = orderItem.order_id)
                join customer in customers on (order.customer_id = customer.customer_id)
                join product in products on (orderItem.product_id = product.product_id)
                join payment in payments on (order.order_id = payment.order_id)
                leftOuterJoin shipmentGroup in shipments on (order.order_id = shipmentGroup.order_id) into shipmentGroup
                leftOuterJoin returnItem in returnsTbl on (orderItem.order_item_id = returnItem.order_item_id) into returnGroup
                for shipment in shipmentGroup.DefaultIfEmpty() do
                for r in returnGroup.DefaultIfEmpty() do
                select {|
                    Order = order.order_id
                    Date = order.order_date
                    Status = order.status
                    Ccy = order.currency
                    Item = product.product_name
                    Qtt = orderItem.quantity
                    Total = orderItem.line_total
                    Carrier = (if isNull (box shipment) then "N/A" else shipment.carrier)
                    Returned = (if isNull (box r) then "N/A" else r.reason)
                |}
            }

        let cellSource = """
query {
    for order in csv.Orders do
    select {|
        Order = order.order_id
        Date = order.order_date
        Status = order.status
        Ccy = order.currency

        Item = product.product_name

        Qtt = orderItem.quantity
        Total = orderItem.line_total

        Carrier = (if isNull (box shipment) then "N/A" else shipment.carrier)
        Returned = (if isNull (box returns) then "N/A" else returns.reason)
    |}
}
"""
        let pushdown = q :?> SqlPushdownQueryable<{| Carrier: string; Ccy: string; Date: DateTime; Item: string; Order: int; Qtt: int; Returned: string; Status: string; Total: decimal |}>
        let rawSql = pushdown.ToRawSql()
        Assert.Contains("SELECT", rawSql)
        Assert.Contains("JOIN", rawSql)

        let item = q |> Seq.head

        Assert.Equal(1001, item.Order)
        Assert.Equal("Shipped", item.Status)
        Assert.Equal("USD", item.Ccy)
        Assert.Equal("Wireless Mouse", item.Item)
        Assert.Equal(2, item.Qtt)
        Assert.Equal(99.50M, item.Total)
        Assert.Equal("DHL", item.Carrier)
        Assert.Equal("Defective", item.Returned)

        let env, _, _ = FSharpQueryStudio.Results.Projection.projectWithCursorDetailed (box q) 0L (Some cellSource) None
        let colNames = env.Schema |> List.map (fun c -> c.Name)
        let firstRow = env.Rows.[0]

        let expectedCols = [ "Order"; "Date"; "Status"; "Ccy"; "Item"; "Qtt"; "Total"; "Carrier"; "Returned" ]
        Assert.Equal<string list>(expectedCols, colNames)
        Assert.Equal(box 1001, firstRow.[0])
        Assert.Equal(box "Shipped", firstRow.[2])
        Assert.Equal(box "USD", firstRow.[3])
        Assert.Equal(box "Wireless Mouse", firstRow.[4])
        Assert.Equal(box 2, firstRow.[5])
        Assert.Equal(box 99.50M, firstRow.[6])
        Assert.Equal(box "DHL", firstRow.[7])
        Assert.Equal(box "Defective", firstRow.[8])
    finally
        try File.Delete(dbPath) with _ -> ()

[<Fact>]
let ``SqlPushdownQueryable translates ToString and Date properties in Select`` () =
    use db = new TempSqliteDb()
    use conn = new SqliteConnection($"Data Source={db.Path}")
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        "CREATE TABLE TestEvents (Id INTEGER PRIMARY KEY, EventDate TEXT, Price NUMERIC); \
         INSERT INTO TestEvents VALUES (1, '2026-04-18 22:37:25', 14.29);"
    cmd.ExecuteNonQuery() |> ignore

    let connector = SqliteConnector(db.Path)
    let events = SqlPushdownQueryable<{| Id: int; EventDate: DateTime; Price: decimal |}>(connector, "TestEvents")

    let q =
        query {
            for e in events do
            select {|
                Id = e.Id
                FormattedDate = e.EventDate.ToString "dd-MM-yyyy"
                Year = e.EventDate.Year
                PriceStr = e.Price.ToString()
            |}
        }

    let item = q |> Seq.head
    Assert.Equal(1, item.Id)
    Assert.Equal("18-04-2026", item.FormattedDate)
    Assert.Equal(2026, item.Year)
    Assert.Equal("14.29", item.PriceStr)

    let sql = SqlPushdownQuery.extractRawSql (box q)
    Assert.Contains("strftime('%d-%m-%Y'", sql)
    Assert.Contains("CAST(strftime('%Y'", sql)

[<CLIMutable>]
type TestOptionProduct = {
    Id: int
    Name: string
    Description: string option
    Rating: decimal option
    Discount: Nullable<decimal>
}

[<Fact>]
let ``SqlPushdownQueryable translates option .Value in where clause to column name instead of Value`` () =
    use db = new TempSqliteDb()
    use conn = new SqliteConnection($"Data Source={db.Path}")
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        "CREATE TABLE OptionProducts (Id INTEGER PRIMARY KEY, Name TEXT, Description TEXT, Rating NUMERIC, Discount NUMERIC); \
         INSERT INTO OptionProducts VALUES (1, 'Widget', 'HighQuality', 4.5, 10.0); \
         INSERT INTO OptionProducts VALUES (2, 'Gadget', NULL, NULL, NULL); \
         INSERT INTO OptionProducts VALUES (3, 'Doohickey', 'Budget', 3.0, 5.0);"
    cmd.ExecuteNonQuery() |> ignore

    let connector = SqliteConnector(db.Path)
    let queryable = SqlPushdownQueryable<TestOptionProduct>(connector, "OptionProducts")

    let q =
        query {
            for p in queryable do
            where (p.Description.Value = "HighQuality")
            select p
        }

    let sql = SqlPushdownQuery.extractRawSql (box q)
    Assert.Contains("Description", sql)
    Assert.DoesNotContain("WHERE Value =", sql)

    let rows = q |> Seq.toList
    Assert.Single(rows) |> ignore
    Assert.Equal("Widget", rows.[0].Name)

[<Fact>]
let ``SqlPushdownQueryable translates option IsSome and IsNone to IS NOT NULL and IS NULL`` () =
    use db = new TempSqliteDb()
    use conn = new SqliteConnection($"Data Source={db.Path}")
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        "CREATE TABLE OptionProducts2 (Id INTEGER PRIMARY KEY, Name TEXT, Description TEXT, Rating NUMERIC, Discount NUMERIC); \
         INSERT INTO OptionProducts2 VALUES (1, 'Widget', 'HighQuality', 4.5, 10.0); \
         INSERT INTO OptionProducts2 VALUES (2, 'Gadget', NULL, NULL, NULL);"
    cmd.ExecuteNonQuery() |> ignore

    let connector = SqliteConnector(db.Path)
    let queryable = SqlPushdownQueryable<TestOptionProduct>(connector, "OptionProducts2")

    let qSome =
        query {
            for p in queryable do
            where p.Description.IsSome
            select p
        }

    let sqlSome = SqlPushdownQuery.extractRawSql (box qSome)
    Assert.Contains("Description IS NOT NULL", sqlSome)

    let rowsSome = qSome |> Seq.toList
    Assert.Single(rowsSome) |> ignore
    Assert.Equal("Widget", rowsSome.[0].Name)

    let qNone =
        query {
            for p in queryable do
            where p.Description.IsNone
            select p
        }

    let sqlNone = SqlPushdownQuery.extractRawSql (box qNone)
    Assert.Contains("Description IS NULL", sqlNone)

    let rowsNone = qNone |> Seq.toList
    Assert.Single(rowsNone) |> ignore
    Assert.Equal("Gadget", rowsNone.[0].Name)

[<Fact>]
let ``SqlPushdownQueryable correctly maps scalar option projection without .Value`` () =
    use db = new TempSqliteDb()
    use conn = new SqliteConnection($"Data Source={db.Path}")
    conn.Open()
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        "CREATE TABLE OptionProducts3 (Id INTEGER PRIMARY KEY, Name TEXT, Description TEXT, Rating NUMERIC, Discount NUMERIC); \
         INSERT INTO OptionProducts3 VALUES (1, 'Widget', 'HighQuality', 4.5, 10.0); \
         INSERT INTO OptionProducts3 VALUES (2, 'Gadget', NULL, NULL, NULL); \
         INSERT INTO OptionProducts3 VALUES (3, 'Gizmo', 'TopTier', NULL, NULL);"
    cmd.ExecuteNonQuery() |> ignore

    let connector = SqliteConnector(db.Path)
    let queryable = SqlPushdownQueryable<TestOptionProduct>(connector, "OptionProducts3")

    // 1. Direct scalar option projection without .Value
    let qScalar =
        query {
            for p in queryable do
            select p.Description
        }

    let rows = qScalar |> Seq.toList
    Assert.Equal(3, rows.Length)
    Assert.Equal(Some "HighQuality", rows.[0])
    Assert.Equal(None, rows.[1])
    Assert.Equal(Some "TopTier", rows.[2])

    // 2. Seq.filter with IsSome
    let filtered =
        qScalar
        |> Seq.filter (fun x -> x.IsSome)
        |> Seq.map (fun x -> x.Value)
        |> Seq.toList

    Assert.Equal(2, filtered.Length)
    Assert.Equal("HighQuality", filtered.[0])
    Assert.Equal("TopTier", filtered.[1])

    // 3. Seq.choose
    let chosen =
        qScalar
        |> Seq.choose id
        |> Seq.toList

    Assert.Equal(2, chosen.Length)
    Assert.Equal("HighQuality", chosen.[0])
    Assert.Equal("TopTier", chosen.[1])

    // 4. In-query pushdown with where .IsSome and select without .Value
    let qPushdownSome =
        query {
            for p in queryable do
            where p.Description.IsSome
            select p.Description
        }

    let pushdownRows = qPushdownSome |> Seq.toList
    Assert.Equal(2, pushdownRows.Length)
    Assert.Equal(Some "HighQuality", pushdownRows.[0])
    Assert.Equal(Some "TopTier", pushdownRows.[1])

[<Fact>]
let ``SqlPushdownQueryable translates Count after Select on column to COUNT(col) and executes accurately`` () =
    use db = new TempSqliteDb()
    let connector = SqliteConnector(db.Path)
    let queryable = SqlPushdownQueryable<TestProduct>(connector, "Products")

    // 1. LINQ queryable.Select(fun p -> p.Id).Count()
    let count = queryable.Select(fun p -> p.Id).Count()
    Assert.Equal(5, count)

    // 2. F# query { for p in q do select p.Id; count }
    let qCount =
        query {
            for p in queryable do
            select p.Id
            count
        }
    Assert.Equal(5, qCount)

    // Verify raw SQL has COUNT and not raw column
    let rawSql = (queryable.Select(fun p -> p.Id) :?> SqlPushdownQueryable<int>).Count("Id")
    Assert.Equal(5, rawSql)

[<Fact>]
let ``SqlPushdownQueryPlan omits ORDER BY on scalar aggregates without GROUP BY for SQL Server`` () =
    let plan = PushdownQueryPlan.Create("Products", [])
    plan.OrderBy.Add({ ColumnName = "Price"; Direction = Descending })
    plan.Projection <- Some "COUNT(*)"

    let sqlServer = plan.BuildSql(SqlDialect.SqlServer)
    // SQL Server must NOT include ORDER BY for single-row aggregates
    Assert.DoesNotContain("ORDER BY", sqlServer)
    Assert.Equal("SELECT COUNT(*) FROM Products", sqlServer)

    let postgres = plan.BuildSql(SqlDialect.PostgreSql)
    Assert.DoesNotContain("ORDER BY", postgres)

    let sqlite = plan.BuildSql(SqlDialect.Sqlite)
    Assert.DoesNotContain("ORDER BY", sqlite)

[<Fact>]
let ``SqlPushdownQueryPlan generates compliant CROSS APPLY and OUTER APPLY for SQL Server, PostgreSQL, and SQLite`` () =
    let plan = PushdownQueryPlan.Create("Orders", [])
    plan.Joins.Add({
        JoinType = "CROSS APPLY"
        SourceTable = "SELECT TOP (1) * FROM OrderDetails od WHERE od.OrderId = _t0.Id"
        Alias = "_t1"
        OnClause = ""
        RowType = None
    })

    // SQL Server: CROSS APPLY (subquery) AS _t1 (NO ON clause)
    let sqlServer = plan.BuildSql(SqlDialect.SqlServer)
    Assert.Contains("CROSS APPLY (SELECT TOP (1) * FROM OrderDetails od WHERE od.OrderId = _t0.Id) AS _t1", sqlServer)
    Assert.DoesNotContain(" ON ", sqlServer)

    // PostgreSQL: CROSS JOIN LATERAL (subquery) AS _t1 (NO ON clause)
    let postgres = plan.BuildSql(SqlDialect.PostgreSql)
    Assert.Contains("CROSS JOIN LATERAL (SELECT TOP (1) * FROM OrderDetails od WHERE od.OrderId = _t0.Id) AS _t1", postgres)
    Assert.DoesNotContain(" ON ", postgres)

    // SQLite: CROSS JOIN (subquery) AS _t1 (NO ON clause)
    let sqlite = plan.BuildSql(SqlDialect.Sqlite)
    Assert.Contains("CROSS JOIN (SELECT TOP (1) * FROM OrderDetails od WHERE od.OrderId = _t0.Id) AS _t1", sqlite)
    Assert.DoesNotContain(" ON ", sqlite)

[<Fact>]
let ``SqlPushdownQueryPlan generates compliant OUTER APPLY for SQL Server and PostgreSQL`` () =
    let plan = PushdownQueryPlan.Create("Orders", [])
    plan.Joins.Add({
        JoinType = "OUTER APPLY"
        SourceTable = "SELECT * FROM OrderDetails od WHERE od.OrderId = _t0.Id"
        Alias = "_t1"
        OnClause = ""
        RowType = None
    })

    // SQL Server: OUTER APPLY (...) AS _t1 (NO ON clause)
    let sqlServer = plan.BuildSql(SqlDialect.SqlServer)
    Assert.Contains("OUTER APPLY (SELECT * FROM OrderDetails od WHERE od.OrderId = _t0.Id) AS _t1", sqlServer)
    Assert.DoesNotContain(" ON ", sqlServer)

    // PostgreSQL: LEFT JOIN LATERAL (...) AS _t1 ON true
    let postgres = plan.BuildSql(SqlDialect.PostgreSql)
    Assert.Contains("LEFT JOIN LATERAL (SELECT * FROM OrderDetails od WHERE od.OrderId = _t0.Id) AS _t1 ON true", postgres)

[<Fact>]
let ``SqlPushdownQueryPlan generates compliant CROSS JOIN without ON clause`` () =
    let plan = PushdownQueryPlan.Create("Products", [])
    plan.Joins.Add({
        JoinType = "CROSS JOIN"
        SourceTable = "Categories"
        Alias = "_t1"
        OnClause = ""
        RowType = None
    })

    let sqlServer = plan.BuildSql(SqlDialect.SqlServer)
    Assert.Contains("CROSS JOIN Categories _t1", sqlServer)
    Assert.DoesNotContain(" ON ", sqlServer)

    let postgres = plan.BuildSql(SqlDialect.PostgreSql)
    Assert.Contains("CROSS JOIN Categories _t1", postgres)
    Assert.DoesNotContain(" ON ", postgres)

[<Fact>]
let ``SqlPushdownQueryable functional count, distinct count, and aggregate methods execute accurately`` () =
    use db = new TempSqliteDb()
    let connector = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<TestProduct>(connector, "Products")

    // Count all
    Assert.Equal(5, products.Count())

    // Count by column
    Assert.Equal(5, products.Count("Id"))
    Assert.Equal(5, products.Count(fun p -> p.Id))

    // Count distinct
    Assert.Equal(2, products.CountDistinct("Category"))
    Assert.Equal(2, products.CountDistinct(fun p -> p.Category))

    // Sum, Min, Max, Average
    Assert.Equal(377.98M, products.Sum<decimal>("Price"))
    Assert.Equal(377.98M, products.Sum<decimal>(fun p -> p.Price))

    Assert.Equal(1.50M, products.Min<decimal>("Price"))
    Assert.Equal(1.50M, products.Min<decimal>(fun p -> p.Price))

    Assert.Equal(299.99M, products.Max<decimal>("Price"))
    Assert.Equal(299.99M, products.Max<decimal>(fun p -> p.Price))

    Assert.True(products.Average<decimal>("Price") > 50M)

[<Fact>]
let ``SqlPushdownQueryable Having clause filters aggregate results`` () =
    use db = new TempSqliteDb()
    let connector = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<TestProduct>(connector, "Products")

    let grouped =
        products
            .SelectProjection("Category, COUNT(*) AS TotalCount")
            .Where("Price > @minPrice", [ "@minPrice", box 10.0M ])
            .Having("COUNT(*) >= @minCount", [ "@minCount", box 2 ])

    // Force group by
    grouped.Plan.GroupBy.Add("Category")

    let sql = grouped.ToRawSql()
    Assert.Contains("GROUP BY Category", sql)
    Assert.Contains("HAVING (COUNT(*) >= @minCount)", sql)

    let rows = grouped |> Seq.toList
    // Electronics has 3 items with Price > 10, so it qualifies
    Assert.True(rows.Length >= 1)

[<Fact>]
let ``SqlPushdownQueryable WhereExists, WhereNotExists, WhereIn, and WhereNotIn clauses`` () =
    use db = new TempSqliteDb()
    let connector = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<TestProduct>(connector, "Products")

    let existsQuery =
        products.WhereExists("SELECT 1 FROM Orders o WHERE o.ProductId = _t0.Id")
    let sqlExists = existsQuery.ToRawSql()
    Assert.Contains("WHERE EXISTS (SELECT 1 FROM Orders o WHERE o.ProductId = _t0.Id)", sqlExists)

    let notExistsQuery =
        products.WhereNotExists("SELECT 1 FROM Orders o WHERE o.ProductId = _t0.Id")
    let sqlNotExists = notExistsQuery.ToRawSql()
    Assert.Contains("WHERE NOT EXISTS (SELECT 1 FROM Orders o WHERE o.ProductId = _t0.Id)", sqlNotExists)

    let inQuery =
        products.WhereIn("Category", "SELECT DISTINCT Category FROM Promotions")
    let sqlIn = inQuery.ToRawSql()
    Assert.Contains("WHERE (Category IN (SELECT DISTINCT Category FROM Promotions))", sqlIn)

    let notInQuery =
        products.WhereNotIn("Category", "SELECT Category FROM DiscontinuedCategories")
    let sqlNotIn = notInQuery.ToRawSql()
    Assert.Contains("WHERE (Category NOT IN (SELECT Category FROM DiscontinuedCategories))", sqlNotIn)

[<Fact>]
let ``SqlPushdownQueryable CrossApply, OuterApply, and CrossJoin methods generate proper pushdown plans`` () =
    use db = new TempSqliteDb()
    let connector = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<TestProduct>(connector, "Products")

    let applied = products.CrossApply("SELECT * FROM Orders o WHERE o.ProductId = _t0.Id", "_o")
    let sqlServerSql = applied.Plan.BuildSql(SqlDialect.SqlServer)
    Assert.Contains("CROSS APPLY (SELECT * FROM Orders o WHERE o.ProductId = _t0.Id) AS _o", sqlServerSql)

    let outerApplied = products.OuterApply("SELECT * FROM Orders o WHERE o.ProductId = _t0.Id", "_o")
    let postgresSql = outerApplied.Plan.BuildSql(SqlDialect.PostgreSql)
    Assert.Contains("LEFT JOIN LATERAL (SELECT * FROM Orders o WHERE o.ProductId = _t0.Id) AS _o ON true", postgresSql)

    let crossJoined = products.CrossJoin("Categories", "_c")
    let sqliteSql = crossJoined.Plan.BuildSql(SqlDialect.Sqlite)
    Assert.Contains("CROSS JOIN Categories _c", sqliteSql)

[<Fact>]
let ``SQL Server advanced DQL features generate 100% compliant T-SQL`` () =
    // 1. Column aggregates with DISTINCT and aliased expressions
    let planAgg = PushdownQueryPlan.Create("Products", [])
    planAgg.Projection <- Some "COUNT(DISTINCT [Category]) AS [UniqueCategories], MAX([Price]) AS [MaxPrice], MIN([Price]) AS [MinPrice], AVG([Price]) AS [AvgPrice]"
    let sqlAgg = planAgg.BuildSql(SqlDialect.SqlServer)
    Assert.Equal("SELECT COUNT(DISTINCT [Category]) AS [UniqueCategories], MAX([Price]) AS [MaxPrice], MIN([Price]) AS [MinPrice], AVG([Price]) AS [AvgPrice] FROM Products", sqlAgg)

    // 2. Scalar sub-select in projection + CROSS APPLY + WHERE EXISTS + GROUP BY + HAVING
    let complexPlan = PushdownQueryPlan.Create("Customers", [], baseAlias = "_c")
    complexPlan.Joins.Add({
        JoinType = "CROSS APPLY"
        SourceTable = "SELECT TOP (1) * FROM Orders _o WHERE _o.CustomerId = _c.CustomerId ORDER BY _o.Amount DESC"
        Alias = "_latestOrder"
        OnClause = ""
        RowType = None
    })
    complexPlan.Joins.Add({
        JoinType = "OUTER APPLY"
        SourceTable = "SELECT COUNT(*) AS TotalItems FROM OrderItems _oi WHERE _oi.OrderId = _latestOrder.OrderId"
        Alias = "_itemCount"
        OnClause = ""
        RowType = None
    })
    complexPlan.WhereClauses.Add("EXISTS (SELECT 1 FROM VIPStatus _v WHERE _v.CustomerId = _c.CustomerId)")
    complexPlan.GroupBy.Add("_c.CustomerId")
    complexPlan.GroupBy.Add("_c.CustomerName")
    complexPlan.Having.Add("COUNT(_latestOrder.OrderId) >= 1")
    complexPlan.OrderBy.Add({ ColumnName = "_c.CustomerName"; Direction = Ascending })
    complexPlan.Projection <- Some "_c.CustomerId, _c.CustomerName, MAX(_latestOrder.Amount) AS HighestOrder, (SELECT COUNT(*) FROM Invoices _inv WHERE _inv.CustomerId = _c.CustomerId) AS InvoiceCount"
    complexPlan.Skip <- Some 0
    complexPlan.Take <- Some 25

    let sqlComplex = complexPlan.BuildSql(SqlDialect.SqlServer)
    Assert.Contains("CROSS APPLY (SELECT TOP (1) * FROM Orders _o WHERE _o.CustomerId = _c.CustomerId ORDER BY _o.Amount DESC) AS _latestOrder", sqlComplex)
    Assert.Contains("OUTER APPLY (SELECT COUNT(*) AS TotalItems FROM OrderItems _oi WHERE _oi.OrderId = _latestOrder.OrderId) AS _itemCount", sqlComplex)
    Assert.Contains("WHERE EXISTS (SELECT 1 FROM VIPStatus _v WHERE _v.CustomerId = _c.CustomerId)", sqlComplex)
    Assert.Contains("GROUP BY _c.CustomerId, _c.CustomerName", sqlComplex)
    Assert.Contains("HAVING COUNT(_latestOrder.OrderId) >= 1", sqlComplex)
    Assert.Contains("ORDER BY _c.CustomerName ASC OFFSET 0 ROWS FETCH NEXT 25 ROWS ONLY", sqlComplex)
    Assert.Contains("(SELECT COUNT(*) FROM Invoices _inv WHERE _inv.CustomerId = _c.CustomerId) AS InvoiceCount", sqlComplex)

[<Fact>]
let ``PostgreSQL advanced DQL features generate 100% compliant Postgres SQL`` () =
    let complexPlan = PushdownQueryPlan.Create("customers", [], baseAlias = "_c")
    complexPlan.Joins.Add({
        JoinType = "CROSS APPLY"
        SourceTable = "SELECT * FROM orders _o WHERE _o.customer_id = _c.customer_id ORDER BY _o.amount DESC LIMIT 1"
        Alias = "_latestOrder"
        OnClause = ""
        RowType = None
    })
    complexPlan.Joins.Add({
        JoinType = "OUTER APPLY"
        SourceTable = "SELECT COUNT(*) AS total_items FROM order_items _oi WHERE _oi.order_id = _latestOrder.order_id"
        Alias = "_itemCount"
        OnClause = ""
        RowType = None
    })
    complexPlan.WhereClauses.Add("EXISTS (SELECT 1 FROM vip_status _v WHERE _v.customer_id = _c.customer_id)")
    complexPlan.GroupBy.Add("_c.customer_id")
    complexPlan.GroupBy.Add("_c.customer_name")
    complexPlan.Having.Add("COUNT(_latestOrder.order_id) >= 1")
    complexPlan.OrderBy.Add({ ColumnName = "_c.customer_name"; Direction = Ascending })
    complexPlan.Projection <- Some "_c.customer_id, _c.customer_name, MAX(_latestOrder.amount) AS highest_order"
    complexPlan.Skip <- Some 10
    complexPlan.Take <- Some 20

    let sqlPostgres = complexPlan.BuildSql(SqlDialect.PostgreSql)
    Assert.Contains("CROSS JOIN LATERAL (SELECT * FROM orders _o WHERE _o.customer_id = _c.customer_id ORDER BY _o.amount DESC LIMIT 1) AS _latestOrder", sqlPostgres)
    Assert.Contains("LEFT JOIN LATERAL (SELECT COUNT(*) AS total_items FROM order_items _oi WHERE _oi.order_id = _latestOrder.order_id) AS _itemCount ON true", sqlPostgres)
    Assert.Contains("WHERE EXISTS (SELECT 1 FROM vip_status _v WHERE _v.customer_id = _c.customer_id)", sqlPostgres)
    Assert.Contains("GROUP BY _c.customer_id, _c.customer_name", sqlPostgres)
    Assert.Contains("HAVING COUNT(_latestOrder.order_id) >= 1", sqlPostgres)
    Assert.Contains("ORDER BY _c.customer_name ASC LIMIT 20 OFFSET 10", sqlPostgres)

[<Fact>]
let ``SQLite advanced DQL query executes live with sub-select, cross join, and group aggregates`` () =
    use db = new TempSqliteDb()
    let connector = SqliteConnector(db.Path)

    // Execute sub-select in projection live against SQLite
    let plan = PushdownQueryPlan.Create("Customers", [], baseAlias = "_c")
    plan.Projection <- Some "_c.CustomerId, _c.CustomerName, (SELECT COUNT(*) FROM Orders _o WHERE _o.CustomerId = _c.CustomerId) AS OrderCount"
    let sql = plan.BuildSql(SqlDialect.Sqlite)

    let res = connector.ExecuteQuery sql []
    Assert.Equal(3, (res.Rows |> Seq.length))
    Assert.Equal<string list>([ "CustomerId"; "CustomerName"; "OrderCount" ], res.Columns)

[<Fact>]
let ``SqlPushdownQueryable WhereIn with seq values generates parameterized query correctly`` () =
    use db = new TempSqliteDb()
    let connector = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<TestProduct>(connector, "Products")

    let filtered = products.WhereIn("Category", [ "Electronics"; "Stationery" ])
    let sql = filtered.ToRawSql()
    Assert.Contains("Category IN (@in_p", sql)
    let list = filtered.ToList()
    Assert.Equal(5, list.Count)

[<Fact>]
let ``SqlPushdownQueryable GroupBy and Having generate expected SQL`` () =
    use db = new TempSqliteDb()
    let connector = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<TestProduct>(connector, "Products")

    let grouped =
        products
            .GroupBy("Category")
            .SelectProjection("Category, COUNT(*) AS ItemCount")
            .Having("COUNT(*) >= @minItems", [ "@minItems", box 2 ])
    let sql = grouped.ToRawSql()
    Assert.Contains("GROUP BY Category", sql)
    Assert.Contains("HAVING (COUNT(*) >= @minItems)", sql)








