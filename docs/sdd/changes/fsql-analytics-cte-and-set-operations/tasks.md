# SDD Task Ledger: FSQL Analytics, CTEs, Window Functions, and Set Operations

- **Change ID**: `fsql-analytics-cte-and-set-operations`
- **Total Tasks**: 6
- **Completed**: 2
- **In Progress**: 0

---

### TASK-ANL-01: Window Functions AST and Over Clause Pushdown
- **Goal**: Implement AST nodes and pushdown translation for SQL Window Functions (`ROW_NUMBER`, `RANK`, `DENSE_RANK`, `LEAD`, `LAG`, and aggregates `SUM`, `AVG`, `MIN`, `MAX`, `COUNT` with `OVER (PARTITION BY ... ORDER BY ...)`).
- **Related Requirements**: Delta Spec Section 1 & 2
- **Done When**: Queries using `fsql.rowNumber(...)`, `fsql.rank(...)`, and `fsql.sum(col, over = ...)` compile and execute natively across SQL Server, PostgreSQL, and SQLite 3.25+.
- **Status**: Completed

---

### TASK-ANL-02: Common Table Expressions (CTEs) & Recursive CTEs Pushdown
- **Goal**: Add `CtePlan` to `PushdownQueryPlan`, supporting `withCte` and `fsql.recursive(...)` across SQL Server, PostgreSQL, and SQLite.
- **Related Requirements**: Delta Spec Section 1 & 2
- **Done When**: Non-recursive and recursive hierarchical CTE queries transpile into valid `WITH` and `WITH RECURSIVE` SQL with properly isolated and merged parameters.
- **Status**: Pending

---

### TASK-ANL-03: Set Operations Pushdown (`Union`, `UnionAll`, `Intersect`, `Except`)
- **Goal**: Support set combinators on `SqlPushdownQueryable` and within `fsql`, generating native SQL set operations with combined parameter maps.
- **Related Requirements**: Delta Spec Section 1
- **Done When**: Combining queries with `.Union(...)`, `.UnionAll(...)`, `.Intersect(...)`, and `.Except(...)` produces compliant multi-part SQL and executes seamlessly.
- **Status**: Pending

---

### TASK-ANL-04: Full Outer Join & Right Outer Join
- **Goal**: Add `fullOuterJoin` and `rightOuterJoin` custom operations to `FSqlQueryBuilder` and `JoinPlan` pushdown.
- **Related Requirements**: Delta Spec Section 1
- **Done When**: Outer join variants emit compliant SQL for SQL Server, PostgreSQL, and SQLite without syntax errors.
- **Status**: Pending

---

### TASK-ANL-05: Semi-Structured JSON Path Pushdown Navigation
- **Goal**: Transpile `fsql.jsonValue(col, path)` to `JSON_VALUE` (SQL Server), `->>` (PostgreSQL), and `json_extract` (SQLite).
- **Related Requirements**: Delta Spec Section 1
- **Done When**: Queries filtering or selecting JSON column paths execute natively in the database engine without client deserialization.
- **Status**: Completed

---

### TASK-ANL-06: Query Plan Diagnostics & `explainQuery` Integration
- **Goal**: Implement `explainQuery` returning database execution plans (SQL Server `SHOWPLAN_TEXT`, PostgreSQL `EXPLAIN`, SQLite `EXPLAIN QUERY PLAN`).
- **Related Requirements**: Delta Spec Section 1
- **Done When**: Calling `explainQuery` in notebooks or scripts returns formatted execution plan text and diagnostics.
- **Status**: Pending
