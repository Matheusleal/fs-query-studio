# Tasks Ledger: Excel Schema Container & Prefixed SQLite Table Mapping

- [x] **TASK-EXCEL-01** (`Done`) — Normalize table names in `FileSqliteEngine.IsStagedAndCurrent` and add `DropStaleTableIfExists` helper in `src/FSharpQueryStudio.Connectors/FileSqliteEngine.fs` (Traces to: `REQ-EXCEL-02`, `REQ-EXCEL-03`).
- [x] **TASK-EXCEL-02** (`Done`) — Fix `ExcelConnection.SheetQueryable` / `SheetPushdownQueryable` / `Queryable` / `PushdownQueryable` and remove duplicate `<Conn>` table staging in `Connections.syncDataSourcesAsyncWithProgress` in `src/FSharpQueryStudio.Connectors/Connections.fs` (Traces to: `REQ-EXCEL-02`, `REQ-EXCEL-03`).
- [x] **TASK-EXCEL-03** (`Done`) — Update `ExcelEntityGenerator` in `src/FSharpQueryStudio.Connectors/ModularEntityGenerators.fs` so `Connections.excel.<Conn>` acts strictly as a schema container exposing `SqlPushdownQueryable` worksheet properties without root `IQueryable`/`Rows` (Traces to: `REQ-EXCEL-01`, `REQ-EXCEL-03`).
- [x] **TASK-EXCEL-04** (`Done`) — Update existing unit tests and add regression tests for `teste.xlsx` (`calendar`, `names`, `courses`) staging, schema exposure, and SQL pushdown translation (`FROM teste_calendar`, `FROM teste_names`, `FROM teste_courses`), plus mandatory `sample_demo` validation (Traces to: `REQ-EXCEL-01`, `REQ-EXCEL-02`, `REQ-EXCEL-03`).

