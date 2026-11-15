# SDD Delta Spec: FSQL Analytics, CTEs, Window Functions, and Set Operations

- **Change ID**: `fsql-analytics-cte-and-set-operations`
- **Author**: Matheus Leal / Antigravity AI
- **Date**: 2026-11-13
- **Status**: Proposed

---

## 1. ADDED Requirements

```text
When the user specifies a window function (rowNumber, rank, denseRank, ntile, lead, lag, firstValue, lastValue, or aggregate with an over clause) in a projection,
the SqlPushdownQuery compiler shall transpile the call into a dialect-compliant SQL OVER (PARTITION BY ... ORDER BY ...) expression.

Where the target dialect is SqlServer, PostgreSql, or Sqlite,
when a window function specifies an over clause with partition columns and sort orders,
the PushdownQueryPlan shall format the partition criteria as PARTITION BY <columns> and the order criteria as ORDER BY <columns>.

When the user specifies a Common Table Expression using withCte in an fsql computation expression or fluent query,
the PushdownQueryPlan shall prepend the WITH <alias> AS (<subquery>) clause to the generated SQL and namespace the CTE parameters uniquely.

Where the target dialect is PostgreSql or Sqlite,
when the user defines a recursive Common Table Expression using fsql.recursive,
the PushdownQueryPlan shall format the clause as WITH RECURSIVE <name> AS (<anchor> UNION ALL <recursive>).

Where the target dialect is SqlServer,
when the user defines a recursive Common Table Expression using fsql.recursive,
the PushdownQueryPlan shall format the clause as WITH <name> AS (<anchor> UNION ALL <recursive>).

When the user invokes a set operation (Union, UnionAll, Intersect, Except) combining two pushdown queries,
the SqlPushdownQuery compiler shall emit the corresponding SQL set operator between the two queries with merged and renumbered parameters.

Where the target dialect is SqlServer, PostgreSql, or Sqlite,
when the user invokes Except on two pushdown queries,
the PushdownQueryPlan shall format the operator as EXCEPT.

When the user specifies a fullOuterJoin in an fsql computation expression,
the PushdownQueryPlan shall format the join as FULL OUTER JOIN <target> ON <condition>.

When the user specifies a rightOuterJoin in an fsql computation expression,
the PushdownQueryPlan shall format the join as RIGHT OUTER JOIN <target> ON <condition>.

Where the target dialect is SqlServer,
when the user references a JSON column path using fsql.jsonValue,
the SqlPushdownQuery compiler shall transpile the expression into JSON_VALUE(<column>, <path>).

Where the target dialect is PostgreSql,
when the user references a JSON column path using fsql.jsonValue,
the SqlPushdownQuery compiler shall transpile the expression into (<column> ->> <property_key>).

Where the target dialect is Sqlite,
when the user references a JSON column path using fsql.jsonValue,
the SqlPushdownQuery compiler shall transpile the expression into json_extract(<column>, <path>).

When the user invokes explainQuery on an FSQL query or pushdown plan,
the execution engine shall issue the dialect-appropriate explain command (EXPLAIN QUERY PLAN for SQLite, EXPLAIN for PostgreSQL, SET SHOWPLAN_TEXT for SQL Server) and return a structured execution plan report.
```

---

## 2. MODIFIED Requirements

```text
When PushdownQueryPlan.BuildSql compiles a query containing CTE definitions,
the PushdownQueryPlan shall render all CTE definitions prior to the root SELECT statement.

When FSqlQueryBuilder rewrites expression trees,
the quotation rewriter shall intercept window function and CTE marker calls and preserve their structural AST nodes for the pushdown translator.
```

---

## 3. REMOVED Requirements

*None.*
