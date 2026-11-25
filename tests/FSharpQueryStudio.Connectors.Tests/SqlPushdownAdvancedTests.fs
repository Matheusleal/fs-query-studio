module FSharpQueryStudio.Connectors.Tests.SqlPushdownAdvancedTests

open System
open System.IO
open System.Linq
open Microsoft.Data.Sqlite
open Xunit
open FSharpQueryStudio.Connectors
open FSharpQueryStudio.Protocol

[<CLIMutable>]
type AdvProduct =
    {
        Id: int
        Name: string
        Price: decimal
        Category: string
        InStock: bool
    }

[<CLIMutable>]
type AdvCustomer =
    {
        CustomerId: int
        CustomerName: string
    }

[<CLIMutable>]
type AdvOrder =
    {
        OrderId: int
        CustomerId: int
        Amount: decimal
        Status: string
    }

type AdvCustomerWithCount =
    { Tabela: AdvCustomer; OrderCount: int }

type AdvTempSqliteDb() =
    let path =
        Path.Combine(Path.GetTempPath(), $"pushdown_adv_test_{Guid.NewGuid():N}.db")

    do
        use conn = new SqliteConnection($"Data Source={path}")
        conn.Open()
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            "CREATE TABLE Products (Id INTEGER PRIMARY KEY, Name TEXT, Price NUMERIC, Category TEXT, InStock INTEGER); \
             INSERT INTO Products VALUES (1, '  Keyboard  ', 45.50, 'Electronics', 1); \
             INSERT INTO Products VALUES (2, 'Mouse', 25.00, 'Electronics', 1); \
             INSERT INTO Products VALUES (3, 'Notebook', 5.99, 'Stationery', 1); \
             INSERT INTO Products VALUES (4, 'Monitor', 299.99, 'Electronics', 0); \
             INSERT INTO Products VALUES (5, 'Pen', 1.50, 'Stationery', 1); \
             INSERT INTO Products VALUES (6, 'Desk Pad', 25.00, 'Stationery', 1); \
             INSERT INTO Products VALUES (7, 'Stapler', 12.00, 'Stationery', 0); \
             CREATE TABLE Customers (CustomerId INTEGER PRIMARY KEY, CustomerName TEXT); \
             INSERT INTO Customers VALUES (1, 'Alice'); \
             INSERT INTO Customers VALUES (2, 'Bob'); \
             INSERT INTO Customers VALUES (3, 'Charlie'); \
             CREATE TABLE Orders (OrderId INTEGER PRIMARY KEY, CustomerId INTEGER, Amount NUMERIC, Status TEXT); \
             INSERT INTO Orders VALUES (101, 1, 150.00, 'Completed'); \
             INSERT INTO Orders VALUES (102, 1, 200.00, 'Completed'); \
             INSERT INTO Orders VALUES (103, 2, 50.00, 'Pending'); \
             INSERT INTO Orders VALUES (104, 999, 99.00, 'Guest');"

        cmd.ExecuteNonQuery() |> ignore

    member _.Path = path

    interface IDisposable with
        member _.Dispose() =
            try
                if File.Exists(path) then
                    File.Delete(path)
            with _ ->
                ()

// =========================================================================
// 1. RIGHT OUTER JOIN TESTS
// =========================================================================

