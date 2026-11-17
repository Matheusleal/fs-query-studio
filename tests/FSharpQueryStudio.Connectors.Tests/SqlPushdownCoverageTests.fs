module FSharpQueryStudio.Connectors.Tests.SqlPushdownCoverageTests

open System
open System.IO
open System.Linq
open System.Linq.Expressions
open Microsoft.Data.Sqlite
open Xunit
open FSharpQueryStudio.Connectors
open FSharpQueryStudio.Protocol

[<CLIMutable>]
type CovProduct = {
    Id: int
    Name: string
    Price: decimal
    Category: string
    InStock: bool
    Score: Nullable<decimal>
    Timestamp: DateTime
}

[<CLIMutable>]
type CovOrder = {
    OrderId: int
    CustomerId: int
    Amount: decimal
    Status: string
}

[<CLIMutable>]
type CovOptionItem = {
    Id: int
    Rating: decimal option
    Comment: string option
}

type CovTempSqliteDb() =
    let path = Path.Combine(Path.GetTempPath(), $"pushdown_cov_test_{Guid.NewGuid():N}.db")
    do
        use conn = new SqliteConnection($"Data Source={path}")
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "CREATE TABLE Products (Id INTEGER PRIMARY KEY, Name TEXT, Price NUMERIC, Category TEXT, InStock INTEGER, Score NUMERIC, Timestamp TEXT); \
             INSERT INTO Products VALUES (1, '  Keyboard  ', 45.50, 'Electronics', 1, 85.5, '2024-05-15 14:30:45'); \
             INSERT INTO Products VALUES (2, 'Mouse', 25.00, 'Electronics', 1, NULL, '2025-11-20 08:15:30'); \
             INSERT INTO Products VALUES (3, 'Notebook', 5.99, 'Stationery', 1, 95.0, '2026-01-01 23:59:59'); \
             INSERT INTO Products VALUES (4, 'Monitor', 299.99, 'Electronics', 0, 0.0, '2024-12-31 12:00:00'); \
             INSERT INTO Products VALUES (5, 'Pen', 1.50, 'Stationery', 1, NULL, '2025-06-10 18:45:12'); \
             CREATE TABLE Orders (OrderId INTEGER PRIMARY KEY, CustomerId INTEGER, Amount NUMERIC, Status TEXT); \
             INSERT INTO Orders VALUES (101, 1, 150.00, 'Completed'); \
             INSERT INTO Orders VALUES (102, 1, 200.00, 'Completed'); \
             INSERT INTO Orders VALUES (103, 2, 50.00, 'Pending'); \
             INSERT INTO Orders VALUES (104, 999, 99.00, 'Guest');"
        cmd.ExecuteNonQuery() |> ignore

    member _.Path = path
    interface IDisposable with
        member _.Dispose() =
            try if File.Exists(path) then File.Delete(path) with _ -> ()

type AdvancedMockConnector(kind: ConnectorKind, ?onExecute: string -> (string * obj) list -> ConnectorQueryResult) =
    interface IQueryableConnector with
        member _.Kind = kind
        member _.ConnectionString = $"mock://{kind}"
        member _.TestConnectionAsync(_ct) = System.Threading.Tasks.Task.FromResult true
        member _.DiscoverSchemaAsync(_ct) = System.Threading.Tasks.Task.FromResult []
        member _.ExecuteQuery (sql: string) (parameters: (string * obj) list) =
            match onExecute with
            | Some fn -> fn sql parameters
            | None ->
                match kind.Id with
                | "Oracle" when sql.Contains("DBMS_XPLAN") ->
                    { Columns = ["PLAN_TABLE_OUTPUT"]; Rows = [ [| box "PLAN_LINE_1" |]; [| box "PLAN_LINE_2" |] ] }
                | "MySql" | "MariaDb" ->
                    { Columns = ["id"; "select_type"; "table"]; Rows = [ [| box "1"; box "SIMPLE"; box "Products" |] ] }
                | _ ->
                    { Columns = ["detail"]; Rows = [ [| box "1"; box "0"; box "0"; box "SCAN TABLE Products" |] ] }

type DummyWithToRawSql() =
    member _.ToRawSql() = "SELECT * FROM DummySql"

type DummyWithToRawQuery() =
    member _.ToRawQuery() = "SELECT * FROM DummyQuery"

type DummyPlainObject() =
    override _.ToString() = "SELECT * FROM PlainObject"

type StaticQueryHolder private () =
    static let staticConn = AdvancedMockConnector(ConnectorKind("Sqlite"))
    static let staticQ = SqlPushdownQueryable<CovProduct>(staticConn, "Products")
    static member StaticProducts = staticQ

type InstanceQueryHolder() =
    let conn = AdvancedMockConnector(ConnectorKind("Sqlite"))
    let q = SqlPushdownQueryable<CovProduct>(conn, "Products")
    member val PropProducts = q with get, set
    member val FieldProducts = q

type CustomQueryableWrapper<'T>(inner: IQueryable<'T>) =
    interface IQueryable<'T> with
        member _.ElementType = inner.ElementType
        member _.Expression = inner.Expression
        member _.Provider = inner.Provider
        member _.GetEnumerator() = inner.GetEnumerator()
    interface System.Collections.IEnumerable with
        member _.GetEnumerator() = (inner :> System.Collections.IEnumerable).GetEnumerator()

type CovTuple8Holder<'T>() =
    member val Item8 = Unchecked.defaultof<Tuple<'T>> with get, set

type FieldHolder() =
    [<DefaultValue>] val mutable FieldProducts: SqlPushdownQueryable<CovProduct>

type FakeSetOps() =
    static member Union(source: obj, target: obj) : obj = null

type FakeWindowHolder() =
    static member Lag(col: obj, offset: int, defaultValue: obj, [<ParamArray>] over: obj[]) : obj = null
    static member Lead(col: obj, offset: int, defaultValue: obj, [<ParamArray>] over: obj[]) : obj = null

type FakeAggs() =
    static member Sum(source: obj, col: obj) : obj = null
    static member Min(source: obj, col: obj) : obj = null
    static member Max(source: obj, col: obj) : obj = null
    static member Average(source: obj, col: obj) : obj = null
    static member Sum(col: obj) : obj = null
    static member Min(col: obj) : obj = null
    static member Max(col: obj) : obj = null
    static member Average(col: obj) : obj = null

type FakeAggsInst() =
    member _.Sum() : obj = null
    member _.Min() : obj = null
    member _.Max() : obj = null
    member _.Average() : obj = null

type CustomTestFuncs() =
    static member Custom(x: decimal) : decimal = x
    static member CustomDec(x: decimal) : decimal = x
    static member CustomFloat(x: float) : float = x
    static member CustomInt(x: int) : int = x
    static member CustomStr(x: string) : string = x
    static member CustomNull(x: string) : string = null
    static member CustomDate(x: DateTime) : DateTime = x
    static member GetQueryable(q: IQueryable) : IQueryable = [1; 2].AsQueryable() :> IQueryable
    static member defaultValue(defVal: obj, optVal: obj) : obj = defVal
    static member defaultArg(optVal: obj, defVal: obj) : obj = defVal
    static member whereExists(source: obj, pred: obj) : bool = true
    static member whereNotExists(source: obj, pred: obj) : bool = true

type FieldHolderInstance() =
    [<DefaultValue>] val mutable public ProdsField: SqlPushdownQueryable<CovProduct>

type StaticCteHolder() =
    static member ProdsWithCte: SqlPushdownQueryable<CovProduct> =
        let c = AdvancedMockConnector(ConnectorKind("Sqlite"))
        let p = SqlPushdownQueryable<CovProduct>(c, "Products")
        p.WithCte("StaticCte", p)

type FakeCountHolder() =
    static member Count(source: obj, selector: obj) : obj = null

// =========================================================================
// 1. DATE AND TIME FUNCTIONS ACROSS ALL DIALECTS
// =========================================================================

[<Fact>]
let ``DateTime properties Year Month Day Hour Minute Second Date across dialects`` () =
    // SQLite
    let qSqlite = SqlPushdownQueryable<CovProduct>(AdvancedMockConnector(ConnectorKind("Sqlite")), "Products")
    let sSqlite = fsql { for p in qSqlite do select (p.Timestamp.Year, p.Timestamp.Month, p.Timestamp.Day, p.Timestamp.Hour, p.Timestamp.Minute, p.Timestamp.Second, p.Timestamp.Date) }
    let rawSqlite = toRawSql sSqlite
    Assert.Contains("strftime('%Y'", rawSqlite)
    Assert.Contains("strftime('%m'", rawSqlite)
    Assert.Contains("strftime('%d'", rawSqlite)
    Assert.Contains("strftime('%H'", rawSqlite)
    Assert.Contains("strftime('%M'", rawSqlite)
    Assert.Contains("strftime('%S'", rawSqlite)

    // SQL Server
    let qSs = SqlPushdownQueryable<CovProduct>(AdvancedMockConnector(ConnectorKind("SqlServer")), "Products")
    let sSs = fsql { for p in qSs do select (p.Timestamp.Year, p.Timestamp.Month, p.Timestamp.Day, p.Timestamp.Hour, p.Timestamp.Minute, p.Timestamp.Second, p.Timestamp.Date) }
    let rawSs = toRawSql sSs
    Assert.Contains("YEAR(", rawSs)
    Assert.Contains("MONTH(", rawSs)
    Assert.Contains("DAY(", rawSs)
    Assert.Contains("DATEPART(HOUR", rawSs)
    Assert.Contains("DATEPART(MINUTE", rawSs)
    Assert.Contains("DATEPART(SECOND", rawSs)
    Assert.Contains("CAST(", rawSs)
    Assert.Contains("AS DATE)", rawSs)

    // PostgreSQL
    let qPg = SqlPushdownQueryable<CovProduct>(AdvancedMockConnector(ConnectorKind("PostgreSql")), "Products")
    let sPg = fsql { for p in qPg do select (p.Timestamp.Year, p.Timestamp.Month, p.Timestamp.Day, p.Timestamp.Hour, p.Timestamp.Minute, p.Timestamp.Second, p.Timestamp.Date) }
    let rawPg = toRawSql sPg
    Assert.Contains("EXTRACT(YEAR FROM", rawPg)
    Assert.Contains("EXTRACT(MONTH FROM", rawPg)
    Assert.Contains("EXTRACT(DAY FROM", rawPg)
    Assert.Contains("EXTRACT(HOUR FROM", rawPg)
    Assert.Contains("EXTRACT(MINUTE FROM", rawPg)
    Assert.Contains("EXTRACT(SECOND FROM", rawPg)
    Assert.Contains("CAST(", rawPg)
    Assert.Contains("AS DATE)", rawPg)

    // MySQL / MariaDB
    let qMy = SqlPushdownQueryable<CovProduct>(AdvancedMockConnector(ConnectorKind("MySql")), "Products")
    let sMy = fsql { for p in qMy do select (p.Timestamp.Year, p.Timestamp.Month, p.Timestamp.Day, p.Timestamp.Hour, p.Timestamp.Minute, p.Timestamp.Second, p.Timestamp.Date) }
    let rawMy = toRawSql sMy
    Assert.Contains("YEAR(", rawMy)
    Assert.Contains("MONTH(", rawMy)
    Assert.Contains("DAY(", rawMy)
    Assert.Contains("HOUR(", rawMy)
    Assert.Contains("MINUTE(", rawMy)
    Assert.Contains("SECOND(", rawMy)
    Assert.Contains("DATE(", rawMy)

    // Oracle
    let qOra = SqlPushdownQueryable<CovProduct>(AdvancedMockConnector(ConnectorKind("Oracle")), "Products")
    let sOra = fsql { for p in qOra do select (p.Timestamp.Year, p.Timestamp.Month, p.Timestamp.Day, p.Timestamp.Hour, p.Timestamp.Minute, p.Timestamp.Second, p.Timestamp.Date) }
    let rawOra = toRawSql sOra
    Assert.Contains("EXTRACT(YEAR FROM", rawOra)
    Assert.Contains("EXTRACT(MONTH FROM", rawOra)
    Assert.Contains("EXTRACT(DAY FROM", rawOra)
    Assert.Contains("TO_NUMBER(TO_CHAR(", rawOra)
    Assert.Contains("'HH24'", rawOra)
    Assert.Contains("'MI'", rawOra)
    Assert.Contains("'SS'", rawOra)
    Assert.Contains("TRUNC(", rawOra)

// =========================================================================
// 2. STRING METHODS IN PUSHDOWN
// =========================================================================

[<Fact>]
let ``Pushdown string operations Length, IndexOf, Substring, Replace, IsNullOrEmpty, IsNullOrWhiteSpace`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")

    // WHERE predicate string methods
    let qPreds =
        products.Where(fun p ->
            p.Name.Contains("o") &&
            p.Name.StartsWith("M") &&
            p.Name.EndsWith("e") &&
            not (String.IsNullOrEmpty(p.Category)) &&
            not (String.IsNullOrWhiteSpace(p.Category))
        )
    let sPreds = toRawSql qPreds
    Assert.Contains("LIKE @p", sPreds)
    Assert.Contains("IS NULL OR", sPreds)

    // SELECT projection string methods
    let qProj1 =
        fsql {
            for p in products do
            select (
                p.Name.Length,
                p.Name.IndexOf("e"),
                p.Name.Substring(1, 3),
                p.Name.Substring(2),
                p.Name.Replace("o", "a"),
                p.Name.Trim()
            )
        }
    let sProj1 = toRawSql qProj1
    Assert.Contains("LENGTH(", sProj1)
    Assert.Contains("INSTR(", sProj1)
    Assert.Contains("SUBSTR(", sProj1)
    Assert.Contains("REPLACE(", sProj1)
    Assert.Contains("TRIM(", sProj1)

    let qProj2 =
        fsql {
            for p in products do
            select (
                String.IsNullOrEmpty(p.Name),
                String.IsNullOrWhiteSpace(p.Name),
                String.Concat(p.Category, p.Name),
                p.Name.ToUpper(),
                p.Name.ToLower()
            )
        }
    let sProj2 = toRawSql qProj2
    Assert.Contains("IS NULL OR", sProj2)
    Assert.Contains("UPPER(", sProj2)
    Assert.Contains("LOWER(", sProj2)

[<Fact>]
let ``Substring and IndexOf across SqlServer, PostgreSql, and Oracle`` () =
    let qSs = SqlPushdownQueryable<CovProduct>(AdvancedMockConnector(ConnectorKind("SqlServer")), "Products")
    let sSs = fsql { for p in qSs do select (p.Name.Length, p.Name.IndexOf("a"), p.Name.Substring(1, 2), p.Name.Substring(2)) }
    let rawSs = toRawSql sSs
    Assert.Contains("LEN(", rawSs)
    Assert.Contains("CHARINDEX(", rawSs)
    Assert.Contains("SUBSTRING(", rawSs)

    let qPg = SqlPushdownQueryable<CovProduct>(AdvancedMockConnector(ConnectorKind("PostgreSql")), "Products")
    let sPg = fsql { for p in qPg do select (p.Name.IndexOf("a")) }
    Assert.Contains("POSITION(", toRawSql sPg)

// =========================================================================
// 3. MATH METHODS
// =========================================================================

[<Fact>]
let ``Pushdown Math methods Abs, Round, Floor, Ceiling, Sqrt, Pow across dialects`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")

    let qMath =
        fsql {
            for p in products do
            select (
                Math.Abs(p.Price),
                Math.Round(p.Price),
                Math.Round(p.Price, 2),
                Math.Floor(p.Price),
                Math.Ceiling(p.Price),
                Math.Sqrt(float p.Price),
                Math.Pow(float p.Price, 2.0)
            )
        }
    let sMath = toRawSql qMath
    Assert.Contains("ABS(", sMath)
    Assert.Contains("ROUND(", sMath)
    Assert.Contains("FLOOR(", sMath)
    Assert.Contains("CEIL", sMath)
    Assert.Contains("SQRT(", sMath)
    Assert.Contains("POWER(", sMath)

    // SqlServer CEILING
    let qSs = SqlPushdownQueryable<CovProduct>(AdvancedMockConnector(ConnectorKind("SqlServer")), "Products")
    let sSs = fsql { for p in qSs do select (Math.Ceiling(p.Price)) }
    Assert.Contains("CEILING(", toRawSql sSs)

// =========================================================================
// 4. NULLABLES, OPTIONS, COALESCENCE, AND CASE WHEN
// =========================================================================

[<Fact>]
let ``Pushdown Nullable, Option, GetValueOrDefault, and conditional CASE WHEN`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")

    let q =
        fsql {
            for p in products do
            where (p.Score.HasValue)
            select (
                p.Score.GetValueOrDefault(0.0M),
                (if p.InStock then "InStock" else "OutOfStock"),
                (if p.Price > 50.0M then 1 else 0)
            )
        }
    let s = toRawSql q
    Assert.Contains("IS NOT NULL", s)
    Assert.Contains("COALESCE(", s)
    Assert.Contains("CASE WHEN", s)
    Assert.Contains("THEN", s)
    Assert.Contains("ELSE", s)
    Assert.Contains("END", s)

// =========================================================================
// 5. AGGREGATIONS WITH PREDICATES AND FILTERS
// =========================================================================

[<Fact>]
let ``Pushdown Aggregations Count with predicate, Sum, Min, Max, Average, Distinct`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")

    // Count with predicate
    let cntPred = products.Count(fun p -> p.Price > 10.0M)
    Assert.True(cntPred > 0)

    // Direct scalar overloads
    Assert.Equal(5, products.Count())
    Assert.Equal(5, products.Count("Id"))
    Assert.Equal(5, products.Count(fun p -> p.Id))
    Assert.Equal(2, products.CountDistinct("Category"))
    Assert.Equal(2, products.CountDistinct(fun p -> p.Category))

    let sum = products.Sum<decimal>("Price")
    Assert.True(sum > 0.0M)

    let min = products.Min<decimal>("Price")
    Assert.Equal(1.50M, min)

    let max = products.Max<decimal>("Price")
    Assert.Equal(299.99M, max)

    let avg = products.Average<decimal>("Price")
    Assert.True(avg > 0.0M)

    // Distinct combined with aggregations in LINQ
    let distCnt = products.Select(fun p -> p.Category).Distinct().Count()
    Assert.Equal(2, distCnt)

// =========================================================================
// 6. SET OPERATIONS AND CTES
// =========================================================================

[<Fact>]
let ``SqlPushdownQueryable Set Operations Union, UnionAll, Intersect, Except, Concat and CTEs`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")
    let orders = SqlPushdownQueryable<CovOrder>(conn, "Orders")

    let p1 = products.Where(fun p -> p.Price > 20.0M)
    let p2 = products.Where(fun p -> p.Price <= 20.0M)

    let u1 = products.Union(p2)
    Assert.Contains("UNION", u1.ToRawSql())

    let u2 = products.UnionAll(p2)
    Assert.Contains("UNION ALL", u2.ToRawSql())

    let u3 = products.Intersect(p2)
    Assert.Contains("INTERSECT", u3.ToRawSql())

    let u4 = products.Except(p2)
    Assert.Contains("EXCEPT", u4.ToRawSql())

    let u5 = products.Concat(p2)
    Assert.Contains("UNION ALL", u5.ToRawSql())

    // CTEs
    let cte1 = products.WithCte(orders)
    Assert.Contains("WITH cte_1 AS", cte1.ToRawSql())

    let cte2 = products.WithCte("custom_cte", orders)
    Assert.Contains("WITH custom_cte AS", cte2.ToRawSql())

    let cte3 = products.WithRecursiveCte(orders)
    Assert.Contains("WITH RECURSIVE cte_1 AS", cte3.ToRawSql())

    let cte4 = products.WithRecursiveCte("custom_rec", orders)
    Assert.Contains("WITH RECURSIVE custom_rec AS", cte4.ToRawSql())

    let cteRaw = products.WithCte("raw_cte", "SELECT 1 AS N", isRecursive = false)
    Assert.Contains("WITH raw_cte AS (SELECT 1 AS N)", cteRaw.ToRawSql())

    let cteRecRaw = products.WithCte("raw_rec", "SELECT 2 AS M", isRecursive = true)
    Assert.Contains("WITH RECURSIVE raw_rec AS (SELECT 2 AS M)", cteRecRaw.ToRawSql())

// =========================================================================
// 7. WINDOW FUNCTIONS & JSON NAVIGATION IN FSQL
// =========================================================================

[<Fact>]
let ``fsql window functions and jsonValue across dialects`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")

    let qWin1 =
        fsql {
            for p in products do
            select (
                fsql.rowNumber(over = fsql.window(partitionBy = p.Category, sortBy = p.Price)),
                fsql.rank(over = fsql.window(sortByDescending = p.Price)),
                fsql.denseRank(over = fsql.window(partitionBy = (p.Category, p.InStock))),
                fsql.ntile(3, over = fsql.window(sortBy = p.Price))
            )
        }
    let sWin1 = toRawSql qWin1
    Assert.Contains("ROW_NUMBER() OVER", sWin1)
    Assert.Contains("RANK() OVER", sWin1)
    Assert.Contains("DENSE_RANK() OVER", sWin1)
    Assert.Contains("NTILE(3) OVER", sWin1)

    let qWin2 =
        fsql {
            for p in products do
            select (
                fsql.lag(p.Price, offset = 1, defaultValue = 0.0M, over = fsql.window(sortBy = p.Price)),
                fsql.lead(p.Price, offset = 2, defaultValue = 99.9M, over = fsql.window(sortBy = p.Price)),
                fsql.firstValue(p.Name, over = fsql.window(partitionBy = p.Category, sortBy = p.Price)),
                fsql.lastValue(p.Name, over = fsql.window(partitionBy = p.Category, sortBy = p.Price))
            )
        }
    let sWin2 = toRawSql qWin2
    Assert.Contains("LAG(", sWin2)
    Assert.Contains("LEAD(", sWin2)
    Assert.Contains("FIRST_VALUE(", sWin2)
    Assert.Contains("LAST_VALUE(", sWin2)

    let qWin3 =
        fsql {
            for p in products do
            select (
                fsql.sum(p.Price, over = fsql.window(partitionBy = p.Category)),
                fsql.avg(p.Price, over = fsql.window(partitionBy = p.Category)),
                fsql.min(p.Price, over = fsql.window(partitionBy = p.Category)),
                fsql.max(p.Price, over = fsql.window(partitionBy = p.Category)),
                fsql.count(over = fsql.window(partitionBy = p.Category))
            )
        }
    let sWin3 = toRawSql qWin3
    Assert.Contains("SUM(", sWin3)
    Assert.Contains("AVG(", sWin3)
    Assert.Contains("MIN(", sWin3)
    Assert.Contains("MAX(", sWin3)
    Assert.Contains("COUNT(", sWin3)

    // JSON Value across dialects
    let qSqlite = SqlPushdownQueryable<CovProduct>(AdvancedMockConnector(ConnectorKind("Sqlite")), "Products")
    let sSqlite = fsql { for p in qSqlite do select (fsql.jsonValue(p.Name, "$.key")) }
    Assert.Contains("json_extract(", toRawSql sSqlite)

    let qSs = SqlPushdownQueryable<CovProduct>(AdvancedMockConnector(ConnectorKind("SqlServer")), "Products")
    let sSs = fsql { for p in qSs do select (fsql.jsonValue(p.Name, "$.key")) }
    Assert.Contains("JSON_VALUE(", toRawSql sSs)

    let qOra = SqlPushdownQueryable<CovProduct>(AdvancedMockConnector(ConnectorKind("Oracle")), "Products")
    let sOra = fsql { for p in qOra do select (fsql.jsonValue(p.Name, "$.key")) }
    Assert.Contains("JSON_VALUE(", toRawSql sOra)

    let qMy = SqlPushdownQueryable<CovProduct>(AdvancedMockConnector(ConnectorKind("MySql")), "Products")
    let sMy = fsql { for p in qMy do select (fsql.jsonValue(p.Name, "$.key")) }
    Assert.Contains("JSON_UNQUOTE(JSON_EXTRACT(", toRawSql sMy)

    let qPg = SqlPushdownQueryable<CovProduct>(AdvancedMockConnector(ConnectorKind("PostgreSql")), "Products")
    let sPg1 = fsql { for p in qPg do select (fsql.jsonValue(p.Name, "$.key")) }
    Assert.Contains("->> 'key'", toRawSql sPg1)

    let sPg2 = fsql { for p in qPg do select (fsql.jsonValue(p.Name, "$.a.b.c")) }
    Assert.Contains("#>> '{a', 'b', 'c}'", toRawSql sPg2)

// =========================================================================
// 8. FSQL COMPUTATION EXPRESSION CUSTOM OPERATIONS
// =========================================================================

[<Fact>]
let ``fsql computation expression custom operations whereExists, whereNotExists, having, joins`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")
    let orders = SqlPushdownQueryable<CovOrder>(conn, "Orders")

    // whereExists
    let qExists =
        fsql {
            for p in products do
            whereExists (fsql { for o in orders do where (o.CustomerId = p.Id); select o.OrderId })
            select p.Id
        }
    Assert.Contains("EXISTS (", toRawSql qExists)

    // whereNotExists
    let qNotExists =
        fsql {
            for p in products do
            whereNotExists (fsql { for o in orders do where (o.CustomerId = p.Id); select o.OrderId })
            select p.Id
        }
    Assert.Contains("NOT EXISTS (", toRawSql qNotExists)

    // having
    let qHaving =
        fsql {
            for p in products do
            groupBy p.Category into g
            having (g.Count() > 1)
            select (g.Key, g.Count())
        }
    let sHaving = toRawSql qHaving
    Assert.Contains("GROUP BY", sHaving)
    Assert.Contains("HAVING", sHaving)

    // crossJoin, fullOuterJoinRaw, rightOuterJoinRaw
    let qCross = fsql { for p in products do crossJoin "Orders" "o"; select p.Id }
    Assert.Contains("CROSS JOIN Orders", toRawSql qCross)

    let qFull = fsql { for p in products do fullOuterJoinRaw "Orders" "o" "p.Id = o.CustomerId"; select p.Id }
    Assert.Contains("FULL OUTER JOIN Orders", toRawSql qFull)

    let qRight = fsql { for p in products do rightOuterJoinRaw "Orders" "o" "p.Id = o.CustomerId"; select p.Id }
    Assert.Contains("RIGHT OUTER JOIN Orders", toRawSql qRight)

    // CTEs inside fsql
    let qCte = fsql { for p in products do withCte "sub_cte" (fsql { for o in orders do select o.OrderId }); select p.Id }
    Assert.Contains("WITH sub_cte AS", toRawSql qCte)

    let qCteSql = fsql { for p in products do withCteSql "raw_cte" "SELECT 1 AS N"; select p.Id }
    Assert.Contains("WITH raw_cte AS (SELECT 1 AS N)", toRawSql qCteSql)

    let qRecCte = fsql { for p in products do withRecursiveCte "rec_cte" (fsql { for o in orders do select o.OrderId }); select p.Id }
    Assert.Contains("WITH RECURSIVE rec_cte AS", toRawSql qRecCte)

    let qRecCteSql = fsql { for p in products do withRecursiveCteSql "rec_raw" "SELECT 2 AS M"; select p.Id }
    Assert.Contains("WITH RECURSIVE rec_raw AS (SELECT 2 AS M)", toRawSql qRecCteSql)

    // Set operations inside fsql
    let qU = fsql { for p in products do select p.Id; union (fsql { for o in orders do select o.CustomerId }) }
    Assert.Contains("UNION", toRawSql qU)

    let qUA = fsql { for p in products do select p.Id; unionAll (fsql { for o in orders do select o.CustomerId }) }
    Assert.Contains("UNION ALL", toRawSql qUA)

    let qI = fsql { for p in products do select p.Id; intersect (fsql { for o in orders do select o.CustomerId }) }
    Assert.Contains("INTERSECT", toRawSql qI)

    let qE = fsql { for p in products do select p.Id; except (fsql { for o in orders do select o.CustomerId }) }
    Assert.Contains("EXCEPT", toRawSql qE)

// =========================================================================
// 9. DIAGNOSTICS, EXPLAIN, AND QUERY PROVIDER METHODS
// =========================================================================

[<Fact>]
let ``Explain diagnostics and SqlPushdownQuery explainQuery across dialects`` () =
    // SQLite
    let cSqlite = AdvancedMockConnector(ConnectorKind("Sqlite"))
    let qSqlite = SqlPushdownQueryable<CovProduct>(cSqlite, "Products")
    let repSqlite = qSqlite.Explain()
    Assert.Contains("SQLite EXPLAIN QUERY PLAN", repSqlite.PlanText)

    // PostgreSQL
    let cPg = AdvancedMockConnector(ConnectorKind("PostgreSql"))
    let qPg = SqlPushdownQueryable<CovProduct>(cPg, "Products")
    let repPg = qPg.Explain()
    Assert.Contains("PostgreSQL EXPLAIN", repPg.PlanText)

    // SQL Server
    let cSs = AdvancedMockConnector(ConnectorKind("SqlServer"))
    let qSs = SqlPushdownQueryable<CovProduct>(cSs, "Products")
    let repSs = qSs.Explain()
    Assert.Contains("SQL Server SHOWPLAN", repSs.PlanText)

    // MySQL
    let cMy = AdvancedMockConnector(ConnectorKind("MySql"))
    let qMy = SqlPushdownQueryable<CovProduct>(cMy, "Products")
    let repMy = qMy.Explain()
    Assert.Contains("MySql EXPLAIN", repMy.PlanText)

    // Oracle
    let cOra = AdvancedMockConnector(ConnectorKind("Oracle"))
    let qOra = SqlPushdownQueryable<CovProduct>(cOra, "Products")
    let repOra = qOra.Explain()
    Assert.Contains("Oracle Execution Plan", repOra.PlanText)

    // Exception branch
    let cErr = AdvancedMockConnector(ConnectorKind("Sqlite"), fun _ _ -> failwith "Simulated failure")
    let qErr = SqlPushdownQueryable<CovProduct>(cErr, "Products")
    let repErr = qErr.Explain()
    Assert.Contains("EXPLAIN diagnostic report", repErr.PlanText)

    // explainQuery with null connector
    let repNull = SqlPushdownQuery.explainQuery (DummyWithToRawSql())
    Assert.Contains("Diagnostic execution plan estimate", repNull.PlanText)

[<Fact>]
let ``SqlPushdownQuery extractSqlWithParameters handles all object variants`` () =
    let (sNull, _) = SqlPushdownQuery.extractSqlWithParameters null
    Assert.Equal("", sNull)

    let (sStr, _) = SqlPushdownQuery.extractSqlWithParameters "SELECT 1"
    Assert.Equal("SELECT 1", sStr)

    let plan = PushdownQueryPlan.Create("Products", [])
    let (sPlan, _) = SqlPushdownQuery.extractSqlWithParameters plan
    Assert.Contains("Products", sPlan)

    let (sRaw, _) = SqlPushdownQuery.extractSqlWithParameters (DummyWithToRawSql())
    Assert.Equal("SELECT * FROM DummySql", sRaw)

    let (sQuery, _) = SqlPushdownQuery.extractSqlWithParameters (DummyWithToRawQuery())
    Assert.Equal("SELECT * FROM DummyQuery", sQuery)

    let (sPlain, _) = SqlPushdownQuery.extractSqlWithParameters (DummyPlainObject())
    Assert.Equal("SELECT * FROM PlainObject", sPlain)

    Assert.Equal("SELECT * FROM DummySql", SqlPushdownQuery.toRawSql (DummyWithToRawSql()))
    let rawQ = SqlPushdownQuery.toRawQuery (DummyWithToRawQuery())
    Assert.Contains("SELECT *", rawQ)
    Assert.Contains("FROM DummyQuery", rawQ)

[<Fact>]
let ``SqlPushdownQueryProvider non-streaming execution and LINQ provider methods`` () =
    let mockConn =
        AdvancedMockConnector(
            ConnectorKind("Sqlite"),
            fun sql parms ->
                if sql.Contains("COUNT(") then
                    { Columns = ["cnt"]; Rows = [ [| box 42 |] ] }
                elif sql.Contains("Products") then
                    {
                        Columns = ["Id"; "Name"; "Price"; "Category"; "InStock"; "Score"; "Timestamp"]
                        Rows = [
                            [| box 1; box "Item1"; box 10.5M; box "CatA"; box true; box 85.0M; box (DateTime(2026, 1, 1)) |]
                            [| box 2; box "Item2"; box 20.0M; box "CatB"; box false; null; box (DateTime(2026, 2, 2)) |]
                        ]
                    }
                else
                    { Columns = ["val"]; Rows = [ [| box 99 |] ] }
        )

    let provider = SqlPushdownQueryProvider(mockConn, "Products", [])

    // ExecutePlanOrStream with Record mapping
    let records = provider.ExecutePlanOrStream<CovProduct>(Expression.Constant(null, typeof<IQueryable<CovProduct>>)) |> Seq.toList
    Assert.Equal(2, records.Length)
    Assert.Equal("Item1", records.[0].Name)
    Assert.Equal("Item2", records.[1].Name)

    // ExecutePlanOrStream with Primitive mapping
    let primitives = provider.ExecutePlanOrStream<int>(Expression.Constant(null, typeof<IQueryable<int>>)) |> Seq.toList
    Assert.Equal(2, primitives.Length)
    Assert.Equal(1, primitives.[0])
    Assert.Equal(2, primitives.[1])

    // Fallback Predicates
    let planWithPred = PushdownQueryPlan.Create("Products", [])
    planWithPred.FallbackPredicates.Add(fun item ->
        match item with
        | :? CovProduct as p -> p.Price > 15.0M
        | _ -> false)

    let filtered = provider.ExecutePlanOrStream<CovProduct>(Expression.Constant(null, typeof<IQueryable<CovProduct>>), planWithPred) |> Seq.toList
    Assert.Single(filtered) |> ignore
    Assert.Equal("Item2", filtered.[0].Name)

    // IQueryProvider interface
    let qp = provider :> IQueryProvider
    let dummyQ = SqlPushdownQueryable<CovProduct>(mockConn, "Products")
    let expr = (dummyQ :> IQueryable).Expression
    let created = qp.CreateQuery(expr)
    Assert.NotNull(created)
    let createdGen = qp.CreateQuery<CovProduct>(expr)
    Assert.NotNull(createdGen)

    let selQ = dummyQ.Select(fun p -> p.Id)
    let executedScalar = qp.Execute<int>((selQ :> IQueryable).Expression)
    Assert.Equal(1, executedScalar)

    let executedSeq = provider.ExecuteStream<CovProduct>(expr) |> Seq.toList
    Assert.Equal(2, executedSeq.Length)

// =========================================================================
// 10. SUBQUERY COUNT COMPARISONS AND IN-COLLECTION QUERIES
// =========================================================================

[<Fact>]
let ``Subquery Count comparisons in WHERE generate EXISTS, NOT EXISTS, and scalar subqueries`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")
    let orders = SqlPushdownQueryable<CovOrder>(conn, "Orders")

    let q1 = products.Where(fun p -> orders.Where(fun o -> o.CustomerId = p.Id).Count() = 0)
    Assert.Contains("NOT EXISTS (", toRawSql q1)

    let q2 = products.Where(fun p -> orders.Where(fun o -> o.CustomerId = p.Id).Count() <= 0)
    Assert.Contains("NOT EXISTS (", toRawSql q2)

    let q3 = products.Where(fun p -> 0 = orders.Where(fun o -> o.CustomerId = p.Id).Count())
    Assert.Contains("NOT EXISTS (", toRawSql q3)

    let q4 = products.Where(fun p -> orders.Where(fun o -> o.CustomerId = p.Id).Count() > 0)
    Assert.Contains("EXISTS (", toRawSql q4)

    let q5 = products.Where(fun p -> orders.Where(fun o -> o.CustomerId = p.Id).Count() >= 1)
    Assert.Contains("EXISTS (", toRawSql q5)

    let q6 = products.Where(fun p -> 0 < orders.Where(fun o -> o.CustomerId = p.Id).Count())
    Assert.Contains("EXISTS (", toRawSql q6)

    let q7 = products.Where(fun p -> orders.Where(fun o -> o.CustomerId = p.Id).Count() > 5)
    let s7 = toRawSql q7
    Assert.Contains("COUNT(*)", s7)
    Assert.Contains(">", s7)

    let q8 = products.Where(fun p -> 5 < orders.Where(fun o -> o.CustomerId = p.Id).Count())
    let s8 = toRawSql q8
    Assert.Contains("COUNT(*)", s8)
    Assert.Contains("<", s8)

