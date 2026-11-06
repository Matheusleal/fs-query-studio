# Change Proposal: Support for XML and JSON Local File Data Sources (Schema Container & Sub-Property Model)

---

## 🇧🇷 Português (PT-BR)

### 1. Contexto & Motivação
Atualmente, o F# Query Studio suporta bancos de dados relacionais (`Sqlite`, `SqlServer`, `PostgreSql`), arquivos tabulares (`Csv`, `Excel`) e endpoints HTTP (`RestJson`). Muitos fluxos de análise de dados e integração corporativa utilizam arquivos locais **JSON (`.json`)** e **XML (`.xml`)** na pasta `data_sources/` ou registrados manualmente.

Assim como uma planilha Excel (`.xlsx`) possui múltiplas abas e funciona como um **container de schema** (`Connections.excel.<Conn>.<Sheet>` mapeado para `<Conn>_<Sheet>` no SQLite), arquivos `.json` e `.xml` frequentemente agrupam múltiplas coleções de dados estruturados no mesmo arquivo (ex.: `{ "customers": [...], "orders": [...] }` ou `<ErpExport><Invoices><Invoice.../></Invoices><Suppliers><Supplier.../></Suppliers></ErpExport>`).

Seguindo a arquitetura Micro-Kernel & Plugin SPI (`FSharpQueryStudio.Connectors.Abstractions`) e o modelo do `ExcelConnector` + `FileSqliteEngine`, adicionaremos suporte de primeira classe a arquivos `.json` e `.xml` como **containers de schema com sub-propriedades**, sem adicionar nenhuma dependência NuGet externa (usando `System.Text.Json` e `System.Xml.Linq` nativos do .NET 10).

### 2. Intent Block
```text
Intent Block:
Goal:        Adicionar suporte completo a arquivos locais XML (.xml) e JSON (.json) como fontes de dados nativas no F# Query Studio seguindo o modelo de container de schema com sub-propriedades (análogo ao Excel), integradas ao auto-import da pasta data_sources/, geração de entidades F# fortemente tipadas e staging prefixado (<Conn>_<Table>) no FileSqliteEngine para execução de queries LINQ com SQL Pushdown.
Expectation: O usuário pode adicionar conexões JSON e XML via UI ou colocando arquivos .json e .xml em data_sources/, consultar cada coleção/tabela como sub-propriedade via Connections.json.<Conn>.<Table> e Connections.xml.<Conn>.<Table> usando query { ... }, inspecionar o SQL gerado (SELECT ... FROM <Conn>_<Table>) via toRawSql e realizar joins diretos com outras fontes (CSV, XLSX, SQLite).
Action:      (1) Criar os projetos isolados FSharpQueryStudio.Connectors.Json e FSharpQueryStudio.Connectors.Xml usando System.Text.Json e System.Xml.Linq; (2) Implementar descoberta de múltiplas tabelas/coleções por arquivo, inferência de schema e flattening de propriedades/atributos aninhados; (3) Integrar JsonConnection e XmlConnection ao FileSqliteEngine com staging prefixado (<Conn>_<Table>), Connections.fs (Connections.json e Connections.xml), ModularEntityGenerators.fs (emitindo sub-propriedades SqlPushdownQueryable<Row> igual ao Excel) e BuiltinConnectors.fs; (4) Estender o auto-import (WorkspaceOperations.fs) e o file watcher do VS Code (extension.ts) para detectar .json e .xml; (5) Registrar os módulos frontend json.ts e xml.ts em editors/vscode/src/connections/modules/index.ts e adicionar exemplos multi-coleção nos notebooks sample_demo.
Result:      Fontes de dados locais JSON e XML operam como containers de schema com sub-propriedades tipadas, staging prefixado (<Conn>_<Table>) no SQLite e LINQ-to-SQL pushdown determinístico, sem dependências externas adicionais.
```

### 3. Modelo de Container de Schema e Sub-Propriedades (Similar ao Excel)
- **F# Entity Generation (`Connections.json.<Conn>.<Table>` e `Connections.xml.<Conn>.<Table>`)**:
  - `Connections.json.<Conn>` e `Connections.xml.<Conn>` expõem um tipo `Data` atuando estritamente como **container de schema**, com uma sub-propriedade `SqlPushdownQueryable<<Table>.Row>` para cada tabela/coleção descoberta no arquivo, além de `.Connection` e `.Raw(?tableName, ?maxRows)`.