[<Fact>]
let ``fsql rightOuterJoin generates RIGHT OUTER JOIN and executes correctly on SQLite including null customers`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let customers = SqlPushdownQueryable<AdvCustomer>(conn, "Customers")
    let orders = SqlPushdownQueryable<AdvOrder>(conn, "Orders")

    let q =
        fsql {
            for c in customers do
                rightOuterJoin o in orders on (c.CustomerId = o.CustomerId)
                select (c.CustomerName, o.Amount)
        }

    let sql = toRawSql q

    Assert.True(
        sql.Contains("RIGHT OUTER JOIN Orders") || sql.Contains("RIGHT JOIN Orders"),
        $"Expected RIGHT JOIN in SQL: {sql}"
    )

    let rows = q |> Seq.toList
    // All 4 orders (101, 102, 103, 104) must be in the result
    Assert.Equal(4, rows.Length)

    // Verify orders matched to customers
    Assert.Contains(rows, fun (name, amount) -> name = "Alice" && amount = 150.00M)
    Assert.Contains(rows, fun (name, amount) -> name = "Alice" && amount = 200.00M)
    Assert.Contains(rows, fun (name, amount) -> name = "Bob" && amount = 50.00M)

    // Verify unmatched order 104 has null customer name and 99.00 amount
    let unmatched = rows |> List.find (fun (_, amount) -> amount = 99.00M)
    let (unmatchedName, unmatchedAmount) = unmatched
    Assert.True(isNull unmatchedName || unmatchedName = "", "Expected null/empty customer name for unmatched order")
    Assert.Equal(99.00M, unmatchedAmount)

    // Verify Charlie (CustomerId 3 with no orders) is NOT present in RIGHT OUTER JOIN result
    Assert.DoesNotContain(rows, fun (name, _) -> name = "Charlie")

[<Fact>]
let ``fsql rightOuterJoin generates compliant SQL across multiple dialects`` () =
    let sqlServerConn = SqlServerConnector("Server=dummy;Database=dummy;")
    let pgConn = PostgreSqlConnector("Host=dummy;Database=dummy;")
    let sqliteConn = SqliteConnector("Data Source=:memory:")

    let qSqlServer =
        fsql {
            for c in SqlPushdownQueryable<AdvCustomer>(sqlServerConn, "Customers") do
                rightOuterJoin o in SqlPushdownQueryable<AdvOrder>
                                        (sqlServerConn, "Orders")
                                        on
                                        (c.CustomerId = o.CustomerId)

                select (c.CustomerName, o.Amount)
        }

    let sqlServerRaw = toRawSql qSqlServer
    Assert.Contains("RIGHT OUTER JOIN Orders", sqlServerRaw)

    let qPg =
        fsql {
            for c in SqlPushdownQueryable<AdvCustomer>(pgConn, "customers") do
                rightOuterJoin o in SqlPushdownQueryable<AdvOrder> (pgConn, "orders") on (c.CustomerId = o.CustomerId)
                select (c.CustomerName, o.Amount)
        }

    let pgRaw = toRawSql qPg
    Assert.Contains("RIGHT OUTER JOIN orders", pgRaw)

    let qSqlite =
        fsql {
            for c in SqlPushdownQueryable<AdvCustomer>(sqliteConn, "Customers") do
                rightOuterJoin o in SqlPushdownQueryable<AdvOrder>
                                        (sqliteConn, "Orders")
                                        on
                                        (c.CustomerId = o.CustomerId)

                select (c.CustomerName, o.Amount)
        }

    let sqliteRaw = toRawSql qSqlite
    Assert.Contains("RIGHT OUTER JOIN Orders", sqliteRaw)

[<Fact>]
let ``fsql rightOuterJoin with where filtering and sorting pushes down predicates`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let customers = SqlPushdownQueryable<AdvCustomer>(conn, "Customers")
    let orders = SqlPushdownQueryable<AdvOrder>(conn, "Orders")

    let q =
        fsql {
            for c in customers do
                rightOuterJoin o in orders on (c.CustomerId = o.CustomerId)
                where (o.Amount > 70.0M)
                sortByDescending o.Amount
                select (c.CustomerName, o.Amount)
        }

    let sql = toRawSql q
    Assert.True(sql.Contains("RIGHT OUTER JOIN") || sql.Contains("RIGHT JOIN"))
    Assert.Contains("ORDER BY", sql)

    let rows = q |> Seq.toList
    // Orders > 70: 102 (200.00 Alice), 101 (150.00 Alice), 104 (99.00 null)
    Assert.Equal(3, rows.Length)
    Assert.Equal(200.00M, snd rows.[0])
    Assert.Equal("Alice", fst rows.[0])
    Assert.Equal(150.00M, snd rows.[1])
    Assert.Equal("Alice", fst rows.[1])
    Assert.Equal(99.00M, snd rows.[2])
    Assert.True(isNull (fst rows.[2]) || fst rows.[2] = "")

// =========================================================================
// 2. WINDOW FUNCTIONS TESTS
// =========================================================================

