# Change Proposal: Excel Schema Container & Prefixed SQLite Table Mapping

## 1. Context & Problem Statement
Currently, when an Excel (`.xlsx`) connection (e.g., `teste.xlsx` with worksheets `calendar`, `names`, and `courses`) is imported and mapped:
1. **Duplicate Root Table & Schema Pollution**:
   - `ExcelEntityGenerator` exposes the first worksheet (`calendar`) directly on the connection root (`Connections.excel.teste`) via `defaultQueryable`, `.Rows`, `IQueryable<'Row>`, and `IEnumerable<'Row>`.
   - `Connections.syncDataSourcesAsyncWithProgress` creates a duplicate table named after the connection (`teste`) in `.fsqs/cache/engine.db` containing the first worksheet's rows, alongside `<conn>_<sheet>` tables (`teste_calendar`, `teste_names`, `teste_courses`).
   - However, an Excel connection name (`teste`) represents a workbook / "schema" container (analogous to a database connection), NOT a table itself. In SQLite staging, it must act strictly as a table prefix (`teste_calendar`, `teste_names`, `teste_courses`).
2. **Broken LINQ-to-SQL Pushdown Table Resolution (`FROM <conn>` instead of `FROM <conn>_<sheet>`)**:
   - In `ExcelConnection.SheetQueryable` and `ExcelConnection.SheetPushdownQueryable`, an `elif` fallback (`elif eng.IsStagedAndCurrent(name, connector.FilePath) && (String.Equals(name, sheetName, StringComparison.OrdinalIgnoreCase) || maxRows.IsNone) then name`) evaluates `maxRows.IsNone` as `true` for all generated worksheet properties (`conn.SheetQueryable<Row>("calendar")`).
   - Whenever the unprefixed connection name (`teste`) is staged in SQLite (or when `name = sheetName`), queries against `Connections.excel.teste.calendar` (or any worksheet) resolve `cleanTableName` to `teste`, generating `SELECT <FIELDS> FROM teste` instead of `SELECT <FIELDS> FROM teste_calendar`.

## 2. Intent Block
```text
Intent Block:
Goal:        Ensure Excel (.xlsx) connections act strictly as schema/workbook namespaces in F# and as table prefixes (<conn>_<sheet>) in the SQLite staging engine, with accurate per-worksheet SQL pushdown translation.
Expectation: Importing `teste.xlsx` with sheets `calendar`, `names`, and `courses` stages only `teste_calendar`, `teste_names`, and `teste_courses` in SQLite (never a duplicate `teste` table), exposes only worksheet members on `Connections.excel.teste`, and translates `query { for x in Connections.excel.teste.calendar do select x }` to `SELECT ... FROM teste_calendar`.
Action:      Remove root-level `IQueryable`/`Rows` exposure from `ExcelEntityGenerator`, remove duplicate `<conn>` table staging (and clean up stale unprefixed tables) in `Connections.syncDataSourcesAsyncWithProgress`, and fix `ExcelConnection.SheetQueryable` / `SheetPushdownQueryable` and `FileSqliteEngine.IsStagedAndCurrent` to always resolve `<conn>_<sheet>`.
Result:      100% deterministic worksheet table resolution and zero duplicate root tables for Excel connections.
```

