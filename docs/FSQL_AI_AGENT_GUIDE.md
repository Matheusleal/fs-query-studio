# FSQL (F# Query Language) & Extensions — AI Agent Guide

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

---

## 5. Dialect Pushdown Rules for AI Agents

| Feature | SQL Server | PostgreSQL | SQLite |
| :--- | :--- | :--- | :--- |
| **Limiting / Paging** | `OFFSET n ROWS FETCH NEXT m ROWS ONLY` | `LIMIT m OFFSET n` | `LIMIT m OFFSET n` |
| **Cross Apply** | Native `CROSS APPLY (...)` | Emitted as `CROSS JOIN LATERAL` | Transparent memory fallback |
| **Lateral Join** | Emitted as `CROSS APPLY` | Native `CROSS JOIN LATERAL (...)` | Transparent memory fallback |
| **Scalar Count without GroupBy** | Omit `ORDER BY` (prevents T-SQL 412 error) | Native pushdown | Native pushdown |
| **String Parameters** | `N'value'` / `@pN` | `'value'` / `@pN` | `'value'` / `@pN` |
| **Boolean Types** | Transpiled to `1 / 0` (bit) | Native `true / false` | `1 / 0` |

---

## 6. AI Agent Golden Rules (Guardrails)

1. **NEVER use `query { ... }`**: Standard LINQ `query { ... }` lacks `whereExists`, `having`, `crossApply`, `lateral`, and column-count pushdown fixes. Always use `fsql { ... }`.
2. **NEVER use `fquery`**: `fquery` is a legacy alias. Use `fsql` consistently.
3. **DO NOT write Raw SQL string queries**: Avoid `Connections.query<T> "SELECT * FROM..."`. Express all DQL logic using `fsql { ... }`.
4. **Use Anonymous Records for Projections**: Multi-column `select` should use `{| Col1 = x.Prop1; Col2 = x.Prop2 |}`. It produces strongly-typed rows without requiring pre-declared records.
5. **Inspect Long Queries with `toRawQuery`**: String truncation is disabled in FSI sessions; `toRawQuery` outputs full query texts regardless of complexity.