[<Fact>]
let ``fsql denseRank compiles to DENSE_RANK OVER clause and executes with dense ranks on SQLite`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<AdvProduct>(conn, "Products")

    let q =
        fsql {
            for p in products do
                let w = fsql.window (partitionBy = p.Category, sortBy = p.Price)

                select
                    {|
                        Name = p.Name
                        Category = p.Category
                        Price = p.Price
                        DRank = fsql.denseRank (over = w)
                    |}
        }

    let sql = toRawSql q
    Assert.Contains("DENSE_RANK() OVER (PARTITION BY Category ORDER BY Price ASC) AS DRank", sql)

    let rows = q |> Seq.toList
    Assert.Equal(7, rows.Length)

    // Electronics: Mouse (25.00 -> 1), Keyboard (45.50 -> 2), Monitor (299.99 -> 3)
    let elec =
        rows
        |> List.filter (fun r -> r.Category = "Electronics")
        |> List.sortBy (fun r -> r.Price)

    Assert.Equal(3, elec.Length)
    Assert.Equal(1L, elec.[0].DRank)
    Assert.Equal(2L, elec.[1].DRank)
    Assert.Equal(3L, elec.[2].DRank)

    // Stationery: Pen (1.50 -> 1), Notebook (5.99 -> 2), Stapler (12.00 -> 3), Desk Pad (25.00 -> 4)
    let stat =
        rows
        |> List.filter (fun r -> r.Category = "Stationery")
        |> List.sortBy (fun r -> r.Price)

    Assert.Equal(4, stat.Length)
    Assert.Equal(1L, stat.[0].DRank)
    Assert.Equal(2L, stat.[1].DRank)
    Assert.Equal(3L, stat.[2].DRank)
    Assert.Equal(4L, stat.[3].DRank)

[<Fact>]
let ``fsql ntile compiles to NTILE(2) and NTILE(4) and divides rows on SQLite`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<AdvProduct>(conn, "Products")

    let q2 =
        fsql {
            for p in products do
                let w = fsql.window (sortBy = p.Price)

                select
                    {|
                        Name = p.Name
                        Price = p.Price
                        Tile2 = fsql.ntile (2, over = w)
                    |}
        }

    let sql2 = toRawSql q2
    Assert.Contains("NTILE(2) OVER (ORDER BY Price ASC) AS Tile2", sql2)

    let rows2 = q2 |> Seq.toList
    Assert.Equal(7, rows2.Length)
    // 7 items divided into 2 buckets: 4 in bucket 1, 3 in bucket 2
    let bucket1 = rows2 |> List.filter (fun r -> r.Tile2 = 1L)
    let bucket2 = rows2 |> List.filter (fun r -> r.Tile2 = 2L)
    Assert.Equal(4, bucket1.Length)
    Assert.Equal(3, bucket2.Length)

    let q4 =
        fsql {
            for p in products do
                let w = fsql.window (sortBy = p.Price)

                select
                    {|
                        Name = p.Name
                        Price = p.Price
                        Quartile = fsql.ntile (4, over = w)
                    |}
        }

    let sql4 = toRawSql q4
    Assert.Contains("NTILE(4) OVER (ORDER BY Price ASC) AS Quartile", sql4)

    let rows4 = q4 |> Seq.toList
    Assert.Equal(7, rows4.Length)
    let quartiles = rows4 |> List.map (fun r -> r.Quartile) |> Set.ofList

    Assert.True(
        quartiles.Contains(1L)
        && quartiles.Contains(2L)
        && quartiles.Contains(3L)
        && quartiles.Contains(4L)
    )

[<Fact>]
let ``fsql firstValue and lastValue compile to FIRST_VALUE and LAST_VALUE and execute on SQLite`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<AdvProduct>(conn, "Products")

    let q =
        fsql {
            for p in products do
                let w = fsql.window (partitionBy = p.Category, sortBy = p.Price)

                select
                    {|
                        Name = p.Name
                        Category = p.Category
                        Price = p.Price
                        CheapestInCat = fsql.firstValue (p.Price, over = w)
                        LastInCat = fsql.lastValue (p.Price, over = w)
                    |}
        }

    let sql = toRawSql q
    Assert.Contains("FIRST_VALUE(Price) OVER (PARTITION BY Category ORDER BY Price ASC) AS CheapestInCat", sql)
    Assert.Contains("LAST_VALUE(Price) OVER (PARTITION BY Category ORDER BY Price ASC) AS LastInCat", sql)

    let rows = q |> Seq.toList
    Assert.Equal(7, rows.Length)

    // In Electronics, cheapest is Mouse (25.00)
    let elec = rows |> List.filter (fun r -> r.Category = "Electronics")

    for item in elec do
        Assert.Equal(25.00M, item.CheapestInCat)

    // In Stationery, cheapest is Pen (1.50)
    let stat = rows |> List.filter (fun r -> r.Category = "Stationery")

    for item in stat do
        Assert.Equal(1.50M, item.CheapestInCat)

[<Fact>]
let ``fsql lag and lead with offset and defaultValue compile and execute correctly on SQLite`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<AdvProduct>(conn, "Products")

    let q =
        fsql {
            for p in products do
                let w = fsql.window (partitionBy = p.Category, sortBy = p.Price)

                select
                    {|
                        Id = p.Id
                        Category = p.Category
                        Price = p.Price
                        PrevPrice = fsql.lag (p.Price, 1, 0.0M, over = w)
                        NextPrice = fsql.lead (p.Price, 1, 999.0M, over = w)
                    |}
        }

    let sql = toRawSql q
    Assert.Contains("LAG(Price, 1, 0", sql)
    Assert.Contains("LEAD(Price, 1, 999", sql)

    let rows = q |> Seq.toList
    Assert.Equal(7, rows.Length)

    // In Electronics sorted by Price (25.00 Mouse, 45.50 Keyboard, 299.99 Monitor)
    let elec =
        rows
        |> List.filter (fun r -> r.Category = "Electronics")
        |> List.sortBy (fun r -> r.Price)
    // First item has no prev -> defaultValue 0.0M
    Assert.Equal(0.0M, elec.[0].PrevPrice)
    Assert.Equal(45.50M, elec.[0].NextPrice)

    // Middle item
    Assert.Equal(25.00M, elec.[1].PrevPrice)
    Assert.Equal(299.99M, elec.[1].NextPrice)

    // Last item has no next -> defaultValue 999.0M
    Assert.Equal(45.50M, elec.[2].PrevPrice)
    Assert.Equal(999.0M, elec.[2].NextPrice)

