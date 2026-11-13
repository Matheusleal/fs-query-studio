# Delta Specification: Universal SQL Pushdown (EF-style Select/Join/Where) & File SQLite Engine

## 1. ADDED Requirements

### [REQ-PUSHDOWN-001] Pushdown de Projeção (`Select`)
When a LINQ query applies a projection (`Select`) with explicit member accesses or anonymous records (`{| ... |}`),
the query translator shall compile the projection into explicit SQL column expressions (`SELECT t0.col1 AS Col1, t0.col2 AS Col2`) matching the target database dialect, omitting unrequested columns from the SQL query sent to the database.

### [REQ-PUSHDOWN-002] Mapeamento Dinâmico de Linhas Projetadas
When a database returns result columns corresponding to an anonymous record or tuple projection,
the query executor shall map the returned columns into the target projected type using compiled delegates without requiring all columns of the underlying source table.

### [REQ-PUSHDOWN-003] Pushdown de Junções Relacionais (`Join`)
Where two or more queryable sources reside within the same database connection or shared staging engine,
when a query defines an inner join (`fsql { for a in ... join b in ... on (a.Key = b.Key) }`),
the query translator shall compile the join into a native SQL `INNER JOIN` with corresponding `ON` equality clauses, executing the full relational operation inside the database engine.

### [REQ-PUSHDOWN-004] Suporte a Dialetos de Projeção e Expressões Simples
When a projection expression contains simple scalar operations (such as string concatenation or basic arithmetic),
the query translator shall translate the expression into native dialect syntax (`||` for SQLite and PostgreSQL, `+` for SQL Server) or fallback to post-projection client evaluation when unsupported natively.

### [REQ-PUSHDOWN-005] Inspeção Pré-Execução de Queries via `ResultKind.toRawSql`
When the user invokes `ResultKind.toRawSql`, `toRawSql`, or `.ToRawSql()` on a pushdown query,
the system shall extract and return the generated SQL statement and formatted parameter bindings as text without executing the command or dispatching queries to the database.

### [REQ-ENGINE-001] Staging Transacional de Arquivos Locais no SQLite
Where a workspace contains registered tabular file connections (CSV or Excel),
the workspace file engine shall stage file tables into a dedicated SQLite database located in `.fsqs/cache/engine.db` using high-performance batch transactions with Write-Ahead Logging (`WAL`) enabled.

### [REQ-ENGINE-002] Invalidação e Cache de Arquivos
When staging a CSV or Excel file,
the workspace file engine shall check the file's `LastWriteTimeUtc` and byte length against cached metadata, reusing existing staged SQLite tables when unchanged without re-reading the source file.

### [REQ-ENGINE-003] Índices Automáticos em Colunas Chave
When a file table is staged into the SQLite engine,
the engine shall automatically create B-Tree indexes on columns identified as identifiers (such as `Id` or ending with `_id`), accelerating relational join operations.

### [REQ-ENGINE-004] Execução Direta de Consultas SQL (`Connections.query`)
When the user invokes `Connections.query` with a raw SQL string,
the workspace engine shall execute the query against the consolidated SQLite database containing all staged workspace files, returning the results directly as a streaming `ResultKind.Table`.

### [REQ-ENGINE-005] Suporte a Escala (>200.000 Linhas)
When joining multiple tabular files containing 200,000 or more rows,
the system shall complete the join via the database engine with streaming pagination and without triggering large-object heap (LOH) exhaustion or out-of-memory exceptions.

---

## 2. MODIFIED Requirements

### [REQ-GEN-001] Geração de Entidades Limpa sem Subqueries Redundantes
MODIFIED FROM: `GeneratedEntities.fs` gerando strings fixas `SELECT col1, col2, ... FROM table` envelopadas em `(SELECT ...) AS _src`.
TO:
When generating strongly-typed entity modules in `GeneratedEntities.fs`,
the generator shall emit typed table references (`conn.Table<schema.Table>("TableName")`) allowing the query translator to construct clean, aliased SQL statements (`FROM TableName AS _t0`) with dynamic column projection.

### [REQ-CON-001] Conexões CSV e Excel Apoiadas por Staging Engine
MODIFIED FROM: `Connections.csv.<Name>.Rows` e `Connections.excel.<Name>.Rows` retornando apenas `IEnumerable` em memória para queries LINQ.
TO:
When querying `Connections.csv.<Name>` or `Connections.excel.<Name>` through `fsql { ... }`,
the connector shall provide an `IQueryable` backed by the SQLite staging engine, enabling transparent pushdown of `Where`, `OrderBy`, `Select`, and multi-file `Join`.

---

## 3. REMOVED Requirements

### [REQ-FALLBACK-001] Abandono Incondicional de Pushdown em `hasJoin`
REMOVED: Abandono automático de pushdown para `LinqInMemoryFallback.evaluate` sempre que um `Join` é detectado na árvore de expressão.
REASON: Substituído por compilação nativa de `INNER JOIN` em SQL para fontes do mesmo banco ou engine.

