# SDD Task Ledger: Comprehensive Query Engine Features

- **Change ID**: `comprehensive-query-engine-features`
- **Total Tasks**: 6
- **Completed**: 6
- **In Progress**: 0

---

### TASK-CQE-01: Fix `LinqInMemoryFallback` Overload Resolution
- **Goal**: Ensure in-memory LINQ fallback correctly matches `Enumerable` method overloads by checking delegate argument arity and parameter types instead of purely counting arguments, avoiding `TargetInvocationException` on `SelectMany`.
- **Related Requirements**: Delta Spec Section 1 (Joins & GroupJoin Pushdown)
- **Done When**: Calling `LinqInMemoryFallback.evaluate` with a multi-join `GroupJoin` + `SelectMany` expression evaluates without reflection conversion errors.
- **Status**: Completed

---

### TASK-CQE-02: Support `GroupJoin` (`leftOuterJoin`) in SQL Pushdown
- **Goal**: Allow `leftOuterJoin ... into group` + `DefaultIfEmpty()` in F# query expressions to push down directly to SQL `LEFT JOIN` in `SqlPushdownQuery.fs`.
- **Related Requirements**: Delta Spec Section 1 (Joins & GroupJoin Pushdown)
- **Done When**: An expression with multiple `leftOuterJoin` clauses against workspace CSVs generates `LEFT JOIN ... ON ...` and executes in milliseconds against SQLite.
- **Status**: Completed

---

### TASK-CQE-03: Implement Aggregates, `groupBy`, and Scalar Terminal Pushdown
- **Goal**: Implement pushdown for `groupBy`, `minBy`, `maxBy`, `sumBy`, `averageBy`, and `count`, plus scalar pushdown methods (`.Count()`, `.Sum()`, etc.) on `SqlPushdownQueryable`.
- **Related Requirements**: Delta Spec Section 1 (Aggregates & Grouping)
- **Done When**: Queries using `groupBy` produce SQL `GROUP BY` and `.Count()`, `.Sum()` execute single-row aggregate queries.
- **Status**: Completed

---

### TASK-CQE-04: Implement Limiters (`distinct`, dialect-specific `top`/`take`/`skip`, `first`/`head`)
- **Goal**: Support `distinct` (`SELECT DISTINCT`), dialect-aware `LIMIT`/`OFFSET` vs `TOP`/`FETCH NEXT`, and `first`/`head` pushdown.
- **Related Requirements**: Delta Spec Section 1 (Limiters & Set Operations)
- **Done When**: `fsql { distinct }` produces `SELECT DISTINCT` and `take`/`skip` produces correct SQL for SQLite, PostgreSQL, and SQL Server.
- **Status**: Completed

---

### TASK-CQE-05: Subqueries (`IN`, `EXISTS`) and Dynamic `Pivot` Cross-Tabulation
- **Goal**: Support subquery expressions in WHERE clauses and provide `Connections.sql.pivot` and `seq.Pivot` helpers for dynamic cross-tabulation.
- **Related Requirements**: Delta Spec Section 1 (Subqueries, Pivot / Cross-Tabulation)
- **Done When**: Subquery membership checks generate `IN (SELECT ...)` and `Connections.sql.pivot` returns pivoted results with columns for each category.
- **Status**: Completed

---

### TASK-CQE-06: Verification, E2E Test on User's fs-qs2 Notebook, and Documentation
- **Goal**: Verify that cell 2 of `C:\Users\theus\Downloads\fs-qs2\notebook.fsqnb` executes and renders the interactive data grid in milliseconds; run all unit tests, Linux Docker tests, and update documentation in PT and EN.
- **Related Requirements**: All requirements.
- **Done When**: All tests pass 100%, user's notebook executes and renders grid in ms, and docs are updated.
- **Status**: Completed