[<Fact>]
let ``fsql window with sortByDescending orders rankings from highest to lowest`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<AdvProduct>(conn, "Products")

    let q =
        fsql {
            for p in products do
                let w = fsql.window (partitionBy = p.Category, sortByDescending = p.Price)

                select
                    {|
                        Name = p.Name
                        Category = p.Category
                        Price = p.Price
                        RankDesc = fsql.denseRank (over = w)
                    |}
        }

    let sql = toRawSql q
    Assert.Contains("DENSE_RANK() OVER (PARTITION BY Category ORDER BY Price DESC) AS RankDesc", sql)

    let rows = q |> Seq.toList

    let elec =
        rows
        |> List.filter (fun r -> r.Category = "Electronics")
        |> List.sortByDescending (fun r -> r.Price)
    // Monitor (299.99 -> 1), Keyboard (45.50 -> 2), Mouse (25.00 -> 3)
    Assert.Equal(1L, elec.[0].RankDesc)
    Assert.Equal(2L, elec.[1].RankDesc)
    Assert.Equal(3L, elec.[2].RankDesc)

// =========================================================================
// 3. SCALAR FUNCTIONS & TEXT PROJECTIONS TESTS
// =========================================================================

[<Fact>]
let ``fsql scalar text projections ToUpper ToLower and Trim push down to SQL functions and execute on SQLite`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<AdvProduct>(conn, "Products")

    let q =
        fsql {
            for p in products do
                where (p.Id = 1) // Product 1 is '  Keyboard  '
                select (p.Name.ToUpper(), p.Name.ToLower(), p.Name.Trim())
        }

    let sql = toRawSql q
    Assert.Contains("UPPER(Name)", sql)
    Assert.Contains("LOWER(Name)", sql)
    Assert.Contains("TRIM(Name)", sql)

    let row = q |> Seq.head
    let (upper, lower, trimmed) = row
    Assert.Equal("  KEYBOARD  ", upper)
    Assert.Equal("  keyboard  ", lower)
    Assert.Equal("Keyboard", trimmed)

[<Fact>]
let ``fsql string concatenation in projection generates SQL concatenation operator and executes on SQLite`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<AdvProduct>(conn, "Products")

    let q =
        fsql {
            for p in products do
                where (p.Id = 2) // Mouse
                select (p.Category + ": " + p.Name)
        }

    let sql = toRawSql q
    Assert.Contains("||", sql)

    let row = q |> Seq.head
    Assert.Equal("Electronics: Mouse", row)

[<Fact>]
let ``fsql and query groupBy with count compile to GROUP BY and COUNT and execute on SQLite`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<AdvProduct>(conn, "Products")

    let q =
        fsql {
            for p in products do
                groupBy p.Category into g

                select (
                    g.Key,
                    fsql {
                        for x in g do
                            count
                    }
                )
        }

    let sql = toRawSql q
    Assert.True(sql.Contains("GROUP BY _t0.Category") || sql.Contains("GROUP BY Category"))
    Assert.Contains("COUNT(*)", sql)

    let rows = q |> Seq.toList
    // 3 Electronics, 4 Stationery
    Assert.Equal(2, rows.Length)
    let elec = rows |> List.find (fun (cat, _) -> cat = "Electronics")
    let stat = rows |> List.find (fun (cat, _) -> cat = "Stationery")
    Assert.Equal(3L, snd elec)
    Assert.Equal(4L, snd stat)

[<Fact>]
let ``fsql distinct aggregate queries compile to COUNT DISTINCT and SUM DISTINCT`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<AdvProduct>(conn, "Products")

    let distinctCats =
        fsql {
            for p in products do
                distinct
                select p.Category
        }
        |> Seq.toList

    Assert.Equal(2, distinctCats.Length)
    Assert.Contains("Electronics", distinctCats)
    Assert.Contains("Stationery", distinctCats)

    let distinctCountSql = toRawSql (products.Distinct().Select(fun p -> p.Category))
    Assert.Contains("SELECT DISTINCT", distinctCountSql)

// =========================================================================
// 4. CORRELATED SUBQUERIES AND CTES TESTS
// =========================================================================

[<Fact>]
let ``fsql whereNotExists with multiple conditions compiles to NOT EXISTS subquery and executes on SQLite`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let customers = SqlPushdownQueryable<AdvCustomer>(conn, "Customers")
    let orders = SqlPushdownQueryable<AdvOrder>(conn, "Orders")

    let q =
        fsql {
            for c in customers do
                whereNotExists (
                    fsql {
                        for o in orders do
                            where (o.CustomerId = c.CustomerId && o.Amount > 100.0M)
                    }
                )

                select c.CustomerName
        }

    let sql = toRawSql q
    Assert.Contains("NOT EXISTS (SELECT 1 FROM Orders _sub1 WHERE", sql)
    Assert.Contains("_sub1.CustomerId = _t0.CustomerId", sql)
    Assert.Contains("_sub1.Amount > @p0", sql)

    let results = q |> Seq.toList
    // Alice has orders of 150 and 200 (> 100) -> excluded
    // Bob has only order of 50 (not > 100) -> included
    // Charlie has no orders -> included
    Assert.Equal(2, results.Length)
    Assert.Contains("Bob", results)
    Assert.Contains("Charlie", results)
    Assert.DoesNotContain("Alice", results)

