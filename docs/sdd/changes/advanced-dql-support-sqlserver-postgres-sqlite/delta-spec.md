# SDD Delta Spec: Advanced DQL Support for SQL Server, PostgreSQL, and SQLite

- **Change ID**: `advanced-dql-support-sqlserver-postgres-sqlite`
- **Author**: Matheus Leal / Antigravity AI
- **Date**: 2026-11-12
- **Status**: Proposed

---

## 1. ADDED Requirements

```text
When the user executes a terminal aggregate method (Count, LongCount, Sum, Min, Max, Average) following a Select expression,
the SqlPushdownQuery compiler shall project the aggregate function targeting the selected expression or column instead of overwriting the projection with the raw column.

When a query against SQL Server contains a scalar aggregate projection without a GROUP BY clause,
the PushdownQueryPlan shall omit the ORDER BY clause to comply with SQL Server T-SQL constraints.

When the user specifies a column or expression in a Count operation,
the SqlPushdownQuery compiler shall generate COUNT(col) or COUNT(DISTINCT col) when distinct is requested.

Where the target dialect is SqlServer,
when a CrossApply or OuterApply join is specified,
the PushdownQueryPlan shall format the join clause as CROSS APPLY <source> or OUTER APPLY <source> without an ON clause.

Where the target dialect is PostgreSql,
when a CrossApply or OuterApply join is specified,
the PushdownQueryPlan shall format the join clause as CROSS JOIN LATERAL <source> or LEFT JOIN LATERAL <source> ON true.

Where the target dialect is Sqlite,
when a CrossApply or OuterApply join is specified,
the PushdownQueryPlan shall format the join clause as CROSS JOIN <source> or LEFT JOIN <source> ON 1=1.

When the user specifies a sub-select query in a projection or a WHERE clause (EXISTS, NOT EXISTS, IN, NOT IN),
the SqlPushdownQuery compiler shall transpile the inner query plan into compliant nested SQL with properly namespaced parameters.

When the user invokes functional query combinators (CrossApply, OuterApply, Having, CountDistinct, WhereExists),
the SqlPushdownQueryable shall append the corresponding clauses to the pushdown plan while maintaining immutability.
```

---

## 2. MODIFIED Requirements

```text
When PushdownQueryPlan.BuildSql formats join clauses,
the PushdownQueryPlan shall inspect the JoinType and omit the ON clause for CROSS APPLY, OUTER APPLY, and CROSS JOIN.

When mergeExpressionIntoPlan processes queryable method chains,
the compiler shall prioritize terminal aggregate projections over intermediate Select expressions.
```

---

## 3. REMOVED Requirements

*None.*

