# SDD Change Proposal: FSQL Analytics, CTEs, Window Functions, and Set Operations

- **Change ID**: `fsql-analytics-cte-and-set-operations`
- **Author**: Matheus Leal / Antigravity AI
- **Date**: 2026-11-13
- **Status**: Proposed
- **Target Version**: v0.6.0

---

## 1. Intent Block

```text
Intent Block:
Goal:        Evolve the FSQL specification and universal pushdown engine to support modern enterprise SQL analytics, including Common Table Expressions (CTEs), Window Functions, Set Operations, Full/Right Outer Joins, and JSON column path navigation across SQL Server, PostgreSQL, and SQLite.
Expectation: FSQL developers and AI agents can express complex analytical workflows—such as cumulative totals, moving averages, row rankings, hierarchical graph traversals via recursive CTEs, multi-query set unions, and semi-structured JSON querying—directly in pure F# computation expressions (fsql { ... }) and fluent APIs, with 100% database pushdown, zero raw SQL string concatenation, strict parameterization, and compile-time type safety.
Action:      Extend FSqlQueryBuilder and PushdownQueryPlan with AST nodes for Window Functions (OVER PARTITION BY / ORDER BY), CTE definitions (withCte, withRecursiveCte), Set Operations (union, unionAll, intersect, except), Right/Full Outer Joins, and JSON property extractions; implement dialect-specific translation rules for SQL Server, PostgreSQL, and SQLite 3.25+; introduce explainQuery diagnostics; validate with comprehensive multi-dialect unit and integration test suites.
Result:      FSQL becomes a complete, enterprise-grade replacement for SQL and LINQ in F#, capable of handling advanced analytical and reporting workloads with optimal pushdown execution and guaranteed cross-dialect fidelity.
```

---

## 2. Why (Context & Problem Statement)

1. **Analytical Queries Require Window Functions (`OVER`)**:
   - Modern data analysis routinely requires ranking (`ROW_NUMBER()`, `RANK()`, `DENSE_RANK()`), running totals, moving averages, and lead/lag value lookups (`LEAD()`, `LAG()`).
   - Standard LINQ and the current FSQL engine lack a declarative syntax to express `OVER (PARTITION BY ... ORDER BY ...)` clauses.
   - Without window functions, developers must resort to expensive correlated self-joins or fall back to client memory, degrading performance and increasing network latency.
   - All three supported database engines (SQL Server, PostgreSQL, and SQLite 3.25+) natively support standard SQL window functions.

2. **Complex Query Modularity and Recursion via CTEs**:
   - Deeply nested subqueries become unwieldy, difficult to read, and suboptimal for database query optimizers.
   - Common Table Expressions (`WITH cte AS (...)`) allow modular query composition, reusability of intermediate calculations, and recursive hierarchical queries (e.g., manager-employee organizational hierarchies, bill-of-materials, folder trees).
   - FSQL currently supports inline derived tables (subqueries), but lacks a clean, declarative mechanism to define and reference CTEs.

3. **Set Operations (`UNION`, `UNION ALL`, `INTERSECT`, `EXCEPT`)**:
   - Combining results from multiple queries or data sources is a fundamental DQL capability.
   - Although .NET `IQueryable` defines `Union` and `Concat`, the FSQL pushdown compiler does not yet transpile these into native SQL set operations with parameter unioning, sorting, and pagination across dialects.

4. **Full Outer Join & Right Outer Join Completeness**:
   - Relational joins in FSQL currently encompass `join` (inner), `leftOuterJoin`, `crossApply`, `outerApply`, and `crossJoin`.
   - Data reconciliation, difference detection, and audit queries often require `FULL OUTER JOIN` or `RIGHT OUTER JOIN`.
   - SQL Server and PostgreSQL support both natively; SQLite supports `RIGHT OUTER JOIN` and `FULL OUTER JOIN` (since SQLite 3.39.0) and can be emulated via compound queries for older versions.