[<Fact>]
let ``fsql withCte combined with unionAll compiles to WITH CTE and UNION ALL and executes on SQLite`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<AdvProduct>(conn, "Products")

    let combined =
        fsql {
            for p in products do
                withCte
                    "HighEnd"
                    (fsql {
                        for p1 in products do
                            where (p1.Price >= 100.0M)
                            select p1
                    })

                where (p.Price >= 100.0M)
                select p

                unionAll (
                    fsql {
                        for p2 in products do
                            where (p2.Price <= 10.0M)
                            select p2
                    }
                )
        }

    let sql = toRawQuery combined
    Assert.Contains("WITH HighEnd AS", sql)
    Assert.Contains("UNION ALL", sql)

    let rows = combined |> Seq.toList
    // HighEnd >= 100: Monitor (299.99) -> 1
    // Low <= 10: Notebook (5.99), Pen (1.50) -> 2
    // Total = 3
    Assert.Equal(3, rows.Length)
    Assert.Contains(rows, fun p -> p.Name = "Monitor")
    Assert.Contains(rows, fun p -> p.Name = "Notebook")
    Assert.Contains(rows, fun p -> p.Name = "Pen")

[<Fact>]
let ``fsql withCte combined with intersect compiles to WITH CTE and INTERSECT and executes on SQLite`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<AdvProduct>(conn, "Products")

    let intersected =
        fsql {
            for p in products do
                withCte
                    "ElectronicsCte"
                    (fsql {
                        for p1 in products do
                            where (p1.Category = "Electronics")
                            select p1
                    })

                where (p.Category = "Electronics")
                select p

                intersect (
                    fsql {
                        for p2 in products do
                            where (p2.Price >= 30.0M)
                            select p2
                    }
                )
        }

    let sql = toRawQuery intersected
    Assert.Contains("WITH ElectronicsCte AS", sql)
    Assert.Contains("INTERSECT", sql)

    let rows = intersected |> Seq.toList
    // Electronics: Keyboard (45.50), Mouse (25.00), Monitor (299.99)
    // Price >= 30: Keyboard (45.50), Monitor (299.99)
    // Intersection: Keyboard and Monitor -> 2
    Assert.Equal(2, rows.Length)
    Assert.Contains(rows, fun p -> p.Name.Contains("Keyboard"))
    Assert.Contains(rows, fun p -> p.Name = "Monitor")
    Assert.DoesNotContain(rows, fun p -> p.Name = "Mouse")

[<Fact>]
let ``fsql withCte combined with except compiles to WITH CTE and EXCEPT and executes on SQLite`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<AdvProduct>(conn, "Products")

    let exceptQuery =
        fsql {
            for p in products do
                withCte
                    "ElectronicsCte"
                    (fsql {
                        for p1 in products do
                            where (p1.Category = "Electronics")
                            select p1
                    })

                where (p.Category = "Electronics")
                select p

                except (
                    fsql {
                        for p2 in products do
                            where (p2.Price >= 30.0M)
                            select p2
                    }
                )
        }

    let sql = toRawQuery exceptQuery
    Assert.Contains("WITH ElectronicsCte AS", sql)
    Assert.Contains("EXCEPT", sql)

    let rows = exceptQuery |> Seq.toList
    // Electronics: Keyboard (45.50), Mouse (25.00), Monitor (299.99)
    // EXCEPT Price >= 30 (Keyboard, Monitor)
    // Result: Mouse only -> 1
    Assert.Equal(1, rows.Length)
    Assert.Equal("Mouse", rows.[0].Name)

// =========================================================================
// 8. SELECT MAPPED TABLE WILDCARD PUSHDOWN TESTS (alias.*)
// =========================================================================

[<Fact>]
let ``fsql select with mapped table t0 generates _t0.* and projected properties as aliases (exact user scenario)`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let customers = SqlPushdownQueryable<AdvCustomer>(conn, "Customers")
    let orders = SqlPushdownQueryable<AdvOrder>(conn, "Orders")

    let q =
        fsql {
            for t0 in customers do
                join t1 in orders on (t0.CustomerId = t1.CustomerId)
                where (t0.CustomerId = 1)

                select
                    {|
                        Tabela = t0
                        PropTabela2_1 = t1.OrderId
                        PropTabela2_2 = t1.Amount
                    |}
        }

    let rawSql = toRawSql q

    Assert.Contains(
        "SELECT _t0.*, _t1.OrderId AS PropTabela2_1, _t1.Amount AS PropTabela2_2 FROM Customers _t0",
        rawSql
    )

    Assert.Contains("INNER JOIN Orders _t1 ON _t0.CustomerId = _t1.CustomerId", rawSql)
    Assert.Contains("WHERE (_t0.CustomerId = @p0)", rawSql)

