# Proposta Técnica: Melhorias de Qualidade de Código & Performance

> **Projeto:** F# Query Studio (`fs-query-studio`)  
> **Status:** Planejamento Aprovado / Arquivado para Execução Futura  
> **Escopo:** Backend (.NET 10 / F#), Extensão VS Code (TypeScript), Conectores de Dados & Pushdown SQL, Execução FSI/FSAC, Empacotamento VSIX  

---

## 1. Sumário Executivo

O **F# Query Studio** é um ambiente para análise e execução de scripts de consulta em F# integrado ao VS Code via notebooks `.fsqnb`. A arquitetura baseia-se em um worker em .NET 10 (`src/FSharpQueryStudio.Worker`) comunicando-se via NDJSON sobre `stdio` com a extensão VS Code (`editors/vscode`).

Este documento reúne e detalha o planejamento de melhorias em **Performance**, **Qualidade de Código**, **Robustez de Recursos** e **Otimização de Pacote**, desenhado para orientar implementações futuras com segurança e rastreabilidade técnica.

---

## 2. Diagnóstico Detalhado por Área

### 2.1 Backend (.NET 10 / F#) — Performance & Otimização de I/O

#### P1. Eliminação da Dupla Execução de Queries em `SqliteConnector.ExecuteQuery`
- **Arquivo / Linhas:** [`src/FSharpQueryStudio.Connectors.Sqlite/SqliteConnector.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Connectors.Sqlite/SqliteConnector.fs#L37-L70)
- **Diagnóstico:** Em `ExecuteQuery`, o conector instancia uma conexão e leitor inicial (`initialCmd.ExecuteReader()`) exclusivamente para ler a lista de nomes das colunas e o `FieldCount`. Logo em seguida, fecha e descarta a conexão e o reader. Na sequência, o bloco lazy `Seq.delay` reabre outra conexão e **executa a query inteira uma segunda vez** para fornecer o stream de linhas. Em consultas com joins ou varredura de tabelas grandes, o tempo de execução e o I/O de disco são dobrados.
- **Solução Proposta:** Adaptar o padrão `firstRun` já utilizado com sucesso em [`PostgreSqlConnector.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Connectors.PostgreSql/PostgreSqlConnector.fs#L42-L55), reaproveitando o leitor e conexão abertos na primeira iteração do `seq`, evitando executar duas vezes o comando SQL.

#### P2. Otimização de Reflexão no Mapeamento de Linhas LINQ (Loop Quente)
- **Arquivo / Linhas:** [`src/FSharpQueryStudio.Connectors/SqlPushdownQuery.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Connectors/SqlPushdownQuery.fs#L1361-L1374)
- **Diagnóstico:** No fallback não-streaming de execução de consultas:
  ```fsharp
  for row in res.Rows do
      let targetType = typeof<'T>
      if FSharpType.IsRecord(targetType) then
          let fields = FSharpType.GetRecordFields(targetType)
          let ctor = FSharpValue.PreComputeRecordConstructor(targetType)
          ...
  ```
  `FSharpType.GetRecordFields` e `PreComputeRecordConstructor` são chamados a cada registro dentro do loop. Para uma consulta com 50.000 linhas, são 50.000 chamadas redundantes de reflexão.
- **Solução Proposta:** Mover a extração de campos e o pré-cálculo do construtor para fora do loop `seq { ... }`, garantindo que os metadados sejam resolvidos apenas uma única vez por consulta.

#### P3. Redução de `Lambda.Compile()` Repetitivo no Tradutor de Expressões LINQ
- **Arquivo / Linhas:** [`src/FSharpQueryStudio.Connectors/SqlPushdownQuery.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Connectors/SqlPushdownQuery.fs#L100-L106), [L385-L388](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Connectors/SqlPushdownQuery.fs#L385-L388), [L398-L400](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Connectors/SqlPushdownQuery.fs#L398-L400)
- **Diagnóstico:** Constantes capturadas em closures (ex: `where u.Age >= minAge`) disparam compilação de código dinâmico via `Expression.Lambda(expr).Compile().DynamicInvoke()` usando `Reflection.Emit` sem cache.
- **Solução Proposta:** Avaliar acessos simples de membros sobre constantes diretamente (leitura de propriedade/campo via reflexão ou delegates cacheados), eliminando a compilação dinâmica IL em tempo de execução para valores literais e parâmetros capturados.

#### P4. Inserção em Lote (*Batching*) e Streaming no `FileSqliteEngine`
- **Arquivo / Linhas:** [`src/FSharpQueryStudio.Connectors/FileSqliteEngine.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Connectors/FileSqliteEngine.fs#L324), [L377-L427](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Connectors/FileSqliteEngine.fs#L377-L427)
- **Diagnóstico:**  
  1. `data.Rows |> Seq.toArray` materializa todas as linhas em memória gerenciada antes de inserir, dobrando o consumo de RAM em arquivos tabulares massivos.
  2. Cada registro é inserido por um comando individual `insertCmd.ExecuteNonQuery()`, gerando centenas de milhares de chamadas P/Invoke para a biblioteca nativa do SQLite.
- **Solução Proposta:** Processar os registros via streaming contínuo e agrupar comandos `INSERT` em blocos de 100 a 500 linhas (`INSERT INTO table VALUES (...), (...), ...`), reduzindo o tempo de estagiamento em disco em até 8x.

#### P5. Redução de Atrasos Artificiais no Bridge LSP (FSAC)
- **Arquivo / Linhas:** [`src/FSharpQueryStudio.Fsac/FsacBridge.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Fsac/FsacBridge.fs#L70), [L157](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Fsac/FsacBridge.fs#L157), [L322](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Fsac/FsacBridge.fs#L322)
- **Diagnóstico:** Requisições de `complete` (autocompletar a cada tecla) chamam `awaitDocumentAnalyzed`, que aguarda o término da verificação em segundo plano e aplica `Task.Delay(100)` + `Task.Delay(50)`.
- **Solução Proposta:** No autocompletar, enviar o evento de sincronização `textDocument/didChange` e requisitar imediatamente `textDocument/completion` sem esperar pela análise completa nem aplicar delays artificiais, tornando as sugestões quase instantâneas.

---

### 2.2 Backend (.NET 10 / F#) — Qualidade de Código & Gerenciamento de Recursos

#### Q1. Descarte Correto de Instâncias de `JsonDocument`
- **Arquivos / Linhas:**  
  - [`src/FSharpQueryStudio.Fsac/LspClient.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Fsac/LspClient.fs#L44)
  - [`src/FSharpQueryStudio.Fsac/LspClient.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Fsac/LspClient.fs#L30)
  - [`src/FSharpQueryStudio.Worker/Handlers.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Worker/Handlers.fs#L761)
- **Diagnóstico:** `JsonDocument.Parse` aloca memória no `ArrayPool` do runtime. As chamadas acima não utilizam `use` nem invocam `.Dispose()`, causando retenção e fragmentação de memória. Além disso, em `Handlers.fs#L761`, `c.Configuration` já é um `JsonElement`, tornando `c.Configuration.GetRawText()` e o re-parse redundantes.
- **Solução Proposta:** Usar `use doc = JsonDocument.Parse(...)` em todas as rotas e ler diretamente as propriedades de `c.Configuration` sem nova serialização.

#### Q2. Fechamento Seguro de Conexões em Leituras Parciais (`PostgreSql` & `SqlServer`)
- **Arquivos / Linhas:**  
  - [`src/FSharpQueryStudio.Connectors.PostgreSql/PostgreSqlConnector.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Connectors.PostgreSql/PostgreSqlConnector.fs#L35-L55)
  - [`src/FSharpQueryStudio.Connectors.SqlServer/SqlServerConnector.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Connectors.SqlServer/SqlServerConnector.fs#L35-L55)
- **Diagnóstico:** Em `ExecuteQuery`, a conexão inicial e o reader são criados na chamada e preservados no `firstRun`. Se o consumidor ler apenas `result.Columns` e nunca iterar `result.Rows`, a conexão permanece aberta indefinidamente até o finalizador do GC.
- **Solução Proposta:** Implementar `IDisposable` em `ConnectorQueryResult` ou vincular o ciclo de vida da conexão inicial a um mecanismo de descarte explícito se a sequência não for consumida.

#### Q3. Remoção de `GC.Collect()` Síncrono Forçado no Caminho Quente
- **Arquivo / Linhas:** [`src/FSharpQueryStudio.Worker/Handlers.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Worker/Handlers.fs#L381-L383), [L416-L418](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Worker/Handlers.fs#L416-L418)
- **Diagnóstico:** No encerramento de cada avaliação de célula de notebook:
  ```fsharp
  GC.Collect()
  GC.WaitForPendingFinalizers()
  GC.Collect()
  ```
  Isso gera pausas completas *Stop-the-World* e desperdício de CPU.
- **Solução Proposta:** Remover a coleta forçada no final de `runEvaluate`. A chamada existente `SqliteConnection.ClearAllPools()` já libera adequadamente os descritores de arquivo do SQLite sem necessitar de coletas síncronas completas.

#### Q4. Centralização de Palavras Reservadas e Escape de Identificadores
- **Arquivos:** [`src/FSharpQueryStudio.Connectors/Connections.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Connectors/Connections.fs#L749-L766), [`src/FSharpQueryStudio.Connectors.Abstractions/Descriptor.fs`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Connectors.Abstractions/Descriptor.fs#L261-L278), `CodeGenerationHelpers.fs`
- **Diagnóstico:** A lista com mais de 80 palavras reservadas do F# e a função `escapeIdent` estão duplicadas verbatim em vários projetos.
- **Solução Proposta:** Unificar a lista de palavras-chave e a lógica de escape em um único local canônico em `FSharpQueryStudio.Connectors.Abstractions`.

---

### 2.3 Frontend & Extensão VS Code (TypeScript)

#### F1. Descoberta de Schema Sob Demanda (*Lazy Loading*) no Tree View
- **Arquivo / Linhas:** [`editors/vscode/src/connections/tree.ts`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/editors/vscode/src/connections/tree.ts#L111-L115)
- **Diagnóstico:** Ao listar conexões (`listConnections()`), o código dispara em paralelo `this.fetchSchema(def)` para **todas** as conexões cadastradas, gerando sobrecarga de rede e I/O assim que o VS Code é aberto.
- **Solução Proposta:** Restringir a busca de schema exclusivamente para quando o usuário expandir a conexão (`getChildren(node)` com `node.t === "connection"`).

#### F2. Reciclagem de Nós DOM no Grid Virtualizado do Renderer
- **Arquivo / Linhas:** [`editors/vscode/src/renderer/index.ts`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/editors/vscode/src/renderer/index.ts#L190-L224)
- **Diagnóstico:** Em cada evento de rolagem, `renderWindow` zera o conteúdo com `rowsHost.textContent = ""` e aloca novos elementos `div` via `document.createElement`.
- **Solução Proposta:** Manter um pool fixo de nós de linha (~30 elementos) e atualizar apenas o conteúdo textual, classes e transformações CSS durante o scroll, eliminando a pressão de GC na webview.

#### F3. Modularização de `extension.ts` (~1.200 linhas)
- **Arquivo:** [`editors/vscode/src/extension.ts`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/editors/vscode/src/extension.ts)
- **Diagnóstico:** O arquivo concentra inicialização, status bar, timers, watcher de arquivos, debounce, migrações de workspace e dezenas de comandos.
- **Solução Proposta:** Separar em submódulos dedicados:
  - `src/ui/status-bar.ts`: Gerenciamento do StatusBarItem e animações.
  - `src/workspace/data-sources-watcher.ts`: File watcher e lógica de auto-import.
  - `src/commands/workspace-commands.ts`: Comandos de upgrade, criação de amostras e documentação.

#### F4. Limpeza de Timer em `WorkerClient.cancel`
- **Arquivo / Linhas:** [`editors/vscode/src/worker/client.ts`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/editors/vscode/src/worker/client.ts#L109-L113)
- **Diagnóstico:** Se a requisição for cancelada com sucesso antes do término da tolerância (`gracePeriodMs`), o timer criado pelo `setTimeout` continua ativo na fila de eventos do Node.js.
- **Solução Proposta:** Salvar o identificador do timer e executar `clearTimeout(timerId)` no bloco `finally`.

---

### 2.4 Empacotamento & Distribuição (VSIX)

#### D1. Redução do Tamanho do VSIX de ~85 MB para ~45-50 MB
- **Diagnóstico:**
  1. A pasta [`dist/worker/`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/editors/vscode/dist/worker) contém montagens satélites de localização para 14 idiomas geradas automaticamente por pacotes como `Microsoft.Data.SqlClient`.
  2. A pasta `dist/worker/FsacTool/` consome **65 MB**, duplicando bibliotecas volumosas (`FSharp.Compiler.Service.dll` de 20 MB e `FSharp.Core.dll` de 2,5 MB) que já existem na raiz do worker.
- **Solução Proposta:**
  1. Adicionar `<SatelliteResourceLanguages>en</SatelliteResourceLanguages>` em [`FSharpQueryStudio.Worker.fsproj`](file:///d:/Projects/github/mine/Matheusleal/fs-query-studio/src/FSharpQueryStudio.Worker/FSharpQueryStudio.Worker.fsproj).
  2. No script de build, deduplicar assemblies do `FsacTool` ou configurar resolução compartilhada.

---

## 3. Matriz de Priorização e Roteiro de Implementação

```text
+-----------------------------------------------------------------------------------+
| Prioridade 0 (Imediata / Quick Wins de Alto Impacto)                              |
| - P1: Eliminar dupla execução de queries em SqliteConnector                       |
| - P2: Mover reflexão GetRecordFields/ctor para fora do loop em LINQ               |
| - Q3: Remover chamadas síncronas de GC.Collect() ao final de células              |
| - Q1: Corrigir descarte de JsonDocument e eliminar re-parse em Handlers.fs        |
+-----------------------------------------------------------------------------------+
                                      │
                                      ▼
+-----------------------------------------------------------------------------------+
| Prioridade 1 (Alta Relevância / Otimização de Distribuição e Frontend)            |
| - D1: Reduzir tamanho do VSIX (suprimir satélites de idiomas e deduplicar FSAC)   |
| - F1: Descoberta de schema no TreeView estritamente sob demanda (lazy)            |
| - F4: Limpeza de timer em WorkerClient.cancel                                     |
| - Q4: Unificar lista de palavras reservadas e escapeIdent em Abstractions         |
+-----------------------------------------------------------------------------------+
                                      │
                                      ▼
+-----------------------------------------------------------------------------------+
| Prioridade 2 (Médio Prazo / Refatoração & Arquitetura)                            |
| - P4: Inserções em lote (batching) e streaming em FileSqliteEngine                |
| - P5: Remover atrasos artificiais (Task.Delay) no bridge do autocompletar         |
| - F2: Reciclagem de elementos DOM no grid do renderer                             |
| - F3: Modularização e decomposição de extension.ts (~1.200 linhas)                |
| - Q2: Descarte seguro de conexões em PostgreSql/SqlServer em leituras parciais    |
+-----------------------------------------------------------------------------------+
                                      │
                                      ▼
+-----------------------------------------------------------------------------------+
| Prioridade 3 (Evolutiva / Nova Funcionalidade)                                    |
| - Suporte a streaming de saída em tempo real (output streaming) para células FSI  |
+-----------------------------------------------------------------------------------+
```

---

## 4. Plano de Validação & Critérios de Aceite

Para garantir a estabilidade do projeto durante a futura execução deste planejamento, o seguinte protocolo obrigatório deverá ser observado:

1. **Branch Protection & Isolamento**:
   - Toda implementação deve ocorrer em branch de feature/fix (ex: `feature/performance-optimizations`), nunca diretamente na `main`.
2. **Validação Rigorosa de Testes**:
   - `dotnet test FSharpQueryStudio.slnx -c Release` (100% de sucesso).
   - `npm test` no VS Code (100% de sucesso no Vitest).
   - Validação dos notebooks de demonstração:
     - `dotnet test tests/FSharpQueryStudio.Worker.Tests --filter "FullyQualifiedName~sample"`
     - `npx vitest run src/worker/sample-notebook.integration.test.ts`
3. **Validação Multiplataforma**:
   - Execução dos testes no container Linux (`docker run ...` ou `./scripts/run-all-tests.ps1`).
4. **Métricas de Performance**:
   - Executar os benchmarks (`dotnet run --project tests/FSharpQueryStudio.Benchmarks -c Release`) e verificar se a latência de consultas SQLite e o uso de CPU diminuíram em conformidade com o relatório.

