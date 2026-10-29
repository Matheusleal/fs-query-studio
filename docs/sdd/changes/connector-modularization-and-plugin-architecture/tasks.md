# Tasks: Modularização Desacoplada de Conectores (Micro-Kernel & Plugin SPI)

- [x] **TASK-01: Criação do projeto `FSharpQueryStudio.Connectors.Abstractions` e SPI**
  - **Goal**: Definir interfaces fundamentais (`IConnector`, `IQueryableConnector`, `IStreamingQueryableConnector`, `IConnectorEntityGenerator`), descritores declarativos (`ConnectorDescriptor`, `ConnectorFormField`), fábricas e o registro central thread-safe `ConnectorRegistry`.
  - **Related Requirements**: FR-CONN-SPI-ABSTRACTIONS, FR-CONN-DESCRIPTOR-SCHEMA, FR-CONN-REGISTRY-RESOLUTION
  - **Done When**: O projeto `FSharpQueryStudio.Connectors.Abstractions` compila sem nenhuma dependência de drivers de banco de dados externos e possui testes unitários para registro e resolução.
  - **Status**: Completed

- [x] **TASK-02: Extensibilidade de `ConnectorKind` e DTOs RPC `connector/list` no Protocolo**
  - **Goal**: Atualizar `FSharpQueryStudio.Protocol` para suportar identificadores de conector extensíveis/abertos mantendo compatibilidade retroativa, e declarar os DTOs de solicitação e resposta para `connector/list`.
  - **Related Requirements**: FR-CONN-KIND-EXTENSIBILITY, FR-PROTOCOL-CONNECTOR-LIST
  - **Done When**: `Protocol` serializa e desserializa identificadores de conector padrão e customizados e expõe os frames para a chamada `connector/list`.
  - **Status**: Completed

- [x] **TASK-03: Modularização dos Conectores Nativos em Projetos F# Independentes**
  - **Goal**: Criar os projetos de classe isolados (`FSharpQueryStudio.Connectors.Sqlite`, `SqlServer`, `PostgreSql`, `Csv`, `Excel`, `Rest`), transferindo implementações concretas e dependências NuGet mínimas, e expondo para cada um o seu respectivo `IConnectorDescriptor` com schema de formulário declarativo e seu `IConnectorEntityGenerator`.
  - **Related Requirements**: FR-CONN-MODULAR-PACKAGING, FR-CONN-DESCRIPTOR-SCHEMA
  - **Done When**: Cada conector nativo compila em seu próprio projeto independente e disponibiliza seu descritor com esquema declarativo de UI.
  - **Status**: Completed

- [x] **TASK-04: Refatoração de `FSharpQueryStudio.Connectors` como Composition Root e Gerador Modular**
  - **Goal**: Configurar `FSharpQueryStudio.Connectors` como aggregador/composition root que registra os conectores nativos no `ConnectorRegistry` padrão e refatorar `EntityBackgroundGenerator` para delegar a emissão de código aos geradores modulares sem pattern matching de tipos concretos (`:?`).
  - **Related Requirements**: FR-CONN-COMPOSITION-ROOT, FR-CONN-MODULAR-ENTITY-GEN, FR-CONN-HARDCODED-TYPE-TESTS
  - **Done When**: `GeneratedEntities.fs` é gerado via `IConnectorEntityGenerator` dos descritores registrados, eliminando type tests imperativos e mantendo paridade funcional total.
  - **Status**: Completed

- [x] **TASK-05: Implementação do Handler `connector/list` no Worker**
  - **Goal**: Implementar o método `handleConnectorList` em `Handlers.fs` no Worker para responder requisições `connector/list` com os metadados declarativos de todos os conectores disponíveis no `ConnectorRegistry`.
  - **Related Requirements**: FR-PROTOCOL-CONNECTOR-LIST
  - **Done When**: O Worker responde a `connector/list` com a lista JSON de descritores e seus campos de formulário.
  - **Status**: Completed

- [x] **TASK-06: Refatoração do Frontend VS Code para Formulários Declarativos (Schema-Driven UI)**
  - **Goal**: Refatorar `commands.ts`, `add.ts` e `definition.ts` para carregar `connector/list` do Worker e renderizar dinamicamente o QuickPick e as etapas de coleta de input (arquivos, senhas, selects, strings) a partir do esquema declarativo, eliminando `KIND_PICKS` e condicionais procedurais hardcoded por conector.
  - **Related Requirements**: FR-VSCODE-SCHEMA-DRIVEN-UI, FR-VSCODE-HARDCODED-KIND-BRANCHES
  - **Done When**: A criação e edição de conexões no VS Code ocorrem de forma 100% orientada aos metadados declarativos do Worker, sem nenhuma condicional hardcoded por conector no TypeScript.
  - **Status**: Completed

- [x] **TASK-07: Testes Automatizados Unitários e de Integração (.NET & TypeScript)**
  - **Goal**: Implementar testes unitários para o `ConnectorRegistry`, cada conector isolado, o handler RPC `connector/list` e os providers/wizards declarativos no VS Code, com política estrita de limpeza de diretórios e arquivos temporários.
  - **Related Requirements**: Todos
  - **Done When**: Todos os testes unitários e de integração (.NET e TS) passam com 100% de sucesso e sem resíduos em disco.
  - **Status**: Completed

- [x] **TASK-08: Validação Local e Container Linux (`docker run`) com Watcher Externo (5 min timeout)**
  - **Goal**: Validar compilação limpa de toda a solução e executar a suíte completa de testes no Windows e no container Linux (`docker run`), monitorada por watcher externo com timeout estrito de 5 minutos (300s).
  - **Related Requirements**: Todos
  - **Done When**: 100% dos testes passam sem falhas no ambiente local e no Docker Linux.
  - **Status**: Completed