5. **Semi-Structured JSON Querying**:
   - Modern relational databases frequently store flexible attributes, audit trails, and metadata as JSON columns (`NVARCHAR`, `JSONB`, or `TEXT`).
   - Querying JSON properties inside relational filters and projections currently requires client-side deserialization or raw SQL strings. Native pushdown of JSON accessors (`json_extract`, `->>`, `JSON_VALUE`) preserves performance and indices.

6. **Explain & Performance Diagnostics**:
   - Developers and AI agents need to inspect how the database engine executes pushdown queries. Adding `explainQuery` enables proactive index recommendations and query cost verification directly from notebooks and test harnesses.

---

## 3. Scope & Feature Specifications

### 3.1 Window Functions & Analytic Clauses

#### Pure F# Syntax in `fsql { ... }`:
```fsharp
fsql {
    for e in Connections.sql.SampleStore.Employees do
    select {|
        Employee = e.Name
        Department = e.Department
        Salary = e.Salary
        RankInDept = fsql.rowNumber(over = fsql.window(partitionBy = e.Department, sortByDescending = e.Salary))
        DenseRankInDept = fsql.denseRank(over = fsql.window(partitionBy = e.Department, sortByDescending = e.Salary))
        DeptRunningTotal = fsql.sum(e.Salary, over = fsql.window(partitionBy = e.Department, sortBy = e.HireDate))
        PreviousSalary = fsql.lag(e.Salary, offset = 1, over = fsql.window(partitionBy = e.Department, sortBy = e.HireDate))
        NextSalary = fsql.lead(e.Salary, offset = 1, over = fsql.window(partitionBy = e.Department, sortBy = e.HireDate))
    |}
}
```

#### Transpiled SQL (All Dialects: SQL Server, PostgreSQL, SQLite 3.25+):
```sql
SELECT
  _t0.Name AS Employee,
  _t0.Department AS Department,
  _t0.Salary AS Salary,
  ROW_NUMBER() OVER (PARTITION BY _t0.Department ORDER BY _t0.Salary DESC) AS RankInDept,
  DENSE_RANK() OVER (PARTITION BY _t0.Department ORDER BY _t0.Salary DESC) AS DenseRankInDept,
  SUM(_t0.Salary) OVER (PARTITION BY _t0.Department ORDER BY _t0.HireDate ASC) AS DeptRunningTotal,
  LAG(_t0.Salary, 1) OVER (PARTITION BY _t0.Department ORDER BY _t0.HireDate ASC) AS PreviousSalary,
  LEAD(_t0.Salary, 1) OVER (PARTITION BY _t0.Department ORDER BY _t0.HireDate ASC) AS NextSalary
FROM Employees _t0
```

---

### 3.2 Common Table Expressions (CTEs) & Recursive Queries

#### Non-Recursive CTE Syntax:
```fsharp
let highEarners =
    fsql {
        for e in Connections.sql.SampleStore.Employees do
        where (e.Salary >= 100000.0M)
        select e
    }

fsql {
    withCte "HighEarners" highEarners
    for h in highEarners do
    join d in Connections.sql.SampleStore.Departments on (h.DepartmentId = d.Id)
    select {|
        EmployeeName = h.Name
        DepartmentName = d.Name
        Salary = h.Salary
    |}
}
```

#### Transpiled SQL:
```sql
WITH HighEarners AS (
  SELECT _sub0.*
  FROM Employees _sub0
  WHERE (_sub0.Salary >= @p0)
)
SELECT
  _t0.Name AS EmployeeName,
  _t1.Name AS DepartmentName,
  _t0.Salary AS Salary
FROM HighEarners _t0
INNER JOIN Departments _t1 ON (_t0.DepartmentId = _t1.Id)
```

#### Recursive CTE Syntax (`withRecursiveCte`):
```fsharp
let employeeHierarchy =
    fsql.recursive(
        name = "OrgHierarchy",
        anchor = fsql {
            for e in Connections.sql.SampleStore.Employees do
            where (isNull e.ManagerId)
            select {| Id = e.Id; Name = e.Name; Level = 1 |}
        },
        recursive = (fun self -> fsql {
            for e in Connections.sql.SampleStore.Employees do
            join m in self on (e.ManagerId = m.Id)
            select {| Id = e.Id; Name = e.Name; Level = m.Level + 1 |}
        })
    )
```

