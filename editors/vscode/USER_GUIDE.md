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
   - [Joining Multiple Tables](#joining-multiple-tables)
   - [Aggregations & Grouping](#aggregations--grouping)
   - [Querying CSV Files with LINQ](#querying-csv-files-with-linq)
   - [Combining Database & CSV in Memory](#combining-database--csv-in-memory)
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

## 5. Smart Connection Autocomplete

When editing notebook cells, F# Query Studio provides **context-aware IntelliSense** tailored to each connector category:

- `Connections.connect "..."` ➔ Recommends **data sources that generate entity types** (SQLite, PostgreSQL, SQL Server, MySQL, CSV, Excel, JSON).
- `Connections.sql "..."` ➔ Recommends **only relational SQL databases** (SQLite, PostgreSQL, SQL Server, MySQL).
- `Connections.csv "..."` ➔ Recommends **only CSV connections**.
- `Connections.excel "..."` ➔ Recommends **only Excel connections**.
- `Connections.json "..."` ➔ Recommends **only JSON connections**.
- `Connections.rest "..."` ➔ Recommends **only REST API connections**.

---

## 6. LINQ Queries & Background Entity Provider

F# Query Studio automatically inspects registered connections and **generates strongly typed F# records and database contexts in the background**.

You don't need to configure manual Type Providers, install NuGet packages, or compile helper projects: simply call `.asEntities`!

### Basic Filtering & Projection

```fsharp
// 1. Connect to the sample SQLite store
let conn = Connections.connect "SampleStore"
let db = conn.asEntities

// 2. Query products with price > $50 sorted by unit price
query {
    for p in db.Products do
    where (p.UnitPrice > 50.0M)
    sortByDescending p.UnitPrice
    select (p.Id, p.Name, p.UnitPrice, p.StockQuantity)
}
```

### Joining Multiple Tables

Perform relational joins with compile-time type checking using F#'s `join` clause:

```fsharp
// Join Customers with Orders and aggregate totals
query {
    for c in db.Customers do
    join o in db.Orders on (c.Id = o.CustomerId)
    where (o.Status = "Completed")
    select (c.FullName, c.Country, o.Id, o.OrderDate, o.TotalAmount)
}
```

```fsharp
// Three-way join: OrderItems + Products + Categories
query {
    for item in db.OrderItems do
    join prod in db.Products on (item.ProductId = prod.Id)
    join cat in db.Categories on (prod.CategoryId = cat.Id)
    select (item.OrderId, prod.Name, cat.Name, item.Quantity, item.UnitPrice)
}
```

### Aggregations & Grouping

```fsharp
// Group customers by country and count total orders
query {
    for c in db.Customers do
    groupValBy c.Id into g
    select (g.Key, count g)
}
```

### Querying CSV Files with LINQ

Flat files like CSV, Excel, and JSON get inferred typed schemas automatically:

```fsharp
let sales = (Connections.connect "SalesFeed").asEntities

// Query the CSV transactions feed
query {
    for row in sales.Rows do
    where (row.StoreCity = "New York" && row.Quantity >= 2)
    sortByDescending row.Total
    select (row.TransactionId, row.Product, row.Quantity, row.Total)
}
```

### Combining Database & CSV in Memory

Because F# notebooks run in a unified Interactive session, you can cross-correlate relational databases with external CSV feeds using standard F# functions:

```fsharp
let db = (Connections.connect "SampleStore").asEntities
let csv = (Connections.connect "SalesFeed").asEntities

let dbProducts = 
    query { for p in db.Products do select (p.Name, p.UnitPrice) }
    |> Seq.toList

let csvTransactions =
    query { for r in csv.Rows do select (r.Product, r.Total) }
    |> Seq.toList

// Cross-reference data in pure F#
printfn "Database products: %d | CSV sales: %d" dbProducts.Length csvTransactions.Length
```

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

### Connection Management
| Function | Signature | Description |
|---|---|---|
| `Connections.connect` | `string -> DatabaseConnection` | Connects to a registered SQL database (PostgreSQL, SQL Server, MySQL, SQLite). |
| `Connections.connectCsv` | `string -> CsvConnection` | Connects to a registered CSV file. |
| `Connections.connectExcel` | `string -> ExcelConnection` | Connects to a registered Excel (.xlsx) file. |
| `Connections.connectRest` | `string -> RestConnection` | Connects to a registered REST/JSON endpoint. |
| `Connections.getConnectionString` | `string -> string` | Returns the resolved connection string with stored credentials injected. |

### Query Execution
| Function | Signature | Description |
|---|---|---|
| `Connections.sql` | `string -> string -> (string * obj) list -> ConnectorQueryResult` | Executes parameterized raw SQL and renders results in the interactive grid. |
| `Connections.query<'T>` | `string -> string -> (string * obj) list -> 'T seq` | Executes raw SQL and maps rows directly to strongly-typed record sequence `'T seq`. |
| `Connections.csv` | `string -> ConnectorQueryResult` | Reads raw CSV rows rendered in the grid. |
| `Connections.queryCsv<'T>` | `string -> 'T seq` | Streams CSV rows mapped to strongly-typed records `'T seq`. |
| `Connections.excel` | `string -> int option -> ConnectorQueryResult` | Reads raw Excel rows with optional row limit. |
| `Connections.queryExcel<'T>` | `string -> int option -> 'T seq` | Streams Excel rows mapped to strongly-typed records `'T seq`. |
| `Connections.queryExcelSheet<'T>` | `string -> string option -> int option -> 'T seq` | Streams rows from a specific Excel sheet mapped to records `'T seq`. |
| `Connections.queryJson<'T>` | `string -> 'T seq` | Deserializes JSON arrays or objects into strongly-typed records `'T seq`. |
| `Connections.rest` | `string -> RestRequestOptions -> CancellationToken -> Task<JsonElement>` | Sends an asynchronous custom HTTP request to a REST API connection. |

### Connection Objects & Properties
- **`conn.asEntities`**: Returns the strongly-typed schema and table context for LINQ `query { ... }` expressions.
- **`conn.Execute(sqlText, [params])`**: Executes a query and returns a `ConnectorQueryResult` for the visual grid.
- **`conn.Query<'T>(sqlText, [params])`**: Executes a query and maps directly to `'T seq`.
- **`conn.Queryable<'T>(sqlText, [params])`**: Returns an `IQueryable<'T>` for query composition.
- **`conn.Tables`**: Returns a `string list` of all discovered tables and views.
- **`conn.Schema`**: Returns a `ConnectorTable list` with full column names and data types.

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
- **Go to Definition (`F12`)**: Press `F12` on any symbol (`Connections.connect`, `.asEntities`, generated record fields like `p.UnitPrice`, or your own helper functions). The editor jumps directly to the source definition.
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

For detailed benchmark methodology, raw metrics, and hardware specifications, consult the **[Execution Benchmark Report](../../docs/BENCHMARK_REPORT.md)**.

### UI Element Reliability Test Suite
The extension UI is validated by comprehensive automated test suites:
- **Results Grid & Toolbar**: Toolbar segmented buttons, row counters (including truncated counts like `3+ rows`), dynamic stylesheets, and CSV export action.
- **Sticky Headers & Virtualization**: Sticky `.fsqhead` with column types, `.fsq-num` alignment, alternating `.fsq-odd` rows, and virtualization sizer calculation (`N * 24px`).
- **Cell Formatters**: Badged `.fsq-null` values ("NULL"), right-aligned numeric cells, booleans, and strings.
- **Schema Inspector**: Column names, data types, and nullability flags.
- **Pagination & Banners**: Continuations, disabled state on click, warning notices, and host notifications.
- **Connections Explorer Tree**: TreeItem icons (`database`, `symbol-namespace`, `table`, `symbol-field`, `warning`), descriptions, and context values.

