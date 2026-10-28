# F# Query Studio

[🇺🇸 English Guide](USER_GUIDE.md) | [🇧🇷 Guia em Português](USER_GUIDE_PT.md) | [📊 Benchmark Report](../../docs/BENCHMARK_REPORT.md)

Write, analyze, and run F# query scripts against SQL databases, local files, and
REST APIs — as VS Code notebooks.

Open a `.fsqnb` file as an **F# Query Notebook**. Each code cell runs F# against
a persistent FSI session in a bundled .NET worker. A controlled API reaches
databases and files you register in the sidebar, and tabular results render as a
sortable grid with a schema tab and CSV export.

## Requirements

- **.NET 10 SDK** on your `PATH` (`dotnet --version` ≥ 10). The extension runs a
  bundled framework-dependent F# worker process with it.
- **VS Code 1.90** or newer.
- Windows, macOS, or Linux. One worker process per VS Code window.

## Quick start

1. Run **F# Query Studio: New F# Query Notebook** from the Command Palette (also
   under *File > New File…*, and on a folder's Explorer context menu as **New F#
   Query Notebook Here**). Or just create and open any file ending in `.fsqnb`.
2. Type F# into a code cell and run it. `let` bindings persist to later cells in
   the same notebook — it is one FSI session per notebook file. **F# Query
   Studio: Restart Notebook Session** clears it.
3. Open the **F# Query Studio** view in the Activity Bar and **Add Connection**.
   Pick a kind, answer the prompts. Credentials are stored in the OS keychain
   via VS Code `SecretStorage` — never written to `workspace.json` or the
   notebook.
4. Reference a connection from a cell and run it; a tabular result renders as a
   grid with a schema tab and **Export CSV**.

