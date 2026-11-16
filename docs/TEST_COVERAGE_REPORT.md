# Relatório Completo de Cobertura de Testes e Lacunas de Validação

**Data do Diagnóstico**: Novembro de 2026  
**Repositório**: `fs-query-studio`  
**Escopo**: Backend .NET (F# Core, Connectors, Worker, FSAC, Execution, Storage, Security) e Extensão VS Code (TypeScript, UI, Renderer, Worker Client)  
**Metodologia de Medição**: Execução real de testes com coleta instrumentada via `coverlet.collector` (backend) e `vitest` / `@vscode/test-cli` (frontend)

---

## 1. Sumário Executivo

O projeto `fs-query-studio` conta atualmente com uma suíte sólida de testes automatizados:
- **Backend (.NET 10 / F#)**: **10 projetos de teste**, totalizando **391 testes unitários e de integração** (sem contar os testes de container Docker), atingindo **77.24% de cobertura de linhas** global (12.516 linhas cobertas de 16.203 linhas totais).
- **Frontend / Extensão VS Code (TypeScript)**: **36 suítes de testes unitários** no Vitest com **355 testes passando (100%)** e **5 suítes de testes de integração** via VS Code Test CLI (`test/integration`).
- **Testes E2E de Cadernos**: Suíte integrada validando cadernos em inglês e português (`sample_demo.fsqnb` e `sample_demo_pt.fsqnb`) cobrindo SQLite, CSV, Excel, XML, JSON e REST.

Apesar da alta taxa de aprovação, a análise detalhada de cobertura linha a linha e de branches revelou **lacunas expressivas de validação**, especialmente em:
1. **Conectores SQL Externos (Oracle, MariaDB, MySQL, PostgreSQL, SQL Server)**: Cobertura inferior a 32% (código de conector ~7%), devido à dependência exclusiva de containers Docker para testes de execução.
2. **Leitores e Conversores de Dados (`RecordReaders.fs` e `Connections.fs`)**: Quase 50% das rotinas de normalização numérica, sentinelas, tipos nulos e conversões de reflexão não possuem asserções de teste unitário.
3. **Tradutor de Pushdown SQL (`SqlPushdownQuery.fs`)**: Mais de 1.450 linhas não cobertas, incluindo operações customizadas (`rightOuterJoin`, `denseRank`, `ntile`, `firstValue`, `lastValue`, `lateral`) e funções escalares de projeção.
4. **Comandos e Módulos de Conexão no VS Code (`commands.ts`, `connector-shared.ts` e `connections/modules/*.ts`)**: 15 arquivos TypeScript com mais de 1.000 linhas de código sem suíte direta de testes unitários.

```text
┌────────────────────────────────────────────────────────────────────────┐
│                        PANORAMA DE COBERTURA                           │
├────────────────────────────┬─────────────┬─────────────┬───────────────┤
│ Camada                     │ Cobertas    │ Totais      │ % Cobertura   │
├────────────────────────────┼─────────────┼─────────────┼───────────────┤
│ Backend Global (.NET)      │ 12.516 lin  │ 16.203 lin  │ 77.24 %       │
│ Conectores Arquivos/Embed  │  1.087 lin  │  1.303 lin  │ 83.42 %       │
│ Conectores SQL Externos    │    200 lin  │    740 lin  │ 27.03 % ⚠️    │
│ FSQL Pushdown Engine       │  1.758 lin  │  3.240 lin  │ 54.26 % ⚠️    │
│ Protocolo & Segurança      │     65 lin  │    112 lin  │ 58.04 %       │
│ Worker & Handlers          │    979 lin  │  1.204 lin  │ 81.31 %       │
│ FSAC & Language Server     │    687 lin  │    837 lin  │ 82.08 %       │
│ Armazenamento & Upgrader   │    198 lin  │    265 lin  │ 74.72 %       │
│ Extensão VS Code (TS)      │ 29 arq test │ 44 arq tot  │ 65.91 % (arq) │
└────────────────────────────┴─────────────┴─────────────┴───────────────┘
```

---

## 2. Cobertura Detalhada por Módulo do Backend (.NET)

A tabela abaixo lista a cobertura real obtida através do coletor de cobertura instrumentado:

| Projeto / Pacote | Linhas Cobertas | Linhas Totais | % Cobertura | Status |
|---|---|---|---|---|
| **FSharpQueryStudio.Connectors.Sqlite** | 122 | 127 | **96.06%** | Excelente |
| **FSharpQueryStudio.Execution** | 167 | 181 | **92.27%** | Excelente |
| **FSharpQueryStudio.Connectors.Excel** | 182 | 198 | **91.92%** | Excelente |
| **FSharpQueryStudio.Security** | 11 | 12 | **91.67%** | Excelente |
| **FSharpQueryStudio.Connectors.Rest** | 98 | 108 | **90.74%** | Excelente |
| **FSharpQueryStudio.Logging** | 97 | 110 | **88.18%** | Bom |
| **FSharpQueryStudio.Results** | 514 | 605 | **84.96%** | Bom |
| **FSharpQueryStudio.Fsac** | 687 | 837 | **82.08%** | Bom |
| **FSharpQueryStudio.Worker** | 979 | 1.204 | **81.31%** | Bom |
| **FSharpQueryStudio.Connectors.Csv** | 288 | 362 | **79.56%** | Bom |
| **FSharpQueryStudio.Connectors.Json** | 204 | 258 | **79.07%** | Bom |
| **FSharpQueryStudio.Connectors.Xml** | 197 | 250 | **78.80%** | Bom |
| **FSharpQueryStudio.Connectors** (Core) | 8.241 | 10.482 | **78.62%** | Bom |
| **FSharpQueryStudio.Storage** | 198 | 265 | **74.72%** | Regular |
| **FSharpQueryStudio.Connectors.Abstractions** | 278 | 373 | **74.53%** | Regular |
| **FSharpQueryStudio.Protocol** | 54 | 100 | **54.00%** | Atenção |
| **FSharpQueryStudio.Connectors.SqlServer** | 46 | 146 | **31.51%** | Crítico |
| **FSharpQueryStudio.Connectors.PostgreSql** | 39 | 141 | **27.66%** | Crítico |
| **FSharpQueryStudio.Connectors.MariaDb** | 38 | 145 | **26.21%** | Crítico |
| **FSharpQueryStudio.Connectors.MySql** | 38 | 145 | **26.21%** | Crítico |
| **FSharpQueryStudio.Connectors.Oracle** | 38 | 154 | **24.68%** | Crítico |

---

## 3. O que Falta Ser Testado (Gaps Críticos por Componente)

### 3.1. Conectores SQL Externos (Oracle, MariaDB, MySQL, PostgreSQL, SQL Server)

- **Diagnóstico**: O código de `OracleConnector.fs`, `MySqlConnector.fs`, `MariaDbConnector.fs`, `PostgreSqlConnector.fs` e `SqlServerConnector.fs` possui apenas entre **6.6% e 8.5% de cobertura**.
- **Causa Raiz**: Seus métodos funcionais (`ExecuteQuery`, `TestConnectionAsync`, `DiscoverSchemaAsync`, streaming, paginação e mapeamento de tipos ADO.NET específicos) só são executados nos testes de integração com Testcontainers Docker (`Category=RequiresDocker`). Quando a suíte roda em ambiente sem Docker ou durante testes rápidos locais, esses arquivos rodam quase a zero.
- **O que falta testar**:
  1. **Falhas e Timeouts de Conexão**: Resposta quando o servidor está inacessível, porta fechada ou conexão derrubada no meio do streaming.
  2. **Strings de Conexão Inválidas / Malformadas**: Tratamento amigável e mensagens de erro quando a string de conexão é rejeitada pelo driver ADO.NET.
  3. **Mapeamento de Tipos Proprietários**:
     - *Oracle*: `RAW`, `VARCHAR2`, `NUMBER` com precisão arbitrária, `DATE` vs `TIMESTAMP`, `CLOB`, `BLOB`.
     - *PostgreSQL*: `UUID`, `JSONB`, `NUMERIC`, `TIMESTAMPTZ`, arrays nativos (`text[]`, `int[]`).
     - *SQL Server*: `uniqueidentifier`, `datetimeoffset`, `money`, `hierarchyid`.
     - *MySQL/MariaDB*: `TINYINT(1)` (booleano), `DATETIME` zero (`0000-00-00`), `BIGINT UNSIGNED`.
  4. **Descoberta de Esquema em Ambientes Restritos**: Comportamento de `DiscoverSchemaAsync` quando o usuário não tem permissão em tabelas de catálogo/metadados.

---

### 3.2. Leitores de Registros e Normalização Numérica (`RecordReaders.fs`)

- **Diagnóstico**: O arquivo tem **48.29% de cobertura**, deixando **257 linhas sem teste**.
- **Trechos não cobertos**:
  - `RecordReaders.TryParseNormalizedDecimal` e `TryParseNormalizedDouble` (linhas 38-89):
    - Casos com separador misto (vírgula e ponto na mesma string, ex: `1.234,56` e `1,234.56`).
    - Números com formatação cultural corrente vs invariante.
    - Valores infinitos (`Double.PositiveInfinity`, `Double.NegativeInfinity`) e `NaN`.
  - Conversores de Estreitamento/Alargamento (linhas 91-160):
    - `ReadInt32`: conversão a partir de `single`, `bool`, `decimal` com arredondamento e *clamping* em `Int32.MinValue` e `Int32.MaxValue`.
    - `ReadInt64`: conversão a partir de `decimal`, `double`, `single` e `string` com sentinelas.
    - `ReadInt16`, `ReadByte`: conversões seguras contra estouro (*overflow*).
    - `ReadGuid`: parsing de GUID a partir de strings com formatos especiais ou arrays de bytes.
    - `ReadDateOnly` e `ReadTimeOnly`: formatos ISO 8601 vs formatos regionais com milissegundos.
  - Sentinelas e Nulos (linhas 108-118, 140-150):
    - Textos como `"-"`, `""`, `"NULL"`, `"N/A"`, `"NaN"`, `"#N/A"` sendo parseados para inteiros, decimais e datas.

---

### 3.3. Tradução e Pushdown de Consultas (`SqlPushdownQuery.fs`)

- **Diagnóstico**: O núcleo do `fsql` tem **51.89% de cobertura**, com **1.451 linhas sem nenhum teste**.
- **Operações Customizadas Não Testadas**:
  - `rightOuterJoin` (linhas 3517-3520 e 3525-3528): **Zero testes** de geração de SQL e execução.
  - `lateral` (linhas 3400-3440): **Zero testes**.
  - `denseRank` e `ntile` (linhas 3543-3548): **Zero testes**.
  - `firstValue` e `lastValue` (linhas 3555-3565): **Zero testes**.
- **Funções Escalares e Projeções Não Cobertas**:
  - Funções de string em projeção: `ToUpper()`, `ToLower()`, `Trim()`, `Substring()` traduzidas para dialetos específicos (linhas 1554-1565).
  - Funções matemáticas e de agregação combinadas: `COUNT(DISTINCT *)`, `SUM(DISTINCT ...)`, `AVG()` em subqueries (linhas 1567-1650).
  - Concatenação de colunas em SQLite (`||`), SQL Server (`+` ou `CONCAT`) e MySQL (`CONCAT`).
- **Casos de CTEs e Subconsultas**:
  - `withRecursiveCte` com múltiplos termos ou condições de parada complexas.
  - Subconsultas com correlação múltipla em `whereNotExists` e `whereExists`.
  - Agrupamentos com `having` combinados com filtros `where` e ordenação `sortByDescending`.

---

### 3.4. Motor de Conexões e Mapeamento de Registros (`Connections.fs`)

- **Diagnóstico**: **62.73% de cobertura**, com **372 linhas sem teste**.
- **O que falta testar**:
  - `convertValue` (linhas 86-220):
    - Mapeamento dinâmico para tipos `FSharpOption<'T>` aninhados (`int option`, `decimal option`, `DateTime option`).
    - Parsing de booleanos a partir de strings (`"true"`, `"false"`, `"yes"`, `"no"`, `"1"`, `"0"`).
    - Mapeamento de registros F# quando o banco retorna colunas com nomes sanitizados (`user id` -> `UserId`).
    - Registros com construtores privados ou tipos de união discriminada (*discriminated unions*).
  - Pivotagem (`Connections.sql.pivot` e `Seq.pivot`, linhas 1033-1049 e 1406-1425):
    - Tabelas com valores nulos nas colunas de pivô.
    - Nomes de valores contendo caracteres especiais (aspas, barras, espaços).
    - Agregações alternativas (`COUNT`, `AVG`, `MAX`, `MIN`) no pivô.
  - Sincronização de Fontes de Dados (`syncDataSourcesAsyncWithProgress`):
    - Recuperação de erro quando um arquivo do workspace é deletado ou bloqueado por outro processo durante a sincronização em segundo plano.
    - Tratamento de arquivos Excel com planilhas vazias ou corrompidas.
    - Arquivos XML sem nós tabulares válidos.

---

### 3.5. Servidor de Linguagem e Parser FSAC (`FsacParsing.fs`)

- **Diagnóstico**: **62.24% de cobertura**, com **74 linhas sem teste**.
- **O que falta testar**:
  - `parseFirstLocation` (linhas 110-126): Formato LSP 3.16+ `LocationLink` com `targetUri` e `targetRange` vs formato clássico `Location` com `uri` e `range`.
  - `parseItem` (linhas 188-196): Completion items com `label` como objeto estruturado (`CompletionItemLabelDetails`).
  - `simplifyTypeName` (linhas 155-170): Tipos primitivos F# não mapeados em testes (`Single`, `Byte`, `Guid`, `Object`).

---

### 3.6. Armazenamento e Migrações (`ProjectUpgrader.fs` e `Workspace.fs`)

- **Diagnóstico**: **72.08% de cobertura** em `ProjectUpgrader.fs` e **78.38%** em `Workspace.fs`.
- **O que falta testar**:
  - Migração de projetos legados com conexões duplicadas ou IDs conflitantes entre `workspace.json` na raiz e `.fsqs/workspace.json`.
  - Atualização do projeto global em `AppData`/`~/.fsqs/workspace.json`.
  - Tratamento de arquivos `workspace.json` corrompidos com JSON sintaticamente inválido (deve emitir erro claro sem travar a extensão).
  - Preservação de arquivos gerados em `.fsqs/generated/` durante múltiplos upgrades sucessivos.

---

### 3.7. Worker: Handlers e Canais de Notificação Reversa (`Handlers.fs`)

- **Diagnóstico**: **82.91% de cobertura**, com **122 linhas sem teste**.
- **O que falta testar**:
  - Canais de progresso reverso (`configureHostChannel` nas linhas 112-136): Envio de notificações `entityMapping/status` e `staging/progress` do worker para o cliente host (VS Code).
  - Cancelamento explícito (`handleCancel`): Interrupção de avaliações lentas do FSI ou queries SQL volumosas sem matar o processo do worker.
  - Expiração de Cursores de Paginação (`handleResultsPage`): Requisição de páginas com cursor expirado ou id inexistente (garantindo retorno estruturado de erro).
  - Limpeza de recursos (`handleResultsDiscard`): Liberação de memória de cursores descartados.

---

### 3.8. Extensão VS Code (TypeScript)

- **Diagnóstico**: 15 arquivos de código-fonte não possuem testes unitários diretos.
- **Arquivos sem cobertura unitária direta**:
  1. `editors/vscode/src/connections/commands.ts` (287 linhas):
     - Fluxo de criação, edição e exclusão de conexões via comandos VS Code.
     - Diálogos de confirmação e tratamento de cancelamento pelo usuário no meio do formulário.
     - Falha de comunicação com o Worker ou erro no SecretStorage.
  2. `editors/vscode/src/connections/connector-shared.ts` (137 linhas):
     - Validação de regras de porta, host, database e formatos de arquivo.
     - Construção de strings de conexão com tokens `{password}`.
  3. Módulos individuais de formulário em `editors/vscode/src/connections/modules/`:
     - `csv.ts`, `excel.ts`, `json.ts`, `xml.ts` (validação de paths e delimitadores).
     - `sqlserver.ts`, `postgresql.ts`, `mysql.ts`, `mariadb.ts`, `oracle.ts` (validação de portas padrão, SSL, esquemas).
     - `rest.ts` (validação de URLs e headers).
  4. `editors/vscode/src/worker/spawn.ts` (35 linhas):
     - Detecção de binários do dotnet no PATH do sistema.
     - Tratamento de erro quando o .NET 10 não está instalado ou falha ao iniciar.

---

## 4. Casos de Teste e Cenários que Precisam de Mais Testes (Edge Cases)

### Grupo A: Robustez e Injeção de Dados Malformados
1. **Separadores Numéricos e Localidades Mistas**:
   - String `"1.234,56"` (PT-BR / Europa) convertida para `decimal` e `float`.
   - String `"1,234.56"` (US / Invariante) convertida para `decimal` e `float`.
   - String `"$ 1,234.56"` ou `"R$ 1.234,56"` com símbolos monetários.
   - String com expoente científico `"1.23e-4"` e `"-4.5E+2"`.
2. **Textos Nulos e Sentinelas de Banco**:
   - Strings `"NULL"`, `"null"`, `"(null)"`, `"N/A"`, `"-"`, `""` mapeadas para colunas `int`, `decimal`, `bool` e `DateTime`.
   - Garantir que não disparem exceção e retornem o valor padrão ou `None`.
3. **Estouro de Inteiros (Boundary & Overflow)**:
   - Tentar ler `Int64.MaxValue` como `Int32` através de `RecordReaders.ReadInt32`. O comportamento esperado de *clamping* em `Int32.MaxValue` deve ser verificado.
   - Tentar ler números negativos abaixo de `Int32.MinValue`.

### Grupo B: Conectores e Drivers ADO.NET
1. **Mock / Testes Offline dos Conectores SQL**:
   - Criar mocks de `DbConnection` / `DbCommand` / `DbDataReader` para validar `SqlServerConnector`, `PostgreSqlConnector`, `MySqlConnector`, `MariaDbConnector` e `OracleConnector` sem precisar ligar Docker.
   - Testar o comportamento de `TestConnectionAsync` quando `OpenAsync()` dispara `TimeoutException` ou `SqlException`.
2. **Conexões com Caracteres Especiais na Senha**:
   - Senhas contendo `;`, `=`, `{`, `}`, `"`, `'`, `@` e caracteres Unicode, verificando se o token `{password}` e a resolução no `BridgeSecretStore` preservam a senha intacta.
3. **Arquivos Bloqueados ou Inexistentes**:
   - Consulta a arquivo Excel ou SQLite enquanto o arquivo está aberto com bloqueio exclusivo por outro aplicativo (ex: Excel no Windows).
   - Consulta a arquivo CSV cujo caminho contém espaços, acentos ou caracteres especiais.

### Grupo C: Motor de Consultas FSQL Pushdown
1. **Operadores de Junção**:
   - Consulta com `rightOuterJoin` traduzida para `RIGHT OUTER JOIN` e validada contra SQLite ou SQL Server.
   - Consulta com `fullOuterJoin` onde tanto o lado esquerdo quanto o direito possuem chaves sem correspondência.
2. **Funções Analíticas e de Janela (Window Functions)**:
   - `rowNumber` com partição e ordenação ascendente vs descendente.
   - `denseRank` e `ntile(4)` (quartis).
   - `lag` e `lead` com offset customizado e valor default (ex: `lag(p.Price, 2, 0.0M)`).
3. **Subconsultas Complexas**:
   - `whereNotExists` aninhado com múltiplas correlações na query externa.
   - Consulta utilizando `withCte` combinado com `unionAll` e `intersect`.
   - Projeção de subconsulta escalar: `select (p.Name, query { for o in orders ... select (sum o.Total) })`.

### Grupo D: Experiência do Usuário e Ciclo de Vida do VS Code
1. **Cancelamento de Célula em Execução**:
   - Interrupção de célula que executa um loop infinito no FSI ou query SQL longa (garantindo que o kernel não fique em estado `busy` travado).
2. **Reinício de Kernel e Sessão**:
   - Teste de `workspace/resetSession`: verificar se as variáveis definidas em células anteriores deixam de existir após o reset, enquanto as conexões cadastradas permanecem intactas.
3. **Resolução de Autocomplete FSAC em Scripts Longos**:
   - Autocomplete em notebooks com 50+ células consecutivas, garantindo tempo de resposta < 100ms e contexto preservado.

---

## 5. Plano de Ação Recomendado (Roadmap de Testes)

```mermaid
flowchart TD
    A["1. Testes Unitários de Conectores SQL (Mocks)"] --> B["2. Cobertura de Normalização e Tipos (RecordReaders)"]
    B --> C["3. Cobertura de Operações FSQL (Pushdown)"]
    C --> D["4. Testes Unitários de Formulários TS (VS Code)"]
    D --> E["5. Canais de Progresso Reverso & Handlers"]
```

### Prioridade 1 (Alta Gravidade / Risco Operacional)
1. **Adicionar testes unitários com mocks para os 5 conectores SQL**:
   - Criar `SqlServerConnectorTests.fs`, `PostgreSqlConnectorTests.fs`, etc., dentro de `FSharpQueryStudio.Connectors.Tests`, testando instanciação, tratamento de erros de conexão e montagem de SQL sem depender de Docker.
2. **Expandir suíte de `RecordReadersTests.fs`**:
   - Adicionar matriz de testes cobrindo todas as permutações de números formatados, vírgulas, pontos, sentinelas e limites de tipo.

### Prioridade 2 (Funcionalidades Centrais do Usuário)
3. **Completar testes de custom operations em `SqlPushdownQueryTests.fs`**:
   - Implementar testes específicos para `rightOuterJoin`, `denseRank`, `ntile`, `firstValue`, `lastValue` e projeções escalares (`ToUpper`, `Trim`, `Distinct`).
4. **Adicionar testes unitários para a camada de comandos e módulos do VS Code**:
   - Criar `commands.test.ts` e `connector-shared.test.ts` em `editors/vscode/src/connections/`.

### Prioridade 3 (Robustez e Resiliência)
5. **Testar canais reversos do Worker**:
   - Implementar testes em `FSharpQueryStudio.Worker.Tests` simulando o envio de eventos `staging/progress` e `entityMapping/status` pelo `HostChannel`.
6. **Testes de recuperação de falha no armazenamento**:
   - Validar cenários de `workspace.json` com permissão negada, corrupção de JSON e migração de projetos com estado inconsistente.

---

## 6. Conclusão

A arquitetura de testes do `fs-query-studio` é robusta no caminho feliz e nos fluxos principais de execução de cadernos e geração de amostras. No entanto, o sistema apresenta **pontos cegos significativos em tratamentos de exceção, drivers relacionais sem Docker, rotinas numéricas de borda e operações avançadas do DSL FSQL**.

A execução das ações priorizadas neste documento elevará a cobertura do backend de **77.2% para >90%**, além de fechar a lacuna de testes nos módulos de UI da extensão, garantindo a máxima confiabilidade para ambientes de produção.
