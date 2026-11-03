# Change Proposal: Modularização Desacoplada de Conectores / Decoupled Connector Modularization (Micro-Kernel & Plugin SPI)

---

## 🇧🇷 Português (PT-BR)

### Intent Block
```text
Intent Block:
Goal:        Desacoplar totalmente a arquitetura de conectores no F# Query Studio, permitindo que novos conectores de dados sejam desenvolvidos, testados e plugados de forma independente e isolada, com um único ponto de registro no backend (BuiltinConnectors.fs) e um único ponto no frontend (connector-registry.ts).
Expectation: O desenvolvedor pode criar um novo conector como um módulo .NET isolado (implementando IConnector e IConnectorDescriptor), enquanto o VS Code renderiza a interface de configuração dinamicamente a partir de metadados declarativos expostos pelo Worker.
Action:      (1) Dividir o monólito FSharpQueryStudio.Connectors em um núcleo de abstrações (FSharpQueryStudio.Connectors.Abstractions) e projetos isolados por conector (Sqlite, SqlServer, PostgreSql, Csv, Excel, RestJson); (2) Criar um Connector Registry dinâmico com SPI (Service Provider Interface) e um ponto central único de registro no backend (BuiltinConnectors.fs); (3) Desacoplar a geração de entidades em geradores modulares por conector; (4) Tornar o ConnectorKind extensível no Protocolo e introduzir a chamada RPC `connector/list` com descritores de UI declarativos; (5) Refatorar a extensão VS Code para consumir os descritores declarativos e centralizar toda lógica residual de conector em um único arquivo (`connector-registry.ts`).
Result:      Arquitetura desacoplada verticalmente: novos conectores podem ser adicionados tocando apenas no ponto central de registro, sem inflar o runtime principal com dependências cruzadas e com isolamento total de testes.
```

### Contexto & Motivação
A arquitetura anterior de conectores do F# Query Studio apresentava acoplamento rígido em todas as camadas:
1. **Monólito .NET (`FSharpQueryStudio.Connectors.fsproj`)**: Agregava todos os 6 conectores e todas as dependências NuGet pesadas num único projeto.
2. **Discriminated Union Fechado no Protocolo (`ConnectorKind`)**: Exigia alterar o protocolo central para cada novo conector.
3. **Pattern Matching Rígido no Runtime**: `WorkspaceConnectionResolver.fs` e `EntityBackgroundGenerator.fs` possuíam `match` e type tests imperativos (`:?`).
4. **Acoplamento na Extensão VS Code (TypeScript)**: Múltiplos arquivos (`add.ts`, `definition.ts`, `commands.ts`, `connection-name-completion.ts`) continham switches hardcoded por tipo de conector.

### Arquitetura Implementada: Micro-Kernel & Plugin SPI
1. **Núcleo de Abstrações (`FSharpQueryStudio.Connectors.Abstractions`)**: Contém `IConnector`, `IQueryableConnector`, `IStreamingQueryableConnector`, `IConnectorEntityGenerator`, `IConnectorDescriptor`, `ConnectorFormField` e `ConnectorRegistry`.
2. **Projetos Independentes de Conector**:
   - `FSharpQueryStudio.Connectors.Sqlite`
   - `FSharpQueryStudio.Connectors.SqlServer`
   - `FSharpQueryStudio.Connectors.PostgreSql`
   - `FSharpQueryStudio.Connectors.Csv`
   - `FSharpQueryStudio.Connectors.Excel`
   - `FSharpQueryStudio.Connectors.Rest`
3. **Ponto Único no Backend (`BuiltinConnectors.fs`)**: Único arquivo no backend que referencia e registra todos os módulos de conectores nativos no `ConnectorRegistry`.
4. **Ponto Único no Frontend (`connector-registry.ts`)**: Único arquivo no frontend que centraliza descritores padrão de fallback, sanitização de connection strings e mapeamento de categorias.

### Critérios de Sucesso (PT-BR)
- [x] `FSharpQueryStudio.Connectors.Abstractions` compila sem dependências de drivers de banco de dados externos.
- [x] Cada conector nativo (`Sqlite`, `SqlServer`, `PostgreSql`, `Csv`, `Excel`, `Rest`) possui seu projeto isolado.
- [x] Ponto único de registro no backend (`BuiltinConnectors.fs`) e ponto único no frontend (`connector-registry.ts`).
- [x] O Worker responde ao método `connector/list` retornando metadados declarativos completos.
- [x] Compatibilidade preservada com `workspace.json` (com `connections` ordenado ao final do arquivo) e encoding UTF-8 estrito no stdio do Worker.
- [x] Todos os testes automatizados (.NET e TypeScript) passam com 100% de sucesso localmente e no container Linux sob monitoramento do watcher externo (timeout de 5 minutos).

