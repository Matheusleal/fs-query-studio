# F# Query Studio

[🇺🇸 English](README.md) | [🇧🇷 Português](README_PT.md)

Write, analyze, and run F# query scripts against SQL databases, local files, and
REST APIs — as VS Code notebooks.

F# Query Studio is a **VS Code extension**. You open a `.fsqnb` file as an *F#
Query Notebook*; each code cell runs F# against a persistent FSI session hosted
in a bundled .NET worker process. A controlled API (`Connections.*`) reaches
databases and files you register in the sidebar, and results render as a
sortable grid with schema view and CSV export.

> **Status:** `0.4.0`, ready for distribution. High-performance multi-source query & exploration
> studio featuring full SQL pushdown translation, asynchronous SQLite disk caching with composite B-tree indexes,
> multi-property join optimizations without memory explosions, automated `data_sources/` discovery,
> raw SQL preview (`toRawSql`), verified sub-millisecond benchmarks, and 100% test coverage.
> See [Release v0.4.0](https://github.com/Matheusleal/fs-query-studio/releases/tag/v0.4.0) and [`docs/vscode-extension-release.md`](docs/vscode-extension-release.md).

---

## Requirements

- **.NET 10 SDK** on your `PATH` — `dotnet --version` must report `10` or
  higher. The extension spawns a framework-dependent F# worker with it.
- **VS Code 1.90** or newer.

Windows, macOS, and Linux are all supported. One worker process runs per VS Code
window.

## Install

No Marketplace listing yet. Build the VSIX and install it:

```bash
cd editors/vscode
npm install
npm run package          # produces editors/vscode/fs-query-studio.vsix
code --install-extension fs-query-studio.vsix
```

CI also uploads a `fs-query-studio-vsix` artifact on every push to `main` /
`main-vscode` that touches the extension or the worker — download that instead
of building locally.

## Quick start

1. **New notebook** — run **F# Query Studio: New F# Query Notebook** from the
   Command Palette, or create any file ending in `.fsqnb` and open it.
2. **Run F#** — type into a code cell and run it. `let` bindings persist to
   later cells (one FSI session per notebook). **F# Query Studio: Restart
   Notebook Session** clears it.
3. **Add or auto-import connections** — drop `.csv`, `.xlsx`, or `.db` files into the `data_sources/` folder and run **F# Query Studio: Auto-Import Data Sources Directory**, or use **Add Connection** in the Activity Bar sidebar. Tabular files are immediately and asynchronously pre-staged into a persistent SQLite disk cache (`.fsqs/cache/engine.db`) with automatic B-tree indexing.
4. **Query with Full SQL Pushdown & LINQ** — queries are translated into database-side SQL (`SELECT`, `WHERE`, `JOIN`, `ORDER BY`, `LIMIT`, `SKIP`, `COUNT`, `SUM`) rather than pulling raw tables into memory:
   ```fsharp
   query {
       for u in Connections.sql.MyConn.Users do
       where (u.IsActive && u.Age >= 21)
       sortBy u.LastName
       select {| Name = u.FirstName + " " + u.LastName; Email = u.Email |}
   }
   ```
5. **Inspect Generated SQL** — preview the exact database query and parameters without executing round-trips:
   ```fsharp
   printfn "%s" (Connections.toRawSql myQuery)
   ```
   Or run raw SQL directly: `Connections.sql.MyConn.rawQuery "select * from Orders" []`.
   Results render in a virtualized interactive grid with schema view and **Export CSV**.

Full walkthrough: **[`docs/TUTORIAL.md`](docs/TUTORIAL.md)**.
Comprehensive User Guide: **[`editors/vscode/docs/USER_GUIDE.md`](editors/vscode/docs/USER_GUIDE.md)** (or click `$(book)` in VS Code).

## What you get

- **Notebook execution** — every `.fsqnb` code cell runs against a persistent,
  per-notebook FSI session; stdout / stderr stream live; cancel a running cell
  from the toolbar.
- **Full Database Query Pushdown** — Entity Framework-style query translation for relational databases and flat files. Translates projections (`select`), filters (`where`), joins (`join`), multi-property joins (`on ((a.p1, a.p2) = (b.p1, b.p2))`), sorting, slicing (`take`/`skip`), and aggregations (`count`/`sum`) into optimized native SQL queries.
- **Asynchronous SQLite Disk Cache & Fast File Joins** — Flat files (CSV and Excel `.xlsx`) are pre-staged in the background into a persistent on-disk SQLite database (`.fsqs/cache/engine.db`). Staging validates file modification times (`mtime`) and byte sizes to skip re-importing unchanged files. Automatic composite B-tree indexes prevent Cartesian products and high RAM consumption when joining large files (200,000+ rows).
- **Accurate Tabular Type Inference & Formulas** — Excel and CSV schemas detect genuine column types (ints, decimals, dates, bools) in both tree view and entities. Configure custom column type overrides (`columnTypes`) and formula replacements (`columnTransforms`, e.g. `replace('.', '').replace(',', '.')` or `trim`) while preserving high-precision decimals. Detailed instructions and real-world examples: [Custom Column Types & Formulas](editors/vscode/docs/USER_GUIDE.md#custom-column-types--formula-transformations-excel--csv).
- **SQL Preview (`toRawSql`)** — inspect the raw parameterized SQL query generated by LINQ pushdown at any time using `ResultKind.toRawSql query` or `Connections.toRawSql query`.
- **Automatic Folder Discovery (`data_sources/`)** — drop `.csv`, `.xlsx`, `.db`, or `.sqlite` files into your workspace's `data_sources/` folder and click **Auto-Import Data Sources** to detect, configure, and generate types automatically.
- **Strongly-typed LINQ queries** — automatic background entity generation for
  databases (PostgreSQL, SQL Server, MySQL, SQLite) and flat files (CSV, Excel, JSON).
- **Visual Sync Status** — animated status bar feedback (`$(sync~spin) Syncing data sources...` ➔ `$(database) Data sources ready`) indicates background caching progress and readiness in real time.
- **Results grid** — virtualized table, schema view, CSV export, paged loading
  for large results.
- **In-cell F# intelligence** — completion, hover, signature help, diagnostics, and
  **Go to Definition (`F12`)** across cells and generated entity types, plus
  type-filtered connection name autocomplete.
- **Connection management** — an Activity Bar tree of connections → schemas → tables →
  columns, lazily loaded; add / edit / remove via guided pickers.
- **Built-in Documentation** — click the `$(book)` icon in the Connections view toolbar
  or the Notebook editor title bar to open the User Guide directly in VS Code.

Detail and limits: **[`editors/vscode/README.md`](editors/vscode/README.md)**.

## Performance Benchmarks & Reliability

F# Query Studio is engineered for low latency, deterministic memory consumption, and zero-flicker UI updates:

- **Sub-millisecond queries**: Reused ADO.NET connection pools execute queries in `0.05 ms - 0.20 ms` with negligible CPU impact and zero Gen 1/2 garbage collections.
- **Streaming Volume Scaling**: Seamlessly queries datasets from 100 to 50,000+ rows with lazy cursor streaming (`Results.fs`), maintaining a flat `1.5 KB` allocation baseline without full in-memory materialization.
- **Precompiled LINQ & Reflection Cache**: Typed record mapping (`Connections.query<'T>`) and LINQ queries (`query { ... }`) execute 1,000 records in `~2.0 ms` using compiled constructor delegates.
- **High-Throughput File Queries**: Ingests and queries CSV feeds at `~500,000 rows/second`.
- **100% UI Element Verification**: Every UI component (virtualized table grid, sticky headers, cell badging for nulls and numbers, segmented schema tabs, continuation pagination, notification banners, and Connections Explorer tree items) is verified by automated test suites.

Read the full benchmark report: **[`docs/BENCHMARK_REPORT.md`](docs/BENCHMARK_REPORT.md)**.

## Repository layout

| Path | What it is |
|---|---|
| `src/FSharpQueryStudio.*` | The F# worker and its libraries (.NET 10) — FSI hosting, connectors, results projection, analysis, protocol, secret storage. |
| `editors/vscode/` | The VS Code extension (TypeScript) — notebook controller, results-grid renderer, connections view, worker client. |
| `tests/` | .NET test projects, one per `src` library, plus integration tests. |
| `editors/vscode/test/` | Extension unit tests (vitest) and extension-host tests (`@vscode/test-electron`). |
| `docs/` | Documentation — see [`docs/README.md`](docs/README.md) for the index. |

The extension bundles the worker (`dist/worker/**`) at package time; at runtime
the user still needs `dotnet` on `PATH`. There is no self-contained build yet.

## Development

```bash
cd editors/vscode
npm install
npm run build          # dotnet publish the worker + tsc type-check + esbuild bundle
npm test               # extension unit tests (vitest)
npm run test:vscode    # extension-host tests in a downloaded VS Code
```

Press **F5** in VS Code to launch an Extension Development Host. Set
`FSQS_WORKER_DLL` to point at a worker built elsewhere instead of
`dist/worker`.

The .NET side builds with the standard SDK:

```bash
dotnet build FSharpQueryStudio.slnx
dotnet test  FSharpQueryStudio.slnx
```

Build / package / publish mechanics live in
[`editors/vscode/scripts/README.md`](editors/vscode/scripts/README.md); the
release process is in
[`docs/vscode-extension-release.md`](docs/vscode-extension-release.md).

## License

[MIT](LICENSE) © 2026 Matheus Leal.
