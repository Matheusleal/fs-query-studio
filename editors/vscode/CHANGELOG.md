# Changelog

All notable changes to the **F# Query Studio** VS Code extension. The format
follows [Keep a Changelog](https://keepachangelog.com/); version numbers are the
`package.json` `version` and map to `vscode-v<version>` git tags.

[🇺🇸 English](CHANGELOG.md) | [🇧🇷 Versão em Português](CHANGELOG_PT.md)

## [Unreleased]

## [0.4.0] — 2026-11-02

### Added

- **Universal SQL Pushdown (EF-Style Projection & Relational Joins)** — Complete pushdown of `Select` projection expressions, member access, arithmetic, string concatenation (`||` / `+`), and relational `Join` across SQLite, PostgreSQL, and SQL Server, avoiding memory-bound row fetching and eliminating `SELECT *`.
- **Dynamic Projected Record Mapper** — Dynamically maps anonymous records and projected types (`'TResult`) from SQL reader columns via compiled expression delegate mapping.
- **Embedded File SQLite Staging Engine (`.fsqs/cache/engine.db`)** — High-performance staging engine for CSV and Excel files with single-transaction batch ingest, WAL mode, smart file hash/mtime caching (0ms reload on hits), and automatic B-tree indexing on `Id` and `*_id` keys.
- **Ultra-Fast Multi-Property Relational Joins** — Joins between different files on compound keys (`on ((a.p1, a.p2) = (b.p1, b.p2))` and LINQ `SelectMany` with compound predicates) execute natively in SQLite with sub-second performance (~18 ms for 5,000×40 rows) without memory explosions, supported by automatic composite B-tree indexes.
- **Automatic `data_sources/` Folder Discovery** — Added `fs-query-studio.autoImportFolder` command and quick toolbar button to auto-detect and register compatible files (`.csv`, `.xlsx`, `.db`, `.sqlite`) found in the `data_sources/` folder.
- **Asynchronous Cache Synchronization & Visual Status Feedback** — Added animated status bar item (`$(sync~spin) Syncing data sources cache...` ➔ `$(database) Data sources ready`) and dedicated command `F# Query Studio: Sync Data Sources Cache` to stage flat files into disk cache asynchronously.
- **Pre-Execution Query Inspection (`ResultKind.toRawSql`)** — Added `ResultKind.toRawSql`, `Connections.toRawSql`, and `.ToRawSql()` allowing users to inspect the exact SQL query and parameter list (`-- Parameters: ...`) generated for any pushdown or LINQ query before sending it to the database.
- **Direct SQL Execution on Workspace Engine** — Exposed `Connections.queryEngine(sqlText, ?parameters)` and `Connections.sql.query(sqlText, ?parameters)` returning tabular results.
- **Clean Entity Generation** — `DatabaseConnection.GenerateEntities` emits clean dynamic table references (`conn.Table<...>("TableName")`) rather than hardcoded full-column SELECT queries.
- **Modular Connector Architecture (Backend & Frontend)** — Decoupled connectors into independent, self-contained modules (`FSharpQueryStudio.Connectors.Abstractions`, `.Sqlite`, `.SqlServer`, `.PostgreSql`, `.Csv`, `.Excel`, `.Rest`) on the backend and declarative frontend modules (`editors/vscode/src/connections/modules/*.ts`) registered via `ConnectorRegistry` and `ConnectorFrontRegistry`.
- **Declarative Schema-Driven Connection Wizard (`connector/list`)** — Added `connector/list` RPC endpoint exposing connector descriptors and form field schemas so the VS Code extension dynamically renders connection forms, secret handling, and validation rules per connector module.
- **Expanded Multi-Source Sample Notebooks** — Added cell 7.1 demonstrating pushdown multi-property joins and `toRawSql` SQL inspection, alongside existing Excel (`.xlsx`) and GitHub REST API queries across `sample_demo.fsqnb` and `sample_demo_pt.fsqnb`.
- **Full Bilingual Documentation Parity** — Complete English and Portuguese documentation covering all user guides, tutorials, benchmarks, and changelogs.

### Fixed

- **Relative Local File Path Resolution** — Automatically resolves relative paths for local file-based connections (SQLite, CSV, Excel) relative to workspace directories.


## [0.4.0-rc1] — 2026-10-30

### Added

- **Modular Connector Architecture (Backend & Frontend)** — Decoupled connectors into independent, self-contained modules (`FSharpQueryStudio.Connectors.Abstractions`, `.Sqlite`, `.SqlServer`, `.PostgreSql`, `.Csv`, `.Excel`, `.Rest`) on the backend and declarative frontend modules (`editors/vscode/src/connections/modules/*.ts`) registered via `ConnectorRegistry` and `ConnectorFrontRegistry`.
- **Declarative Schema-Driven Connection Wizard (`connector/list`)** — Added `connector/list` RPC endpoint exposing connector descriptors and form field schemas so the VS Code extension dynamically renders connection forms, secret handling, and validation rules per connector module.
- **Expanded Multi-Source Sample Notebooks (`sample_demo.fsqnb` & `sample_demo_pt.fsqnb`)** — Added **Excel (`.xlsx`)** multi-sheet workbook (`RegionalTargets`) and **GitHub HTTPS REST API (`https://`)** (`GitHubFSharp`) connections and interactive LINQ query cells (11 code cells across 6 data sources: 2x SQLite, 2x CSV, Excel `.xlsx`, and GitHub HTTPS REST).
- **External Test Execution Watcher** — Added `scripts/watch-tests.ps1`, `scripts/run-all-tests.ps1`, and `scripts/run-all-tests.sh` enforcing a strict 5-minute external timeout and process-tree cleanup across all test suites.

### Fixed

- **UTF-8 Encoding Preservation Across Cell & Result Grid** — Enforced UTF-8 without BOM across NDJSON stdio streams in the worker and host process, preserving accented characters (e.g., `Transações`, `São Paulo`) without mojibake.
- **Sample Data Deduplication, Healing & FSI Entity Reloading** — Fixed duplicate connection name registration in `workspace.json`, added automatic healing for 0-byte or missing sample files, enforced synchronous entity generation when `GeneratedEntities.fs` is missing or stale, and added automatic `#load` reloading of updated entities in persistent FSI sessions.
- **Workspace Schema v2 Field Ordering** — Placed `connections` as the last property in `.fsqs/workspace.json` for improved readability of top-level metadata (`schemaVersion`, `extensionVersion`, `updatedAt`).

## [0.3.2] — 2026-10-28

### Removed

- **Unused Dependencies & External References Cleanup** — Removed unused `SSH.NET` package dependency from integration test suite and central package management (`Directory.Packages.props`). Cleaned up obsolete references to Ionide across documentation to reflect F# Query Studio's fully autonomous, built-in language service worker.

## [0.3.1] — 2026-10-28

### Fixed

- **Documentation Benchmark Report Link** — Bundled `BENCHMARK_REPORT.md` directly into the extension package and updated links in `USER_GUIDE.md` and `USER_GUIDE_PT.md` so that clicking the benchmark report from within the VS Code documentation preview opens the document directly without navigation errors.

## [0.3.0] — 2026-10-28

### Added

- **Typed Connections by Category API** — Replaced global connection leaking with strictly isolated categories under `Connections`:
  - `Connections.sql.<ConnName>.<Table>` for relational databases (SQLite, PostgreSQL, SQL Server, MySQL).
  - `Connections.sql.<ConnName>.rawQuery` scoped strictly to that specific connection.
  - `Connections.csv.<ConnName>` for strongly-typed CSV row streams and direct LINQ querying.
  - `Connections.excel.<ConnName>` for Excel worksheets.
  - `Connections.rest.<ConnName>` for REST API endpoints.
  - Legacy methods (`connect`, `connectCsv`, `connectExcel`, `connectRest`) and leaky `.asEntities` were retired.
- **Right-Aligned Type Badges in Autocomplete** — Suggestions in the autocomplete dropdown now render their exact type (`int`, `string`, `bool`, `decimal`, `DateTime`, etc.) or kind aligned to the right:
  - Fields and properties resolve and display their actual underlying type.
  - Methods display `method`.
  - Connection completions display their connector kind (`sql`, `csv`, `excel`, `rest`).
- **Entity Mapping Progress in Status Bar** — Added a real-time status bar spinner (`$(sync~spin) Mapping entities...`) during background schema inspection and F# type generation, providing clear visual feedback when updating connections.
- **FSAC Notebook Cell Workspace Resolution** — Background F# analysis engine now automatically maps notebook cell virtual URIs (`vscode-notebook-cell:`) to their workspace folder and loads `GeneratedEntities.fs` into FSAC, eliminating false-positive red squiggles and providing full IntelliSense and Go-to-Definition (`F12`) across cells.

## [0.2.3] — 2026-10-27

### Improved

- **Rich Sample Notebook Formatting & Bilingual Support** — Completely revamped the sample notebooks generated by `Create Sample Data` (`sample_demo.fsqnb` in English and `sample_demo_pt.fsqnb` in Portuguese):
  - Formatted markdown cells with visual badges, data source tables, keyboard shortcuts, and guidance.
  - Replaced tuple query projections with anonymous records (`{| Id = ...; Name = ... |}`) so the interactive table renderer displays named column headers with sorting, filtering, and type badges instead of generic `Item1`, `Item2` headers.
  - Added analytical aggregation examples (`groupBy`, `sumBy`, `averageBy`) and cross-source queries comparing relational SQLite data with flat-file CSV transactions.
  - Automatic language detection opens `sample_demo_pt.fsqnb` for Portuguese users and `sample_demo.fsqnb` for English users while creating both in the workspace.

## [0.2.2] — 2026-10-27

### Fixed

- **Multi-Datasource Code Generation** — Fixed entity background generation when multiple datasources of the same type (e.g., two CSV files) are registered. Unique module names and typed accessors are now generated, and `.asEntities` correctly pattern-matches across all registered connections without dropping extensions or causing name collisions.
- **CSV Decimal Type Inference** — Flat-file columns with floating-point / decimal numbers (e.g. `355272835.53` or `355272835,53`) are now accurately inferred as `decimal` instead of falling back to `string`. Quotes and whitespace in sample rows are stripped prior to inference.
- **Culture-Invariant Decimal & Numeric Parsing** — Added robust culture-invariant conversion in `Connections.convertValue` supporting dot (`.`) and comma (`,`) decimal notation under any host culture (such as `pt-BR`), resolving `FormatException: Input string was not in a correct format`.
- **Column Name Sanitization** — Empty or duplicate header columns in CSV and flat files are now automatically sanitized (`Column1`, `Column2`, deduplication suffixes) to prevent syntax errors in generated F# record types.

## [0.2.0] — 2026-10-27

### Added

- **Execution & Resource Benchmarks** — dedicated benchmark suite (`tests/FSharpQueryStudio.Benchmarks`) and automated regression tests measuring latency, CPU time, and GC allocations from connection to execution across cold start, warm queries, volume scaling (100 to 50,000 rows), LINQ queries, and CSV files. See `docs/BENCHMARK_REPORT.md`.
- **Comprehensive UI Element Testing** — complete test suite (`editors/vscode/src/renderer/ui-elements.test.ts` and `connections/ui-elements.test.ts`) covering 100% of the UI components: grid layout, toolbar, segmented views, row counters, sticky headers, virtualization sizer, alternating row styles, cell formatters (`NULL`, numeric, string, boolean), schema inspector, load more pagination, notification banners, and Connections Explorer tree items.
- **New F# Query Notebook** commands — **F# Query Studio: New F# Query Notebook**
  (Command Palette and *File > New File…*) opens a new untitled notebook with one
  empty F# cell. A folder's Explorer context menu gets **New F# Query Notebook
  Here**, which writes and opens a real `.fsqnb` in that folder.
- **Multi-Schema & Views Discovery** — expanded discovery for PostgreSQL, SQL Server, and SQLite to include user schemas (e.g. `dbo`, custom schemas) and views, grouping them into clean schema namespaces in the Connections tree.
- **Strongly-Typed LINQ & Entity Background Generator** — automatic background generation of typed record types and LINQ query contexts for databases and flat files (CSV, Excel, JSON) with `SQLProvider` integration.
- **Contextual Connection Name Autocomplete** — connection name completion inside `Connections.connect(...)`, `Connections.get(...)`, `Connections.query(...)`, and specific connectors (`sql`, `csv`, `excel`, `rest`), filtered by compatible connector kind.
- **Go to Definition (`F12`)** — definition navigation across notebook cells, external files, and background-generated entity models.
- **Safe Password Sanitization** — automatic detection and extraction of plaintext passwords from connection strings into secure OS keychain (`SecretStorage`), replaced with `{password}` tokens.
- **Sample Data Generator** — 1-click generation of sample SQLite database (`sample_store.db`), CSV feed (`sales_feed.csv`), and interactive demo notebook (`sample_demo.fsqnb`).
- **LINQ Query Expression Syntax Highlighting** — TextMate injection grammar and semantic tokens provider for F# query keywords (`select`, `where`, `join`, `groupBy`, `sortBy`, etc.).
- **Bilingual Documentation** — complete user guides in English (`USER_GUIDE.md`) and Portuguese (`USER_GUIDE_PT.md`), with automatic language detection and official Microsoft F# docs links.
- **Extension Icon** — custom icon with glowing F# lambda and holographic database cylinder emblem.
- Build / package / publish scripts under `editors/vscode/scripts/`
  (`npm run build` / `package` / `publish` / `doctor`). CI packages the VSIX
  through the same `npm run package` path.

### Changed

- The results grid is now the default view for a cell that produced a table.
  Previously the grid was hidden behind the output's "Change presentation" menu
  and the cell showed only FSI's `val … =` echo. That echo and any `printf`
  output now move to the `text/plain` "raw data" presentation.
- The results grid was restyled to match VS Code's own tables: UI font,
  right-aligned numeric columns, zebra rows, a contained sticky header, a
  rounded card, faint `NULL` markers, and a "No rows" placeholder.

### Fixed

- A cell's stdout and its result are now separate outputs, so a table result is
  no longer suppressed by `printf` output in the same cell.
- FSI's raw declaration echo is the fallback text view rather than the default.

## [0.1.0] — 2026-10-27

First packaged build of the extension — feature parity with the retired Avalonia
desktop app for the query-and-execution workflow. Not yet published to the
Marketplace.

### Added

- **F# Query Notebook** (`.fsqnb`) — a custom notebook type; each code cell runs
  against a persistent, per-notebook FSI session in a bundled F# worker process.
  stdout / stderr stream live; a running cell can be cancelled; **Restart
  Notebook Session** clears the session.
- **Results grid renderer** — virtualized table for tabular results, with a
  schema tab, CSV export, and paged "load more" for large result sets.
- **In-cell F# intelligence** — completion, hover, signature help, and
  diagnostics for notebook cells, forwarded to the worker's `analysis/*` methods
  (Ionide is not involved). Includes connection-name completion inside
  `Connections.*` string literals.
- **Connections view** — an Activity Bar tree of connections → tables → columns
  (lazily loaded); add / edit / remove via guided pickers. Supports SQLite, CSV,
  Excel, SQL Server, PostgreSQL, and REST JSON.
- **Cross-platform secret storage** — credentials go to the OS keychain via VS
  Code `SecretStorage`, reached from the worker over a reverse RPC. Nothing
  secret is written to `workspace.json` or the notebook file.
- Worker health / protocol-version check at activation, with an actionable
  message when `dotnet` (≥ 10) is missing.

### Known limitations

- Requires the **.NET 10 SDK** on `PATH`; the VSIX bundles a
  framework-dependent worker. A self-contained per-platform build is backlog.
- In-cell intelligence is completion / hover / signature help / diagnostics
  only — no find-all-references, formatting, code actions, or cross-cell rename.
- One evaluation runs at a time, including across multiple open `.fsqnb` files in
  the same window.