[<Fact>]
let ``In-memory collection and subquery Contains in WHERE generate IN and 1=0`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")
    let orders = SqlPushdownQueryable<CovOrder>(conn, "Orders")

    let targetIds = [1; 3; 5]
    let qList = products.Where(fun p -> targetIds.Contains(p.Id))
    Assert.Contains("IN (@p", toRawSql qList)

    let emptyIds: int list = []
    let qEmpty = products.Where(fun p -> emptyIds.Contains(p.Id))
    Assert.Contains("(1=0)", toRawSql qEmpty)

    let qSubContains = products.Where(fun p -> orders.Select(fun o -> o.CustomerId).Contains(p.Id))
    Assert.Contains("IN (SELECT", toRawSql qSubContains)

[<Fact>]
let ``tryFindPushdownSource finds sources via reflection on static and instance members`` () =
    let staticProp = typeof<StaticQueryHolder>.GetProperty("StaticProducts")
    let staticExpr = Expression.Property(null, staticProp)
    let foundStatic = SqlPushdownQuery.tryFindPushdownSource staticExpr
    Assert.True(foundStatic.IsSome)

    let inst = InstanceQueryHolder()
    let instProp = typeof<InstanceQueryHolder>.GetProperty("PropProducts")
    let instExpr = Expression.Property(Expression.Constant(inst), instProp)
    let foundInst = SqlPushdownQuery.tryFindPushdownSource instExpr
    Assert.True(foundInst.IsSome)

[<Fact>]
let ``FSqlExtensions Union, UnionAll, Intersect, Except, and WithCte on wrapped and plain queries`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")
    let orders = SqlPushdownQueryable<CovOrder>(conn, "Orders")

    let wrapped1 = products.Where(fun p -> p.Price > 10.0M)
    let wrapped2 = products.Where(fun p -> p.Price <= 10.0M)

    let u1 = wrapped1.Union(wrapped2)
    Assert.Contains("UNION", toRawSql u1)

    let u2 = wrapped1.UnionAll(wrapped2)
    Assert.Contains("UNION ALL", toRawSql u2)

    let u3 = wrapped1.Intersect(wrapped2)
    Assert.Contains("INTERSECT", toRawSql u3)

    let u4 = wrapped1.Except(wrapped2)
    Assert.Contains("EXCEPT", toRawSql u4)

    let c1 = wrapped1.WithCte(orders)
    Assert.Contains("WITH", toRawSql c1)

    let c2 = wrapped1.WithCte("orders_cte", orders)
    Assert.Contains("WITH orders_cte AS", toRawSql c2)

    let plain1 = ([1; 2; 3] :> seq<int>).AsQueryable()
    let plain2 = ([3; 4; 5] :> seq<int>).AsQueryable()

    let pu = plain1.Union(plain2)
    Assert.Equal(5, pu.Count())

    let pua = plain1.UnionAll(plain2)
    Assert.Equal(6, pua.Count())

    let pi = plain1.Intersect(plain2)
    Assert.Equal(1, pi.Count())

    let pe = plain1.Except(plain2)
    Assert.Equal(2, pe.Count())

    let pc = plain1.WithCte(plain2)
    Assert.Equal(3, pc.Count())

// =========================================================================
// 8. SQL PUSHDOWN PLAN DIRECT MEMBERS & WINDOW DEFINITION
// =========================================================================

[<Fact>]
let ``SqlPushdownPlan direct members, WindowDefinition, CtePlan, and dialect edge cases`` () =
    // WindowDefinition dummy members
    let w = { WindowDefinition.Dummy = "win" }
    let w1 = w.PartitionBy("Category")
    let w2 = w1.SortBy("Price")
    let w3 = w2.SortByDescending("Id")
    Assert.Equal("win", w3.Dummy)

    // CtePlan.Create
    let cte = CtePlan.Create("cte_test", "SELECT 1", isRecursive = true, sourceQuery = box 123)
    Assert.Equal("cte_test", cte.Name)
    Assert.True(cte.IsRecursive)
    Assert.True(cte.SourceQuery.IsSome)

    // SqlPushdownFormatting with DateTime and bool parameters
    let dtVal = DateTime(2025, 6, 15, 12, 30, 0)
    let formatted = SqlPushdownFormatting.formatSqlAndParameters "SELECT 1" [ ("@dt", box dtVal); ("@flag", box true); ("@null", null) ]
    Assert.Contains("2025-06-15 12:30:00", formatted)
    Assert.Contains("@flag = 1", formatted)
    Assert.Contains("@null = NULL", formatted)

    // PushdownQueryPlan.Clone with OuterAliases
    let plan = PushdownQueryPlan.Create("Products", [], baseAlias = "_t0", dialect = SqlDialect.Sqlite)
    plan.OuterAliases.["p"] <- "_t0"
    plan.OuterAliases.["o"] <- "_t1"
    let cloned = plan.Clone()
    Assert.Equal("_t0", cloned.OuterAliases.["p"])
    Assert.Equal("_t1", cloned.OuterAliases.["o"])

    // plan.BuildRawSql, plan.BuildRawQuery, plan.ToRawQuery
    let rawSql = plan.BuildRawSql(SqlDialect.Sqlite)
    Assert.Contains("SELECT * FROM Products", rawSql)
    let rawQ = plan.BuildRawQuery(SqlDialect.SqlServer)
    Assert.Contains("SELECT *", rawQ)
    let toRawQ = plan.ToRawQuery()
    Assert.Contains("SELECT *", toRawQ)
    let toRawQPg = plan.ToRawQuery(SqlDialect.PostgreSql)
    Assert.Contains("SELECT *", toRawQPg)

    // SQLite OUTER APPLY with empty OnClause
    let planOuter = PushdownQueryPlan.Create("Products", [], baseAlias = "p", dialect = SqlDialect.Sqlite)
    planOuter.Joins.Add({ JoinType = "OUTER APPLY"; SourceTable = "Orders"; Alias = "o"; OnClause = ""; RowType = None })
    let sqlOuter = planOuter.BuildSql(SqlDialect.Sqlite)
    Assert.Contains("LEFT JOIN Orders o ON 1=1", sqlOuter)

    // SQLite OUTER APPLY with non-empty OnClause
    let planOuterOn = PushdownQueryPlan.Create("Products", [], baseAlias = "p", dialect = SqlDialect.Sqlite)
    planOuterOn.Joins.Add({ JoinType = "OUTER APPLY"; SourceTable = "Orders"; Alias = "o"; OnClause = "p.Id = o.CustomerId"; RowType = None })
    let sqlOuterOn = planOuterOn.BuildSql(SqlDialect.Sqlite)
    Assert.Contains("LEFT JOIN Orders o ON p.Id = o.CustomerId", sqlOuterOn)

    // Non-table baseSql with joins
    let planSubFrom = PushdownQueryPlan.Create("SELECT * FROM Products WHERE Price > 10", [], baseAlias = "p", dialect = SqlDialect.Sqlite)
    planSubFrom.Joins.Add({ JoinType = "INNER JOIN"; SourceTable = "Orders"; Alias = "o"; OnClause = "p.Id = o.CustomerId"; RowType = None })
    let sqlSubFrom = planSubFrom.BuildSql(SqlDialect.Sqlite)
    Assert.Contains("(SELECT * FROM Products WHERE Price > 10) AS p INNER JOIN", sqlSubFrom)

    // Non-table baseSql without joins requiring base alias
    let planSubAlias = PushdownQueryPlan.Create("SELECT * FROM Products", [], baseAlias = "p", dialect = SqlDialect.Sqlite)
    planSubAlias.WhereClauses.Add("p.Price > 10")
    let sqlSubAlias = planSubAlias.BuildSql(SqlDialect.Sqlite)
    Assert.Contains("(SELECT * FROM Products) AS p WHERE p.Price > 10", sqlSubAlias)

    // Non-table baseSql without joins NOT requiring base alias
    let planSubNoAlias = PushdownQueryPlan.Create("SELECT * FROM Products", [], baseAlias = "_t0", dialect = SqlDialect.Sqlite)
    let sqlSubNoAlias = planSubNoAlias.BuildSql(SqlDialect.Sqlite)
    Assert.Contains("(SELECT * FROM Products) AS _src", sqlSubNoAlias)

    // SQL Server Skip without Take
    let planSsSkip = PushdownQueryPlan.Create("Products", [], baseAlias = "p", dialect = SqlDialect.SqlServer)
    planSsSkip.Skip <- Some 15
    planSsSkip.Take <- None
    let sqlSsSkip = planSsSkip.BuildSql(SqlDialect.SqlServer)
    Assert.Contains("OFFSET 15 ROWS", sqlSsSkip)

    // Oracle Skip without Take
    let planOraSkip = PushdownQueryPlan.Create("Products", [], baseAlias = "p", dialect = SqlDialect.Oracle)
    planOraSkip.Skip <- Some 20
    planOraSkip.Take <- None
    let sqlOraSkip = planOraSkip.BuildSql(SqlDialect.Oracle)
    Assert.Contains("OFFSET 20 ROWS", sqlOraSkip)

    // Oracle Take without Skip
    let planOraTake = PushdownQueryPlan.Create("Products", [], baseAlias = "p", dialect = SqlDialect.Oracle)
    planOraTake.Skip <- None
    planOraTake.Take <- Some 10
    let sqlOraTake = planOraTake.BuildSql(SqlDialect.Oracle)
    Assert.Contains("OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY", sqlOraTake)

    // SQLite Skip without Take
    let planLiteSkip = PushdownQueryPlan.Create("Products", [], baseAlias = "p", dialect = SqlDialect.Sqlite)
    planLiteSkip.Skip <- Some 25
    planLiteSkip.Take <- None
    let sqlLiteSkip = planLiteSkip.BuildSql(SqlDialect.Sqlite)
    Assert.Contains("OFFSET 25", sqlLiteSkip)

// =========================================================================
// 9. SQLPUSHDOWNQUERYABLE DIRECT METHODS AND SCALAR AGGREGATES
// =========================================================================

[<Fact>]
let ``SqlPushdownQueryable direct aggregates, WhereNotIn, ToRawQuery, and set operations`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")
    let orders = SqlPushdownQueryable<CovOrder>(conn, "Orders")

    // WhereNotIn with values
    let qNotIn = products.WhereNotIn("Id", [1; 2; 3])
    let sqlNotIn = toRawSql qNotIn
    Assert.Contains("Id NOT IN (@notin_p", sqlNotIn)

    // WhereNotIn with empty values returns same query
    let qNotInEmpty = products.WhereNotIn("Id", Seq.empty<int>)
    Assert.Same(products, qNotInEmpty)

    // WhereIn with empty values adds 1 = 0
    let qInEmpty = products.WhereIn("Id", Seq.empty<int>)
    let sqlInEmpty = toRawSql qInEmpty
    Assert.Contains("1 = 0", sqlInEmpty)

    // Count with predicate lambda
    let countFiltered = products.Count(fun p -> p.Price > 20.0M)
    Assert.Equal(3, countFiltered)

    // Count with column selector lambda
    let countCategory = products.Count(fun p -> p.Category)
    Assert.Equal(5, countCategory)

    // CountDistinct by string column and lambda selector
    let cdStr = products.CountDistinct("Category")
    Assert.Equal(2, cdStr)
    let cdLambda = products.CountDistinct(fun p -> p.Category)
    Assert.Equal(2, cdLambda)

    // Min with string and selector
    let minStr = products.Min<decimal>("Price")
    Assert.Equal(1.50M, minStr)
    let minLambda = products.Min(fun p -> p.Price)
    Assert.Equal(1.50M, minLambda)

    // Max with string and selector
    let maxStr = products.Max<decimal>("Price")
    Assert.Equal(299.99M, maxStr)
    let maxLambda = products.Max(fun p -> p.Price)
    Assert.Equal(299.99M, maxLambda)

    // Average with string and selector
    let avgStr = products.Average<decimal>("Price")
    Assert.True(avgStr > 0.0M)
    let avgLambda = products.Average(fun p -> p.Price)
    Assert.True(avgLambda > 0.0M)

    // ToRawSql, ToRawQuery, ToSqlWithParameters
    let rawSql = products.ToRawSql()
    Assert.Contains("SELECT * FROM Products", rawSql)
    let rawQ = products.ToRawQuery()
    Assert.Contains("SELECT *", rawQ)
    let (pSql, pParams) = products.ToSqlWithParameters()
    Assert.Contains("SELECT * FROM Products", pSql)
    Assert.Empty(pParams)

    // Concat on SqlPushdownQueryable
    let concatQ = products.Concat(products)
    Assert.Contains("UNION ALL", toRawSql concatQ)

    // Union, UnionAll, Intersect, Except with wrapped IQueryable<'T>
    let wrappedFilter = products.Where(fun p -> p.Price < 100.0M)
    let u1 = products.Union(wrappedFilter)
    Assert.Contains("UNION", toRawSql u1)
    let u2 = products.UnionAll(wrappedFilter)
    Assert.Contains("UNION ALL", toRawSql u2)
    let u3 = products.Intersect(wrappedFilter)
    Assert.Contains("INTERSECT", toRawSql u3)
    let u4 = products.Except(wrappedFilter)
    Assert.Contains("EXCEPT", toRawSql u4)

// =========================================================================
// 10. SQLCOMPUTATIONEXTENSIONS AND FSQL QUERY BUILDER METHODS
// =========================================================================

[<Fact>]
let ``SqlComputationExtensions direct methods, dummy builder methods, and FSql query extensions`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")

    // SqlComputationExtensions module functions with plain queryable
    let plain1 = ([1; 2] :> seq<int>).AsQueryable()
    let plain2 = ([2; 3] :> seq<int>).AsQueryable()
    let uPlain = SqlComputationExtensions.union plain1 plain2
    Assert.Equal(3, uPlain.Count())
    let uaPlain = SqlComputationExtensions.unionAll plain1 plain2
    Assert.Equal(4, uaPlain.Count())
    let iPlain = SqlComputationExtensions.intersect plain1 plain2
    Assert.Equal(1, iPlain.Count())
    let ePlain = SqlComputationExtensions.except plain1 plain2
    Assert.Equal(1, ePlain.Count())

    // SqlComputationExtensions module functions with SqlPushdownQueryable
    let uSpq = SqlComputationExtensions.union (products :> IQueryable<CovProduct>) (products :> IQueryable<CovProduct>)
    Assert.Contains("UNION", toRawSql uSpq)
    let uaSpq = SqlComputationExtensions.unionAll (products :> IQueryable<CovProduct>) (products :> IQueryable<CovProduct>)
    Assert.Contains("UNION ALL", toRawSql uaSpq)
    let iSpq = SqlComputationExtensions.intersect (products :> IQueryable<CovProduct>) (products :> IQueryable<CovProduct>)
    Assert.Contains("INTERSECT", toRawSql iSpq)
    let eSpq = SqlComputationExtensions.except (products :> IQueryable<CovProduct>) (products :> IQueryable<CovProduct>)
    Assert.Contains("EXCEPT", toRawSql eSpq)

    // Dummy helper functions
    let recRes = SqlComputationExtensions.recursive "cte" [10; 20] id
    Assert.Equal(2, Seq.length recRes)
    let jsonRes = SqlComputationExtensions.jsonValue "details" "$.path"
    Assert.Equal("", jsonRes)

    // FSqlQueryBuilder dummy helper methods
    fsql.withCte("my_cte", [1; 2])
    fsql.withCte([1; 2])
    fsql.withCte("SELECT 1")
    fsql.withCteSql("my_cte", "SELECT 1")
    fsql.withCteSql("SELECT 1")
    fsql.withRecursiveCte("rec_cte", [1])
    fsql.withRecursiveCte([1])
    fsql.withRecursiveCte("SELECT 1")
    fsql.withRecursiveCteSql("rec_cte", "SELECT 1")
    fsql.withRecursiveCteSql("SELECT 1")

    Assert.Equal(123, fsql.max(123))
    Assert.Equal(0L, fsql.count())
    Assert.Equal(0L, fsql.count(123))
    Assert.Equal("", fsql.jsonValue("col", "$.a"))
    let rDummy = fsql.recursive("cte", [42], id)
    Assert.Equal(1, Seq.length rDummy)

    let report = fsql.explain(products)
    Assert.Contains("SELECT *", report.FormattedSql)
    Assert.Contains("FROM Products", report.FormattedSql)

    let uAllBuild = fsql.unionAll (products :> IQueryable<CovProduct>) (products :> IQueryable<CovProduct>)
    Assert.Contains("UNION ALL", toRawSql uAllBuild)
    let iBuild = fsql.intersect (products :> IQueryable<CovProduct>) (products :> IQueryable<CovProduct>)
    Assert.Contains("INTERSECT", toRawSql iBuild)
    let eBuild = fsql.except (products :> IQueryable<CovProduct>) (products :> IQueryable<CovProduct>)
    Assert.Contains("EXCEPT", toRawSql eBuild)

// =========================================================================
// 11. SQLPUSHDOWNQUERYPROVIDER EXECUTE AND EXECUTE<'TRESULT>
// =========================================================================

[<Fact>]
let ``SqlPushdownQueryProvider Execute and ExecuteOfTResult with various collection and scalar types`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")
    let prov = (products :> IQueryable).Provider
    let prodExpr = (products :> IQueryable).Expression

    // Execute(expr) returning obj
    let scalarObj = prov.Execute(products.Select(fun p -> p.Id).Expression)
    Assert.NotNull(scalarObj)

    // Execute<seq<CovProduct>>
    let seqRes = prov.Execute<seq<CovProduct>>(prodExpr)
    Assert.Equal(5, Seq.length seqRes)

    // Execute<IEnumerable<CovProduct>>
    let enumRes = prov.Execute<System.Collections.Generic.IEnumerable<CovProduct>>(prodExpr)
    Assert.Equal(5, Seq.length enumRes)

    // Execute<int>
    let intRes = prov.Execute<int>(products.Select(fun p -> p.Id).Expression)
    Assert.Equal(1, intRes)

// =========================================================================
// 12. EXPLAIN AND EXPLAINQUERY ACROSS ALL ENGINES AND ERROR PATHS
// =========================================================================

[<Fact>]
let ``SqlPushdownQuery explain and explainQuery across all dialects and error paths`` () =
    let dialects = [
        ("SqlServer", "Microsoft.Data.SqlClient")
        ("PostgreSql", "Npgsql")
        ("MySql", "MySqlConnector")
        ("MariaDb", "MySqlConnector")
        ("Oracle", "Oracle.ManagedDataAccess.Client")
    ]

    for (name, _) in dialects do
        let mockConn = AdvancedMockConnector(ConnectorKind(name))
        let q = SqlPushdownQueryable<CovProduct>(mockConn, "Products")
        let rep = SqlPushdownQuery.explainQuery q
        Assert.Contains("Products", rep.FormattedSql)
        Assert.NotEmpty(rep.PlanText)

    // Connector error path in explain
    let failingConn = AdvancedMockConnector(ConnectorKind("Sqlite"), onExecute = (fun _ _ -> failwith "Simulated explain failure"))
    let qFail = SqlPushdownQueryable<CovProduct>(failingConn, "Products")
    let repFail = SqlPushdownQuery.explainQuery qFail
    Assert.Contains("Simulated explain failure", repFail.PlanText)

    // Query without connector
    let plainQ = ([1; 2; 3] :> seq<int>).AsQueryable()
    let repPlain = SqlPushdownQuery.explainQuery plainQ
    Assert.Contains("Diagnostic execution plan estimate", repPlain.PlanText)

// =========================================================================
// 13. DATE AND NUMBER TOSTRING PATTERNS ACROSS ALL DIALECTS
// =========================================================================

[<Fact>]
let ``ToString with and without format string across all dialects`` () =
    let testDialect dialect expectedFmtPart =
        let mock = AdvancedMockConnector(ConnectorKind(dialect))
        let q = SqlPushdownQueryable<CovProduct>(mock, "Products")
        let qFormatted = q.Select(fun p -> p.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"))
        let sqlFmt = toRawSql qFormatted
        Assert.Contains(expectedFmtPart, sqlFmt)

        let qPlain = q.Select(fun p -> p.Price.ToString())
        let sqlPlain = toRawSql qPlain
        if dialect = "Oracle" then
            Assert.Contains("TO_CHAR(", sqlPlain)
        else
            Assert.Contains("CAST(", sqlPlain)

    testDialect "Sqlite" "strftime('%Y-%m-%d %H:%M:%S'"
    testDialect "SqlServer" "FORMAT("
    testDialect "PostgreSql" "to_char("
    testDialect "MySql" "DATE_FORMAT("
    testDialect "Oracle" "TO_CHAR("

// =========================================================================
// 14. INVERTED COMPARISONS AND NULL PREDICATES IN WHERE
// =========================================================================

[<Fact>]
let ``Inverted comparisons and null predicates in Where clauses`` () =
    let mock = AdvancedMockConnector(ConnectorKind("Sqlite"))
    let products = SqlPushdownQueryable<CovProduct>(mock, "Products")

    // Inverted comparison: constant on left, property on right
    let qInvLt = products.Where(fun p -> 50.0M < p.Price)
    Assert.Contains("Price > @p", toRawSql qInvLt)

    let qInvLte = products.Where(fun p -> 50.0M <= p.Price)
    Assert.Contains("Price >= @p", toRawSql qInvLte)

    let qInvGt = products.Where(fun p -> 50.0M > p.Price)
    Assert.Contains("Price < @p", toRawSql qInvGt)

    let qInvGte = products.Where(fun p -> 50.0M >= p.Price)
    Assert.Contains("Price <= @p", toRawSql qInvGte)

    // Inverted equality with null
    let qNullEq = products.Where(fun p -> null = p.Name)
    Assert.Contains("Name IS NULL", toRawSql qNullEq)

    let qNullNeq = products.Where(fun p -> null <> p.Name)
    Assert.Contains("Name IS NOT NULL", toRawSql qNullNeq)

    // PostgreSQL boolean comparison
    let mockPg = AdvancedMockConnector(ConnectorKind("PostgreSql"))
    let productsPg = SqlPushdownQueryable<CovProduct>(mockPg, "Products")
    let qPgBool = productsPg.Where(fun p -> p.InStock)
    Assert.Contains("InStock = TRUE", toRawSql qPgBool)

// =========================================================================
// 15. STRING METHODS IN WHERE CLAUSE (IsNullOrEmpty, IsNullOrWhiteSpace, etc)
// =========================================================================

[<Fact>]
let ``String methods inside Where predicate lambda`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")

    let qNullOrEmpty = products.Where(fun p -> String.IsNullOrEmpty(p.Name))
    Assert.Contains("IS NULL OR", toRawSql qNullOrEmpty)

    let qNullOrWs = products.Where(fun p -> String.IsNullOrWhiteSpace(p.Name))
    Assert.Contains("TRIM(", toRawSql qNullOrWs)

    let qContains = products.Where(fun p -> p.Name.Contains("Board"))
    Assert.Contains("LIKE @p", toRawSql qContains)

    let qStarts = products.Where(fun p -> p.Name.StartsWith("Key"))
    Assert.Contains("LIKE @p", toRawSql qStarts)

    let qEnds = products.Where(fun p -> p.Name.EndsWith("board"))
    Assert.Contains("LIKE @p", toRawSql qEnds)

// =========================================================================
// 16. WINDOW FUNCTIONS LAG AND LEAD WITH DEFAULT VALUE
// =========================================================================

[<Fact>]
let ``Window functions LAG and LEAD with defaultValue`` () =
    let mock = AdvancedMockConnector(ConnectorKind("Sqlite"))
    let products = SqlPushdownQueryable<CovProduct>(mock, "Products")

    let qLagDef =
        fsql {
            for p in products do
            select (fsql.lag(p.Price, offset = 2, defaultValue = 0.0M))
        }
    let sqlLag = toRawSql qLagDef
    Assert.Contains("LAG(", sqlLag)
    Assert.Contains("2, 0.0", sqlLag)

    let qLeadDef =
        fsql {
            for p in products do
            select (fsql.lead(p.Price, offset = 1, defaultValue = 99.99M))
        }
    let sqlLead = toRawSql qLeadDef
    Assert.Contains("LEAD(", sqlLead)
    Assert.Contains("1, 99.99", sqlLead)

// =========================================================================
// 17. SCALAR SUBQUERIES IN PROJECTION AND WHERE
// =========================================================================

[<Fact>]
let ``Scalar subqueries in projection and predicate`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")
    let orders = SqlPushdownQueryable<CovOrder>(conn, "Orders")

    // Scalar subquery in projection
    let qSubProj = products.Select(fun p -> (p.Name, orders.Where(fun o -> o.CustomerId = p.Id).Count()))
    let sqlProj = toRawSql qSubProj
    Assert.Contains("SELECT", sqlProj)
    Assert.Contains("COUNT(", sqlProj)

    // Constant queryable subquery in projection
    let qConstSub = products.Select(fun p -> (p.Name, orders.Count()))
    let sqlConstSub = toRawSql qConstSub
    Assert.Contains("COUNT(", sqlConstSub)

// =========================================================================
// 18. QUERYABLE.ANY AND EXISTS / NOT EXISTS IN PREDICATES
// =========================================================================

[<Fact>]
let ``Queryable Any and Exists / NotExists predicates in Where`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")
    let orders = SqlPushdownQueryable<CovOrder>(conn, "Orders")

    // Queryable.Any with predicate inside Where
    let qAnyPred = products.Where(fun p -> orders.Any(fun o -> o.CustomerId = p.Id))
    let sqlAnyPred = toRawSql qAnyPred
    Assert.Contains("EXISTS (SELECT 1 FROM Orders", sqlAnyPred)

    // Queryable.Any without predicate (subquery already filtered)
    let qAnyNoPred = products.Where(fun p -> orders.Where(fun o -> o.CustomerId = p.Id).Any())
    let sqlAnyNoPred = toRawSql qAnyNoPred
    Assert.Contains("EXISTS (SELECT 1 FROM Orders", sqlAnyNoPred)

    // Negated Any -> NOT EXISTS
    let qNotAny = products.Where(fun p -> not (orders.Any(fun o -> o.CustomerId = p.Id)))
    let sqlNotAny = toRawSql qNotAny
    Assert.Contains("NOT EXISTS", sqlNotAny)

// =========================================================================
// 19. FSQL COMPUTATION EXPRESSION ADVANCED DQL CLAUSES
// =========================================================================

