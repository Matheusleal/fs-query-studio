# Changelog

All notable changes to the **F# Query Studio** VS Code extension are listed
here. The format follows [Keep a Changelog](https://keepachangelog.com/);
versions are the extension's `package.json` `version` and map to
`vscode-v<version>` git tags.

## [Unreleased]

- **Fixed:** a cell that produced a table showed only FSI's `val … =`
  echo — the grid was hidden behind the output's "Change presentation"
  menu. The grid is now the default view; FSI's declaration echo and any
  `printf` output move to the `text/plain` "raw data" presentation.
- **New F# Query Notebook** commands — `F# Query Studio: New F# Query
  Notebook` (Command Palette and File > New File…) opens a new untitled
  notebook with one empty F# cell; a folder's Explorer context menu gets
  **New F# Query Notebook Here**, which writes and opens a `.fsqnb` in that
  folder (F9).
- Build / package / publish scripts under `editors/vscode/scripts/`
  (`npm run build`, `npm run package`, `npm run publish`, `npm run doctor`);
  CI packages the VSIX through the same `npm run package` path (F8).

## [0.1.0] — unreleased

First packaged build of the extension — feature parity with the retired
Avalonia desktop app for the query-and-execution workflow. Not yet
published to the Marketplace.

### Added

- **F# Query Notebook** (`.fsqnb`) — a custom notebook type; each code cell
  runs against a persistent, per-notebook FSI session in a bundled F#
  worker process. stdout / stderr stream live; a running cell can be
  cancelled; **Restart Notebook Session** clears the session.
- **Results grid renderer** — virtualized table for tabular results, with a
  schema tab, CSV export, and paged "load more" for large result sets.
- **In-cell F# intelligence** — completion, hover, signature help, and
  diagnostics for notebook cells, forwarded to the worker's `analysis/*`
  methods (Polyglot-style; Ionide is not involved). Includes
  connection-name completion inside `Connections.*` string literals.
- **Connections view** — an Activity Bar tree of connections → tables →
  columns (lazily loaded); add / edit / remove via guided pickers.
  Supports SQLite, CSV, Excel, SQL Server, PostgreSQL, and REST JSON.
- **Cross-platform secret storage** — credentials go to the OS keychain via
  VS Code `SecretStorage`, reached from the worker over a reverse RPC.
  Nothing secret is written to `workspace.json` or the notebook file.
- Worker health / protocol-version check at activation, with an actionable
  message when `dotnet` (>= 10) is missing.

### Notes

- Requires the **.NET 10 SDK** on `PATH`; the VSIX bundles a
  framework-dependent worker. A self-contained per-platform build is
  backlog.
- In-cell intelligence is completion / hover / signature help / diagnostics
  only — no find-all-references, formatting, code actions, or cross-cell
  rename.
- One evaluation runs at a time, including across multiple open `.fsqnb`
  files in the same window.
</content>
