# F# Query Studio

[🇺🇸 English](README.md) | [🇧🇷 Português](README_PT.md) | [📖 User Guide (EN)](USER_GUIDE.md) | [📖 Guia do Usuário (PT)](USER_GUIDE_PT.md) | [📊 Benchmark Report](BENCHMARK_REPORT.md)

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
3. Open the **F# Query Studio** view in the Activity Bar and **Add Connection** (or drop files into `data_sources/` and run **Auto-Import Data Sources Directory**). Credentials are stored in the OS keychain via VS Code `SecretStorage` — never written to `workspace.json` or the notebook.
4. Reference a connection from a cell and run it; a tabular result renders as a
   grid with a schema tab and **Export CSV**.

A full hands-on walkthrough is in
[`docs/TUTORIAL.md`](https://github.com/Matheusleal/fs-query-studio/blob/main/docs/TUTORIAL.md) ([versão em português](https://github.com/Matheusleal/fs-query-studio/blob/main/docs/TUTORIAL_PT.md)).

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

### Universal SQL Pushdown (EF-Style)

F# Query Studio translates F# LINQ query expressions directly into optimized native SQL queries across SQLite, PostgreSQL, and SQL Server. Only the requested columns and filtered rows are retrieved from the database:

```fsharp
query {
    for o in Connections.sql.Sales.Orders do
    where (o.TotalAmount >= 100.0f && o.Status = "Completed")
    sortByDescending o.CreatedAt
    take 10
    select {| OrderId = o.Id; Total = o.TotalAmount; Date = o.CreatedAt |}
}
```

This translates directly into:
```sql
SELECT _t0.Id AS OrderId, _t0.TotalAmount AS Total, _t0.CreatedAt AS Date
FROM Orders _t0
WHERE (_t0.TotalAmount >= 100.0 AND _t0.Status = 'Completed')
ORDER BY _t0.CreatedAt DESC
LIMIT 10
```

### Inspecting Generated SQL (`toRawSql`)

To inspect the translated SQL query and parameters before execution:

```fsharp
query {
    for o in Connections.sql.Sales.Orders do
    where (o.TotalAmount >= 100.0f)
    select o
}
|> Connections.toRawSql
```

### High-Performance Staging Engine for CSV & Excel (`.fsqs/cache/engine.db`)

CSV and Excel (`.xlsx`) files are automatically cached into an embedded SQLite staging database under `<workspace>/.fsqs/cache/engine.db`:
- **Single-Transaction Batch Streaming**: Ingests hundreds of thousands of rows at >100,000 rows/sec.
- **Smart MTime/Size Cache Validation**: If the source files haven't changed, queries execute immediately against the cached tables in 0 ms.
- **Automatic Composite Indexes & Multi-Property Joins**: Joins between different files on multiple keys execute natively in SQLite with sub-second performance (~18 ms for 5,000×40 rows) without memory explosions:

```fsharp
query {
    for t in Connections.csv.Transactions do
    join tgt in Connections.excel.Targets.Sheet1 on ((t.StoreId, t.Region) = (tgt.StoreId, tgt.Region))
    where (t.Amount > 1000.0M)
    select {| Tx = t.TransactionId; Store = t.StoreId; Target = tgt.TargetAmount |}
}
```

### Custom Column Mappings & Transformation Formulas
Excel sheets and CSV files frequently contain messy data formats: localized comma-separated decimals (`1.234,56`), currency symbols, percentages, or high-precision decimals that might get truncated. F# Query Studio provides a built-in pre-processing pipeline configurable via **Add/Edit Connection** in VS Code or in `.fsqs/connections.json`:

- **`columnTypes`**: Explicitly override inferred F# types (`decimal`, `double`, `int`, `int64`, `bool`, `DateTime`, `string`). E.g. `"UnitPrice=decimal, Quantity=int"`.
- **`columnTransforms`**: Apply cleaning formulas before staging into SQLite:
  - `replace('from', 'to')`: Replaces characters or substrings (e.g. `replace(',', '.')`).
  - Chained replacements: `replace('.', '').replace(',', '.')` (normalizes localized `"1.234,56"` to `"1234.56"`).
  - `trim`: Strips whitespace.
  - `multiply(factor)` / `divide(divisor)`: Multiplies or divides values by numeric factors (e.g. `divide(100)` for percentages).

See the [User Guide (Custom Column Types & Formula Transformations)](USER_GUIDE.md#custom-column-types--formula-transformations-excel--csv) for in-depth examples and real-world scenarios.

### Auto-Discovery via `data_sources/` Directory

Drop your SQLite databases (`.db`, `.sqlite`), CSV files (`.csv`), or Excel spreadsheets (`.xlsx`) into a `data_sources/` folder in your project root. Click **Auto-Import Sources** in the Connections view toolbar or run **F# Query Studio: Auto-Import Data Sources Directory** to register them all automatically.

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
- **Universal SQL Pushdown** — full query pushdown (`select`, `where`, `join`, `sortBy`, `take`, `skip`, `count`, `sum`) translated into SQL across all relational databases and staged files, avoiding client memory overhead.
- **High-Performance Staging Engine** — on-disk SQLite cache (`.fsqs/cache/engine.db`) with smart timestamp validation and fast multi-key joins for large CSV and Excel files.
- **Pre-Execution SQL Inspection** — inspect exact generated SQL and parameters via `Connections.toRawSql` before touching the database.
- **Auto-Discovery via `data_sources`** — one-click discovery and automatic registration of all databases, CSV, and Excel files in `data_sources/`.
- **Results grid** — virtualized table styled to match VS Code's own tables:
  right-aligned numeric columns, zebra rows, a schema tab, CSV export, and paged
  "load more" for large results.
- **In-cell F# intelligence** — completion, hover, signature help, diagnostics, and
  **Go to Definition (`F12`)** across cells and generated entity types.
- **Categorized Connections & Autocomplete** — Access `Connections.sql.<Conn>`, `Connections.csv.<Conn>`, etc. with full IntelliSense, compile-time type safety, and scoped queries without cross-connection leakage.
- **Connection management** — an Activity Bar tree of connections → schemas → tables →
  columns, lazily loaded; add / edit / remove via guided pickers.
- **Built-in Bilingual Documentation** — click the `$(book)` icon in the Connections view toolbar
  or the Notebook editor title bar to open the full User Guide side-by-side in English or Portuguese.

## Connection kinds

| Kind | You provide | Secret |
|---|---|---|
| SQLite | absolute or workspace-relative database-file path | — |
| CSV | absolute or workspace-relative `.csv` file path | — |
| Excel | absolute or workspace-relative `.xlsx` file path | — |
| JSON | absolute or workspace-relative `.json` file path | — |
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
| **F# Query Studio: Auto-Import Data Sources Directory** | Auto-detects and adds compatible files from `data_sources/` as connections. |
| **F# Query Studio: Sync Data Sources Cache** | Triggers asynchronous background caching of CSV and Excel data sources into `.fsqs/cache/engine.db`. |
| **F# Query Studio: Create Sample Data (SQLite, CSV & XLSX)** | Generates multi-source sample files in `data_sources/` and demo notebooks (`sample_demo.fsqnb` and `sample_demo_pt.fsqnb`). |
| **F# Query Studio: Restart Notebook Session** | Discards the active notebook's FSI session. |
| **F# Query Studio: Check Worker** | Reports worker health and protocol version; explains what to fix if `dotnet` is missing. |
| **F# Query Studio: Connections: Add / Edit / Remove / Refresh** | Manage the connections tree. |
| **F# Query Studio: Initialize Empty Project** | Initializes a `.fsqs/workspace.json` project in the active workspace. |
| **F# Query Studio: Upgrade Project Schema** | Migrates legacy workspace configurations to the latest v2 schema. |
| **F# Query Studio: Show Output Logs** | Opens the extension's output channel showing worker and RPC logs. |

For detailed examples and tutorials, see [`USER_GUIDE.md`](USER_GUIDE.md) ([Guia do Usuário em Português](USER_GUIDE_PT.md)).

## Limitations

- One evaluation runs at a time, including across multiple open `.fsqnb` files in
  the same window.
- Requires `dotnet` on `PATH`; there is no self-contained build yet.

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
