# Tasks Ledger: `where-not-null-empty-sqlite-and-multi-db-tree`

| ID | Status | Requisito | Descrição da Tarefa |
|---|---|---|---|
| **TASK-01** | `Done` | `REQ-FSQL-NOT-001` | Corrigir tradução de `not`, `isNull`, `ReferenceEquals`, `ConditionalExpression` e comparações booleanas em `translatePredicate`, `translateScalarCondition` e `translateScalarExpr` em `SqlPushdownQuery.fs`. |
| **TASK-02** | `Done` | `REQ-FSQL-COND-002` | Implementar funções, métodos no builder `fsql`, custom operations (`whereNotNull`, `whereNotEmpty`, `whereNotNullOrEmpty`, `whereIsNull`, `whereIsEmpty`, `whereNotNullOrWhiteSpace`) e métodos fluentes em `SqlPushdownQueryable<'T>` para `notNull` e `notEmpty`. |
| **TASK-03** | `Done` | `REQ-SQLITE-DIRECT-003`, `REQ-ENGINE-004` | Garantir auto-staging sob demanda em `Connections.fs` e `FileSqliteEngine.fs`, criar views SQLite para nomes de conexão/arquivo/tabela de XML e CSV, adicionar `.rawQuery`/`.query` e suporte a `IQueryable` direto em conexões XML/CSV/JSON/Excel e anexar `engine.db` em consultas SQLite diretas. |
| **TASK-04** | `Done` | `REQ-MULTIDB-TREE-004` | Atualizar `SqlServerConnector`, `PostgreSqlConnector`, `MySqlConnector`, `MariaDbConnector`, `OracleConnector`, `DatabaseConnection.GenerateEntities` e `ConnectionsTreeProvider` (`tree.ts`) para buscar todos os databases existentes e mapeá-los em estrutura de árvore. |
| **TASK-05** | `Done` | `REQ-FSQL-NOT-001`..`004` | Escrever testes unitários e de integração (mínimo 2 por cenário, cobertura >= 90%), executar `dotnet fsi .\build.fsx release` e validar E2E todos os notebooks de exemplo. |
| **TASK-06** | `Done` | `REQ-FSQL-NOT-001`..`004` | Criar células de exemplo para os novos casos em `SampleDataGenerator.fs` e `AdvancedSampleGenerator.fs` (EN e PT) e atualizar a especificação (`FSQL_LANGUAGE_SPECIFICATION.md`, `docs/sdd/`) e documentações (`USER_GUIDE`, `TUTORIAL`, `README`, `CHANGELOG`, `FSQL_AI_AGENT_GUIDE`). |
