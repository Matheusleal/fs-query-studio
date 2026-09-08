# Tutorial — your first F# Query Notebook

This walks you from a clean VS Code through running F#, wiring up a database
connection, and exporting a result. It takes about fifteen minutes. Every step
is something you type or click; nothing here is theory.

By the end you will have:

- run F# in a notebook cell and understood how the session persists,
- registered a SQLite connection,
- queried it and rendered the result as a grid,
- exported that grid to CSV,
- seen the CSV, Excel, and REST variants and the `Results.*` controls.

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

## 5. Query the connection

Back in the notebook, run:

```fsharp
Connections.sql "chinook" "select * from Track limit 20" []
```

(Use a table name that exists in your database.) The result renders as a grid.

`Connections` is already `open` in every cell — no `#r`, no `open` needed. The
signature is:

```
Connections.sql  <connection name>  <sql text>  <parameters>
```

`parameters` is a list of `name, value` pairs. **Never** build SQL by string
concatenation — pass values as parameters and they are bound through the
driver:

```fsharp
Connections.sql
    "chinook"
    "select Name, Milliseconds from Track where GenreId = @genre order by Milliseconds desc"
    [ "genre", box 1 ]
```

`box` turns the value into `obj` so the list is well-typed when the values have
different types.

> **Tip.** Inside the `"chinook"` string literal, completion offers your
> registered connection names. Inside the SQL string it does not — that text is
> opaque to the analyzer.

---

## 6. Export a result to CSV

Run the query again, but pipe it through `Results.raw`:

```fsharp
Connections.sql "chinook" "select * from Track limit 200" []
|> Results.raw
```

The grid now has an **Export CSV** button. `Results.raw` is what makes export
work: it fully materializes the rows and retains them for as long as the cell's
output is open. A bare `Connections.sql` result still renders as a grid, but
only a preview slice is kept, so there is nothing to export.

Click **Export CSV**, choose a location, done.

`Results.raw` accepts a `ConnectorQueryResult` — that is, the return of
`Connections.sql` / `csv` / `excel` — and nothing else.

---

## 7. `Results.table`, `Results.raw`, `Results.text`

`Results.*` functions all return their argument unchanged, so they drop into a
pipeline without disturbing your logic. Whichever ran last in the cell decides
what the pane shows.

| Call | Give it | Shows |
|---|---|---|
| `Results.raw q` | a `ConnectorQueryResult` | the grid, with working **Export CSV** |
| `Results.table coll` | any collection — records, tuples, `Map`, nested lists, scalars | a grid with columns inferred from the shape |
| `Results.tableWithColumns coll names` | same, plus a header list | `table` with headers overridden by position (needed for tuple sequences — tuples carry no field names) |
| `Results.text value` | anything | the value's `%A` structured form, as plain text |

Examples:

```fsharp
// a computed collection, not from a connector — tuples need explicit headers
Results.tableWithColumns [ "alice", 3; "bob", 7; "carol", 1 ] [ "user"; "score" ]
```

```fsharp
// force the text view for something that would otherwise render as a grid
Results.text (Connections.sql "chinook" "select * from Album limit 3" [])
```

---

## 8. CSV and Excel connections

Add a connection of kind `Csv` (or `Excel`) the same way — the prompt asks for
an **absolute file path** to the `.csv` / `.xlsx`. Then:

```fsharp
Connections.csv "my-csv" |> Results.raw
```

```fsharp
// Excel takes an optional row cap
Connections.excel "my-xlsx" (Some 500) |> Results.raw
// all rows:
Connections.excel "my-xlsx" None |> Results.raw
```

Both return the same `ConnectorQueryResult` shape as `Connections.sql`, so
`Results.raw` and **Export CSV** work identically.

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
|> Results.raw
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

To get a grid out of JSON, project it into records yourself and hand that to
`Results.table`:

```fsharp
[ for el in payload.EnumerateArray() ->
    {| id = el.GetProperty("id").GetInt32()
       total = el.GetProperty("total").GetDecimal() |} ]
|> Results.table
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
actions, no rename across cells. This intelligence comes from the extension's
worker, not from Ionide.

[Ionide](https://ionide.io) still owns ordinary `.fs` / `.fsx` files; it never
sees notebook cells and this extension never touches `.fs` / `.fsx`. Running
both is fine.

---

## 12. Troubleshooting

| Symptom | Do this |
|---|---|
| Cells do nothing / "worker" errors | **F# Query Studio: Check Worker**. Usually `dotnet` 10 is missing from `PATH`; install it and restart VS Code. |
| "The worker exited before responding" | The FSI session crashed and was lost. It restarts on your next run; re-run the cells you need. |
| `Connections has not been configured` | You referenced a connection name that is not registered, or the workspace has no `workspace.json` yet — add the connection in the sidebar. |
| **Export CSV** missing on a grid | The cell did not go through `Results.raw` / `Results.table`. Pipe the result through one of them. |
| A redefinition or new `open` "does not take" | **Restart Notebook Session** — FSI keeps the earlier definition for the session's lifetime. |
| A cell seems stuck | Only one evaluation runs at a time across the whole window. Stop the running cell (the ▪ button) or wait for it. |

---

## Where to go next

- [`../README.md`](../README.md) — project overview and repo layout.
- [`../editors/vscode/README.md`](../editors/vscode/README.md) — the full
  feature and API reference.
- [`README.md`](README.md) — index of everything under `docs/`.