---

## 🇺🇸 English (EN)

### Intent Block
```text
Intent Block:
Goal:        Fully decouple the connector architecture in F# Query Studio, allowing new data connectors to be developed, tested, and plugged in independently and in isolation, with a single registration point in the backend (BuiltinConnectors.fs) and a single point in the frontend (connector-registry.ts).
Expectation: A developer can create a new connector as an isolated .NET module (implementing IConnector and IConnectorDescriptor), while VS Code dynamically renders the configuration UI from declarative metadata exposed by the Worker.
Action:      (1) Split the FSharpQueryStudio.Connectors monolith into an abstractions core (FSharpQueryStudio.Connectors.Abstractions) and isolated per-connector projects (Sqlite, SqlServer, PostgreSql, Csv, Excel, RestJson); (2) Create a dynamic Connector Registry with SPI (Service Provider Interface) and a single central registration point in the backend (BuiltinConnectors.fs); (3) Decouple entity generation into modular per-connector generators; (4) Make ConnectorKind extensible in the Protocol and introduce the `connector/list` RPC call with declarative UI descriptors; (5) Refactor the VS Code extension to consume declarative descriptors and centralize all connector-specific frontend rules into a single file (`connector-registry.ts`).
Result:      Vertically decoupled architecture: new connectors can be added by touching only the single registration point, without bloating the core runtime with cross-dependencies and with complete test isolation.
```

### Context & Motivation
Previously, the F# Query Studio connector architecture suffered from tight coupling across all layers:
1. **.NET Monolith (`FSharpQueryStudio.Connectors.fsproj`)**: Bundled all 6 connectors and heavy NuGet dependencies into a single project.
2. **Closed Discriminated Union in Protocol (`ConnectorKind`)**: Required modifying the core protocol for every new connector.
3. **Rigid Runtime Pattern Matching**: `WorkspaceConnectionResolver.fs` and `EntityBackgroundGenerator.fs` relied on hardcoded `match` expressions and imperative type tests (`:?`).
4. **Tight Coupling in the VS Code Extension (TypeScript)**: Multiple files (`add.ts`, `definition.ts`, `commands.ts`, `connection-name-completion.ts`) contained hardcoded switches per connector kind.

### Implemented Architecture: Micro-Kernel & Plugin SPI
1. **Abstractions Core (`FSharpQueryStudio.Connectors.Abstractions`)**: Defines `IConnector`, `IQueryableConnector`, `IStreamingQueryableConnector`, `IConnectorEntityGenerator`, `IConnectorDescriptor`, `ConnectorFormField`, and `ConnectorRegistry`.
2. **Independent Connector Projects**:
   - `FSharpQueryStudio.Connectors.Sqlite`
   - `FSharpQueryStudio.Connectors.SqlServer`
   - `FSharpQueryStudio.Connectors.PostgreSql`
   - `FSharpQueryStudio.Connectors.Csv`
   - `FSharpQueryStudio.Connectors.Excel`
   - `FSharpQueryStudio.Connectors.Rest`
3. **Single Backend Point (`BuiltinConnectors.fs`)**: The sole file in the backend that references and registers all built-in connector modules into `ConnectorRegistry`.
4. **Single Frontend Point (`connector-registry.ts`)**: The sole file in the frontend that centralizes fallback descriptors, connection string sanitization, and category mappings.

### Success Criteria (EN)
- [x] `FSharpQueryStudio.Connectors.Abstractions` compiles cleanly without external database driver dependencies.
- [x] Each native connector (`Sqlite`, `SqlServer`, `PostgreSql`, `Csv`, `Excel`, `Rest`) resides in its own isolated project.
- [x] Single registration point in the backend (`BuiltinConnectors.fs`) and single registry point in the frontend (`connector-registry.ts`).
- [x] The Worker responds to `connector/list` returning complete declarative metadata.
- [x] Backward compatibility preserved for `workspace.json` (with `connections` placed at the end of the file) and strict UTF-8 encoding enforced on Worker stdio.
- [x] All automated tests (.NET and TypeScript) pass with 100% success locally and inside the Linux container under external watcher monitoring (5-minute timeout).
