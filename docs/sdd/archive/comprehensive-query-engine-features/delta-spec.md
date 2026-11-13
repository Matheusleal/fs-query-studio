# SDD Delta Spec: Comprehensive Query Engine Features

- **Change ID**: `comprehensive-query-engine-features`
- **Spec Status**: Draft
- **Target Baseline**: `docs/sdd/03-requirements.md`

---

## 1. ADDED Requirements (GEARS Syntax)

### Joins & GroupJoin Pushdown
```text
When an F# query expression contains a leftOuterJoin clause compiled to Queryable.GroupJoin followed by DefaultIfEmpty,
the SqlPushdownQuery compiler shall translate the join into a SQL LEFT JOIN preserving outer and inner key join conditions.

Where a query contains cross-table joins across registered file connections (CSV/Excel) within the workspace,
when canPushdownAllJoins evaluates the expression tree,
the SqlPushdownQuery compiler shall accept GroupJoin as a valid pushdown candidate when all participating tables belong to the workspace SQLite staging cache.

When a query expression cannot be pushed down to SQL,
when LinqInMemoryFallback.evaluate executes,
the fallback engine shall match Enumerable method overloads by exact parameter types to avoid invoking index-based delegate signatures.
```

### Aggregates & Grouping
```text
When a query expression contains a groupBy clause,
the SqlPushdownQuery compiler shall append a SQL GROUP BY clause referencing the grouping key columns.

Where a query projects aggregate operations over grouped or ungrouped sequences (Count, Sum, Min, Max, Average),
the SqlPushdownQuery compiler shall emit native SQL aggregate expressions (COUNT, SUM, MIN, MAX, AVG) in the SELECT list.

When a terminal scalar aggregate method (Count, LongCount, Sum, Min, Max, Average) is invoked on a SqlPushdownQueryable,
the SqlPushdownQueryProvider shall execute a scalar SQL query and return the single aggregated value directly without fetching all rows.
```

### Limiters & Set Operations
```text
When a query expression contains a distinct clause or calls Distinct(),
the SqlPushdownQuery compiler shall emit SELECT DISTINCT in the generated SQL statement.

Where the target dialect is Sqlite or PostgreSql,
when Take and Skip operators are present in the query plan,
the SqlPushdownQuery compiler shall format the limit clause as LIMIT <take> OFFSET <skip>.

Where the target dialect is SqlServer,
when Take and Skip operators are present in the query plan,
the SqlPushdownQuery compiler shall format the pagination using OFFSET <skip> ROWS FETCH NEXT <take> ROWS ONLY (or TOP (<take>) when Skip is zero).

When a query invokes first, firstOrDefault, head, or headOrDefault,
the SqlPushdownQueryProvider shall enforce Take(1) pushdown and return the first row.
```

### Subqueries
```text
Where a WHERE predicate checks membership against an inner queryable (Contains or IN),
the SqlPushdownQuery compiler shall render a SQL WHERE <column> IN (SELECT ...) subquery.

Where a WHERE predicate tests for record existence in an inner queryable (Any or Exists),
the SqlPushdownQuery compiler shall render a SQL WHERE EXISTS (SELECT 1 FROM ...) subquery.
```

### Pivot / Cross-Tabulation
```text
When Connections.sql.pivot is called with a table name, row key columns, pivot column, and value column,
the Connections engine shall discover all distinct values in the pivot column and generate an optimized conditional aggregation query using SUM/MAX(CASE WHEN ... THEN ... ELSE NULL END).

When seq.Pivot is invoked on an in-memory sequence of records,
the Pivot helper shall return a cross-tabulated list of dictionaries or rectangular records with pivot columns.
```

---

## 2. MODIFIED Requirements

```text
When mergeExpressionIntoPlan processes method calls in an expression tree,
the compiler shall recognize GroupJoin, Distinct, GroupBy, and aggregate projections in addition to Join, Where, OrderBy, Take, and Skip.
```

---

## 3. REMOVED Requirements

None. All existing query pushdown features remain backward compatible.

