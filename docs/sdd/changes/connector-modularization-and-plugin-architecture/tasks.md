# Tasks: Modularização Desacoplada de Conectores / Decoupled Connector Modularization (Micro-Kernel & Plugin SPI)

---

## 🇧🇷 Português (PT-BR)

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

- [x] **TASK-04: Refatoração de `FSharpQueryStudio.Connectors` como Composition Root (`BuiltinConnectors.fs`) e Gerador Modular**
  - **Goal**: Configurar `BuiltinConnectors.fs` em `FSharpQueryStudio.Connectors` como o ponto único de referência no backend para registrar os conectores nativos no `ConnectorRegistry` padrão e refatorar `EntityBackgroundGenerator` para delegar a emissão de código aos geradores modulares sem pattern matching de tipos concretos (`:?`).
  - **Related Requirements**: FR-CONN-COMPOSITION-ROOT, FR-CONN-MODULAR-ENTITY-GEN, FR-CONN-HARDCODED-TYPE-TESTS
  - **Done When**: `GeneratedEntities.fs` é gerado via `IConnectorEntityGenerator` dos descritores registrados em `BuiltinConnectors.fs`, eliminando type tests imperativos e mantendo paridade funcional total.
  - **Status**: Completed

- [x] **TASK-05: Implementação do Handler `connector/list` no Worker**
  - **Goal**: Implementar o método `handleConnectorList` em `Handlers.fs` no Worker para responder requisições `connector/list` com os metadados declarativos de todos os conectores disponíveis no `ConnectorRegistry`.
  - **Related Requirements**: FR-PROTOCOL-CONNECTOR-LIST
  - **Done When**: O Worker responde a `connector/list` com a lista JSON de descritores e seus campos de formulário.
  - **Status**: Completed

- [x] **TASK-06: Refatoração do Frontend VS Code para Registro Único (`connector-registry.ts`) e Formulários Declarativos (Schema-Driven UI)**
  - **Goal**: Centralizar toda a referência de conectores no frontend em `editors/vscode/src/connections/connector-registry.ts` e refatorar `commands.ts`, `add.ts`, `definition.ts` e `connection-name-completion.ts` para consumir `ConnectorFrontRegistry` e `connector/list` sem acoplamento espalhado.
  - **Related Requirements**: FR-VSCODE-SCHEMA-DRIVEN-UI, FR-VSCODE-HARDCODED-KIND-BRANCHES
  - **Done When**: A criação, edição e autocompletar de conexões no VS Code ocorrem de forma 100% orientada ao `ConnectorFrontRegistry` e aos metadados declarativos do Worker.
  - **Status**: Completed

- [x] **TASK-07: Testes Automatizados Unitários e de Integração (.NET & TypeScript)**
  - **Goal**: Implementar testes unitários para o `ConnectorRegistry`, cada conector isolado, o handler RPC `connector/list` e os providers/wizards declarativos no VS Code, utilizando exclusivamente recursos gerados pelos próprios testes e limpeza estrita de arquivos temporários.
  - **Related Requirements**: Todos
  - **Done When**: Todos os testes unitários e de integração (.NET e TS) passam com 100% de sucesso e sem resíduos em disco.
  - **Status**: Completed

- [x] **TASK-08: Validação Local e Container Linux (`docker run`) com Watcher Externo (5 min timeout)**
  - **Goal**: Validar compilação limpa de toda a solução e executar a suíte completa de testes no Windows e no container Linux (`docker run`), monitorada por watcher externo em inglês com timeout estrito de 5 minutos (300s).
  - **Related Requirements**: Todos
  - **Done When**: 100% dos testes passam sem falhas no ambiente local e no Docker Linux.
  - **Status**: Completed

---

## 🇺🇸 English (EN)

- [x] **TASK-01: Create `FSharpQueryStudio.Connectors.Abstractions` Project and SPI**
  - **Goal**: Define core interfaces (`IConnector`, `IQueryableConnector`, `IStreamingQueryableConnector`, `IConnectorEntityGenerator`), declarative descriptors (`ConnectorDescriptor`, `ConnectorFormField`), factories, and the thread-safe central `ConnectorRegistry`.
  - **Related Requirements**: FR-CONN-SPI-ABSTRACTIONS, FR-CONN-DESCRIPTOR-SCHEMA, FR-CONN-REGISTRY-RESOLUTION
  - **Done When**: `FSharpQueryStudio.Connectors.Abstractions` compiles with zero external database driver dependencies and includes unit tests for registration and resolution.
  - **Status**: Completed