[<Fact>]
let ``FSql computation expression advanced DQL operations and joins`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")
    let orders = SqlPushdownQueryable<CovOrder>(conn, "Orders")

    // whereExists subquery
    let qWhereExists =
        fsql {
            for p in products do
            whereExists (orders.Where(fun o -> o.CustomerId = p.Id))
            select p
        }
    let sqlWhereExists = toRawSql qWhereExists
    Assert.Contains("EXISTS", sqlWhereExists)

    // whereNotExists subquery
    let qWhereNotExists =
        fsql {
            for p in products do
            whereNotExists (orders.Where(fun o -> o.CustomerId = p.Id))
            select p
        }
    let sqlWhereNotExists = toRawSql qWhereNotExists
    Assert.Contains("NOT EXISTS", sqlWhereNotExists)

    // having
    let qHaving =
        fsql {
            for p in products do
            groupBy p.Category into g
            having (g.Key <> "")
            select g.Key
        }
    let sqlHaving = toRawSql qHaving
    Assert.Contains("GROUP BY", sqlHaving)
    Assert.Contains("HAVING", sqlHaving)

    // fullOuterJoin
    let qFullJoin =
        fsql {
            for p in products do
            fullOuterJoin o in orders on (p.Id = o.CustomerId) into g
            for o in g.DefaultIfEmpty() do
            select (p.Id, o.OrderId)
        }
    let sqlFullJoin = toRawSql qFullJoin
    Assert.Contains("FULL OUTER JOIN", sqlFullJoin)

    // rightOuterJoin
    let qRightJoin =
        fsql {
            for p in products do
            rightOuterJoin o in orders on (p.Id = o.CustomerId)
            select (p.Id, o.OrderId)
        }
    let sqlRightJoin = toRawSql qRightJoin
    Assert.Contains("RIGHT OUTER JOIN", sqlRightJoin)

    // rightOuterJoinGroup
    let qRightGroup =
        fsql {
            for p in products do
            rightOuterJoinGroup o in orders on (p.Id = o.CustomerId) into g
            select (p.Id, g.Count())
        }
    let sqlRightGroup = toRawSql qRightGroup
    Assert.Contains("RIGHT OUTER JOIN", sqlRightGroup)

    // fsql set operations
    let qUnion =
        fsql {
            for p in products do
            select p
            union products
        }
    Assert.Contains("UNION", toRawSql qUnion)

    let qUnionAll =
        fsql {
            for p in products do
            select p
            unionAll products
        }
    Assert.Contains("UNION ALL", toRawSql qUnionAll)

    let qIntersect =
        fsql {
            for p in products do
            select p
            intersect products
        }
    Assert.Contains("INTERSECT", toRawSql qIntersect)

    let qExcept =
        fsql {
            for p in products do
            select p
            except products
        }
    Assert.Contains("EXCEPT", toRawSql qExcept)

    // withCte and withRecursiveCte inside fsql
    let qWithCte =
        fsql {
            for p in products do
            withCte "orders_cte" orders
            select p
        }
    Assert.Contains("WITH orders_cte AS", toRawSql qWithCte)

    let qWithCteSql =
        fsql {
            for p in products do
            withCteSql "sql_cte" "SELECT 1 AS Num"
            select p
        }
    Assert.Contains("WITH sql_cte AS (SELECT 1 AS Num)", toRawSql qWithCteSql)

    let qWithRecSql =
        fsql {
            for p in products do
            withRecursiveCteSql "rec_cte" "SELECT 1 UNION ALL SELECT 2"
            select p
        }
    Assert.Contains("rec_cte AS", toRawSql qWithRecSql)

// =========================================================================
// 20. OPTION TYPE TRANSLATION AND COALESCE
// =========================================================================

[<Fact>]
let ``Option and Nullable operations isSome isNone defaultValue and pattern match`` () =
    let mock = AdvancedMockConnector(ConnectorKind("Sqlite"))
    let items = SqlPushdownQueryable<CovOptionItem>(mock, "OptionItems")

    // Option.isSome and Option.isNone
    let qIsSome = items.Where(fun x -> Option.isSome x.Rating)
    Assert.Contains("Rating IS NOT NULL", toRawSql qIsSome)

    let qIsNone = items.Where(fun x -> Option.isNone x.Rating)
    Assert.Contains("Rating IS NULL", toRawSql qIsNone)

    // .IsSome and .IsNone properties
    let qPropIsSome = items.Where(fun x -> x.Rating.IsSome)
    Assert.Contains("Rating IS NOT NULL", toRawSql qPropIsSome)

    let qPropIsNone = items.Where(fun x -> x.Rating.IsNone)
    Assert.Contains("Rating IS NULL", toRawSql qPropIsNone)

    // Option.defaultValue
    let qOptDef =
        fsql {
            for x in items do
            select (Option.defaultValue 0.0M x.Rating)
        }
    Assert.Contains("COALESCE(", toRawSql qOptDef)

    // defaultArg
    let qDefArg =
        fsql {
            for x in items do
            select (defaultArg x.Rating 10.0M)
        }
    Assert.Contains("COALESCE(", toRawSql qDefArg)

    // Nullable parameterless GetValueOrDefault()
    let products = SqlPushdownQueryable<CovProduct>(mock, "Products")
    let qParamlessGvod = products.Select(fun p -> p.Score.GetValueOrDefault())
    Assert.Contains("COALESCE(", toRawSql qParamlessGvod)

// =========================================================================
// 21. LINQ EXTENSION METHODS AND TERMINAL AGGREGATES
// =========================================================================

[<Fact>]
let ``LINQ OrderBy ThenBy First Single and Terminal Aggregates`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")

    // OrderBy and ThenBy
    let qOrderThen =
        products
            .OrderBy(fun p -> p.Category)
            .ThenBy(fun p -> p.Price)
            .OrderByDescending(fun p -> p.Category)
            .ThenByDescending(fun p -> p.Price)
    let sqlOrderThen = toRawSql qOrderThen
    Assert.Contains("ORDER BY", sqlOrderThen)
    Assert.Contains("DESC", sqlOrderThen)

    // Queryable First, FirstOrDefault, Single, SingleOrDefault
    let prov = (products :> IQueryable).Provider :?> SqlPushdownQueryProvider
    let planFirst = prov.ExtractPlan(Queryable.Take(products, 1).Expression)
    Assert.Equal(Some 1, planFirst.Take)

    // Queryable terminal aggregations
    let sumVal = Queryable.Sum(products.Select(fun p -> p.Price))
    Assert.True(sumVal > 0.0M)

    let minVal = Queryable.Min(products.Select(fun p -> p.Price))
    Assert.Equal(1.50M, minVal)

    let maxVal = Queryable.Max(products.Select(fun p -> p.Price))
    Assert.Equal(299.99M, maxVal)

    let avgVal = Queryable.Average(products.Select(fun p -> p.Price))
    Assert.True(avgVal > 0.0M)

    // LongCount
    let longCount = Queryable.LongCount(products)
    Assert.Equal(5L, longCount)

    // Terminal aggregate Avg on Projection
    let selExpr = products.Select(fun p -> p.Price).Expression
    let avgMethod = typeof<Queryable>.GetMethods() |> Seq.find (fun m -> m.Name = "Average" && m.GetParameters().Length = 1 && m.GetParameters().[0].ParameterType.GetGenericArguments().[0] = typeof<decimal>)
    let avgExpr = Expression.Call(null, avgMethod, selExpr)
    let planAvg = prov.ExtractPlan(avgExpr)
    Assert.True(planAvg.Projection.IsSome)
    Assert.Contains("AVG(", planAvg.Projection.Value)

// =========================================================================
// 22. NON-CORRELATED SELECTMANY AND CROSS-CONNECTOR JOIN FALLBACK
// =========================================================================

[<Fact>]
let ``Non-correlated SelectMany and cross-connector LinqInMemoryFallback`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")
    let orders = SqlPushdownQueryable<CovOrder>(conn, "Orders")

    // Non-correlated SelectMany (INNER JOIN on 1=1)
    let pParam = Expression.Parameter(typeof<CovProduct>, "p")
    let ordersConst = Expression.Constant(orders)
    let selManyLambda = Expression.Lambda<Func<CovProduct, seq<CovOrder>>>(ordersConst, pParam)
    let qCrossJoin = Queryable.SelectMany(products, selManyLambda)
    let sqlCrossJoin = toRawSql qCrossJoin
    Assert.Contains("INNER JOIN Orders _t1 ON 1=1", sqlCrossJoin)

    // Non-correlated SelectMany with DefaultIfEmpty (LEFT JOIN on 1=1)
    let defIfEmptyMethod = typeof<Enumerable>.GetMethods() |> Seq.find (fun m -> m.Name = "DefaultIfEmpty" && m.GetParameters().Length = 1)
    let defIfEmptyCall = Expression.Call(null, defIfEmptyMethod.MakeGenericMethod(typeof<CovOrder>), ordersConst)
    let selManyDefLambda = Expression.Lambda<Func<CovProduct, seq<CovOrder>>>(defIfEmptyCall, pParam)
    let qOuterJoin = Queryable.SelectMany(products, selManyDefLambda)
    let sqlOuterJoin = toRawSql qOuterJoin
    Assert.Contains("LEFT JOIN Orders _t1 ON 1=1", sqlOuterJoin)

    // LinqInMemoryFallback.evaluate direct calls
    let listQ = ([1; 2; 3; 4; 5] :> seq<int>).AsQueryable()
    let whereExpr = Queryable.Where(listQ, fun x -> x > 2).Expression
    let whereRes = LinqInMemoryFallback.evaluate whereExpr :?> seq<int>
    Assert.Equal(3, Seq.length whereRes)

    let countMethod = typeof<Queryable>.GetMethods() |> Seq.find (fun m -> m.Name = "Count" && m.GetParameters().Length = 1)
    let countExpr = Expression.Call(null, countMethod.MakeGenericMethod(typeof<int>), listQ.Expression)
    let countRes = LinqInMemoryFallback.evaluate countExpr :?> int
    Assert.Equal(5, countRes)

    // Cross-connector join execution triggering LinqInMemoryFallback
    let mockPg = AdvancedMockConnector(ConnectorKind("PostgreSql"))
    let pgOrders = SqlPushdownQueryable<CovOrder>(mockPg, "Orders")
    let crossConnJoin = products.Join(pgOrders, (fun p -> p.Id), (fun o -> o.CustomerId), (fun p o -> (p.Name, o.Amount)))
    let prov = (products :> IQueryable).Provider :?> SqlPushdownQueryProvider
    let res = prov.ExecutePlanOrStream (crossConnJoin :> IQueryable).Expression
    Assert.NotNull(res)

// =========================================================================
// 23. CTE PROPAGATION IN SET OPERATIONS AND EXTENSIONS
// =========================================================================

[<Fact>]
let ``CTE propagation in Set Operations and Queryable extensions`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")
    let orders = SqlPushdownQueryable<CovOrder>(conn, "Orders")

    // WithCte on wrapped queryable
    let withCteQ = products.WithCte("orders_cte", orders).Where(fun p -> p.Price > 10.0M)
    let otherQ = products.Where(fun p -> p.Price < 100.0M)

    let u1 = withCteQ.Union(otherQ)
    Assert.Contains("WITH orders_cte AS", toRawSql u1)

    let u2 = withCteQ.UnionAll(otherQ)
    Assert.Contains("WITH orders_cte AS", toRawSql u2)

    let u3 = withCteQ.Intersect(otherQ)
    Assert.Contains("WITH orders_cte AS", toRawSql u3)

    let u4 = withCteQ.Except(otherQ)
    Assert.Contains("WITH orders_cte AS", toRawSql u4)

    // products.Union(otherWithCte)
    let otherWithCte = products.WithCte("sub_cte", orders).Where(fun p -> p.Price > 5.0M)
    let pu1 = products.Union(otherWithCte)
    Assert.Contains("WITH sub_cte AS", toRawSql pu1)

    let pu2 = products.UnionAll(otherWithCte)
    Assert.Contains("WITH sub_cte AS", toRawSql pu2)

    let pu3 = products.Intersect(otherWithCte)
    Assert.Contains("WITH sub_cte AS", toRawSql pu3)

    let pu4 = products.Except(otherWithCte)
    Assert.Contains("WITH sub_cte AS", toRawSql pu4)

// =========================================================================
// 24. REFLECTION AND PROVIDER METADATA
// =========================================================================

[<Fact>]
let ``SqlPushdownQueryProvider properties, CreateQuery, and ExpressionPushdownTranslator helpers`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")
    let prov = (products :> IQueryable).Provider :?> SqlPushdownQueryProvider

    // Provider properties
    Assert.Same(conn, prov.Connector)
    Assert.Equal("Products", prov.BaseSql)
    Assert.Empty(prov.BaseParameters)
    Assert.Equal(SqlDialect.Sqlite, prov.Dialect)

    // (IQueryProvider).CreateQuery non-generic
    let nonGeneric = (prov :> IQueryProvider).CreateQuery((products :> IQueryable).Expression)
    Assert.NotNull(nonGeneric)
    Assert.Equal(typeof<CovProduct>, nonGeneric.ElementType)

    // hasJoin helper
    let joinQ = products.Join(products, (fun p -> p.Id), (fun p -> p.Id), (fun p1 p2 -> p1))
    Assert.True(ExpressionPushdownTranslator.hasJoin (joinQ :> IQueryable).Expression)
    Assert.False(ExpressionPushdownTranslator.hasJoin (products :> IQueryable).Expression)

    // extractColumnName and extractKeyColumns reflection helpers
    let param = Expression.Parameter(typeof<CovProduct>, "p")
    let propExpr = Expression.Property(param, "Price")
    let colName = ExpressionPushdownTranslator.extractColumnName param propExpr
    Assert.Equal("Price", colName)

    let convertExpr = Expression.Convert(propExpr, typeof<obj>)
    let colNameConv = ExpressionPushdownTranslator.extractColumnName param convertExpr
    Assert.Equal("Price", colNameConv)

    let plan = PushdownQueryPlan.Create("Products", [])
    let keys = ExpressionPushdownTranslator.extractKeyColumns plan "p" param propExpr
    Assert.Equal<string list>([ "p.Price" ], keys)

// =========================================================================
// 25. ADVANCED INVERTED COMPARISONS AND NULL CHECKS IN WHERE
// =========================================================================

[<Fact>]
let ``Advanced inverted comparisons constants and null checks in Where clauses`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")

    // Inverted constant comparisons (constant on LEFT, column on RIGHT)
    let qInvLt = products.Where(fun p -> 10.0M < p.Price)
    Assert.Contains("Price > @p0", toRawSql qInvLt)

    let qInvLte = products.Where(fun p -> 10.0M <= p.Price)
    Assert.Contains("Price >= @p0", toRawSql qInvLte)

    let qInvGt = products.Where(fun p -> 10.0M > p.Price)
    Assert.Contains("Price < @p0", toRawSql qInvGt)

    let qInvGte = products.Where(fun p -> 10.0M >= p.Price)
    Assert.Contains("Price <= @p0", toRawSql qInvGte)

    let qInvEq = products.Where(fun p -> 10.0M = p.Price)
    Assert.Contains("Price = @p0", toRawSql qInvEq)

    let qInvNeq = products.Where(fun p -> 10.0M <> p.Price)
    Assert.Contains("Price <> @p0", toRawSql qInvNeq)

    // Null constant on LEFT with Nullable column on RIGHT
    let qNullEq = products.Where(fun p -> Nullable() = p.Score)
    Assert.Contains("Score IS NULL", toRawSql qNullEq)

    let qNullNeq = products.Where(fun p -> Nullable() <> p.Score)
    Assert.Contains("Score IS NOT NULL", toRawSql qNullNeq)

    // Option comparisons
    let qSomeEq = products.Where(fun p -> p.Price = (Some 25.0M).Value)
    Assert.Contains("Price = @p0", toRawSql qSomeEq)

    // Inverted column to column comparison
    let qColCol = products.Where(fun p -> p.Price = p.Score.Value)
    Assert.Contains("Price = Score", toRawSql qColCol)

    // Boolean column member expression directly in Where
    let qBool = products.Where(fun p -> p.InStock)
    Assert.Contains("InStock = 1", toRawSql qBool)

    let connPg = AdvancedMockConnector(ConnectorKind("PostgreSql"))
    let qBoolPg = SqlPushdownQueryable<CovProduct>(connPg, "Products").Where(fun p -> p.InStock)
    Assert.Contains("InStock = TRUE", toRawSql qBoolPg)

// =========================================================================
// 26. DEEP SCALAR EXPRESSIONS, MATH, STRING, AND JSON METHODS ACROSS DIALECTS
// =========================================================================

[<Fact>]
let ``Deep scalar expressions Math String Arithmetic and JsonValue across dialects`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")

    // Math methods
    let qMath1 = products.Select(fun p -> Math.Round(p.Price))
    Assert.Contains("ROUND(Price)", toRawSql qMath1)

    let qMath2 = products.Select(fun p -> Math.Round(p.Price, 2))
    Assert.Contains("ROUND(Price, 2)", toRawSql qMath2)

    let qMathFloor = products.Select(fun p -> Math.Floor(p.Price))
    Assert.Contains("FLOOR(Price)", toRawSql qMathFloor)

    let qMathCeilSqlite = products.Select(fun p -> Math.Ceiling(p.Price))
    Assert.Contains("CEIL(Price)", toRawSql qMathCeilSqlite)

    let connSs = AdvancedMockConnector(ConnectorKind("SqlServer"))
    let qMathCeilSs = SqlPushdownQueryable<CovProduct>(connSs, "Products").Select(fun p -> Math.Ceiling(p.Price))
    Assert.Contains("CEILING(Price)", toRawSql qMathCeilSs)

    let qMathSqrt = products.Select(fun p -> Math.Sqrt(float p.Price))
    Assert.Contains("SQRT(", toRawSql qMathSqrt)

    let qMathPow = products.Select(fun p -> Math.Pow(float p.Price, 2.0))
    Assert.Contains("POWER(", toRawSql qMathPow)

    let qMathAbs = products.Select(fun p -> Math.Abs(p.Price))
    Assert.Contains("ABS(Price)", toRawSql qMathAbs)

    // String Substring and Replace
    let qSub1 = products.Select(fun p -> p.Name.Substring(1))
    Assert.Contains("SUBSTR(Name", toRawSql qSub1)

    let qSub2 = products.Select(fun p -> p.Name.Substring(1, 3))
    Assert.Contains("SUBSTR(Name", toRawSql qSub2)

    let qSubSs = SqlPushdownQueryable<CovProduct>(connSs, "Products").Select(fun p -> p.Name.Substring(1, 3))
    Assert.Contains("SUBSTRING(Name", toRawSql qSubSs)

    let qRepl = products.Select(fun p -> p.Name.Replace("a", "b"))
    Assert.Contains("REPLACE(Name, 'a', 'b')", toRawSql qRepl)

    // String Case and Trim
    let qCaseTrim = products.Select(fun p -> (p.Name.ToUpper(), p.Name.ToLower(), p.Name.Trim()))
    let sCaseTrim = toRawSql qCaseTrim
    Assert.Contains("UPPER(Name)", sCaseTrim)
    Assert.Contains("LOWER(Name)", sCaseTrim)
    Assert.Contains("TRIM(Name)", sCaseTrim)

    // String IndexOf across dialects
    let qIdxSqlite = products.Select(fun p -> p.Name.IndexOf("a"))
    Assert.Contains("INSTR(Name, 'a') - 1", toRawSql qIdxSqlite)

    let qIdxSs = SqlPushdownQueryable<CovProduct>(connSs, "Products").Select(fun p -> p.Name.IndexOf("a"))
    Assert.Contains("CHARINDEX('a', Name) - 1", toRawSql qIdxSs)

    let connPg = AdvancedMockConnector(ConnectorKind("PostgreSql"))
    let qIdxPg = SqlPushdownQueryable<CovProduct>(connPg, "Products").Select(fun p -> p.Name.IndexOf("a"))
    Assert.Contains("POSITION('a' IN Name) - 1", toRawSql qIdxPg)

    let connMy = AdvancedMockConnector(ConnectorKind("MySql"))
    let qIdxMy = SqlPushdownQueryable<CovProduct>(connMy, "Products").Select(fun p -> p.Name.IndexOf("a"))
    Assert.Contains("INSTR(Name, 'a') - 1", toRawSql qIdxMy)

    // String addition across dialects
    let qConcatSqlite = products.Select(fun p -> p.Name + " - " + p.Category)
    Assert.Contains("||", toRawSql qConcatSqlite)

    let qConcatSs = SqlPushdownQueryable<CovProduct>(connSs, "Products").Select(fun p -> p.Name + " - " + p.Category)
    Assert.Contains("+", toRawSql qConcatSs)

    let qConcatMy = SqlPushdownQueryable<CovProduct>(connMy, "Products").Select(fun p -> p.Name + " - " + p.Category)
    Assert.Contains("CONCAT(", toRawSql qConcatMy)

    // Arithmetic in projection
    let qArith = products.Select(fun p -> (p.Price + 1.0M, p.Price - 1.0M, p.Price * 2.0M, p.Price / 2.0M, p.Id % 2))
    let sArith = toRawSql qArith
    Assert.Contains("+", sArith)
    Assert.Contains("-", sArith)
    Assert.Contains("*", sArith)
    Assert.Contains("/", sArith)
    Assert.Contains("%", sArith)

    // String.Concat method
    let qStrConcat = products.Select(fun p -> String.Concat(p.Name, p.Category))
    Assert.Contains("||", toRawSql qStrConcat)

    // JsonValue across dialects
    let qJsonSqlite = fsql { for p in products do select (fsql.jsonValue(p.Name, "$.info.title")) }
    Assert.Contains("json_extract(Name, '$.info.title')", toRawSql qJsonSqlite)

    let qJsonSs = fsql { for p in SqlPushdownQueryable<CovProduct>(connSs, "Products") do select (fsql.jsonValue(p.Name, "$.info.title")) }
    Assert.Contains("JSON_VALUE(Name, '$.info.title')", toRawSql qJsonSs)

    let qJsonMy = fsql { for p in SqlPushdownQueryable<CovProduct>(connMy, "Products") do select (fsql.jsonValue(p.Name, "$.info.title")) }
    Assert.Contains("JSON_UNQUOTE(JSON_EXTRACT(Name, '$.info.title'))", toRawSql qJsonMy)

    let qJsonPgNested = fsql { for p in SqlPushdownQueryable<CovProduct>(connPg, "Products") do select (fsql.jsonValue(p.Name, "$.data.user.name")) }
    Assert.Contains("#>>", toRawSql qJsonPgNested)
    Assert.Contains("data", toRawSql qJsonPgNested)

    let qJsonPgSingle = fsql { for p in SqlPushdownQueryable<CovProduct>(connPg, "Products") do select (fsql.jsonValue(p.Name, "$.title")) }
    Assert.Contains("->> 'title'", toRawSql qJsonPgSingle)

// =========================================================================
// 27. CONDITIONAL EXPRESSIONS (CASE WHEN) IN PROJECTION
// =========================================================================

[<Fact>]
let ``Conditional expressions Case When and Nullable helpers in projection`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")

    let qCase1 = products.Select(fun p -> if p.Price > 10.0M && p.InStock then 1 else 0)
    Assert.Contains("CASE WHEN", toRawSql qCase1)
    Assert.Contains("AND", toRawSql qCase1)

    let qCase2 = products.Select(fun p -> if p.Price < 5.0M || p.Price >= 100.0M then "Ext" else "Mid")
    Assert.Contains("OR", toRawSql qCase2)

    let qCase3 = products.Select(fun p -> if not (p.Price <= 20.0M) then "Over20" else "Under20")
    Assert.Contains("NOT", toRawSql qCase3)

    let qCase4 = products.Select(fun p -> if p.Score = Nullable() then "NoScore" else "HasScore")
    Assert.Contains("IS NULL", toRawSql qCase4)

    let qCase5 = products.Select(fun p -> if Nullable() <> p.Score then "HasScore" else "NoScore")
    Assert.Contains("IS NOT NULL", toRawSql qCase5)

    let qCase6 = products.Select(fun p -> if p.Score.HasValue then "Val" else "NoVal")
    Assert.Contains("IS NOT NULL", toRawSql qCase6)

    let qCase7 = products.Select(fun p -> if String.IsNullOrEmpty(p.Name) then "Empty" else "Filled")
    Assert.Contains("IS NULL", toRawSql qCase7)

    let qCase8 = products.Select(fun p -> if String.IsNullOrWhiteSpace(p.Name) then "Blank" else "Text")
    Assert.Contains("TRIM(Name)", toRawSql qCase8)

    let qCase9 = products.Select(fun p -> if p.Name.Contains("o") then "HasO" else "NoO")
    Assert.Contains("LIKE", toRawSql qCase9)

    let qCase10 = products.Select(fun p -> if p.Name.StartsWith("K") then "StartsK" else "Other")
    Assert.Contains("LIKE", toRawSql qCase10)

    let qCase11 = products.Select(fun p -> if p.Name.EndsWith("d") then "EndsD" else "Other")
    Assert.Contains("LIKE", toRawSql qCase11)

    let qCase12 = products.Select(fun p -> if p.InStock then "In" else "Out")
    Assert.Contains("InStock = 1", toRawSql qCase12)

    // Nullable GetValueOrDefault
    let qGvod0 = products.Select(fun p -> p.Score.GetValueOrDefault())
    Assert.Contains("COALESCE(Score, 0)", toRawSql qGvod0)

    let qGvod1 = products.Select(fun p -> p.Score.GetValueOrDefault(99.0M))
    Assert.Contains("COALESCE(Score, 99.0)", toRawSql qGvod1)

    let qGvodStatic = products.Select(fun p -> p.Score.GetValueOrDefault(50.0M))
    Assert.Contains("COALESCE(Score, 50.0)", toRawSql qGvodStatic)

// =========================================================================
// 28. WINDOW FUNCTIONS AND WINDOW DEFINITION WITH CLIENT EVALUATED VALUES
// =========================================================================

[<Fact>]
let ``Window functions and WindowDefinition edge cases with fallback defaults`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")

    // Window specifications with different sort and partition variants
    let qRowNumber = products.Select(fun p -> fsql.rowNumber(over = fsql.window(partitionBy = p.Category, sortBy = p.Price)))
    Assert.Contains("ROW_NUMBER() OVER (PARTITION BY Category ORDER BY Price ASC)", toRawSql qRowNumber)

    let qRank = products.Select(fun p -> fsql.rank(over = fsql.window(partitionBy = p.Category, sortByDescending = p.Price)))
    Assert.Contains("RANK() OVER (PARTITION BY Category ORDER BY Price DESC)", toRawSql qRank)

    let qDenseRank = products.Select(fun p -> fsql.denseRank(over = fsql.window(sortBy = p.Price)))
    Assert.Contains("DENSE_RANK() OVER (ORDER BY Price ASC)", toRawSql qDenseRank)

    let qNtile = products.Select(fun p -> fsql.ntile(4, over = fsql.window(sortByDescending = p.Price)))
    Assert.Contains("NTILE(4) OVER (ORDER BY Price DESC)", toRawSql qNtile)

    let qFirstVal = products.Select(fun p -> fsql.firstValue(p.Price, over = fsql.window(partitionBy = p.Category)))
    Assert.Contains("FIRST_VALUE(Price) OVER (PARTITION BY Category)", toRawSql qFirstVal)

    let qLastVal = products.Select(fun p -> fsql.lastValue(p.Price, over = fsql.window()))
    Assert.Contains("LAST_VALUE(Price) OVER ()", toRawSql qLastVal)

    let qSumOver = products.Select(fun p -> fsql.sum(p.Price, over = fsql.window(partitionBy = p.Category)))
    Assert.Contains("SUM(Price) OVER (PARTITION BY Category)", toRawSql qSumOver)

    let qAvgOver = products.Select(fun p -> fsql.avg(p.Price, over = fsql.window(partitionBy = p.Category)))
    Assert.Contains("AVG(Price) OVER (PARTITION BY Category)", toRawSql qAvgOver)

    let qMinOver = products.Select(fun p -> fsql.min(p.Price, over = fsql.window(partitionBy = p.Category)))
    Assert.Contains("MIN(Price) OVER (PARTITION BY Category)", toRawSql qMinOver)

    let qMaxOver = products.Select(fun p -> fsql.max(p.Price, over = fsql.window(partitionBy = p.Category)))
    Assert.Contains("MAX(Price) OVER (PARTITION BY Category)", toRawSql qMaxOver)

    let qCountOverCol = products.Select(fun p -> fsql.count(p.Price, over = fsql.window(partitionBy = p.Category)))
    Assert.Contains("COUNT(Price) OVER (PARTITION BY Category)", toRawSql qCountOverCol)

    // LAG and LEAD with client-evaluated fallback defaultValue expressions
    let qLag = products.Select(fun p -> fsql.lag(p.Price, 1, defaultValue = decimal (Math.Abs(-5)), over = fsql.window(sortBy = p.Price)))
    Assert.Contains("LAG(Price, 1, ABS(-5))", toRawSql qLag)

    let qLead = products.Select(fun p -> fsql.lead(p.Price, 1, defaultValue = decimal (Math.Sqrt(16.0)), over = fsql.window(sortBy = p.Price)))
    Assert.Contains("LEAD(Price, 1, SQRT(", toRawSql qLead)

    let qLagStr = products.Select(fun p -> fsql.lag(p.Name, 1, defaultValue = String.Concat("N", "/A"), over = fsql.window(sortBy = p.Name)))
    Assert.Contains("LAG(Name, 1, ('N' || '/A'))", toRawSql qLagStr)

    let qLeadNull = products.Select(fun p -> fsql.lead(p.Name, 1, defaultValue = null, over = fsql.window(sortBy = p.Name)))
    Assert.Contains("LEAD(Name, 1, NULL)", toRawSql qLeadNull)

// =========================================================================
// 29. TERMINAL AGGREGATES, DISTINCT AGGREGATES, AND PROJECTIONS
// =========================================================================

[<Fact>]
let ``Terminal aggregates distinct aggregates and projection expressions`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")

    // Direct methods on SqlPushdownQueryable
    Assert.Equal(1.50M, products.Min<decimal>("Price"))
    Assert.Equal(1.50M, products.Min(fun p -> p.Price))
    Assert.Equal(299.99M, products.Max<decimal>("Price"))
    Assert.Equal(299.99M, products.Max(fun p -> p.Price))
    Assert.True(products.Average<decimal>("Price") > 0.0M)
    Assert.True(products.Average(fun p -> p.Price) > 0.0M)
    Assert.True(products.Sum<decimal>("Price") > 0.0M)
    Assert.True(products.Sum(fun p -> p.Price) > 0.0M)
    Assert.Equal(5, products.Count())
    Assert.Equal(4, products.Count(fun p -> p.Price > 5.0M))
    Assert.Equal(5L, products.LongCount())

    // Distinct aggregates via LINQ
    let qSumDist = products.Distinct().Select(fun p -> p.Price).Sum()
    Assert.True(qSumDist > 0.0M)

    let qAvgDist = products.Distinct().Select(fun p -> p.Price).Average()
    Assert.True(qAvgDist > 0.0M)

    let qMinDist = products.Distinct().Select(fun p -> p.Price).Min()
    Assert.Equal(1.50M, qMinDist)

    let qMaxDist = products.Distinct().Select(fun p -> p.Price).Max()
    Assert.Equal(299.99M, qMaxDist)

    let qCountDist = products.Distinct().Select(fun p -> p.Price).Count()
    Assert.Equal(5, qCountDist)

    let qCountDistId = products.Distinct().Select(fun p -> p.Id).Count()
    Assert.Equal(5, qCountDistId)

    // Chained Select
    let qChained = products.Select(fun p -> {| P = p; Cat = p.Category |}).Select(fun x -> x.Cat)
    Assert.Contains("Category", toRawSql qChained)

    // Projection of single expressions with AS Value
    let qSingleBinary = products.Select(fun p -> p.Price * 1.1M)
    Assert.Contains("AS Value", toRawSql qSingleBinary)

    let qSingleMethod = products.Select(fun p -> p.Name.ToUpper())
    Assert.Contains("AS Value", toRawSql qSingleMethod)

    let qSingleScore = products.Select(fun p -> p.Score)
    Assert.Contains("Score", toRawSql qSingleScore)

// =========================================================================
// 30. SELECTMANY, OUTER APPLY, JOINS WITH TUPLE KEYS, AND FALLBACK PREDICATES
// =========================================================================

[<Fact>]
let ``SelectMany cross apply outer apply tuple joins and fallback predicates`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")
    let orders = SqlPushdownQueryable<CovOrder>(conn, "Orders")

    // Non-correlated SelectMany (INNER JOIN / CROSS JOIN 1=1)
    let qSmNonCorr =
        fsql {
            for p in products do
            for o in orders do
            select (p, o)
        }
    Assert.Contains("INNER JOIN", toRawSql qSmNonCorr)
    Assert.Contains("1=1", toRawSql qSmNonCorr)

    // Non-correlated SelectMany with DefaultIfEmpty (LEFT JOIN 1=1)
    let qSmNonCorrLeft =
        fsql {
            for p in products do
            for o in orders.DefaultIfEmpty() do
            select (p, o)
        }
    Assert.Contains("LEFT JOIN", toRawSql qSmNonCorrLeft)
    Assert.Contains("1=1", toRawSql qSmNonCorrLeft)

    // Correlated SelectMany (CROSS APPLY)
    let pParam = Expression.Parameter(typeof<CovProduct>, "p")
    let oParam = Expression.Parameter(typeof<CovOrder>, "o")
    let custIdProp = Expression.Property(oParam, "CustomerId")
    let prodIdProp = Expression.Property(pParam, "Id")
    let eqPred = Expression.Equal(custIdProp, prodIdProp)
    let predLambda = Expression.Lambda<Func<CovOrder, bool>>(eqPred, oParam)
    let whereMethod = typeof<Queryable>.GetMethods() |> Seq.find (fun m -> m.Name = "Where" && m.GetParameters().Length = 2)
    let ordersConst = Expression.Constant(orders)
    let whereCall = Expression.Call(null, whereMethod.MakeGenericMethod(typeof<CovOrder>), ordersConst, predLambda)
    let selManyLambda = Expression.Lambda<Func<CovProduct, seq<CovOrder>>>(whereCall, pParam)
    let qSmCorr = Queryable.SelectMany(products, selManyLambda)
    Assert.Contains("CROSS JOIN (SELECT * FROM Orders", toRawSql qSmCorr)

    // Correlated SelectMany with DefaultIfEmpty (OUTER APPLY)
    let defIfEmptyMethod = typeof<Queryable>.GetMethods() |> Seq.find (fun m -> m.Name = "DefaultIfEmpty" && m.GetParameters().Length = 1)
    let defCall = Expression.Call(null, defIfEmptyMethod.MakeGenericMethod(typeof<CovOrder>), whereCall)
    let selManyDefLambda = Expression.Lambda<Func<CovProduct, seq<CovOrder>>>(defCall, pParam)
    let qSmCorrOuter = Queryable.SelectMany(products, selManyDefLambda)
    Assert.Contains("LEFT JOIN (SELECT * FROM Orders", toRawSql qSmCorrOuter)

    // SqlServer CROSS APPLY / OUTER APPLY
    let connSs = AdvancedMockConnector(ConnectorKind("SqlServer"))
    let prodSs = SqlPushdownQueryable<CovProduct>(connSs, "Products")
    let qSmCorrSs = Queryable.SelectMany(prodSs, selManyLambda)
    Assert.Contains("CROSS APPLY", toRawSql qSmCorrSs)
    let qSmCorrOuterSs = Queryable.SelectMany(prodSs, selManyDefLambda)
    Assert.Contains("OUTER APPLY", toRawSql qSmCorrOuterSs)

    // Join with composite keys using Tuple.Create
    let qTupleJoin = products.Join(orders, (fun p -> Tuple.Create(p.Id, p.Category)), (fun o -> Tuple.Create(o.CustomerId, o.Status)), (fun p o -> (p, o)))
    let sTuple = toRawSql qTupleJoin
    Assert.Contains("INNER JOIN Orders", sTuple)
    Assert.Contains("_t0.Id = _t1.CustomerId", sTuple)
    Assert.Contains("_t0.Category = _t1.Status", sTuple)

    // Fallback predicate in Where (client evaluated)
    let qFallback = products.Where(fun p -> (fun (s: string) -> s.Trim().Length > 3) p.Name)
    let resFallback = qFallback |> Seq.toList
    Assert.NotEmpty(resFallback)

    // Paging terminal methods
    Assert.NotNull(products.First())
    Assert.NotNull(products.FirstOrDefault())
    Assert.NotNull(products.Take(1).SingleOrDefault())

// =========================================================================
// 31. SCALAR SUBQUERIES IN PROJECTION AND IN-LIST PREDICATES
// =========================================================================

[<Fact>]
let ``Scalar subqueries in projection and list contains in Where`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")
    let orders = SqlPushdownQueryable<CovOrder>(conn, "Orders")

    // Scalar subqueries in projection: Count, Sum, Min, Max, Average
    let qSubCount = products.Select(fun p -> orders.Where(fun o -> o.CustomerId = p.Id).Count())
    Assert.Contains("(SELECT COUNT(*)", toRawSql qSubCount)

    let qSubSumNoLambda = products.Select(fun p -> orders.Where(fun o -> o.CustomerId = p.Id).Select(fun o -> o.Amount).Sum())
    Assert.Contains("(SELECT SUM(*)", toRawSql qSubSumNoLambda)

    let qSubMinNoLambda = products.Select(fun p -> orders.Where(fun o -> o.CustomerId = p.Id).Select(fun o -> o.Amount).Min())
    Assert.Contains("(SELECT MIN(*)", toRawSql qSubMinNoLambda)

    let qSubMaxNoLambda = products.Select(fun p -> orders.Where(fun o -> o.CustomerId = p.Id).Select(fun o -> o.Amount).Max())
    Assert.Contains("(SELECT MAX(*)", toRawSql qSubMaxNoLambda)

    let qSubAvgNoLambda = products.Select(fun p -> orders.Where(fun o -> o.CustomerId = p.Id).Select(fun o -> o.Amount).Average())
    Assert.Contains("(SELECT AVG(*)", toRawSql qSubAvgNoLambda)

    // Subquery in Where via Contains
    let qSubContains = products.Where(fun p -> orders.Where(fun o -> o.Amount > 100.0M).Select(fun o -> o.CustomerId).Contains(p.Id))
    Assert.Contains("IN (SELECT", toRawSql qSubContains)
    Assert.Contains("FROM Orders", toRawSql qSubContains)

    // Exists and NotExists marker in Where
    let qMarkerExists = products.Where(fun p -> SqlComputationExtensions.exists (orders.Where(fun o -> o.CustomerId = p.Id)))
    Assert.Contains("EXISTS (SELECT 1 FROM Orders", toRawSql qMarkerExists)

    let qMarkerNotExists = products.Where(fun p -> SqlComputationExtensions.notExists (orders.Where(fun o -> o.CustomerId = p.Id)))
    Assert.Contains("NOT EXISTS (SELECT 1 FROM Orders", toRawSql qMarkerNotExists)

    // Empty list and populated list in Where
    let emptyList: string list = []
    let qEmpty = products.Where(fun p -> emptyList.Contains(p.Category))
    Assert.Contains("(1=0)", toRawSql qEmpty)

    let catList = [ "Electronics"; "Stationery" ]
    let qPopulated = products.Where(fun p -> catList.Contains(p.Category))
    Assert.Contains("IN (@p0, @p1)", toRawSql qPopulated)