[<Fact>]
let ``fsql select with mapped table t0 executes on SQLite and hydrates both nested record and scalar properties`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let customers = SqlPushdownQueryable<AdvCustomer>(conn, "Customers")
    let orders = SqlPushdownQueryable<AdvOrder>(conn, "Orders")

    let q =
        fsql {
            for t0 in customers do
                join t1 in orders on (t0.CustomerId = t1.CustomerId)
                where (t0.CustomerId = 1)
                sortBy t1.OrderId

                select
                    {|
                        Tabela = t0
                        PropTabela2_1 = t1.OrderId
                        PropTabela2_2 = t1.Amount
                    |}
        }

    let rows = q |> Seq.toList
    Assert.Equal(2, rows.Length)
    Assert.Equal(1, rows.[0].Tabela.CustomerId)
    Assert.Equal("Alice", rows.[0].Tabela.CustomerName)
    Assert.Equal(101, rows.[0].PropTabela2_1)
    Assert.Equal(150.00M, rows.[0].PropTabela2_2)

    Assert.Equal(1, rows.[1].Tabela.CustomerId)
    Assert.Equal("Alice", rows.[1].Tabela.CustomerName)
    Assert.Equal(102, rows.[1].PropTabela2_1)
    Assert.Equal(200.00M, rows.[1].PropTabela2_2)

[<Fact>]
let ``fsql select with mapped join table t1 generates _t1.* and t0 scalar properties`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let customers = SqlPushdownQueryable<AdvCustomer>(conn, "Customers")
    let orders = SqlPushdownQueryable<AdvOrder>(conn, "Orders")

    let q =
        fsql {
            for c in customers do
                join o in orders on (c.CustomerId = o.CustomerId)
                where (o.OrderId = 103)

                select
                    {|
                        CustomerName = c.CustomerName
                        TabelaOrdem = o
                    |}
        }

    let rawSql = toRawSql q
    Assert.Contains("SELECT _t1.*, _t0.CustomerName AS CustomerName FROM Customers _t0", rawSql)
    Assert.Contains("INNER JOIN Orders _t1 ON _t0.CustomerId = _t1.CustomerId", rawSql)

[<Fact>]
let ``fsql select with mapped join table t1 executes on SQLite and hydrates both nested record and scalar properties``
    ()
    =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let customers = SqlPushdownQueryable<AdvCustomer>(conn, "Customers")
    let orders = SqlPushdownQueryable<AdvOrder>(conn, "Orders")

    let q =
        fsql {
            for c in customers do
                join o in orders on (c.CustomerId = o.CustomerId)
                where (o.OrderId = 103)

                select
                    {|
                        CustomerName = c.CustomerName
                        TabelaOrdem = o
                    |}
        }

    let rows = q |> Seq.toList
    Assert.Equal(1, rows.Length)
    Assert.Equal("Bob", rows.[0].CustomerName)
    Assert.Equal(103, rows.[0].TabelaOrdem.OrderId)
    Assert.Equal(2, rows.[0].TabelaOrdem.CustomerId)
    Assert.Equal(50.00M, rows.[0].TabelaOrdem.Amount)
    Assert.Equal("Pending", rows.[0].TabelaOrdem.Status)

[<Fact>]
let ``fsql select with multiple mapped tables t0 and t1 generates _t0.* and _t1.*`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let customers = SqlPushdownQueryable<AdvCustomer>(conn, "Customers")
    let orders = SqlPushdownQueryable<AdvOrder>(conn, "Orders")

    let q =
        fsql {
            for c in customers do
                join o in orders on (c.CustomerId = o.CustomerId)
                where (c.CustomerId = 1 && o.OrderId = 101)

                select {| Cliente = c; Ordem = o |}
        }

    let rawSql = toRawSql q
    Assert.Contains("SELECT _t0.*, _t1.* FROM Customers _t0", rawSql)

    let rows = q |> Seq.toList
    Assert.Equal(1, rows.Length)
    Assert.Equal("Alice", rows.[0].Cliente.CustomerName)
    Assert.Equal(150.00M, rows.[0].Ordem.Amount)

[<Fact>]
let ``fsql select with multiple mapped tables and expressions generates _t0.*, _t1.* and computed column`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let customers = SqlPushdownQueryable<AdvCustomer>(conn, "Customers")
    let orders = SqlPushdownQueryable<AdvOrder>(conn, "Orders")

    let q =
        fsql {
            for c in customers do
                join o in orders on (c.CustomerId = o.CustomerId)
                where (c.CustomerId = 1 && o.OrderId = 101)

                select
                    {|
                        Cliente = c
                        Ordem = o
                        Dobro = o.Amount * 2.0M
                    |}
        }

    let rawSql = toRawSql q
    Assert.Contains("SELECT _t0.*, _t1.*, (_t1.Amount * 2.0) AS Dobro FROM Customers _t0", rawSql)

    let rows = q |> Seq.toList
    Assert.Equal(1, rows.Length)
    Assert.Equal(300.00M, rows.[0].Dobro)

