# Proposta Técnica: Melhorias Arquiteturais e Remoção de Código Não Utilizado
**F# Query Studio** — Análise Integral da Base de Código (F# Worker + VS Code Extension)

---

## 1. Sumário Executivo

O **F# Query Studio** passou recentemente por uma transição arquitetural de grande porte: a aplicação desktop original baseada em Avalonia UI foi descontinuada e substituída por uma extensão nativa do VS Code (`editors/vscode`) alimentada por um processo worker em .NET 10 (`src/FSharpQueryStudio.Worker`) via protocolo NDJSON sobre `stdio`.

A suíte de testes atual reflete estabilidade funcional (100% de sucesso em 334 testes .NET e 243 testes TypeScript). No entanto, a análise aprofundada da base de código revelou dois cenários críticos que demandam evolução:

1. **Código Morto e Órfão Pós-Migração (~1.200+ linhas de código + 9 suítes de testes órfãs)**:
   Diversos módulos, tipos de protocolo e arquivos de teste foram criados exclusivamente para a interface desktop Avalonia (Monaco WebView bridge, persistência de DockLayout, visualizador de logs in-app, empacotador de logs em zip, detecção de origem de arquivos e sistema de aprovação de pacotes NuGet) e permaneceram no repositório sem nenhum consumidor ativo na extensão ou no worker.
2. **Oportunidades de Melhoria Arquitetural e Robustez**:
   - Resiliência de cancelamento no Worker (atualmente o cancelamento cooperativo não atinge o FSI/banco, forçando `kill` do processo e perda de estado).
   - Gerenciamento de estado de paginação (`currentPaging` é variável única global, sofrendo race condition e perda de cursor entre múltiplos notebooks).
   - Acúmulo de `stdout`/`stderr` em sessões persistentes entre células de notebook.
   - Posicionamento de interfaces nos conectores (`IQueryableConnector` acoplado dentro de `SqliteConnector.fs`).
   - Ciclo de vida e vazamento de conexões/sockets (`HttpClient` sem reuso em `RestConnector` e descarte tardio de ADO.NET DataReaders).
   - Governança de dependências .NET ausente (falta de Central Package Management, redundância de 19 referências a `System.Security.Cryptography.Pkcs`).

---

## 2. Inventário de Código Não Utilizado e Órfão

### 2.1 Módulos e Tipos Residuais da Era Avalonia UI (Código Morto)

