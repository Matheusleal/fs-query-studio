# F# Query Studio — Comprehensive User Guide

[🇧🇷 Versão em Português](USER_GUIDE_PT.md) | [🇺🇸 English Version](USER_GUIDE.md)

**F# Query Studio** is a professional Visual Studio Code extension that turns your editor into a high-performance, interactive data query and exploration studio powered by the **F#** programming language.

Write type-safe, expressive queries with compile-time verification using **F# Query Notebooks (`.fsqnb`)**, connecting relational databases (**PostgreSQL, SQL Server, MySQL, SQLite**), flat files (**CSV, Excel/XLSX, JSON**), and **REST APIs**.

---

## Official Documentation & Useful Links

- 📘 **Official F# Documentation**: [Microsoft Learn — F# Overview](https://learn.microsoft.com/dotnet/fsharp/)
- 🔍 **F# Query Expressions (LINQ)**: [Microsoft Learn — Query Expressions Reference](https://learn.microsoft.com/dotnet/fsharp/language-reference/query-expressions)
- ⚡ **F# Interactive (FSI)**: [Microsoft Learn — F# Interactive Guide](https://learn.microsoft.com/dotnet/fsharp/tools/fsharp-interactive/)
- 🗄️ **SQLProvider Reference**: [SQLProvider Documentation](https://fsprojects.github.io/SQLProvider/)
- 📦 **GitHub Repository & Issues**: [fs-query-studio on GitHub](https://github.com/Matheusleal/fs-query-studio)

---

## Table of Contents

1. [Requirements & Installation](#1-requirements--installation)
2. [User Interface & Quick Access](#2-user-interface--quick-access)
3. [Quick Start: Generating Sample Data (SQLite & CSV)](#3-quick-start-generating-sample-data-sqlite--csv)
4. [Managing Connections (Sidebar Explorer)](#4-managing-connections-sidebar-explorer)
5. [Smart Connection Autocomplete](#5-smart-connection-autocomplete)
6. [LINQ Queries & Background Entity Provider](#6-linq-queries--background-entity-provider)
   - [Basic Filtering & Projection](#basic-filtering--projection)
   - [Full SQL Pushdown Translation](#full-sql-pushdown-translation)
   - [Multi-Property Joins without Memory Explosions](#multi-property-joins-without-memory-explosions)
   - [Inspecting Generated SQL (toRawSql)](#inspecting-generated-sql-torawsql)
   - [Aggregations & Grouping](#aggregations--grouping)
   - [Persistent SQLite Disk Cache for CSV & Excel](#persistent-sqlite-disk-cache-for-csv--excel)
   - [Automatic Discovery via data_sources Directory](#automatic-discovery-via-data_sources-directory)
7. [Traditional Parameterized SQL (Raw SQL)](#7-traditional-parameterized-sql-raw-sql)
8. [Interactive Results Grid](#8-interactive-results-grid)
9. [Language Features & Go to Definition (F12)](#9-language-features--go-to-definition-f12)
10. [Notebook Lifecycle & FSI Session Management](#10-notebook-lifecycle--fsi-session-management)
11. [Troubleshooting & Diagnostics](#11-troubleshooting--diagnostics)
12. [Performance Benchmarks & UI Reliability](#12-performance-benchmarks--ui-reliability)

---

## 1. Requirements & Installation

- **.NET 10 SDK** installed and accessible in your system `PATH` (`dotnet --version` ≥ 10.0). F# Query Studio runs a native backend worker managing interactive FSI execution and language intelligence (FSAC).
- **VS Code 1.90** or higher.
- Cross-platform support: **Windows, Linux, and macOS**.

### Installing the VSIX Package

To install manually from a local `.vsix` file:
```bash
code --install-extension fs-query-studio.vsix --force
```

---

## 2. User Interface & Quick Access

F# Query Studio integrates into three key areas of VS Code:

1. **Activity Bar**: Look for the F# Query Studio logo on the left sidebar. It displays registered connections, schemas, tables, and column metadata.
2. **Title Icons**:
   - `$(book)` **Documentation**: Opens this guide right next to your active code (available on both the Connections view title and the notebook editor header).
   - `$(database)` **Create Sample Data**: Automatically creates sample SQLite and CSV databases along with a demo notebook in 1 click.
   - `$(add)` **Add Connection**: Prompts you step-by-step to register a new database, file, or API.
   - `$(refresh)` **Refresh**: Reloads schema discovery and regenerates entity types.
3. **Command Palette (`Ctrl+Shift+P` / `Cmd+Shift+P`)**:
   - `F# Query Studio: Create Sample Data (SQLite & CSV)`
   - `F# Query Studio: Open Documentation`
   - `F# Query Studio: New F# Query Notebook`
   - `F# Query Studio: Restart Notebook Session`
   - `F# Query Studio: Check Worker`

---

## 3. Quick Start: Generating Sample Data (SQLite & CSV)

Want to try queries immediately without configuring an external database?

Click the **`Create Sample Data`** button (`$(database)`) on the Connections panel header, or run `F# Query Studio: Create Sample Data (SQLite & CSV)` from the Command Palette.

### What Gets Created:
1. **`sample_store.db` (SQLite)**: A full relational database containing 5 populated tables:
   - `Categories` (`Id`, `Name`, `Description`)
   - `Products` (`Id`, `CategoryId`, `Name`, `UnitPrice`, `StockQuantity`)
   - `Customers` (`Id`, `FullName`, `Email`, `Country`, `City`, `CreatedAt`)
   - `Orders` (`Id`, `CustomerId`, `OrderDate`, `Status`, `TotalAmount`)
   - `OrderItems` (`Id`, `OrderId`, `ProductId`, `Quantity`, `UnitPrice`)
2. **`sales_feed.csv`**: A CSV feed with real-world sales transactions:
   - `TransactionId`, `Date`, `StoreCity`, `Category`, `Product`, `Quantity`, `UnitPrice`, `Total`
3. **`sample_demo.fsqnb` & `sample_demo_pt.fsqnb`**: Interactive, richly formatted notebooks ready with LINQ queries, joins, aggregations, CSV queries, and raw parameterized SQL in English and Portuguese.
4. Both connections (`SampleStore` and `SalesFeed`) are automatically registered in your workspace and entity types are generated in the background!

---

## 4. Managing Connections (Sidebar Explorer)

### Supported Data Sources

| Type | Description | Configuration Example |
|---|---|---|
| **SQLite** | Local file database | File path: `sample_store.db` or `C:\data\app.db` |
| **PostgreSQL** | Multi-schema relational database (`public`, custom schemas, views) | `Host=localhost;Database=mydb;Username=postgres;Password={password}` |
| **SQL Server** | Enterprise relational database (`dbo`, custom schemas, views) | `Server=localhost;Database=master;User Id=sa;Password={password};TrustServerCertificate=True` |
| **MySQL** | Relational database (catalogs, tables, views) | `Server=localhost;Database=shop;Uid=root;Pwd={password};` |
| **CSV** | Delimited text files (comma, semicolon, tab) | File path: `sales_feed.csv` |
| **Excel (XLSX)** | Excel workbooks with multiple sheets | File path: `finances.xlsx` |
| **JSON** | Local JSON files (objects or array of objects) | File path: `catalog.json` |
| **REST API** | HTTP/HTTPS endpoints returning JSON | Base URL: `https://api.github.com` |

### Security & Password Protection

Passwords are **never stored in plaintext** on disk. When you register a connection with `{password}`, credentials are encrypted and stored in your operating system's native secure credential vault via VS Code's `SecretStorage`.

---

## 5. Smart Connection Autocomplete & Type Badges

When editing notebook cells, F# Query Studio provides **context-aware IntelliSense** organized cleanly by connection category:

- `Connections.sql.<ConnName>` ➔ Direct access to relational database tables (SQLite, PostgreSQL, SQL Server, MySQL) and scoped `.rawQuery`.
- `Connections.csv.<ConnName>` ➔ Direct access to strongly-typed CSV rows and `.Raw()`.
- `Connections.excel.<ConnName>` ➔ Direct access to strongly-typed Excel sheets and `.Raw()`.
- `Connections.rest.<ConnName>` ➔ Direct access to REST API endpoints and `.SendAsync()`.

### Right-Aligned Type Badges in Autocomplete
The suggestion dropdown displays the item's underlying type or kind right-aligned for instant recognition:
- **Record Fields & Properties**: Shows the concrete type (`string`, `int`, `decimal`, `bool`, `DateTime`, etc.).
- **Methods & Functions**: Displays `method`.
- **Connections**: Displays the connector kind (`sql`, `csv`, `excel`, `rest`).

### Live Entity Mapping Status
When connections are added or modified, a spinner appears in the VS Code status bar (`$(sync~spin) Mapping entities...`), confirming in real time that schemas are being inspected and F# types generated.

---

## 6. LINQ Queries & Background Entity Provider

F# Query Studio automatically inspects registered connections and **generates strongly typed F# records and database contexts in the background**.

You don't need to configure manual Type Providers, install NuGet packages, or compile helper projects: typed entities are available immediately under each category!

### Basic Filtering & Projection

```fsharp
// 1. Query products with price > $50 sorted by unit price directly via Connections.sql
query {
    for p in Connections.sql.SampleStore.Products do
    where (p.UnitPrice > 50.0M)
    sortByDescending p.UnitPrice
    select (p.Id, p.Name, p.UnitPrice, p.StockQuantity)
}
```

### Full SQL Pushdown Translation

Rather than executing naive `SELECT *` queries and filtering records in client memory, F# Query Studio features an **Entity Framework-grade SQL Pushdown Engine**:
- **Column Projections**: `select {| Id = o.Id; Amount = o.Total |}` translates to `SELECT _t0.Id, _t0.Total FROM ...`
- **Parameterized Filters**: `where (o.TotalAmount >= 100.0f)` translates to `WHERE (_t0.TotalAmount >= @p0)`
- **Sorting & Slicing**: `sortBy`, `sortByDescending`, `take N`, `skip M` translate directly to `ORDER BY ... LIMIT N OFFSET M`
- **Server-Side Aggregations**: `count` and `sum` translate directly to `COUNT(*)` and `SUM(...)`

### Multi-Property Joins without Memory Explosions

F# query expressions support joins over compound keys using tuples:

```fsharp
// Compile-time multi-property join translated to SQL without Cartesian products
query {
    for s in Connections.sql.Transactions.Sales do
    join t in Connections.sql.Transactions.Targets on ((s.StoreId, s.Region) = (t.StoreId, t.Region))
    where (s.TotalAmount >= 250.0M)
    select {| Store = s.StoreId; Region = s.Region; Amount = s.TotalAmount; Manager = t.Manager |}
}
```

This translates directly into:
```sql
SELECT _t0.StoreId AS Store, _t0.Region AS Region, _t0.TotalAmount AS Amount, _t1.Manager AS Manager
FROM Transactions _t0
INNER JOIN Targets _t1 ON _t0.StoreId = _t1.StoreId AND _t0.Region = _t1.Region
WHERE (_t0.TotalAmount >= @p0)
```

Automatic composite B-tree indexes (`ix_table_StoreId_Region`) ensure sub-100ms execution times even when joining hundreds of thousands of rows.

### Inspecting Generated SQL (`toRawSql`)

Want to inspect the exact SQL query and bound parameters that will run on your database engine before or alongside execution?
Use `ResultKind.toRawSql` or `Connections.toRawSql`:

```fsharp
let myQuery =
    query {
        for o in Connections.sql.SampleStore.Orders do
        join c in Connections.sql.SampleStore.Customers on (o.CustomerId = c.Id)
        where (o.Status = "Completed")
        select {| OrderId = o.Id; Customer = c.FirstName + " " + c.LastName; Total = o.TotalAmount |}
    }

// Prints the exact parameterized SQL to stdout without touching the database!
printfn "Generated SQL:\n%s" (Connections.toRawSql myQuery)

// Execute and render the interactive table grid
myQuery
```

### Persistent SQLite Disk Cache for CSV & Excel

When you register or auto-import flat files (`.csv`, `.xlsx`), F# Query Studio immediately and asynchronously pre-stages them into a persistent local SQLite database located at:
`<workspace>/.fsqs/cache/engine.db`

- **Smart Cache Validation**: The engine inspects each file's last modified timestamp (`mtime`) and byte length. Unchanged files are reused instantaneously with zero loading lag on subsequent runs.
- **Immediate Async Pre-warming**: Staging starts automatically in the background upon connection registration or workspace open.
- **Visual Status Bar Feedback**: The VS Code status bar displays `$(sync~spin) Syncing data sources...` during staging and `$(database) Data sources ready` when complete. You can also run the Command Palette command **F# Query Studio: Sync Data Sources Cache** at any time.

### Automatic Discovery & Real-Time Watching via `data_sources` Directory

Skip manual connection configuration by organizing your files in a `data_sources` folder:
1. Place or drag your `.csv`, `.xlsx`, `.db`, or `.sqlite` files inside the `data_sources/` folder in your project root.
2. **Automatic Real-Time Watcher**: The extension continuously monitors the `data_sources/` directory. Whenever you move, copy, modify, or delete files, F# Query Studio automatically detects the changes, registers new connections, cleans up removed files, refreshes the connections tree, and stages the data into SQLite disk cache with visual status feedback (`$(sync~spin)`).
3. **Manual Trigger (Optional)**: You can also click **Auto-Import Data Sources** in the sidebar toolbar or run `F# Query Studio: Auto-Import Data Sources Directory` from the Command Palette at any time.

---

## 7. Traditional Parameterized SQL (Raw SQL)

When you need custom SQL queries, complex window functions, CTEs, or database-specific extensions, use `Connections.sql`:

```fsharp
Connections.sql
    "SampleStore"
    """
    SELECT 
        c.FullName,
        COUNT(o.Id) AS OrderCount,
        SUM(o.TotalAmount) AS TotalSpent
    FROM Customers c
    LEFT JOIN Orders o ON c.Id = o.CustomerId
    WHERE c.Country = @country
    GROUP BY c.Id, c.FullName
    ORDER BY TotalSpent DESC
    """
    [ "country", box "USA" ]
```

All parameters use native ADO.NET parameter binding, safeguarding against SQL injection.

---

## 8. Connections Module API Reference

The `Connections` module provides the core programmatic interface inside your notebooks:

### Connection Categories & Strongly-Typed Access
| Category / Function | Type / Signature | Description |
|---|---|---|
| `Connections.sql.<Conn>` | `DatabaseConnection` | Accesses relational database tables and scoped `.rawQuery "..." [ ... ]`. |
| `Connections.csv.<Conn>` | `CsvConnector.Data` | Accesses strongly-typed CSV rows (implements `IQueryable<'Row>`) and `.Raw()`. |
| `Connections.excel.<Conn>` | `ExcelConnector.Data` | Accesses strongly-typed Excel sheets and `.Raw()`. |
| `Connections.rest.<Conn>` | `RestConnector.Data` | Accesses strongly-typed REST items and `.SendAsync()`. |
| `Connections.getConnectionString` | `string -> string` | Returns the resolved connection string with stored credentials injected. |

### Query Execution Helpers
| Function | Signature | Description |
|---|---|---|
| `Connections.query<'T>` | `string -> string -> (string * obj) list -> 'T seq` | Executes raw SQL and maps rows directly to strongly-typed record sequence `'T seq`. |
| `Connections.queryCsv<'T>` | `string -> 'T seq` | Streams CSV rows mapped to strongly-typed records `'T seq`. |
| `Connections.queryExcel<'T>` | `string -> int option -> 'T seq` | Streams Excel rows mapped to strongly-typed records `'T seq`. |
| `Connections.queryExcelSheet<'T>` | `string -> string option -> int option -> 'T seq` | Streams rows from a specific Excel sheet mapped to records `'T seq`. |
| `Connections.queryJson<'T>` | `string -> 'T seq` | Deserializes JSON arrays or objects into strongly-typed records `'T seq`. |

### Database Connection Properties & Methods
- **`db.rawQuery(sqlText, [params])`**: Executes parameterized SQL strictly scoped to this connection and returns a `ConnectorQueryResult` for the visual grid.
- **`db.Execute(sqlText, [params])`**: Executes a query and returns a `ConnectorQueryResult` for the visual grid.
- **`db.Query<'T>(sqlText, [params])`**: Executes a query and maps directly to `'T seq`.
- **`db.Queryable<'T>(sqlText, [params])`**: Returns an `IQueryable<'T>` for query composition.
- **`db.Tables`**: Returns a `string list` of all discovered tables and views.
- **`db.Schema`**: Returns a `ConnectorTable list` with full column names and data types.

---

## 9. Interactive Results Grid

Any expression returning tabular data or collections renders as an **interactive, rich data grid**:

- **Sorting**: Click any column header to sort ascending or descending.
- **Visual Typography**: Numeric values are right-aligned, strings are left-aligned, and nulls are cleanly badged.
- **Schema View**: Switch to the **Schema** tab at the top of the grid to inspect .NET types and database column definitions.
- **Export to CSV**: Click the **Export CSV** button in the grid toolbar to save the current dataset directly to disk.

---

## 10. Language Features, Syntax Highlighting & Go to Definition (F12)

F# Query Studio includes language intelligence and full syntax highlighting for F# query expressions:

- **Syntax Highlighting for Query Expressions**:
  - Keywords such as `query`, `for`, `in`, `do`, `select`, `where`, `join`, `on`, `equals`, `into`, `groupBy`, `sortBy`, `distinct`, `count`, `sumBy`, `take`, `skip` are highlighted in dedicated keyword colors (matching your editor theme's control keywords).
  - Powered by both TextMate grammar injection (`fsharp.query.injection`) and VS Code's `DocumentSemanticTokensProvider`.
- **Go to Definition (`F12`)**: Press `F12` on any symbol (`Connections.sql`, `Connections.csv`, generated record fields like `p.UnitPrice`, or your own helper functions). The editor jumps directly to the source definition.
- **Hover / Tooltips**: Hover over variables, functions, and properties to view comprehensive XML documentation and type signatures.
- **Diagnostics**: Syntax and semantic errors are highlighted in real time before running the cell.
- **Renaming (`F2`)**: Rename identifiers across all cells in your active notebook.

---

## 11. Notebook Lifecycle & FSI Session Management

- **Shared State**: All cells in an `.fsqnb` file share the same live F# Interactive session. Values declared in earlier cells are immediately accessible in subsequent cells.
- **Cancellation**: Stop long-running queries anytime using the Stop button (⏹) on the cell toolbar.
- **Session Restart**: Click the **Restart Notebook Session** button (`$(refresh)`) on the notebook header or use `Ctrl+Shift+P > Restart Notebook Session` to reinitialize FSI cleanly.

---

## 12. Troubleshooting & Diagnostics

- **Connecting to PostgreSQL?**
  You can input standard ADO.NET connection strings (`Host=localhost;Database=mydb;Username=postgres`) or URI format (`postgresql://postgres@localhost:5432/mydb`). If you specify the password in the connection string or in the password prompt, F# Query Studio automatically extracts and secures it in VS Code `SecretStorage`, replacing it with `{password}`.
- **Worker Not Starting?**
  Run `F# Query Studio: Check Worker` from the Command Palette (`Ctrl+Shift+P`). It validates your .NET 10 SDK installation and reports actionable feedback.
- **New Tables Not Showing Up in Autocomplete?**
  Click the **Refresh** icon (`$(refresh)`) in the **Connections** panel to discover new tables and regenerate entity types.
- **Need Help or Found a Bug?**
  Open an issue on GitHub: [https://github.com/Matheusleal/fs-query-studio/issues](https://github.com/Matheusleal/fs-query-studio/issues).

---

## 13. Performance Benchmarks & UI Element Reliability

F# Query Studio undergoes rigorous automated performance benchmarking and complete UI verification:

### Execution Benchmarks (CPU, Memory & Latency)
- **Sub-millisecond Latency**: Warm pooled queries execute in **0.05 ms - 0.20 ms** with 0% CPU consumption and zero Gen 0/1/2 garbage collections.
- **Streaming Volume Scaling**: Seamlessly queries 100 to 50,000+ rows through `Results.fs` lazy streaming with a flat memory allocation baseline (~1.5 KB per cursor fetch).
- **LINQ & Dynamic Records**: Strongly-typed F# record mapping (`Connections.query<'T>`) and LINQ expressions (`query { for ... in ... do where ... select ... }`) process 1,000 rows in **~2.0 ms** using compiled constructor reflection caching.
- **High-Throughput File Queries**: CSV processing reaches **~500,000 records/sec**.

For detailed benchmark methodology, raw metrics, and hardware specifications, consult the **[Execution Benchmark Report](BENCHMARK_REPORT.md)** ([view on GitHub](https://github.com/Matheusleal/fs-query-studio/blob/main/docs/BENCHMARK_REPORT.md)).

### UI Element Reliability Test Suite
The extension UI is validated by comprehensive automated test suites:
- **Results Grid & Toolbar**: Toolbar segmented buttons, row counters (including truncated counts like `3+ rows`), dynamic stylesheets, and CSV export action.
- **Sticky Headers & Virtualization**: Sticky `.fsqhead` with column types, `.fsq-num` alignment, alternating `.fsq-odd` rows, and virtualization sizer calculation (`N * 24px`).
- **Cell Formatters**: Badged `.fsq-null` values ("NULL"), right-aligned numeric cells, booleans, and strings.
- **Schema Inspector**: Column names, data types, and nullability flags.
- **Pagination & Banners**: Continuations, disabled state on click, warning notices, and host notifications.
- **Connections Explorer Tree**: TreeItem icons (`database`, `symbol-namespace`, `table`, `symbol-field`, `warning`), descriptions, and context values.

