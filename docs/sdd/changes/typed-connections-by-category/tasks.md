# Tasks: Typed Connections By Category & Entity Mapping Progress

- [x] **TASK-01: Refatoração do módulo `Connections` com categorias de conexão e remoção de `connect*` / `asEntities`**
  - **Goal**: Implementar containers de categoria (`SqlCategory`, `CsvCategory`, `ExcelCategory`, `RestCategory`) no módulo `Connections` e remover as funções antigas `connect`, `connectCsv`, `connectExcel`, `connectRest` e a propriedade `.asEntities`.
  - **Related Requirements**: FR-CONN-CATEGORY-API, FR-CONN-CONNECT-METHODS, FR-CONN-AS-ENTITIES
  - **Done When**: O módulo `Connections` expõe `sql`, `csv`, `excel` e `rest`, e os métodos legados não existem mais no assembly.
  - **Status**: Completed

- [x] **TASK-02: Atualização do gerador `EntityBackgroundGenerator` para emitir conexões isoladas por categoria**
  - **Goal**: Modificar a geração de código em `EntityBackgroundGenerator.fs` para estender `Connections.sql`, `Connections.csv`, `Connections.excel`, `Connections.rest`, isolando tabelas/consultas exclusivamente sob cada conexão correspondente e gerando `rawQuery` para conexões SQL e `IQueryable<Row>` para CSV.
  - **Related Requirements**: FR-CONN-SQL-ISOLATION, FR-CONN-SQL-RAW-QUERY, FR-CONN-CSV-DIRECT-QUERY, FR-CONN-EXCEL-REST-ISOLATION, FR-CONN-CODEGEN-STRUCTURE
  - **Done When**: `GeneratedEntities.fs` compila com a nova estrutura, sem vazamento entre conexões e com suporte a `Connections.sql.<Conn>.<Table>`, `Connections.sql.<Conn>.rawQuery`, e `Connections.csv.<Conn>`.
  - **Status**: Completed

- [x] **TASK-03: Feedback visual na barra de status do VS Code durante mapeamento de entidades**
  - **Goal**: Emitir notificação do Worker via `HostChannel` para o VS Code (`entityMapping/status`) no início e fim da geração de entidades, exibindo um `StatusBarItem` com spinner `$(sync~spin) Mapping entities...` na barra inferior.
  - **Related Requirements**: FR-UI-ENTITY-MAPPING-STATUS
  - **Done When**: A barra de status exibe o indicador animado enquanto as entidades estão sendo inspecionadas/geradas e é atualizada ou ocultada ao finalizar.
  - **Status**: Completed

- [x] **TASK-04: Atualização de `SampleDataGenerator.fs`, documentação e tutoriais**
  - **Goal**: Atualizar o gerador de notebooks de demonstração e todos os arquivos de documentação/guias para usar a nova sintaxe `Connections.sql.<Conn>.<Table>` e `Connections.csv.<Conn>`, removendo menções a `connect` e `asEntities`.
  - **Related Requirements**: FR-CONN-CATEGORY-API, FR-CONN-CONNECT-METHODS, FR-CONN-AS-ENTITIES
  - **Done When**: O notebook gerado por `createSampleData` e as documentações utilizam a nova sintaxe e executam sem erros.
  - **Status**: Completed

- [x] **TASK-05: Ajuste de autocompletion e providers na extensão VS Code**
  - **Goal**: Atualizar os providers de IntelliSense e autocompletion (`connection-name-completion.ts`, `providers.ts`) para reconhecer as novas categorias `Connections.sql`, `Connections.csv`, etc.
  - **Related Requirements**: FR-CONN-CATEGORY-API
  - **Done When**: A extensão fornece autocompletion correto para as categorias e nomes de conexão.
  - **Status**: Completed

- [x] **TASK-06: Atualização e criação de testes automatizados com política estrita de limpeza**
  - **Goal**: Ajustar os testes unitários e de integração em F# e TypeScript para validar a nova API e garantir que todas as pastas/arquivos temporários criados em testes sejam devidamente excluídos ao final.
  - **Related Requirements**: FR-TEST-CLEANUP-INTEGRITY, FR-CONN-SQL-ISOLATION, FR-CONN-CSV-DIRECT-QUERY
  - **Done When**: Todos os testes passam e nenhum arquivo ou diretório temporário residual permanece no disco.
  - **Status**: Completed

- [x] **TASK-07: Validação e execução de testes em ambiente local e container Linux**
  - **Goal**: Executar a suíte completa de testes no Windows (dotnet test + npm test) e no container Linux (`docker run`), garantindo 100% de sucesso sem pendências antes de qualquer entrega.
  - **Related Requirements**: Todos
  - **Done When**: 100% dos testes passam sem falhas no ambiente local e no Docker Linux.
  - **Status**: Completed
