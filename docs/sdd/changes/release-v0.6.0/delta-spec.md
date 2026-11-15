# Delta Specification: Release v0.6.0

## MODIFIED Requirements

Where extension and worker packages are versioned,
the build metadata shall define version 0.6.0 across manifest configurations and compatibility constants.

Where release documentation is maintained,
the project changelogs and readmes shall document all v0.6.0 enhancements and additions:
- Modern In-CE Common Table Expressions (CTEs): unnamed CTEs (`withCte subquery`) with transparent registration, named CTEs (`withCte "Name" subquery`), raw recursive CTEs (`withRecursiveCteSql "Name" sql` & `withRecursiveCteSql sql`), and automatic CTE deduplication and top-level bubble-up in compound queries.
- In-CE Set Operations: `union`, `unionAll`, `intersect`, and `except` written directly inside `fsql { ... }`, builder functions (`fsql.union`), module functions (`union`), and extension methods (`.Union()`).
- 100% Idiomatic Outer Joins: pure LINQ `GroupJoin` and `DefaultIfEmpty()` syntax for `fullOuterJoin` and `rightOuterJoin` inside `fsql { ... }`.
- Advanced DQL Pushdown Engine: column-level aggregate pushdown avoiding SQL Server T-SQL 412 ordering restrictions, dedicated `having` filtering, correlated row-level joins (`CROSS APPLY` on SQL Server, `CROSS JOIN LATERAL` on PostgreSQL), correlated subqueries (`whereExists`, `whereNotExists`, scalar subqueries in `select`), window functions (`fsql.window`, `rowNumber`, `rank`, `lag`, `lead`), and semi-structured JSON extraction (`fsql.jsonValue`).
- Live Query Plan Diagnostics & Formatted SQL: in-CE `explain` operation, `toRawQuery` formatted SQL output with parameter documentation headers, and live database engine execution plans (`explainQuery` and `.Explain()`).
- Unified Multi-Engine Demo Generator & Language Selection: single-click status bar / toolbar button (`$(database)`) and command with multi-engine selection (Original [SQLite, CSV, Excel, REST], PostgreSQL, SQL Server) and optional language selection (English, Português, Both / Ambos).
- Bilingual Documentation Parity: 100% synchronized English and Portuguese documentation across user guides, tutorials, FSQL language specifications, and AI coding agent guidelines (`FSQL_AI_AGENT_GUIDE.md` & `FSQL_AI_AGENT_GUIDE_PT.md`).