- **PostgreSQL / SQLite:** Emits `WITH RECURSIVE OrgHierarchy AS (...)`
- **SQL Server:** Emits `WITH OrgHierarchy AS (...)`

---

### 3.3 Set Operations (`union`, `unionAll`, `intersect`, `except`)

#### Fluent & CE Syntax:
```fsharp
let activeUsers = fsql { for u in db.Users do where u.IsActive; select u.Email }
let newsletterSubscribers = fsql { for s in db.Subscribers do select s.Email }

// In-expression set operations
let unionQuery = activeUsers.Union(newsletterSubscribers)
let unionAllQuery = activeUsers.UnionAll(newsletterSubscribers)
let intersectQuery = activeUsers.Intersect(newsletterSubscribers)
let exceptQuery = activeUsers.Except(newsletterSubscribers)
```

#### Multi-Dialect Set Operation Matrix:

| Set Operation | SQL Server | PostgreSQL | SQLite |
|---|---|---|---|
| `Union` | `UNION` | `UNION` | `UNION` |
| `UnionAll` | `UNION ALL` | `UNION ALL` | `UNION ALL` |
| `Intersect` | `INTERSECT` | `INTERSECT` | `INTERSECT` |
| `Except` | `EXCEPT` | `EXCEPT` | `EXCEPT` |

---

### 3.4 Full Outer Join & Right Outer Join

#### Syntax:
```fsharp
fsql {
    for c in Connections.sql.SampleStore.Customers do
    fullOuterJoin o in Connections.sql.SampleStore.Orders on (c.Id = o.CustomerId) into g
    for o in g.DefaultIfEmpty() do
    select {|
        CustomerId = (if isNull (box c) then 0 else c.Id)
        OrderId = (if isNull (box o) then 0 else o.Id)
    |}
}
```

- **SQL Server & PostgreSQL:** Transpiled to `FULL OUTER JOIN Orders _t1 ON (_t0.Id = _t1.CustomerId)`.
- **SQLite (>= 3.39):** Transpiled to native `FULL OUTER JOIN`.
- **SQLite (< 3.39 fallback):** Emulated via union of `LEFT JOIN` and anti-join.

---

### 3.5 Semi-Structured JSON Navigation Expressions

#### Syntax:
```fsharp
fsql {
    for p in Connections.sql.SampleStore.Products do
    where (fsql.jsonValue(p.Attributes, "$.color") = "Red")
    select {|
        ProductName = p.Name
        Color = fsql.jsonValue(p.Attributes, "$.color")
        Weight = fsql.jsonValue(p.Attributes, "$.dimensions.weight")
    |}
}
```

#### Transpiled SQL by Dialect:
- **SQL Server:** `JSON_VALUE(_t0.Attributes, '$.color')`
- **PostgreSQL:** `(_t0.Attributes ->> 'color')`
- **SQLite:** `json_extract(_t0.Attributes, '$.color')`

---

### 3.6 Explain Query Diagnostic Tooling (`explainQuery`)

#### Syntax:
```fsharp
let planInfo = explainQuery myQuery
printfn "Execution Plan:\n%s" planInfo.PlanText
```

- **SQL Server:** Runs with `SET SHOWPLAN_TEXT ON`.
- **PostgreSQL:** Runs `EXPLAIN (ANALYZE false, FORMAT TEXT) <query>`.
- **SQLite:** Runs `EXPLAIN QUERY PLAN <query>`.

---

## 4. Architectural Impact & Non-Breaking Compatibility

1. **Backwards Compatibility**:
   - All existing `fsql { ... }` queries, methods, and quotations remain 100% source- and binary-compatible.
   - `FSqlQueryBuilder` gains additive custom operations without altering existing grammar.
2. **PushdownQueryPlan Extension**:
   - Adds `Ctes: ResizeArray<CtePlan>`
   - Adds `SetOperations: ResizeArray<SetOperationPlan>`
   - Adds window function nodes to `ScalarExpression` translation.
3. **Execution Pipeline**:
   - Seamlessly uses existing ADO.NET connection pooling and `DynamicRecordActivator` record streaming.
