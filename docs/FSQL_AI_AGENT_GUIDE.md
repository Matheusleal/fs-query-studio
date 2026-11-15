# FSQL (F# Query Language) & Extensions — AI Agent Guide

[🇺🇸 English](FSQL_AI_AGENT_GUIDE.md) | [🇧🇷 Versão em Português](FSQL_AI_AGENT_GUIDE_PT.md)

> **Target Audience**: AI Coding Assistants, LLMs, and Automated Agents developing queries for **F# Query Studio**.  
> **Rule**: When generating F# code that interacts with databases or data sources in F# Query Studio, agents **MUST** follow this specification.

---

## 1. Executive Summary & Philosophy

In F# Query Studio, **`fsql`** (**F# Query Language**) is the unified, first-class computation expression designed for functional, type-safe data querying with complete pushdown to database engines.

### Why `fsql` over Standard LINQ `query { ... }`?
- **Column-level Aggregates Pushdown**: Solves the SQL Server bug where `count` on a projected column failed with invalid SQL.
- **Dedicated Correlated Operations**: Native keywords for `whereExists`, `whereNotExists`, `having`, `crossApply`, and `lateral`.
- **Seamless Subquery Nesting**: The exact same `fsql { ... }` keyword works for both top-level queries and deeply nested subqueries (scalar sub-selects, `EXISTS`, `IN`).
- **Zero Raw SQL String Concatenation**: AI agents should **never** emit SQL string templates (`"SELECT * FROM..."`); write 100% pure F# DSL.
- **Smart Dialect Translation**: Automatically emits optimized T-SQL (SQL Server 2016+ / 2022), PL/pgSQL (PostgreSQL 10+ / 17), and SQLite 3.

---

## 2. Standard Session Imports & Connection Access

When writing scripts or notebook cells, agents must include:

```fsharp
open System
open System.Linq
open FSharpQueryStudio.Connectors
open FSharpQueryStudio.Generated
```

### Table Resolution Pattern: `Connections.sql`
Databases configured in `.fsqs/workspace.json` are typed and exposed through `Connections.sql.<ConnectionName>.<TableName>`:

```fsharp
// SQL Server
let mssqlProducts = Connections.sql.SqlServer_Demo.Products
let mssqlOrders   = Connections.sql.SqlServer_Demo.Orders

// PostgreSQL (table and column names respect Postgres lower_case conventions)
let pgProducts = Connections.sql.PostgreSQL_Demo.products
let pgOrders   = Connections.sql.PostgreSQL_Demo.orders

// SQLite
let sqliteProducts = Connections.sql.SQLite_Demo.Products
let sqliteOrders   = Connections.sql.SQLite_Demo.Orders
```

---

## 3. Comprehensive `fsql` Syntax Catalog

### 3.1 Basic Queries, Filtering, Sorting & Pagination
```fsharp
let pageOfProducts =
    fsql {
        for p in mssqlProducts do
        where (p.Price >= 50.0M && p.InStock)
        sortByDescending p.Price
        thenBy p.Name
        skip 10
        take 5
        select {| Id = p.Id; Name = p.Name; Price = p.Price |}
    } |> Seq.toList
```

### 3.2 Aggregations (Pushdown to `COUNT`, `MAX`, `MIN`, `SUM`, `AVG`)
```fsharp
// Table-level count
let totalRows = fsql { for p in mssqlProducts do count }

// Column-level count (guaranteed safe pushdown across all dialects)
let totalIds = fsql { for p in mssqlProducts do select p.Id; count }

// Extremes and sums
let maxPrice = fsql { for p in mssqlProducts do maxBy p.Price }
let minPrice = fsql { for p in mssqlProducts do minBy p.Price }
let sumPrice = fsql { for p in mssqlProducts do sumBy p.Price }
let avgPrice = fsql { for p in mssqlProducts do averageBy p.Price }
```

### 3.3 In-List Filtering (Collections & Subqueries)
```fsharp
// 1. Parameterized IN with local F# collections
let targetCategories = [ "Electronics"; "Hardware" ]
let filteredByList =
    fsql {
        for p in mssqlProducts do
        where (targetCategories.Contains(p.Category))
        select p.Name
    }

// 2. Transpiled IN with nested fsql subquery:
// Transpiles to: WHERE (p.Id IN (SELECT o.ProductId FROM Orders o WHERE ...))
let productsWithLargeOrders =
    fsql {
        for p in mssqlProducts do
        where ((fsql { for o in mssqlOrders do where (o.Quantity >= 5); select o.ProductId }).Contains(p.Id))
        select p.Name
    }
```

