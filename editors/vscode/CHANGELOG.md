# Changelog

All notable changes to the **F# Query Studio** VS Code extension. The format
follows [Keep a Changelog](https://keepachangelog.com/); version numbers are the
`package.json` `version` and map to `vscode-v<version>` git tags.

## [Unreleased]

### Added

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

## [0.1.0] — unreleased

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