- [x] **TASK-02: `ConnectorKind` Extensibility and `connector/list` RPC DTOs in Protocol**
  - **Goal**: Update `FSharpQueryStudio.Protocol` to support open/extensible connector identifiers while preserving backward compatibility, and declare request/response DTOs for `connector/list`.
  - **Related Requirements**: FR-CONN-KIND-EXTENSIBILITY, FR-PROTOCOL-CONNECTOR-LIST
  - **Done When**: `Protocol` serializes and deserializes built-in and custom connector identifiers and exposes the `connector/list` RPC frames.
  - **Status**: Completed

- [x] **TASK-03: Modularize Built-in Connectors into Independent F# Projects**
  - **Goal**: Create isolated class library projects (`FSharpQueryStudio.Connectors.Sqlite`, `SqlServer`, `PostgreSql`, `Csv`, `Excel`, `Rest`), moving concrete implementations and minimal NuGet dependencies, and exposing each connector's `IConnectorDescriptor` with declarative form schema and `IConnectorEntityGenerator`.
  - **Related Requirements**: FR-CONN-MODULAR-PACKAGING, FR-CONN-DESCRIPTOR-SCHEMA
  - **Done When**: Each built-in connector compiles in its own standalone project and provides its descriptor and declarative UI schema.
  - **Status**: Completed

- [x] **TASK-04: Refactor `FSharpQueryStudio.Connectors` as Composition Root (`BuiltinConnectors.fs`) and Modular Generator**
  - **Goal**: Configure `BuiltinConnectors.fs` in `FSharpQueryStudio.Connectors` as the single backend reference point to register built-in connectors in the default `ConnectorRegistry`, and refactor `EntityBackgroundGenerator` to delegate code emission to modular generators without concrete runtime type checks (`:?`).
  - **Related Requirements**: FR-CONN-COMPOSITION-ROOT, FR-CONN-MODULAR-ENTITY-GEN, FR-CONN-HARDCODED-TYPE-TESTS
  - **Done When**: `GeneratedEntities.fs` is generated via `IConnectorEntityGenerator` from descriptors registered in `BuiltinConnectors.fs`, eliminating imperative type tests and maintaining full functional parity.
  - **Status**: Completed

- [x] **TASK-05: Implement `connector/list` Handler in Worker**
  - **Goal**: Implement `handleConnectorList` in `Handlers.fs` in the Worker to answer `connector/list` requests with the declarative metadata of all connectors registered in `ConnectorRegistry`.
  - **Related Requirements**: FR-PROTOCOL-CONNECTOR-LIST
  - **Done When**: The Worker responds to `connector/list` with the JSON list of descriptors and their form fields.
  - **Status**: Completed

- [x] **TASK-06: Refactor VS Code Frontend to Single Registry (`connector-registry.ts`) and Schema-Driven UI**
  - **Goal**: Centralize all frontend connector references into `editors/vscode/src/connections/connector-registry.ts` and refactor `commands.ts`, `add.ts`, `definition.ts`, and `connection-name-completion.ts` to consume `ConnectorFrontRegistry` and `connector/list` without scattered coupling.
  - **Related Requirements**: FR-VSCODE-SCHEMA-DRIVEN-UI, FR-VSCODE-HARDCODED-KIND-BRANCHES
  - **Done When**: Connection creation, editing, and completion in VS Code are 100% driven by `ConnectorFrontRegistry` and declarative Worker metadata.
  - **Status**: Completed

- [x] **TASK-07: Automated Unit and Integration Tests (.NET & TypeScript)**
  - **Goal**: Implement unit tests for `ConnectorRegistry`, each isolated connector, the `connector/list` RPC handler, and the declarative VS Code providers/wizards, exclusively using self-generated test resources and strict temporary file cleanup.
  - **Related Requirements**: All
  - **Done When**: All unit and integration tests (.NET and TS) pass 100% with zero leftover artifacts on disk.
  - **Status**: Completed

- [x] **TASK-08: Local and Linux Container (`docker run`) Validation with External Watcher (5 min timeout)**
  - **Goal**: Validate clean compilation of the entire solution and execute the complete test suite on Windows and inside the Linux container (`docker run`), monitored by an English-language external watcher with a strict 5-minute (300s) timeout.
  - **Related Requirements**: All
  - **Done When**: 100% of tests pass without failures in both local and Linux Docker environments.
  - **Status**: Completed
