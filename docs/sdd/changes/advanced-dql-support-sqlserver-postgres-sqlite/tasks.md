# SDD Task Ledger: Advanced DQL Support for SQL Server, PostgreSQL, and SQLite

- **Change ID**: `advanced-dql-support-sqlserver-postgres-sqlite`
- **Total Tasks**: 6
- **Completed**: 6
- **In Progress**: 0

---

### TASK-DQL-01: Fix Terminal Aggregate Projection Overwrite & SQL Server Aggregates Without GroupBy
- **Goal**: Ensure terminal aggregate methods (`Count`, `LongCount`, `Sum`, `Min`, `Max`, `Average`) preserve their aggregate projection when preceded by a `Select` call, and omit `ORDER BY` for single-row aggregates in SQL Server.
- **Related Requirements**: Delta Spec Section 1 & 2
- **Done When**: Queries like `queryable.Select(fun p -> p.Id).Count()` and `query { for p in q do select p.Id; count }` correctly output `SELECT COUNT(col)` (or `COUNT(*)`) instead of raw column projection, and do not append `ORDER BY` when no `GROUP BY` is present.
- **Status**: Completed

---

### TASK-DQL-02: Support Column-Specific and Distinct Aggregates (`COUNT(col)`, `COUNT(DISTINCT col)`)
- **Goal**: Enable pushdown translation of `COUNT(col)` (counting non-nulls) and `COUNT(DISTINCT col)` across SQL Server, PostgreSQL, and SQLite.
- **Related Requirements**: Delta Spec Section 1
- **Done When**: Pushdown plans and query expressions with column selection generate `COUNT(col)` or `COUNT(DISTINCT col)` when distinct is specified.
- **Status**: Completed

---

### TASK-DQL-03: Dialect-Aware `CROSS APPLY`, `OUTER APPLY`, `CROSS JOIN`
- **Goal**: Support `CROSS APPLY` and `OUTER APPLY` in `JoinPlan` and `PushdownQueryPlan.BuildSql`, formatting natively for SQL Server (`CROSS/OUTER APPLY`), PostgreSQL (`CROSS/LEFT JOIN LATERAL`), and SQLite, omitting invalid `ON` clauses.
- **Related Requirements**: Delta Spec Section 1 & 2
- **Done When**: Queries with `CROSS APPLY` and `OUTER APPLY` generate valid syntax for SQL Server (`CROSS APPLY (SELECT ...)`), PostgreSQL (`CROSS JOIN LATERAL (...)`), and SQLite without generating `ON 1=1`.
- **Status**: Completed

---

### TASK-DQL-04: Sub-select (Scalar Subquery, EXISTS/NOT EXISTS, IN/NOT IN) & HAVING Clause Support
- **Goal**: Support scalar subqueries in `SELECT` projections, correlated subqueries in `WHERE EXISTS` / `NOT EXISTS`, `WHERE IN` / `NOT IN`, and `HAVING` aggregate filters.
- **Related Requirements**: Delta Spec Section 1
- **Done When**: Pushdown compiler handles scalar sub-selects and HAVING clauses correctly across SQL Server, Postgres, and SQLite.
- **Status**: Completed

---

### TASK-DQL-05: Functional Query Combinators & Extensions on `SqlPushdownQueryable`
- **Goal**: Provide idiomatic F# functional methods on `SqlPushdownQueryable` (`.Count(col)`, `.CountDistinct(col)`, `.CrossApply(...)`, `.OuterApply(...)`, `.Having(...)`, `.WhereExists(...)`, etc.).
- **Related Requirements**: Delta Spec Section 1
- **Done When**: Developers can chain fluent functional query combinators to build and execute complex DQL queries.
- **Status**: Completed

---

### TASK-DQL-06: Comprehensive Test Suites across SQL Server, PostgreSQL, SQLite, and Full Validation
- **Goal**: Write extensive unit and integration tests verifying all advanced DQL features on SQL Server, PostgreSQL, and SQLite dialects, and run the complete test suite.
- **Related Requirements**: All requirements
- **Done When**: All tests pass 100% locally with no regressions.
- **Status**: Completed