### 3.4 Correlated Subqueries (`whereExists` & `whereNotExists`)
Agents must prefer `whereExists` / `whereNotExists` over manual count comparisons:

```fsharp
// Transpiles to: WHERE EXISTS (SELECT 1 FROM Orders _sub WHERE _sub.ProductId = _t0.Id)
let activeProducts =
    fsql {
        for p in mssqlProducts do
        whereExists (fsql { for o in mssqlOrders do where (o.ProductId = p.Id) })
        select {| Id = p.Id; Name = p.Name |}
    }

// Transpiles to: WHERE NOT EXISTS (SELECT 1 FROM Orders _sub WHERE _sub.ProductId = _t0.Id)
let abandonedProducts =
    fsql {
        for p in mssqlProducts do
        whereNotExists (fsql { for o in mssqlOrders do where (o.ProductId = p.Id) })
        select {| Id = p.Id; Name = p.Name |}
    }
```

### 3.5 Scalar Subqueries in `select` Projections
Compute per-row correlated counts or sums directly in the `SELECT` projection:

```fsharp
// Transpiles to:
// SELECT _t0.Name AS ProductName,
//        (SELECT COUNT(*) FROM Orders _sub WHERE _sub.ProductId = _t0.Id) AS OrderCount
// FROM Products _t0
let catalog =
    fsql {
        for p in mssqlProducts do
        select {|
            ProductName = p.Name
            Price = p.Price
            OrderCount = fsql { for o in mssqlOrders do where (o.ProductId = p.Id); count }
        |}
    }
```

### 3.6 Correlated Joins (`crossApply` & `lateral`)
Use explicit operations for correlated top-N or row-by-row expansion:

- **SQL Server (`crossApply`)**: Transpiles to `CROSS APPLY (SELECT ... OFFSET 0 ROWS FETCH NEXT N ROWS ONLY) AS _t1`.
- **PostgreSQL (`lateral`)**: Transpiles to `CROSS JOIN LATERAL (SELECT ... LIMIT N) AS _t1`.
- **SQLite**: Executes with transparent memory-fallback.

```fsharp
// 1. SQL Server: CROSS APPLY
let topOrderPerProduct =
    fsql {
        for p in mssqlProducts do
        for o in (crossApply (fsql { for o in mssqlOrders do where (o.ProductId = p.Id); sortByDescending o.UnitPrice; take 1 })) do
        select {|
            ProductName = p.Name
            TopOrderPrice = o.UnitPrice
            Quantity = o.Quantity
        |}
    }

// 2. PostgreSQL: CROSS JOIN LATERAL
let pgTopOrderPerProduct =
    fsql {
        for p in pgProducts do
        for o in (lateral (fsql { for o in pgOrders do where (o.product_id = p.id); sortByDescending o.unit_price; take 1 })) do
        select {|
            ProductName = p.name
            TopOrderPrice = o.unit_price
        |}
    }
```

### 3.7 Grouping & Dedicated `having` Filtering
Filters on grouped aggregates must use `having`:

```fsharp
// Transpiles to:
// GROUP BY _t0.ProductId
// HAVING (COUNT(*) >= @p0)
let highVolumeProducts =
    fsql {
        for o in mssqlOrders do
        groupBy o.ProductId into g
        having (fsql { for item in g do count } >= 2)
        select {|
            ProductId = g.Key
            TotalOrders = fsql { for item in g do count }
        |}
    }
```

### 3.8 Window Functions & Analytics (`fsql.window`, `rowNumber`, `rank`, `lag`, `lead`)
Analytics and ranking functions map directly to SQL standard `OVER (PARTITION BY ... ORDER BY ...)`:

```fsharp
let rankedProducts =
    fsql {
        for p in mssqlProducts do
        select {|
            Category = p.Category
            Name = p.Name
            Price = p.Price
            RowInCat = fsql.rowNumber(fsql.window(partitionBy = p.Category, sortByDescending = p.Price))
            RankInCat = fsql.rank(fsql.window(partitionBy = p.Category, sortByDescending = p.Price))
            RunningTotal = fsql.sum(p.Price, over = fsql.window(partitionBy = p.Category, sortBy = p.Id))
        |}
    }
```

### 3.9 Semi-Structured JSON Navigation (`fsql.jsonValue`)
Pushes scalar extraction directly to database engines (`JSON_VALUE` on SQL Server, `->>` on Postgres, `json_extract` on SQLite):

```fsharp
let errors =
    fsql {
        for log in mssqlLogs do
        where (fsql.jsonValue(log.Payload, "status") = "ERROR")
        select {|
            Id = log.Id
            UserId = fsql.jsonValue(log.Payload, "user.id")
            Code = fsql.jsonValue(log.Payload, "error.code")
        |}
    }
```

