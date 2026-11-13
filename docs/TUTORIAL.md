# Tutorial — your first F# Query Notebook

[🇺🇸 English](TUTORIAL.md) | [🇧🇷 Português](TUTORIAL_PT.md)

This walks you from a clean VS Code through running F#, wiring up a database
connection, and exporting a result. It takes about fifteen minutes. Every step
is something you type or click; nothing here is theory.

By the end you will have:

- run F# in a notebook cell and understood how the session persists,
- registered a SQLite connection,
- queried it and rendered the result as a grid,
- exported that grid to CSV,
- seen the CSV, Excel, and REST connection variants.

---

## 0. Prerequisites

**The .NET 10 SDK must be on your `PATH`.** Check:

```bash
dotnet --version
```

You need `10.` or higher. If the command is missing or older, install the .NET
10 SDK from <https://dotnet.microsoft.com/download> and restart VS Code so it
picks up the new `PATH`.

**Install the extension.** There is no Marketplace listing yet, so build the
VSIX once:

```bash
cd editors/vscode
npm install
npm run package
code --install-extension fs-query-studio.vsix
```

Reload VS Code when it prompts.

> If anything below misbehaves, run **F# Query Studio: Check Worker** from the
> Command Palette (`Ctrl+Shift+P` / `Cmd+Shift+P`). It reports whether the
> worker started and what to fix — most often "`dotnet` 10 not found on PATH".

---

## 1. Create a notebook and run a cell

Open the Command Palette and run **F# Query Studio: New F# Query Notebook**. An
untitled notebook opens with one empty code cell.

You can also:
- use *File > New File…* and pick **F# Query Notebook**, or
- right-click a folder in the Explorer and choose **New F# Query Notebook Here**
  (this one writes a real `.fsqnb` file straight away), or
- create any file whose name ends in `.fsqnb` and open it.

Type this into the cell and run it (`Ctrl+Enter`, or the ▶ button):

```fsharp
printfn "hello from fsi"
1 + 1
```

You get two things in the cell output:

- the text `hello from fsi` — anything written to stdout/stderr,
- the value `2` — the cell's last expression, rendered by the results view.

While a cell runs, its output streams in live and the ▶ button becomes a stop
button. One cell runs at a time, across every open `.fsqnb` in the window.

---

## 2. The session persists across cells

Add a second cell (`Esc` then `B`, or the `+ Code` button) and run:

```fsharp
let greeting = "notebook"
```

Add a third cell and run:

```fsharp
printfn "hello, %s" greeting
```

It prints `hello, notebook`. The `let` binding from the second cell is still in
scope — the whole notebook is **one persistent FSI session**, top to bottom.

That also means state accumulates. When you want a clean slate — a redefined
type, a fresh `open`, a mutable you have been poking at — run **F# Query Studio:
Restart Notebook Session** from the Command Palette. The next cell you run starts
from nothing.

---

## 3. Render a grid

Any cell whose last expression is a sequence of records renders as a table. Run:

```fsharp
[ for i in 1..5 -> {| n = i; square = i * i; even = (i % 2 = 0) |} ]
```

The output is a grid: one column per record field, numeric columns
right-aligned, a **Schema** tab next to **Data**, and a row count. For a result
this small there is nothing to page; large results load a page at a time as you
scroll.

Notice the output's presentation picker (the `⋯` / "Change presentation" on the
output). The grid is the default; FSI's own `val it = …` echo is tucked into the
**raw data** view. You rarely need it.

---

## 4. Register a SQLite connection

You need a SQLite database file to point at. Use any `.sqlite` / `.db` file you
already have, or download a sample — the classic **Chinook** SQLite database is
a good one. Note its **absolute path**.

Now open the **F# Query Studio** view: click its icon in the Activity Bar (the
left-hand strip). The view shows *"No connections registered yet."*