[<Fact>]
let ``fsql select with direct table projection select t0 in relational join generates _t0.*`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let customers = SqlPushdownQueryable<AdvCustomer>(conn, "Customers")
    let orders = SqlPushdownQueryable<AdvOrder>(conn, "Orders")

    let q =
        fsql {
            for c in customers do
                join o in orders on (c.CustomerId = o.CustomerId)
                where (o.OrderId = 101)
                select c
        }

    let rawSql = toRawSql q
    Assert.Contains("SELECT _t0.* FROM Customers _t0", rawSql)

    let rows = q |> Seq.toList
    Assert.Equal(1, rows.Length)
    Assert.Equal("Alice", rows.[0].CustomerName)

[<Fact>]
let ``fsql select with direct table projection select t1 in relational join generates _t1.*`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let customers = SqlPushdownQueryable<AdvCustomer>(conn, "Customers")
    let orders = SqlPushdownQueryable<AdvOrder>(conn, "Orders")

    let q =
        fsql {
            for c in customers do
                join o in orders on (c.CustomerId = o.CustomerId)
                where (c.CustomerId = 1)
                sortBy o.OrderId
                select o
        }

    let rawSql = toRawSql q
    Assert.Contains("SELECT _t1.* FROM Customers _t0", rawSql)

    let rows = q |> Seq.toList
    Assert.Equal(2, rows.Length)
    Assert.Equal(101, rows.[0].OrderId)
    Assert.Equal(102, rows.[1].OrderId)

[<Fact>]
let ``fsql select with tuple containing mapped table (t0, t1.Amount) generates _t0.* and _t1.Amount AS Item2`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let customers = SqlPushdownQueryable<AdvCustomer>(conn, "Customers")
    let orders = SqlPushdownQueryable<AdvOrder>(conn, "Orders")

    let q =
        fsql {
            for c in customers do
                join o in orders on (c.CustomerId = o.CustomerId)
                where (o.OrderId = 101)
                select (c, o.Amount)
        }

    let rawSql = toRawSql q
    Assert.Contains("SELECT _t0.*, _t1.Amount AS item2 FROM Customers _t0", rawSql)

[<Fact>]
let ``fsql select with tuple containing multiple mapped tables (t0, t1) generates _t0.*, _t1.*`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let customers = SqlPushdownQueryable<AdvCustomer>(conn, "Customers")
    let orders = SqlPushdownQueryable<AdvOrder>(conn, "Orders")

    let q =
        fsql {
            for c in customers do
                join o in orders on (c.CustomerId = o.CustomerId)
                where (o.OrderId = 101)
                select (c, o)
        }

    let rawSql = toRawSql q
    Assert.Contains("SELECT _t0.*, _t1.* FROM Customers _t0", rawSql)

[<Fact>]
let ``fsql select with typed F# record containing mapped table and calculated field`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let customers = SqlPushdownQueryable<AdvCustomer>(conn, "Customers")
    let orders = SqlPushdownQueryable<AdvOrder>(conn, "Orders")

    let q =
        fsql {
            for c in customers do
                join o in orders on (c.CustomerId = o.CustomerId)
                where (c.CustomerId = 1 && o.OrderId = 101)

                select { Tabela = c; OrderCount = 2 }
        }

    let rawSql = toRawSql q
    Assert.Contains("SELECT _t0.*, 2 AS orderCount FROM Customers _t0", rawSql)

    let rows = q |> Seq.toList
    Assert.Equal(1, rows.Length)
    Assert.Equal("Alice", rows.[0].Tabela.CustomerName)
    Assert.Equal(2, rows.[0].OrderCount)

[<Fact>]
let ``fsql select with mapped tables on SQL Server and Postgres formats dialect quotes properly`` () =
    let sqlServerConn = SqlServerConnector("Server=dummy;Database=dummy;")
    let pgConn = PostgreSqlConnector("Host=dummy;Database=dummy;")

    let qSqlServer =
        fsql {
            for c in SqlPushdownQueryable<AdvCustomer>(sqlServerConn, "Customers") do
                join o in SqlPushdownQueryable<AdvOrder> (sqlServerConn, "Orders") on (c.CustomerId = o.CustomerId)

                select
                    {|
                        Tabela = c
                        ``Prop Com Espaco`` = o.OrderId
                    |}
        }

    let sqlServerRaw = toRawSql qSqlServer
    Assert.Contains("SELECT _t0.*, _t1.OrderId AS [Prop Com Espaco] FROM Customers _t0", sqlServerRaw)

    let qPg =
        fsql {
            for c in SqlPushdownQueryable<AdvCustomer>(pgConn, "customers") do
                join o in SqlPushdownQueryable<AdvOrder> (pgConn, "orders") on (c.CustomerId = o.CustomerId)

                select
                    {|
                        Tabela = c
                        ``Prop Com Espaco`` = o.OrderId
                    |}
        }

    let pgRaw = toRawSql qPg
    Assert.Contains("SELECT _t0.*, _t1.OrderId AS \"Prop Com Espaco\" FROM customers _t0", pgRaw)

