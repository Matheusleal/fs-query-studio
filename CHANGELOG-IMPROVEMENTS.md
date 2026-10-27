# Changelog de Melhorias Arquiteturais e Limpeza
**F# Query Studio** — Registro de Execução e Acompanhamento das Melhorias

Todas as alterações listadas neste documento são rastreadas a partir da proposta técnica em [`docs/proposta-melhorias-arquiteturais-e-remocao-codigo-morto.md`](docs/proposta-melhorias-arquiteturais-e-remocao-codigo-morto.md) e executadas na branch `feature/architectural-improvements-and-cleanup`.

---

## Índice de Itens e Subagentes Responsáveis

| ID | Item de Melhoria | Subagente Responsável | Status Atual |
|---|---|---|---|
| **ITEM-01** | Remoção de Código Morto e Módulos Órfãos (Avalonia UI) | `dead-code-cleaner` | 🟢 Concluído |
| **ITEM-02** | Central Package Management (CPM) e Higiene de `.fsproj` | `dependency-manager` | 🟢 Concluído |
| **ITEM-03** | Resiliência de Cancelamento sem Kill de Processo | `cancellation-resilience` | 🟢 Concluído |
| **ITEM-04** | Isolamento Concorrente de Cursores de Paginação | `concurrency-paging` | 🟢 Concluído |
| **ITEM-05** | Correção de Acúmulo de Saída de Console em Células | `evaluation-output-buffer` | 🟢 Concluído |
| **ITEM-06** | Refatoração de Conectores, Sockets HTTP e DataReaders | `connectors-refactor` | 🟢 Concluído |
| **ITEM-07** | Otimização de Performance e Reflexão na Projeção | `projection-performance` | 🟢 Concluído |
| **ITEM-08** | Ativação de Provedores Go-to-Definition e Rename no VS Code | `vscode-lang-features` | 🟢 Concluído |
| **ITEM-09** | Descoberta Multi-Schema e Consultas LINQ com Type Provider | `schema-discovery`, `linq-type-provider`, `integration-tests` | 🟢 Concluído |
| **ITEM-10** | Documentação Bilíngue, Gerador de Dados de Exemplo e Links Oficiais | `docs-and-samples` | 🟢 Concluído |

---

## Detalhamento dos Itens e Registro de Mudanças

### [ITEM-01] Remoção de Código Morto e Módulos Órfãos
- **Subagente**: `dead-code-cleaner`
- **Status**: 🟢 Concluído
- **Arquivos a remover**:
  - `src/FSharpQueryStudio.Protocol/UiState.fs`
  - `src/FSharpQueryStudio.Storage/UiState.fs`
  - `src/FSharpQueryStudio.Protocol/EditorRequest.fs`
  - `src/FSharpQueryStudio.Protocol/HostNotification.fs`
  - `src/FSharpQueryStudio.Logging/LogFileReader.fs`
  - `src/FSharpQueryStudio.Logging/DiagnosticBundle.fs`
  - `src/FSharpQueryStudio.Security/OriginDetection.fs`
  - `src/FSharpQueryStudio.Storage/FsProj.fs`
  - `src/FSharpQueryStudio.Storage/PackageApproval.fs`
  - `src/FSharpQueryStudio.Storage/PackageLock.fs`
  - `src/FSharpQueryStudio.Storage/Packages.fs`
  - `src/FSharpQueryStudio.Protocol/Packages.fs`
  - `src/FSharpQueryStudio.Protocol/Trust.fs`
  - `src/FSharpQueryStudio.Storage/Trust.fs`
  - 9 arquivos de teste correspondentes em `tests/`
  - Diretórios vazios `src/FSharpQueryStudio.Analysis` e `tests/FSharpQueryStudio.Analysis.Tests`
- **Critério de Aceite**: `dotnet build` e `dotnet test` compilam com 0 warnings e 100% de testes ativos passando.
- **Registro de Mudanças**:
  - `STEP-01 Concluído`: Diretórios fantasmas `src/FSharpQueryStudio.Analysis` e `tests/FSharpQueryStudio.Analysis.Tests` removidos do filesystem.
  - `STEP-02 Concluído`: Módulos órfãos de UI/Desktop (`UiState`, `EditorRequest`, `HostNotification`, `Frames.EditorEnvelope`, `LogFileReader`, `DiagnosticBundle`, `OriginDetection`) e seus testes removidos dos projetos e do repositório. Build e testes 100% verdes.
  - `STEP-03 Concluído`: Subsistemas de Packages, Trust e FsProj removidos de Storage, Protocol e testes (`PackagesTests`, `TrustTests`, `PackageLockTests`, `FsProjTests`, `PackageApprovalTests`). 283 testes ativos passando sem warnings.

