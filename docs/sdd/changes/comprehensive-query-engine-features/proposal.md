# SDD Change Proposal: Comprehensive Query Engine Features (Joins, Aggregates, Limiters, Subqueries & Pivot)

- **Change ID**: `comprehensive-query-engine-features`
- **Author**: Matheus Leal / Antigravity AI
- **Date**: 2026-11-02
- **Status**: Proposed
- **Target Version**: v0.4.1

---

## 1. Intent Block

```text
Intent Block:
Goal:        Expand F# Query Studio's SQL pushdown engine and query capabilities to natively support all join types (including leftOuterJoin/GroupJoin), aggregate functions (Count, Sum, Min, Max, Average, GroupBy), result limiters (Distinct, Top, Limit, Skip, First/Head), subselect expressions, and cross-tabulation/pivot queries.
Expectation: Queries written via F# fsql { ... } expressions or SQL helper APIs execute natively with pushdown in milliseconds on multi-table datasets (such as 100k+ row datasets) without falling back to slow in-memory evaluation or throwing reflection errors.
Action:      Extend SqlPushdownQuery compiler to handle GroupJoin (LEFT JOIN), GroupBy with aggregate projections (COUNT, SUM, AVG, MIN, MAX), DISTINCT, TOP/LIMIT/OFFSET dialect-specific syntax, subquery IN/EXISTS translation, in-memory LINQ fallback overload fix, and provide a high-performance dynamic Pivot generator in Connections.
Result:      Users can express any analytical, grouping, filtering, subquery, or pivot operation seamlessly in F# notebooks and have it execute in milliseconds across SQLite, PostgreSQL, SQL Server, CSV and Excel.
```

---

## 2. Why (Context & Problem Statement)

1. **Failure on `leftOuterJoin`**:
   - In F#, `leftOuterJoin ... into group` + `for x in group.DefaultIfEmpty()` compiles into `Queryable.GroupJoin` followed by `SelectMany`.
   - The pushdown engine previously didn't recognize `GroupJoin`, falling back to `LinqInMemoryFallback`.
   - In `LinqInMemoryFallback`, reflection over `Enumerable.SelectMany` matched the wrong overload (`(source, index) -> collection`), causing runtime crashes and rendering only `<seq>` in notebook outputs.
2. **Missing Analytical & Aggregation Capabilities**:
   - Analytical workloads require `groupBy`, `count`, `sumBy`, `minBy`, `maxBy`, `averageBy` with native SQL `GROUP BY` pushdown.
   - Without native pushdown, aggregating 100k+ row datasets pulls entire tables into memory.
3. **Limiters & Set Modifiers**:
   - `distinct` (`SELECT DISTINCT`), `take` (`TOP` / `LIMIT`), `skip` (`OFFSET`), `first` / `head` should be translated natively per dialect.
4. **Subqueries & Pivot**:
   - Users need subqueries (`WHERE col IN (SELECT ...)` or `EXISTS (...)`) and cross-tabulation / pivot reporting (`Connections.sql.pivot` or conditional aggregation).

---

## 3. Scope

### In-Scope
1. **Join Types in Pushdown**:
   - `LEFT JOIN` via `Queryable.GroupJoin` + `DefaultIfEmpty`.
   - `RIGHT JOIN`, `FULL OUTER JOIN`, `CROSS JOIN`.
   - Fix `LinqInMemoryFallback.evaluate` overload matching to ensure fallback never crashes when pushdown is bypassed.
2. **Aggregates & Grouping**:
   - `groupBy` clause in `fsql { ... }` compiling to `GROUP BY` in SQL.
   - Pushdown of `COUNT(*)`, `COUNT(col)`, `SUM(col)`, `MIN(col)`, `MAX(col)`, `AVG(col)`.
   - Support both grouped projections and direct scalar evaluation (`.Count()`, `.Sum()`, etc.).
3. **Limiters & Modifiers**:
   - `distinct` / `Distinct()` -> `SELECT DISTINCT`.
   - Dialect-aware `take` / `skip`:
     - SQLite & PostgreSQL: `LIMIT <n> OFFSET <m>`
     - SQL Server: `OFFSET <m> ROWS FETCH NEXT <n> ROWS ONLY` (or `TOP (n)` when no offset).
   - `head`, `headOrDefault`, `first`, `firstOrDefault`.
4. **Subqueries**:
   - Correlated and uncorrelated `IN (SELECT ...)` and `EXISTS (SELECT ...)`.
5. **Pivot / Cross-Tabulation**:
   - `Connections.sql.pivot(tableName, rowKeyCols, pivotCol, valCol, ?aggregateFunc)` helper that dynamically inspects distinct values in `pivotCol` and generates/executes high-speed conditional aggregation.
   - `seq.Pivot(...)` helper for in-memory cross-tabulation.

### Out-of-Scope
- Window functions (`ROW_NUMBER() OVER (...)`) for non-SQL engines (can be executed via direct SQL for relational engines).
- Modifying underlying connector storage schemas.

---

## 4. Success Criteria

1. Notebook `C:\Users\theus\Downloads\fs-qs2\notebook.fsqnb` cell 2 executes with `leftOuterJoin` and renders the interactive data grid in milliseconds.
2. `fsql { ... groupBy ... select ... }` translates to `GROUP BY` with aggregate columns in SQL pushdown.
3. `fsql { ... distinct ... }` generates `SELECT DISTINCT`.
4. `fsql { ... take 10 ... }` and `skip` produce valid dialect-specific SQL.
5. `Connections.sql.pivot` produces pivot table results from any staged or database table.
6. 100% of all unit, integration, worker, and Docker Linux tests pass.
7. Documentation updated in Portuguese and English.

---

## 5. Impacted Artifacts

- `src/FSharpQueryStudio.Connectors/SqlPushdownQuery.fs`
- `src/FSharpQueryStudio.Connectors/Connections.fs`
- `src/FSharpQueryStudio.Results/Projection.fs`
- `tests/FSharpQueryStudio.Connectors.Tests/FileSqliteEngineTests.fs`
- `tests/FSharpQueryStudio.Connectors.Tests/PushdownQueryTests.fs`
- `editors/vscode/USER_GUIDE.md`
- `editors/vscode/USER_GUIDE_PT.md`

