# Delta Specification: Support for XML and JSON Local File Data Sources (Schema Container & Sub-Property Model)

## ADDED Requirements

### REQ-JSON-01 — Conector de Arquivo Local JSON Multi-Coleção (`JsonConnector` & `JsonDescriptor`)
```text
When a connection of kind 'Json' is configured with a local '.json' file path,
the JsonConnector shall parse the JSON document using System.Text.Json, discover all logical tables/collections in the document (named array properties under a root object, a root array named 'Items', or a configured 'tableName'/'jsonPath'), flatten nested scalar object properties using underscore separators when 'flattenNested' is enabled, apply configured column transforms and type overrides, and expose per-table schema metadata (DiscoverSchemaAsync) and lazy row enumeration (ReadRows(?tableName, ?maxRows)).
```

### REQ-XML-01 — Conector de Arquivo Local XML Seguro Multi-Coleção (`XmlConnector` & `XmlDescriptor`)
```text
When a connection of kind 'Xml' is configured with a local '.xml' file path,
the XmlConnector shall parse the XML document using System.Xml.Linq with DTD processing prohibited (XXE protection), discover all repeating element collections in the document (or a configured 'rowElement' tag name), project XML attributes and leaf child elements into columns (flattening nested elements with underscore separators when 'flattenNested' is enabled), apply configured column transforms and type overrides, and expose per-table schema metadata (DiscoverSchemaAsync) and lazy row enumeration (ReadRows(?tableName, ?maxRows)).
```

### REQ-JSON-XML-02 — Geração de Entidades como Schema Container com Sub-Propriedades (`Connections.json` e `Connections.xml`)
```text
When generating F# entities for a 'Json' or 'Xml' connection,
the JsonEntityGenerator and XmlEntityGenerator shall emit 'Connections.json.<Conn>' and 'Connections.xml.<Conn>' ('Data') strictly as schema containers (analogous to ExcelEntityGenerator) exposing strongly-typed 'SqlPushdownQueryable<<Table>.Row>' sub-properties for each discovered table/collection ('.<Table>'), '.Connection', and '.Raw(?maxRows)', inferring F# field types via TabularTypeInference (including option types when null, missing, or sentinel values are present).
```

### REQ-JSON-XML-03 — Staging Prefixado (`<Conn>_<Table>`) no `FileSqliteEngine` e Pushdown SQL Determinístico
```text
When querying a sub-property via 'Connections.json.<Conn>.<Table>' or 'Connections.xml.<Conn>.<Table>', or when synchronizing workspace data sources in 'Connections.syncDataSourcesAsyncWithProgress',
the JsonConnection, XmlConnection, and Connections synchronizer shall stage each discovered table exclusively under the prefixed SQLite table name '<Conn>_<Table>' (sanitized via 'cleanIdentifier') in '.fsqs/cache/engine.db', create automatic B-Tree indexes on key columns, and translate LINQ queries via SqlPushdownQueryable<'T> to 'FROM <Conn>_<Table>'.
```

### REQ-JSON-XML-04 — Auto-Import na Pasta `data_sources/` e Monitoramento no VS Code
```text
When scanning or watching the 'data_sources/' directory,
the WorkspaceOperations auto-importer and the VS Code extension file watcher shall detect '.json' and '.xml' files, register them as 'Json' and 'Xml' connections respectively, generate F# entities, and pre-stage all discovered sub-tables in the SQLite cache.
```

### REQ-JSON-XML-05 — Registro Modular no Backend e Frontend
```text
When the worker or VS Code extension initializes connector descriptors,
the BuiltinConnectors module in the backend and BUILTIN_FRONTEND_CONNECTORS in the frontend shall register the 'Json' and 'Xml' descriptors, exposing declarative configuration forms and connection-name autocomplete for 'Connections.json' and 'Connections.xml'.
```

## MODIFIED Requirements

### REQ-SAMPLE-JSON-XML-01 — Inclusão de Fontes JSON e XML Multi-Coleção nos Dados de Exemplo (`sample_demo`)
- **MODIFIED FROM**: `SampleDataGenerator` e `WorkspaceOperations.createSampleData` gerando apenas arquivos SQLite, CSV e XLSX em `data_sources/`.
- **MODIFIED TO (GEARS)**:
```text
When generating sample project data ('workspace/createSampleData'),
the SampleDataGenerator and WorkspaceOperations shall also generate multi-collection sample '.json' and '.xml' files inside 'data_sources/', auto-import them into the workspace, and include interactive LINQ query cells demonstrating sub-property queries on 'Connections.json.<Conn>.<Table>' and 'Connections.xml.<Conn>.<Table>' in both 'sample_demo.fsqnb' and 'sample_demo_pt.fsqnb'.
```
