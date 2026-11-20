# Delta Specification: `where (not (isNull ...))`, Condições `notNull`/`notEmpty`, Query Direta SQLite para XML/CSV e Mapeamento Multi-Database em Árvore

## 1. ADDED Requirements

### [REQ-FSQL-NOT-001] Tradução Pushdown de `not` e `isNull` em Predicados `where`
```text
When a query predicate in `fsql` contains `isNull`, `not (isNull <col>)`, `Object.ReferenceEquals(<col>, null)`, boolean column negation `not <boolCol>`, or conditional boolean expressions,
the `ExpressionPushdownTranslator` shall translate the predicate into native SQL (`(<col> IS NULL)`, `(<col> IS NOT NULL)`, `(NOT <predicate>)`) across all supported SQL dialects without falling back to incompatible post-projection evaluation.
```

### [REQ-FSQL-COND-002] Condições Dedicadas `isNotNull`, `isNotEmpty` e `isNotNullOrEmpty`
```text
When a query uses `isNotNull`, `isNotEmpty`, or `isNotNullOrEmpty` inside `where` predicates in `fsql`,
the `ExpressionPushdownTranslator` and `FSqlQueryBuilder` shall compile the condition into dialect-appropriate SQL (`(<col> IS NOT NULL)` and `(<col> IS NOT NULL AND <col> <> '')` or `LENGTH(<col>) > 0` on Oracle) and evaluate accurately in memory when used outside pushdown, without exposing redundant custom operations (`whereNotNull`, `whereNotEmpty`, `whereNotNullOrEmpty`, etc.).
```

### [REQ-SQLITE-DIRECT-003] Execução Direta no SQLite para Fontes XML e CSV Mapeadas
```text
Where a workspace contains mapped CSV, XML, JSON, or Excel connections,
when the user executes a direct SQLite query (via `Connections.sql.query`, `Connections.sql.rawQuery`, `Connections.queryEngine`, `Connections.sql.<SqliteConn>.rawQuery`, `Connections.csv.<Conn>.rawQuery`, `Connections.xml.<Conn>.rawQuery`, or `fsql` directly over `Connections.xml.<Conn>` and `Connections.csv.<Conn>`),
the `Connections` module and `FileSqliteEngine` shall automatically ensure all mapped file sources are staged in `.fsqs/cache/engine.db` prior to execution and expose SQLite views for connection names, unprefixed table/collection names, and file base names so queries succeed regardless of table alias form.
```

### [REQ-MULTIDB-TREE-004] Descoberta de Todos os Databases e Mapeamento em Estrutura de Árvore
```text
When a database connection (`SqlServer`, `PostgreSql`, `MySql`, `MariaDb`, `Oracle`, `Sqlite`) is added or inspected,
the connector's `DiscoverSchemaAsync`, `DatabaseConnection.GenerateEntities`, and VS Code `ConnectionsTreeProvider` shall discover all accessible user databases on the server and map them in a hierarchical tree structure (`Connection -> Database -> [Schema ->] Table -> Column` in the TreeView and `Connections.sql.<Conn>.<Database>.[<Schema>.]<Table>` in generated F# entities) while preserving top-level default database table access (`Connections.sql.<Conn>.<Table>`).
```

---

## 2. MODIFIED Requirements

### [REQ-ENGINE-004] Execução Direta de Consultas SQL (`Connections.sql.query` / `Connections.sql.rawQuery`)
- **MODIFIED FROM**: `Connections.sql.query` e `Connections.sql.rawQuery` executando `ExecuteQuery` no `FileSqliteEngine` assumindo que as tabelas já foram previamente stageadas manualmente.
- **MODIFIED TO (GEARS)**:
```text
When the user invokes `Connections.sql.query`, `Connections.sql.rawQuery`, `Connections.queryEngine`, `Connections.queryEngineWith`, or `.rawQuery` on a file or SQLite connection,
the workspace engine shall synchronize any unstaged or modified workspace file connections (`Csv`, `Xml`, `Json`, `Excel`) into `.fsqs/cache/engine.db`, register table views matching the connection name and schema table names, and execute the SQL query against the consolidated SQLite database.
```