A full hands-on walkthrough is in
[`docs/TUTORIAL.md`](https://github.com/Matheusleal/fs-query-studio/blob/main/docs/TUTORIAL.md).

## Writing cells

Every code cell runs in a session where this is already open — no `#r` or
`open` needed:

```fsharp
open FSharpQueryStudio.Connectors   // the Connections.* API
```

### `Connections` — strongly-typed connections by category

| Category | Kinds | Usage |
|---|---|---|
| `Connections.sql.<Conn>` | SQLite, SQL Server, PostgreSQL, MySQL | Typed tables (`.Users`) and scoped `.rawQuery "..." [ ... ]` |
| `Connections.csv.<Conn>` | CSV | Typed rows via LINQ `query { for r in Connections.csv.<Conn> do ... }` or `.Rows` |
| `Connections.excel.<Conn>` | Excel (XLSX) | Typed sheets (`.<SheetName>`) and `.Raw()` |
| `Connections.rest.<Conn>` | REST JSON | Typed items (`.Items`) and `.SendAsync()` |

### LINQ Queries with Automatic Strongly-Typed Entities

F# Query Studio generates typed entities in background for all registered connections. You can query databases, CSV, Excel, and JSON files using idiomatic F# LINQ query expressions directly under each category without any intermediate setup:

```fsharp
query {
    for u in Connections.sql.Sales.Users do
    where (u.Email.Contains("@") && u.IsActive)
    sortBy u.CreatedAt
    select (u.Name, u.Email)
}
```

Multi-schema PostgreSQL and SQL Server databases are mapped naturally under schema namespaces (e.g. `Connections.sql.Sales.Sales.Orders`).

### Raw SQL Queries

`parameters` is an `(string * obj) list` — always bound through the driver's own parameter API, never concatenated into the SQL text:

```fsharp
Connections.sql
    "Sales"
    "select * from Orders where CreatedAt >= @from"
    [ "from", box (System.DateTime(2026, 1, 1)) ]
```

### What a cell shows

A cell renders the value of its **last expression** — the same value F#
Interactive would echo. You don't call anything special.

| Last expression | Renders as |
|---|---|
| A connector query (`Connections.sql …`, `Connections.csv …`, …) | A grid, with a working **Export CSV** |
| A sequence of records / LINQ query result | A grid; columns are the record fields |
| A sequence of tuples | A grid; columns are `Item1`, `Item2`, … |
| A sequence of `Map`s / dictionaries | A grid; columns are the union of keys |
| A sequence of collections (e.g. `int[] list`, rows of arrays) | A grid; the first inner collection becomes the column headers, the rest are the rows |
| A sequence of scalars | A one-column grid, streamed with **Load more** |
| A single record | A one-row grid |
| A scalar (`int`, `string`, `DateTime`, …) | Plain text |
| A `let` binding or a `printfn` (no value) | Just the printed output |

To force plain structured text, end the cell with `sprintf "%A" value`.

**Export CSV** is available for any of the fully-materialized grid rows above
(everything except the streamed scalar-sequence row).

## Features

- **Notebook execution** — each `.fsqnb` code cell runs against a persistent,
  per-notebook FSI session; stdout / stderr stream live; cancel a running cell
  from the toolbar.
- **Results grid** — virtualized table styled to match VS Code's own tables:
  right-aligned numeric columns, zebra rows, a schema tab, CSV export, and paged
  "load more" for large results. The grid is the default view; FSI's `val … =`
  echo and any `printf` output move to the output's "raw data" presentation.
- **In-cell F# intelligence** — completion, hover, signature help, diagnostics, and
  **Go to Definition (`F12`)** across cells and generated entity types.
- **Categorized Connections & Autocomplete** — Access `Connections.sql.<Conn>`, `Connections.csv.<Conn>`, etc. with full IntelliSense, compile-time type safety, and scoped queries without cross-connection leakage.
- **Connection management** — an Activity Bar tree of connections → schemas → tables →
  columns, lazily loaded; add / edit / remove via guided pickers.
- **Built-in Documentation** — click the `$(book)` icon in the Connections view toolbar
  or the Notebook editor title bar to open the full User Guide side-by-side.

## Connection kinds

| Kind | You provide | Secret |
|---|---|---|
| SQLite | absolute database-file path | — |
| CSV | absolute `.csv` file path | — |
| Excel | absolute `.xlsx` file path | — |
| JSON | absolute `.json` file path | — |
| PostgreSQL | connection string — put `{password}` where the secret goes | optional password, prompted separately, stored in the OS keychain |
| SQL Server | connection string — put `{password}` where the secret goes | optional password, prompted separately, stored in the OS keychain |
| MySQL | connection string — put `{password}` where the secret goes | optional password, prompted separately, stored in the OS keychain |
| REST JSON | base URL | — |

The non-secret parts are saved to `workspace.json` in the workspace. When a
password is set, only its `{password}` placeholder sits in the saved connection
string; the value is fetched from `SecretStorage` at query time over a reverse
RPC to the worker, and never lands in the workspace file or the notebook.

## Commands

| Command | Does |
|---|---|
| **F# Query Studio: Open Documentation** | Opens the comprehensive User Guide in markdown preview (icon `$(book)`). |
| **F# Query Studio: New F# Query Notebook** | Opens a new untitled `.fsqnb` with one empty cell. |
| **F# Query Studio: New F# Query Notebook Here** | Explorer folder context menu — writes and opens a `.fsqnb` in that folder. |
| **F# Query Studio: Restart Notebook Session** | Discards the active notebook's FSI session. |
| **F# Query Studio: Check Worker** | Reports worker health and protocol version; explains what to fix if `dotnet` is missing. |
| **F# Query Studio: Connections: Add / Edit / Remove / Refresh** | Manage the connections tree. |

For detailed examples and tutorials, see [`USER_GUIDE.md`](USER_GUIDE.md).

## Limitations

- One evaluation runs at a time, including across multiple open `.fsqnb` files in
  the same window.
- Requires `dotnet` on `PATH`; there is no self-contained build yet.

## Relationship to Ionide

[Ionide](https://ionide.io) handles F# in ordinary `.fs` / `.fsx` files. It
never sees notebook cells, and this extension never touches `.fs` / `.fsx`.
Install both; they do not conflict.

## Contributing / development

```bash
cd editors/vscode
npm install
npm run build         # dotnet publish the worker + type-check + esbuild bundle
npm test              # unit tests (vitest)
npm run test:vscode   # extension-host tests in a downloaded VS Code
```

Press **F5** in VS Code to launch an Extension Development Host. Set
`FSQS_WORKER_DLL` to use a worker built elsewhere instead of `dist/worker`.

### Building & packaging

| Command | Purpose |
|---|---|
| `npm run build` | Full runnable build: publish the F# worker, then type-check + bundle. |
| `npm run package` | Build, then produce `fs-query-studio.vsix` with the same warning gate + contents assertion CI uses. |
| `npm run doctor` | Read-only preflight for a release (toolchain, extension id, Marketplace icon, `VSCE_PAT`). |
| `npm run publish` | Preflight → package → `vsce publish`. Needs `VSCE_PAT`. |

Flags and detail: [`editors/vscode/scripts/README.md`](https://github.com/Matheusleal/fs-query-studio/blob/main/editors/vscode/scripts/README.md).
Full release runbook: [`docs/vscode-extension-release.md`](https://github.com/Matheusleal/fs-query-studio/blob/main/docs/vscode-extension-release.md).
Design: [`docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md`](https://github.com/Matheusleal/fs-query-studio/blob/main/docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md).

## License

[MIT](LICENSE) © 2026 Matheus Leal.