---

### [ITEM-02] Central Package Management (CPM) e Higiene de `.fsproj`
- **Subagente**: `dependency-manager`
- **Status**: 🟢 Concluído
- **Arquivos a modificar/criar**:
  - `Directory.Packages.props` (criação)
  - 19 arquivos `.fsproj` (limpeza de versões e remoção de `System.Security.Cryptography.Pkcs`)
- **Critério de Aceite**: Build e restore determinísticos via CPM sem dependências órfãs.
- **Registro de Mudanças**:
  - `STEP-04 Concluído`: Central Package Management habilitado via `Directory.Packages.props`, inclusão centralizada de `FSharp.Core` no `Directory.Build.props`, e remoção de atributos `Version` e diretivas `Update` redundantes nos 19 arquivos `.fsproj`. Restore e build determinísticos com 283 testes passando.
  - `STEP-05 Concluído`: Remoção de `System.Security.Cryptography.Pkcs` de todos os 19 arquivos `.fsproj`. Pinned transitivamente via `CentralPackageTransitivePinningEnabled` no `Directory.Packages.props` mitigando vulnerabilidades sem redundância no código-fonte. 283 testes passando com 0 warnings.

---

### [ITEM-03] Resiliência de Cancelamento sem Kill de Processo
- **Subagente**: `cancellation-resilience`
- **Status**: 🟢 Concluído
- **Arquivos a modificar**:
  - `src/FSharpQueryStudio.Worker/Handlers.fs`
  - `src/FSharpQueryStudio.Execution/CancellationSignal.fs`
  - `tests/FSharpQueryStudio.Execution.Tests/CancellationSignalTests.fs`
- **Critério de Aceite**: Cancelamento de consulta SQL cancela cooperativamente via ADO.NET/token sem matar o Worker e sem perder sessões FSI.
- **Registro de Mudanças**:
  - `Concluído`: Mecanismo cooperativo de sinalização e cancelamento via `CancellationSignal` e `Handlers.handleCancel` auditado e validado. Avaliações em andamento recebem o sinal de cancelamento cooperativamente sem término forçado do processo (`proc.kill()`), preservando o estado e bindings do FSI para as células subsequentes. 100% dos testes de cancelamento passando.

---

### [ITEM-04] Isolamento Concorrente de Cursores de Paginação
- **Subagente**: `concurrency-paging`
- **Status**: 🟢 Concluído
- **Arquivos a modificar**:
  - `src/FSharpQueryStudio.Worker/Handlers.fs`
  - `tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs`
- **Critério de Aceite**: `currentPaging` suporta múltiplos cursores simultâneos via `ConcurrentDictionary`, evitando race conditions entre abas de notebook.
- **Registro de Mudanças**:
  - `STEP-07 Concluído`: Substituição de cursor mutável único por `ConcurrentDictionary<string, PagingCursor>` e `ConcurrentDictionary<string, string>` em `Handlers.fs`. Múltiplas abas e notebooks concorrentes mantêm cursores independentes sem colisão. Novo teste unitário validando concorrência adicionado e 285 testes verdes.

---

### [ITEM-05] Correção de Acúmulo de Saída de Console em Células
- **Subagente**: `evaluation-output-buffer`
- **Status**: 🟢 Concluído
- **Arquivos a modificar**:
  - `src/FSharpQueryStudio.Execution/Evaluation.fs`
  - `tests/FSharpQueryStudio.Execution.Tests/EvaluationTests.fs`
- **Critério de Aceite**: Execuções consecutivas na mesma sessão exibem apenas a saída de console gerada na célula em execução.
- **Registro de Mudanças**:
  - `STEP-06 Concluído`: `Evaluation.evaluate` agora limpa os buffers de `session.Output` e `session.Errors` antes de cada execução. Novo teste `evaluate clears previous stdout and stderr between consecutive evaluations in the same session` adicionado e validado. (284 testes verdes).

---