- **Regras de Descoberta de Tabelas/Coleções**:
  - **JSON (`JsonConnector`)**:
    1. Se a raiz for um objeto JSON (`{ ... }`), cada propriedade cujo valor seja um array de objetos (ou sub-objetos contendo arrays de objetos) é descoberta como uma tabela distinta (`<PropName>`). Caso um objeto raiz não possua arrays, mas apenas propriedades escalares/aninhadas, é exposto como tabela única `Root`.
    2. Se a raiz for diretamente um array JSON (`[ {...}, {...} ]`), é descoberta como a tabela `Items` (ou o nome configurado em `tableName`/`jsonPath`).
  - **XML (`XmlConnector`)**:
    1. Descobre cada grupo de elementos repetidos no documento (ex.: `<Catalog><Products><Product .../></Products><Suppliers><Supplier .../></Suppliers></Catalog>` -> tabelas `Products` e `Suppliers`, ou `<Catalog><Book .../><Book .../></Catalog>` -> tabela `Book`).
    2. Projeta atributos XML e elementos filhos folhas como colunas (com flattening via `_` para sub-elementos aninhados).
- **Staging Prefixado no SQLite (`FileSqliteEngine`)**:
  - Cada tabela descoberta em uma conexão JSON ou XML é stageada em `.fsqs/cache/engine.db` sob o nome composto `<Conn>_<Table>` (higienizado via `cleanIdentifier`), garantindo que `query { for x in Connections.json.Catalog.Products do select x }` traduza para `SELECT ... FROM Catalog_Products`.

### 4. Escopo (In / Out)
- **In Scope**:
  - Novos projetos `src/FSharpQueryStudio.Connectors.Json` e `src/FSharpQueryStudio.Connectors.Xml` usando apenas bibliotecas da BCL do .NET 10 (`System.Text.Json` e `System.Xml.Linq`).
  - Descoberta multi-tabela (`DiscoverSchemaAsync`) com inferência de tipos (`int`, `int64`, `decimal`, `double`, `bool`, `System.DateTime`, `System.Guid`, `string` e `option` para valores nulos/ausentes).
  - Staging automático prefixado `<Conn>_<Table>` no `FileSqliteEngine` com criação de índices B-Tree automáticos e suporte a `SqlPushdownQueryable<'T>`.
  - Categorias fortemente tipadas `Connections.json.<Conn>.<Table>` e `Connections.xml.<Conn>.<Table>`.
  - Detecção automática de `.json` e `.xml` em `data_sources/` (auto-import no Worker e file watcher no VS Code).
  - Módulos de UI declarativos no VS Code (`json.ts` e `xml.ts`) e atualização dos dados de exemplo (`sample_demo.fsqnb` e `sample_demo_pt.fsqnb`).
- **Out of Scope**:
  - Validação de XSD/DTD externo com resolução de rede (DTD processing permanecerá proibido por segurança contra XXE).
  - Escrita/mutação nos arquivos `.json` e `.xml` de origem.

### 5. Artefatos Impactados
- `FSharpQueryStudio.slnx`
- `src/FSharpQueryStudio.Connectors.Json/FSharpQueryStudio.Connectors.Json.fsproj`, `JsonConnector.fs`, `JsonDescriptor.fs`
- `src/FSharpQueryStudio.Connectors.Xml/FSharpQueryStudio.Connectors.Xml.fsproj`, `XmlConnector.fs`, `XmlDescriptor.fs`
- `src/FSharpQueryStudio.Connectors/FSharpQueryStudio.Connectors.fsproj`
- `src/FSharpQueryStudio.Connectors/Connections.fs`
- `src/FSharpQueryStudio.Connectors/ModularEntityGenerators.fs`
- `src/FSharpQueryStudio.Connectors/BuiltinConnectors.fs`
- `src/FSharpQueryStudio.Connectors/SampleDataGenerator.fs`
- `src/FSharpQueryStudio.Fsac/Prologue.fs`
- `src/FSharpQueryStudio.Worker/WorkspaceOperations.fs`
- `src/FSharpQueryStudio.Worker/Handlers.fs`
- `editors/vscode/src/connections/connector-shared.ts`
- `editors/vscode/src/connections/modules/json.ts`
- `editors/vscode/src/connections/modules/xml.ts`
- `editors/vscode/src/connections/modules/index.ts`
- `editors/vscode/src/extension.ts`
- `editors/vscode/package.json`
- `tests/FSharpQueryStudio.Connectors.Tests/` & `tests/FSharpQueryStudio.Worker.Tests/`

### 6. Critérios de Sucesso
- [ ] `FSharpQueryStudio.Connectors.Json` e `FSharpQueryStudio.Connectors.Xml` compilam sem dependências NuGet de terceiros.
- [ ] Segurança XML garantida (`DtdProcessing.Prohibit` e `XmlResolver = null` por padrão contra ataques XXE).
- [ ] `Connections.json.<Conn>.<Table>` e `Connections.xml.<Conn>.<Table>` expõem sub-propriedades fortemente tipadas com `SqlPushdownQueryable<Row>` traduzindo para `FROM <Conn>_<Table>` via `FileSqliteEngine`.
- [ ] Auto-import da pasta `data_sources/` registra arquivos `.json` e `.xml` automaticamente e sincroniza todas as sub-tabelas com o cache SQLite.
- [ ] `sample_demo.fsqnb` e `sample_demo_pt.fsqnb` executam com 100% de sucesso nos testes do Worker e na integração E2E do VS Code localmente e no container Linux.