### 3.10 Common Table Expressions (CTEs) & Set Operations (Transparent Composition)
FSQL supports declaring CTEs and set operations in parts as separate variables, and combining them seamlessly with **zero dummy tables** or manual registration:

```fsharp
// 1. Unnamed CTE (Preferred): Injected transparently without requiring dummy table registration or explicit names
let highStockProducts =
    fsql {
        for p in Connections.sql.SampleStore.Products do
        where (p.StockQuantity >= 100)
        select p
    }

let catalogWithUnnamedCte =
    fsql {
        for c in Connections.sql.SampleStore.Categories do
        withCte highStockProducts
        join p in highStockProducts on (c.Id = p.CategoryId)
        select {| Category = c.Name; Product = p.Name; Stock = p.StockQuantity |}
    }

// 2. Named CTE: Explicit identifier overload
let highPriceProducts =
    fsql {
        for p in Connections.sql.SampleStore.Products do
        withCte "HighEnd" (fsql { for p2 in Connections.sql.SampleStore.Products do where (p2.Price >= 500.0f); select p2 })
        where (p.Price >= 200.0f)
        select p
    }

let budgetProducts =
    fsql {
        for p in Connections.sql.SampleStore.Products do
        withCte "Budget" (fsql { for p2 in Connections.sql.SampleStore.Products do where (p2.Price <= 30.0f); select p2 })
        where (p.Price < 15.0f)
        select p
    }

// 3. Raw Recursive CTEs (T-SQL, PostgreSQL, SQLite)
let orgHierarchy =
    fsql {
        for e in Connections.sql.SampleStore.Employees do
        withRecursiveCteSql "OrgHierarchy" """
            SELECT Id, Name, ManagerId, 1 AS Level
            FROM Employees WHERE ManagerId IS NULL
            UNION ALL
            SELECT e.Id, e.Name, e.ManagerId, h.Level + 1
            FROM Employees e
            INNER JOIN OrgHierarchy h ON e.ManagerId = h.Id
        """
        select e
    }

// 4. Composition in 4 equivalent forms (CTEs bubble up into top-level WITH automatically without duplicate WITH clauses):

// Form A: Inside Computation Expression (fsql)
let combinedA =
    fsql {
        for p in highPriceProducts do
        union budgetProducts
    }

// Form B: Curried builder function
let combinedB = fsql.union highPriceProducts budgetProducts

// Form C: Module function
let combinedC = union highPriceProducts budgetProducts

// Form D: Extension method on IQueryable
let combinedD = highPriceProducts.Union(budgetProducts)

// In-CE Set Operations: union, unionAll, intersect, except
let allItems =
    fsql {
        for a in activeItems do
        unionAll archivedItems
    }

let commonItems =
    fsql {
        for r in retailItems do
        intersect wholesaleItems
    }

let exclusiveItems =
    fsql {
        for r in retailItems do
        except wholesaleItems
    }

// 5. Composition of Subqueries in whereExists and crossApply:
let completedOrders =
    fsql {
        for o in Connections.sql.SampleStore.Orders do
        where (o.Status = "Completed")
        select o
    }

let customersWithOrders =
    fsql {
        for c in Connections.sql.SampleStore.Customers do
        whereExists (fsql { for o in completedOrders do where (o.CustomerId = c.Id) })
        select c
    }
```

### 3.11 Idiomatic Outer Joins inside `fsql` CE
AI agents must generate 100% idiomatic LINQ `GroupJoin` syntax using `into <group>` and `for <var> in <group>.DefaultIfEmpty() do`:

```fsharp
// 1. Full Outer Join directly within fsql
let suppliersProducts =
    fsql {
        for p in Connections.sql.SampleStore.Products do
        fullOuterJoin s in Connections.sql.SampleStore.Suppliers on (p.SupplierId = s.Id) into suppliers
        for s in suppliers.DefaultIfEmpty() do
        select {|
            Product = p.Name
            Supplier = (if isNull (box s) then "Unknown" else s.Name)
        |}
    }

// 2. Right Outer Join directly within fsql
let rightJoined =
    fsql {
        for p in Connections.sql.SampleStore.Products do
        rightOuterJoin c in Connections.sql.SampleStore.Categories on (p.CategoryId = c.Id) into categories
        for c in categories.DefaultIfEmpty() do
        select {| Category = c.Name; Product = (if isNull (box p) then "None" else p.Name) |}
    }
```

### 3.12 Execution Plan Inspection (`explain` & `fsql.explain`)
AI agents can request execution plans directly inside the CE:

