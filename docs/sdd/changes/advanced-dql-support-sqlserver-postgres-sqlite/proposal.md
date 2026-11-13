# SDD Change Proposal: Advanced DQL Support for SQL Server, PostgreSQL, and SQLite

- **Change ID**: `advanced-dql-support-sqlserver-postgres-sqlite`
- **Author**: Matheus Leal / Antigravity AI
- **Date**: 2026-11-12
- **Status**: Proposed
- **Target Version**: v0.5.4

---

## 1. Intent Block

```text
Intent Block:
Goal:        Fix SQL Server query generation bugs when counting columns and provide comprehensive, first-class Data Query Language (DQL) support across SQL Server, PostgreSQL, and SQLite, embracing F# functional query idioms beyond traditional LINQ limitations.
Expectation: Queries using column-specific aggregates (COUNT(col), COUNT(DISTINCT col), MIN, MAX, SUM, AVG), sub-selects (scalar subqueries in projections, WHERE IN / NOT IN, WHERE EXISTS / NOT EXISTS), CROSS APPLY / OUTER APPLY / LATERAL joins, HAVING clauses, and functional combinators execute seamlessly and generate compliant SQL dialects for SQL Server, PostgreSQL, and SQLite without syntax errors or runtime crashes.
Action:      Refactor SqlPushdownQuery and SqlPushdownPlan to correctly track projection overrides when terminal aggregates are applied after Select; implement dialect-aware CROSS APPLY/OUTER APPLY (transpiled to CROSS/LEFT JOIN LATERAL for Postgres); add column-targeted count, distinct count, scalar subquery, HAVING clause, and functional query combinators to SqlPushdownQueryable; validate with extensive test suites across SQL Server, PostgreSQL, and SQLite.
Result:      Developers can express powerful, expressive, and idiomatic F# functional queries leveraging all core DQL capabilities with guaranteed dialect precision across all supported database engines.
```

---

## 2. Why (Context & Problem Statement)

1. **SQL Server Count Breakdown**:
   - When a user writes `fsql { for p in db.Products do select p.Id; count }` or `queryable.Select(fun p -> p.Id).Count()`, the `Select` method call registers a projection (`_t0.Id AS [Id]`) which subsequently overwrote the `plan.Projection` intended by `Count()` (`COUNT(*)` or `COUNT(col)`).
   - The query compiler generated `SELECT _t0.Id AS [Id] FROM Products _t0`, which failed during scalar conversion (`ExecuteScalar<int>`) with invalid cast or conversion format errors.
   - Furthermore, SQL Server strictly prohibits `ORDER BY` in single-row aggregate queries without `GROUP BY`, causing query failures when sorting was combined with terminal aggregates.
2. **Missing Column-Specific Count & Aggregation**:
   - In SQL DQL, `COUNT(col)` differs semantically from `COUNT(*)` as it only counts non-NULL column values.
   - Users need both `COUNT(col)` and `COUNT(DISTINCT col)` natively in F# query expressions and via fluent functional methods.
   - Terminal `Sum`, `Min`, `Max`, `Average` methods called after `Select(fun p -> p.Col)` suffered from the same projection overwrite bug when no arguments were supplied to the aggregate call.
3. **Advanced DQL Pushdown Support (Cross Apply, Lateral, Sub-selects, Having)**:
   - SQL Server heavily relies on `CROSS APPLY` and `OUTER APPLY` for table-valued functions and correlated subqueries. PostgreSQL uses `CROSS JOIN LATERAL` and `LEFT JOIN LATERAL ... ON true`.
   - Existing pushdown query plans forced `ON <condition>` on every join, making `CROSS APPLY` and `CROSS JOIN` emit invalid SQL (`CROSS APPLY tbl ON 1=1`).
   - Scalar subqueries in `SELECT` projections and correlated subqueries in `WHERE` / `HAVING` filters lacked full native transpilation support.
4. **Functional F# Approach Beyond Traditional LINQ**:
   - F# developers should not be constrained by C# LINQ's rigid query operators. First-class functional methods such as `.Count(colSelector)`, `.CountDistinct(colSelector)`, `.CrossApply(...)`, `.OuterApply(...)`, `.WhereExists(...)`, `.Having(...)`, and `.Select(rawProjection)` empower users to write expressive queries.

---

## 3. Scope

### In-Scope
1. **Fix Aggregate Projection Overwrite & SQL Server ORDER BY Invalidation**:
   - Ensure `Count`, `LongCount`, `Sum`, `Min`, `Max`, `Average` are preserved as terminal projections even when preceded by `Select`.
   - In scalar aggregates without `GROUP BY`, automatically strip `ORDER BY` in SQL generation (specifically required by SQL Server).
2. **Column-Specific & Distinct Count**:
   - Support `COUNT(col)` and `COUNT(DISTINCT col)` in pushdown plans and queryable methods.
   - Support `countBy` and `g.Count(col)` in `GroupBy` projections.
3. **CROSS APPLY / OUTER APPLY / LATERAL Joins**:
   - Adapt `JoinPlan` and `PushdownQueryPlan.BuildSql` to omit `ON` for `CROSS APPLY`, `OUTER APPLY`, and `CROSS JOIN`.
   - Map `CROSS APPLY` to `CROSS JOIN LATERAL` and `OUTER APPLY` to `LEFT JOIN LATERAL ... ON true` in `SqlDialect.PostgreSql`.
   - Provide fluent `.CrossApply(...)` and `.OuterApply(...)` on `SqlPushdownQueryable`.
4. **Subqueries & Having Clauses**:
   - Support scalar subqueries in `Projection`.
   - Support `WHERE EXISTS`, `WHERE NOT EXISTS`, `WHERE IN`, `WHERE NOT IN` with subquery pushdown plans.
   - Support `HAVING` clauses via `.Having(condition, ?parameters)`.
5. **Comprehensive Cross-Dialect Tests**:
   - Unit and integration tests covering SQLite, PostgreSQL, and SQL Server dialect generation and execution.

