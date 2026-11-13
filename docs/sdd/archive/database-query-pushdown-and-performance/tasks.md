# Tasks: Database Query Pushdown & High-Performance Mapping

- [x] **TASK-01: Ciclo de vida re-enumerável do DataReader nos conectores SQL**
  - **Goal**: Refatorar `ExecuteQuery` em `PostgreSqlConnector`, `SqlServerConnector` e `SqliteConnector` para retornar um `IEnumerable` que abre a conexão e o leitor dentro de `GetEnumerator()`, permitindo múltiplas enumerações sem erros de objeto descartado ou leitor fechado.
  - **Related Requirements**: FR-STREAMING-REENUMERABLE-READER
  - **Done When**: Sequências retornadas por `ExecuteQuery` podem ser enumeradas mais de uma vez (`Seq.length` seguido de `Seq.iter`) sem disparar exceções de descarte de conexão.
  - **Status**: Completed

- [x] **TASK-02: Mapeador compilado de alta performance (`IDataRecord -> 'T`) sem alocações intermediárias**
  - **Goal**: Implementar em `Connections.fs` a compilação de delegado via `System.Linq.Expressions` que lê tipos nativos diretamente de `IDataRecord` (`GetInt32`, `GetString`, `GetDecimal`, `GetDateTime`, etc.), instanciando o record F# sem criar arrays `obj[]` transitórios por linha.
  - **Related Requirements**: FR-COMPILED-EXPRESSION-MAPPER, FR-CONN-VALUE-CONVERSION
  - **Done When**: O mapeamento de registros executa via delegado compilado, suporta `DateOnly`/`DateTime`/`Option` e reduz sensivelmente o tempo de mapeamento e alocações do GC.
  - **Status**: Completed

- [x] **TASK-03: Provedor LINQ com SQL Pushdown (`SqlPushdownQueryable` e `SqlPushdownQueryProvider`)**
  - **Goal**: Construir a infraestrutura de tradução de expressões LINQ para SQL parametrizado, suportando filtros (`where`), ordenação (`sortBy`, `thenBy`), paginação (`take`, `skip`) e agregações (`count`, `sumBy`), com fallback em memória para predicados complexos.
  - **Related Requirements**: FR-QUERY-PUSHDOWN-PROVIDER, FR-QUERY-PUSHDOWN-PARAM-BINDING, FR-QUERY-PUSHDOWN-FALLBACK, FR-CONN-QUERYABLE-IMPL
  - **Done When**: Expressões F# `fsql { for o in db.orders do where ... take ... }` geram e executam SQL com cláusulas `WHERE`, `ORDER BY`, `LIMIT`/`TOP` no banco de dados com parâmetros seguros (`@p0`, `@p1`).
  - **Status**: Completed

- [x] **TASK-04: Integração no `GenerateEntities` e helpers de paginação e filtro**
  - **Goal**: Atualizar o gerador de entidades para emitir propriedades tipadas baseadas em `SqlPushdownQueryable` e expor métodos auxiliares de conveniência (`.Where(sql, params)`, `.Page(page, pageSize)`).
  - **Related Requirements**: FR-ENTITY-PUSHDOWN-HELPERS, FR-CONN-QUERYABLE-IMPL
  - **Done When**: As classes de entidade geradas expõem consultas com pushdown nativo e os novos helpers compilam e funcionam perfeitamente.
  - **Status**: Completed

- [x] **TASK-05: Validação completa em testes unitários e de integração com Docker**
  - **Goal**: Atualizar e executar todos os testes da suíte, incluindo os testes de integração com contêineres Docker de PostgreSQL e SQL Server com esquemas de 10k e 15k linhas, validando a economia de memória e o ganho de desempenho.
  - **Related Requirements**: Todos
  - **Done When**: 100% dos testes passam localmente e no contêiner Linux sem falhas, sem vazamento de recursos e com benchmarks comprovando o ganho de pushdown.
  - **Status**: Completed

