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




