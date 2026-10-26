# F# Query Studio

Write, analyze, and run F# query scripts against SQL databases, local files, and
REST APIs — as VS Code notebooks.

F# Query Studio is a **VS Code extension**. You open a `.fsqnb` file as an *F#
Query Notebook*; each code cell runs F# against a persistent FSI session hosted
in a bundled .NET worker process. A controlled API (`Connections.*`) reaches
databases and files you register in the sidebar, and results render as a
sortable grid with schema view and CSV export.

> **Status:** `0.1.0`, pre-Marketplace. Feature-complete for the
> query-and-execution workflow; install from a locally built `.vsix` for now.
> See [`docs/vscode-extension-release.md`](docs/vscode-extension-release.md).

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
3. **Add a connection** — open the **F# Query Studio** view in the Activity Bar
   and **Add Connection**: SQLite / CSV / Excel (file path), SQL Server /
   PostgreSQL (connection string), or REST JSON (URL). Secrets go to the OS
   keychain, never to disk in the workspace.
4. **Query it** — `Connections.sql "my-conn" "select * from Orders" []`, run the
   cell, and the result renders as a grid with a schema tab and **Export CSV**.

Full walkthrough: **[`docs/TUTORIAL.md`](docs/TUTORIAL.md)**.

## What you get

- **Notebook execution** — every `.fsqnb` code cell runs against a persistent,
  per-notebook FSI session; stdout / stderr stream live; cancel a running cell
  from the toolbar.
- **Query API** — `Connections.sql` / `csv` / `excel` / `rest` reach connections
  by name through a parameterized, injection-safe surface. A cell renders the
  value of its last expression — collections become a grid, connector queries add
  **Export CSV**, scalars print as text; no special call needed.
- **Results grid** — virtualized table, schema view, CSV export, paged loading
  for large results.
- **In-cell F# intelligence** — completion, hover, signature help, and
  diagnostics for notebook cells, including connection-name completion inside
  `Connections.*` string literals.
- **Connection management** — an Activity Bar tree of connections → tables →
  columns, lazily loaded; add / edit / remove via guided pickers.

Detail and limits: **[`editors/vscode/README.md`](editors/vscode/README.md)**.

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

## Relationship to Ionide

[Ionide](https://ionide.io) handles F# in ordinary `.fs` / `.fsx` files. It
never sees notebook cells, and this extension never touches `.fs` / `.fsx`.
Install both; they do not conflict.

## License

[MIT](LICENSE) © 2026 Matheus Leal.