### [ITEM-06] Refatoração de Conectores, Sockets HTTP e DataReaders
- **Subagente**: `connectors-refactor`
- **Status**: 🟢 Concluído
- **Arquivos a modificar**:
  - `src/FSharpQueryStudio.Connectors/IConnector.fs`
  - `src/FSharpQueryStudio.Connectors/SqliteConnector.fs`
  - `src/FSharpQueryStudio.Connectors/RestConnector.fs`
  - `src/FSharpQueryStudio.Connectors/Connections.fs`
  - `src/FSharpQueryStudio.Connectors/SqlServerConnector.fs`
  - `src/FSharpQueryStudio.Connectors/PostgreSqlConnector.fs`
- **Critério de Aceite**: `IQueryableConnector` e `ConnectorQueryResult` movidos para `IConnector.fs`, `HttpClient` singleton no `RestConnector`, fechamento determinístico de DataReaders e telemetria no `Connections.csv`/`excel`.
- **Registro de Mudanças**:
  - `STEP-08 Concluído`: `ConnectorQueryResult` e `IQueryableConnector` desacoplados de `SqliteConnector.fs` e movidos para `IConnector.fs`. Conectores SQL e fakes agora dependem diretamente do contrato canônico de abstrações. 285 testes verdes.
  - `STEP-09 Concluído`: `RestConnector` agora reutiliza uma instância singleton de `HttpClient` quando nenhum cliente customizado é injetado, prevenindo exaustão de sockets TCP. Conectores SQL (`SqliteConnector`, `SqlServerConnector`, `PostgreSqlConnector`) agora abrem leitores com `CommandBehavior.CloseConnection`, assegurando fechamento e descarte determinístico de conexões ADO.NET. 49 testes de conectores verdes.
  - `STEP-10 Concluído`: `Connections.csv` e `Connections.excel` agora utilizam `logOnceDrained` com medição de tempo (`Stopwatch`) e contagem lazy de linhas, unificando a telemetria do Serilog entre todos os conectores (SQL, CSV e Excel). Testes de enumeração adicionados em `ConnectionsTests.fs`. 49 testes de conectores verdes.

---

### [ITEM-07] Otimização de Performance e Reflexão na Projeção
- **Subagente**: `projection-performance`
- **Status**: 🟢 Concluído
- **Arquivos a modificar**:
  - `src/FSharpQueryStudio.Results/Projection.fs`
  - `tests/FSharpQueryStudio.Results.Tests/ProjectionTests.fs`
- **Critério de Aceite**: Eliminação de reflexão dinâmica repetida por célula na renderização de dicionários e mapas.
- **Registro de Mudanças**:
  - `STEP-11 Concluído`: Implementado cache thread-safe (`ConcurrentDictionary<Type, IDictionaryAccessor option>`) com abstração `IDictionaryAccessor` em `Projection.fs`. Tipos derivados de `IDictionary` agora executam sem nenhuma reflexão dinâmica via cast direto, e tipos genéricos/F# Maps realizam lookup de `PropertyInfo`/`MethodInfo` apenas uma única vez por tipo. A duração da suíte de testes de `Results` foi reduzida em mais de 10x (de ~950ms para ~60ms). Novo teste de projeção de grande volume adicionado (47 testes verdes).

---

### [ITEM-08] Ativação de Provedores Go-to-Definition e Rename no VS Code
- **Subagente**: `vscode-lang-features`
- **Status**: 🟢 Concluído
- **Arquivos a modificar**:
  - `editors/vscode/src/protocol.ts`
  - `editors/vscode/src/lang/analysis-client.ts`
  - `editors/vscode/src/lang/providers.ts`
  - Testes em `editors/vscode/test/`
- **Critério de Aceite**: Navegação para definição (F12) e renomeação de identificadores (F2) funcionam nativamente nas células de notebook F#.
- **Registro de Mudanças**:
  - `STEP-12 Concluído`: Ativados os endpoints de análise `analysis/definition` e `analysis/rename` no cliente TypeScript (`analysis-client.ts`), com mapeamento bidirecional de células e linhas de notebook (`mapSourcePositionToCell` e `mapSourceRangeToCell` em `providers.ts`). Registrados `DefinitionProvider` e `RenameProvider` no VS Code. Todos os 245 testes unitários e de integração do VS Code passando com sucesso.

---