```fsharp
let planInspection =
    fsql {
        for p in Connections.sql.SampleStore.Products do
        where (p.StockQuantity > 50)
        explain
        select p
    }
```

---

## 4. Query Inspection & Diagnostic Tools

When debugging, validating, or verifying queries, agents have three extraction functions:

### 4.1 `toRawQuery` (Recommended for Display and Logs)
Formats, indents, and beautifies the SQL query with dialect-specific indentation and lists all bound parameter values in an SQL comment header:

```fsharp
let sqlText = toRawQuery highVolumeProducts
printfn "%s" sqlText
```

*Output Example:*
```sql
-- Parameters:
--   @p0 = 2

SELECT
  _t0.ProductId AS ProductId,
  COUNT(*) AS TotalOrders
FROM Orders _t0
GROUP BY _t0.ProductId
HAVING (COUNT(*) >= @p0)
```

### 4.2 `toRawSql` (One-liner string)
```fsharp
let raw = toRawSql highVolumeProducts
```

### 4.3 `toRawSqlWithParameters` (Tuple for assertions)
```fsharp
let (sql, parameters) = toRawSqlWithParameters highVolumeProducts
// parameters: [("@p0", box 2)]
```

### 4.4 `explainQuery` & `.Explain()` (Live Execution Plan Diagnostics)
Retrieves the execution plan generated directly by the underlying database engine:
- SQLite: `EXPLAIN QUERY PLAN`
- PostgreSQL: `EXPLAIN`
- SQL Server: `SET SHOWPLAN_TEXT ON ... OFF`

```fsharp
let diag = explainQuery highVolumeProducts
// Or:
let diag = highVolumeProducts.Explain()

printfn "Plan text:\n%s" diag.PlanText
```

---

## 5. Dialect Pushdown Rules for AI Agents

| Feature | SQL Server | PostgreSQL | SQLite |
| :--- | :--- | :--- | :--- |
| **Limiting / Paging** | `OFFSET n ROWS FETCH NEXT m ROWS ONLY` | `LIMIT m OFFSET n` | `LIMIT m OFFSET n` |
| **Cross Apply** | Native `CROSS APPLY (...)` | Emitted as `CROSS JOIN LATERAL` | Transparent memory fallback |
| **Lateral Join** | Emitted as `CROSS APPLY` | Native `CROSS JOIN LATERAL (...)` | Transparent memory fallback |
| **Scalar Count without GroupBy** | Omit `ORDER BY` (prevents T-SQL 412 error) | Native pushdown | Native pushdown |
| **Window Functions OVER** | Native (Auto `ORDER BY (SELECT NULL)` for ranking) | Native `OVER (...)` | Native `OVER (...)` (SQLite 3.25+) |
| **JSON Extraction** | `JSON_VALUE(col, '$.path')` | `col ->> 'path'` / `col #>> '{path}'` | `json_extract(col, '$.path')` |
| **Common Table Expressions** | `WITH [name] AS (...)` | `WITH [RECURSIVE] "name" AS (...)` | `WITH [RECURSIVE] [name] AS (...)` |
| **Set Operations** | `UNION [ALL]`, `INTERSECT`, `EXCEPT` | `UNION [ALL]`, `INTERSECT`, `EXCEPT` | `UNION [ALL]`, `INTERSECT`, `EXCEPT` |
| **Outer Joins** | `FULL OUTER JOIN`, `RIGHT OUTER JOIN` | `FULL OUTER JOIN`, `RIGHT OUTER JOIN` | `FULL OUTER JOIN`, `RIGHT OUTER JOIN` |
| **String Parameters** | `N'value'` / `@pN` | `'value'` / `@pN` | `'value'` / `@pN` |
| **Boolean Types** | Transpiled to `1 / 0` (bit) | Native `true / false` | `1 / 0` |

---

## 6. AI Agent Golden Rules (Guardrails)

1. **NEVER use `query { ... }`**: Standard LINQ `query { ... }` lacks `whereExists`, `having`, `crossApply`, `lateral`, and column-count pushdown fixes. Always use `fsql { ... }`.
2. **NEVER use `fquery`**: `fquery` is a legacy alias. Use `fsql` consistently.
3. **DO NOT write Raw SQL string queries**: Avoid `Connections.query<T> "SELECT * FROM..."`. Express all DQL logic using `fsql { ... }`.
4. **Use Anonymous Records for Projections**: Multi-column `select` should use `{| Col1 = x.Prop1; Col2 = x.Prop2 |}`. It produces strongly-typed rows without requiring pre-declared records.
5. **Inspect Long Queries with `toRawQuery`**: String truncation is disabled in FSI sessions; `toRawQuery` outputs full query texts regardless of complexity.