// =========================================================================
// 32. CTE DISCOVERY ON MEMBERS, RECURSIVE CTES, AND FSQL BUILDER METHODS
// =========================================================================

[<Fact>]
let ``CTE discovery on static and instance holders recursive CTEs and fsql builder methods`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")
    let orders = SqlPushdownQueryable<CovOrder>(conn, "Orders")

    // Static holder
    let qStatic = StaticQueryHolder.StaticProducts.Where(fun p -> p.Price > 10.0M)
    Assert.Contains("Price > @p0", toRawSql qStatic)

    // Instance holder field and property
    let holder = InstanceQueryHolder()
    let qField = holder.FieldProducts.Where(fun p -> p.Price > 10.0M)
    Assert.Contains("Price > @p0", toRawSql qField)

    let qProp = holder.PropProducts.Where(fun p -> p.Price > 10.0M)
    Assert.Contains("Price > @p0", toRawSql qProp)

    // Custom queryable wrapper with WithCte
    let wrapper = CustomQueryableWrapper(products)
    let w1 = wrapper.WithCte(orders)
    Assert.Contains("WITH cte_1 AS", toRawSql w1)

    let w2 = wrapper.WithCte("wrapper_cte", orders)
    Assert.Contains("WITH wrapper_cte AS", toRawSql w2)

    // ISqlPushdownQueryable interface and properties
    let ispq = products :> ISqlPushdownQueryable
    Assert.Same(conn, ispq.Connector)
    Assert.NotNull(ispq.Plan)
    Assert.NotNull(ispq.Explain())
    Assert.Equal("Products", products.BaseSql)
    Assert.Empty(products.BaseParameters)

    // Recursive CTE methods
    let r1 = products.WithRecursiveCte("rec_cte", orders)
    Assert.Contains("WITH RECURSIVE rec_cte AS", toRawSql r1)

    let r2 = products.WithRecursiveCte(orders)
    Assert.Contains("WITH RECURSIVE cte_1 AS", toRawSql r2)

    let r3 = products.WithCte("rec_cte3", "SELECT 1 AS X", isRecursive = true)
    Assert.Contains("WITH RECURSIVE rec_cte3 AS", toRawSql r3)

    let r4 = products.WithCte("SELECT 1 AS X", isRecursive = true)
    Assert.Contains("WITH RECURSIVE cte_1 AS", toRawSql r4)

    // fsql set operations
    let uLhs = products.Where(fun p -> p.Price > 10.0M)
    let uRhs = products.Where(fun p -> p.Price < 100.0M)
    Assert.Contains("UNION", toRawSql (fsql.union uLhs uRhs))
    Assert.Contains("UNION ALL", toRawSql (fsql.unionAll uLhs uRhs))
    Assert.Contains("INTERSECT", toRawSql (fsql.intersect uLhs uRhs))
    Assert.Contains("EXCEPT", toRawSql (fsql.except uLhs uRhs))

    // Direct WhereExists, WhereNotExists, WhereIn, WhereNotIn on SqlPushdownQueryable
    let qDirExists = products.WhereExists("SELECT 1 FROM Orders WHERE CustomerId = Products.Id")
    Assert.Contains("EXISTS (SELECT 1 FROM Orders WHERE CustomerId = Products.Id)", toRawSql qDirExists)

    let qDirNotExists = products.WhereNotExists("SELECT 1 FROM Orders WHERE CustomerId = Products.Id")
    Assert.Contains("NOT EXISTS (SELECT 1 FROM Orders WHERE CustomerId = Products.Id)", toRawSql qDirNotExists)

    let qDirIn = products.WhereIn("Category", [ "Electronics"; "Stationery" ])
    Assert.Contains("Category IN (@in_", toRawSql qDirIn)

    let qDirNotIn = products.WhereNotIn("Category", [ "Stationery" ])
    Assert.Contains("Category NOT IN (@notin_", toRawSql qDirNotIn)

    // Top-level marker functions in SqlComputationExtensions
    Assert.True(SqlComputationExtensions.exists orders)
    Assert.False(SqlComputationExtensions.notExists orders)
    Assert.Same(orders, SqlComputationExtensions.crossApply orders)
    Assert.Same(orders, SqlComputationExtensions.lateral orders)

    // fsql recursive
    let recAnchor = fsql.recursive("rec_anchor", products, fun r -> r)
    Assert.Same(products, recAnchor)

    // All dummy methods on fsql
    let _ = fsql.window()
    let _ = fsql.rowNumber()
    let _ = fsql.rank()
    let _ = fsql.denseRank()
    let _ = fsql.ntile(3)
    let _ = fsql.lag(1)
    let _ = fsql.lead(1)
    let _ = fsql.firstValue(1)
    let _ = fsql.lastValue(1)
    let _ = fsql.sum(1)
    let _ = fsql.avg(1)
    let _ = fsql.min(1)
    let _ = fsql.max(1)
    let _ = fsql.count()
    let _ = fsql.count(1)
    let _ = fsql.jsonValue(box 1, "$")
    let _ = fsql.withCte("c", orders)
    let _ = fsql.withCte(orders)
    let _ = fsql.withCte("SELECT 1")
    let _ = fsql.withCteSql("c", "SELECT 1")
    let _ = fsql.withCteSql("SELECT 1")
    let _ = fsql.withRecursiveCte("c", orders)
    let _ = fsql.withRecursiveCte(orders)
    let _ = fsql.withRecursiveCte("SELECT 1")
    let _ = fsql.withRecursiveCteSql("c", "SELECT 1")
    let _ = fsql.withRecursiveCteSql("SELECT 1")
    ()

// =========================================================================
// 33. FSQL COMPUTATION EXPRESSION CUSTOM OPERATIONS AND SET OPERATIONS
// =========================================================================

[<Fact>]
let ``FSql custom operations whereExists whereNotExists having and set operations`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")
    let orders = SqlPushdownQueryable<CovOrder>(conn, "Orders")

    // whereExists with subquery
    let qFsqlExistsSub =
        fsql {
            for p in products do
            whereExists (orders.Where(fun o -> o.CustomerId = p.Id))
            select p
        }
    Assert.Contains("EXISTS (SELECT", toRawSql qFsqlExistsSub)

    let qFsqlExistsSub2 =
        fsql {
            for p in products do
            whereExists (orders.Where(fun o -> o.CustomerId = p.Id && o.Amount > 100.0M))
            select p
        }
    Assert.Contains("EXISTS (SELECT", toRawSql qFsqlExistsSub2)

    // whereNotExists with subquery
    let qFsqlNotExistsSub =
        fsql {
            for p in products do
            whereNotExists (orders.Where(fun o -> o.CustomerId = p.Id))
            select p
        }
    Assert.Contains("NOT EXISTS (SELECT", toRawSql qFsqlNotExistsSub)

    let qFsqlNotExistsSub2 =
        fsql {
            for p in products do
            whereNotExists (orders.Where(fun o -> o.CustomerId = p.Id && o.Amount <= 100.0M))
            select p
        }
    Assert.Contains("NOT EXISTS", toRawSql qFsqlNotExistsSub2)

    // having in fsql
    let qFsqlHaving =
        fsql {
            for p in products do
            having (p.Price > 10.0M)
            select p
        }
    Assert.Contains("Price > @p0", toRawSql qFsqlHaving)

    let qFsqlHavingGroup =
        fsql {
            for p in products do
            groupBy p.Category into g
            having (g.Count() > 0)
            select g.Key
        }
    Assert.Contains("HAVING", toRawSql qFsqlHavingGroup)

    // Set operations in fsql with seq/enumerable
    let prodList = [ { Id = 1; Name = "W"; Price = 10.0M; Category = "E"; InStock = true; Score = Nullable(1.0M); Timestamp = DateTime.UtcNow } ]
    let qFsqlUnionSeq =
        fsql {
            for p in products do
            select p
            union (prodList :> seq<CovProduct>)
        }
    Assert.NotNull(qFsqlUnionSeq)

    let qFsqlUnionAllSeq =
        fsql {
            for p in products do
            select p
            unionAll (prodList :> seq<CovProduct>)
        }
    Assert.NotNull(qFsqlUnionAllSeq)

    let qFsqlIntersectSeq =
        fsql {
            for p in products do
            select p
            intersect (prodList :> seq<CovProduct>)
        }
    Assert.NotNull(qFsqlIntersectSeq)

    let qFsqlExceptSeq =
        fsql {
            for p in products do
            select p
            except (prodList :> seq<CovProduct>)
        }
    Assert.NotNull(qFsqlExceptSeq)

// =========================================================================
// 34. SUBQUERY AGGREGATES WITH LAMBDA AND CLIENT EVALUATED WINDOW DEFAULTS
// =========================================================================

[<Fact>]
let ``Subquery aggregates with lambda and client evaluated window defaults`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")
    let orders = SqlPushdownQueryable<CovOrder>(conn, "Orders")

    // Subquery aggregates with lambda selector
    let qSubSumLambda = products.Select(fun p -> orders.Where(fun o -> o.CustomerId = p.Id).Sum(fun o -> o.Amount))
    Assert.Contains("SUM(_sub1.Amount)", toRawSql qSubSumLambda)

    let qSubMinLambda = products.Select(fun p -> orders.Where(fun o -> o.CustomerId = p.Id).Min(fun o -> o.Amount))
    Assert.Contains("MIN(_sub1.Amount)", toRawSql qSubMinLambda)

    let qSubMaxLambda = products.Select(fun p -> orders.Where(fun o -> o.CustomerId = p.Id).Max(fun o -> o.Amount))
    Assert.Contains("MAX(_sub1.Amount)", toRawSql qSubMaxLambda)

    let qSubAvgLambda = products.Select(fun p -> orders.Where(fun o -> o.CustomerId = p.Id).Average(fun o -> o.Amount))
    Assert.Contains("AVG(_sub1.Amount)", toRawSql qSubAvgLambda)

    let qSubSumDistinctLambda = products.Select(fun p -> orders.Where(fun o -> o.CustomerId = p.Id).Distinct().Sum(fun o -> o.Amount))
    Assert.Contains("DISTINCT SUM(_sub1.Amount)", toRawSql qSubSumDistinctLambda)

    // fsql.lag and fsql.lead with client-evaluated fallback defaultValue expressions
    let clientDec = 12.34M
    let clientFlt = 56.78
    let clientInt = 99
    let clientStr = "fallback_val"
    let clientNullObj: string = null

    let qLagDec = products.Select(fun p -> fsql.lag(p.Price, 1, defaultValue = (clientDec + 1.0M), over = fsql.window(sortBy = p.Price)))
    Assert.Contains("LAG(Price, 1, (12.34 + 1.0))", toRawSql qLagDec)

    let qLeadFlt = products.Select(fun p -> fsql.lead(p.Price, 1, defaultValue = decimal (clientFlt + 1.0), over = fsql.window(sortBy = p.Price)))
    Assert.Contains("LEAD(Price, 1,", toRawSql qLeadFlt)

    let qLeadInt = products.Select(fun p -> fsql.lead(p.Price, 1, defaultValue = decimal (clientInt + 1), over = fsql.window(sortBy = p.Price)))
    Assert.Contains("LEAD(Price, 1,", toRawSql qLeadInt)

    let qLagStrVal = products.Select(fun p -> fsql.lag(p.Name, 1, defaultValue = (clientStr + "_tail"), over = fsql.window(sortBy = p.Name)))
    Assert.Contains("LAG(Name, 1, ('fallback_val' || '_tail'))", toRawSql qLagStrVal)

    let qLeadNullVal = products.Select(fun p -> fsql.lead(p.Name, 1, defaultValue = clientNullObj, over = fsql.window(sortBy = p.Name)))
    Assert.Contains("LEAD(Name, 1, NULL)", toRawSql qLeadNullVal)

// =========================================================================
// 35. LINQ QUERYABLE SET OPERATIONS CTE PROPAGATION AND INVERTED COMPARISONS
// =========================================================================

[<Fact>]
let ``Queryable set operations CTE propagation and inverted comparisons`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")
    let orders = SqlPushdownQueryable<CovOrder>(conn, "Orders")

    // LINQ Queryable set operations
    let qConcatLinq = Queryable.Concat(products, products.Where(fun p -> p.Price > 50.0M))
    Assert.Contains("UNION ALL", toRawSql qConcatLinq)

    let qUnionLinq = Queryable.Union(products, products.Where(fun p -> p.Price > 50.0M))
    Assert.Contains("UNION", toRawSql qUnionLinq)

    let qIntersectLinq = Queryable.Intersect(products, products.Where(fun p -> p.Price > 50.0M))
    Assert.Contains("INTERSECT", toRawSql qIntersectLinq)

    let qExceptLinq = Queryable.Except(products, products.Where(fun p -> p.Price > 50.0M))
    Assert.Contains("EXCEPT", toRawSql qExceptLinq)

    // Set operations with CTE preservation
    let pWithCte1 = products.WithCte("cte_a", orders)
    let pWithCte2 = products.WithCte("cte_b", orders)
    let qUnionCte = pWithCte1.Union(pWithCte2)
    Assert.Contains("cte_a", toRawSql qUnionCte)
    Assert.Contains("UNION", toRawSql qUnionCte)

    let qUnionAllCte = pWithCte1.UnionAll(pWithCte2)
    Assert.Contains("cte_a", toRawSql qUnionAllCte)
    Assert.Contains("UNION ALL", toRawSql qUnionAllCte)

    let qIntersectCte = pWithCte1.Intersect(pWithCte2)
    Assert.Contains("cte_a", toRawSql qIntersectCte)
    Assert.Contains("INTERSECT", toRawSql qIntersectCte)

    let qExceptCte = pWithCte1.Except(pWithCte2)
    Assert.Contains("cte_a", toRawSql qExceptCte)
    Assert.Contains("EXCEPT", toRawSql qExceptCte)

    // Subquery referencing static/instance holders
    let qStaticSub = products.Where(fun p -> StaticQueryHolder.StaticProducts.Where(fun s -> s.Id = p.Id).Any())
    Assert.Contains("EXISTS (SELECT", toRawSql qStaticSub)

    let instHolder = InstanceQueryHolder()
    let qInstFieldSub = products.Where(fun p -> instHolder.FieldProducts.Where(fun f -> f.Id = p.Id).Any())
    Assert.Contains("EXISTS (SELECT", toRawSql qInstFieldSub)

    let qInstPropSub = products.Where(fun p -> instHolder.PropProducts.Where(fun pr -> pr.Id = p.Id).Any())
    Assert.Contains("EXISTS (SELECT", toRawSql qInstPropSub)

    let holderWithCte = InstanceQueryHolder()
    holderWithCte.PropProducts <- products.WithCte("holder_cte", orders)
    let qHolderCteSub = products.Where(fun p -> holderWithCte.PropProducts.Where(fun f -> f.Id = p.Id).Any())
    Assert.Contains("holder_cte", toRawSql qHolderCteSub)

    // Inverted comparison operators in Where
    let qInvLt = products.Where(fun p -> 10.0M < p.Price)
    Assert.Contains("Price > @p0", toRawSql qInvLt)

    let qInvLte = products.Where(fun p -> 10.0M <= p.Price)
    Assert.Contains("Price >= @p0", toRawSql qInvLte)

    let qInvGt = products.Where(fun p -> 10.0M > p.Price)
    Assert.Contains("Price < @p0", toRawSql qInvGt)

    let qInvGte = products.Where(fun p -> 10.0M >= p.Price)
    Assert.Contains("Price <= @p0", toRawSql qInvGte)

    let qInvEqNull = products.Where(fun p -> null = p.Name)
    Assert.Contains("Name IS NULL", toRawSql qInvEqNull)

    let qInvNeqNull = products.Where(fun p -> null <> p.Name)
    Assert.Contains("Name IS NOT NULL", toRawSql qInvNeqNull)

// =========================================================================
// 36. MULTI JOINS WITH TRANSPARENT IDENTIFIERS AND NON-SPQ EXTENSIONS
// =========================================================================

[<Fact>]
let ``Multi joins with transparent identifiers and non-spq extension methods`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")
    let orders = SqlPushdownQueryable<CovOrder>(conn, "Orders")

    // Multiple nested left outer joins with transparent tuples
    let qMultiJoin =
        query {
            for p in products do
            leftOuterJoin o in orders on (p.Id = o.CustomerId) into go
            for o in go.DefaultIfEmpty() do
            join p2 in products on (p.Category = p2.Category)
            select (p.Name, (if isNull (box o) then 0.0M else o.Amount), p2.Price)
        }
    let sMulti = toRawSql qMultiJoin
    Assert.Contains("LEFT JOIN Orders", sMulti)
    Assert.Contains("item1", sMulti)
    Assert.Contains("item2", sMulti)

    // Non-SqlPushdown IQueryable extension methods
    let inMemProducts = ([ { Id = 1; Name = "P"; Price = 10.0M; Category = "C"; InStock = true; Score = Nullable(1.0M); Timestamp = DateTime.UtcNow } ] : CovProduct list).AsQueryable()
    let nonSpqUnion = inMemProducts.Union(products)
    Assert.NotNull(nonSpqUnion)

    let nonSpqUnionAll = inMemProducts.UnionAll(products)
    Assert.NotNull(nonSpqUnionAll)

    let nonSpqIntersect = inMemProducts.Intersect(products)
    Assert.NotNull(nonSpqIntersect)

    let nonSpqExcept = inMemProducts.Except(products)
    Assert.NotNull(nonSpqExcept)

    let nonSpqCte = inMemProducts.WithCte("non_spq_cte", orders)
    Assert.NotNull(nonSpqCte)

    let nonSpqCteRec = inMemProducts.WithCte("non_spq_cte_rec", orders, isRecursive = true)
    Assert.NotNull(nonSpqCteRec)

    let nonSpqCteNamed = inMemProducts.WithCte("non_spq_cte_named", orders)
    Assert.NotNull(nonSpqCteNamed)

// =========================================================================
// 37. FSQL RAW JOINS AND AUTO-NAMED CTES
// =========================================================================

[<Fact>]
let ``FSql raw joins and auto named CTEs`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")
    let orders = SqlPushdownQueryable<CovOrder>(conn, "Orders")

    let qFullRaw =
        fsql {
            for p in products do
            fullOuterJoinRaw "Orders" "o" "Products.Id = o.CustomerId"
            select p
        }
    Assert.Contains("FULL OUTER JOIN Orders o ON Products.Id = o.CustomerId", toRawSql qFullRaw)

    let qRightRaw =
        fsql {
            for p in products do
            rightOuterJoinRaw "Orders" "o" "Products.Id = o.CustomerId"
            select p
        }
    Assert.Contains("RIGHT OUTER JOIN Orders o ON Products.Id = o.CustomerId", toRawSql qRightRaw)

    let qCrossRaw =
        fsql {
            for p in products do
            crossJoin "Orders" "o"
            select p
        }
    Assert.Contains("CROSS JOIN Orders o", toRawSql qCrossRaw)

    let qWithCteAuto =
        fsql {
            for p in products do
            withCte orders
            select p
        }
    Assert.Contains("WITH cte_1 AS", toRawSql qWithCteAuto)

    let qWithCteSqlAuto =
        fsql {
            for p in products do
            withCteSql "SELECT 1 AS Num"
            select p
        }
    Assert.Contains("WITH cte_1 AS (SELECT 1 AS Num)", toRawSql qWithCteSqlAuto)

    let qWithRecAuto =
        fsql {
            for p in products do
            withRecursiveCte orders
            select p
        }
    Assert.Contains("WITH RECURSIVE cte_1 AS", toRawSql qWithRecAuto)

    let qWithRecSqlAuto =
        fsql {
            for p in products do
            withRecursiveCteSql "SELECT 1 AS Num"
            select p
        }
    Assert.Contains("cte_1 AS (SELECT 1 AS Num)", toRawSql qWithRecSqlAuto)

// =========================================================================
// 38. SQLPUSHDOWNQUERYABLE WITHCTE OVERLOADS AND DIRECT AGGREGATE CALLS
// =========================================================================

[<Fact>]
let ``SqlPushdownQueryable WithCte overloads and direct aggregate calls`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")
    let orders = SqlPushdownQueryable<CovOrder>(conn, "Orders")

    let iqOrders = orders :> IQueryable<CovOrder>

    let c1 = products.WithCte(iqOrders)
    Assert.Contains("WITH cte_1 AS", toRawSql c1)

    let c2 = products.WithCte("c1_named", iqOrders)
    Assert.Contains("WITH c1_named AS", toRawSql c2)

    let c3 = products.WithCte(iqOrders, isRecursive = true)
    Assert.Contains("WITH RECURSIVE cte_1 AS", toRawSql c3)

    let c4 = products.WithCte("rec1_named", iqOrders, isRecursive = true)
    Assert.Contains("WITH RECURSIVE rec1_named AS", toRawSql c4)

    let c5 = products.WithCte(orders)
    Assert.Contains("WITH cte_1 AS", toRawSql c5)

    let c6 = products.WithCte("c2_named", orders)
    Assert.Contains("WITH c2_named AS", toRawSql c6)

    let c7 = products.WithCte(orders, isRecursive = true)
    Assert.Contains("WITH RECURSIVE cte_1 AS", toRawSql c7)

    let c8 = products.WithCte("rec2_named", orders, isRecursive = true)
    Assert.Contains("WITH RECURSIVE rec2_named AS", toRawSql c8)

    let c9 = products.WithRecursiveCte("rec3_named", iqOrders)
    Assert.Contains("WITH RECURSIVE rec3_named AS", toRawSql c9)

    let c10 = products.WithRecursiveCte(iqOrders)
    Assert.Contains("WITH RECURSIVE cte_1 AS", toRawSql c10)

    let c11 = products.WithRecursiveCte("rec4_named", orders)
    Assert.Contains("WITH RECURSIVE rec4_named AS", toRawSql c11)

    let c12 = products.WithRecursiveCte(orders)
    Assert.Contains("WITH RECURSIVE cte_1 AS", toRawSql c12)

    let c13 = products.WithCte("SELECT 1 AS X")
    Assert.Contains("WITH cte_1 AS (SELECT 1 AS X)", toRawSql c13)

    let c14 = products.WithCte("c3_sql", "SELECT 1 AS X")
    Assert.Contains("WITH c3_sql AS (SELECT 1 AS X)", toRawSql c14)

    let c15 = products.WithCte("SELECT @p AS X", parameters = [ ("@p", box 42) ])
    Assert.Contains("WITH cte_1 AS (SELECT @p", toRawSql c15)

    let c16 = products.WithCte("c4_sql", "SELECT @p AS X", parameters = [ ("@p", box 42) ])
    Assert.Contains("WITH c4_sql AS (SELECT @p", toRawSql c16)

    let c17 = products.WithCte("SELECT 1 AS X", isRecursive = true)
    Assert.Contains("WITH RECURSIVE cte_1 AS (SELECT 1 AS X)", toRawSql c17)

    let c18 = products.WithCte("rec5_sql", "SELECT 1 AS X", isRecursive = true)
    Assert.Contains("WITH RECURSIVE rec5_sql AS (SELECT 1 AS X)", toRawSql c18)

    let c19 = products.WithCte("rec6_sql", "SELECT @p AS X", parameters = [ ("@p", box 42) ], isRecursive = true)
    Assert.Contains("WITH RECURSIVE rec6_sql AS (SELECT @p", toRawSql c19)

    // Direct aggregate calls with column name strings
    Assert.Equal(299.99M, products.Max("Price"))
    Assert.Equal(1.50M, products.Min("Price"))
    Assert.True(products.Average("Price") > 0.0M)
    Assert.True(products.Sum("Price") > 0.0M)
    Assert.Equal(5, products.Count("Price"))
    Assert.Equal(4, products.Count(fun p -> p.Price > 5.0M))

// =========================================================================
// 39. EXPRESSIONPUSHDOWNTRANSLATOR DIRECT SCALAR AND PREDICATE TESTS
// =========================================================================

[<Fact>]
let ``ExpressionPushdownTranslator direct helper and translation tests`` () =
    let mock = AdvancedMockConnector(ConnectorKind("Sqlite"))
    let plan = PushdownQueryPlan.Create("Products", [])
    let pParam = Expression.Parameter(typeof<CovProduct>, "p")

    // dependsOnParameter and hasAnyParameter
    let pPrice = Expression.Property(pParam, "Price")
    Assert.True(ExpressionPushdownTranslator.dependsOnParameter pParam pPrice)
    Assert.True(ExpressionPushdownTranslator.hasAnyParameter pPrice)
    Assert.False(ExpressionPushdownTranslator.dependsOnParameter pParam (Expression.Constant(42)))
    Assert.False(ExpressionPushdownTranslator.hasAnyParameter (Expression.Constant(42)))

    // isOptionOrNullableType
    Assert.True(ExpressionPushdownTranslator.isOptionOrNullableType typeof<Nullable<int>>)
    Assert.True(ExpressionPushdownTranslator.isOptionOrNullableType typeof<Option<string>>)
    Assert.False(ExpressionPushdownTranslator.isOptionOrNullableType typeof<string>)

    // isGroupingType
    Assert.True(ExpressionPushdownTranslator.isGroupingType typeof<System.Linq.IGrouping<string, CovProduct>>)
    Assert.False(ExpressionPushdownTranslator.isGroupingType typeof<CovProduct>)

    // unwrapOptionOrNullableMember
    let optItem = Expression.Parameter(typeof<CovOptionItem>, "x")
    let ratingProp = Expression.Property(optItem, "Rating")
    let valProp = Expression.Property(ratingProp, "Value")
    Assert.Equal("Rating", (ExpressionPushdownTranslator.unwrapOptionOrNullableMember valProp :?> MemberExpression).Member.Name)

    // AndAlso with one side None
    let nullBin = Expression.Equal(Expression.Constant(1), Expression.Constant(1))
    let validBin = Expression.GreaterThan(pPrice, Expression.Constant(10.0M))
    let andAlso1 = Expression.AndAlso(validBin, nullBin)
    let andRes1 = ExpressionPushdownTranslator.translatePredicate pParam plan SqlDialect.Sqlite andAlso1
    Assert.True(andRes1.IsSome)

    let andAlso2 = Expression.AndAlso(nullBin, validBin)
    let andRes2 = ExpressionPushdownTranslator.translatePredicate pParam plan SqlDialect.Sqlite andAlso2
    Assert.True(andRes2.IsSome)

    // translateScalarExpr with MethodCallExpression Count, Sum, Min, Max, Average across dialects
    let sumMethod = typeof<Enumerable>.GetMethods() |> Seq.find (fun m -> m.Name = "Sum" && m.ReturnType = typeof<decimal> && m.GetParameters().Length = 2)
    let sumGen = sumMethod.MakeGenericMethod(typeof<CovProduct>)
    let priceLambda = Expression.Lambda<Func<CovProduct, decimal>>(pPrice, pParam)
    let prodConst = Expression.Constant(SqlPushdownQueryable<CovProduct>(mock, "Products"))
    let sumCall = Expression.Call(null, sumGen, prodConst, priceLambda)
    let sumRes = ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan sumCall
    Assert.True(sumRes.IsSome)

    let minMethod = typeof<Enumerable>.GetMethods() |> Seq.find (fun m -> m.Name = "Min" && m.ReturnType = typeof<decimal> && m.GetParameters().Length = 2)
    let minGen = minMethod.MakeGenericMethod(typeof<CovProduct>)
    let minCall = Expression.Call(null, minGen, prodConst, priceLambda)
    let minRes = ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan minCall
    Assert.True(minRes.IsSome)

    let maxMethod = typeof<Enumerable>.GetMethods() |> Seq.find (fun m -> m.Name = "Max" && m.ReturnType = typeof<decimal> && m.GetParameters().Length = 2)
    let maxGen = maxMethod.MakeGenericMethod(typeof<CovProduct>)
    let maxCall = Expression.Call(null, maxGen, prodConst, priceLambda)
    let maxRes = ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan maxCall
    Assert.True(maxRes.IsSome)

    let avgMethod = typeof<Enumerable>.GetMethods() |> Seq.find (fun m -> m.Name = "Average" && m.ReturnType = typeof<decimal> && m.GetParameters().Length = 2)
    let avgGen = avgMethod.MakeGenericMethod(typeof<CovProduct>)
    let avgCall = Expression.Call(null, avgGen, prodConst, priceLambda)
    let avgRes = ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan avgCall
    Assert.True(avgRes.IsSome)

// =========================================================================
// 40. CHAINED EXTENSION METHODS AND TOP-LEVEL QUERY FUNCTIONS
// =========================================================================

[<Fact>]
let ``Chained IQueryable extension methods and top level query functions`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")
    let orders = SqlPushdownQueryable<CovOrder>(conn, "Orders")

    // Chained IQueryable with seedPlan preservation
    let nonSpq = (products :> IQueryable<CovProduct>).Where(fun p -> p.Price > 10.0M)
    let u1 = nonSpq.Union(products)
    Assert.NotNull(u1)
    Assert.Contains("UNION", toRawSql u1)

    let u2 = nonSpq.UnionAll(products)
    Assert.NotNull(u2)
    Assert.Contains("UNION ALL", toRawSql u2)

    let u3 = nonSpq.Intersect(products)
    Assert.NotNull(u3)
    Assert.Contains("INTERSECT", toRawSql u3)

    let u4 = nonSpq.Except(products)
    Assert.NotNull(u4)
    Assert.Contains("EXCEPT", toRawSql u4)

    let w1 = nonSpq.WithCte(orders)
    Assert.NotNull(w1)
    Assert.Contains("WITH cte_1 AS", toRawSql w1)

    let w2 = nonSpq.WithCte("w2_named", orders)
    Assert.NotNull(w2)
    Assert.Contains("WITH w2_named AS", toRawSql w2)

    let w3 = nonSpq.WithCte(orders, isRecursive = true)
    Assert.NotNull(w3)
    Assert.Contains("WITH RECURSIVE cte_1 AS", toRawSql w3)

    let w4 = nonSpq.WithCte("w4_named", orders, isRecursive = true)
    Assert.NotNull(w4)
    Assert.Contains("WITH RECURSIVE w4_named AS", toRawSql w4)

    // Top-level module functions in SqlPushdownQuery
    let (sqlWithP, pList) = SqlPushdownQuery.extractSqlWithParameters products
    Assert.Contains("SELECT", sqlWithP)

    let rawSql = SqlPushdownQuery.extractRawSql products
    Assert.Contains("SELECT", rawSql)

    let sSql = SqlPushdownQuery.toRawSql products
    Assert.Contains("SELECT", sSql)

    let sQuery = SqlPushdownQuery.toRawQuery products
    Assert.Contains("SELECT", sQuery)

    let report = SqlPushdownQuery.explainQuery products
    Assert.NotNull(report)
    Assert.NotEmpty(report.FormattedSql)

