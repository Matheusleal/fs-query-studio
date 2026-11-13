# SDD Proposal: Backend Code Cleanup, Modularization & Decoupling

## 1. Intent Block

```text
Intent Block:
Goal:        Modernizar e organizar a arquitetura do backend em F# (.NET 10), eliminando monólitos com acoplamento excessivo, removendo comentários redundantes/históricos de desenvolvimento e unificando duplicações de código sem alterar contratos ou contratos de teste.
Expectation: Todos os módulos de backend passam a ter responsabilidade única com arquivos menores (< 600 linhas), comentários concisos e informativos (focados no PORQUÊ e nas regras de negócio, sem diários de tarefas passadas), mantendo 100% de compatibilidade com todos os testes e fluxos E2E.
Action:      (1) Segmentar `FSharpQueryStudio.Fsac` extraindo `FsacUri.fs` e `FsacParsing.fs` de `FsacBridge.fs`. (2) Segmentar `FSharpQueryStudio.Connectors` extraindo `SqlDialect.fs`, `SqlPushdownPlan.fs` e `RecordReaders.fs` de `SqlPushdownQuery.fs`, e unificando heurísticas duplicadas de parsing de tipos entre `Connections.fs` e `SqlPushdownQuery.fs`. (3) Modularizar operações de workspace e importação de dados no Worker em `WorkspaceOperations.fs`, reduzindo o tamanho de `Handlers.fs`. (4) Eliminar narrativas e comentários históricos desnecessários em todo o backend.
Result:      Backend mais coeso, desacoplado, manutenível e limpo, com tempos de compilação otimizados e zero regressões em testes unitários, de integração ou fluxos E2E (sample_demo).
```

---

## 2. Varredura Detalhada do Backend (Diagnóstico)

Após varredura completa dos projetos em `src/`, foram identificados os seguintes gargalos de organização, legibilidade e acoplamento:

### 2.1. Monólito e Diários Históricos em `FSharpQueryStudio.Fsac` (`FsacBridge.fs`)
- **Tamanho**: Mais de 1.130 linhas em um único arquivo.
- **Acoplamento**:
  1. *Manipulação de URIs*: Lógica de sanitização de caminhos untitled, remoção de fragmentos e canonicalização misturada com o ciclo de vida do cliente LSP.
  2. *Parsing de Protocolo LSP*: Parsing e manipulação de árvores JSON para hover, assinatura, diagnóstico e completion embutidos no mesmo módulo de orquestração assíncrona.
  3. *Comentários Redundantes*: Centenas de linhas de comentários narrativos em formato de diário de desenvolvimento (referenciando "Task 12", "Task 4's recovery path", "Finding 1 above fixed", "confirmed against real fsautocomplete 0.83.0 during this task"). Esses comentários aumentam o ruído cognitivo e poluem a leitura do código.

### 2.2. Monólito e Duplicação Tripla em `FSharpQueryStudio.Connectors`
- **`SqlPushdownQuery.fs` (> 2.290 linhas)**:
  - Mistura a definição de dialetos (`SqlDialect`, quoting e palavras reservadas), mapeamento de `IDataRecord` compilado (`RecordReaders`, `CompiledRecordMapper`), planos de consulta SQL (`OrderClause`, `JoinPlan`, `PushdownQueryPlan`), tradutor de árvores de expressão LINQ (> 1.150 linhas) e a implementação de `IQueryable`/`IQueryProvider`.
- **`Connections.fs` (> 1.260 linhas)**:
  - Contém duplicação de lógica de parsing primitivo (`isSentinelString`, `tryParseNormalizedDecimal`, `tryParseNormalizedDouble`, `convertValue`), repetindo heurísticas já existentes em `SqlPushdownQuery.fs` e `TabularDataMapping.fs`.
  - Mistura configuração de engine SQLite de staging, registro estático, mapeador reflexivo de registros F#, classes tipadas de conexão (`DatabaseConnection`, `CsvConnection`, etc.) e orquestração de sincronização em lote.

### 2.3. God-Module em `FSharpQueryStudio.Worker` (`Handlers.fs`)
- **Tamanho**: Mais de 1.090 linhas.
- **Acoplamento**:
  - `Handlers.fs` atua como roteador de frames JSON e simultaneamente implementa lógicas densas de sistema de arquivos e criação de projetos (`handleAutoImportDataSources` com ~200 linhas, `handleSampleCreate` com ~150 linhas, `handleInitEmptyProject`).
  - Dificulta a manutenção da camada de protocolo RPC do Worker.

---

## 3. Arquitetura Alvo & Estratégia de Segmentação

### Módulo 1: `FSharpQueryStudio.Fsac`
1. **`FsacUri.fs`**:
   - `canonicalizeUri`: normalização de URIs para comparação determinística.
   - `untitledUriRoot`: diretório raiz temporário por processo.
   - `fsacUriFor`: mapeamento determinístico de URIs não-arquivo para `file://` seguro com extensão `.fsx`.
2. **`FsacParsing.fs`**:
   - `mapSeverity`, `parseDiagnostics`.
   - `stripSymbolKind`, `parseHoverMarkdown`.
   - `parseFirstLocation`, `splitTopLevelArrows`.
   - `simplifyTypeName`, `extractTypeFromDetail`, `parseItem`, `parseCompletionItems`.
3. **`FsacBridge.fs`**:
   - Concentrado na orquestração: fila de diagnósticos concorrentes, sincronização atômica de documentos, chamadas assíncronas ao `FsacProcess` com timeout.
   - Exposição das funções públicas com as exatas assinaturas existentes.
   - Comentários enxutos em XML docstring padrão.

### Módulo 2: `FSharpQueryStudio.Connectors`
1. **`SqlDialect.fs`**:
   - Enumeração `SqlDialect`, palavras reservadas e `quoteIdentifier`.
2. **`SqlPushdownPlan.fs`**:
   - `OrderDirection`, `OrderClause`, `JoinPlan`, `SqlPushdownFormatting`, `PushdownQueryPlan`.
3. **`RecordReaders.fs`**:
   - Métodos estáticos de alta performance para extração direta de `IDataRecord` sem boxing (`RecordReaders`).
   - Mapeador compilado via árvores de expressão (`CompiledRecordMapper`).
4. **`SqlPushdownQuery.fs`**:
   - Focado exclusivamente na tradução da árvore de expressões LINQ (`ExpressionPushdownTranslator`) e provedor de consulta `IQueryable` (`SqlPushdownQueryProvider`, `SqlPushdownQueryable`).

### Módulo 3: `FSharpQueryStudio.Worker`
1. **`WorkspaceOperations.fs`**:
   - Isolamento das operações de criação do workspace sample (`createSampleWorkspace`), auto-import de fontes (`autoImportDataSources`) e inicialização vazia (`initEmptyProject`).
2. **`Handlers.fs`**:
   - Roteador limpo e conciso de comandos RPC do Worker, delegando as operações de workspace.