| Componente | Arquivo(s) | Linhas | Motivo do Abandono / Diagnóstico |
|---|---|---|---|
| **`UiState` (Protocol & Storage)** | [`src/FSharpQueryStudio.Protocol/UiState.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Protocol/UiState.fs)<br>[`src/FSharpQueryStudio.Storage/UiState.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Storage/UiState.fs) | ~67 | Armazenava tema visual e blob do Dock.Avalonia em `%LOCALAPPDATA%`. O VS Code gerencia seu próprio tema e layout de janelas. Nenhum consumidor no worker ou na extensão. |
| **`EditorRequest` & `HostNotification`** | [`src/FSharpQueryStudio.Protocol/EditorRequest.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Protocol/EditorRequest.fs)<br>[`src/FSharpQueryStudio.Protocol/HostNotification.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Protocol/HostNotification.fs) | ~110 | Tipos de mensagens e envelopes bidirecionais entre o host Avalonia e a WebView com Monaco Editor. A extensão VS Code utiliza LSP nativo via `analysis/*` sobre NDJSON. |
| **`EditorEnvelope`** | [`src/FSharpQueryStudio.Protocol/Frames.fs#L8-L11`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Protocol/Frames.fs#L8-L11) | ~12 | Envelope de mensagens do Monaco WebView. Não utilizado por nenhum componente atual (apenas em um teste legado). |
| **`LogFileReader`** | [`src/FSharpQueryStudio.Logging/LogFileReader.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Logging/LogFileReader.fs) | ~123 | Parser e leitor de logs JSON criado para alimentar o visualizador de logs da interface desktop. No VS Code, os logs vão para o OutputChannel e arquivo Serilog diretamente. |
| **`DiagnosticBundle`** | [`src/FSharpQueryStudio.Logging/DiagnosticBundle.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Logging/DiagnosticBundle.fs) | ~45 | Gerador de `.zip` com logs dos últimos N dias acionado via botão na UI desktop. Inexistente no VS Code. |
| **`OriginDetection`** | [`src/FSharpQueryStudio.Security/OriginDetection.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Security/OriginDetection.fs) | ~35 | Detectava se um arquivo aberto veio da pasta Downloads, caminho externo ou projeto compartilhado. Não integrado na extensão do VS Code. |
| **`Trust` (Protocol & Storage)** | [`src/FSharpQueryStudio.Protocol/Trust.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Protocol/Trust.fs)<br>[`src/FSharpQueryStudio.Storage/Trust.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Storage/Trust.fs) | ~80 | Estrutura de `trust.json` e verificação de capacidades de scripts. Nunca foi acoplado à execução de notebooks no VS Code nem no Worker. |
| **`Packages`, `PackageLock`, `PackageApproval`** | [`src/FSharpQueryStudio.Protocol/Packages.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Protocol/Packages.fs)<br>[`src/FSharpQueryStudio.Storage/Packages.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Storage/Packages.fs)<br>[`src/FSharpQueryStudio.Storage/PackageLock.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Storage/PackageLock.fs)<br>[`src/FSharpQueryStudio.Storage/PackageApproval.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Storage/PackageApproval.fs) | ~180 | Política de aprovação prévia de pacotes `#r "nuget:"` e trava determinística de hashes (`packages.json`). As células do VS Code são executadas diretamente no FSI sem checagem de manifest. |
| **`FsProj` (Storage)** | [`src/FSharpQueryStudio.Storage/FsProj.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Storage/FsProj.fs) | ~52 | Parser XML de arquivos `.fsproj` para resolução linear de ordem de compilação. Não é consumido pelo Worker nem pelo VS Code (que opera sobre arquivos de notebook `.fsqnb`). |

### 2.2 Diretórios e Artefatos Fantasmas no Filesystem

- `src/FSharpQueryStudio.Analysis/` contendo apenas pastas vazias `bin/` e `obj/`. O projeto foi substituído por `FSharpQueryStudio.Fsac`, mas o diretório não foi removido do disco.
- `tests/FSharpQueryStudio.Analysis.Tests/` contendo apenas pastas vazias `bin/` e `obj/`.
- Ambos não constam mais em [`FSharpQueryStudio.slnx`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/FSharpQueryStudio.slnx), devendo ser excluídos do disco.

### 2.3 Endpoints Implementados no Worker mas Não Utilizados pelo VS Code

O Worker e o `FsacBridge` implementam com sucesso:
- `analysis/definition` ([`src/FSharpQueryStudio.Worker/Handlers.fs#L301-L320`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Worker/Handlers.fs#L301-L320)): resolução de Go-to-Definition via FSAC.
- `analysis/rename` ([`src/FSharpQueryStudio.Worker/Handlers.fs#L378-L397`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Worker/Handlers.fs#L378-L397)): resolução de referências para Rename via FSAC.

No entanto, o cliente TypeScript [`editors/vscode/src/lang/analysis-client.ts`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/editors/vscode/src/lang/analysis-client.ts) e os provedores [`editors/vscode/src/lang/providers.ts`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/editors/vscode/src/lang/providers.ts) **não registram** `DefinitionProvider` nem `RenameProvider` no VS Code.
- **Opção A (Recomendada)**: Expor ambos na extensão VS Code, pois agregam grande valor à experiência do notebook (Go-to-Definition e Rename de variáveis em F#).
- **Opção B**: Se não houver interesse em suportá-los no notebook, remover os handlers e chamadas no FsacBridge para economizar complexidade.

### 2.4 Dependências Redundantes e Inúteis

1. **`System.Security.Cryptography.Pkcs`**:
   - Declarada como `<PackageReference Include="..." />` e `<PackageReference Update="..." Version="10.0.14" />` em **19 arquivos `.fsproj`** (todos os projetos de src e tests).
   - Nenhuma linha de código F# do repositório importa ou utiliza essa biblioteca (foi adicionada como tentativa de supressão de vulnerabilidade transitiva herdada de dependências antigas).
2. **Ausência de Central Package Management (CPM)**:
   - Todas as versões de pacotes (`FSharp.Core`, `Serilog`, `Microsoft.Data.Sqlite`, `Npgsql`, etc.) são repetidas individualmente em cada arquivo `.fsproj`.

### 2.5 Testes Unitários e de Integração que Apenas Validam Código Morto

Com a remoção dos módulos órfãos, as seguintes 9 suítes de teste tornam-se candidatas à remoção:
1. [`tests/FSharpQueryStudio.Storage.Tests/UiStateTests.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/tests/FSharpQueryStudio.Storage.Tests/UiStateTests.fs)
2. [`tests/FSharpQueryStudio.Storage.Tests/FsProjTests.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/tests/FSharpQueryStudio.Storage.Tests/FsProjTests.fs)
3. [`tests/FSharpQueryStudio.Storage.Tests/TrustTests.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/tests/FSharpQueryStudio.Storage.Tests/TrustTests.fs)
4. [`tests/FSharpQueryStudio.Storage.Tests/PackageLockTests.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/tests/FSharpQueryStudio.Storage.Tests/PackageLockTests.fs)
5. [`tests/FSharpQueryStudio.Storage.Tests/PackagesTests.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/tests/FSharpQueryStudio.Storage.Tests/PackagesTests.fs)
6. [`tests/FSharpQueryStudio.Integration.Tests/PackageApprovalTests.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/tests/FSharpQueryStudio.Integration.Tests/PackageApprovalTests.fs)
7. [`tests/FSharpQueryStudio.Security.Tests/OriginDetectionTests.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/tests/FSharpQueryStudio.Security.Tests/OriginDetectionTests.fs)
8. [`tests/FSharpQueryStudio.Logging.Tests/DiagnosticBundleTests.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/tests/FSharpQueryStudio.Logging.Tests/DiagnosticBundleTests.fs)
9. [`tests/FSharpQueryStudio.Logging.Tests/LogFileReaderTests.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/tests/FSharpQueryStudio.Logging.Tests/LogFileReaderTests.fs)
10. Métodos de teste específicos em [`tests/FSharpQueryStudio.Protocol.Tests/Tests.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/tests/FSharpQueryStudio.Protocol.Tests/Tests.fs) que testam `EditorEnvelope` e `ApprovedPackage`.

---

## 3. Diagnóstico e Propostas de Melhorias Arquiteturais

### 3.1 Mecanismo de Cancelamento e Interrupção de Execução
- **Problema Atual**:
  O cancelamento no Worker ([`src/FSharpQueryStudio.Execution/CancellationSignal.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Execution/CancellationSignal.fs)) grava um arquivo sentinela `.cancel-requested` no diretório de trabalho. No entanto:
  - Nenhuma rotina do FSI ou dos conectores consulta esse arquivo.
  - O método [`Connections.sql`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Connectors/Connections.fs#L79) executa chamadas ADO.NET síncronas bloqueantes sem nenhum `CancellationToken`.
  - Quando o usuário clica em "Interrupt" no VS Code, a extensão aguarda 5 segundos (`CANCEL_GRACE_MS`) e mata o processo do Worker com `proc.kill()`.
  - **Consequência**: A morte forçada do processo Worker destrói todas as sessões FSI ativas da janela do VS Code, obrigando o usuário a reexecutar todas as células anteriores do notebook.
- **Proposta Arquitetural**:
  1. No Worker, manter um `CancellationTokenSource` ativo durante o `beginEvaluate` / `runEvaluate`.
  2. Disponibilizar a verificação desse token e método de abort/cancel no contexto do `Connections`.
  3. No caso de comandos SQL, expor o cancelamento chamando `command.Cancel()` no ADO.NET ou utilizando execução com `CancellationToken`.
  4. Se o comando for cancelado cooperativamente, o worker retorna `execution/cancelled`, preservando o processo e o estado do FSI intactos.

### 3.2 Isolamento de Cursors de Paginação (`currentPaging`)
- **Problema Atual**:
  Em [`src/FSharpQueryStudio.Worker/Handlers.fs#L189`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Worker/Handlers.fs#L189) e [`Handlers.fs#L407`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Worker/Handlers.fs#L407), o cursor de paginação de tabelas é armazenado em uma variável única mutável:
  ```fsharp
  let mutable private currentPaging: (string * PagingCursor) option = None
  ```
  Se o usuário tiver dois notebooks ou duas consultas paginadas e solicitar páginas subsequentes ou executar uma célula após outra, o cursor da primeira consulta é imediatamente sobrescrito e perdido, gerando erro de `results/unknownToken`.
- **Proposta Arquitetural**:
  Substituir a variável única por um `ConcurrentDictionary<string, PagingCursor>` indexado pelo `continuationToken` ou pelo par `(documentUri, token)`, com política de expiração automática ou descarte explícito via `results/discard`.

### 3.3 Gestão de Saída (`Stdout`/`Stderr`) em Sessões Persistentes
- **Problema Atual**:
  Em [`src/FSharpQueryStudio.Execution/Evaluation.fs#L28-L29`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Execution/Evaluation.fs#L28-L29):
  ```fsharp
  { Value = value
    Output = session.Output.ToString()
    ErrorOutput = session.Errors.ToString()
    Diagnostics = diagnostics
    Exception = exn }
  ```
  O `StringWriter` de `session.Output` e `session.Errors` é criado uma única vez por sessão. Em notebooks com `isNotebookCell = true`, o buffer **nunca é limpo**. Ao executar a Célula 1 (`printfn "A"`) e depois a Célula 2 (`printfn "B"`), a saída da Célula 2 conterá `"A\nB\n"`.
- **Proposta Arquitetural**:
  Efetuar o reset dos `StringBuilder` (`session.Output.GetStringBuilder().Clear()`) antes de avaliar cada nova célula ou capturar o delta de caracteres produzidos especificamente durante a execução daquela interação.

### 3.4 Desacoplamento e Localização de Interfaces nos Conectores
- **Problema Atual**:
  A interface [`IQueryableConnector`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Connectors/SqliteConnector.fs#L19-L21) e o tipo [`ConnectorQueryResult`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Connectors/SqliteConnector.fs#L13) estão definidos dentro do arquivo de implementação concreta `SqliteConnector.fs`, apenas porque ele era compilado antes dos outros conectores SQL no `.fsproj`.
- **Proposta Arquitetural**:
  Mover `IQueryableConnector` e `ConnectorQueryResult` para [`src/FSharpQueryStudio.Connectors/IConnector.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Connectors/IConnector.fs). Isso restabelece a separação limpa entre contratos e implementações concretas de banco de dados.

### 3.5 Gerenciamento de Recursos e Prevenção de Socket Exhaustion (`HttpClient`)
- **Problema Atual**:
  Em [`src/FSharpQueryStudio.Connectors/RestConnector.fs#L61-L62`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Connectors/RestConnector.fs#L61-L62):
  ```fsharp
  type RestConnector(baseOptions: RestRequestOptions, ?httpClient: HttpClient) =
      let httpClient = defaultArg httpClient (new HttpClient())
  ```
  O [`WorkspaceConnectionResolver`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Connectors/WorkspaceConnectionResolver.fs#L78) recria uma nova instância de `RestConnector` a cada chamada `Connections.rest`. Sem um `HttpClient` compartilhado e sem descartar o cliente, múltiplas requisições criam e abandonam instâncias de `HttpClient`, ocasionando esgotamento de portas de socket no sistema operacional (conforme documentação oficial da Microsoft).
- **Proposta Arquitetural**:
  Compartilhar um `HttpClient` singleton/estático ou um `SocketsHttpHandler` configurado para todas as instâncias de `RestConnector` geradas pelo resolver.

### 3.6 Descarte Seguro de Leitores e Conexões SQL
- **Problema Atual**:
  Nos três conectores SQL ([`SqliteConnector.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Connectors/SqliteConnector.fs), [`SqlServerConnector.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Connectors/SqlServerConnector.fs) e [`PostgreSqlConnector.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Connectors/PostgreSqlConnector.fs)):
  ```fsharp
  let connection = new SqlConnection(connectionString)
  connection.Open()
  let command = connection.CreateCommand()
  ...
  let reader = command.ExecuteReader()
  let columns = [ ... ]
  let rows = seq {
      use connection = connection
      use command = command
      use reader = reader
      while reader.Read() do ...
  }
  ```
  Se o chamador apenas inspecionar `Columns` ou descartar o resultado sem drenar a sequência `rows`, a conexão e o reader nunca entrarão no bloco `seq { use ... }` e não serão fechados imediatamente.
- **Proposta Arquitetural**:
  Utilizar `command.ExecuteReader(CommandBehavior.CloseConnection)` e implementar `IDisposable` ou wrapper seguro sobre `ConnectorQueryResult` para garantir fechamento de conexão mesmo em cenários de abandono de consumo ou exceções intermediárias.

### 3.7 Padronização de Telemetria e Observabilidade
- **Problema Atual**:
  O método [`Connections.sql`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Connectors/Connections.fs#L84) instrumenta a sequência com `logOnceDrained` registrando duração e contagem de linhas retornadas. No entanto, `Connections.csv` e `Connections.excel` não passam por esse mecanismo, deixando uma lacuna nos logs estruturados do Serilog.
- **Proposta Arquitetural**:
  Uniformizar a instrumentação de telemetria para que `Connections.csv` e `Connections.excel` também emitam eventos estruturados via Serilog com tipo de connector, tempo decorrido e total de linhas processadas.

### 3.8 Otimização de Performance na Projeção de Dicionários (`Projection.fs`)
- **Problema Atual**:
  Em [`src/FSharpQueryStudio.Results/Projection.fs#L85-L188`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Results/Projection.fs#L85-L188), a identificação e extração de valores de tipos dicionário/mapa é realizada através de reflexão dinâmica direta (`gi.GetMethod("ContainsKey")`, `itemProperty.GetValue(...)`) executada para **cada célula individual** de cada linha e coluna. Em tabelas com 500 linhas e 20 colunas, são realizadas até 10.000 chamadas reflexivas por avaliação.
- **Proposta Arquitetural**:
  Cachear os accessors/delegates compilados por tipo ou converter antecipadamente para a interface não genérica `IDictionary` ou `IReadOnlyDictionary<string, obj>` quando suportado, reduzindo drasticamente o overhead de alocação e reflexão.

### 3.9 Adoção de Central Package Management (CPM) em .NET
- **Problema Atual**:
  Versões de pacotes NuGet estão espalhadas em 19 arquivos `.fsproj`, com repetição de versões e flags manuais.
- **Proposta Arquitetural**:
  Criar um arquivo [`Directory.Packages.props`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/Directory.Packages.props) na raiz da solução com `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>`:
  - Centraliza versões de `FSharp.Core`, `Serilog`, `FSharp.Compiler.Service`, `Microsoft.Data.Sqlite`, `ClosedXML`, `Npgsql`, etc.
  - Elimina a poluição de `<PackageReference Update="..." />` em cada projeto.
  - Remove a referência residual ao pacote não utilizado `System.Security.Cryptography.Pkcs`.

---

## 4. Plano de Execução Proposto (Roadmap em Fases)

Para mitigar riscos e garantir rastreabilidade completa (alinhado às regras de SDD e proteção de branch):

### Fase 1: Limpeza de Código Morto e Simplificação do Build (Quick Wins & Higiene)
1. **Excluir diretórios fantasmas**:
   - Deletar fisicamente `src/FSharpQueryStudio.Analysis` e `tests/FSharpQueryStudio.Analysis.Tests`.
2. **Remover módulos e testes órfãos da era Avalonia**:
   - Remover `UiState.fs` (Protocol e Storage), `UiStateTests.fs`.
   - Remover `EditorRequest.fs`, `HostNotification.fs` e `EditorEnvelope` em `Frames.fs`.
   - Remover `LogFileReader.fs`, `DiagnosticBundle.fs` e seus respectivos testes.
   - Remover `OriginDetection.fs` e `OriginDetectionTests.fs`.
   - Remover `FsProj.fs` e `FsProjTests.fs`.
   - Remover subsistema de aprovação de pacotes órfão: `PackageApproval.fs`, `PackageLock.fs`, `Packages.fs` e seus testes.
   - Remover `Trust.fs` (Protocol e Storage) e `TrustTests.fs`.
3. **Implantar Central Package Management (CPM)**:
   - Adicionar `Directory.Packages.props` na raiz e limpar os 19 arquivos `.fsproj`.
   - Remover a dependência de `System.Security.Cryptography.Pkcs`.
4. **Validar compilação e suíte de testes**:
   - Garantir 100% de sucesso nos testes restantes.

### Fase 2: Correções de Robustez e Concorrência (Estabilidade do Produto)
1. **Corrigir acúmulo de saída em células sequenciais**:
   - Ajustar `Evaluation.evaluate` para limpar os buffers de saída antes de cada execução ou calcular delta.
2. **Isolamento de cursores de paginação (`currentPaging`)**:
   - Migrar `currentPaging` para `ConcurrentDictionary<string, PagingCursor>` com limpeza adequada.
3. **Reorganização de interfaces de conectores**:
   - Mover `IQueryableConnector` e `ConnectorQueryResult` para `IConnector.fs`.
4. **Ciclo de vida do `HttpClient` e descarte de DataReaders**:
   - Singleton de `HttpClient` no `RestConnector`.
   - Garantir descarte correto de conexões SQL caso a sequência de linhas não seja drenada.
5. **Telemetria uniforme**:
   - Adicionar medição de tempo e contagem de linhas para `Connections.csv` e `Connections.excel`.

### Fase 3: Ativação de Recursos de Linguagem e Performance (Evolução)
1. **Ativar Go-to-Definition e Rename no VS Code**:
   - Conectar as rotinas existentes de `analysis/definition` e `analysis/rename` no cliente TypeScript e registrar os provedores em `providers.ts`.
2. **Otimizar Projeção de Resultados**:
   - Otimizar acesso a propriedades de dicionários em `Projection.fs` evitando reflexão por célula.

---

## 5. Conclusão

Esta análise demonstra que o núcleo do **F# Query Studio** possui uma arquitetura moderna e de alta qualidade (FSI embeddado em Worker + extensão VS Code limpa com comunicação por frames NDJSON), mas carrega uma bagagem substancial de código legado da versão desktop anterior.

A execução das melhorias propostas trará:
- **Redução de ~1.500 linhas de código desnecessário e 9 arquivos de teste obsoletos**.
- **Maior estabilidade na execução concorrente de notebooks** (eliminação de conflito de cursores de paginação).
- **Prevenção de vazamento de sockets e conexões de banco de dados**.
- **Comportamento correto de saída de console (`printfn`) entre células consecutivas**.
- **Facilidade e velocidade de manutenção** através de Central Package Management.

---

## 6. Tabela de Status de Cada Step

A tabela abaixo centraliza o status de execução de cada etapa de melhoria, indicando o subagente responsável configurado em `.agents/` e os gates de validação associados:

| Step | Item | Fase | Subagente Responsável | Status | Gate de Validação |
|---|---|---|---|---|---|
| **STEP-01** | Exclusão de diretórios vazios/fantasmas (`Analysis`) | Fase 1 | `dead-code-cleaner` | 🟢 Concluído | Diretórios removidos do filesystem sem impacto no git status |
| **STEP-02** | Purga de módulos órfãos de UI/Desktop (`UiState`, `EditorRequest`, etc.) | Fase 1 | `dead-code-cleaner` | 🟢 Concluído | `dotnet build` sem referências faltantes (`TreatWarningsAsErrors=true`) e 296 testes verdes |
| **STEP-03** | Remoção de 9 arquivos de teste unitário/integração órfãos | Fase 1 | `dead-code-cleaner` | 🟢 Concluído | `dotnet test` executando apenas testes de código ativo com 100% pass (283 testes) |
| **STEP-04** | Implantação de Central Package Management (`Directory.Packages.props`) | Fase 1 | `dependency-manager` | 🟢 Concluído | `dotnet restore` e `dotnet build` determinísticos com 283 testes passando |
| **STEP-05** | Remoção da dependência inútil `System.Security.Cryptography.Pkcs` | Fase 1 | `dependency-manager` | 🟢 Concluído | Pacote removido de todos os 19 `.fsproj` com build limpo e 283 testes passando |
| **STEP-06** | Reset de saída de console entre células consecutivas (`Evaluation.fs`) | Fase 2 | `evaluation-output-buffer` | 🟢 Concluído | Novo teste de `EvaluationTests` validando isolamento de `stdout` (284 testes verdes) |
| **STEP-07** | Concorrência de cursores de paginação (`ConcurrentDictionary`) | Fase 2 | `concurrency-paging` | 🟡 Pendente | Testes de paginação com tokens concorrentes sem colisão |
| **STEP-08** | Desacoplamento de `IQueryableConnector` para `IConnector.fs` | Fase 2 | `connectors-refactor` | 🟡 Pendente | Compilação limpa de todos os conectores e testes |
| **STEP-09** | Reuso de `HttpClient` e descarte determinístico de leitores ADO.NET | Fase 2 | `connectors-refactor` | 🟡 Pendente | Testes de integração de banco e HTTP sem leaks de recursos |
| **STEP-10** | Uniformização de telemetria nos métodos `Connections.csv` e `excel` | Fase 2 | `connectors-refactor` | 🟡 Pendente | Eventos de Serilog verificados em testes de conectores |
| **STEP-11** | Otimização de reflexão em dicionários (`Projection.fs`) | Fase 3 | `projection-performance` | 🟡 Pendente | `Results.Tests` passando com benchmark acelerado |
| **STEP-12** | Ativação de Go-to-Definition e Rename na extensão VS Code | Fase 3 | `vscode-lang-features` | 🟡 Pendente | `npm test` e `npm run test:vscode` verdes com os novos provedores |