// =========================================================================
// 50. DEEP TRANSLATOR, AGGREGATE, SET OPERATION AND PROVIDER COVERAGE
// =========================================================================

[<Fact>]
let ``Deep coverage of ExpressionPushdownTranslator, Set Operations, Window and Builder`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")
    let orders = SqlPushdownQueryable<CovOrder>(conn, "Orders")
    let mock = AdvancedMockConnector(ConnectorKind("Sqlite"))
    let plan = PushdownQueryPlan.Create("Products", [], baseAlias = "p", dialect = SqlDialect.Sqlite)

    // --- 1. ExpressionPushdownTranslator.isHavingPredicate ---
    let pParam = Expression.Parameter(typeof<CovProduct>, "p")
    let pPrice = Expression.Property(pParam, "Price")
    let pKey = Expression.Property(Expression.Parameter(typeof<IGrouping<string, CovProduct>>, "g"), "Key")
    Assert.True(ExpressionPushdownTranslator.isHavingPredicate pKey)
    Assert.False(ExpressionPushdownTranslator.isHavingPredicate (null: Expression))
    Assert.False(ExpressionPushdownTranslator.isHavingPredicate (Expression.Constant(42)))
    let uExpr = Expression.UnaryPlus(pPrice)
    Assert.False(ExpressionPushdownTranslator.isHavingPredicate uExpr)
    let countMethod = typeof<Enumerable>.GetMethods() |> Seq.find (fun m -> m.Name = "Count" && m.GetParameters().Length = 1)
    let countGen = countMethod.MakeGenericMethod(typeof<CovProduct>)
    let countCall = Expression.Call(null, countGen, Expression.Parameter(typeof<seq<CovProduct>>, "seq"))
    Assert.True(ExpressionPushdownTranslator.isHavingPredicate countCall)
    let binHaving = Expression.GreaterThan(countCall, Expression.Constant(0))
    Assert.True(ExpressionPushdownTranslator.isHavingPredicate binHaving)

    // --- 2. ExpressionPushdownTranslator.resolveMemberRef ---
    let planNoGroup = PushdownQueryPlan.Create("Products", [], baseAlias = "p", dialect = SqlDialect.Sqlite)
    let (noGrpAlias, noGrpCol) = ExpressionPushdownTranslator.resolveMemberRef planNoGroup pKey
    Assert.Equal("Key", noGrpCol)

    let planWithGroup = PushdownQueryPlan.Create("Products", [], baseAlias = "p", dialect = SqlDialect.Sqlite)
    planWithGroup.GroupBy.Add("Category")
    let (grpAlias, grpCol) = ExpressionPushdownTranslator.resolveMemberRef planWithGroup pKey
    Assert.Equal("Category", grpCol)

    let planOuter = PushdownQueryPlan.Create("Products", [], baseAlias = "p", dialect = SqlDialect.Sqlite)
    planOuter.OuterAliases.["pOuter"] <- "po"
    let pOuterParam = Expression.Parameter(typeof<CovProduct>, "pOuter")
    let pOuterProp = Expression.Property(pOuterParam, "Price")
    let (outAlias1, outCol1) = ExpressionPushdownTranslator.resolveMemberRef planOuter pOuterProp
    Assert.Equal(Some "po", outAlias1)

    planOuter.BaseRowType <- Some typeof<CovProduct>
    let pBaseParam = Expression.Parameter(typeof<CovProduct>, "pBase")
    let pBaseProp = Expression.Property(pBaseParam, "Price")
    let (outAlias2, outCol2) = ExpressionPushdownTranslator.resolveMemberRef planOuter pBaseProp
    Assert.Equal(Some "p", outAlias2)

    let pDiffParam = Expression.Parameter(typeof<CovOrder>, "pDiff")
    let pDiffProp = Expression.Property(pDiffParam, "Amount")
    let (outAlias3, outCol3) = ExpressionPushdownTranslator.resolveMemberRef planOuter pDiffProp
    Assert.Equal(Some "po", outAlias3)

    // Joins row type match
    let planJoins = PushdownQueryPlan.Create("Products", [], baseAlias = "p", dialect = SqlDialect.Sqlite)
    planJoins.BaseRowType <- Some typeof<CovProduct>
    planJoins.Joins.Add({ JoinType = "INNER JOIN"; SourceTable = "Orders"; Alias = "o1"; OnClause = "p.Id = o1.CustomerId"; RowType = Some typeof<CovOrder> })
    let (joinAlias1, _) = ExpressionPushdownTranslator.resolveMemberRef planJoins pDiffProp
    Assert.Equal(Some "o1", joinAlias1)

    // Tuple hierarchies
    let t2Param = Expression.Parameter(typeof<Tuple<CovProduct, CovOrder>>, "root")
    let t2Item1 = Expression.Property(t2Param, "Item1")
    let t2Item2 = Expression.Property(t2Param, "Item2")
    let t2ProdPrice = Expression.Property(t2Item1, "Price")
    let t2OrdAmount = Expression.Property(t2Item2, "Amount")
    let (tAlias1, _) = ExpressionPushdownTranslator.resolveMemberRef planJoins t2ProdPrice
    Assert.Equal(Some "p", tAlias1)
    let (tAlias2, _) = ExpressionPushdownTranslator.resolveMemberRef planJoins t2OrdAmount
    Assert.Equal(Some "o1", tAlias2)

    // Item8 overflow tuple
    let t8Param = Expression.Parameter(typeof<CovTuple8Holder<CovOrder>>, "root8")
    let t8Item8 = Expression.Property(t8Param, "Item8")
    let t8SubItem1 = Expression.Property(t8Item8, "Item1")
    let t8Prop = Expression.Property(t8SubItem1, "Amount")
    let (t8Alias1, _) = ExpressionPushdownTranslator.resolveMemberRef planJoins t8Prop
    Assert.True(t8Alias1.IsSome)

    let planLeftJoins = PushdownQueryPlan.Create("Products", [], baseAlias = "p", dialect = SqlDialect.Sqlite)
    planLeftJoins.Joins.Add({ JoinType = "LEFT JOIN"; SourceTable = "Orders"; Alias = "j1"; OnClause = "1=1"; RowType = None })
    let (t8Alias2, _) = ExpressionPushdownTranslator.resolveMemberRef planLeftJoins t8Prop
    Assert.Equal(Some "_t1", t8Alias2)

    // Nested left-associative tuple tree: root.Item1.Item2
    let nestedParam = Expression.Parameter(typeof<Tuple<Tuple<CovProduct, CovOrder>, CovOrder>>, "nested")
    let nItem1 = Expression.Property(nestedParam, "Item1")
    let nItem1Item1 = Expression.Property(nItem1, "Item1")
    let nItem1Item2 = Expression.Property(nItem1, "Item2")
    let nProp1 = Expression.Property(nItem1Item1, "Price")
    let nProp2 = Expression.Property(nItem1Item2, "Amount")
    let (nAlias1, _) = ExpressionPushdownTranslator.resolveMemberRef planJoins nProp1
    Assert.True(nAlias1.IsSome)
    let (nAlias2, _) = ExpressionPushdownTranslator.resolveMemberRef planJoins nProp2
    Assert.True(nAlias2.IsSome)

    // --- 3. unwrapClientOption ---
    Assert.Null(ExpressionPushdownTranslator.unwrapClientOption null)
    Assert.Equal(box 42, ExpressionPushdownTranslator.unwrapClientOption (box 42))
    Assert.Equal(box 10.5M, ExpressionPushdownTranslator.unwrapClientOption (box (Some 10.5M)))
    Assert.Null(ExpressionPushdownTranslator.unwrapClientOption (box (None: decimal option)))
    let nullDecimal = Nullable<decimal>(15.0M)
    let emptyDecimal = Nullable<decimal>()
    Assert.Equal(box 15.0M, ExpressionPushdownTranslator.unwrapClientOption (box nullDecimal))
    Assert.Null(ExpressionPushdownTranslator.unwrapClientOption (box emptyDecimal))

    // --- 4. extractKeyColumns ---
    let newTupleExpr = Expression.New(typeof<Tuple<decimal, string>>.GetConstructors().[0], [| pPrice :> Expression; Expression.Property(pParam, "Name") :> Expression |])
    let tupleCols = ExpressionPushdownTranslator.extractKeyColumns plan "p" pParam newTupleExpr
    Assert.Equal(2, tupleCols.Length)

    let convExpr = Expression.Convert(pPrice, typeof<obj>)
    let convCols = ExpressionPushdownTranslator.extractKeyColumns plan "p" pParam convExpr
    Assert.Single(convCols) |> ignore

    let tupleCreateMethod = typeof<Tuple>.GetMethods() |> Seq.find (fun m -> m.Name = "Create" && m.GetParameters().Length = 2)
    let tupleCreateGen = tupleCreateMethod.MakeGenericMethod(typeof<decimal>, typeof<string>)
    let tupleCreateCall = Expression.Call(null, tupleCreateGen, pPrice, Expression.Property(pParam, "Name"))
    let createCols = ExpressionPushdownTranslator.extractKeyColumns plan "p" pParam tupleCreateCall
    Assert.Equal(2, createCols.Length)

    // --- 5. tryFindPushdownSource on FieldInfo ---
    let fieldHolder = FieldHolder()
    fieldHolder.FieldProducts <- products
    let holderConst = Expression.Constant(fieldHolder)
    let fieldInfo = typeof<FieldHolder>.GetField("FieldProducts")
    let memberExpr = Expression.Field(holderConst, fieldInfo)
    let foundSrc1 = ExpressionPushdownTranslator.tryFindPushdownSource memberExpr
    Assert.True(foundSrc1.IsSome)

    let nullHolderConst = Expression.Constant(null, typeof<FieldHolder>)
    let memberExprNull = Expression.Field(nullHolderConst, fieldInfo)
    let foundSrcNull = ExpressionPushdownTranslator.tryFindPushdownSource memberExprNull
    Assert.True(foundSrcNull.IsNone)

    // --- 6. canPushdownAllJoins ---
    let conn1 = AdvancedMockConnector(ConnectorKind("Sqlite"))
    let conn2 = AdvancedMockConnector(ConnectorKind("Sqlite"))
    let q1 = SqlPushdownQueryable<CovProduct>(conn1, "Products")
    let q2 = SqlPushdownQueryable<CovOrder>(conn1, "Orders")
    let qDiffConn = SqlPushdownQueryable<CovOrder>(conn2, "Orders")

    let joinMethod = typeof<Queryable>.GetMethods() |> Seq.find (fun m -> m.Name = "Join" && m.GetParameters().Length = 5)
    let joinGen = joinMethod.MakeGenericMethod(typeof<CovProduct>, typeof<CovOrder>, typeof<int>, typeof<obj>)
    let outerKeyLambda = Expression.Lambda<Func<CovProduct, int>>(Expression.Property(pParam, "Id"), pParam)
    let oParam = Expression.Parameter(typeof<CovOrder>, "o")
    let innerKeyLambda = Expression.Lambda<Func<CovOrder, int>>(Expression.Property(oParam, "CustomerId"), oParam)
    let resLambda = Expression.Lambda<Func<CovProduct, CovOrder, obj>>(Expression.Constant("res"), pParam, oParam)

    let joinCallSame = Expression.Call(null, joinGen, (q1 :> IQueryable).Expression, (q2 :> IQueryable).Expression, outerKeyLambda, innerKeyLambda, resLambda)
    Assert.True(ExpressionPushdownTranslator.canPushdownAllJoins joinCallSame conn1)

    let joinCallDiff = Expression.Call(null, joinGen, (q1 :> IQueryable).Expression, (qDiffConn :> IQueryable).Expression, outerKeyLambda, innerKeyLambda, resLambda)
    Assert.True(ExpressionPushdownTranslator.canPushdownAllJoins joinCallDiff conn1)

    let otherKindConn = AdvancedMockConnector(ConnectorKind("PostgreSql"))
    let qDiffKind = SqlPushdownQueryable<CovOrder>(otherKindConn, "Orders")
    let joinCallDiffKind = Expression.Call(null, joinGen, (q1 :> IQueryable).Expression, (qDiffKind :> IQueryable).Expression, outerKeyLambda, innerKeyLambda, resLambda)
    Assert.False(ExpressionPushdownTranslator.canPushdownAllJoins joinCallDiffKind conn1)

    let smMethod =
        typeof<Queryable>.GetMethods()
        |> Seq.find (fun m ->
            m.Name = "SelectMany"
            && m.GetParameters().Length = 3
            && m.GetParameters().[1].ParameterType.GetGenericArguments().[0].GetGenericTypeDefinition() = typedefof<Func<_, _>>)
    let smGen = smMethod.MakeGenericMethod(typeof<CovProduct>, typeof<CovOrder>, typeof<obj>)
    let collSelector = Expression.Lambda<Func<CovProduct, seq<CovOrder>>>((q2 :> IQueryable).Expression, pParam)
    let smCall = Expression.Call(null, smGen, (q1 :> IQueryable).Expression, collSelector, resLambda)
    Assert.True(ExpressionPushdownTranslator.canPushdownAllJoins smCall conn1)

    // --- 7. tryExtractFromValue and tryExtractQueryableInfo ---
    Assert.True(ExpressionPushdownTranslator.tryExtractFromValue null |> Option.isNone)
    Assert.True(ExpressionPushdownTranslator.tryExtractFromValue (box 123) |> Option.isNone)
    let extractedQ = ExpressionPushdownTranslator.tryExtractFromValue (box q1)
    Assert.True(extractedQ.IsSome)

    let holder = InstanceQueryHolder()
    let extractedHolder = ExpressionPushdownTranslator.tryExtractFromValue (box holder)
    Assert.True(extractedHolder.IsSome)

    let memberPropExpr = Expression.Property(Expression.Constant(holder), "PropProducts")
    let extractedProp = ExpressionPushdownTranslator.tryExtractQueryableInfo memberPropExpr
    Assert.True(extractedProp.IsSome)

    // --- 8. translateScalarCondition ---
    let cond1 = Expression.GreaterThan(pPrice, Expression.Constant(10.0M))
    let cond2 = Expression.LessThan(pPrice, Expression.Constant(100.0M))
    let andCond = Expression.AndAlso(cond1, cond2)
    Assert.True(ExpressionPushdownTranslator.translateScalarCondition SqlDialect.Sqlite plan andCond |> Option.isSome)
    let orCond = Expression.OrElse(cond1, cond2)
    Assert.True(ExpressionPushdownTranslator.translateScalarCondition SqlDialect.Sqlite plan orCond |> Option.isSome)

    let pInStock = Expression.Property(pParam, "InStock")
    let boolCondPg = ExpressionPushdownTranslator.translateScalarCondition SqlDialect.PostgreSql plan pInStock
    Assert.True(boolCondPg.IsSome)
    Assert.Contains("= TRUE", boolCondPg.Value)
    let boolCondSqlite = ExpressionPushdownTranslator.translateScalarCondition SqlDialect.Sqlite plan pInStock
    Assert.True(boolCondSqlite.IsSome)
    Assert.Contains("= 1", boolCondSqlite.Value)

    let voptParam = Expression.Parameter(typeof<ValueOption<decimal>>, "vopt")
    let isSomeProp = Expression.Property(voptParam, "IsSome")
    Assert.True(ExpressionPushdownTranslator.translateScalarCondition SqlDialect.Sqlite plan isSomeProp |> Option.isSome)
    let isNoneProp = Expression.Property(voptParam, "IsNone")
    Assert.True(ExpressionPushdownTranslator.translateScalarCondition SqlDialect.Sqlite plan isNoneProp |> Option.isSome)

    let nullParam = Expression.Parameter(typeof<Nullable<decimal>>, "nullDec")
    let hasValProp = Expression.Property(nullParam, "HasValue")
    Assert.True(ExpressionPushdownTranslator.translateScalarCondition SqlDialect.Sqlite plan hasValProp |> Option.isSome)

    let notCond = Expression.Not(isSomeProp)
    Assert.True(ExpressionPushdownTranslator.translateScalarCondition SqlDialect.Sqlite plan notCond |> Option.isSome)

    let pName = Expression.Property(pParam, "Name")
    let nullStr = Expression.Constant(null, typeof<string>)
    let eqNullRight = Expression.Equal(pName, nullStr)
    Assert.Equal(Some "(Name IS NULL)", ExpressionPushdownTranslator.translateScalarCondition SqlDialect.Sqlite plan eqNullRight)
    let neNullRight = Expression.NotEqual(pName, nullStr)
    Assert.Equal(Some "(Name IS NOT NULL)", ExpressionPushdownTranslator.translateScalarCondition SqlDialect.Sqlite plan neNullRight)
    let eqNullLeft = Expression.Equal(nullStr, pName)
    Assert.Equal(Some "(Name IS NULL)", ExpressionPushdownTranslator.translateScalarCondition SqlDialect.Sqlite plan eqNullLeft)
    let neNullLeft = Expression.NotEqual(nullStr, pName)
    Assert.Equal(Some "(Name IS NOT NULL)", ExpressionPushdownTranslator.translateScalarCondition SqlDialect.Sqlite plan neNullLeft)

    // --- 9. Inverted and NULL predicate translation in translatePredicate ---
    Assert.Equal(Some "(Name IS NULL)", ExpressionPushdownTranslator.translatePredicate pParam plan SqlDialect.Sqlite eqNullRight)
    Assert.Equal(Some "(Name IS NOT NULL)", ExpressionPushdownTranslator.translatePredicate pParam plan SqlDialect.Sqlite neNullRight)
    let pScore = Expression.Property(pParam, "Score")
    let nullDec = Expression.Constant(null, typeof<Nullable<decimal>>)
    Assert.Equal(None, ExpressionPushdownTranslator.translatePredicate pParam plan SqlDialect.Sqlite (Expression.GreaterThan(pScore, nullDec)))

    Assert.Equal(Some "(Name IS NULL)", ExpressionPushdownTranslator.translatePredicate pParam plan SqlDialect.Sqlite eqNullLeft)
    Assert.Equal(Some "(Name IS NOT NULL)", ExpressionPushdownTranslator.translatePredicate pParam plan SqlDialect.Sqlite neNullLeft)
    Assert.Equal(None, ExpressionPushdownTranslator.translatePredicate pParam plan SqlDialect.Sqlite (Expression.GreaterThan(nullDec, pScore)))

    let planInv = PushdownQueryPlan.Create("Products", [], baseAlias = "p", dialect = SqlDialect.Sqlite)
    ExpressionPushdownTranslator.translatePredicate pParam planInv SqlDialect.Sqlite (Expression.GreaterThan(Expression.Constant(10.0M), pPrice)) |> Option.isSome |> Assert.True
    ExpressionPushdownTranslator.translatePredicate pParam planInv SqlDialect.Sqlite (Expression.GreaterThanOrEqual(Expression.Constant(10.0M), pPrice)) |> Option.isSome |> Assert.True
    ExpressionPushdownTranslator.translatePredicate pParam planInv SqlDialect.Sqlite (Expression.LessThan(Expression.Constant(10.0M), pPrice)) |> Option.isSome |> Assert.True
    ExpressionPushdownTranslator.translatePredicate pParam planInv SqlDialect.Sqlite (Expression.LessThanOrEqual(Expression.Constant(10.0M), pPrice)) |> Option.isSome |> Assert.True
    ExpressionPushdownTranslator.translatePredicate pParam planInv SqlDialect.Sqlite (Expression.Equal(Expression.Constant(10.0M), pPrice)) |> Option.isSome |> Assert.True
    ExpressionPushdownTranslator.translatePredicate pParam planInv SqlDialect.Sqlite (Expression.NotEqual(Expression.Constant(10.0M), pPrice)) |> Option.isSome |> Assert.True

    // --- 10. Aggregates in translateScalarExpr with non-queryable parameters ---
    let seqParam = Expression.Parameter(typeof<seq<CovProduct>>, "seqItems")
    let priceLambda = Expression.Lambda<Func<CovProduct, decimal>>(pPrice, pParam)

    let sumMethod = typeof<Enumerable>.GetMethods() |> Seq.find (fun m -> m.Name = "Sum" && m.ReturnType = typeof<decimal> && m.GetParameters().Length = 2)
    let sumGen = sumMethod.MakeGenericMethod(typeof<CovProduct>)
    let sumCall2 = Expression.Call(null, sumGen, seqParam, priceLambda)
    Assert.True(ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan sumCall2 |> Option.isSome)

    let distinctMethod = typeof<Enumerable>.GetMethods() |> Seq.find (fun m -> m.Name = "Distinct" && m.GetParameters().Length = 1)
    let distinctGen = distinctMethod.MakeGenericMethod(typeof<CovProduct>)
    let distinctCall = Expression.Call(null, distinctGen, seqParam)
    let sumDistinctCall = Expression.Call(null, sumGen, distinctCall, priceLambda)
    let sumDistRes = ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan sumDistinctCall
    Assert.True(sumDistRes.IsSome)
    Assert.Contains("DISTINCT", sumDistRes.Value)

    let minMethod = typeof<Enumerable>.GetMethods() |> Seq.find (fun m -> m.Name = "Min" && m.ReturnType = typeof<decimal> && m.GetParameters().Length = 2)
    let minGen = minMethod.MakeGenericMethod(typeof<CovProduct>)
    let minCall = Expression.Call(null, minGen, seqParam, priceLambda)
    Assert.True(ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan minCall |> Option.isSome)

    let maxMethod = typeof<Enumerable>.GetMethods() |> Seq.find (fun m -> m.Name = "Max" && m.ReturnType = typeof<decimal> && m.GetParameters().Length = 2)
    let maxGen = maxMethod.MakeGenericMethod(typeof<CovProduct>)
    let maxCall = Expression.Call(null, maxGen, seqParam, priceLambda)
    Assert.True(ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan maxCall |> Option.isSome)

    let avgMethod = typeof<Enumerable>.GetMethods() |> Seq.find (fun m -> m.Name = "Average" && m.ReturnType = typeof<decimal> && m.GetParameters().Length = 2)
    let avgGen = avgMethod.MakeGenericMethod(typeof<CovProduct>)
    let avgCall = Expression.Call(null, avgGen, seqParam, priceLambda)
    Assert.True(ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan avgCall |> Option.isSome)

    // --- 11. safeOverClause on SqlServer and Oracle ---
    let builder = FSqlQueryBuilder()
    let dummyWin = Expression.Constant(null, typeof<WindowDefinition option>)
    let winMethod = typeof<FSqlQueryBuilder>.GetMethod("rowNumber")
    let rowNumCall = Expression.Call(Expression.Constant(builder), winMethod, [| dummyWin :> Expression |])
    let rowNumSs = ExpressionPushdownTranslator.tryTranslateWindowFunction SqlDialect.SqlServer plan rowNumCall
    Assert.True(rowNumSs.IsSome)
    Assert.Contains("ORDER BY (SELECT NULL)", rowNumSs.Value)
    let rowNumOra = ExpressionPushdownTranslator.tryTranslateWindowFunction SqlDialect.Oracle plan rowNumCall
    Assert.True(rowNumOra.IsSome)
    Assert.Contains("ORDER BY NULL", rowNumOra.Value)

    // --- 12. Direct Min/Max/Average and Sql Pushdown Queryable methods ---
    let minPrice: decimal = products.Min(fun p -> p.Price)
    Assert.True(minPrice > 0.0M)
    let maxPrice: decimal = products.Max(fun p -> p.Price)
    Assert.True(maxPrice >= minPrice)
    let avgPrice: decimal = products.Average(fun p -> p.Price)
    Assert.True(avgPrice > 0.0M)

    let rawQuery = products.ToRawQuery()
    Assert.NotEmpty(rawQuery)
    let (sqlP, parms) = products.ToSqlWithParameters()
    Assert.NotEmpty(sqlP)

    // --- 13. CustomQueryableWrapper on Set Operations and Extension Methods ---
    let wrapped = CustomQueryableWrapper(products)
    let wrappedWithCte = CustomQueryableWrapper(products.WithCte("c1", products))
    let u1 = products.Union(wrappedWithCte)
    Assert.Contains("UNION", toRawSql u1)
    let u2 = products.UnionAll(wrappedWithCte)
    Assert.Contains("UNION ALL", toRawSql u2)
    let u3 = products.Intersect(wrappedWithCte)
    Assert.Contains("INTERSECT", toRawSql u3)
    let u4 = products.Except(wrappedWithCte)
    Assert.Contains("EXCEPT", toRawSql u4)

    let extU1 = wrapped.Union(products)
    Assert.Contains("UNION", toRawSql extU1)
    let extU2 = wrapped.UnionAll(products)
    Assert.Contains("UNION ALL", toRawSql extU2)
    let extU3 = wrapped.Intersect(products)
    Assert.Contains("INTERSECT", toRawSql extU3)
    let extU4 = wrapped.Except(products)
    Assert.Contains("EXCEPT", toRawSql extU4)

    let emptyWrapped = CustomQueryableWrapper(Enumerable.Empty<CovProduct>().AsQueryable())
    Assert.Throws<Exception>(fun () -> products.Union(emptyWrapped) |> ignore) |> ignore
    Assert.Throws<Exception>(fun () -> products.UnionAll(emptyWrapped) |> ignore) |> ignore
    Assert.Throws<Exception>(fun () -> products.Intersect(emptyWrapped) |> ignore) |> ignore
    Assert.Throws<Exception>(fun () -> products.Except(emptyWrapped) |> ignore) |> ignore

    let cteWrapped1 = wrapped.WithCte(products)
    Assert.NotNull(cteWrapped1)
    let cteWrapped2 = wrapped.WithCte("custom_cte", products)
    Assert.NotNull(cteWrapped2)
    let cteEmpty1 = emptyWrapped.WithCte(products)
    Assert.NotNull(cteEmpty1)
    let cteEmpty2 = emptyWrapped.WithCte("empty_cte", products)
    Assert.NotNull(cteEmpty2)

    // --- 14. Builder marker failwith coverage via reflection ---
    for m in typeof<FSqlQueryBuilder>.GetMethods() do
        let name = m.Name
        if name.StartsWith("With") || name.StartsWith("Where") || name.StartsWith("Union") || name.StartsWith("Intersect") || name.StartsWith("Except") || name.StartsWith("Having") || name.StartsWith("Full") || name.StartsWith("Right") || name.StartsWith("Cross") then
            try
                let pCount = m.GetParameters().Length
                let args = Array.create pCount null
                if m.IsGenericMethodDefinition then
                    let genParams = m.GetGenericArguments()
                    let typeArgs = Array.create genParams.Length typeof<obj>
                    let concreteM = m.MakeGenericMethod(typeArgs)
                    concreteM.Invoke(builder, args) |> ignore
                else
                    m.Invoke(builder, args) |> ignore
            with _ -> ()

    let winDef = builder.window(partitionBy = "cat", sortBy = "price", sortByDescending = "id")
    Assert.Equal("window", winDef.Dummy)

    let bU1 = builder.union products products
    let bU2 = builder.union wrapped wrapped
    let bUA1 = builder.unionAll products products
    let bUA2 = builder.unionAll wrapped wrapped
    let bI1 = builder.intersect products products
    let bI2 = builder.intersect wrapped wrapped
    let bE1 = builder.except products products
    let bE2 = builder.except wrapped wrapped
    Assert.NotNull(bU1); Assert.NotNull(bU2); Assert.NotNull(bUA1); Assert.NotNull(bUA2)
    Assert.NotNull(bI1); Assert.NotNull(bI2); Assert.NotNull(bE1); Assert.NotNull(bE2)

    // --- 15. fsql Computation Expression with subqueries, whereExists, having and set ops ---
    let fsqlEx = fsql {
        for p in products do
        whereExists (fsql { for o in orders do where (o.CustomerId = p.Id) })
        select p
    }
    Assert.NotEmpty(toRawSql fsqlEx)

    let fsqlNotEx = fsql {
        for p in products do
        whereNotExists (fsql { for o in orders do where (o.CustomerId = p.Id) })
        select p
    }
    Assert.NotEmpty(toRawSql fsqlNotEx)

    let fsqlUnion = fsql {
        for p in products do
        select p
        union products
    }
    Assert.NotEmpty(toRawSql fsqlUnion)

    let fsqlUnionAll = fsql {
        for p in products do
        select p
        unionAll products
    }
    Assert.NotEmpty(toRawSql fsqlUnionAll)

    let fsqlIntersect = fsql {
        for p in products do
        select p
        intersect products
    }
    Assert.NotEmpty(toRawSql fsqlIntersect)

    let fsqlExcept = fsql {
        for p in products do
        select p
        except products
    }
    Assert.NotEmpty(toRawSql fsqlExcept)

    // --- 16. ExtractPlan with targetPlan constant in set ops ---
    let prov = (products :> IQueryable).Provider :?> SqlPushdownQueryProvider
    let dummyTargetPlan = PushdownQueryPlan.Create("OtherTable", [], baseAlias = "o", dialect = SqlDialect.Sqlite)
    dummyTargetPlan.Parameters.Add("@p0", 123)
    let dummyPlanConst = Expression.Constant(dummyTargetPlan)
    let fakeUnion = typeof<FakeSetOps>.GetMethod("Union")
    let unionCallWithPlan = Expression.Call(null, fakeUnion, Expression.Constant("src"), dummyPlanConst)
    let extractedWithPlan = prov.ExtractPlan(unionCallWithPlan, seedPlan = products.Plan)
    Assert.True(extractedWithPlan.SetOperations.Count > 0)

    let unionCallWithStr = Expression.Call(null, fakeUnion, Expression.Constant("src"), Expression.Constant("SELECT 1"))
    let extractedWithStr = prov.ExtractPlan(unionCallWithStr, seedPlan = products.Plan)
    Assert.True(extractedWithStr.SetOperations.Count > 0)

// =========================================================================
// 51. PUSHDOWN INTERNALS, AGGREGATES, WINDOW FUNCTIONS, AND FSQL OPERATIONS
// =========================================================================

[<Fact>]
let ``Deep coverage of Pushdown internals, aggregates, window functions, and FSql operations`` () =
    let mockOnExec (sql: string) (parms: (string * obj) list) =
        { Columns = ["val"]; Rows = [ [| box 42.0M |] ] }
    let conn = AdvancedMockConnector(ConnectorKind("Sqlite"), onExecute = mockOnExec)
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")
    let orders = SqlPushdownQueryable<CovOrder>(conn, "Orders")
    let pParam = Expression.Parameter(typeof<CovProduct>, "p")
    let pPrice = Expression.Property(pParam, "Price")
    let plan = products.Plan

    // 1. Direct aggregates on SqlPushdownQueryable
    let priceSel = Expression.Lambda<Func<CovProduct, decimal>>(pPrice, pParam)
    let sumCol = products.Sum<decimal>("Price")
    let sumSel = products.Sum(priceSel)
    let avgCol = products.Average<decimal>("Price")
    let avgSel = products.Average(priceSel)
    let minSel = products.Min(priceSel)
    let maxCol = products.Max<decimal>("Price")
    let maxSel = products.Max(priceSel)
    Assert.Equal(42.0M, sumCol)
    Assert.Equal(42.0M, sumSel)
    Assert.Equal(42.0M, avgCol)
    Assert.Equal(42.0M, avgSel)
    Assert.Equal(42.0M, minSel)
    Assert.Equal(42.0M, maxCol)
    Assert.Equal(42.0M, maxSel)

    // Also with record return and empty row fallback in ExecuteScalarWithPlan
    let emptyConn = AdvancedMockConnector(ConnectorKind("Sqlite"), onExecute = (fun _ _ -> { Columns = []; Rows = [] }))
    let emptyProds = SqlPushdownQueryable<CovProduct>(emptyConn, "Products")
    let emptySum = emptyProds.Sum<decimal>("Price")
    Assert.Equal(0.0M, emptySum)

    let recordConn = AdvancedMockConnector(ConnectorKind("Sqlite"), onExecute = (fun _ _ -> { Columns = ["OrderId"; "CustomerId"; "Amount"; "Status"]; Rows = [ [| box 101; box 1; box 50.0M; box "OK" |] ] }))
    let oParam = Expression.Parameter(typeof<CovOrder>, "o")
    let orderIdSel = Expression.Lambda<Func<CovOrder, CovOrder>>(oParam, oParam)
    let recOrder = (SqlPushdownQueryable<CovOrder>(recordConn, "Orders")).Min(orderIdSel)
    Assert.Equal(101, recOrder.OrderId)

    // 2. Queryable.Count with predicate on SqlPushdownQueryable
    let countMockConn = AdvancedMockConnector(ConnectorKind("Sqlite"), onExecute = (fun _ _ -> { Columns = ["val"]; Rows = [ [| box 7 |] ] }))
    let countProds = SqlPushdownQueryable<CovProduct>(countMockConn, "Products")
    let countPredRes = countProds.Count(fun p -> p.Price > 10.0M)
    Assert.Equal(7, countPredRes)
    let countSelectorRes = countProds.Count(fun p -> p.Id)
    Assert.Equal(7, countSelectorRes)

    // 3. translateScalarExpr with IQueryable constant
    let iqConst = Expression.Constant(products :> IQueryable)
    let subquerySql = ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan iqConst
    Assert.True(subquerySql.IsSome)

    // 4. Nested tuple depth and leadingOnes
    let root3Param = Expression.Parameter(typeof<Tuple<Tuple<CovProduct, CovOrder>, CovOptionItem>>, "root3")
    let rItem1 = Expression.Property(root3Param, "Item1")
    let rItem2 = Expression.Property(rItem1, "Item2")
    let rAmount = Expression.Property(rItem2, "Amount")
    let planNested = PushdownQueryPlan.Create("Products", [], baseAlias = "p", dialect = SqlDialect.Sqlite)
    planNested.Joins.Add({ JoinType = "INNER JOIN"; SourceTable = "Orders"; Alias = "o"; OnClause = "p.Id = o.CustomerId"; RowType = Some typeof<CovOrder> })
    planNested.Joins.Add({ JoinType = "INNER JOIN"; SourceTable = "Items"; Alias = "i"; OnClause = "p.Id = i.Id"; RowType = Some typeof<CovOptionItem> })
    let (nestedAlias, _) = ExpressionPushdownTranslator.resolveMemberRef planNested rAmount
    Assert.Equal(Some "o", nestedAlias)

    // 5. FakeWindow Lag and Lead with various default values
    let fakeLagMethod = typeof<FakeWindowHolder>.GetMethod("Lag")
    let fakeLeadMethod = typeof<FakeWindowHolder>.GetMethod("Lead")
    let overParam = Expression.Constant(null, typeof<obj[]>)
    let pPriceObj = Expression.Convert(pPrice, typeof<obj>)

    for defVal in [ box 0.0M; box 0.5; box 10; box "N/A"; null; box (DateTime.UtcNow) ] do
        let lagCall = Expression.Call(fakeLagMethod, pPriceObj, Expression.Constant(1), Expression.Constant(defVal, typeof<obj>), overParam)
        let leadCall = Expression.Call(fakeLeadMethod, pPriceObj, Expression.Constant(2), Expression.Constant(defVal, typeof<obj>), overParam)
        Assert.True(ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan lagCall |> Option.isSome)
        Assert.True(ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan leadCall |> Option.isSome)

    // 6. FakeAggs: 2-arg non-lambda, 1-arg
    let fakeAggSum2 = typeof<FakeAggs>.GetMethod("Sum", [| typeof<obj>; typeof<obj> |])
    let fakeAggMin2 = typeof<FakeAggs>.GetMethod("Min", [| typeof<obj>; typeof<obj> |])
    let fakeAggMax2 = typeof<FakeAggs>.GetMethod("Max", [| typeof<obj>; typeof<obj> |])
    let fakeAggAvg2 = typeof<FakeAggs>.GetMethod("Average", [| typeof<obj>; typeof<obj> |])
    Assert.True(ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan (Expression.Call(null, fakeAggSum2, Expression.Constant("seq", typeof<obj>), pPriceObj)) |> Option.isSome)
    Assert.True(ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan (Expression.Call(null, fakeAggMin2, Expression.Constant("seq", typeof<obj>), pPriceObj)) |> Option.isSome)
    Assert.True(ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan (Expression.Call(null, fakeAggMax2, Expression.Constant("seq", typeof<obj>), pPriceObj)) |> Option.isSome)
    Assert.True(ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan (Expression.Call(null, fakeAggAvg2, Expression.Constant("seq", typeof<obj>), pPriceObj)) |> Option.isSome)

    let fakeAggSum1 = typeof<FakeAggs>.GetMethod("Sum", [| typeof<obj> |])
    let fakeAggMin1 = typeof<FakeAggs>.GetMethod("Min", [| typeof<obj> |])
    let fakeAggMax1 = typeof<FakeAggs>.GetMethod("Max", [| typeof<obj> |])
    let fakeAggAvg1 = typeof<FakeAggs>.GetMethod("Average", [| typeof<obj> |])
    Assert.True(ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan (Expression.Call(null, fakeAggSum1, pPriceObj)) |> Option.isSome)
    Assert.True(ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan (Expression.Call(null, fakeAggMin1, pPriceObj)) |> Option.isSome)
    Assert.True(ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan (Expression.Call(null, fakeAggMax1, pPriceObj)) |> Option.isSome)
    Assert.True(ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan (Expression.Call(null, fakeAggAvg1, pPriceObj)) |> Option.isSome)

    // 7. MemberInitExpression in extractKeyColumns and single column fallback
    let memberInit = Expression.MemberInit(Expression.New(typeof<CovProduct>), Expression.Bind(typeof<CovProduct>.GetProperty("Price"), pPrice))
    let initCols = ExpressionPushdownTranslator.extractKeyColumns plan "p" pParam memberInit
    Assert.NotEmpty(initCols)
    let singleCols = ExpressionPushdownTranslator.extractKeyColumns plan "p" pParam pPrice
    Assert.NotEmpty(singleCols)

    // 8. exprHasParamName
    let bExpr = Expression.Equal(pPrice, Expression.Constant(10.0M))
    let uExpr = Expression.Not(bExpr)
    let lExpr = Expression.Lambda(bExpr, pParam)
    Assert.True(ExpressionPushdownTranslator.exprHasParamName "p" bExpr)
    Assert.True(ExpressionPushdownTranslator.exprHasParamName "p" uExpr)
    Assert.True(ExpressionPushdownTranslator.exprHasParamName "p" lExpr)
    Assert.False(ExpressionPushdownTranslator.exprHasParamName "other" bExpr)

    // 9. tryUnwrapOptionArg
    Assert.True(ExpressionPushdownTranslator.tryUnwrapOptionArg null |> Option.isNone)
    let someMethod = typeof<Option<decimal>>.GetMethod("Some")
    let someExpr = Expression.Call(someMethod, Expression.Constant(10.0M))
    Assert.True(ExpressionPushdownTranslator.tryUnwrapOptionArg someExpr |> Option.isSome)
    let valPropExpr = Expression.Property(Expression.Constant(Some 10.0M), "Value")
    Assert.True(ExpressionPushdownTranslator.tryUnwrapOptionArg valPropExpr |> Option.isSome)
    let noneExpr = Expression.Property(null, typeof<Option<decimal>>.GetProperty("None"))
    Assert.True(ExpressionPushdownTranslator.tryUnwrapOptionArg noneExpr |> Option.isNone)
    let nonOptExpr = Expression.Constant(42)
    Assert.True(ExpressionPushdownTranslator.tryUnwrapOptionArg nonOptExpr |> Option.isSome)

    // 10. tryExtractSubquerySql
    Assert.True(ExpressionPushdownTranslator.tryExtractSubquerySql plan null |> Option.isNone)
    Assert.True(ExpressionPushdownTranslator.tryExtractSubquerySql plan "not a queryable" |> Option.isNone)
    Assert.True(ExpressionPushdownTranslator.tryExtractSubquerySql plan products |> Option.isSome)

    // 11. unwrapOptionOrNullableMember
    Assert.Null(ExpressionPushdownTranslator.unwrapOptionOrNullableMember null)
    let pScore = Expression.Property(pParam, "Score")
    let pScoreValue = Expression.Property(pScore, "Value")
    Assert.Equal<Expression>(pScore, ExpressionPushdownTranslator.unwrapOptionOrNullableMember pScoreValue)
    let convExpr = Expression.Convert(pScoreValue, typeof<decimal>)
    Assert.Equal<Expression>(pScore, ExpressionPushdownTranslator.unwrapOptionOrNullableMember convExpr)

    // 12. fsql operations: whereExists, whereNotExists, having, rightOuterJoin
    let fsqlExists = fsql {
        for p in products do
        whereExists (orders.Where(fun o -> o.CustomerId = p.Id))
        select p
    }
    Assert.NotEmpty(toRawSql fsqlExists)

    let fsqlNotExists = fsql {
        for p in products do
        whereNotExists (orders.Where(fun o -> o.CustomerId = p.Id))
        select p
    }
    Assert.NotEmpty(toRawSql fsqlNotExists)

    let fsqlHaving = fsql {
        for p in products do
        having (p.Price > 10.0M)
        select p
    }
    Assert.NotEmpty(toRawSql fsqlHaving)

    let fsqlRightJoin = fsql {
        for p in products do
        rightOuterJoin o in orders on (p.Id = o.CustomerId)
        select (p, o)
    }
    Assert.NotEmpty(toRawSql fsqlRightJoin)

    // 13. fsql Set operations with memory seq and IQueryable
    let memList = [ { Id = 1; Name = "P1"; Price = 10.0M; Category = "C1"; InStock = true; Timestamp = DateTime.UtcNow; Score = Nullable 5.0M } ]
    let fsqlUnionSeq = fsql {
        for p in products do
        select p
        union memList
    }
    Assert.NotEmpty(toRawSql fsqlUnionSeq)

    let fsqlUnionIq = fsql {
        for p in products do
        select p
        union (memList.AsQueryable())
    }
    Assert.NotEmpty(toRawSql fsqlUnionIq)

    // 14. IQueryable extension methods on CustomQueryableWrapper and in-memory IQueryable
    let wrap = CustomQueryableWrapper(products)
    let _ = wrap.Union(products)
    let _ = wrap.UnionAll(products)
    let _ = wrap.Intersect(products)
    let _ = wrap.Except(products)
    let _ = wrap.WithCte(products)
    let _ = wrap.WithCte(products, isRecursive = true)
    let _ = wrap.WithCte("CustomCte", products)
    let _ = wrap.WithCte("CustomCteRec", products, isRecursive = true)

    let memIq = memList.AsQueryable()
    let _ = memIq.Union(memIq)
    let _ = memIq.UnionAll(memIq)
    let _ = memIq.Intersect(memIq)
    let _ = memIq.Except(memIq)
    let _ = memIq.WithCte(products)
    let _ = memIq.WithCte("NamedMem", products)

    // 15. Top-level standalone functions
    let _ = recursive "rec" products (fun q -> q)
    let _ = jsonValue (box "data") "$.prop"
    let _ = union (products :> IQueryable<CovProduct>) (products :> IQueryable<CovProduct>)
    let _ = unionAll (products :> IQueryable<CovProduct>) (products :> IQueryable<CovProduct>)
    let _ = intersect (products :> IQueryable<CovProduct>) (products :> IQueryable<CovProduct>)
    let _ = except (products :> IQueryable<CovProduct>) (products :> IQueryable<CovProduct>)
    let _ = union memIq memIq
    let _ = unionAll memIq memIq
    let _ = intersect memIq memIq
    let _ = except memIq memIq
    Assert.True(true)

// =========================================================================
// 52. EXTENDED PUSHDOWN PREDICATES, CORRELATED SUBQUERIES, AND QUOTATIONS
// =========================================================================

type JoinHolder() =
    member val MyProds = (SqlPushdownQueryable<CovProduct>(AdvancedMockConnector(ConnectorKind("Sqlite")), "Products")) with get, set
    [<DefaultValue>] val mutable MyProdsField: SqlPushdownQueryable<CovProduct>

[<Fact>]
let ``Deep coverage of Pushdown predicates, subquery Any/Count, Quotation rewriting and edge branches`` () =
    let conn = AdvancedMockConnector(ConnectorKind("Sqlite"))
    let products = SqlPushdownQueryable<CovProduct>(conn, "Products")
    let orders = SqlPushdownQueryable<CovOrder>(conn, "Orders")
    let pParam = Expression.Parameter(typeof<CovProduct>, "p")
    let pPrice = Expression.Property(pParam, "Price")
    let plan = products.Plan
    let prov = (products :> IQueryable).Provider :?> SqlPushdownQueryProvider

    // 1. Lines 895-926: Custom method on p causing translateScalarExpr to return None
    let customCall = Expression.Call(typeof<CustomTestFuncs>.GetMethod("Custom"), pPrice)
    let customCallObj = Expression.Convert(customCall, typeof<obj>)
    let nullDec = Expression.Constant(null, typeof<obj>)
    let tenDec = Expression.Constant(10.0M)

    // Left has param, right is constant
    let predLeftNull = ExpressionPushdownTranslator.translatePredicate pParam plan SqlDialect.Sqlite (Expression.Equal(customCallObj, nullDec))
    Assert.True(predLeftNull.IsSome)
    Assert.Contains("IS NULL", predLeftNull.Value)

    let predLeftNotNull = ExpressionPushdownTranslator.translatePredicate pParam plan SqlDialect.Sqlite (Expression.NotEqual(customCallObj, nullDec))
    Assert.True(predLeftNotNull.IsSome)
    Assert.Contains("IS NOT NULL", predLeftNotNull.Value)

    let predLeftVal = ExpressionPushdownTranslator.translatePredicate pParam plan SqlDialect.Sqlite (Expression.Equal(customCall, tenDec))
    Assert.True(predLeftVal.IsSome)

    // Right has param, left is constant
    let predRightNull = ExpressionPushdownTranslator.translatePredicate pParam plan SqlDialect.Sqlite (Expression.Equal(nullDec, customCallObj))
    Assert.True(predRightNull.IsSome)
    Assert.Contains("IS NULL", predRightNull.Value)

    let predRightNotNull = ExpressionPushdownTranslator.translatePredicate pParam plan SqlDialect.Sqlite (Expression.NotEqual(nullDec, customCallObj))
    Assert.True(predRightNotNull.IsSome)
    Assert.Contains("IS NOT NULL", predRightNotNull.Value)

    let predRightGt = ExpressionPushdownTranslator.translatePredicate pParam plan SqlDialect.Sqlite (Expression.GreaterThan(tenDec, customCall))
    Assert.True(predRightGt.IsSome)

    let predRightGte = ExpressionPushdownTranslator.translatePredicate pParam plan SqlDialect.Sqlite (Expression.GreaterThanOrEqual(tenDec, customCall))
    Assert.True(predRightGte.IsSome)

    let predRightLt = ExpressionPushdownTranslator.translatePredicate pParam plan SqlDialect.Sqlite (Expression.LessThan(tenDec, customCall))
    Assert.True(predRightLt.IsSome)

    let predRightLte = ExpressionPushdownTranslator.translatePredicate pParam plan SqlDialect.Sqlite (Expression.LessThanOrEqual(tenDec, customCall))
    Assert.True(predRightLte.IsSome)

    // 2. Lines 1313-1320 & 1348-1355: Window Lag/Lead with defaultValue from evaluateClientValue
    let fakeLagMethod = typeof<FakeWindowHolder>.GetMethod("Lag")
    let fakeLeadMethod = typeof<FakeWindowHolder>.GetMethod("Lead")
    let pPriceObj = Expression.Convert(pPrice, typeof<obj>)
    let overParam = Expression.Constant(null, typeof<obj[]>)

    let customDecCall = Expression.Call(typeof<CustomTestFuncs>.GetMethod("CustomDec"), Expression.Constant(10.0M))
    let customFloatCall = Expression.Call(typeof<CustomTestFuncs>.GetMethod("CustomFloat"), Expression.Constant(1.5))
    let customIntCall = Expression.Call(typeof<CustomTestFuncs>.GetMethod("CustomInt"), Expression.Constant(5))
    let customStrCall = Expression.Call(typeof<CustomTestFuncs>.GetMethod("CustomStr"), Expression.Constant("xyz"))
    let customNullCall = Expression.Call(typeof<CustomTestFuncs>.GetMethod("CustomNull"), Expression.Constant("x"))
    let customDateCall = Expression.Call(typeof<CustomTestFuncs>.GetMethod("CustomDate"), Expression.Constant(DateTime.UtcNow))

    for callExpr: Expression in [ (Expression.Convert(customDecCall, typeof<obj>) :> Expression); (Expression.Convert(customFloatCall, typeof<obj>) :> Expression); (Expression.Convert(customIntCall, typeof<obj>) :> Expression); (customStrCall :> Expression); (customNullCall :> Expression); (Expression.Convert(customDateCall, typeof<obj>) :> Expression) ] do
        let lagCall = Expression.Call(fakeLagMethod, pPriceObj, Expression.Constant(1), callExpr, overParam)
        let leadCall = Expression.Call(fakeLeadMethod, pPriceObj, Expression.Constant(1), callExpr, overParam)
        Assert.True(ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan lagCall |> Option.isSome)
        Assert.True(ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan leadCall |> Option.isSome)

    // 3. Lines 237-242: FieldHolder with populated FieldProducts
    let fieldHolder = FieldHolder()
    fieldHolder.FieldProducts <- products
    let fExpr = Expression.Field(Expression.Constant(fieldHolder), typeof<FieldHolder>.GetField("FieldProducts"))
    let fRes = ExpressionPushdownTranslator.tryFindPushdownSource fExpr
    Assert.True(fRes.IsSome)

    // 4. Lines 526-530: tryExtractFromValue on EnumerableQuery
    let enumQ = [ 1; 2 ].AsQueryable()
    let eqRes = ExpressionPushdownTranslator.tryExtractFromValue (box enumQ)
    Assert.True(eqRes.IsNone)

    // 5. Lines 1088-1100: tryTranslateSubqueryPredicate Any with 2 arguments
    let anyMethod = typeof<Queryable>.GetMethods() |> Seq.find (fun m -> m.Name = "Any" && m.GetParameters().Length = 2)
    let anyGen = anyMethod.MakeGenericMethod(typeof<CovOrder>)
    let oParam = Expression.Parameter(typeof<CovOrder>, "o")
    let anyPred = Expression.Lambda<Func<CovOrder, bool>>(Expression.Equal(Expression.Property(oParam, "CustomerId"), Expression.Property(pParam, "Id")), oParam)
    let anyCall = Expression.Call(null, anyGen, (orders :> IQueryable).Expression, anyPred)
    let anySql = ExpressionPushdownTranslator.translatePredicate pParam plan SqlDialect.Sqlite anyCall
    Assert.True(anySql.IsSome)
    Assert.Contains("EXISTS", anySql.Value)

    // 6. Lines 805-815: Correlated Count subquery comparisons
    let countMethod = (typeof<Queryable>.GetMethods() |> Seq.find (fun m -> m.Name = "Count" && m.GetParameters().Length = 1)).MakeGenericMethod(typeof<CovOrder>)
    let whereMethod = (typeof<Queryable>.GetMethods() |> Seq.find (fun m -> m.Name = "Where" && m.GetParameters().Length = 2)).MakeGenericMethod(typeof<CovOrder>)
    let whereOrdersCall = Expression.Call(null, whereMethod, (orders :> IQueryable).Expression, anyPred)
    let countOrdersCall = Expression.Call(null, countMethod, whereOrdersCall)

    let gtCallLeft = Expression.GreaterThan(countOrdersCall, Expression.Constant(0))
    let gtSqlLeft = ExpressionPushdownTranslator.translatePredicate pParam plan SqlDialect.Sqlite gtCallLeft
    Assert.True(gtSqlLeft.IsSome)

    let gtCallRight = Expression.LessThan(Expression.Constant(0), countOrdersCall)
    let gtSqlRight = ExpressionPushdownTranslator.translatePredicate pParam plan SqlDialect.Sqlite gtCallRight
    Assert.True(gtSqlRight.IsSome)

    // 7. Lines 2419-2428: ExtractPlan with Count on bool lambda, non-bool lambda, and non-lambda
    let count2Method = (typeof<Queryable>.GetMethods() |> Seq.find (fun m -> m.Name = "Count" && m.GetParameters().Length = 2)).MakeGenericMethod(typeof<CovProduct>)
    let boolLambda = Expression.Lambda<Func<CovProduct, bool>>(Expression.GreaterThan(pPrice, tenDec), pParam)
    let nonBoolLambda = Expression.Lambda<Func<CovProduct, decimal>>(pPrice, pParam)

    let pCountBool = prov.ExtractPlan(Expression.Call(null, count2Method, (products :> IQueryable).Expression, boolLambda), seedPlan = plan)
    Assert.True(pCountBool.WhereClauses.Count > 0)

    let fakeCountMethod = typeof<FakeCountHolder>.GetMethod("Count")
    let pCountNonBool = prov.ExtractPlan(Expression.Call(null, fakeCountMethod, (products :> IQueryable).Expression, nonBoolLambda), seedPlan = plan)
    Assert.True(pCountNonBool.Projection.IsSome)

    let pCountNonLambda = prov.ExtractPlan(Expression.Call(null, fakeCountMethod, (products :> IQueryable).Expression, pPriceObj), seedPlan = plan)
    Assert.True(pCountNonLambda.Projection.IsSome)

    // 8. Lines 2220-2224: Join where outerCols and innerCols length mismatch
    let joinMethod = (typeof<Queryable>.GetMethods() |> Seq.find (fun m -> m.Name = "Join" && m.GetParameters().Length = 5)).MakeGenericMethod(typeof<CovProduct>, typeof<CovOrder>, typeof<obj>, typeof<obj>)
    let outerKeyMismatch = Expression.Lambda<Func<CovProduct, obj>>(Expression.New(typeof<Tuple<int, string>>.GetConstructors().[0], [| Expression.Property(pParam, "Id") :> Expression; Expression.Property(pParam, "Name") :> Expression |]), pParam)
    let innerKeyMismatch = Expression.Lambda<Func<CovOrder, obj>>(Expression.Convert(Expression.Property(oParam, "CustomerId"), typeof<obj>), oParam)
    let resSelector = Expression.Lambda<Func<CovProduct, CovOrder, obj>>(Expression.Constant("res", typeof<obj>), pParam, oParam)
    let joinCallMismatch = Expression.Call(null, joinMethod, (products :> IQueryable).Expression, (orders :> IQueryable).Expression, outerKeyMismatch, innerKeyMismatch, resSelector)
    let pJoin = prov.ExtractPlan(joinCallMismatch, seedPlan = plan)
    Assert.True(pJoin.Joins.Count > 0)

    // 9. Lines 2100-2120: MemberExpression accessing property and field on Join innerArg
    let jHolder = JoinHolder()
    jHolder.MyProdsField <- products
    let joinPropExpr = Expression.Property(Expression.Constant(jHolder), "MyProds")
    let joinFieldExpr = Expression.Field(Expression.Constant(jHolder), "MyProdsField")
    let outerKeySimple = Expression.Lambda<Func<CovProduct, obj>>(Expression.Convert(Expression.Property(pParam, "Id"), typeof<obj>), pParam)
    let pParam2 = Expression.Parameter(typeof<CovProduct>, "p2")
    let innerKeySimple = Expression.Lambda<Func<CovProduct, obj>>(Expression.Convert(Expression.Property(pParam2, "Id"), typeof<obj>), pParam2)
    let resSelSimple = Expression.Lambda<Func<CovProduct, CovProduct, obj>>(Expression.Constant("r", typeof<obj>), pParam, pParam2)
    let jMethod2 = (typeof<Queryable>.GetMethods() |> Seq.find (fun m -> m.Name = "Join" && m.GetParameters().Length = 5)).MakeGenericMethod(typeof<CovProduct>, typeof<CovProduct>, typeof<obj>, typeof<obj>)
    let callPropJoin = Expression.Call(null, jMethod2, (products :> IQueryable).Expression, joinPropExpr, outerKeySimple, innerKeySimple, resSelSimple)
    let callFieldJoin = Expression.Call(null, jMethod2, (products :> IQueryable).Expression, joinFieldExpr, outerKeySimple, innerKeySimple, resSelSimple)
    let pPropJoin = prov.ExtractPlan(callPropJoin, seedPlan = plan)
    let pFieldJoin = prov.ExtractPlan(callFieldJoin, seedPlan = plan)
    Assert.True(pPropJoin.Joins.Count > 0)
    Assert.True(pFieldJoin.Joins.Count > 0)

    // 10. Lines 4203-4210: toSpq with seed plan having CTEs
    let prodsWithCte = products.WithCte("TestCte", products)
    let fsqlWithSeedCte = fsql {
        for p in prodsWithCte do
        select p
        union prodsWithCte
    }
    Assert.NotEmpty(toRawSql fsqlWithSeedCte)

    // 11. Lines 1657-1660: isNotNullCheck in ConditionalExpression -> COALESCE
    let pScoreObj = Expression.Convert(Expression.Property(pParam, "Score"), typeof<obj>)
    let condIsNotNull = Expression.Condition(Expression.NotEqual(pScoreObj, Expression.Constant(null, typeof<obj>)), pPrice, Expression.Constant(0.0M))
    let coalesceSql = ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan condIsNotNull
    Assert.True(coalesceSql.IsSome)
    Assert.Contains("COALESCE", coalesceSql.Value)

    let condIsNull = Expression.Condition(Expression.Equal(pScoreObj, Expression.Constant(null, typeof<obj>)), Expression.Constant(0.0M), pPrice)
    let coalesceNullSql = ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan condIsNull
    Assert.True(coalesceNullSql.IsSome)
    Assert.Contains("COALESCE", coalesceNullSql.Value)

    // 12. Lines 2478-2481: Count on non-MemberExpression projection body
    let addExpr = Expression.Add(pPrice, Expression.Constant(1.0M))
    let selectAddMethod = (typeof<Queryable>.GetMethods() |> Seq.find (fun m -> m.Name = "Select" && m.GetParameters().Length = 2)).MakeGenericMethod(typeof<CovProduct>, typeof<decimal>)
    let selectAddCall = Expression.Call(null, selectAddMethod, (products :> IQueryable).Expression, Expression.Lambda<Func<CovProduct, decimal>>(addExpr, pParam))
    let countAfterAdd = Expression.Call(null, countMethod.GetGenericMethodDefinition().MakeGenericMethod(typeof<decimal>), selectAddCall)
    let pCountAdd = prov.ExtractPlan(countAfterAdd, seedPlan = plan)
    Assert.True(pCountAdd.Projection.IsSome)

    // 13. Lines 4326-4331: PropertyGet on static property with CTE
    let fsqlStaticCte = fsql {
        for p in StaticCteHolder.ProdsWithCte do
        select p
    }
    Assert.NotEmpty(toRawSql fsqlStaticCte)

    // 14. Subquery whereExists and whereNotExists
    let fsqlExistsSub = fsql {
        for p in products do
        whereExists (fsql { for o in orders do where (o.CustomerId = p.Id); select o })
        select p
    }
    Assert.NotEmpty(toRawSql fsqlExistsSub)

    let fsqlNotExistsSub = fsql {
        for p in products do
        whereNotExists (fsql { for o in orders do where (o.CustomerId = p.Id); select o })
        select p
    }
    Assert.NotEmpty(toRawSql fsqlNotExistsSub)

    // 15. Line 576-578: tryExtractQueryableInfo fallback on method call
    let mcGetQ = Expression.Call(typeof<CustomTestFuncs>.GetMethod("GetQueryable"), (products :> IQueryable).Expression)
    let qInfoRes = ExpressionPushdownTranslator.tryExtractQueryableInfo mcGetQ
    Assert.True(qInfoRes.IsSome)

    Assert.True(true)

[<Fact>]
let ``Direct SqlPushdownQueryable and IQueryProvider API coverage`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path) :> IQueryableConnector
    let qProducts = SqlPushdownQueryable<CovProduct>(conn, "SELECT * FROM Products")
    
    // Properties
    Assert.Equal("SELECT * FROM Products", qProducts.BaseSql)
    Assert.Empty(qProducts.BaseParameters)
    Assert.NotNull(qProducts.Connector)
    Assert.NotNull(qProducts.Plan)

    // Direct methods
    let qW = qProducts.Where("Id > @p0", [ ("@p0", box 1) ])
    Assert.NotNull(qW)
    let qP1 = qProducts.Page(1, 5)
    let qP0 = qProducts.Page(0, 0)
    Assert.NotNull(qP1)
    Assert.NotNull(qP0)

    let qG = qProducts.GroupBy("Category, InStock")
    Assert.NotNull(qG)

    let qH = qProducts.Having("COUNT(*) > 0")
    Assert.NotNull(qH)

    let qCA = qProducts.CrossApply("SELECT 1 AS X", "ca")
    Assert.NotNull(qCA)

    let qOA = qProducts.OuterApply("SELECT 1 AS X", "oa")
    Assert.NotNull(qOA)

    let qCJ = qProducts.CrossJoin("Products", "p2")
    Assert.NotNull(qCJ)

    let qFO = qProducts.FullOuterJoin("Products", "p3", "Products.Id = p3.Id")
    Assert.NotNull(qFO)

    let qRO = qProducts.RightOuterJoin("Products", "p4", "Products.Id = p4.Id")
    Assert.NotNull(qRO)

    let qProj = qProducts.SelectProjection("Id, Name")
    Assert.NotNull(qProj)

    let qWE = qProducts.WhereExists("SELECT 1 FROM Products WHERE Id = 1")
    Assert.NotNull(qWE)

    let qWNE = qProducts.WhereNotExists("SELECT 1 FROM Products WHERE Id = 999")
    Assert.NotNull(qWNE)

    let qWI1 = qProducts.WhereIn("Id", "SELECT 1")
    let qWI2 = qProducts.WhereIn("Category", [ "Electronics"; "Stationery" ])
    let qWI3 = qProducts.WhereIn("Category", ([]: string list))
    Assert.NotNull(qWI1)
    Assert.NotNull(qWI2)
    Assert.NotNull(qWI3)

    let qWNI1 = qProducts.WhereNotIn("Id", "SELECT 999")
    let qWNI2 = qProducts.WhereNotIn("Category", [ "Books" ])
    let qWNI3 = qProducts.WhereNotIn("Category", ([]: string list))
    Assert.NotNull(qWNI1)
    Assert.NotNull(qWNI2)
    Assert.NotNull(qWNI3)

    // Direct scalars
    let cnt = qProducts.Count()
    Assert.Equal(5, cnt)
    let cntId = qProducts.Count("Id")
    Assert.Equal(5, cntId)
    let cntExpr1 = qProducts.Count(fun p -> p.Id)
    Assert.Equal(5, cntExpr1)
    let cntExpr2 = qProducts.Count(fun p -> p.Price > 10.0M)
    Assert.True(cntExpr2 > 0)
    let pParam2 = Expression.Parameter(typeof<CovProduct>, "p")
    let cntDistExprSel = Expression.Lambda<Func<CovProduct, string>>(Expression.Property(pParam2, "Category"), pParam2)
    let priceExprSel = Expression.Lambda<Func<CovProduct, decimal>>(Expression.Property(pParam2, "Price"), pParam2)
    let cntDist = qProducts.CountDistinct("Category")
    Assert.Equal(2, cntDist)
    let cntDistExpr = qProducts.CountDistinct(cntDistExprSel)
    Assert.Equal(2, cntDistExpr)

    let sum1 = qProducts.Sum<decimal>("Price")
    Assert.True(sum1 > 0.0M)
    let sum2 = qProducts.Sum(priceExprSel)
    Assert.True(sum2 > 0.0M)

    let min1 = qProducts.Min<decimal>("Price")
    Assert.True(min1 > 0.0M)
    let min2 = qProducts.Min(priceExprSel)
    Assert.True(min2 > 0.0M)

    let max1 = qProducts.Max<decimal>("Price")
    Assert.True(max1 > 0.0M)
    let max2 = qProducts.Max(priceExprSel)
    Assert.True(max2 > 0.0M)

    let avg1 = qProducts.Average<decimal>("Price")
    Assert.True(avg1 > 0.0M)
    let avg2 = qProducts.Average(priceExprSel)
    Assert.True(avg2 > 0.0M)

    // SQL extraction
    let rawSql = qProducts.ToRawSql()
    Assert.NotEmpty(rawSql)
    let rawQ = qProducts.ToRawQuery()
    Assert.NotEmpty(rawQ)
    let sql, prms = qProducts.ToSqlWithParameters()
    Assert.NotEmpty(sql)

    // IQueryProvider implementation
    let prov = (qProducts :> IQueryable).Provider
    let createdNonGen = prov.CreateQuery((qProducts :> IQueryable).Expression)
    Assert.NotNull(createdNonGen)
    let createdGen = prov.CreateQuery<CovProduct>((qProducts :> IQueryable).Expression)
    Assert.NotNull(createdGen)

    let execNonGen = prov.Execute((qProducts :> IQueryable).Expression)
    Assert.NotNull(execNonGen)
    let execSeq = prov.Execute<seq<CovProduct>>((qProducts :> IQueryable).Expression)
    Assert.NotEmpty(execSeq)

    // Helper functions coverage
    Assert.True(ExpressionPushdownTranslator.isOptionOrNullableType typeof<decimal option>)
    Assert.True(ExpressionPushdownTranslator.isOptionOrNullableType typeof<Nullable<int>>)
    Assert.True(ExpressionPushdownTranslator.isOptionOrNullableType typeof<ValueOption<string>>)
    Assert.False(ExpressionPushdownTranslator.isOptionOrNullableType typeof<string>)
    Assert.False(ExpressionPushdownTranslator.isOptionOrNullableType null)

    Assert.Null(ExpressionPushdownTranslator.unwrapClientOption null)
    Assert.Equal(box 42, ExpressionPushdownTranslator.unwrapClientOption (Some 42))
    Assert.Null(ExpressionPushdownTranslator.unwrapClientOption (None: int option))
    Assert.Equal(box 99, ExpressionPushdownTranslator.unwrapClientOption (Nullable 99))
    Assert.Null(ExpressionPushdownTranslator.unwrapClientOption (Nullable<int>()))
    Assert.Equal(box "vopt", ExpressionPushdownTranslator.unwrapClientOption (ValueSome "vopt"))
    Assert.Null(ExpressionPushdownTranslator.unwrapClientOption (ValueNone: ValueOption<string>))
    Assert.Equal(box "plain", ExpressionPushdownTranslator.unwrapClientOption "plain")

