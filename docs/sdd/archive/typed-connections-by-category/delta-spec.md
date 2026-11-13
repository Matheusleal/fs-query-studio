# Delta Specification: Typed Connections By Category & Entity Mapping Progress

## Requisitos GEARS

### ADDED Requirements

#### FR-CONN-CATEGORY-API: Categorias de Conexão no Módulo `Connections`
```text
The Connections module shall expose typed category containers:
'sql' for relational database connections,
'csv' for delimiter-separated values connections,
'excel' for spreadsheet workbook connections,
and 'rest' for HTTP REST/JSON endpoints.
```

#### FR-CONN-SQL-ISOLATION: Isolamento de Esquemas e Tabelas por Conexão SQL
```text
Where a SQL connection is registered with name <ConnName>,
the entity code generator shall emit a strongly-typed database accessor on 'Connections.sql.<ConnName>'
exposing only the tables and schemas discovered from <ConnName>,
and shall not expose tables or entities belonging to other SQL connections.
```

#### FR-CONN-SQL-RAW-QUERY: Execução de Consulta SQL Direta na Conexão
```text
Where a SQL connection is registered with name <ConnName>,
the entity code generator shall emit a 'rawQuery' member on 'Connections.sql.<ConnName>'
that accepts a SQL string and optional parameters and executes the query through <ConnName>.
```

#### FR-CONN-CSV-DIRECT-QUERY: Acesso e Consulta Direta de Conexão CSV
```text
Where a CSV connection is registered with name <ConnName>,
the entity code generator shall emit an accessor on 'Connections.csv.<ConnName>'
implementing 'System.Linq.IQueryable<Row>' and exposing '.Rows' and '.Raw()'
without exposing other registered CSV connections.
```

#### FR-CONN-EXCEL-REST-ISOLATION: Isolamento para Conexões Excel e REST
```text
Where an Excel or REST connection is registered with name <ConnName>,
the entity code generator shall emit accessors on 'Connections.excel.<ConnName>' and 'Connections.rest.<ConnName>'
exposing sheets and items strictly scoped to <ConnName>.
```

#### FR-UI-ENTITY-MAPPING-STATUS: Feedback Visual na Barra de Status do VS Code
```text
While entity background generation is in progress,
the worker shall notify the host environment
and the VS Code extension shall display a visual indicator on the status bar
with text '$(sync~spin) Mapping entities...' and hide or update it upon completion.
```

#### FR-AUTOCOMPLETE-TYPE-DETAIL: Exibição do Tipo Alinhado à Direita no Autocomplete
```text
When the language client requests completion items for notebook cells,
the worker shall extract the concrete field/property type, method signature, or connector kind,
and the VS Code completion provider shall render it aligned to the right via 'CompletionItemLabel.description'.
```

#### FR-TEST-CLEANUP-INTEGRITY: Limpeza Obrigatória de Recursos e Arquivos de Testes
```text
When any automated test creates files or directories on disk,
the test execution shall delete all created files and directories upon completion.
```

---

### REMOVED Requirements

#### FR-CONN-CONNECT-METHODS: Remoção dos Métodos Genéricos `Connections.connect*`
```text
The Connections module shall not expose 'connect', 'connectCsv', 'connectExcel', or 'connectRest' functions.
```

#### FR-CONN-AS-ENTITIES: Remoção da Propriedade `.asEntities`
```text
The connection wrappers and generated database/data classes shall not expose an 'asEntities' member.
```

---

### MODIFIED Requirements

#### FR-CONN-CODEGEN-STRUCTURE: Estrutura do Código Gerado em `GeneratedEntities.fs`
```text
When entity background generation executes for registered workspace connections,
the entity generator shall generate type extensions for 'Connections.SqlCategory', 'Connections.CsvCategory', 'Connections.ExcelCategory', and 'Connections.RestCategory',
providing direct, strongly-typed connection members without global aggregate types.
```