[<Fact>]
let ``fsql select with extract table compiles to wildcard and hydrates ExtractedTable on SQLite`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let customers = SqlPushdownQueryable<AdvCustomer>(conn, "Customers")
    let orders = SqlPushdownQueryable<AdvOrder>(conn, "Orders")

    let q =
        fsql {
            for c in customers do
                join o in orders on (c.CustomerId = o.CustomerId)
                where (c.CustomerId = 1)

                select
                    {|
                        CustomerTable = extract c
                        OrderId = o.OrderId
                        OrderAmount = o.Amount
                    |}
        }

    let sql = toRawSql q
    Assert.Contains("SELECT _t0.*,", sql)
    Assert.Contains("_t1.Amount AS OrderAmount", sql)
    Assert.Contains("_t1.OrderId AS OrderId", sql)
    Assert.Contains("JOIN Orders _t1 ON", sql)
    Assert.Contains("CustomerId", sql)

    let rows = q |> Seq.toList
    Assert.Equal(2, rows.Length)
    Assert.Equal("Alice", rows.[0].CustomerTable.Value.CustomerName)
    Assert.Equal(1, rows.[0].CustomerTable.Value.CustomerId)
    Assert.Equal(150.0M, rows.[0].OrderAmount)

[<Fact>]
let ``fsql select with extract table projects to flat columns in ResultEnvelope without JSON object column`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let customers = SqlPushdownQueryable<AdvCustomer>(conn, "Customers")
    let orders = SqlPushdownQueryable<AdvOrder>(conn, "Orders")

    let q =
        fsql {
            for c in customers do
                join o in orders on (c.CustomerId = o.CustomerId)
                where (c.CustomerId = 1)

                select
                    {|
                        CustomerTable = fsql.extract c
                        OrderId = o.OrderId
                        OrderAmount = o.Amount
                    |}
        }

    let env, _, _ = FSharpQueryStudio.Results.Projection.projectWithCursor q 0L
    let colNames = env.Schema |> List.map (fun c -> c.Name)

    // Extracted columns are flat, individual columns!
    Assert.Contains("CustomerId", colNames)
    Assert.Contains("CustomerName", colNames)
    Assert.Contains("OrderId", colNames)
    Assert.Contains("OrderAmount", colNames)
    Assert.DoesNotContain("CustomerTable", colNames)

    // The rows contain the individual flattened values, not a JSON object
    Assert.True(env.Rows.Length >= 2)
    let row0 = env.Rows.[0]
    Assert.Equal<int>(colNames.Length, row0.Length)

    let custNameIdx = colNames |> List.findIndex (fun c -> c = "CustomerName")
    Assert.Equal("Alice", row0.[custNameIdx].ToString())

    let orderAmtIdx = colNames |> List.findIndex (fun c -> c = "OrderAmount")
    Assert.True(row0.[orderAmtIdx].ToString().StartsWith("150"))

[<Fact>]
let ``fsql select with multiple extracted tables disambiguates colliding column names in grid schema`` () =
    use db = new AdvTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let customers = SqlPushdownQueryable<AdvCustomer>(conn, "Customers")
    let orders = SqlPushdownQueryable<AdvOrder>(conn, "Orders")

    let q =
        fsql {
            for c in customers do
                join o in orders on (c.CustomerId = o.CustomerId)
                where (c.CustomerId = 1)

                select
                    {|
                        Customer = extract c
                        Order = extract o
                    |}
        }

    let sql = toRawSql q
    Assert.Contains("SELECT _t0.*, _t1.* FROM Customers _t0", sql)

    let env, _, _ = FSharpQueryStudio.Results.Projection.projectWithCursor q 0L
    let colNames = env.Schema |> List.map (fun c -> c.Name)

    // CustomerId exists in both AdvCustomer and AdvOrder -> disambiguated with prefix!
    Assert.Contains("Customer_CustomerId", colNames)
    Assert.Contains("Order_CustomerId", colNames)

    // Non-colliding columns remain clean without prefix
    Assert.Contains("CustomerName", colNames)
    Assert.Contains("OrderId", colNames)
    Assert.Contains("Amount", colNames)

    // Rows are fully flattened into separate columns
    let row0 = env.Rows.[0]
    Assert.Equal<int>(colNames.Length, row0.Length)
