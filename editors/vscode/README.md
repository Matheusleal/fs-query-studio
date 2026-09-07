# F# Query Studio

Write, analyze, and run F# query scripts against SQL databases, local files, and REST APIs — as VS Code notebooks.

## Requirements

- **.NET 10 SDK** on your `PATH` (`dotnet --version` ≥ 10). The extension runs a bundled F# worker process with it.
- VS Code 1.90 or newer.

## Quick start

1. Run **F# Query Studio: New F# Query Notebook** from the Command Palette (or File > New File…, or right-click a folder in the Explorer) — or just create a file ending in `.fsqnb` and open it. It opens as an **F# Query Notebook**.
2. Type F# into a code cell and run it. `let` bindings persist to later cells in the same notebook (a persistent FSI session). **F# Query Studio: Restart Notebook Session** clears it.
3. Open the **F# Query Studio** view in the Activity Bar and **Add Connection** — SQLite / CSV / Excel (file path), SQL Server / PostgreSQL (connection string; use `{password}` where the secret goes), or REST JSON (URL). Credentials are stored in the OS keychain via VS Code `SecretStorage`, never written to `workspace.json` or the notebook.
4. Reference a connection from a cell (`Connections.sql "my-conn" "select ..."`), run it, and the result renders as a sortable grid with a schema tab and **Export CSV**.

## Features

- **Notebook execution** — each `.fsqnb` code cell runs against a persistent, per-notebook FSI session; stdout/stderr stream live; cancel a running cell from the toolbar.
- **Results grid** — virtualized table, schema view, CSV export, paged loading for large results.
- **In-cell F# intelligence** — completion, hover, signature help, and diagnostics. Provided by this extension.
- **Connection management** — a tree of connections → tables → columns, lazily loaded; add / edit / remove via guided pickers.
- **Cross-platform** — Windows, macOS, Linux. One F# worker per VS Code window.

## Limitations

- In-cell intelligence is completion / hover / signature help / diagnostics only — no find-all-references, formatting, code actions, or cross-cell rename.
- One evaluation runs at a time, including across multiple open `.fsqnb` files in the same window.
- Requires `dotnet` on `PATH`; there is no self-contained build yet.

## Relationship to Ionide

[Ionide](https://ionide.io) handles F# in ordinary `.fs` / `.fsx` files. It never sees notebook cells, and this extension never touches `.fs` / `.fsx`. Install both; they do not conflict.

## Contributing / development

```bash
cd editors/vscode
npm install
npm run build         # dotnet publish the worker + type-check + esbuild bundle
npm test              # unit tests (vitest)
npm run test:vscode   # extension-host tests in a downloaded VS Code
```

Press F5 in VS Code to launch an Extension Development Host. Set `FSQS_WORKER_DLL` to use a worker built elsewhere instead of `dist/worker`.

### Building & packaging

| Command | Purpose |
|---|---|
| `npm run build` | Full runnable build: publish the F# worker, then type-check + bundle. |
| `npm run package` | Build, then produce `fs-query-studio.vsix` with the same warning gate + contents assertion CI uses. |
| `npm run doctor` | Read-only preflight for a release (toolchain, extension id, Marketplace icon, `VSCE_PAT`). |
| `npm run publish` | Preflight → package → `vsce publish`. Needs `VSCE_PAT`. |

Details and flags: [`editors/vscode/scripts/README.md`](https://github.com/Matheusleal/fs-query-studio/blob/main/editors/vscode/scripts/README.md). Full release runbook: [`docs/vscode-extension-release.md`](https://github.com/Matheusleal/fs-query-studio/blob/main/docs/vscode-extension-release.md).

Design: `docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md`.
