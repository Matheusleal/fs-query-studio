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