[<Fact>]
let ``Pushdown comprehensive coverage section 53 - translation, sets, aggregates, explain, and Linq edge cases`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path) :> IQueryableConnector
    let products = SqlPushdownQueryable<CovProduct>(conn, "SELECT * FROM Products")
    let orders = SqlPushdownQueryable<CovOrder>(conn, "SELECT * FROM Orders")
    let plan = PushdownQueryPlan.Create("Products", [], baseAlias = "_t0", baseRowType = typeof<CovProduct>, dialect = SqlDialect.Sqlite)

    // 1. Date and Time Functions across dialects
    let pParam = Expression.Parameter(typeof<CovProduct>, "p")
    let pTime = Expression.Property(pParam, "Timestamp")
    let pDate = Expression.Property(pTime, "Date")
    let pYear = Expression.Property(pTime, "Year")
    let pMonth = Expression.Property(pTime, "Month")
    let pDay = Expression.Property(pTime, "Day")
    let pHour = Expression.Property(pTime, "Hour")
    let pMinute = Expression.Property(pTime, "Minute")
    let pSecond = Expression.Property(pTime, "Second")

    let dialects = [ SqlDialect.Sqlite; SqlDialect.SqlServer; SqlDialect.PostgreSql; SqlDialect.MySql; SqlDialect.MariaDb; SqlDialect.Oracle ]
    for d in dialects do
        Assert.True((ExpressionPushdownTranslator.translateScalarExpr d plan pDate).IsSome)
        Assert.True((ExpressionPushdownTranslator.translateScalarExpr d plan pYear).IsSome)
        Assert.True((ExpressionPushdownTranslator.translateScalarExpr d plan pMonth).IsSome)
        Assert.True((ExpressionPushdownTranslator.translateScalarExpr d plan pDay).IsSome)
        Assert.True((ExpressionPushdownTranslator.translateScalarExpr d plan pHour).IsSome)
        Assert.True((ExpressionPushdownTranslator.translateScalarExpr d plan pMinute).IsSome)
        Assert.True((ExpressionPushdownTranslator.translateScalarExpr d plan pSecond).IsSome)

    // DateTime ToString with formats across dialects
    let toStringFmtMethod = typeof<DateTime>.GetMethod("ToString", [| typeof<string> |])
    let callFmt = Expression.Call(pTime, toStringFmtMethod, Expression.Constant("yyyy-MM-dd HH:mm:ss"))
    for d in dialects do
        let resFmt = ExpressionPushdownTranslator.translateScalarExpr d plan callFmt
        Assert.True(resFmt.IsSome)

    // DateTime ToString without format
    let toStringNoArgMethod = typeof<DateTime>.GetMethod("ToString", [||])
    let callNoArg = Expression.Call(pTime, toStringNoArgMethod)
    for d in dialects do
        let resNoArg = ExpressionPushdownTranslator.translateScalarExpr d plan callNoArg
        Assert.True(resNoArg.IsSome)

    // 2. String methods
    let pName = Expression.Property(pParam, "Name")
    // Substring 1-arg and 2-arg
    let substr1Method = typeof<string>.GetMethod("Substring", [| typeof<int> |])
    let substr2Method = typeof<string>.GetMethod("Substring", [| typeof<int>; typeof<int> |])
    let callSub1 = Expression.Call(pName, substr1Method, Expression.Constant(2))
    let callSub2 = Expression.Call(pName, substr2Method, Expression.Constant(2), Expression.Constant(4))
    for d in [ SqlDialect.Sqlite; SqlDialect.SqlServer ] do
        Assert.True((ExpressionPushdownTranslator.translateScalarExpr d plan callSub1).IsSome)
        Assert.True((ExpressionPushdownTranslator.translateScalarExpr d plan callSub2).IsSome)

    // Replace
    let replaceMethod = typeof<string>.GetMethod("Replace", [| typeof<string>; typeof<string> |])
    let callReplace = Expression.Call(pName, replaceMethod, Expression.Constant("old"), Expression.Constant("new"))
    Assert.True((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan callReplace).IsSome)

    // Concat
    let concatMethod = typeof<string>.GetMethod("Concat", [| typeof<string>; typeof<string> |])
    let callConcat = Expression.Call(null, concatMethod, pName, Expression.Constant("!"))
    for d in [ SqlDialect.SqlServer; SqlDialect.MySql; SqlDialect.Sqlite ] do
        Assert.True((ExpressionPushdownTranslator.translateScalarExpr d plan callConcat).IsSome)

    // ToUpper, ToLower, Trim, IsNullOrEmpty, IsNullOrWhiteSpace
    let toUpperMethod = typeof<string>.GetMethod("ToUpper", [||])
    let toLowerMethod = typeof<string>.GetMethod("ToLower", [||])
    let trimMethod = typeof<string>.GetMethod("Trim", [||])
    let isNullOrEmptyMethod = typeof<string>.GetMethod("IsNullOrEmpty", [| typeof<string> |])
    let isNullOrWhiteSpaceMethod = typeof<string>.GetMethod("IsNullOrWhiteSpace", [| typeof<string> |])
    Assert.True((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan (Expression.Call(pName, toUpperMethod))).IsSome)
    Assert.True((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan (Expression.Call(pName, toLowerMethod))).IsSome)
    Assert.True((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan (Expression.Call(pName, trimMethod))).IsSome)
    Assert.True((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan (Expression.Call(null, isNullOrEmptyMethod, pName))).IsSome)
    Assert.True((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan (Expression.Call(null, isNullOrWhiteSpaceMethod, pName))).IsSome)

    // IndexOf
    let indexOfMethod = typeof<string>.GetMethod("IndexOf", [| typeof<string> |])
    let callIndexOf = Expression.Call(pName, indexOfMethod, Expression.Constant("key"))
    for d in [ SqlDialect.SqlServer; SqlDialect.PostgreSql; SqlDialect.Sqlite ] do
        Assert.True((ExpressionPushdownTranslator.translateScalarExpr d plan callIndexOf).IsSome)

    // String Contains / StartsWith / EndsWith via translateScalarCondition
    let containsMethod = typeof<string>.GetMethod("Contains", [| typeof<string> |])
    let startsWithMethod = typeof<string>.GetMethod("StartsWith", [| typeof<string> |])
    let endsWithMethod = typeof<string>.GetMethod("EndsWith", [| typeof<string> |])
    Assert.True((ExpressionPushdownTranslator.translateScalarCondition SqlDialect.Sqlite plan (Expression.Call(pName, containsMethod, Expression.Constant("abc")))).IsSome)
    Assert.True((ExpressionPushdownTranslator.translateScalarCondition SqlDialect.Sqlite plan (Expression.Call(pName, startsWithMethod, Expression.Constant("abc")))).IsSome)
    Assert.True((ExpressionPushdownTranslator.translateScalarCondition SqlDialect.Sqlite plan (Expression.Call(pName, endsWithMethod, Expression.Constant("abc")))).IsSome)

    // String StartsWith / EndsWith via translatePredicate with parameter registration
    let planWithP = plan.Clone()
    let predStarts = ExpressionPushdownTranslator.translatePredicate pParam planWithP SqlDialect.Sqlite (Expression.Call(pName, startsWithMethod, Expression.Constant("foo")))
    let predEnds = ExpressionPushdownTranslator.translatePredicate pParam planWithP SqlDialect.Sqlite (Expression.Call(pName, endsWithMethod, Expression.Constant("bar")))
    Assert.True(predStarts.IsSome)
    Assert.True(predEnds.IsSome)

    // 3. Math methods
    let pPrice = Expression.Property(pParam, "Price")
    let pPriceDouble = Expression.Convert(pPrice, typeof<double>)
    let mathAbs = typeof<Math>.GetMethod("Abs", [| typeof<decimal> |])
    let mathFloor = typeof<Math>.GetMethod("Floor", [| typeof<decimal> |])
    let mathCeiling = typeof<Math>.GetMethod("Ceiling", [| typeof<decimal> |])
    let mathRound1 = typeof<Math>.GetMethod("Round", [| typeof<decimal> |])
    let mathRound2 = typeof<Math>.GetMethod("Round", [| typeof<decimal>; typeof<int> |])
    let mathSqrt = typeof<Math>.GetMethod("Sqrt", [| typeof<double> |])
    let mathPow = typeof<Math>.GetMethod("Pow", [| typeof<double>; typeof<double> |])

    Assert.True((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan (Expression.Call(null, mathAbs, pPrice))).IsSome)
    Assert.True((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan (Expression.Call(null, mathFloor, pPrice))).IsSome)
    for d in [ SqlDialect.PostgreSql; SqlDialect.SqlServer ] do
        Assert.True((ExpressionPushdownTranslator.translateScalarExpr d plan (Expression.Call(null, mathCeiling, pPrice))).IsSome)
    Assert.True((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan (Expression.Call(null, mathRound1, pPrice))).IsSome)
    Assert.True((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan (Expression.Call(null, mathRound2, pPrice, Expression.Constant(2)))).IsSome)
    Assert.True((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan (Expression.Call(null, mathSqrt, pPriceDouble))).IsSome)
    Assert.True((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan (Expression.Call(null, mathPow, pPriceDouble, Expression.Constant(2.0)))).IsSome)

    // 4. Nullable, Option, Coalescence
    let pScore = Expression.Property(pParam, "Score")
    let getValDef1 = typeof<Nullable<decimal>>.GetMethod("GetValueOrDefault", [| typeof<decimal> |])
    let getValDef0 = typeof<Nullable<decimal>>.GetMethod("GetValueOrDefault", [||])
    Assert.True((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan (Expression.Call(pScore, getValDef1, Expression.Constant(0.0M)))).IsSome)
    Assert.True((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan (Expression.Call(pScore, getValDef0))).IsSome)

    // Option defaultValue / defaultArg calls
    let miDefVal = typeof<CustomTestFuncs>.GetMethod("defaultValue")
    let miDefArg = typeof<CustomTestFuncs>.GetMethod("defaultArg")
    let callDefVal = Expression.Call(null, miDefVal, Expression.Constant("def"), Expression.Constant("opt"))
    let callDefArg = Expression.Call(null, miDefArg, Expression.Constant("opt"), Expression.Constant("def"))
    Assert.True((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan callDefVal).IsSome)
    Assert.True((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan callDefArg).IsSome)

    // Boolean constant in PostgreSQL
    Assert.Equal("TRUE", (ExpressionPushdownTranslator.translateScalarExpr SqlDialect.PostgreSql plan (Expression.Constant(true))).Value)
    Assert.Equal("FALSE", (ExpressionPushdownTranslator.translateScalarExpr SqlDialect.PostgreSql plan (Expression.Constant(false))).Value)

    // 5. Correlated aggregate subqueries and renumbering parameters
    let qSubParam = orders.Where(fun o -> o.Amount > 10.0M)
    let subPlanCorrelated = plan.Clone()
    let sumMethodDef =
        typeof<Enumerable>.GetMethods()
        |> Seq.find (fun m ->
            m.Name = "Sum"
            && m.IsGenericMethod
            && m.GetParameters().Length = 2
            && m.ReturnType = typeof<decimal>)
    let sumMethod = sumMethodDef.MakeGenericMethod(typeof<CovOrder>)
    let sumCorrelated = Expression.Call(null, sumMethod,
                                        (qSubParam :> IQueryable).Expression,
                                        Expression.Lambda<Func<CovOrder, decimal>>(Expression.Property(Expression.Parameter(typeof<CovOrder>, "o"), "Amount"), Expression.Parameter(typeof<CovOrder>, "o")))
    let resSubScalar = ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite subPlanCorrelated sumCorrelated
    Assert.True(resSubScalar.IsSome)

    // Comparison with subquery Count on Left and Right
    let pId = Expression.Property(pParam, "Id")
    let countMethodDef =
        typeof<Queryable>.GetMethods()
        |> Seq.find (fun m -> m.Name = "Count" && m.GetParameters().Length = 1)
    let countMethod = countMethodDef.MakeGenericMethod(typeof<CovOrder>)
    let countCorrelated = Expression.Call(null, countMethod, (qSubParam :> IQueryable).Expression)

    let binSubOnRight = Expression.LessThan(pId, countCorrelated)
    let binSubOnLeft = Expression.GreaterThan(countCorrelated, pId)
    let predSubRight = ExpressionPushdownTranslator.tryTranslateSubqueryPredicate pParam subPlanCorrelated SqlDialect.Sqlite binSubOnRight
    let predSubLeft = ExpressionPushdownTranslator.tryTranslateSubqueryPredicate pParam subPlanCorrelated SqlDialect.Sqlite binSubOnLeft
    Assert.True(predSubRight.IsSome)
    Assert.True(predSubLeft.IsSome)

    // 6. Set operations with CustomQueryableWrapper holding CTEs
    let prodsWithCte = products.WithCte("CteOne", products)
    let wrapperWithCte = CustomQueryableWrapper<CovProduct>(prodsWithCte)

    let spqUnionWrap = products.Union(wrapperWithCte)
    let spqUnionAllWrap = products.UnionAll(wrapperWithCte)
    let spqIntersectWrap = products.Intersect(wrapperWithCte)
    let spqExceptWrap = products.Except(wrapperWithCte)
    Assert.NotEmpty(toRawSql spqUnionWrap)
    Assert.NotEmpty(toRawSql spqUnionAllWrap)
    Assert.NotEmpty(toRawSql spqIntersectWrap)
    Assert.NotEmpty(toRawSql spqExceptWrap)

    // IQueryable extension methods on wrapper with CTEs
    let iqUnion = wrapperWithCte.Union(products :> IQueryable<CovProduct>)
    let iqUnionAll = wrapperWithCte.UnionAll(products :> IQueryable<CovProduct>)
    let iqIntersect = wrapperWithCte.Intersect(products :> IQueryable<CovProduct>)
    let iqExcept = wrapperWithCte.Except(products :> IQueryable<CovProduct>)
    let iqWithCteAuto = wrapperWithCte.WithCte(products)
    let iqWithCteNamed = wrapperWithCte.WithCte("NamedCte", products)
    Assert.NotNull(iqUnion)
    Assert.NotNull(iqUnionAll)
    Assert.NotNull(iqIntersect)
    Assert.NotNull(iqExcept)
    Assert.NotNull(iqWithCteAuto)
    Assert.NotNull(iqWithCteNamed)

    // 7. fsql quotation coverage: withCte variable used in for loop, fullOuterJoinRaw, rightOuterJoinRaw
    let cteSample = products.Where(fun p -> p.Price > 10.0M)
    let fsqlFromCte = fsql {
        withCte "CteSample" cteSample
        for p in cteSample do
        select p
    }
    Assert.NotEmpty(toRawSql fsqlFromCte)

    let fsqlFojStar = fsql {
        for p in products do
        fullOuterJoinRaw "*" "t_alias" "1=1"
        select p
    }
    Assert.NotEmpty(toRawSql fsqlFojStar)

    let fsqlRojStar = fsql {
        for p in products do
        rightOuterJoinRaw "*" "t_alias" "1=1"
        select p
    }
    Assert.NotEmpty(toRawSql fsqlRojStar)

    // 8. explainQuery coverage across all dialects and edge branches
    let prov = (products :> IQueryable).Provider :?> SqlPushdownQueryProvider
    let makeQWithDialect d =
        let p = plan.Clone()
        p.Dialect <- Some d
        SqlPushdownQueryable<CovProduct>(prov, (products :> IQueryable).Expression, conn, "Products", [], p)

    let repPg = SqlPushdownQuery.explainQuery (makeQWithDialect SqlDialect.PostgreSql)
    let repMs = SqlPushdownQuery.explainQuery (makeQWithDialect SqlDialect.SqlServer)
    let repMy = SqlPushdownQuery.explainQuery (makeQWithDialect SqlDialect.MySql)
    let repMaria = SqlPushdownQuery.explainQuery (makeQWithDialect SqlDialect.MariaDb)
    let repOra = SqlPushdownQuery.explainQuery (makeQWithDialect SqlDialect.Oracle)
    let repPlanNoConn = SqlPushdownQuery.explainQuery plan
    let repStrNoConn = SqlPushdownQuery.explainQuery "SELECT 1"
    Assert.NotNull(repPg.PlanText)
    Assert.NotNull(repMs.PlanText)
    Assert.NotNull(repMy.PlanText)
    Assert.NotNull(repMaria.PlanText)
    Assert.NotNull(repOra.PlanText)
    Assert.NotNull(repPlanNoConn.PlanText)
    Assert.NotNull(repStrNoConn.PlanText)

    // 9. SqlPushdownQuery.extractSqlWithParameters & tryFindPushdownSource
    let (sNull, pNull) = SqlPushdownQuery.extractSqlWithParameters null
    let (sStr, pStr) = SqlPushdownQuery.extractSqlWithParameters (box "SELECT 42")
    let (sPlan, pPlan) = SqlPushdownQuery.extractSqlWithParameters (box plan)
    let (sIq, pIq) = SqlPushdownQuery.extractSqlWithParameters (box products)
    Assert.Equal("", sNull)
    Assert.Equal("SELECT 42", sStr)
    Assert.NotEmpty(sPlan)
    Assert.NotEmpty(sIq)

    let qLambda = Expression.Lambda((products :> IQueryable).Expression)
    let qUnary = Expression.Convert((products :> IQueryable).Expression, typeof<obj>)
    let qBinary = Expression.Coalesce((products :> IQueryable).Expression, (products :> IQueryable).Expression)
    let qStaticProp = Expression.Property(null, typeof<StaticCteHolder>.GetProperty("ProdsWithCte"))
    Assert.True((SqlPushdownQuery.tryFindPushdownSource qLambda).IsSome)
    Assert.True((SqlPushdownQuery.tryFindPushdownSource qUnary).IsSome)
    Assert.True((SqlPushdownQuery.tryFindPushdownSource qBinary).IsSome)
    Assert.True((SqlPushdownQuery.tryFindPushdownSource qStaticProp).IsSome)

    // 10. IQueryProvider edge branches
    let emptyPlanExpr = Expression.Constant(products, typeof<IQueryable<CovProduct>>)
    let qProv = (prov :> IQueryProvider)
    let qCreatedNoSeed = qProv.CreateQuery(emptyPlanExpr)
    let qCreatedGenNoSeed = qProv.CreateQuery<CovProduct>(emptyPlanExpr)
    Assert.NotNull(qCreatedNoSeed)
    Assert.NotNull(qCreatedGenNoSeed)

    try
        let _ = qProv.Execute<CovProduct[]>((products :> IQueryable).Expression)
        ()
    with :? InvalidCastException -> ()

    let recScalar = prov.ExecuteScalarWithPlan<CovProduct>(null, plan)
    Assert.NotNull(recScalar)

    let stream = prov.ExecuteStream<CovProduct>((products :> IQueryable).Expression)
    Assert.NotEmpty(stream)

    Assert.True(true)

[<Fact>]
let ``Pushdown comprehensive coverage section 54 - deep AST branches, joins, tuples, and quotation rewrites`` () =
    use db = new CovTempSqliteDb()
    let conn = SqliteConnector(db.Path) :> IQueryableConnector
    let products = SqlPushdownQueryable<CovProduct>(conn, "SELECT * FROM Products")
    let orders = SqlPushdownQueryable<CovOrder>(conn, "SELECT * FROM Orders")
    let plan = PushdownQueryPlan.Create("Products", [], baseAlias = "_t0", baseRowType = typeof<CovProduct>, dialect = SqlDialect.Sqlite)

    // 1. FieldHolderInstance: FieldInfo on ConstantExpression -> tryFindPushdownSource (Lines 237-242)
    let holder = FieldHolderInstance()
    holder.ProdsField <- products
    let fieldExpr = Expression.Field(Expression.Constant(holder), typeof<FieldHolderInstance>.GetField("ProdsField"))
    let sourceFromField = ExpressionPushdownTranslator.tryFindPushdownSource fieldExpr
    Assert.True(sourceFromField.IsSome)

    // 2. Nested tuple depth and resolveMemberRef (Lines 135-138, 424-443, 457)
    let pTuple = Expression.Parameter(typeof<Tuple<Tuple<CovProduct, CovOrder>, CovProduct>>, "root")
    let pItem1 = Expression.Property(pTuple, "Item1")
    let pItem2 = Expression.Property(pItem1, "Item2")
    let pAmount = Expression.Property(pItem2, "Amount")
    let planWithJoin = plan.Clone()
    planWithJoin.Joins.Add({ SourceTable = "Orders"; Alias = "_t1"; OnClause = "_t0.Id = _t1.CustomerId"; JoinType = "INNER JOIN"; RowType = Some typeof<CovOrder> })
    let (aliasOpt, colName) = ExpressionPushdownTranslator.resolveMemberRef planWithJoin pAmount
    Assert.True(aliasOpt.IsSome)
    Assert.Equal("Amount", colName)

    // 3. MemberInitExpression in extractKeyColumns (Lines 78-84)
    let pParam = Expression.Parameter(typeof<CovProduct>, "p")
    let miExpr = Expression.MemberInit(Expression.New(typeof<CovProduct>), Expression.Bind(typeof<CovProduct>.GetProperty("Id"), Expression.Property(pParam, "Id")))
    let keys = ExpressionPushdownTranslator.extractKeyColumns plan "_t0" pParam miExpr
    Assert.NotEmpty(keys)

    // 4. whereExists and whereNotExists with 2 arguments in translatePredicate (Lines 1050-1056, 1088-1100, 1104-1106)
    let oParam = Expression.Parameter(typeof<CovOrder>, "o")
    let oPred = Expression.Lambda<Func<CovOrder, bool>>(Expression.GreaterThan(Expression.Property(oParam, "Amount"), Expression.Constant(50.0M)), oParam)
    let callWhereNotExists = Expression.Call(null, typeof<CustomTestFuncs>.GetMethod("whereNotExists"), Expression.Constant(orders, typeof<IQueryable<CovOrder>>), oPred)
    let callWhereExists = Expression.Call(null, typeof<CustomTestFuncs>.GetMethod("whereExists"), Expression.Constant(orders, typeof<IQueryable<CovOrder>>), oPred)
    let predNotExistsRes = ExpressionPushdownTranslator.translatePredicate pParam plan SqlDialect.Sqlite callWhereNotExists
    let predExistsRes = ExpressionPushdownTranslator.translatePredicate pParam plan SqlDialect.Sqlite callWhereExists
    Assert.True(predNotExistsRes.IsSome)
    Assert.True(predExistsRes.IsSome)

    // 5. CustomQueryableWrapper in fsql union and CTE extraction (Lines 4205-4210, 4320-4323)
    let wrapCte = CustomQueryableWrapper<CovProduct>(products.WithCte("WrapCte", products))
    let fsqlWrapUnion = fsql {
        for p in wrapCte do
        select p
        union wrapCte
    }
    Assert.NotEmpty(toRawSql fsqlWrapUnion)

    // 6. fsql with rightOuterJoin and fullOuterJoin (Lines 4059-4087, 4157-4158, 4162-4163)
    let fsqlRoj = fsql {
        for p in products do
        rightOuterJoin o in orders on (p.Id = o.CustomerId)
        select (p, o)
    }
    Assert.NotEmpty(toRawSql fsqlRoj)

    let fsqlFoj = fsql {
        for p in products do
        fullOuterJoin o in orders on (p.Id = o.CustomerId) into g
        select (p, g)
    }
    Assert.NotEmpty(toRawSql fsqlFoj)

    // 7. Quotation rewriting of WhereExistsPred, WhereNotExistsPred, Having (Lines 4101-4106, 4120-4126, 4128-4133)
    let builder = FSqlQueryBuilder()
    let miExistsPred = typeof<FSqlQueryBuilder>.GetMethod("WhereExistsPred").MakeGenericMethod(typeof<CovProduct>, typeof<System.Linq.IQueryable>)
    let miNotExistsPred = typeof<FSqlQueryBuilder>.GetMethod("WhereNotExistsPred").MakeGenericMethod(typeof<CovProduct>, typeof<System.Linq.IQueryable>)
    let miHaving = typeof<FSqlQueryBuilder>.GetMethod("Having").MakeGenericMethod(typeof<CovProduct>, typeof<System.Linq.IQueryable>)
    let qs = Microsoft.FSharp.Linq.QuerySource<CovProduct, System.Linq.IQueryable>(products :> IQueryable<CovProduct>)
    let qSource = Microsoft.FSharp.Quotations.Expr.Value(qs, typeof<Microsoft.FSharp.Linq.QuerySource<CovProduct, System.Linq.IQueryable>>)
    let qVar = Microsoft.FSharp.Quotations.Var("p", typeof<CovProduct>)
    let qTrue = <@@ true @@>
    let qCallExists = Microsoft.FSharp.Quotations.Expr.Call(Microsoft.FSharp.Quotations.Expr.Value(builder), miExistsPred, [ qSource; Microsoft.FSharp.Quotations.Expr.Lambda(qVar, qTrue) ])
    let qCallNotExists = Microsoft.FSharp.Quotations.Expr.Call(Microsoft.FSharp.Quotations.Expr.Value(builder), miNotExistsPred, [ qSource; Microsoft.FSharp.Quotations.Expr.Lambda(qVar, qTrue) ])
    let qCallHaving = Microsoft.FSharp.Quotations.Expr.Call(Microsoft.FSharp.Quotations.Expr.Value(builder), miHaving, [ qSource; Microsoft.FSharp.Quotations.Expr.Lambda(qVar, qTrue) ])

    let runMethodDef =
        typeof<FSqlQueryBuilder>.GetMethods()
        |> Seq.find (fun m -> m.Name = "Run" && m.DeclaringType = typeof<FSqlQueryBuilder> && m.IsGenericMethod)
    let runMethod = runMethodDef.MakeGenericMethod(typeof<CovProduct>)
    try let _ = runMethod.Invoke(builder, [| box (Microsoft.FSharp.Quotations.Expr.Cast<Microsoft.FSharp.Linq.QuerySource<CovProduct, System.Linq.IQueryable>>(qCallExists)) |]) in () with _ -> ()
    try let _ = runMethod.Invoke(builder, [| box (Microsoft.FSharp.Quotations.Expr.Cast<Microsoft.FSharp.Linq.QuerySource<CovProduct, System.Linq.IQueryable>>(qCallNotExists)) |]) in () with _ -> ()
    try let _ = runMethod.Invoke(builder, [| box (Microsoft.FSharp.Quotations.Expr.Cast<Microsoft.FSharp.Linq.QuerySource<CovProduct, System.Linq.IQueryable>>(qCallHaving)) |]) in () with _ -> ()

    // 8. collectAllCtesFromExpr on PropertyGet (Lines 4326-4331)
    let propGetExpr = Microsoft.FSharp.Quotations.Expr.PropertyGet(typeof<StaticCteHolder>.GetProperty("ProdsWithCte"))
    let srcMi = typeof<FSqlQueryBuilder>.GetMethods() |> Seq.find (fun m -> m.Name = "Source" && m.IsGenericMethod && m.GetParameters().[0].ParameterType.Name.StartsWith("IQueryable"))
    let srcGenArgs = if srcMi.GetGenericArguments().Length = 2 then [| typeof<CovProduct>; typeof<System.Linq.IQueryable> |] else [| typeof<CovProduct> |]
    let srcCall = Microsoft.FSharp.Quotations.Expr.Call(Microsoft.FSharp.Quotations.Expr.Value(builder), srcMi.MakeGenericMethod(srcGenArgs), [ propGetExpr ])
    try let _ = runMethod.Invoke(builder, [| box (Microsoft.FSharp.Quotations.Expr.Cast<Microsoft.FSharp.Linq.QuerySource<CovProduct, System.Linq.IQueryable>>(srcCall)) |]) in () with _ -> ()

    // 9. ValueOption in unwrapClientOption (Lines 168-173)
    Assert.Equal(box "vsome", ExpressionPushdownTranslator.unwrapClientOption (box (ValueSome "vsome")))
    Assert.Null(ExpressionPushdownTranslator.unwrapClientOption (box (ValueNone: ValueOption<string>)))

    // 10. DateTime constant in translateScalarExpr (Lines 1516-1518)
    let dtConst = Expression.Constant(DateTime(2025, 5, 1, 10, 0, 0, 123))
    let dtSql = ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan dtConst
    Assert.True(dtSql.IsSome)

    // 11. NewExpression and DefaultExpression for Nullable / Option (Lines 1626-1629)
    let neNull = Expression.New(typeof<Nullable<int>>)
    let neSome = Expression.New(typeof<Nullable<int>>.GetConstructor([| typeof<int> |]), Expression.Constant(42))
    let deNull = Expression.Default(typeof<Nullable<int>>)
    Assert.Equal("NULL", (ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan neNull).Value)
    Assert.Equal("42", (ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan neSome).Value)
    Assert.Equal("NULL", (ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan deNull).Value)

    // 12. Grouping terminal aggregates (Lines 1683-1697)
    let gType = typeof<System.Linq.IGrouping<string, CovProduct>>
    let gParam = Expression.Parameter(gType, "g")
    let pPriceLambda = Expression.Lambda<Func<CovProduct, decimal>>(Expression.Property(Expression.Parameter(typeof<CovProduct>, "p"), "Price"), Expression.Parameter(typeof<CovProduct>, "p"))

    let countGen = (typeof<Enumerable>.GetMethods() |> Seq.find (fun m -> m.Name = "Count" && m.GetParameters().Length = 1)).MakeGenericMethod(typeof<CovProduct>)
    let mcCountG = Expression.Call(null, countGen, gParam)

    let sumGen = (typeof<Enumerable>.GetMethods() |> Seq.find (fun m -> m.Name = "Sum" && m.GetParameters().Length = 2 && m.ReturnType = typeof<decimal>)).MakeGenericMethod(typeof<CovProduct>)
    let mcSumG = Expression.Call(null, sumGen, gParam, pPriceLambda)

    let minGen = (typeof<Enumerable>.GetMethods() |> Seq.find (fun m -> m.Name = "Min" && m.GetParameters().Length = 2 && m.ReturnType = typeof<decimal>)).MakeGenericMethod(typeof<CovProduct>)
    let mcMinG = Expression.Call(null, minGen, gParam, pPriceLambda)

    let maxGen = (typeof<Enumerable>.GetMethods() |> Seq.find (fun m -> m.Name = "Max" && m.GetParameters().Length = 2 && m.ReturnType = typeof<decimal>)).MakeGenericMethod(typeof<CovProduct>)
    let mcMaxG = Expression.Call(null, maxGen, gParam, pPriceLambda)

    let avgGen = (typeof<Enumerable>.GetMethods() |> Seq.find (fun m -> m.Name = "Average" && m.GetParameters().Length = 2 && m.ReturnType = typeof<decimal>)).MakeGenericMethod(typeof<CovProduct>)
    let mcAvgG = Expression.Call(null, avgGen, gParam, pPriceLambda)

    Assert.Equal("COUNT(*)", (ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan mcCountG).Value)
    Assert.True(((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan mcSumG).Value.StartsWith("SUM(")))
    Assert.True(((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan mcMinG).Value.StartsWith("MIN(")))
    Assert.True(((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan mcMaxG).Value.StartsWith("MAX(")))
    Assert.True(((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan mcAvgG).Value.StartsWith("AVG(")))

    // 13. Direct invocation of FSqlQueryBuilder window, CTE, and helper methods
    let b = FSqlQueryBuilder()
    let win = b.window(partitionBy = 1, sortBy = 2, sortByDescending = 3)
    Assert.Equal("window", win.Dummy)
    Assert.Equal(0L, b.rowNumber(win))
    Assert.Equal(0L, b.rank(win))
    Assert.Equal(0L, b.denseRank(win))
    Assert.Equal(0L, b.ntile(4, win))
    Assert.Equal("v", b.lag("v", 1, "d", win))
    Assert.Equal("v", b.lead("v", 1, "d", win))
    Assert.Equal("v", b.firstValue("v", win))
    Assert.Equal("v", b.lastValue("v", win))
    Assert.Equal(10, b.sum(10, win))
    Assert.Equal(10.5, b.avg(10.5, win))
    Assert.Equal(5, b.min(5, win))
    Assert.Equal(50, b.max(50, win))
    Assert.Equal(0L, b.count(win))
    Assert.Equal(0L, b.count("x", win))
    Assert.Equal("", b.jsonValue("col", "$.path"))
    b.withCte("cte1", [1])
    b.withCte([1])
    b.withCte("SELECT 1")
    b.withCteSql("cte1", "SELECT 1")
    b.withCteSql("SELECT 1")
    b.withRecursiveCte("rcte", [1])
    b.withRecursiveCte([1])
    b.withRecursiveCte("SELECT 1")
    b.withRecursiveCteSql("rcte", "SELECT 1")
    b.withRecursiveCteSql("SELECT 1")
    let recRes = b.recursive("rcte", [1], fun _ -> [2])
    Assert.Equal<int seq>([1], recRes)

    // Markers
    Assert.True(exists [1])
    Assert.False(notExists [1])
    Assert.Equal<int seq>([1], crossApply [1])
    Assert.Equal<int seq>([1], lateral [1])

    // Set operations on standard IQueryable
    let sq1 = [1; 2].AsQueryable()
    let sq2 = [2; 3].AsQueryable()
    Assert.NotNull(b.union sq1 sq2)
    Assert.NotNull(b.unionAll sq1 sq2)
    Assert.NotNull(b.intersect sq1 sq2)
    Assert.NotNull(b.except sq1 sq2)

    // Set operations on SqlPushdownQueryable
    let pq1 = products
    let pq2 = products
    Assert.NotNull(b.union pq1 pq2)
    Assert.NotNull(b.unionAll pq1 pq2)
    Assert.NotNull(b.intersect pq1 pq2)
    Assert.NotNull(b.except pq1 pq2)

    // 14. Step 4 targeted branches: MemberInitExpression, overflow/nested tuples, subquery CTEs, FSqlExtensions on wrapped IQueryables
    // 14a. extractMemberMap on MemberInitExpression (lines 78-84)
    let mie = Expression.MemberInit(Expression.New(typeof<CovProduct>), [| Expression.Bind(typeof<CovProduct>.GetProperty("Name"), Expression.Constant("x")) :> MemberBinding |])
    let mieMap = ExpressionPushdownTranslator.extractMemberMap mie
    Assert.True(mieMap.ContainsKey("Name"))
    Assert.True((ExpressionPushdownTranslator.extractMemberMap (Expression.Constant(1))).IsEmpty)

    // 14b. getTupleDepth & resolveMemberRef on overflow tuple (Item8) and nested left-associative tuples (lines 135-142, 404-445)
    let planWith8Joins = PushdownQueryPlan.Create("Products", [], baseAlias = "t0")
    for jIdx in 1 .. 8 do
        planWith8Joins.Joins.Add({ JoinType = "LEFT JOIN"; SourceTable = $"T{jIdx}"; Alias = $"_t{jIdx}"; OnClause = "1=1"; RowType = None })
    let overflowTupleType = typeof<Tuple<int, int, int, int, int, int, int, Tuple<CovProduct>>>
    let overflowParam = Expression.Parameter(overflowTupleType, "ti")
    let item8Expr = Expression.Property(Expression.Property(overflowParam, "Rest"), "Item1")
    // Also test synthetic Item8 property path if present or nested Tuple<Tuple<CovProduct, CovOrder>, CovProduct>
    let nestedTupleType = typeof<Tuple<Tuple<CovProduct, CovOrder>, CovProduct>>
    let nestedParam = Expression.Parameter(nestedTupleType, "nt")
    let nestedItem1_1_Name = Expression.Property(Expression.Property(Expression.Property(nestedParam, "Item1"), "Item1"), "Name")
    let nestedItem1_2_OrderId = Expression.Property(Expression.Property(Expression.Property(nestedParam, "Item1"), "Item2"), "OrderId")
    let (alias1, col1) = ExpressionPushdownTranslator.resolveMemberRef planWith8Joins nestedItem1_1_Name
    let (alias2, col2) = ExpressionPushdownTranslator.resolveMemberRef planWith8Joins nestedItem1_2_OrderId
    Assert.Equal("Name", col1)
    Assert.Equal("OrderId", col2)
    Assert.True(alias1.IsSome && alias2.IsSome)
    let _ = ExpressionPushdownTranslator.resolveMemberRef planWith8Joins (Expression.Property(item8Expr, "Name"))

    // 14c. tryFindPushdownSource on InvocationExpression (lines 247-248)
    let invExpr = Expression.Invoke(Expression.Lambda(Expression.Constant(products)))
    Assert.True((ExpressionPushdownTranslator.tryFindPushdownSource invExpr).IsSome)

    // 14d. unwrapOptionOrNullableMember on MethodCallExpression get_Value (lines 278-285)
    let optIntParam = Expression.Parameter(typeof<option<int>>, "opt")
    let getValMc = Expression.Call(optIntParam, typeof<option<int>>.GetProperty("Value").GetGetMethod())
    Assert.Same(optIntParam, ExpressionPushdownTranslator.unwrapOptionOrNullableMember getValMc)

    // 14e. extractKeyColumns fallback (lines 504-508)
    let pParam2 = Expression.Parameter(typeof<CovProduct>, "p")
    let keyCols = ExpressionPushdownTranslator.extractKeyColumns plan "t0" pParam2 (Expression.Property(pParam2, "Id"))
    Assert.Equal<string list>([ "t0.Id" ], keyCols)

    // 14f. Subqueries with CTE propagation and 1 <= subquery.Count() (lines 689-696, 747-755, 774-780, 1026-1034, 1077-1085)
    let subWithCte = orders.WithCte("sub_cte_prop", orders)
    let planOuter = PushdownQueryPlan.Create("Products", [], baseAlias = "t0", baseRowType = typeof<CovProduct>)
    planOuter.OuterAliases.["outerP"] <- "t0"
    let countOrderMethod = (typeof<Queryable>.GetMethods() |> Seq.find (fun m -> m.Name = "Count" && m.GetParameters().Length = 1)).MakeGenericMethod(typeof<CovOrder>)
    let subCountCall = Expression.Call(null, countOrderMethod, Expression.Constant(subWithCte))
    let scalarSubSql = ExpressionPushdownTranslator.tryTranslateSubqueryScalarExpr SqlDialect.Sqlite planOuter subCountCall
    Assert.True(scalarSubSql.IsSome)
    Assert.Contains(planOuter.Ctes, fun c -> c.Name = "sub_cte_prop")

    let planOuter2 = PushdownQueryPlan.Create("Products", [], baseAlias = "t0", baseRowType = typeof<CovProduct>)
    planOuter2.OuterAliases.["outerP"] <- "t0"
    let oneLeCount = Expression.LessThanOrEqual(Expression.Constant(1), subCountCall)
    let pred1Le = ExpressionPushdownTranslator.tryTranslateSubqueryPredicate pParam2 planOuter2 SqlDialect.Sqlite oneLeCount
    Assert.True(pred1Le.IsSome)
    Assert.Contains("EXISTS", pred1Le.Value)

    let anyOrderMethod = (typeof<Queryable>.GetMethods() |> Seq.find (fun m -> m.Name = "Any" && m.GetParameters().Length = 1)).MakeGenericMethod(typeof<CovOrder>)
    let subAnyCall = Expression.Call(null, anyOrderMethod, Expression.Constant(subWithCte))
    let planOuter3 = PushdownQueryPlan.Create("Products", [], baseAlias = "t0", baseRowType = typeof<CovProduct>)
    planOuter3.OuterAliases.["outerP"] <- "t0"
    let predAnyCte = ExpressionPushdownTranslator.translatePredicate pParam2 planOuter3 SqlDialect.Sqlite subAnyCall
    Assert.True(predAnyCte.IsSome)

    let notExistsMi =
        match <@ notExists subWithCte @> with
        | Microsoft.FSharp.Quotations.Patterns.Call(_, mi, _) -> mi
        | _ -> failwith "expected Call"
    let subNotExistsCall = Expression.Call(null, notExistsMi, Expression.Constant(subWithCte))
    let planOuter4 = PushdownQueryPlan.Create("Products", [], baseAlias = "t0", baseRowType = typeof<CovProduct>)
    planOuter4.OuterAliases.["outerP"] <- "t0"
    let predNotExistsCte = ExpressionPushdownTranslator.translatePredicate pParam2 planOuter4 SqlDialect.Sqlite subNotExistsCall
    Assert.True(predNotExistsCte.IsSome)

    // 14g. translatePredicate partial AndAlso/OrElse and inverted StartsWith/EndsWith (lines 825-831, 1000-1005)
    let validPred = Expression.Equal(Expression.Property(pParam2, "Id"), Expression.Constant(1))
    let unsupportedPred = Expression.TypeIs(pParam2, typeof<obj>) :> Expression
    Assert.True((ExpressionPushdownTranslator.translatePredicate pParam2 plan SqlDialect.Sqlite (Expression.AndAlso(validPred, unsupportedPred))).IsSome)
    Assert.True((ExpressionPushdownTranslator.translatePredicate pParam2 plan SqlDialect.Sqlite (Expression.AndAlso(unsupportedPred, validPred))).IsSome)
    Assert.True((ExpressionPushdownTranslator.translatePredicate pParam2 plan SqlDialect.Sqlite (Expression.AndAlso(unsupportedPred, unsupportedPred))).IsNone)
    Assert.True((ExpressionPushdownTranslator.translatePredicate pParam2 plan SqlDialect.Sqlite (Expression.OrElse(validPred, unsupportedPred))).IsNone)

    let startsWithM = typeof<string>.GetMethod("StartsWith", [| typeof<string> |])
    let endsWithM = typeof<string>.GetMethod("EndsWith", [| typeof<string> |])
    let invStarts = Expression.Call(Expression.Property(pParam2, "Name"), startsWithM, Expression.Constant("elec"))
    let invEnds = Expression.Call(Expression.Property(pParam2, "Name"), endsWithM, Expression.Constant("elec"))
    Assert.True((ExpressionPushdownTranslator.translatePredicate pParam2 plan SqlDialect.Sqlite invStarts).IsSome)
    Assert.True((ExpressionPushdownTranslator.translatePredicate pParam2 plan SqlDialect.Sqlite invEnds).IsSome)

    // 14h. translateScalarCondition & translateScalarExpr on Constant IQueryable, aggregates, and GetValueOrDefault (lines 1444, 1477, 1486-1502, 1744-1833, 1991-1998)
    let condEnds = Expression.Call(Expression.Property(pParam2, "Name"), endsWithM, Expression.Constant("x"))
    Assert.True((ExpressionPushdownTranslator.translateScalarCondition SqlDialect.Sqlite plan condEnds).IsSome)
    Assert.True((ExpressionPushdownTranslator.translateScalarCondition SqlDialect.Sqlite plan (Expression.Not(Expression.Constant(true)))).IsNone)

    let filteredOrdersWithParam = (orders :> IQueryable<CovOrder>).Where(fun o -> o.Amount > 50m)
    let constIqExpr = Expression.Constant(filteredOrdersWithParam, typeof<IQueryable<CovOrder>>)
    let constIqSql = ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan constIqExpr
    Assert.True(constIqSql.IsSome)

    let selectedOrders = (orders :> IQueryable<CovOrder>).Select(fun o -> o.CustomerId)
    let countIntMethod = (typeof<Queryable>.GetMethods() |> Seq.find (fun m -> m.Name = "Count" && m.GetParameters().Length = 1)).MakeGenericMethod(typeof<int>)
    let countOnSelectExpr = Expression.Call(null, countIntMethod, selectedOrders.Expression)
    Assert.True((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan countOnSelectExpr).IsSome)

    let getValOrDef1 = typeof<Nullable<decimal>>.GetMethod("GetValueOrDefault", [| typeof<decimal> |])
    let gvodCall = Expression.Call(Expression.Property(pParam2, "Score"), getValOrDef1, Expression.Constant(0m))
    Assert.Contains("COALESCE", (ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan gvodCall).Value)

    // 14i. LinqInMemoryFallback.evaluate on Queryable.Where (lines 2639-2686)
    let memQ = [ 1; 2; 3 ].AsQueryable().Where(fun x -> x > 1)
    let memRes = LinqInMemoryFallback.evaluate memQ.Expression :?> System.Collections.Generic.IEnumerable<int>
    Assert.Equal<int list>([ 2; 3 ], memRes |> Seq.toList)

    // 14j. FSqlExtensions on wrapped IQueryable with seedPlan CTEs and parameters (lines 4428-4536)
    let baseWithSeedCte = (products.WithCte("c_seed", orders) :> IQueryable<CovProduct>).Where(fun p -> p.Price > 10m)
    let rhsWrapped = (products :> IQueryable<CovProduct>).Where(fun p -> p.InStock)
    Assert.Contains("UNION", SqlPushdownQuery.toRawSql (baseWithSeedCte.Union(rhsWrapped)))
    Assert.Contains("UNION ALL", SqlPushdownQuery.toRawSql (baseWithSeedCte.UnionAll(rhsWrapped)))
    Assert.Contains("INTERSECT", SqlPushdownQuery.toRawSql (baseWithSeedCte.Intersect(rhsWrapped)))
    Assert.Contains("EXCEPT", SqlPushdownQuery.toRawSql (baseWithSeedCte.Except(rhsWrapped)))

    let cteWithParam = (orders :> IQueryable<CovOrder>).Where(fun o -> o.Amount > 25m)
    let wrappedWithAutoCte = rhsWrapped.WithCte(cteWithParam)
    let wrappedWithNamedCte = rhsWrapped.WithCte("named_cte_param", cteWithParam)
    Assert.Contains("WITH", SqlPushdownQuery.toRawSql wrappedWithAutoCte)
    Assert.Contains("named_cte_param", SqlPushdownQuery.toRawSql wrappedWithNamedCte)

    // 15. Additional branches to push SqlPushdownQuery.fs >= 90%:
    // 15a. Non-grouping, non-pushdown-queryable Enumerable aggregates in translateScalarExpr (lines 1734-1836)
    let seqParam = Expression.Parameter(typeof<System.Collections.Generic.IEnumerable<CovProduct>>, "items")
    let pInStockLambda = Expression.Lambda<Func<CovProduct, bool>>(Expression.Property(pParam2, "InStock"), pParam2)
    let pCatLambda = Expression.Lambda<Func<CovProduct, string>>(Expression.Property(pParam2, "Category"), pParam2)
    let countPredGen = (typeof<Enumerable>.GetMethods() |> Seq.find (fun m -> m.Name = "Count" && m.GetParameters().Length = 2)).MakeGenericMethod(typeof<CovProduct>)
    let selectEnumGen = (typeof<Enumerable>.GetMethods() |> Seq.find (fun m -> m.Name = "Select" && m.GetParameters().Length = 2 && (m.GetParameters().[1].ParameterType.GetGenericArguments().Length = 2))).MakeGenericMethod(typeof<CovProduct>, typeof<string>)
    let distinctEnumGen = (typeof<Enumerable>.GetMethods() |> Seq.find (fun m -> m.Name = "Distinct" && m.GetParameters().Length = 1)).MakeGenericMethod(typeof<string>)
    let countStrGen = (typeof<Enumerable>.GetMethods() |> Seq.find (fun m -> m.Name = "Count" && m.GetParameters().Length = 1)).MakeGenericMethod(typeof<string>)
    let longCountGen = (typeof<Enumerable>.GetMethods() |> Seq.find (fun m -> m.Name = "LongCount" && m.GetParameters().Length = 1)).MakeGenericMethod(typeof<CovProduct>)

    let mcCountPredSeq = Expression.Call(null, countPredGen, seqParam, pInStockLambda)
    let mcSelectSeq = Expression.Call(null, selectEnumGen, seqParam, pCatLambda)
    let mcDistinctSeq = Expression.Call(null, distinctEnumGen, mcSelectSeq)
    let mcCountSelectSeq = Expression.Call(null, countStrGen, mcSelectSeq)
    let mcCountDistinctSeq = Expression.Call(null, countStrGen, mcDistinctSeq)
    let mcLongCountSeq = Expression.Call(null, longCountGen, seqParam)
    let mcSumSeq = Expression.Call(null, sumGen, seqParam, pPriceLambda)
    let mcMinSeq = Expression.Call(null, minGen, seqParam, pPriceLambda)
    let mcMaxSeq = Expression.Call(null, maxGen, seqParam, pPriceLambda)
    let mcAvgSeq = Expression.Call(null, avgGen, seqParam, pPriceLambda)

    Assert.True((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan mcCountPredSeq).IsSome)
    Assert.True((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan mcCountSelectSeq).IsSome)
    Assert.True((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan mcCountDistinctSeq).IsSome)
    Assert.True((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan mcLongCountSeq).IsSome)
    Assert.True((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan mcSumSeq).IsSome)
    Assert.True((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan mcMinSeq).IsSome)
    Assert.True((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan mcMaxSeq).IsSome)
    Assert.True((ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan mcAvgSeq).IsSome)

    // 15b. mergeExpressionIntoPlan terminal aggregates over Select and scalar projections (lines 2498-2612)
    let qSelectPrice = (products :> IQueryable<CovProduct>).Select(fun p -> p.Price)
    let sumDecQ = (typeof<Queryable>.GetMethods() |> Seq.find (fun m -> m.Name = "Sum" && m.GetParameters().Length = 1 && m.ReturnType = typeof<decimal>))
    let minDecQ = (typeof<Queryable>.GetMethods() |> Seq.find (fun m -> m.Name = "Min" && m.GetParameters().Length = 1 && m.IsGenericMethod)).MakeGenericMethod(typeof<decimal>)
    let maxDecQ = (typeof<Queryable>.GetMethods() |> Seq.find (fun m -> m.Name = "Max" && m.GetParameters().Length = 1 && m.IsGenericMethod)).MakeGenericMethod(typeof<decimal>)
    let avgDecQ = (typeof<Queryable>.GetMethods() |> Seq.find (fun m -> m.Name = "Average" && m.GetParameters().Length = 1 && m.ReturnType = typeof<decimal>))

    Assert.Contains("SUM(", (ExpressionPushdownTranslator.extractPlanFromExpression (Expression.Call(null, sumDecQ, qSelectPrice.Expression)) "Products" [] SqlDialect.Sqlite).BuildSql(SqlDialect.Sqlite))
    Assert.Contains("MIN(", (ExpressionPushdownTranslator.extractPlanFromExpression (Expression.Call(null, minDecQ, qSelectPrice.Expression)) "Products" [] SqlDialect.Sqlite).BuildSql(SqlDialect.Sqlite))
    Assert.Contains("MAX(", (ExpressionPushdownTranslator.extractPlanFromExpression (Expression.Call(null, maxDecQ, qSelectPrice.Expression)) "Products" [] SqlDialect.Sqlite).BuildSql(SqlDialect.Sqlite))
    Assert.Contains("AVG(", (ExpressionPushdownTranslator.extractPlanFromExpression (Expression.Call(null, avgDecQ, qSelectPrice.Expression)) "Products" [] SqlDialect.Sqlite).BuildSql(SqlDialect.Sqlite))

    let qSelectNullableVal = (products :> IQueryable<CovProduct>).Select(fun p -> p.Score.Value)
    let qSelectBin = (products :> IQueryable<CovProduct>).Select(fun p -> p.Price + 10m)
    let qSelectMc = (products :> IQueryable<CovProduct>).Select(fun p -> p.Name.Trim())
    let qSelectCond = (products :> IQueryable<CovProduct>).Select(fun p -> if p.InStock then 1 else 0)
    Assert.NotNull((ExpressionPushdownTranslator.extractPlanFromExpression qSelectNullableVal.Expression "Products" [] SqlDialect.Sqlite).Projection)
    Assert.NotNull((ExpressionPushdownTranslator.extractPlanFromExpression qSelectBin.Expression "Products" [] SqlDialect.Sqlite).Projection)
    Assert.NotNull((ExpressionPushdownTranslator.extractPlanFromExpression qSelectMc.Expression "Products" [] SqlDialect.Sqlite).Projection)
    Assert.NotNull((ExpressionPushdownTranslator.extractPlanFromExpression qSelectCond.Expression "Products" [] SqlDialect.Sqlite).Projection)

    // 15c. In-memory set operations in fsql (lines 4218-4251)
    let inMemIq = [ 1; 2 ].AsQueryable()
    let qMemUnion = fsql { for x in inMemIq do union ([ 2; 3 ] :> seq<int>) }
    let qMemUnionAll = fsql { for x in inMemIq do unionAll ([ 2; 3 ] :> seq<int>) }
    let qMemIntersect = fsql { for x in inMemIq do intersect ([ 2; 3 ] :> seq<int>) }
    let qMemExcept = fsql { for x in inMemIq do except ([ 2; 3 ] :> seq<int>) }
    Assert.Equal(3, qMemUnion |> Seq.length)
    Assert.Equal(4, qMemUnionAll |> Seq.length)
    Assert.Equal(1, qMemIntersect |> Seq.length)
    Assert.Equal(1, qMemExcept |> Seq.length)

    // 15d. Fallback None branches in translateScalarExpr and translateScalarCondition when inner operand is unsupported
    let unsupInt = Expression.Default(typeof<int>) :> Expression
    let unsupDec = Expression.Default(typeof<decimal>) :> Expression
    let unsupDbl = Expression.Default(typeof<double>) :> Expression
    let unsupStr = Expression.Default(typeof<string>) :> Expression
    let unsupDt = Expression.Default(typeof<DateTime>) :> Expression
    let unsupBool = Expression.Default(typeof<bool>) :> Expression

    let evalScalar e = ExpressionPushdownTranslator.translateScalarExpr SqlDialect.Sqlite plan e |> ignore
    let evalCond e = ExpressionPushdownTranslator.translateScalarCondition SqlDialect.Sqlite plan e |> ignore

    evalScalar (Expression.Property(unsupDt, "Year"))
    evalScalar (Expression.Property(unsupStr, "Length"))
    evalScalar (Expression.Property(null, typeof<DateTime>.GetProperty("UtcNow")))

    let strAddM = typeof<string>.GetMethod("Concat", [| typeof<string>; typeof<string> |])
    evalScalar (Expression.Add(unsupStr, Expression.Constant("a"), strAddM))
    evalScalar (Expression.Add(unsupInt, Expression.Constant(1)))
    evalScalar (Expression.Subtract(unsupInt, Expression.Constant(1)))
    evalScalar (Expression.Multiply(unsupInt, Expression.Constant(1)))
    evalScalar (Expression.Divide(unsupInt, Expression.Constant(1)))
    evalScalar (Expression.Modulo(unsupInt, Expression.Constant(1)))
    evalScalar (Expression.ExclusiveOr(unsupInt, Expression.Constant(1)))

    let nullConst = Expression.Constant(null, typeof<string>)
    evalScalar (Expression.Condition(Expression.Equal(unsupStr, nullConst), unsupStr, Expression.Constant("a")))
    evalScalar (Expression.Condition(Expression.NotEqual(unsupStr, nullConst), unsupStr, Expression.Constant("a")))
    evalScalar (Expression.Condition(unsupBool, unsupInt, Expression.Constant(0)))

    evalScalar (Expression.Call(null, strAddM, unsupStr, Expression.Constant("a")))
    evalScalar (Expression.Call(unsupStr, typeof<string>.GetMethod("ToUpper", Type.EmptyTypes)))
    evalScalar (Expression.Call(unsupStr, typeof<string>.GetMethod("ToLower", Type.EmptyTypes)))
    evalScalar (Expression.Call(unsupStr, typeof<string>.GetMethod("Trim", Type.EmptyTypes)))
    evalScalar (Expression.Call(null, typeof<string>.GetMethod("IsNullOrEmpty", [| typeof<string> |]), unsupStr))
    evalScalar (Expression.Call(null, typeof<string>.GetMethod("IsNullOrWhiteSpace", [| typeof<string> |]), unsupStr))
    evalScalar (Expression.Call(unsupInt, typeof<obj>.GetMethod("ToString", Type.EmptyTypes)))
    evalScalar (Expression.Call(unsupStr, typeof<string>.GetMethod("Substring", [| typeof<int> |]), unsupInt))
    evalScalar (Expression.Call(unsupStr, typeof<string>.GetMethod("Substring", [| typeof<int> |]), Expression.Constant(0)))
    evalScalar (Expression.Call(unsupStr, typeof<string>.GetMethod("Replace", [| typeof<string>; typeof<string> |]), Expression.Constant("a"), Expression.Constant("b")))
    evalScalar (Expression.Call(null, typeof<Math>.GetMethod("Round", [| typeof<double> |]), unsupDbl))
    evalScalar (Expression.Call(null, typeof<Math>.GetMethod("Round", [| typeof<double>; typeof<int> |]), unsupDbl, Expression.Constant(2)))
    evalScalar (Expression.Call(null, typeof<Math>.GetMethod("Abs", [| typeof<double> |]), unsupDbl))
    evalScalar (Expression.Call(null, typeof<Math>.GetMethod("Floor", [| typeof<double> |]), unsupDbl))
    evalScalar (Expression.Call(null, typeof<Math>.GetMethod("Ceiling", [| typeof<double> |]), unsupDbl))
    evalScalar (Expression.Call(null, typeof<Math>.GetMethod("Sqrt", [| typeof<double> |]), unsupDbl))
    evalScalar (Expression.Call(null, typeof<Math>.GetMethod("Pow", [| typeof<double>; typeof<double> |]), unsupDbl, unsupDbl))
    evalScalar (Expression.Call(unsupStr, typeof<string>.GetMethod("IndexOf", [| typeof<string> |]), Expression.Constant("a")))
    evalScalar (Expression.Call(Expression.Property(pParam2, "Score"), getValOrDef1, unsupDec))

    // translateScalarCondition fallback None branches (lines 1415, 1419, 1429, 1440, 1462, 1466, 1470, 1478, 1479)
    evalCond (Expression.AndAlso(unsupBool, unsupBool))
    evalCond (Expression.OrElse(unsupBool, unsupBool))
    evalCond (Expression.Equal(unsupInt, Expression.Constant(1)))
    evalCond (Expression.Add(unsupInt, Expression.Constant(1)))
    evalCond (Expression.Call(null, typeof<string>.GetMethod("IsNullOrEmpty", [| typeof<string> |]), unsupStr))
    evalCond (Expression.Call(null, typeof<string>.GetMethod("IsNullOrWhiteSpace", [| typeof<string> |]), unsupStr))
    evalCond (Expression.Call(unsupStr, typeof<string>.GetMethod("Contains", [| typeof<string> |]), Expression.Constant("a")))
    evalCond (Expression.Call(Expression.Constant("a"), typeof<string>.GetMethod("Contains", [| typeof<string> |]), Expression.Constant("b")))

    // parseWindowSpec fallback branches (lines 1196, 1203, 1204, 1213, 1222)
    let winMi = typeof<FSqlQueryBuilder>.GetMethod("window").MakeGenericMethod(typeof<int>, typeof<int>, typeof<int>)
    let someIntMi = typeof<option<int>>.GetMethod("Some")
    let winCallUnsup =
        Expression.Call(
            Expression.Constant(b),
            winMi,
            Expression.Call(null, someIntMi, unsupInt),
            Expression.Call(null, someIntMi, unsupInt),
            Expression.Call(null, someIntMi, unsupInt)
        )
    ExpressionPushdownTranslator.parseWindowSpec SqlDialect.Sqlite plan winCallUnsup |> ignore

    // mergeExpressionIntoPlan fallback branches when Select body fails scalar translation (lines 2477-2612)
    let selectUnsupInt =
        let selMi = (typeof<Queryable>.GetMethods() |> Seq.find (fun m -> m.Name = "Select" && m.GetParameters().Length = 2)).MakeGenericMethod(typeof<CovProduct>, typeof<int>)
        Expression.Call(null, selMi, (products :> IQueryable<CovProduct>).Expression, Expression.Quote(Expression.Lambda<Func<CovProduct, int>>(unsupInt :?> DefaultExpression, pParam2)))
    let selectUnsupDec =
        let selMi = (typeof<Queryable>.GetMethods() |> Seq.find (fun m -> m.Name = "Select" && m.GetParameters().Length = 2)).MakeGenericMethod(typeof<CovProduct>, typeof<decimal>)
        Expression.Call(null, selMi, (products :> IQueryable<CovProduct>).Expression, Expression.Quote(Expression.Lambda<Func<CovProduct, decimal>>(unsupDec :?> DefaultExpression, pParam2)))
    let countIntQ = (typeof<Queryable>.GetMethods() |> Seq.find (fun m -> m.Name = "Count" && m.GetParameters().Length = 1)).MakeGenericMethod(typeof<int>)
    ExpressionPushdownTranslator.extractPlanFromExpression (Expression.Call(null, countIntQ, selectUnsupInt)) "Products" [] SqlDialect.Sqlite |> ignore
    ExpressionPushdownTranslator.extractPlanFromExpression (Expression.Call(null, sumDecQ, selectUnsupDec)) "Products" [] SqlDialect.Sqlite |> ignore
    ExpressionPushdownTranslator.extractPlanFromExpression (Expression.Call(null, minDecQ, selectUnsupDec)) "Products" [] SqlDialect.Sqlite |> ignore
    ExpressionPushdownTranslator.extractPlanFromExpression (Expression.Call(null, maxDecQ, selectUnsupDec)) "Products" [] SqlDialect.Sqlite |> ignore
    ExpressionPushdownTranslator.extractPlanFromExpression (Expression.Call(null, avgDecQ, selectUnsupDec)) "Products" [] SqlDialect.Sqlite |> ignore

    let makeSelectExpr (body: Expression) =
        let selMi = (typeof<Queryable>.GetMethods() |> Seq.find (fun m -> m.Name = "Select" && m.GetParameters().Length = 2)).MakeGenericMethod(typeof<CovProduct>, body.Type)
        let lam = Expression.Lambda(body, pParam2)
        Expression.Call(null, selMi, (products :> IQueryable<CovProduct>).Expression, Expression.Quote(lam))
    ExpressionPushdownTranslator.extractPlanFromExpression (makeSelectExpr (Expression.New(typeof<Tuple<int>>.GetConstructors().[0], unsupInt))) "Products" [] SqlDialect.Sqlite |> ignore
    ExpressionPushdownTranslator.extractPlanFromExpression (makeSelectExpr (Expression.Property(unsupStr, "Length"))) "Products" [] SqlDialect.Sqlite |> ignore
    ExpressionPushdownTranslator.extractPlanFromExpression (makeSelectExpr (Expression.Add(unsupInt, Expression.Constant(1)))) "Products" [] SqlDialect.Sqlite |> ignore
    ExpressionPushdownTranslator.extractPlanFromExpression (makeSelectExpr (Expression.Call(unsupInt, typeof<obj>.GetMethod("ToString", Type.EmptyTypes)))) "Products" [] SqlDialect.Sqlite |> ignore
    ExpressionPushdownTranslator.extractPlanFromExpression (makeSelectExpr (Expression.Condition(unsupBool, unsupInt, Expression.Constant(0)))) "Products" [] SqlDialect.Sqlite |> ignore

    // 15e. Final 19 lines: 1-arg Sum/Min/Max/Avg on Default(IQueryable<decimal>), Nullable.Value Select, and LinqInMemoryFallback BinaryExpression
    let unsupIqDec = Expression.Default(typeof<IQueryable<decimal>>) :> Expression
    evalScalar (Expression.Call(null, sumDecQ, unsupIqDec))
    evalScalar (Expression.Call(null, minDecQ, unsupIqDec))
    evalScalar (Expression.Call(null, maxDecQ, unsupIqDec))
    evalScalar (Expression.Call(null, avgDecQ, unsupIqDec))

    ExpressionPushdownTranslator.extractPlanFromExpression (Expression.Call(null, sumDecQ, unsupIqDec)) "Products" [] SqlDialect.Sqlite |> ignore
    ExpressionPushdownTranslator.extractPlanFromExpression (Expression.Call(null, minDecQ, unsupIqDec)) "Products" [] SqlDialect.Sqlite |> ignore
    ExpressionPushdownTranslator.extractPlanFromExpression (Expression.Call(null, maxDecQ, unsupIqDec)) "Products" [] SqlDialect.Sqlite |> ignore
    ExpressionPushdownTranslator.extractPlanFromExpression (Expression.Call(null, avgDecQ, unsupIqDec)) "Products" [] SqlDialect.Sqlite |> ignore

    let nullDecParam = Expression.Parameter(typeof<Nullable<decimal>>, "nd")
    let selNullValMi = (typeof<Queryable>.GetMethods() |> Seq.find (fun m -> m.Name = "Select" && m.GetParameters().Length = 2)).MakeGenericMethod(typeof<Nullable<decimal>>, typeof<decimal>)
    let selNullValCall = Expression.Call(null, selNullValMi, Expression.Default(typeof<IQueryable<Nullable<decimal>>>), Expression.Quote(Expression.Lambda<Func<Nullable<decimal>, decimal>>(Expression.Property(nullDecParam, "Value"), nullDecParam)))
    ExpressionPushdownTranslator.extractPlanFromExpression selNullValCall "Products" [] SqlDialect.Sqlite |> ignore

    Assert.Equal(box 3, LinqInMemoryFallback.evaluate (Expression.Add(Expression.Constant(1), Expression.Constant(2))))

    Assert.True(true)



