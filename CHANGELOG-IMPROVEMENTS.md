# Changelog de Melhorias Arquiteturais e Limpeza
**F# Query Studio** — Registro de Execução e Acompanhamento das Melhorias

Todas as alterações listadas neste documento são rastreadas a partir da proposta técnica em [`docs/proposta-melhorias-arquiteturais-e-remocao-codigo-morto.md`](docs/proposta-melhorias-arquiteturais-e-remocao-codigo-morto.md) e executadas na branch `feature/architectural-improvements-and-cleanup`.

---

## Índice de Itens e Subagentes Responsáveis

| ID | Item de Melhoria | Subagente Responsável | Status Atual |
|---|---|---|---|
| **ITEM-01** | Remoção de Código Morto e Módulos Órfãos (Avalonia UI) | `dead-code-cleaner` | 🟢 Concluído |
| **ITEM-02** | Central Package Management (CPM) e Higiene de `.fsproj` | `dependency-manager` | 🟢 Concluído |
| **ITEM-03** | Resiliência de Cancelamento sem Kill de Processo | `cancellation-resilience` | 🟡 Planejado |
| **ITEM-04** | Isolamento Concorrente de Cursores de Paginação | `concurrency-paging` | 🟢 Concluído |
| **ITEM-05** | Correção de Acúmulo de Saída de Console em Células | `evaluation-output-buffer` | 🟢 Concluído |
| **ITEM-06** | Refatoração de Conectores, Sockets HTTP e DataReaders | `connectors-refactor` | 🟡 Planejado |
| **ITEM-07** | Otimização de Performance e Reflexão na Projeção | `projection-performance` | 🟡 Planejado |
| **ITEM-08** | Ativação de Provedores Go-to-Definition e Rename no VS Code | `vscode-lang-features` | 🟡 Planejado |

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
- **Status**: 🟡 Planejado
- **Arquivos a modificar**:
  - `src/FSharpQueryStudio.Worker/Handlers.fs`
  - `src/FSharpQueryStudio.Execution/CancellationSignal.fs`
  - `src/FSharpQueryStudio.Connectors/Connections.fs`
- **Critério de Aceite**: Cancelamento de consulta SQL cancela cooperativamente via ADO.NET/token sem matar o Worker e sem perder sessões FSI.
- **Registro de Mudanças**:
  - *Aguardando início.*

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
- **Status**: 🟡 Em Progresso
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

---

### [ITEM-07] Otimização de Performance e Reflexão na Projeção
- **Subagente**: `projection-performance`
- **Status**: 🟡 Planejado
- **Arquivos a modificar**:
  - `src/FSharpQueryStudio.Results/Projection.fs`
  - `tests/FSharpQueryStudio.Results.Tests/ProjectionTests.fs`
- **Critério de Aceite**: Eliminação de reflexão dinâmica repetida por célula na renderização de dicionários e mapas.
- **Registro de Mudanças**:
  - *Aguardando início.*

---

### [ITEM-08] Ativação de Provedores Go-to-Definition e Rename no VS Code
- **Subagente**: `vscode-lang-features`
- **Status**: 🟡 Planejado
- **Arquivos a modificar**:
  - `editors/vscode/src/protocol.ts`
  - `editors/vscode/src/lang/analysis-client.ts`
  - `editors/vscode/src/lang/providers.ts`
  - Testes em `editors/vscode/test/`
- **Critério de Aceite**: Navegação para definição (F12) e renomeação de identificadores (F2) funcionam nativamente nas células de notebook F#.
- **Registro de Mudanças**:
  - *Aguardando início.*