Click **Add Connection** (the `+` in the view's title bar). Answer the prompts:

1. **Kind** — choose `Sqlite`.
2. **Connection name** — `chinook` (this is the name you use from cells).
3. **Absolute file path** — the full path to your `.db` file.

The connection appears in the tree. Expand it: it lazily loads tables, and each
table expands to its columns. Nothing is queried until you expand a node.

---

## 5. Query the connection with `fsql` and Full SQL Pushdown

Back in the notebook, you can write strongly-typed queries using **`fsql`** (F# Query Language). Entities are generated automatically in the background under `Connections.sql.<ConnectionName>`:

```fsharp
fsql {
    for t in Connections.sql.Chinook.Track do
    where (t.Milliseconds > 300000)
    sortByDescending t.Milliseconds
    take 20
    select {| Title = t.Name; DurationMs = t.Milliseconds; UnitPrice = t.UnitPrice |}
}
```

### Full SQL Pushdown
Notice what happened:
- Only the 3 selected fields (`Name`, `Milliseconds`, `UnitPrice`) are fetched from the database (`SELECT _t0.Name AS Title, ...`).
- Filtering (`WHERE`), sorting (`ORDER BY`), and limits (`LIMIT 20` or `OFFSET / FETCH`) are executed **inside the database engine**, avoiding pulling thousands of rows into memory.

### Advanced DQL: Correlated Subqueries & Exists
`fsql` supports advanced functional operations without raw SQL strings:

```fsharp
// Correlated EXISTS test
fsql {
    for a in Connections.sql.Chinook.Album do
    whereExists (fsql { for t in Connections.sql.Chinook.Track do where (t.AlbumId = a.AlbumId && t.Milliseconds > 600000) })
    select {| AlbumId = a.AlbumId; Title = a.Title |}
}
```

### Inspect the Generated SQL with `toRawQuery`
Curious about what query is being compiled? Preview the exact, indented, multi-dialect SQL with bound parameters at any time:

```fsharp
let q =
    fsql {
        for t in Connections.sql.Chinook.Track do
        where (t.Milliseconds > 300000)
        select t
    }

printfn "%s" (toRawQuery q)
```

### Traditional Raw Parameterized SQL
You can also run traditional parameterized SQL directly when needed:

```fsharp
Connections.sql.Chinook.rawQuery
    "select Name, Milliseconds from Track where GenreId = @genre order by Milliseconds desc limit 20"
    [ "genre", box 1 ]
```

---

## 6. Export a result to CSV

Run a cell whose **last expression** is a connector query:

```fsharp
Connections.sql "chinook" "select * from Track limit 200" []
```

The grid has an **Export CSV** button. A connector-query tail value is fully
materialized and kept per notebook: **Export CSV** writes whichever
materialized-grid cell ran most recently — the whole result set, not just the
page on screen. Running another grid cell replaces what **Export CSV** will
write.

Click **Export CSV**, choose a location, done.

**Export CSV** is available whenever the cell's last expression renders as a grid
backed by fully-materialized rows — a connector query (`Connections.sql` / `csv`
/ `excel`) or any computed sequence of records, tuples, or maps. Scalars, plain
text, and lazily-streamed scalar sequences have nothing to export.

---

## 7. What a cell shows

A cell renders the value of its **last expression** — the same value F#
Interactive would echo. You don't call anything special.

| Last expression | Renders as |
|---|---|
| A connector query (`Connections.sql …`, `Connections.csv …`, …) | A grid, with a working **Export CSV** |
| A sequence of records | A grid; columns are the record fields |
| A sequence of tuples | A grid; columns are `Item1`, `Item2`, … |
| A sequence of `Map`s / dictionaries | A grid; columns are the union of keys |
| A sequence of collections (e.g. `int[] list`, rows of arrays) | A grid; the first inner collection becomes the column headers, the rest are the rows |
| A sequence of scalars | A one-column grid, streamed with **Load more** |
| A single record | A one-row grid |
| A scalar (`int`, `string`, `DateTime`, …) | Plain text |
| A `let` binding or a `printfn` (no value) | Just the printed output |

To force plain structured text, end the cell with `sprintf "%A" value`.

Examples:

```fsharp
// a computed collection — a tuple list renders with Item1 / Item2 headers
[ "alice", 3; "bob", 7; "carol", 1 ]
```

A tuple list renders with positional `Item1` / `Item2` headers — project it to a
record if you want named columns.

```fsharp
// force the text view for something that would otherwise render as a grid
sprintf "%A" (Connections.sql "chinook" "select * from Album limit 3" [])
```

---

## 8. CSV and Excel Connections & Persistent Disk Caching

### Auto-Import via `data_sources/`
The easiest way to register flat files is to drop them directly into your workspace's `data_sources/` folder:
- Place any `.csv`, `.xlsx`, `.db`, or `.sqlite` files in `data_sources/`.
- Run **F# Query Studio: Auto-Import Data Sources Directory** from the Command Palette (or click the button in the Connections sidebar).
- Files are detected, mapped with typed record schemas, and immediately pre-staged in background into a local SQLite disk cache (`.fsqs/cache/engine.db`).

### Querying Flat Files with LINQ
Query your files directly using typed accessors:

```fsharp
query {
    for tx in Connections.csv.SalesFeed do
    where (tx.Quantity > 2)
    sortByDescending tx.TotalAmount
    select (tx.OrderId, tx.Product, tx.TotalAmount)
}
```

```fsharp
// Excel sheets are exposed individually with sheet-specific records
query {
    for row in Connections.excel.RegionalTargets.RegionalTargets do
    where (row.GrowthRate >= 10.0M)
    select row
}
```

### Fast Multi-Property Joins between Files
Because tabular files are cached in SQLite with composite B-tree indexes, you can join multiple files on multiple properties without Cartesian products:

```fsharp
query {
    for s in Connections.csv.SalesFeed do
    join c in Connections.csv.CustomersFeed on (s.Customer = c.CustomerName)
    select {| OrderId = s.OrderId; Customer = s.Customer; Tier = c.LoyaltyTier |}
}
```

### Custom Column Mappings & Transformation Formulas
When importing messy CSVs or Excel spreadsheets with localized currency (`R$ 1.234,56`), custom percentage scales, or high-precision decimals, customize the connection either in the **Connections sidebar** (via *Add Connection* / *Edit Connection*) or in `.fsqs/connections.json`:

- **`columnTypes`**: Force target types like `decimal`, `double`, `int`, `int64`, `bool`, `DateTime`, or `string`. E.g.: `"TotalAmount=decimal, TaxRate=double"`.
- **`columnTransforms`**: Apply cleaning formulas:
  - `replace('.', '').replace(',', '.')`: Normalize Brazilian/European comma decimals to standard dots.
  - `replace('$', '').trim`: Strip currency symbols.
  - `multiply(100)` or `divide(100)`: Scale numeric values.

```json
{
  "name": "SalesReport",
  "kind": "Excel",
  "configuration": {
    "filePath": "data_sources/sales.xlsx",
    "sheetName": "Sheet1",
    "columnTypes": "Revenue=decimal, Units=int",
    "columnTransforms": "Revenue=replace('$', '').replace(',', '').trim"
  }
}
```

---

## 9. SQL Server and PostgreSQL connections

Choose kind `SqlServer` or `PostgreSql`. You are asked for:

1. **Connection string** — write it with `{password}` where the secret belongs,
   e.g. `Server=localhost;Database=Sales;User Id=app;Password={password};`.
2. **Password (optional)** — typed masked. It is stored in the **OS keychain**
   via VS Code `SecretStorage`.

Only the connection string with the literal `{password}` placeholder is saved to
`workspace.json`. The real secret is fetched from the keychain at query time and
never written to the workspace file or the notebook. Query them exactly like
SQLite:

```fsharp
Connections.sql "Sales" "select top 100 * from Orders" []
```

---

## 10. A REST JSON connection

Add a connection of kind `REST JSON`; the prompt asks for a **base URL**.
`Connections.rest` returns a `System.Text.Json.JsonElement` from a
`Task`, so a cell awaits it:

```fsharp
open System.Threading

let payload =
    Connections.rest
        "my-api"
        (RestRequestOptions.Get "https://api.example.com/v1/orders")
        CancellationToken.None
    |> fun t -> t.Result

payload
```

`RestRequestOptions.Get` builds a plain GET; the record also has `Method`,
`Headers`, `QueryParams`, and `Body` fields if you need to set them:

```fsharp
{ RestRequestOptions.Get "https://api.example.com/v1/orders" with
    QueryParams = [ "status", "open" ]
    Headers = [ "Accept", "application/json" ] }
```

To get a grid out of JSON, project it into records yourself and make that list
the cell's last expression:

```fsharp
[ for el in payload.EnumerateArray() ->
    {| id = el.GetProperty("id").GetInt32()
       total = el.GetProperty("total").GetDecimal() |} ]
```

---

## 11. In-cell F# help

As you type in a cell you get:

- **completion** — members, values in scope, and your connection names inside
  `Connections.*` string literals,
- **hover** — types and doc comments,
- **signature help** — parameter hints while typing a call,
- **diagnostics** — red squiggles for errors, per cell.

That is the whole list. There is no find-all-references, no formatting, no code
actions, no rename across cells. This intelligence is provided directly by the extension's
built-in language service worker.

---

## 12. Troubleshooting

| Symptom | Do this |
|---|---|
| Cells do nothing / "worker" errors | **F# Query Studio: Check Worker**. Usually `dotnet` 10 is missing from `PATH`; install it and restart VS Code. |
| "The worker exited before responding" | The FSI session crashed and was lost. It restarts on your next run; re-run the cells you need. |
| `Connections has not been configured` | You referenced a connection name that is not registered, or the workspace has no `workspace.json` yet — add the connection in the sidebar. |
| **Export CSV** reports nothing to export | The cell's last expression didn't produce a materialized grid — it was a scalar, plain text, or a streamed scalar sequence. Make the last line a connector query or a computed sequence of records / tuples / maps. |
| A redefinition or new `open` "does not take" | **Restart Notebook Session** — FSI keeps the earlier definition for the session's lifetime. |
| A cell seems stuck | Only one evaluation runs at a time across the whole window. Stop the running cell (the ▪ button) or wait for it. |
| A cell from an older notebook fails with `error FS0039: The value, namespace, type or module 'Results' is not defined` | The `Results.raw` / `table` / `tableWithColumns` / `text` API was removed. Delete the `\|> Results.…` pipe (or the `Results.…` call) — the cell's last expression now renders on its own. |

---

## Where to go next

- [`../README.md`](../README.md) — project overview and repo layout.
- [`../editors/vscode/README.md`](../editors/vscode/README.md) — the full
  feature and API reference.
- [`README.md`](README.md) — index of everything under `docs/`.
