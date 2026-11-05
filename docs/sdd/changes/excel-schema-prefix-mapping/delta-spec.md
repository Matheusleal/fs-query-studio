# Delta Specification: Excel Schema Container & Prefixed SQLite Table Mapping

## MODIFIED Requirements

### REQ-EXCEL-01 — Excel Connection as Schema Container in F# Entity Generation
- **MODIFIED FROM**: `ExcelEntityGenerator` generating `defaultQueryable`, `.Rows`, `IQueryable<FirstSheet.Row>`, and `IEnumerable<FirstSheet.Row>` directly on the `Connections.excel.<Conn>` `Data` type.
- **MODIFIED TO (GEARS)**:
```text
When generating F# entities for an Excel connection,
the ExcelEntityGenerator shall emit `Connections.excel.<Conn>` (`Data`) strictly as a workbook/schema container exposing strongly-typed `SqlPushdownQueryable<'Row>` properties for each worksheet (`.<Sheet>`), `.Connection`, and `.Raw(?maxRows)`, without implementing `IQueryable` or exposing `.Rows` for the first worksheet at the connection root.
```

### REQ-EXCEL-02 — Prefixed Worksheet Table Staging (`<Conn>_<Sheet>`) in SQLite Engine
- **MODIFIED FROM**: `Connections.syncDataSourcesAsyncWithProgress` staging a duplicate table named `<Conn>` with the first worksheet's rows in addition to `<Conn>_<Sheet>`.
- **MODIFIED TO (GEARS)**:
```text
When staging or synchronizing an Excel workbook in FileSqliteEngine,
the Connections synchronizer shall stage each worksheet exclusively under the prefixed table name `<Conn>_<Sheet>` (sanitized via `cleanIdentifier`), shall not create a table named `<Conn>`, and shall drop any legacy unprefixed `<Conn>` table previously staged for that Excel file.
```

### REQ-EXCEL-03 — Deterministic Per-Worksheet SQL Pushdown Table Resolution
- **MODIFIED FROM**: `ExcelConnection.SheetQueryable` and `SheetPushdownQueryable` falling back to the unprefixed connection name `name` when `maxRows.IsNone` or when `name` equals `sheetName`.
- **MODIFIED TO (GEARS)**:
```text
When querying a worksheet via `Connections.excel.<Conn>.<Sheet>`, `SheetQueryable`, or `SheetPushdownQueryable`,
the ExcelConnection shall always resolve and stage the prefixed SQLite table `<Conn>_<Sheet>` so that LINQ-to-SQL pushdown translates queries to `FROM <Conn>_<Sheet>`.
```