### [ITEM-09] Descoberta Multi-Schema e Consultas LINQ com Type Provider
- **Subagentes**: `schema-discovery`, `linq-type-provider`, `integration-tests`
- **Status**: 🟢 Concluído
- **Objetivos**:
  - **Descoberta Multi-Schema**: Expandir a descoberta de schemas em todos os bancos suportados (PostgreSQL, SQL Server, SQLite) para incluir múltiplos schemas de usuário (ex: `dbo`, schemas customizados), nomeando tabelas com prefixo do schema quando fora do default (ex: `dbo.MinhaTabela`).
  - **Type Provider & Consultas LINQ**: Adicionar suporte ao `SQLProvider` (`FSharp.Data.SqlProvider`) com gerenciamento centralizado de pacotes (CPM) e helpers LINQ de alta performance (`asQueryable`, mapeamentos tipados) sem degradação de throughput.
  - **Testes de Integração Locais Dedicados**: Suíte de testes automatizados com arquivos locais para SQLite e containers Docker (Testcontainers) para PostgreSQL e SQL Server, cobrindo criação de schemas, tabelas e consultas LINQ.
- **Critério de Aceite**:
  1. Conexões PostgreSQL com schemas como `dbo` mapeiam e exibem suas tabelas e colunas na árvore do VS Code.
  2. Suporte a consultas LINQ com tipagem estática e operadores `query { ... }` funcionando nas conexões.
  3. 100% dos testes passando localmente e no container Linux (`docker run`).
- **Registro de Mudanças**:
  - `STEP-13 Concluído`: Subagente `schema-discovery` expandiu a descoberta de metadados em `PostgreSqlConnector.fs` (removido filtro restritivo de `public`, suportando schemas como `dbo` e views), `SqlServerConnector.fs` (preservação de schemas e views) e `SqliteConnector.fs` (suporte a views).
  - `STEP-14 Concluído`: Subagente `linq-type-provider` adicionou `SQLProvider` via CPM (`Directory.Packages.props`), helpers LINQ de alta performance (`query<'T>`, `queryCsv<'T>`, `queryExcel<'T>`, `result.As<'T>()`, `result.AsQueryable<'T>()`) com mapeamento dinâmico sem reflexão repetida por linha, e injeção automática de `SQLProvider` e `System.Linq` no `Prologue.fs` e `FsiSession.fs`.
  - `STEP-15 Concluído`: Subagente `integration-tests` implementou `LocalDatabaseIntegrationTests.fs`, validando criação e consulta em arquivos locais SQLite e containers Docker para PostgreSQL e SQL Server com schemas e LINQ. 291 testes passaram no container Linux e 245 testes no VS Code.

---

### [ITEM-10] Documentação Bilíngue, Gerador de Dados de Exemplo e Links Oficiais
- **Subagente**: `docs-and-samples`
- **Status**: 🟢 Concluído
- **Objetivos**:
  - **Documentação Bilíngue**: Disponibilizar guias completos em Inglês (`USER_GUIDE.md`) e Português (`USER_GUIDE_PT.md`), com detecção automática do idioma do usuário e links cruzados.
  - **Links Oficiais**: Adicionar links diretos para a documentação oficial da Microsoft de F#, F# Query Expressions (LINQ), F# Interactive (FSI) e SQLProvider.
  - **Gerador de Base de Dados Fictícia**: Criar funcionalidade 1-clique (`$(database)` no cabeçalho de Connections e Command Palette) para gerar banco relacional SQLite (`sample_store.db` com Categories, Products, Customers, Orders e OrderItems), feed CSV (`sales_feed.csv`) e notebook executável interativo (`sample_demo.fsqnb`).
  - **Exemplos Ricos de LINQ**: Adicionar exemplos de consultas básicas, joins entre tabelas, agregações/groupings, consultas em CSV e combinação em memória na documentação.
- **Registro de Mudanças**:
  - Implementado `SampleDataGenerator.fs` em `FSharpQueryStudio.Connectors`.
  - Adicionado endpoint `workspace/createSampleData` em `Handlers.fs` e comando `fs-query-studio.createSampleData` na extensão VS Code.
  - Atualizado `openDocsCommand` para selecionar automaticamente o idioma apropriado (`pt` ou `en`).
  - Escritos `USER_GUIDE.md` (EN) e `USER_GUIDE_PT.md` (PT) com exemplos completos e links oficiais.


