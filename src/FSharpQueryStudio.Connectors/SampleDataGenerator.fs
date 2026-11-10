namespace FSharpQueryStudio.Connectors

open System
open System.IO
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Xml
open ClosedXML.Excel
open Microsoft.Data.Sqlite
open FSharpQueryStudio.Protocol

module SampleDataGenerator =

    let [<Literal>] DefaultGitHubRestUrl = "https://api.github.com/orgs/fsprojects/repos?per_page=8"

    type SampleFilesResult =
        { SqlitePath: string
          Sqlite2Path: string
          CsvPath: string
          Csv2Path: string
          ExcelPath: string
          Excel2Path: string
          JsonPath: string
          XmlPath: string
          RestUrl: string
          NotebookPath: string
          NotebookPtPath: string }

    let private createTablesSql = """
CREATE TABLE IF NOT EXISTS Categories (
    Id INTEGER PRIMARY KEY,
    Name TEXT NOT NULL,
    Description TEXT,
    Code TEXT NOT NULL,
    Department TEXT NOT NULL,
    IsActive INTEGER NOT NULL,
    CreatedAt TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS Products (
    Id INTEGER PRIMARY KEY,
    Name TEXT NOT NULL,
    CategoryId INTEGER NOT NULL,
    Price REAL NOT NULL,
    StockQuantity INTEGER NOT NULL,
    Sku TEXT NOT NULL,
    Brand TEXT NOT NULL,
    CostPrice REAL NOT NULL,
    WeightKg REAL NOT NULL,
    IsDiscontinued INTEGER NOT NULL,
    Rating REAL NOT NULL,
    FOREIGN KEY (CategoryId) REFERENCES Categories(Id)
);

CREATE TABLE IF NOT EXISTS Customers (
    Id INTEGER PRIMARY KEY,
    FirstName TEXT NOT NULL,
    LastName TEXT NOT NULL,
    Email TEXT NOT NULL,
    City TEXT NOT NULL,
    Country TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    Phone TEXT NOT NULL,
    PostalCode TEXT NOT NULL,
    State TEXT NOT NULL,
    LoyaltyPoints INTEGER NOT NULL,
    CustomerType TEXT NOT NULL,
    IsActive INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS Orders (
    Id INTEGER PRIMARY KEY,
    CustomerId INTEGER NOT NULL,
    OrderDate TEXT NOT NULL,
    TotalAmount REAL NOT NULL,
    Status TEXT NOT NULL,
    OrderNumber TEXT NOT NULL,
    PaymentMethod TEXT NOT NULL,
    ShippingCost REAL NOT NULL,
    DiscountAmount REAL NOT NULL,
    Currency TEXT NOT NULL,
    Channel TEXT NOT NULL,
    Notes TEXT,
    FOREIGN KEY (CustomerId) REFERENCES Customers(Id)
);

CREATE TABLE IF NOT EXISTS OrderItems (
    Id INTEGER PRIMARY KEY,
    OrderId INTEGER NOT NULL,
    ProductId INTEGER NOT NULL,
    Quantity INTEGER NOT NULL,
    UnitPrice REAL NOT NULL,
    DiscountPercent REAL NOT NULL,
    LineTotal REAL NOT NULL,
    TaxAmount REAL NOT NULL,
    Sku TEXT NOT NULL,
    FOREIGN KEY (OrderId) REFERENCES Orders(Id),
    FOREIGN KEY (ProductId) REFERENCES Products(Id)
);
"""

    let private createInventoryTablesSql = """
CREATE TABLE IF NOT EXISTS Warehouses (
    Id INTEGER PRIMARY KEY,
    Code TEXT NOT NULL,
    City TEXT NOT NULL,
    Country TEXT NOT NULL,
    Capacity INTEGER NOT NULL,
    Name TEXT NOT NULL,
    Address TEXT NOT NULL,
    PostalCode TEXT NOT NULL,
    ManagerName TEXT NOT NULL,
    OperatingStatus TEXT NOT NULL,
    RefrigeratedCapacity INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS StockLevels (
    Id INTEGER PRIMARY KEY,
    WarehouseId INTEGER NOT NULL,
    ProductSku TEXT NOT NULL,
    QuantityOnHand INTEGER NOT NULL,
    ReorderLevel INTEGER NOT NULL,
    LastRestocked TEXT NOT NULL,
    BinLocation TEXT NOT NULL,
    ReservedQuantity INTEGER NOT NULL,
    AvailableQuantity INTEGER NOT NULL,
    UnitCost REAL NOT NULL,
    TotalValue REAL NOT NULL,
    SafetyStock INTEGER NOT NULL,
    Status TEXT NOT NULL,
    FOREIGN KEY (WarehouseId) REFERENCES Warehouses(Id)
);

CREATE TABLE IF NOT EXISTS Suppliers (
    Id INTEGER PRIMARY KEY,
    CompanyName TEXT NOT NULL,
    ContactEmail TEXT NOT NULL,
    Country TEXT NOT NULL,
    Rating REAL NOT NULL,
    ContactName TEXT NOT NULL,
    ContactPhone TEXT NOT NULL,
    City TEXT NOT NULL,
    PaymentTermsDays INTEGER NOT NULL,
    TaxId TEXT NOT NULL,
    IsPreferred INTEGER NOT NULL
);
"""

    type private NotebookCell =
        { kind: string
          language: string option
          value: string }

    let private mkMarkdown (text: string) : NotebookCell =
        { kind = "markdown"; language = None; value = text.Trim() }

    let private mkCode (code: string) : NotebookCell =
        { kind = "code"; language = Some "fsharp"; value = code.Trim() }

    let private serializeNotebook (cells: NotebookCell list) : string =
        let mapped =
            cells
            |> List.map (fun c ->
                let dict = System.Collections.Generic.Dictionary<string, obj>()
                dict.["kind"] <- c.kind
                match c.language with
                | Some l -> dict.["language"] <- l
                | None -> ()
                dict.["value"] <- c.value
                dict)
        let root = System.Collections.Generic.Dictionary<string, obj>()
        root.["version"] <- 1
        root.["cells"] <- mapped
        root.["metadata"] <- System.Collections.Generic.Dictionary<string, obj>()
        let options = JsonSerializerOptions(WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)
        JsonSerializer.Serialize(root, options) + "\n"

    let private getSampleDemoEnglishNotebook () : string =
        let cells =
            [
                mkMarkdown """
# 🚀 F# Query Studio — High-Volume Multi-Source Interactive Notebook

> **Welcome to F# Query Studio!** This interactive notebook demonstrates querying multiple heterogeneous data sources without collisions, alongside strongly-typed LINQ, high-performance SQL pushdown, joins, analytical aggregations, and virtualized results rendering over **100,000+ records per data source**.

---

### 📋 Pre-Connected Data Sources (100k+ Records Each with Expanded Columns)

| Connection | Kind | Description | Primary Tables & Expanded Columns |
| :--- | :--- | :--- | :--- |
| **`SampleStore`** | SQLite | Relational e-commerce orders & catalog (100k+ orders) | `Categories`, `Products`, `Customers`, `Orders`, `OrderItems` |
| **`InventoryStore`** | SQLite | Warehouse inventory & logistics (100k+ warehouses & stocks) | `Warehouses`, `StockLevels`, `Suppliers` |
| **`SalesFeed`** | CSV | Flat-file transaction export (100k+ rows) | `Date`, `OrderId`, `Customer`, `Product`, `Category`, `TotalAmount`, `Status`, `GrossMargin`, etc. |
| **`CustomersFeed`** | CSV | Flat-file loyalty & satisfaction feed (100k+ rows) | `CustomerId`, `CustomerName`, `LoyaltyTier`, `TotalSpent`, `Score`, `TotalOrders`, `Segment`, etc. |
| **`RegionalTargets`** | Excel (`.xlsx`) | Multi-sheet regional revenue targets & budgets (100k+ rows) | `RegionalTargets`, `QuarterlyBudget` |
| **`CommerceOps`** | Excel (`.xlsx`) | 4-sheet operations workbook (100k+ sales & deliveries) | `Customers`, `ProductsStock`, `Sales`, `DeliveryLogs` |
| **`ProductCatalog`** | JSON (`.json`) | Multi-collection catalog & reviews (100k+ items) | `Products`, `Reviews` |
| **`LogisticsHub`** | XML (`.xml`) | Multi-collection carrier network & shipments (100k+ items) | `Carriers`, `Shipments` |
| **`GitHubFSharp`** | REST (`https://`) | Secure GitHub REST API (`fsprojects` repositories) | `name`, `full_name`, `language`, `stargazers_count`, `forks_count`, `html_url` |

---

### ⌨️ Quick Keyboard Shortcuts & Navigation

- **Run Active Cell**: `Ctrl + Enter` (Windows/Linux) or `Cmd + Enter` (macOS)
- **Run and Advance**: `Shift + Enter`
- **IntelliSense / Completion**: `Ctrl + Space`
- **Go to Definition**: `F12` on any property (`.Country`, `.TotalAmount`, etc.)
- **Interactive Data Grid**: Use the footer in query results to switch between **Grid** (sortable/paginated), **JSON**, and **Raw Text**.
"""

                mkMarkdown """
## 1. ⚡ Strongly-Typed LINQ: Filtering Customers (`SampleStore`)

Access strongly-typed entities directly using `Connections.sql.SampleStore.Customers`. Notice how `c.Country`, `c.FirstName`, and `c.Email` have complete IntelliSense autocomplete, hover documentation, and type safety!

> 💡 **Formatting Tip**: Projecting into anonymous records (`{| ... |}`) generates clean, labeled column headers in the interactive table grid.
"""

                mkCode """
// 1. Query SQLite (SampleStore) customers from Brazil directly via Connections.sql
query {
    for c in Connections.sql.SampleStore.Customers do
    where (c.Country = "Brazil")
    sortBy c.FirstName
    select {|
        Id = c.Id
        CustomerName = c.FirstName + " " + c.LastName
        Email = c.Email
        City = c.City
        Country = c.Country
    |}
}
"""

                mkMarkdown """
## 2. 🔗 Relational Joins: Orders with Customer Profiles (`SampleStore`)

Join tables cleanly and naturally using standard F# LINQ `join ... on (...)` syntax within the same connection.
"""

                mkCode """
// 2. Join Orders and Customers within SampleStore to view transaction details with customer info
query {
    for o in Connections.sql.SampleStore.Orders do
    join c in Connections.sql.SampleStore.Customers on (o.CustomerId = c.Id)
    sortByDescending o.TotalAmount
    select {|
        OrderId = o.Id
        Customer = c.FirstName + " " + c.LastName
        Country = c.Country
        OrderDate = o.OrderDate
        TotalAmount = o.TotalAmount
        Status = o.Status
    |}
}
"""

                mkMarkdown """
## 3. 🏬 Second SQLite Source: Querying Warehouses & Stocks (`InventoryStore`)

Notice that multiple SQLite data sources coexist seamlessly without naming or module collisions!
Here we query the second database directly via `Connections.sql.InventoryStore`.
"""

                mkCode """
// 3. Query the second SQLite database (InventoryStore) directly via Connections.sql
query {
    for w in Connections.sql.InventoryStore.Warehouses do
    join s in Connections.sql.InventoryStore.StockLevels on (w.Id = s.WarehouseId)
    sortByDescending s.QuantityOnHand
    select {|
        Warehouse = w.Code
        City = w.City
        ProductSku = s.ProductSku
        QuantityOnHand = s.QuantityOnHand
        ReorderLevel = s.ReorderLevel
        LastRestocked = s.LastRestocked
    |}
}
"""

                mkMarkdown """
## 4. 📊 Analytical Aggregations & Grouping (GroupBy)

Compute business metrics, order counts, and revenue statistics grouped by order status across 100,000+ orders.
"""

                mkCode """
// 4. Financial breakdown: Total revenue and average ticket by order status across 100k orders
let orders = Connections.sql.SampleStore.Orders |> Seq.toList

orders
|> List.groupBy (fun o -> o.Status)
|> List.map (fun (status, items) -> {|
    Status = status
    OrderCount = items.Length
    TotalRevenue = items |> List.sumBy (fun i -> i.TotalAmount)
    AverageTicket = items |> List.averageBy (fun i -> i.TotalAmount)
|})
"""

                mkMarkdown """
## 5. 📁 First Flat-File CSV: Sales Transactions (`SalesFeed`)

CSV files are automatically discovered, inferred, and mapped to strongly-typed record rows. Decimal values, integers, and text fields are parsed culture-invariantly.
Access the feed directly via `Connections.csv.SalesFeed`.
"""

                mkCode """
// 5. Query the first CSV feed (SalesFeed) directly and filter electronics purchases
query {
    for row in Connections.csv.SalesFeed do
    where (row.Category = "Electronics")
    sortByDescending row.TotalAmount
    select {|
        Date = row.Date
        Customer = row.Customer
        Product = row.Product
        Quantity = row.Quantity
        UnitPrice = row.UnitPrice
        TotalAmount = row.TotalAmount
        Status = row.Status
    |}
}
"""

                mkMarkdown """
## 6. 📋 Second Flat-File CSV: Customer Loyalty Feed (`CustomersFeed`)

A second CSV source is registered alongside `SalesFeed`. Both CSV sources generate independent, unique strongly-typed models without collision or context leakage.
"""

                mkCode """
// 6. Query the second CSV feed (CustomersFeed) directly
query {
    for row in Connections.csv.CustomersFeed do
    where (row.TotalSpent >= 300.0M)
    sortByDescending row.Score
    select {|
        Customer = row.CustomerName
        Tier = row.LoyaltyTier
        TotalSpent = row.TotalSpent
        SatisfactionScore = row.Score
        ActiveSince = row.ActiveSince
        Feedback = row.Feedback
    |}
}
"""

                mkMarkdown """
## 7. 🔄 Cross-Source LINQ: Correlating Multiple CSV Feeds in Memory

Seamlessly join and correlate data from both CSV files (`SalesFeed` + `CustomersFeed`) in a single F# query!
"""

                mkCode """
// 7. In-memory correlation between SalesFeed transactions and CustomersFeed loyalty
let salesRows = Connections.csv.SalesFeed |> Seq.toList
let loyaltyRows = Connections.csv.CustomersFeed |> Seq.toList

query {
    for s in salesRows do
    join l in loyaltyRows on (s.Customer = l.CustomerName)
    where (s.TotalAmount >= 100.0M)
    sortByDescending s.TotalAmount
    select {|
        OrderId = s.OrderId
        Customer = s.Customer
        LoyaltyTier = l.LoyaltyTier
        Product = s.Product
        OrderAmount = s.TotalAmount
        CustomerScore = l.Score
    |}
}
"""

                mkMarkdown """
### ⚡ Pushdown & Multi-Property Joins with SQL Preview (`toRawSql`)

F# Query Studio compiles multi-property joins `on ((a.param1, a.param2) = (b.param1, b.param2))` directly to index-backed SQL `_t0.col1 = _t1.col1 AND _t0.col2 = _t1.col2`, avoiding Cartesian products and excessive memory usage.
Use `ResultKind.toRawSql` to preview the pushdown SQL before or alongside execution!
"""

                mkCode """
// 7.1 Preview generated pushdown SQL for relational joins before execution
let complexJoinQuery =
    query {
        for o in Connections.sql.SampleStore.Orders do
        join i in Connections.sql.SampleStore.OrderItems on (o.Id = i.OrderId)
        where (o.TotalAmount >= 100.0f)
        select {| OrderId = o.Id; ItemId = i.Id; Amount = o.TotalAmount |}
    }

printfn "Pushdown SQL Query:\n%s" (ResultKind.toRawSql complexJoinQuery)
complexJoinQuery
"""

                mkMarkdown """
## 8. 🛡️ Safe Parameterized SQL (`SampleStore`)

For complex database-specific queries, use `Connections.sql.<Conn>.rawQuery` with parameterized inputs to guard against SQL injection while maintaining raw database performance.
"""

                mkCode """
// 8. Parameterized raw SQL query joining Products and Categories
let catalogSql = @"
SELECT 
    p.Name          AS Product,
    c.Name          AS Category,
    p.Price         AS Price,
    p.StockQuantity AS Stock
FROM Products p
JOIN Categories c ON p.CategoryId = c.Id
WHERE p.Price >= @minPrice
ORDER BY p.Price DESC
"

Connections.sql.SampleStore.rawQuery(catalogSql, [ "minPrice", box 100.0 ])
"""

                mkMarkdown """
## 9. 📊 Excel Workbook (`.xlsx`): Regional Revenue Targets (`RegionalTargets`)

Excel workbooks (`.xlsx`) are inspected per worksheet and exposed with strongly-typed sheet accessors via `Connections.excel.RegionalTargets`. You can query individual sheets (`RegionalTargets` or `QuarterlyBudget`) directly with LINQ!
"""

                mkCode """
// 9. Query the RegionalTargets worksheet from the Excel (.xlsx) workbook
query {
    for row in Connections.excel.RegionalTargets.RegionalTargets do
    where (row.GrowthRate >= 10.0M)
    sortByDescending row.AchievedRevenue
    select {|
        Region = row.Region
        Country = row.Country
        LeadManager = row.LeadManager
        AnnualTarget = row.AnnualTarget
        AchievedRevenue = row.AchievedRevenue
        GrowthRatePct = row.GrowthRate
    |}
}
"""

                mkMarkdown """
## 10. 📦 4-Sheet Excel Workbook (`.xlsx`): Customers, Products Stock, Sales & Delivery Logs (`CommerceOps`)

Each Excel connection acts as a schema container (`Connections.excel.CommerceOps.<Sheet>`) mapped to prefixed SQLite tables (`CommerceOps_Customers`, `CommerceOps_ProductsStock`, `CommerceOps_Sales`, `CommerceOps_DeliveryLogs`). You can join all four worksheets in a single SQL-pushed LINQ query!
"""

                mkCode """
// 10. Join all 4 sheets (Sales, Customers, ProductsStock, DeliveryLogs) in CommerceOps (.xlsx)
query {
    for s in Connections.excel.CommerceOps.Sales do
    join c in Connections.excel.CommerceOps.Customers on (s.CustomerId = c.CustomerId)
    join p in Connections.excel.CommerceOps.ProductsStock on (s.ProductId = p.ProductId)
    join d in Connections.excel.CommerceOps.DeliveryLogs on (s.SaleId = d.SaleId)
    sortByDescending s.TotalAmount
    select {|
        SaleId = s.SaleId
        Customer = c.FullName
        Country = c.Country
        Product = p.ProductName
        StockAvailable = p.StockQuantity
        Quantity = s.Quantity
        TotalAmount = s.TotalAmount
        Carrier = d.Carrier
        TrackingCode = d.TrackingCode
        DeliveryStatus = d.DeliveryStatus
    |}
}
"""

                mkMarkdown """
## 11. 🗂️ Local JSON Document (`.json`): Multi-Collection Catalog & Reviews (`ProductCatalog`)

Local JSON files (`.json`) work just like Excel workbooks: each discovered array collection is exposed as a strongly-typed sub-property (`Connections.json.ProductCatalog.Products` and `Connections.json.ProductCatalog.Reviews`) staged in SQLite (`ProductCatalog_Products`, `ProductCatalog_Reviews`) with full LINQ-to-SQL pushdown!
"""

                mkCode """
// 11. Join Products and Reviews inside the local JSON file (ProductCatalog.json) via Connections.json
query {
    for p in Connections.json.ProductCatalog.Products do
    join r in Connections.json.ProductCatalog.Reviews on (p.ProductId = r.ProductId)
    where (r.Stars >= 4)
    sortByDescending p.Price
    select {|
        Sku = p.Sku
        Product = p.Title
        Category = p.Category
        Price = p.Price
        Reviewer = r.Reviewer
        Stars = r.Stars
        Comment = r.Comment
    |}
}
"""

                mkMarkdown """
## 12. 📄 Local XML Document (`.xml`): Carrier Network & Shipments (`LogisticsHub`)

Local XML files (`.xml`) automatically extract both XML attributes (`@CarrierId`, `@Code`) and child elements (`<TrackingNumber>`, `<Destination>`, `<ShippingCost>`) into strongly-typed sub-properties (`Connections.xml.LogisticsHub.Carriers` and `Connections.xml.LogisticsHub.Shipments`).
"""

                mkCode """
// 12. Join Shipments and Carriers inside the local XML file (logistics_hub.xml) via Connections.xml
query {
    for s in Connections.xml.LogisticsHub.Shipments do
    join c in Connections.xml.LogisticsHub.Carriers on (s.CarrierId = c.CarrierId)
    sortByDescending s.ShippingCost
    select {|
        ShipmentId = s.ShipmentId
        TrackingNumber = s.TrackingNumber
        Carrier = c.Name
        HubCountry = c.HubCountry
        Destination = s.Destination
        WeightKg = s.WeightKg
        ShippingCost = s.ShippingCost
        Status = s.Status
    |}
}
"""

                mkMarkdown """
## 13. 🔒 Secure GitHub REST API (`https://`): F# Community Repositories (`GitHubFSharp`)

REST/JSON endpoints over HTTPS are automatically sampled and mapped into strongly-typed F# records. Here we query public F# repositories from `https://api.github.com/orgs/fsprojects/repos` via `Connections.rest.GitHubFSharp`.
"""

                mkCode """
// 13. Query public F# repositories from GitHub HTTPS REST API via Connections.rest
query {
    for repo in Connections.rest.GitHubFSharp do
    sortByDescending repo.stargazers_count
    select {|
        Repository = repo.full_name
        Language = repo.language
        Stars = repo.stargazers_count
        Forks = repo.forks_count
        OpenIssues = repo.open_issues_count
        DefaultBranch = repo.default_branch
        Url = repo.html_url
    |}
}
"""

                mkMarkdown """
## 14. 🌐 High-Scale Overview: 9-Source Pipeline (100k+ Records Each)

Because F# Query Studio executes queries in an integrated F# Interactive session, you can aggregate metadata and counts across all 9 sources (`SQLite`, `CSV`, `Excel`, `JSON`, `XML`, and `REST/JSON`) simultaneously.
"""

                mkCode """
// 14. Comprehensive summary across all 9 registered data sources
let sampleStoreOrders = Connections.sql.SampleStore.Orders |> Seq.length
let inventoryWarehouses = Connections.sql.InventoryStore.Warehouses |> Seq.length
let salesCount = Connections.csv.SalesFeed |> Seq.length
let loyaltyCount = Connections.csv.CustomersFeed |> Seq.length
let excelTargetsCount = Connections.excel.RegionalTargets.RegionalTargets |> Seq.length
let commerceSalesCount = Connections.excel.CommerceOps.Sales |> Seq.length
let jsonProductsCount = Connections.json.ProductCatalog.Products |> Seq.length
let xmlShipmentsCount = Connections.xml.LogisticsHub.Shipments |> Seq.length
let githubReposCount = Connections.rest.GitHubFSharp |> Seq.length

[
    {| Source = "SampleStore";     Kind = "SQLite (Relational)"; RecordCount = sampleStoreOrders;   PrimaryEntity = "Orders" |}
    {| Source = "InventoryStore";  Kind = "SQLite (Relational)"; RecordCount = inventoryWarehouses; PrimaryEntity = "Warehouses" |}
    {| Source = "SalesFeed";       Kind = "CSV (Flat-File)";     RecordCount = salesCount;          PrimaryEntity = "Transactions" |}
    {| Source = "CustomersFeed";   Kind = "CSV (Flat-File)";     RecordCount = loyaltyCount;        PrimaryEntity = "Loyalty Profiles" |}
    {| Source = "RegionalTargets"; Kind = "Excel (.xlsx)";       RecordCount = excelTargetsCount;   PrimaryEntity = "RegionalTargets" |}
    {| Source = "CommerceOps";     Kind = "Excel (.xlsx)";       RecordCount = commerceSalesCount;  PrimaryEntity = "Customers / Stock / Sales / Delivery" |}
    {| Source = "ProductCatalog";  Kind = "JSON (.json)";        RecordCount = jsonProductsCount;   PrimaryEntity = "Products / Reviews" |}
    {| Source = "LogisticsHub";    Kind = "XML (.xml)";          RecordCount = xmlShipmentsCount;   PrimaryEntity = "Carriers / Shipments" |}
    {| Source = "GitHubFSharp";    Kind = "REST JSON (HTTPS)";   RecordCount = githubReposCount;    PrimaryEntity = "GitHub Repositories" |}
]
"""

                mkMarkdown """
## 15. 📁 File Operations & Custom Pipelines with `System.IO` (`data_sources`)

F# Query Studio keeps the process working directory (`System.Environment.CurrentDirectory`) strictly aligned with your workspace root. You can create, stream, and manipulate local files using standard .NET APIs (`System.IO.File`, `FileStream`, `StreamWriter`) completely free of OS file locks across notebook cells!

In this example, we stream raw records from `data_sources/sales_feed.csv`, extract top transactions, and generate a new summary file `data_sources/top_sales_exported.csv` using `System.IO.File.Create`.
"""

                mkCode """
// 15. Create a filtered summary file from data_sources/sales_feed.csv using System.IO.File.Create
open System.IO
open System.Text

let salesFeedPath = Path.Combine("data_sources", "sales_feed.csv")
let summaryPath = Path.Combine("data_sources", "top_sales_exported.csv")

// Stream lines from data_sources and write with explicit stream disposal
let summaryRows =
    File.ReadLines(salesFeedPath)
    |> Seq.skip 1
    |> Seq.take 100
    |> Seq.map (fun line -> line.Split(','))
    |> Seq.filter (fun p -> p.Length >= 7)
    |> Seq.map (fun p ->
        {|
            OrderId = p.[1]
            Customer = p.[2]
            Product = p.[3]
            Amount = p.[5]
            Status = p.[6]
        |})
    |> Seq.toList

do
    use stream = File.Create(summaryPath)
    use writer = new StreamWriter(stream, Encoding.UTF8)
    writer.WriteLine("OrderId,Customer,Product,Amount,Status")
    for r in summaryRows do
        writer.WriteLine($"{r.OrderId},{r.Customer},{r.Product},{r.Amount},{r.Status}")
    writer.Flush()

printfn "Successfully generated: %s (Total records: %d, File size: %d bytes)"
    summaryPath summaryRows.Length (FileInfo(summaryPath).Length)

summaryRows
"""

                mkMarkdown """
## 16. 🔄 Multi-Cell Lock-Free Verification Across Notebook Sessions

Next, in this subsequent cell, we inspect and update the `top_sales_exported.csv` file created in the previous cell. F# Query Studio actively releases superseded continuation cursors and finalizes file handles between cell evaluations, ensuring that reading (`File.ReadAllLines`) and appending (`File.AppendAllText`) proceed seamlessly without sharing violations (`IOException`).
"""

                mkCode """
// 16. Verify lock-free access across cells: read, append metadata, and project to table
open System.IO

let summaryPath = Path.Combine("data_sources", "top_sales_exported.csv")

// Read lines back from the file created in the previous cell
let lines = File.ReadAllLines(summaryPath)

// Append metadata to verify write access is immediately available
File.AppendAllText(summaryPath, "# Processed by F# Query Studio\n")

printfn "Verified lock-free access to %s: read %d lines, updated size %d bytes"
    summaryPath lines.Length (FileInfo(summaryPath).Length)

lines
|> Seq.skip 1
|> Seq.filter (fun l -> not (l.StartsWith("#")))
|> Seq.take 20
|> Seq.map (fun l ->
    let p = l.Split(',')
    {|
        OrderId = p.[0]
        Customer = p.[1]
        Product = p.[2]
        Amount = p.[3]
        Status = p.[4]
    |})
|> Seq.toList
"""

                mkMarkdown """
---

### 📚 Official Documentation & References

- 📘 [F# Documentation (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/fsharp/)
- 🔍 [F# Query Expressions / LINQ Guide](https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/query-expressions)
- ⚡ [F# Interactive (FSI) Reference](https://learn.microsoft.com/en-us/dotnet/fsharp/tools/fsharp-interactive/)
"""
            ]
        serializeNotebook cells

    let private getSampleDemoPortugueseNotebook () : string =
        let cells =
            [
                mkMarkdown """
# 🚀 F# Query Studio — Guia Interativo Multi-Fontes de Alta Escala

> **Bem-vindo ao F# Query Studio!** Este notebook interativo demonstra o funcionamento com múltiplas fontes de dados heterogêneas sem colisões de nomes, consultas LINQ fortemente tipadas, pushdown para SQL de alto desempenho, joins, agregações analíticas e renderização de dados virtualizada com **mais de 100 mil registros em cada data source**.

---

### 📋 Fontes de Dados Pré-Conectadas (Ao menos 100k registros cada com colunas expandidas)

| Conexão | Tipo | Descrição | Tabelas / Colunas Expandidas Principais |
| :--- | :--- | :--- | :--- |
| **`SampleStore`** | SQLite | E-commerce relacional com mais de 100k pedidos | `Categories`, `Products`, `Customers`, `Orders`, `OrderItems` |
| **`InventoryStore`** | SQLite | Gestão de armazéns e logística com 100k+ registros | `Warehouses`, `StockLevels`, `Suppliers` |
| **`SalesFeed`** | CSV | Feed diário de vendas com 100k+ linhas | `Date`, `OrderId`, `Customer`, `Product`, `Category`, `TotalAmount`, `Status`, `GrossMargin`, etc. |
| **`CustomersFeed`** | CSV | Feed de fidelidade e avaliação com 100k+ linhas | `CustomerId`, `CustomerName`, `LoyaltyTier`, `TotalSpent`, `Score`, `TotalOrders`, `Segment`, etc. |
| **`RegionalTargets`** | Excel (`.xlsx`) | Planilha multi-abas de metas com 100k+ linhas | `RegionalTargets`, `QuarterlyBudget` |
| **`CommerceOps`** | Excel (`.xlsx`) | Planilha com 4 abas e 100k+ vendas e entregas | `Customers`, `ProductsStock`, `Sales`, `DeliveryLogs` |
| **`ProductCatalog`** | JSON (`.json`) | Catálogo multi-coleções com 100k+ produtos e reviews | `Products`, `Reviews` |
| **`LogisticsHub`** | XML (`.xml`) | Hub logístico com 100k+ remessas e transportadoras | `Carriers`, `Shipments` |
| **`GitHubFSharp`** | REST (`https://`) | API REST segura do GitHub (`fsprojects` repos) | `name`, `full_name`, `language`, `stargazers_count`, `forks_count`, `html_url` |

---

### ⌨️ Dicas de Execução e Atalhos

- **Executar Célula Ativa**: `Ctrl + Enter` (Windows/Linux) ou `Cmd + Enter` (macOS)
- **Executar e Avançar**: `Shift + Enter`
- **IntelliSense / Autocompletar**: `Ctrl + Space`
- **Ir para Definição dos Tipos**: `F12` em qualquer campo (ex: `.Country`, `.TotalAmount`)
- **Tabela Interativa**: No rodapé dos resultados, alterne entre **Grid** (ordenação/paginação interativa), **JSON** e **Raw Text**.
"""

                mkMarkdown """
## 1. ⚡ Consultas LINQ Fortemente Tipadas: Filtrando Clientes (`SampleStore`)

Ao usar `Connections.sql.SampleStore.Customers`, o **F# Query Studio** fornece suporte a autocompletar completo com IntelliSense e tipos fortemente tipados gerados em segundo plano.

> 💡 **Dica de Formatação**: Retornar registros anônimos (`{| ... |}`) produz cabeçalhos limpos com nomes descritivos e tipos explícitos na tabela interativa.
"""

                mkCode """
// 1. Conecta e consulta clientes do Brasil diretamente via Connections.sql
query {
    for c in Connections.sql.SampleStore.Customers do
    where (c.Country = "Brazil")
    sortBy c.FirstName
    select {|
        Id = c.Id
        NomeCliente = c.FirstName + " " + c.LastName
        Email = c.Email
        Cidade = c.City
        Pais = c.Country
    |}
}
"""

                mkMarkdown """
## 2. 🔗 Relacionamentos e Joins Relacionais (`SampleStore`)

Combine tabelas relacionais de forma natural utilizando a sintaxe `join ... on (...)` do F# LINQ dentro da mesma conexão.
"""

                mkCode """
// 2. Junção relacional entre Pedidos (Orders) e Clientes (Customers) dentro de SampleStore
query {
    for o in Connections.sql.SampleStore.Orders do
    join c in Connections.sql.SampleStore.Customers on (o.CustomerId = c.Id)
    sortByDescending o.TotalAmount
    select {|
        PedidoId = o.Id
        Cliente = c.FirstName + " " + c.LastName
        Pais = c.Country
        Data = o.OrderDate
        ValorTotal = o.TotalAmount
        Status = o.Status
    |}
}
"""

                mkMarkdown """
## 3. 🏬 Segunda Fonte SQLite: Armazéns e Estoque (`InventoryStore`)

Múltiplas fontes SQLite coexistem sem colisão de módulos ou tipos gerados.
Aqui consultamos a segunda base SQLite diretamente via `Connections.sql.InventoryStore`.
"""

                mkCode """
// 3. Consulta a segunda base SQLite (InventoryStore) diretamente via Connections.sql
query {
    for w in Connections.sql.InventoryStore.Warehouses do
    join s in Connections.sql.InventoryStore.StockLevels on (w.Id = s.WarehouseId)
    sortByDescending s.QuantityOnHand
    select {|
        Armazem = w.Code
        Cidade = w.City
        SkuProduto = s.ProductSku
        QuantidadeEmEstoque = s.QuantityOnHand
        PontoReposicao = s.ReorderLevel
        UltimoReabastecimento = s.LastRestocked
    |}
}
"""

                mkMarkdown """
## 4. 📊 Agregações Analíticas e Agrupamento (GroupBy)

Realize agregações analíticas (`sumBy`, `count`, `averageBy`) agrupando os dados por status do pedido sobre 100 mil registros.
"""

                mkCode """
// 4. Resumo financeiro: Faturamento total e ticket médio por Status do Pedido sobre 100k pedidos
let orders = Connections.sql.SampleStore.Orders |> Seq.toList

orders
|> List.groupBy (fun o -> o.Status)
|> List.map (fun (status, items) -> {|
    Status = status
    QtdPedidos = items.Length
    ValorTotal = items |> List.sumBy (fun i -> i.TotalAmount)
    TicketMedio = items |> List.averageBy (fun i -> i.TotalAmount)
|})
"""

                mkMarkdown """
## 5. 📁 Primeiro Arquivo CSV: Transações de Vendas (`SalesFeed`)

Arquivos CSV são mapeados como registros fortemente tipados com inferência automática de colunas numéricas, decimais e texto.
Acesse o feed diretamente via `Connections.csv.SalesFeed`.
"""

                mkCode """
// 5. Consulta o primeiro arquivo CSV (SalesFeed) diretamente
query {
    for row in Connections.csv.SalesFeed do
    where (row.Category = "Electronics")
    sortByDescending row.TotalAmount
    select {|
        Data = row.Date
        Cliente = row.Customer
        Produto = row.Product
        Quantidade = row.Quantity
        PrecoUnitario = row.UnitPrice
        ValorTotal = row.TotalAmount
        Status = row.Status
    |}
}
"""

                mkMarkdown """
## 6. 📋 Segundo Arquivo CSV: Feed de Fidelidade (`CustomersFeed`)

Uma segunda fonte CSV registrada ao lado de `SalesFeed`. Ambas possuem inferência de tipos e módulos independentes sem vazamento de contexto.
"""

                mkCode """
// 6. Consulta o segundo arquivo CSV (CustomersFeed) diretamente
query {
    for row in Connections.csv.CustomersFeed do
    where (row.TotalSpent >= 300.0M)
    sortByDescending row.Score
    select {|
        Cliente = row.CustomerName
        NivelFidelidade = row.LoyaltyTier
        GastoTotal = row.TotalSpent
        NotaSatisfacao = row.Score
        ClienteDesde = row.ActiveSince
        Avaliacao = row.Feedback
    |}
}
"""

                mkMarkdown """
## 7. 🔄 Cruzamento Multi-CSV com LINQ em Memória

Cruze dados de múltiplos arquivos CSV distintos (`SalesFeed` + `CustomersFeed`) com um join tipado em memória:
"""

                mkCode """
// 7. Correlação em memória entre transações de vendas e fidelidade de clientes
let salesRows = Connections.csv.SalesFeed |> Seq.toList
let loyaltyRows = Connections.csv.CustomersFeed |> Seq.toList

query {
    for s in salesRows do
    join l in loyaltyRows on (s.Customer = l.CustomerName)
    where (s.TotalAmount >= 100.0M)
    sortByDescending s.TotalAmount
    select {|
        PedidoId = s.OrderId
        Cliente = s.Customer
        NivelFidelidade = l.LoyaltyTier
        Produto = s.Product
        ValorPedido = s.TotalAmount
        Satisfacao = l.Score
    |}
}
"""

                mkMarkdown """
### ⚡ Pushdown e Joins com Múltiplas Propriedades com Visualização SQL (`toRawSql`)

O F# Query Studio traduz joins com múltiplas propriedades no formato `on ((a.param1, a.param2) = (b.param1, b.param2))` diretamente para SQL com índices (`_t0.col1 = _t1.col1 AND _t0.col2 = _t1.col2`), evitando produtos cartesianos e consumo excessivo de memória RAM.
Use `ResultKind.toRawSql` para visualizar a query SQL gerada antes de enviá-la ao banco de dados!
"""

                mkCode """
// 7.1 Inspeciona o SQL gerado com pushdown para joins antes da execução
let consultaComplexa =
    query {
        for o in Connections.sql.SampleStore.Orders do
        join i in Connections.sql.SampleStore.OrderItems on (o.Id = i.OrderId)
        where (o.TotalAmount >= 100.0f)
        select {| PedidoId = o.Id; ItemId = i.Id; ValorTotal = o.TotalAmount |}
    }

printfn "SQL Gerado pelo Pushdown:\n%s" (ResultKind.toRawSql consultaComplexa)
consultaComplexa
"""

                mkMarkdown """
## 8. 🛡️ Consultas SQL Parametrizadas Seguras (`SampleStore`)

Para queries complexas ou instruções SQL nativas, use `Connections.sql.<Conn>.rawQuery` com passagem segura de parâmetros nomeados para evitar injeção de SQL.
"""

                mkCode """
// 8. Query SQL nativa com parâmetros nomeados seguros executada via SampleStore
let sqlCatalogo = @"
SELECT 
    p.Name          AS Produto,
    c.Name          AS Categoria,
    p.Price         AS Preco,
    p.StockQuantity AS Estoque
FROM Products p
JOIN Categories c ON p.CategoryId = c.Id
WHERE p.Price >= @precoMinimo
ORDER BY p.Price DESC
"

Connections.sql.SampleStore.rawQuery(sqlCatalogo, [ "precoMinimo", box 100.0 ])
"""

                mkMarkdown """
## 9. 📊 Planilha Excel (`.xlsx`): Metas Regionais de Receita (`RegionalTargets`)

Arquivos Excel (`.xlsx`) têm suas abas (worksheets) inspecionadas e tipadas automaticamente via `Connections.excel.RegionalTargets`. Você pode consultar cada aba (`RegionalTargets` ou `QuarterlyBudget`) diretamente com LINQ!
"""

                mkCode """
// 9. Consulta a aba RegionalTargets da planilha Excel (.xlsx) diretamente via Connections.excel
query {
    for row in Connections.excel.RegionalTargets.RegionalTargets do
    where (row.GrowthRate >= 10.0M)
    sortByDescending row.AchievedRevenue
    select {|
        Regiao = row.Region
        Pais = row.Country
        Responsavel = row.LeadManager
        MetaAnual = row.AnnualTarget
        ReceitaRealizada = row.AchievedRevenue
        TaxaCrescimentoPct = row.GrowthRate
    |}
}
"""

                mkMarkdown """
## 10. 📦 Planilha Excel 4 Abas (`.xlsx`): Clientes, Produtos com Estoque, Vendas e Logs de Delivery (`CommerceOps`)

Cada conexão Excel funciona como um schema/container (`Connections.excel.CommerceOps.<Sheet>`) mapeado para tabelas prefixadas no SQLite (`CommerceOps_Customers`, `CommerceOps_ProductsStock`, `CommerceOps_Sales`, `CommerceOps_DeliveryLogs`). Veja como unir as 4 abas em uma única consulta LINQ com pushdown para SQL:
"""

                mkCode """
// 10. Join entre as 4 abas da planilha em inglês CommerceOps (Sales, Customers, ProductsStock, DeliveryLogs)
query {
    for s in Connections.excel.CommerceOps.Sales do
    join c in Connections.excel.CommerceOps.Customers on (s.CustomerId = c.CustomerId)
    join p in Connections.excel.CommerceOps.ProductsStock on (s.ProductId = p.ProductId)
    join d in Connections.excel.CommerceOps.DeliveryLogs on (s.SaleId = d.SaleId)
    sortByDescending s.TotalAmount
    select {|
        VendaId = s.SaleId
        Cliente = c.FullName
        Pais = c.Country
        Produto = p.ProductName
        EstoqueDisponivel = p.StockQuantity
        Quantidade = s.Quantity
        ValorTotal = s.TotalAmount
        Transportadora = d.Carrier
        CodigoRastreio = d.TrackingCode
        StatusEntrega = d.DeliveryStatus
    |}
}
"""

                mkMarkdown """
## 11. 🗂️ Documento JSON Local (`.json`): Catálogo Multi-Coleções e Avaliações (`ProductCatalog`)

Arquivos JSON locais (`.json`) funcionam exatamente como planilhas Excel: cada array descoberto é exposto como uma sub-propriedade fortemente tipada (`Connections.json.ProductCatalog.Products` e `Connections.json.ProductCatalog.Reviews`) com staging em SQLite (`ProductCatalog_Products`, `ProductCatalog_Reviews`) e pushdown de LINQ para SQL!
"""

                mkCode """
// 11. Join entre Products e Reviews dentro do arquivo JSON local (product_catalog.json) via Connections.json
query {
    for p in Connections.json.ProductCatalog.Products do
    join r in Connections.json.ProductCatalog.Reviews on (p.ProductId = r.ProductId)
    where (r.Stars >= 4)
    sortByDescending p.Price
    select {|
        Sku = p.Sku
        Produto = p.Title
        Categoria = p.Category
        Preco = p.Price
        Avaliador = r.Reviewer
        Estrelas = r.Stars
        Comentario = r.Comment
    |}
}
"""

                mkMarkdown """
## 12. 📄 Documento XML Local (`.xml`): Rede de Transportadoras e Remessas (`LogisticsHub`)

Arquivos XML locais (`.xml`) extraem automaticamente tanto atributos XML (`@CarrierId`, `@Code`) quanto elementos filhos (`<TrackingNumber>`, `<Destination>`, `<ShippingCost>`) em sub-propriedades fortemente tipadas (`Connections.xml.LogisticsHub.Carriers` e `Connections.xml.LogisticsHub.Shipments`).
"""

                mkCode """
// 12. Join entre Shipments e Carriers dentro do arquivo XML local (logistics_hub.xml) via Connections.xml
query {
    for s in Connections.xml.LogisticsHub.Shipments do
    join c in Connections.xml.LogisticsHub.Carriers on (s.CarrierId = c.CarrierId)
    sortByDescending s.ShippingCost
    select {|
        RemessaId = s.ShipmentId
        CodigoRastreio = s.TrackingNumber
        Transportadora = c.Name
        PaisHub = c.HubCountry
        Destino = s.Destination
        PesoKg = s.WeightKg
        CustoEnvio = s.ShippingCost
        Status = s.Status
    |}
}
"""

                mkMarkdown """
## 13. 🔒 API REST Segura do GitHub (`https://`): Repositórios da Comunidade F# (`GitHubFSharp`)

Endpoints REST/JSON em páginas seguras (`https://`) são inspecionados e mapeados em registros F# fortemente tipados. Aqui consultamos os repositórios públicos de F# em `https://api.github.com/orgs/fsprojects/repos` via `Connections.rest.GitHubFSharp`.
"""

                mkCode """
// 13. Consulta repositórios públicos da comunidade F# via API HTTPS do GitHub em Connections.rest
query {
    for repo in Connections.rest.GitHubFSharp do
    sortByDescending repo.stargazers_count
    select {|
        Repositorio = repo.full_name
        Linguagem = repo.language
        Estrelas = repo.stargazers_count
        Forks = repo.forks_count
        IssuesAbertas = repo.open_issues_count
        BranchPadrao = repo.default_branch
        Url = repo.html_url
    |}
}
"""

                mkMarkdown """
## 14. 🌐 Visão Consolidada Multi-Fontes de Alta Escala (100k+ Registros Cada)

Como as consultas no F# Query Studio rodam em uma sessão FSI unificada, você pode cruzar e comparar dados das 9 fontes simultaneamente:
"""

                mkCode """
// 14. Resumo consolidado das 9 fontes de dados registradas
let sampleStoreOrders = Connections.sql.SampleStore.Orders |> Seq.length
let inventoryWarehouses = Connections.sql.InventoryStore.Warehouses |> Seq.length
let salesCount = Connections.csv.SalesFeed |> Seq.length
let loyaltyCount = Connections.csv.CustomersFeed |> Seq.length
let excelTargetsCount = Connections.excel.RegionalTargets.RegionalTargets |> Seq.length
let commerceSalesCount = Connections.excel.CommerceOps.Sales |> Seq.length
let jsonProductsCount = Connections.json.ProductCatalog.Products |> Seq.length
let xmlShipmentsCount = Connections.xml.LogisticsHub.Shipments |> Seq.length
let githubReposCount = Connections.rest.GitHubFSharp |> Seq.length

[
    {| Fonte = "SampleStore";     Tipo = "SQLite (Relacional)"; TotalRegistros = sampleStoreOrders;   EntidadePrincipal = "Orders" |}
    {| Fonte = "InventoryStore";  Tipo = "SQLite (Relacional)"; TotalRegistros = inventoryWarehouses; EntidadePrincipal = "Warehouses" |}
    {| Fonte = "SalesFeed";       Tipo = "CSV (Arquivo Plano)"; TotalRegistros = salesCount;          EntidadePrincipal = "Transações" |}
    {| Fonte = "CustomersFeed";   Tipo = "CSV (Arquivo Plano)"; TotalRegistros = loyaltyCount;        EntidadePrincipal = "Perfis de Fidelidade" |}
    {| Fonte = "RegionalTargets"; Tipo = "Excel (.xlsx)";       TotalRegistros = excelTargetsCount;   EntidadePrincipal = "RegionalTargets" |}
    {| Fonte = "CommerceOps";     Tipo = "Excel (.xlsx)";       TotalRegistros = commerceSalesCount;  EntidadePrincipal = "Customers / Stock / Sales / Delivery" |}
    {| Fonte = "ProductCatalog";  Tipo = "JSON (.json)";        TotalRegistros = jsonProductsCount;   EntidadePrincipal = "Products / Reviews" |}
    {| Fonte = "LogisticsHub";    Tipo = "XML (.xml)";          TotalRegistros = xmlShipmentsCount;   EntidadePrincipal = "Carriers / Shipments" |}
    {| Fonte = "GitHubFSharp";    Tipo = "REST JSON (HTTPS)";   TotalRegistros = githubReposCount;    EntidadePrincipal = "Repositórios GitHub" |}
]
"""

                mkMarkdown """
## 15. 📁 Operações com Arquivos e Manipulação com `System.IO` (`data_sources`)

O F# Query Studio garante que o diretório de trabalho (`System.Environment.CurrentDirectory`) esteja sempre alinhado à raiz do seu workspace. Você pode criar, escrever e manipular arquivos locais utilizando as APIs nativas do .NET (`System.IO.File`, `FileStream`, `StreamWriter`) sem bloqueios de arquivo (*file locks*) entre células!

Neste exemplo, lemos registros brutos de `data_sources/sales_feed.csv`, processamos o cabeçalho e criamos um novo arquivo consolidado `data_sources/top_sales_exportado.csv` via `System.IO.File.Create`.
"""

                mkCode """
// 15. Cria um arquivo resumo a partir de data_sources/sales_feed.csv usando System.IO.File.Create
open System.IO
open System.Text

let salesFeedPath = Path.Combine("data_sources", "sales_feed.csv")
let summaryPath = Path.Combine("data_sources", "top_sales_exportado.csv")

// Lê linhas de data_sources e processa dados com descarte explícito da stream
let summaryRows =
    File.ReadLines(salesFeedPath)
    |> Seq.skip 1
    |> Seq.take 100
    |> Seq.map (fun line -> line.Split(','))
    |> Seq.filter (fun p -> p.Length >= 7)
    |> Seq.map (fun p ->
        {|
            PedidoId = p.[1]
            Cliente = p.[2]
            Produto = p.[3]
            Valor = p.[5]
            Status = p.[6]
        |})
    |> Seq.toList

do
    use stream = File.Create(summaryPath)
    use writer = new StreamWriter(stream, Encoding.UTF8)
    writer.WriteLine("PedidoId,Cliente,Produto,Valor,Status")
    for r in summaryRows do
        writer.WriteLine($"{r.PedidoId},{r.Cliente},{r.Produto},{r.Valor},{r.Status}")
    writer.Flush()

printfn "Arquivo gerado com sucesso: %s (Total de registros: %d, Tamanho: %d bytes)"
    summaryPath summaryRows.Length (FileInfo(summaryPath).Length)

summaryRows
"""

                mkMarkdown """
## 16. 🔄 Validação de Arquivo Livre de Lock Entre Células do Notebook

Em seguida, nesta célula subsequente, lemos, inspecionamos e atualizamos o arquivo `top_sales_exportado.csv` criado na célula anterior. O F# Query Studio descarta automaticamente cursores anteriores e handles de sistema operacional, garantindo que operações de releitura (`File.ReadAllLines`) e escrita/adição (`File.AppendAllText`) ocorram sem violações de compartilhamento (*sharing violation / IOException*).
"""

                mkCode """
// 16. Valida acesso livre de locks entre células: releitura, acréscimo e projeção em tabela
open System.IO

let summaryPath = Path.Combine("data_sources", "top_sales_exportado.csv")

// Relê o arquivo gerado na célula anterior
let lines = File.ReadAllLines(summaryPath)

// Acrescenta metadados ao final para certificar permissão de escrita imediata
File.AppendAllText(summaryPath, "# Processado pelo F# Query Studio\n")

printfn "Acesso sem lock confirmado para %s: lidas %d linhas, tamanho atualizado %d bytes"
    summaryPath lines.Length (FileInfo(summaryPath).Length)

lines
|> Seq.skip 1
|> Seq.filter (fun l -> not (l.StartsWith("#")))
|> Seq.take 20
|> Seq.map (fun l ->
    let p = l.Split(',')
    {|
        PedidoId = p.[0]
        Cliente = p.[1]
        Produto = p.[2]
        Valor = p.[3]
        Status = p.[4]
    |})
|> Seq.toList
"""

                mkMarkdown """
---

### 📚 Documentação & Recursos Oficiais

- 📖 [Documentação Oficial de F# (Microsoft Learn)](https://learn.microsoft.com/pt-br/dotnet/fsharp/)
- 🔍 [Guia de Expressões de Consulta F# / LINQ](https://learn.microsoft.com/pt-br/dotnet/fsharp/language-reference/query-expressions)
- ⚡ [Visão Geral do F# Interactive (FSI)](https://learn.microsoft.com/pt-br/dotnet/fsharp/tools/fsharp-interactive/)
"""
            ]
        serializeNotebook cells

    let createSampleStoreDatabase (filePath: string) : unit =
        let dir = Path.GetDirectoryName(filePath)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        let connectionString = $"Data Source={filePath};Pooling=False"
        use connection = new SqliteConnection(connectionString)
        connection.Open()

        use pragmaCmd = connection.CreateCommand()
        pragmaCmd.CommandText <- "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;"
        pragmaCmd.ExecuteNonQuery() |> ignore

        use createCmd = connection.CreateCommand()
        createCmd.CommandText <- createTablesSql
        createCmd.ExecuteNonQuery() |> ignore

        use tx = connection.BeginTransaction()

        // 1. Categories (25 categories)
        use catCmd = connection.CreateCommand()
        catCmd.Transaction <- tx
        catCmd.CommandText <- "INSERT OR REPLACE INTO Categories VALUES (@id, @name, @desc, @code, @dept, @active, @created);"
        let pCatId = catCmd.Parameters.Add("@id", SqliteType.Integer)
        let pCatName = catCmd.Parameters.Add("@name", SqliteType.Text)
        let pCatDesc = catCmd.Parameters.Add("@desc", SqliteType.Text)
        let pCatCode = catCmd.Parameters.Add("@code", SqliteType.Text)
        let pCatDept = catCmd.Parameters.Add("@dept", SqliteType.Text)
        let pCatActive = catCmd.Parameters.Add("@active", SqliteType.Integer)
        let pCatCreated = catCmd.Parameters.Add("@created", SqliteType.Text)

        let knownCategories = [|
            (1, "Electronics", "Gadgets, devices, and computer peripherals", "CAT-ELEC", "Technology")
            (2, "Books", "Technical and programming books", "CAT-BOOK", "Media")
            (3, "Home & Kitchen", "Appliances and household items", "CAT-HOME", "Home")
            (4, "Sports & Fitness", "Sports apparel, footwear, and accessories", "CAT-SPRT", "Leisure")
        |]
        for id, name, desc, code, dept in knownCategories do
            pCatId.Value <- id; pCatName.Value <- name; pCatDesc.Value <- desc; pCatCode.Value <- code
            pCatDept.Value <- dept; pCatActive.Value <- 1; pCatCreated.Value <- "2025-01-01T00:00:00Z"
            catCmd.ExecuteNonQuery() |> ignore

        for i = 5 to 25 do
            pCatId.Value <- i
            pCatName.Value <- $"Category {i}"
            pCatDesc.Value <- $"Expanded product category classification {i}"
            pCatCode.Value <- $"CAT-{i:D3}"
            pCatDept.Value <- "General Retail"
            pCatActive.Value <- 1
            pCatCreated.Value <- "2025-01-01T00:00:00Z"
            catCmd.ExecuteNonQuery() |> ignore

        // 2. Products (1,000 products)
        use prodCmd = connection.CreateCommand()
        prodCmd.Transaction <- tx
        prodCmd.CommandText <- "INSERT OR REPLACE INTO Products VALUES (@id, @name, @catId, @price, @stock, @sku, @brand, @cost, @weight, @disc, @rating);"
        let pProdId = prodCmd.Parameters.Add("@id", SqliteType.Integer)
        let pProdName = prodCmd.Parameters.Add("@name", SqliteType.Text)
        let pProdCatId = prodCmd.Parameters.Add("@catId", SqliteType.Integer)
        let pProdPrice = prodCmd.Parameters.Add("@price", SqliteType.Real)
        let pProdStock = prodCmd.Parameters.Add("@stock", SqliteType.Integer)
        let pProdSku = prodCmd.Parameters.Add("@sku", SqliteType.Text)
        let pProdBrand = prodCmd.Parameters.Add("@brand", SqliteType.Text)
        let pProdCost = prodCmd.Parameters.Add("@cost", SqliteType.Real)
        let pProdWeight = prodCmd.Parameters.Add("@weight", SqliteType.Real)
        let pProdDisc = prodCmd.Parameters.Add("@disc", SqliteType.Integer)
        let pProdRating = prodCmd.Parameters.Add("@rating", SqliteType.Real)

        let knownProducts = [|
            (1, "Laptop Pro 16", 1, 1499.99, 25, "LAPTOP-PRO-16", "Apple", 950.0, 2.1, 0, 4.8)
            (2, "Wireless Ergonomic Mouse", 1, 49.90, 150, "MOUSE-WL-ERG", "Logitech", 22.0, 0.15, 0, 4.6)
            (3, "Mechanical Keyboard RGB", 1, 119.50, 75, "KEYBOARD-RGB", "Keychron", 55.0, 0.85, 0, 4.7)
            (4, "Noise-Cancelling Headphones", 1, 249.00, 40, "HEADPHONES-NC", "Sony", 120.0, 0.25, 0, 4.9)
            (5, "Expert F# 4.0", 2, 54.99, 30, "BOOK-EXP-FS4", "Apress", 25.0, 0.60, 0, 4.9)
            (6, "Domain Modeling Made Functional", 2, 45.00, 60, "BOOK-DM-FUNC", "Pragmatic", 20.0, 0.45, 0, 5.0)
            (7, "F# Deep Dives", 2, 42.50, 20, "BOOK-FS-DEEP", "Manning", 18.0, 0.50, 0, 4.7)
            (8, "Espresso Coffee Machine", 3, 189.90, 15, "COFFEE-MACH-01", "Breville", 90.0, 4.20, 0, 4.5)
            (9, "Insulated Stainless Tumbler", 3, 24.99, 200, "TUMBLER-SS-02", "Stanley", 8.0, 0.35, 0, 4.6)
            (10, "Running Shoes Pro", 4, 129.99, 50, "SHOES-PRO-RUN", "Nike", 50.0, 0.70, 0, 4.8)
        |]
        for id, name, catId, price, stock, sku, brand, cost, weight, disc, rating in knownProducts do
            pProdId.Value <- id; pProdName.Value <- name; pProdCatId.Value <- catId; pProdPrice.Value <- price
            pProdStock.Value <- stock; pProdSku.Value <- sku; pProdBrand.Value <- brand; pProdCost.Value <- cost
            pProdWeight.Value <- weight; pProdDisc.Value <- disc; pProdRating.Value <- rating
            prodCmd.ExecuteNonQuery() |> ignore

        for i = 11 to 1000 do
            let price = float ((i % 400) + 15) + 0.99
            pProdId.Value <- i
            pProdName.Value <- $"Catalog Product {i}"
            pProdCatId.Value <- ((i % 25) + 1)
            pProdPrice.Value <- price
            pProdStock.Value <- ((i % 300) + 10)
            pProdSku.Value <- $"SKU-PRD-{i:D5}"
            pProdBrand.Value <- if i % 2 = 0 then "CoreBrand" else "ApexGear"
            pProdCost.Value <- price * 0.55
            pProdWeight.Value <- float ((i % 30) + 1) * 0.1
            pProdDisc.Value <- if i % 10 = 0 then 1 else 0
            pProdRating.Value <- 4.0 + float (i % 10) * 0.1
            prodCmd.ExecuteNonQuery() |> ignore

        // 3. Customers (10,000 customers)
        use custCmd = connection.CreateCommand()
        custCmd.Transaction <- tx
        custCmd.CommandText <- "INSERT OR REPLACE INTO Customers VALUES (@id, @fn, @ln, @email, @city, @country, @created, @phone, @post, @state, @points, @type, @active);"
        let pCustId = custCmd.Parameters.Add("@id", SqliteType.Integer)
        let pCustFn = custCmd.Parameters.Add("@fn", SqliteType.Text)
        let pCustLn = custCmd.Parameters.Add("@ln", SqliteType.Text)
        let pCustEmail = custCmd.Parameters.Add("@email", SqliteType.Text)
        let pCustCity = custCmd.Parameters.Add("@city", SqliteType.Text)
        let pCustCountry = custCmd.Parameters.Add("@country", SqliteType.Text)
        let pCustCreated = custCmd.Parameters.Add("@created", SqliteType.Text)
        let pCustPhone = custCmd.Parameters.Add("@phone", SqliteType.Text)
        let pCustPost = custCmd.Parameters.Add("@post", SqliteType.Text)
        let pCustState = custCmd.Parameters.Add("@state", SqliteType.Text)
        let pCustPoints = custCmd.Parameters.Add("@points", SqliteType.Integer)
        let pCustType = custCmd.Parameters.Add("@type", SqliteType.Text)
        let pCustActive = custCmd.Parameters.Add("@active", SqliteType.Integer)

        let knownCustomers = [|
            (1, "Carlos", "Silva", "carlos.silva@example.com", "São Paulo", "Brazil", "2025-11-10T10:00:00Z", "+55 11 98765-4321", "01310-100", "SP", 2500, "Platinum", 1)
            (2, "Alice", "Smith", "alice.smith@example.co.uk", "London", "UK", "2025-12-01T14:30:00Z", "+44 20 7946 0912", "EC1A 1BB", "Greater London", 1800, "Gold", 1)
            (3, "John", "Doe", "john.doe@example.com", "New York", "USA", "2026-01-05T09:15:00Z", "+1 212-555-0143", "10001", "NY", 950, "Silver", 1)
            (4, "Maria", "Garcia", "maria.garcia@example.es", "Madrid", "Spain", "2026-01-12T16:45:00Z", "+34 91 123 4567", "28001", "Madrid", 1450, "Gold", 1)
            (5, "Yuki", "Tanaka", "yuki.tanaka@example.jp", "Tokyo", "Japan", "2026-02-01T11:20:00Z", "+81 3 5555 0198", "100-0001", "Tokyo", 800, "Silver", 1)
            (6, "Ana", "Pereira", "ana.pereira@example.com", "Rio de Janeiro", "Brazil", "2026-02-18T13:10:00Z", "+55 21 99876-5432", "20040-002", "RJ", 650, "Bronze", 1)
        |]
        for id, fn, ln, email, city, country, created, phone, post, state, points, ctype, active in knownCustomers do
            pCustId.Value <- id; pCustFn.Value <- fn; pCustLn.Value <- ln; pCustEmail.Value <- email
            pCustCity.Value <- city; pCustCountry.Value <- country; pCustCreated.Value <- created
            pCustPhone.Value <- phone; pCustPost.Value <- post; pCustState.Value <- state
            pCustPoints.Value <- points; pCustType.Value <- ctype; pCustActive.Value <- active
            custCmd.ExecuteNonQuery() |> ignore

        let countries = [| "USA"; "UK"; "Spain"; "Japan"; "Germany"; "Canada"; "Australia"; "France" |]
        let cities = [| "New York"; "London"; "Madrid"; "Tokyo"; "Berlin"; "Toronto"; "Sydney"; "Paris" |]
        let customerTypes = [| "Bronze"; "Silver"; "Gold"; "Platinum" |]
        for i = 7 to 10000 do
            let cIdx = i % 8
            pCustId.Value <- i
            pCustFn.Value <- $"CustomerFirstName{i}"
            pCustLn.Value <- $"CustomerLastName{i}"
            pCustEmail.Value <- $"customer{i}@sampledomain.com"
            pCustCity.Value <- cities.[cIdx]
            pCustCountry.Value <- countries.[cIdx]
            pCustCreated.Value <- "2026-01-01T00:00:00Z"
            pCustPhone.Value <- $"+1 555-{i:D4}"
            pCustPost.Value <- $"{10000 + (i % 90000)}"
            pCustState.Value <- "Region"
            pCustPoints.Value <- (i * 15) % 5000
            pCustType.Value <- customerTypes.[i % 4]
            pCustActive.Value <- 1
            custCmd.ExecuteNonQuery() |> ignore

        // 4. Orders (100,000 orders)
        use ordCmd = connection.CreateCommand()
        ordCmd.Transaction <- tx
        ordCmd.CommandText <- "INSERT OR REPLACE INTO Orders VALUES (@id, @cid, @date, @total, @status, @num, @pay, @ship, @disc, @cur, @chan, @notes);"
        let pOrdId = ordCmd.Parameters.Add("@id", SqliteType.Integer)
        let pOrdCid = ordCmd.Parameters.Add("@cid", SqliteType.Integer)
        let pOrdDate = ordCmd.Parameters.Add("@date", SqliteType.Text)
        let pOrdTotal = ordCmd.Parameters.Add("@total", SqliteType.Real)
        let pOrdStatus = ordCmd.Parameters.Add("@status", SqliteType.Text)
        let pOrdNum = ordCmd.Parameters.Add("@num", SqliteType.Text)
        let pOrdPay = ordCmd.Parameters.Add("@pay", SqliteType.Text)
        let pOrdShip = ordCmd.Parameters.Add("@ship", SqliteType.Real)
        let pOrdDisc = ordCmd.Parameters.Add("@disc", SqliteType.Real)
        let pOrdCur = ordCmd.Parameters.Add("@cur", SqliteType.Text)
        let pOrdChan = ordCmd.Parameters.Add("@chan", SqliteType.Text)
        let pOrdNotes = ordCmd.Parameters.Add("@notes", SqliteType.Text)

        let knownOrders = [|
            (101, 1, "2026-03-01T10:00:00Z", 1549.89, "Completed")
            (102, 2, "2026-03-05T15:20:00Z", 99.99, "Completed")
            (103, 3, "2026-03-10T11:00:00Z", 298.90, "Completed")
            (104, 1, "2026-03-15T09:40:00Z", 189.90, "Processing")
            (105, 4, "2026-03-20T17:15:00Z", 129.99, "Shipped")
            (106, 5, "2026-03-25T12:00:00Z", 119.50, "Completed")
            (107, 6, "2026-04-01T14:50:00Z", 87.50, "Processing")
            (108, 3, "2026-04-05T16:30:00Z", 249.00, "Pending")
        |]
        for id, cid, dt, tot, st in knownOrders do
            pOrdId.Value <- id; pOrdCid.Value <- cid; pOrdDate.Value <- dt; pOrdTotal.Value <- tot; pOrdStatus.Value <- st
            pOrdNum.Value <- $"ORD-2026-{id:D6}"; pOrdPay.Value <- "Credit Card"; pOrdShip.Value <- 15.0; pOrdDisc.Value <- 0.0
            pOrdCur.Value <- "USD"; pOrdChan.Value <- "Web"; pOrdNotes.Value <- "Express standard delivery"
            ordCmd.ExecuteNonQuery() |> ignore

        let statuses = [| "Completed"; "Processing"; "Shipped"; "Pending" |]
        let payMethods = [| "Credit Card"; "Debit Card"; "PayPal"; "Wire Transfer"; "Pix" |]
        let channels = [| "Web"; "Mobile App"; "Marketplace"; "In-Store" |]
        for i = 109 to 100100 do
            let status = statuses.[i % 4]
            let total = float ((i % 1000) * 8 + 25) + 0.99
            pOrdId.Value <- i
            pOrdCid.Value <- ((i % 10000) + 1)
            pOrdDate.Value <- "2026-03-15T12:00:00Z"
            pOrdTotal.Value <- total
            pOrdStatus.Value <- status
            pOrdNum.Value <- $"ORD-2026-{i:D6}"
            pOrdPay.Value <- payMethods.[i % 5]
            pOrdShip.Value <- float ((i % 25) + 5)
            pOrdDisc.Value <- float (i % 20)
            pOrdCur.Value <- "USD"
            pOrdChan.Value <- channels.[i % 4]
            pOrdNotes.Value <- "High-volume transactional order"
            ordCmd.ExecuteNonQuery() |> ignore

        // 5. OrderItems (100,000 items)
        use itemCmd = connection.CreateCommand()
        itemCmd.Transaction <- tx
        itemCmd.CommandText <- "INSERT OR REPLACE INTO OrderItems VALUES (@id, @oid, @pid, @qty, @price, @disc, @total, @tax, @sku);"
        let pItId = itemCmd.Parameters.Add("@id", SqliteType.Integer)
        let pItOid = itemCmd.Parameters.Add("@oid", SqliteType.Integer)
        let pItPid = itemCmd.Parameters.Add("@pid", SqliteType.Integer)
        let pItQty = itemCmd.Parameters.Add("@qty", SqliteType.Integer)
        let pItPrice = itemCmd.Parameters.Add("@price", SqliteType.Real)
        let pItDisc = itemCmd.Parameters.Add("@disc", SqliteType.Real)
        let pItTotal = itemCmd.Parameters.Add("@total", SqliteType.Real)
        let pItTax = itemCmd.Parameters.Add("@tax", SqliteType.Real)
        let pItSku = itemCmd.Parameters.Add("@sku", SqliteType.Text)

        let knownItems = [|
            (1, 101, 1, 1, 1499.99)
            (2, 101, 2, 1, 49.90)
            (3, 102, 5, 1, 54.99)
            (4, 102, 6, 1, 45.00)
            (5, 103, 2, 1, 49.90)
            (6, 103, 4, 1, 249.00)
            (7, 104, 8, 1, 189.90)
            (8, 105, 10, 1, 129.99)
            (9, 106, 3, 1, 119.50)
            (10, 107, 6, 1, 45.00)
            (11, 107, 7, 1, 42.50)
            (12, 108, 4, 1, 249.00)
        |]
        for id, oid, pid, qty, price in knownItems do
            pItId.Value <- id; pItOid.Value <- oid; pItPid.Value <- pid; pItQty.Value <- qty; pItPrice.Value <- price
            pItDisc.Value <- 0.0; pItTotal.Value <- price * float qty; pItTax.Value <- price * 0.08; pItSku.Value <- $"SKU-PRD-{pid:D5}"
            itemCmd.ExecuteNonQuery() |> ignore

        for i = 13 to 100000 do
            let pid = (i % 1000) + 1
            let qty = (i % 5) + 1
            let price = float ((i % 250) + 15) + 0.50
            pItId.Value <- i
            pItOid.Value <- ((i % 100000) + 101)
            pItPid.Value <- pid
            pItQty.Value <- qty
            pItPrice.Value <- price
            pItDisc.Value <- 0.05
            pItTotal.Value <- price * float qty
            pItTax.Value <- price * 0.08
            pItSku.Value <- $"SKU-PRD-{pid:D5}"
            itemCmd.ExecuteNonQuery() |> ignore

        tx.Commit()
        SqliteConnection.ClearAllPools()

    let createInventoryDatabase (filePath: string) : unit =
        let dir = Path.GetDirectoryName(filePath)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        let connectionString = $"Data Source={filePath};Pooling=False"
        use connection = new SqliteConnection(connectionString)
        connection.Open()

        use pragmaCmd = connection.CreateCommand()
        pragmaCmd.CommandText <- "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;"
        pragmaCmd.ExecuteNonQuery() |> ignore

        use createCmd = connection.CreateCommand()
        createCmd.CommandText <- createInventoryTablesSql
        createCmd.ExecuteNonQuery() |> ignore

        use tx = connection.BeginTransaction()

        // 1. Warehouses (100,000 warehouses)
        use whCmd = connection.CreateCommand()
        whCmd.Transaction <- tx
        whCmd.CommandText <- "INSERT OR REPLACE INTO Warehouses VALUES (@id, @code, @city, @country, @cap, @name, @addr, @post, @mgr, @stat, @refcap);"
        let pWhId = whCmd.Parameters.Add("@id", SqliteType.Integer)
        let pWhCode = whCmd.Parameters.Add("@code", SqliteType.Text)
        let pWhCity = whCmd.Parameters.Add("@city", SqliteType.Text)
        let pWhCountry = whCmd.Parameters.Add("@country", SqliteType.Text)
        let pWhCap = whCmd.Parameters.Add("@cap", SqliteType.Integer)
        let pWhName = whCmd.Parameters.Add("@name", SqliteType.Text)
        let pWhAddr = whCmd.Parameters.Add("@addr", SqliteType.Text)
        let pWhPost = whCmd.Parameters.Add("@post", SqliteType.Text)
        let pWhMgr = whCmd.Parameters.Add("@mgr", SqliteType.Text)
        let pWhStat = whCmd.Parameters.Add("@stat", SqliteType.Text)
        let pWhRefCap = whCmd.Parameters.Add("@refcap", SqliteType.Integer)

        let knownWarehouses = [|
            (1, "WH-SP-01", "São Paulo", "Brazil", 50000, "Paulista Hub Central", "Av. Paulista, 1000", "01310-100", "Rodrigo Alves", "Active", 15000)
            (2, "WH-LON-01", "London", "UK", 35000, "Thames Logistics Gateway", "12 Royal Docks", "E16 2QU", "Victoria Sterling", "Active", 10000)
            (3, "WH-NYC-01", "New York", "USA", 45000, "Hudson River Distribution", "500 West 34th St", "10001", "Michael Chang", "Active", 12000)
            (4, "WH-MAD-01", "Madrid", "Spain", 25000, "Iberia Air Express Hub", "Calle de Alcala, 45", "28014", "Elena Fernandez", "Active", 8000)
        |]
        for id, code, city, country, cap, name, addr, post, mgr, stat, refcap in knownWarehouses do
            pWhId.Value <- id; pWhCode.Value <- code; pWhCity.Value <- city; pWhCountry.Value <- country
            pWhCap.Value <- cap; pWhName.Value <- name; pWhAddr.Value <- addr; pWhPost.Value <- post
            pWhMgr.Value <- mgr; pWhStat.Value <- stat; pWhRefCap.Value <- refcap
            whCmd.ExecuteNonQuery() |> ignore

        let whCities = [| "São Paulo"; "London"; "New York"; "Madrid"; "Tokyo"; "Berlin"; "Paris"; "Singapore" |]
        let whCountries = [| "Brazil"; "UK"; "USA"; "Spain"; "Japan"; "Germany"; "France"; "Singapore" |]
        for i = 5 to 100000 do
            let cIdx = i % 8
            pWhId.Value <- i
            pWhCode.Value <- $"WH-{i:D5}"
            pWhCity.Value <- whCities.[cIdx]
            pWhCountry.Value <- whCountries.[cIdx]
            pWhCap.Value <- ((i % 50000) + 10000)
            pWhName.Value <- $"Global Distribution Facility {i}"
            pWhAddr.Value <- $"Industrial Park Way, Suite {i}"
            pWhPost.Value <- $"{20000 + (i % 70000)}"
            pWhMgr.Value <- $"Manager {i}"
            pWhStat.Value <- "Active"
            pWhRefCap.Value <- ((i % 15000) + 2000)
            whCmd.ExecuteNonQuery() |> ignore

        // 2. StockLevels (100,000 stock levels)
        use stockCmd = connection.CreateCommand()
        stockCmd.Transaction <- tx
        stockCmd.CommandText <- "INSERT OR REPLACE INTO StockLevels VALUES (@id, @wid, @sku, @qty, @reorder, @restocked, @bin, @res, @avail, @cost, @val, @safety, @stat);"
        let pStkId = stockCmd.Parameters.Add("@id", SqliteType.Integer)
        let pStkWid = stockCmd.Parameters.Add("@wid", SqliteType.Integer)
        let pStkSku = stockCmd.Parameters.Add("@sku", SqliteType.Text)
        let pStkQty = stockCmd.Parameters.Add("@qty", SqliteType.Integer)
        let pStkReorder = stockCmd.Parameters.Add("@reorder", SqliteType.Integer)
        let pStkRestocked = stockCmd.Parameters.Add("@restocked", SqliteType.Text)
        let pStkBin = stockCmd.Parameters.Add("@bin", SqliteType.Text)
        let pStkRes = stockCmd.Parameters.Add("@res", SqliteType.Integer)
        let pStkAvail = stockCmd.Parameters.Add("@avail", SqliteType.Integer)
        let pStkCost = stockCmd.Parameters.Add("@cost", SqliteType.Real)
        let pStkVal = stockCmd.Parameters.Add("@val", SqliteType.Real)
        let pStkSafety = stockCmd.Parameters.Add("@safety", SqliteType.Integer)
        let pStkStat = stockCmd.Parameters.Add("@stat", SqliteType.Text)

        let knownStocks = [|
            (1, 1, "LAPTOP-PRO-16", 45, 10, "2026-03-01T08:00:00Z")
            (2, 1, "MOUSE-WL-ERG", 220, 50, "2026-03-05T09:30:00Z")
            (3, 2, "BOOK-EXP-FS4", 80, 20, "2026-02-28T14:00:00Z")
            (4, 2, "BOOK-DM-FUNC", 110, 30, "2026-03-10T11:15:00Z")
            (5, 3, "HEADPHONES-NC", 65, 15, "2026-03-12T16:00:00Z")
            (6, 4, "SHOES-PRO-RUN", 90, 25, "2026-03-15T10:00:00Z")
        |]
        for id, wid, sku, qty, reorder, restocked in knownStocks do
            pStkId.Value <- id; pStkWid.Value <- wid; pStkSku.Value <- sku; pStkQty.Value <- qty
            pStkReorder.Value <- reorder; pStkRestocked.Value <- restocked; pStkBin.Value <- $"BIN-{wid:D2}-A01"
            pStkRes.Value <- 5; pStkAvail.Value <- qty - 5; pStkCost.Value <- 100.0
            pStkVal.Value <- float qty * 100.0; pStkSafety.Value <- reorder * 2; pStkStat.Value <- "Optimal"
            stockCmd.ExecuteNonQuery() |> ignore

        for i = 7 to 100000 do
            let wid = (i % 100000) + 1
            let qty = (i % 500) + 20
            let reorder = (i % 40) + 10
            let cost = float ((i % 150) + 10) + 0.50
            pStkId.Value <- i
            pStkWid.Value <- wid
            pStkSku.Value <- $"SKU-INV-{i:D6}"
            pStkQty.Value <- qty
            pStkReorder.Value <- reorder
            pStkRestocked.Value <- "2026-03-15T10:00:00Z"
            pStkBin.Value <- $"BIN-{(wid % 90 + 10):D2}-Z{(i % 20 + 1):D2}"
            pStkRes.Value <- 5
            pStkAvail.Value <- qty - 5
            pStkCost.Value <- cost
            pStkVal.Value <- float qty * cost
            pStkSafety.Value <- reorder * 2
            pStkStat.Value <- if qty > reorder then "Optimal" else "ReorderRequired"
            stockCmd.ExecuteNonQuery() |> ignore

        // 3. Suppliers (1,000 suppliers)
        use supCmd = connection.CreateCommand()
        supCmd.Transaction <- tx
        supCmd.CommandText <- "INSERT OR REPLACE INTO Suppliers VALUES (@id, @comp, @email, @country, @rating, @cname, @phone, @city, @terms, @tax, @pref);"
        let pSupId = supCmd.Parameters.Add("@id", SqliteType.Integer)
        let pSupComp = supCmd.Parameters.Add("@comp", SqliteType.Text)
        let pSupEmail = supCmd.Parameters.Add("@email", SqliteType.Text)
        let pSupCountry = supCmd.Parameters.Add("@country", SqliteType.Text)
        let pSupRating = supCmd.Parameters.Add("@rating", SqliteType.Real)
        let pSupCName = supCmd.Parameters.Add("@cname", SqliteType.Text)
        let pSupPhone = supCmd.Parameters.Add("@phone", SqliteType.Text)
        let pSupCity = supCmd.Parameters.Add("@city", SqliteType.Text)
        let pSupTerms = supCmd.Parameters.Add("@terms", SqliteType.Integer)
        let pSupTax = supCmd.Parameters.Add("@tax", SqliteType.Text)
        let pSupPref = supCmd.Parameters.Add("@pref", SqliteType.Integer)

        let knownSuppliers = [|
            (1, "Global Tech Logistics", "contact@globaltech.com", "Brazil", 4.8)
            (2, "EuroPublishing Group", "supply@europub.co.uk", "UK", 4.9)
            (3, "Apex Electronics Corp", "sales@apexelectronics.com", "USA", 4.6)
            (4, "Iberia Sports Supply", "info@iberiasports.es", "Spain", 4.7)
        |]
        for id, comp, email, country, rating in knownSuppliers do
            pSupId.Value <- id; pSupComp.Value <- comp; pSupEmail.Value <- email; pSupCountry.Value <- country
            pSupRating.Value <- rating; pSupCName.Value <- "Key Account Lead"; pSupPhone.Value <- "+1 800-555-0199"
            pSupCity.Value <- "Logistics Center"; pSupTerms.Value <- 30; pSupTax.Value <- $"TAX-{id:D6}"; pSupPref.Value <- 1
            supCmd.ExecuteNonQuery() |> ignore

        for i = 5 to 1000 do
            pSupId.Value <- i
            pSupComp.Value <- $"Supplier Partner {i}"
            pSupEmail.Value <- $"partner{i}@supplynetwork.com"
            pSupCountry.Value <- whCountries.[i % 8]
            pSupRating.Value <- 4.0 + float (i % 10) * 0.1
            pSupCName.Value <- $"Representative {i}"
            pSupPhone.Value <- $"+1 800-555-{i:D4}"
            pSupCity.Value <- whCities.[i % 8]
            pSupTerms.Value <- if i % 2 = 0 then 30 else 60
            pSupTax.Value <- $"TAX-SUP-{i:D5}"
            pSupPref.Value <- if i % 3 = 0 then 1 else 0
            supCmd.ExecuteNonQuery() |> ignore

        tx.Commit()
        SqliteConnection.ClearAllPools()

    let createSampleCsv (filePath: string) : unit =
        let dir = Path.GetDirectoryName(filePath)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        use fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite ||| FileShare.Delete, 65536)
        use writer = new StreamWriter(fs, Encoding.UTF8, 65536)
        writer.WriteLine("Date,OrderId,Customer,Product,Category,Quantity,UnitPrice,TotalAmount,Status,PaymentMethod,SalesChannel,Region,DiscountRate,TaxAmount,GrossMargin,SalesRep,Notes")

        let knownCsv = [|
            "2026-03-01,101,Carlos Silva,Laptop Pro 16,Electronics,1,1499.99,1499.99,Completed,Credit Card,Web,LATAM,0.00,120.00,450.00,Camila Rocha,Express delivery"
            "2026-03-05,102,Alice Smith,Expert F# 4.0,Books,1,54.99,54.99,Completed,Debit Card,Web,EMEA,0.05,4.40,22.00,Oliver Grant,Gift wrapping"
            "2026-03-05,102,Alice Smith,Domain Modeling Made Functional,Books,1,45.00,45.00,Completed,Debit Card,Web,EMEA,0.05,3.60,18.00,Oliver Grant,Bundled book"
            "2026-03-10,103,John Doe,Wireless Ergonomic Mouse,Electronics,1,49.90,49.90,Completed,PayPal,Marketplace,NA,0.00,4.00,20.00,Sarah Jenkins,Home office"
            "2026-03-10,103,John Doe,Noise-Cancelling Headphones,Electronics,1,249.00,249.00,Completed,PayPal,Marketplace,NA,0.00,20.00,95.00,Sarah Jenkins,Premium audio"
            "2026-03-15,104,Carlos Silva,Espresso Coffee Machine,Home & Kitchen,1,189.90,189.90,Processing,Credit Card,Web,LATAM,0.00,15.20,60.00,Camila Rocha,Kitchen gear"
            "2026-03-20,105,Maria Garcia,Running Shoes Pro,Sports & Fitness,1,129.99,129.99,Shipped,Wire Transfer,In-Store,EMEA,0.10,10.40,45.00,Diego Morales,Running club"
            "2026-03-25,106,Yuki Tanaka,Mechanical Keyboard RGB,Electronics,1,119.50,119.50,Completed,Credit Card,Web,APAC,0.00,9.50,48.00,Kenji Sato,Gamer setup"
            "2026-04-01,107,Ana Pereira,F# Deep Dives,Books,1,42.50,42.50,Processing,Pix,Mobile App,LATAM,0.00,3.40,16.00,Camila Rocha,Software team"
            "2026-04-05,108,John Doe,Noise-Cancelling Headphones,Electronics,1,249.00,249.00,Pending,PayPal,Marketplace,NA,0.00,20.00,95.00,Sarah Jenkins,Replacement"
        |]
        for line in knownCsv do
            writer.WriteLine(line)

        let categories = [| "Electronics"; "Books"; "Home & Kitchen"; "Sports & Fitness" |]
        let customers = [| "Carlos Silva"; "Alice Smith"; "John Doe"; "Maria Garcia"; "Yuki Tanaka"; "Ana Pereira" |]
        let products = [| "Laptop Pro 16"; "Wireless Ergonomic Mouse"; "Mechanical Keyboard RGB"; "Noise-Cancelling Headphones"; "Expert F# 4.0"; "Domain Modeling Made Functional" |]
        let statuses = [| "Completed"; "Processing"; "Shipped"; "Pending" |]
        let paymentMethods = [| "Credit Card"; "Debit Card"; "PayPal"; "Wire Transfer"; "Pix" |]
        let channels = [| "Web"; "Mobile App"; "Marketplace"; "In-Store" |]
        let regions = [| "LATAM"; "EMEA"; "NA"; "APAC" |]
        let reps = [| "Camila Rocha"; "Oliver Grant"; "Sarah Jenkins"; "Diego Morales"; "Kenji Sato" |]

        for i = 11 to 100000 do
            let cat = categories.[i % 4]
            let cust = customers.[i % 6]
            let prod = products.[i % 6]
            let st = statuses.[i % 4]
            let qty = (i % 5) + 1
            let price = float ((i % 150) + 20) + 0.99
            let total = price * float qty
            let pay = paymentMethods.[i % 5]
            let chan = channels.[i % 4]
            let reg = regions.[i % 4]
            let rep = reps.[i % 5]
            writer.WriteLine($"2026-04-10,{108 + i},{cust},{prod},{cat},{qty},{price:F2},{total:F2},{st},{pay},{chan},{reg},0.00,10.00,30.00,{rep},High volume feed row")

    let createSampleCustomersCsv (filePath: string) : unit =
        let dir = Path.GetDirectoryName(filePath)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        use fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite ||| FileShare.Delete, 65536)
        use writer = new StreamWriter(fs, Encoding.UTF8, 65536)
        writer.WriteLine("CustomerId,CustomerName,LoyaltyTier,TotalSpent,Score,ActiveSince,Feedback,Email,Phone,Country,City,State,PostalCode,Segment,TotalOrders,AverageOrderValue,PreferredChannel")

        let knownCustomersCsv = [|
            "1,Carlos Silva,Platinum,1549.89,9.8,2025-11-10,Excelente atendimento e entrega rapida,carlos.silva@example.com,+55 11 98765-4321,Brazil,São Paulo,SP,01310-100,Enterprise,12,129.15,Web"
            "2,Alice Smith,Gold,499.50,9.5,2025-12-01,Great book selection and fast dispatch,alice.smith@example.co.uk,+44 20 7946 0912,UK,London,Greater London,EC1A 1BB,SMB,8,62.43,Web"
            "3,John Doe,Silver,298.90,8.7,2026-01-05,Reliable wireless accessories and audio gear,john.doe@example.com,+1 212-555-0143,USA,New York,NY,10001,Consumer,5,59.78,Marketplace"
            "4,Maria Garcia,Gold,389.90,9.2,2026-01-12,Muy buena calidad en productos deportivos,maria.garcia@example.es,+34 91 123 4567,Spain,Madrid,Madrid,28001,SMB,7,55.70,In-Store"
            "5,Yuki Tanaka,Silver,119.50,9.0,2026-02-01,High quality mechanical keyboards,yuki.tanaka@example.jp,+81 3 5555 0198,Japan,Tokyo,Tokyo,100-0001,Consumer,3,39.83,Web"
            "6,Ana Pereira,Bronze,87.50,8.9,2026-02-18,Atendimento cordial e catalogo diversificado,ana.pereira@example.com,+55 21 99876-5432,Brazil,Rio de Janeiro,RJ,20040-002,Consumer,2,43.75,Mobile App"
        |]
        for line in knownCustomersCsv do
            writer.WriteLine(line)

        let tiers = [| "Platinum"; "Gold"; "Silver"; "Bronze" |]
        let countries = [| "Brazil"; "UK"; "USA"; "Spain"; "Japan"; "Germany"; "Canada"; "Australia" |]
        let cities = [| "São Paulo"; "London"; "New York"; "Madrid"; "Tokyo"; "Berlin"; "Toronto"; "Sydney" |]
        let segments = [| "Enterprise"; "SMB"; "Consumer"; "Education" |]
        let channels = [| "Web"; "Mobile App"; "Marketplace"; "In-Store" |]

        for i = 7 to 100000 do
            let tier = tiers.[i % 4]
            let cIdx = i % 8
            let spent = float ((i % 800) + 50) + 0.50
            let score = 8.0 + float (i % 20) * 0.1
            let totalOrders = (i % 20) + 1
            let aov = spent / float totalOrders
            let seg = segments.[i % 4]
            let chan = channels.[i % 4]
            writer.WriteLine($"{i},Customer Profile {i},{tier},{spent:F2},{score:F1},2026-01-01,Consistent positive user experience feedback,cust{i}@domain.com,+1 555-01{i % 100:D2},{countries.[cIdx]},{cities.[cIdx]},Region,10001,{seg},{totalOrders},{aov:F2},{chan}")

    let private saveWorkbookShared (workbook: XLWorkbook) (filePath: string) =
        use ms = new MemoryStream()
        workbook.SaveAs(ms)
        let bytes = ms.ToArray()
        use fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite ||| FileShare.Delete)
        fs.Write(bytes, 0, bytes.Length)

    let inline private setCell (sheet: IXLWorksheet) (r: int) (c: int) (v: obj) =
        sheet.Cell(r, c).Value <- XLCellValue.FromObject(v)

    let createSampleExcelWorkbook (filePath: string) : unit =
        let dir = Path.GetDirectoryName(filePath)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        use workbook = new XLWorkbook()

        // Sheet 1: RegionalTargets (100,000 rows with expanded columns)
        let targetsSheet = workbook.Worksheets.Add("RegionalTargets")
        let targetHeaders = [| "Region"; "Country"; "AnnualTarget"; "AchievedRevenue"; "GrowthRate"; "LeadManager"; "FiscalYear"; "TargetMargin"; "ActualMargin"; "PipelineCoverage"; "QuotaAttainmentPct"; "Status" |]
        let headersData: obj array = targetHeaders |> Array.map (fun h -> box h)
        targetsSheet.Cell(1, 1).InsertData([| headersData |]) |> ignore

        let knownTargets: obj array array = [|
            [| box "LATAM"; box "Brazil"; box 850000.00M; box 912400.50M; box 14.5M; box "Camila Rocha"; box "2026"; box 25.0M; box 26.5M; box 3.2M; box 107.3M; box "Exceeded" |]
            [| box "EMEA"; box "UK"; box 620000.00M; box 645800.25M; box 9.2M; box "Oliver Grant"; box "2026"; box 25.0M; box 26.5M; box 3.2M; box 107.3M; box "Exceeded" |]
            [| box "NA"; box "USA"; box 1200000.00M; box 1185000.75M; box 11.8M; box "Sarah Jenkins"; box "2026"; box 25.0M; box 26.5M; box 3.2M; box 107.3M; box "Exceeded" |]
            [| box "EMEA"; box "Spain"; box 410000.00M; box 438900.10M; box 12.4M; box "Diego Morales"; box "2026"; box 25.0M; box 26.5M; box 3.2M; box 107.3M; box "Exceeded" |]
            [| box "APAC"; box "Japan"; box 530000.00M; box 559200.40M; box 10.6M; box "Kenji Sato"; box "2026"; box 25.0M; box 26.5M; box 3.2M; box 107.3M; box "Exceeded" |]
        |]
        targetsSheet.Cell(2, 1).InsertData(knownTargets) |> ignore

        let regions = [| "LATAM"; "EMEA"; "NA"; "APAC" |]
        let countries = [| "Brazil"; "UK"; "USA"; "Spain"; "Japan"; "Germany"; "Canada"; "Australia" |]
        let managers = [| "Camila Rocha"; "Oliver Grant"; "Sarah Jenkins"; "Diego Morales"; "Kenji Sato" |]

        let generatedTargets = Array.init (100001 - 7 + 1) (fun idx ->
            let r = idx + 7
            let reg = regions.[r % 4]
            let ctry = countries.[r % 8]
            let mgr = managers.[r % 5]
            let target = decimal ((r % 500) * 1000 + 100000)
            let growth = decimal ((r % 25) + 5)
            let achieved = target * (1.0M + growth / 100.0M)
            [| box reg; box ctry; box target; box achieved; box growth; box mgr; box "2026"; box 22.0M; box 24.5M; box 2.8M; box 105.0M; box "On Track" |])
        targetsSheet.Cell(7, 1).InsertData(generatedTargets) |> ignore

        // Sheet 2: QuarterlyBudget (1,000 rows with expanded columns)
        let budgetSheet = workbook.Worksheets.Add("QuarterlyBudget")
        let budgetHeaders = [| "Quarter"; "Department"; "AllocatedBudget"; "ActualSpend"; "Status"; "BudgetCode"; "CostCenter"; "ApprovedBy"; "VarianceAmount"; "CommittedSpend"; "ForecastSpend" |]
        let bHeadersData: obj array = budgetHeaders |> Array.map (fun h -> box h)
        budgetSheet.Cell(1, 1).InsertData([| bHeadersData |]) |> ignore

        let knownBudgets: obj array array = [|
            [| box "2026-Q1"; box "Engineering"; box 320000.00M; box 312500.00M; box "On Track"; box "BDG-0001"; box "CC-0100"; box "CFO Executive"; box 7500.00M; box 281250.00M; box 326400.00M |]
            [| box "2026-Q1"; box "Cloud Infrastructure"; box 145000.00M; box 141200.50M; box "Optimized"; box "BDG-0002"; box "CC-0200"; box "CFO Executive"; box 3799.50M; box 127080.45M; box 147900.00M |]
            [| box "2026-Q2"; box "Data Analytics"; box 210000.00M; box 198400.25M; box "Under Budget"; box "BDG-0003"; box "CC-0300"; box "CFO Executive"; box 11599.75M; box 178560.22M; box 214200.00M |]
            [| box "2026-Q2"; box "Developer Relations"; box 95000.00M; box 94100.75M; box "On Track"; box "BDG-0004"; box "CC-0400"; box "CFO Executive"; box 899.25M; box 84690.67M; box 96900.00M |]
        |]
        budgetSheet.Cell(2, 1).InsertData(knownBudgets) |> ignore

        let quarters = [| "2026-Q1"; "2026-Q2"; "2026-Q3"; "2026-Q4" |]
        let depts = [| "Engineering"; "Cloud Infrastructure"; "Data Analytics"; "Developer Relations"; "Operations"; "Security" |]
        let generatedBudgets = Array.init (1001 - 6 + 1) (fun idx ->
            let r = idx + 6
            let q = quarters.[r % 4]
            let d = depts.[r % 6]
            let alloc = decimal ((r % 100) * 5000 + 50000)
            let act = alloc * 0.96M
            [| box q; box d; box alloc; box act; box "On Track"; box $"BDG-{r:D4}"; box $"CC-{r:D4}"; box "Finance Controller"; box (alloc - act); box (act * 0.95M); box alloc |])
        budgetSheet.Cell(6, 1).InsertData(generatedBudgets) |> ignore

        saveWorkbookShared workbook filePath

    let createSampleCommerceExcelWorkbook (filePath: string) : unit =
        let dir = Path.GetDirectoryName(filePath)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        use workbook = new XLWorkbook()

        // Sheet 1: Customers (5,000 rows with expanded columns)
        let customersSheet = workbook.Worksheets.Add("Customers")
        let customerHeaders = [| "CustomerId"; "FullName"; "Email"; "Country"; "City"; "CustomerSegment"; "Phone"; "PostalCode"; "CreditLimit"; "RegisteredDate"; "AccountStatus"; "LoyaltyPoints" |]
        let cHeadersData: obj array = customerHeaders |> Array.map (fun h -> box h)
        customersSheet.Cell(1, 1).InsertData([| cHeadersData |]) |> ignore

        let knownCustomers: obj array array = [|
            [| box 1; box "Emma Watson"; box "emma.watson@ukmail.co.uk"; box "UK"; box "London"; box "Enterprise"; box "+1 555-0199"; box "10001"; box 50000.0M; box "2025-01-15"; box "Active"; box 2500 |]
            [| box 2; box "Liam Carter"; box "liam.carter@techcorp.com"; box "USA"; box "Austin"; box "Enterprise"; box "+1 555-0199"; box "10001"; box 50000.0M; box "2025-01-15"; box "Active"; box 2500 |]
            [| box 3; box "Lucas Andrade"; box "lucas.andrade@brshop.com.br"; box "Brazil"; box "Sao Paulo"; box "SMB"; box "+1 555-0199"; box "10001"; box 50000.0M; box "2025-01-15"; box "Active"; box 2500 |]
            [| box 4; box "Sofia Martinez"; box "sofia.m@madridretail.es"; box "Spain"; box "Madrid"; box "SMB"; box "+1 555-0199"; box "10001"; box 50000.0M; box "2025-01-15"; box "Active"; box 2500 |]
            [| box 5; box "Kenji Takahashi"; box "kenji.t@tokyocloud.jp"; box "Japan"; box "Tokyo"; box "Enterprise"; box "+1 555-0199"; box "10001"; box 50000.0M; box "2025-01-15"; box "Active"; box 2500 |]
        |]
        customersSheet.Cell(2, 1).InsertData(knownCustomers) |> ignore

        let countries = [| "UK"; "USA"; "Brazil"; "Spain"; "Japan"; "Germany"; "France"; "Canada" |]
        let cities = [| "London"; "Austin"; "Sao Paulo"; "Madrid"; "Tokyo"; "Berlin"; "Paris"; "Toronto" |]
        let segments = [| "Enterprise"; "SMB"; "Consumer"; "Startup" |]
        let generatedCustomers = Array.init (5001 - 7 + 1) (fun idx ->
            let r = idx + 7
            let cIdx = r % 8
            [| box (r - 1); box $"Enterprise Customer {r - 1}"; box $"client{r - 1}@corporation.com"; box countries.[cIdx]; box cities.[cIdx]; box segments.[r % 4]; box $"+1 555-{r:D4}"; box $"{20000 + (r % 70000)}"; box 25000.0M; box "2025-06-01"; box "Active"; box ((r * 20) % 5000) |])
        customersSheet.Cell(7, 1).InsertData(generatedCustomers) |> ignore

        // Sheet 2: ProductsStock (5,000 rows with expanded columns)
        let productsSheet = workbook.Worksheets.Add("ProductsStock")
        let productHeaders = [| "ProductId"; "Sku"; "ProductName"; "Category"; "UnitPrice"; "StockQuantity"; "WarehouseZone"; "Brand"; "CostPrice"; "MarginPercent"; "ReorderThreshold"; "MaxStockCapacity"; "SupplierId" |]
        let pHeadersData: obj array = productHeaders |> Array.map (fun h -> box h)
        productsSheet.Cell(1, 1).InsertData([| pHeadersData |]) |> ignore

        let knownProducts: obj array array = [|
            [| box 101; box "HW-LAP-16"; box "Ultrabook Pro 16"; box "Hardware"; box 1599.00M; box 45; box "Zone-A"; box "OmniTech"; box 959.40M; box 40.0M; box 20; box 500; box 1 |]
            [| box 102; box "HW-MON-27"; box "4K IPS Monitor 27"; box "Hardware"; box 449.50M; box 80; box "Zone-A"; box "OmniTech"; box 269.70M; box 40.0M; box 20; box 500; box 1 |]
            [| box 103; box "ACC-KBD-01"; box "Mechanical Tactile Keyboard"; box "Accessories"; box 129.00M; box 150; box "Zone-B"; box "OmniTech"; box 77.40M; box 40.0M; box 20; box 500; box 1 |]
            [| box 104; box "ACC-MSE-02"; box "Ergonomic Wireless Mouse"; box "Accessories"; box 69.90M; box 210; box "Zone-B"; box "OmniTech"; box 41.94M; box 40.0M; box 20; box 500; box 1 |]
            [| box 105; box "NET-RTR-05"; box "Wi-Fi 7 Mesh Router"; box "Networking"; box 299.00M; box 60; box "Zone-C"; box "OmniTech"; box 179.40M; box 40.0M; box 20; box 500; box 1 |]
        |]
        productsSheet.Cell(2, 1).InsertData(knownProducts) |> ignore

        let categories = [| "Hardware"; "Accessories"; "Networking"; "Storage"; "Peripherals" |]
        let zones = [| "Zone-A"; "Zone-B"; "Zone-C"; "Zone-D" |]
        let generatedProducts = Array.init (5001 - 7 + 1) (fun idx ->
            let r = idx + 7
            let pid = 100 + r - 1
            let price = decimal ((r % 300) + 20) + 0.99M
            [| box pid; box $"SKU-OPS-{pid:D5}"; box $"Enterprise Hardware {pid}"; box categories.[r % 5]; box price; box ((r % 200) + 15); box zones.[r % 4]; box "OmniTech"; box (price * 0.6M); box 40.0M; box 25; box 600; box ((r % 10) + 1) |])
        productsSheet.Cell(7, 1).InsertData(generatedProducts) |> ignore

        // Sheet 3: Sales (100,000 rows with expanded columns)
        let salesSheet = workbook.Worksheets.Add("Sales")
        let salesHeaders = [| "SaleId"; "CustomerId"; "ProductId"; "Quantity"; "TotalAmount"; "SaleDate"; "PaymentMethod"; "InvoiceNumber"; "Discount"; "NetAmount"; "TaxRate"; "SalesChannel"; "Currency" |]
        let sHeadersData: obj array = salesHeaders |> Array.map (fun h -> box h)
        salesSheet.Cell(1, 1).InsertData([| sHeadersData |]) |> ignore

        let knownSales: obj array array = [|
            [| box 1001; box 1; box 101; box 2; box 3198.00M; box "2026-03-10"; box "Corporate Card"; box "INV-2026-1001"; box 0.0M; box 3198.00M; box 0.08M; box "Enterprise Direct"; box "USD" |]
            [| box 1002; box 2; box 102; box 3; box 1348.50M; box "2026-03-12"; box "Wire Transfer"; box "INV-2026-1002"; box 0.0M; box 1348.50M; box 0.08M; box "Enterprise Direct"; box "USD" |]
            [| box 1003; box 3; box 103; box 5; box 645.00M; box "2026-03-15"; box "Credit Card"; box "INV-2026-1003"; box 0.0M; box 645.00M; box 0.08M; box "Enterprise Direct"; box "USD" |]
            [| box 1004; box 4; box 105; box 2; box 598.00M; box "2026-03-18"; box "Credit Card"; box "INV-2026-1004"; box 0.0M; box 598.00M; box 0.08M; box "Enterprise Direct"; box "USD" |]
            [| box 1005; box 5; box 101; box 1; box 1599.00M; box "2026-03-20"; box "Wire Transfer"; box "INV-2026-1005"; box 0.0M; box 1599.00M; box 0.08M; box "Enterprise Direct"; box "USD" |]
            [| box 1006; box 1; box 104; box 4; box 279.60M; box "2026-03-22"; box "Corporate Card"; box "INV-2026-1006"; box 0.0M; box 279.60M; box 0.08M; box "Enterprise Direct"; box "USD" |]
        |]
        salesSheet.Cell(2, 1).InsertData(knownSales) |> ignore

        let payMethods = [| "Corporate Card"; "Wire Transfer"; "Credit Card"; "Direct Debit" |]
        let channels = [| "Enterprise Direct"; "Partner Network"; "Web Portal"; "Reseller" |]
        let generatedSales = Array.init (100001 - 8 + 1) (fun idx ->
            let r = idx + 8
            let sid = 1000 + r - 1
            let cid = (r % 5000) + 1
            let pid = 100 + (r % 5000) + 1
            let qty = (r % 4) + 1
            let total = decimal ((r % 400) * 10 + 50) + 0.50M
            [| box sid; box cid; box pid; box qty; box total; box "2026-03-25"; box payMethods.[r % 4]; box $"INV-2026-{sid}"; box 15.0M; box (total - 15.0M); box 0.08M; box channels.[r % 4]; box "USD" |])
        salesSheet.Cell(8, 1).InsertData(generatedSales) |> ignore

        // Sheet 4: DeliveryLogs (5,000 rows with expanded columns)
        let deliverySheet = workbook.Worksheets.Add("DeliveryLogs")
        let deliveryHeaders = [| "DeliveryId"; "SaleId"; "Carrier"; "TrackingCode"; "DeliveryStatus"; "DispatchedDate"; "EstimatedArrival"; "OriginHub"; "DestinationCity"; "ShippingCost"; "WeightKg"; "DeliveryAttempts"; "ActualArrival" |]
        let dHeadersData: obj array = deliveryHeaders |> Array.map (fun h -> box h)
        deliverySheet.Cell(1, 1).InsertData([| dHeadersData |]) |> ignore

        let knownDeliveries: obj array array = [|
            [| box 5001; box 1001; box "DHL Express"; box "DHL-99201-UK"; box "Delivered"; box "2026-03-11"; box "2026-03-13"; box "Main Logistics Center"; box "London"; box 45.0M; box 12.5M; box 1; box "2026-03-13" |]
            [| box 5002; box 1002; box "FedEx Priority"; box "FDX-88412-US"; box "Delivered"; box "2026-03-13"; box "2026-03-15"; box "Main Logistics Center"; box "London"; box 45.0M; box 12.5M; box 1; box "2026-03-15" |]
            [| box 5003; box 1003; box "UPS Worldwide"; box "UPS-77109-BR"; box "In Transit"; box "2026-03-16"; box "2026-03-21"; box "Main Logistics Center"; box "London"; box 45.0M; box 12.5M; box 1; box "2026-03-21" |]
            [| box 5004; box 1004; box "DHL Express"; box "DHL-66320-ES"; box "Delivered"; box "2026-03-19"; box "2026-03-21"; box "Main Logistics Center"; box "London"; box 45.0M; box 12.5M; box 1; box "2026-03-21" |]
            [| box 5005; box 1005; box "Yamato Global"; box "YMT-55401-JP"; box "In Transit"; box "2026-03-21"; box "2026-03-24"; box "Main Logistics Center"; box "London"; box 45.0M; box 12.5M; box 1; box "2026-03-24" |]
            [| box 5006; box 1006; box "DHL Express"; box "DHL-99540-UK"; box "Out for Delivery"; box "2026-03-23"; box "2026-03-25"; box "Main Logistics Center"; box "London"; box 45.0M; box 12.5M; box 1; box "2026-03-25" |]
        |]
        deliverySheet.Cell(2, 1).InsertData(knownDeliveries) |> ignore

        let carriers = [| "DHL Express"; "FedEx Priority"; "UPS Worldwide"; "Yamato Global" |]
        let deliveryStatuses = [| "Delivered"; "In Transit"; "Out for Delivery"; "Customs Cleared" |]
        let generatedDeliveries = Array.init (5001 - 8 + 1) (fun idx ->
            let r = idx + 8
            let did = 5000 + r - 1
            let sid = 1000 + r - 1
            let carrier = carriers.[r % 4]
            let status = deliveryStatuses.[r % 4]
            [| box did; box sid; box carrier; box $"TRK-OPS-{did:D6}"; box status; box "2026-03-25"; box "2026-03-28"; box "Global Air Cargo Hub"; box "Destination City"; box 35.0M; box 8.5M; box 1; box "2026-03-28" |])
        deliverySheet.Cell(8, 1).InsertData(generatedDeliveries) |> ignore

        saveWorkbookShared workbook filePath

    let createSampleJsonFile (filePath: string) : unit =
        let dir = Path.GetDirectoryName(filePath)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        use fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite ||| FileShare.Delete, 65536)
        use writer = new Utf8JsonWriter(fs, JsonWriterOptions(Indented = false))
        writer.WriteStartObject()

        // 1. Products (100,000 items with expanded attributes)
        writer.WriteStartArray("Products")
        let knownProducts = [|
            (101, "PRD-AI-01", "Neural Compute Stick X2", "AI Hardware", 249.90, true, 4.9)
            (102, "PRD-NET-02", "10GbE Smart Switch", "Networking", 389.50, true, 4.8)
            (103, "PRD-DEV-03", "Ergonomic Split Keyboard", "Peripherals", 179.00, true, 4.7)
            (104, "PRD-CLD-04", "NVMe Gen5 Raid Enclosure", "Storage", 319.99, false, 4.6)
        |]
        for pid, sku, title, cat, price, inStock, rating in knownProducts do
            writer.WriteStartObject()
            writer.WriteNumber("ProductId", pid)
            writer.WriteString("Sku", sku)
            writer.WriteString("Title", title)
            writer.WriteString("Category", cat)
            writer.WriteNumber("Price", price)
            writer.WriteBoolean("InStock", inStock)
            writer.WriteNumber("Rating", rating)
            writer.WriteString("Brand", "TechVision")
            writer.WriteNumber("StockQuantity", 150)
            writer.WriteNumber("Cost", price * 0.6)
            writer.WriteNumber("WeightGrams", 450)
            writer.WriteNumber("WarrantyMonths", 24)
            writer.WriteNumber("ReleaseYear", 2026)
            writer.WriteString("Tags", "hardware,ai,pro")
            writer.WriteEndObject()

        let categories = [| "AI Hardware"; "Networking"; "Peripherals"; "Storage"; "Compute Modules" |]
        for i = 5 to 100000 do
            writer.WriteStartObject()
            writer.WriteNumber("ProductId", 100 + i)
            writer.WriteString("Sku", $"PRD-GEN-{i:D6}")
            writer.WriteString("Title", $"Enterprise Component {i}")
            writer.WriteString("Category", categories.[i % 5])
            let price = float ((i % 500) + 50) + 0.99
            writer.WriteNumber("Price", price)
            writer.WriteBoolean("InStock", (i % 6 <> 0))
            writer.WriteNumber("Rating", 4.0 + float (i % 10) * 0.1)
            writer.WriteString("Brand", if i % 2 = 0 then "TechVision" else "HyperDrive")
            writer.WriteNumber("StockQuantity", (i % 200) + 10)
            writer.WriteNumber("Cost", price * 0.58)
            writer.WriteNumber("WeightGrams", (i % 800) + 150)
            writer.WriteNumber("WarrantyMonths", 24)
            writer.WriteNumber("ReleaseYear", 2026)
            writer.WriteString("Tags", "enterprise,cloud,modular")
            writer.WriteEndObject()
        writer.WriteEndArray()

        // 2. Reviews (100,000 items with expanded attributes)
        writer.WriteStartArray("Reviews")
        let knownReviews = [|
            (1, 101, "Elena Rostova", 5, true, "Blazing fast local inference throughput")
            (2, 102, "Marcus Vance", 5, true, "Rock-solid VLAN tagging and low latency")
            (3, 103, "Camila Rocha", 4, true, "Great tactile switches for long coding sessions")
            (4, 101, "Kenji Sato", 5, true, "Seamless ONNX runtime integration")
        |]
        for rid, pid, rev, stars, ver, comment in knownReviews do
            writer.WriteStartObject()
            writer.WriteNumber("ReviewId", rid)
            writer.WriteNumber("ProductId", pid)
            writer.WriteString("Reviewer", rev)
            writer.WriteNumber("Stars", stars)
            writer.WriteBoolean("VerifiedPurchase", ver)
            writer.WriteString("Comment", comment)
            writer.WriteString("ReviewDate", "2026-03-01T10:00:00Z")
            writer.WriteNumber("HelpfulVotes", 12)
            writer.WriteString("Title", "Outstanding Quality")
            writer.WriteString("CustomerCountry", "USA")
            writer.WriteString("Sentiment", "Positive")
            writer.WriteEndObject()

        for i = 5 to 100000 do
            let stars = (i % 5) + 1
            writer.WriteStartObject()
            writer.WriteNumber("ReviewId", i)
            writer.WriteNumber("ProductId", 100 + (i % 1000) + 1)
            writer.WriteString("Reviewer", $"Reviewer {i}")
            writer.WriteNumber("Stars", stars)
            writer.WriteBoolean("VerifiedPurchase", true)
            writer.WriteString("Comment", $"Performance verified for high-volume workload {i}")
            writer.WriteString("ReviewDate", "2026-03-15T12:00:00Z")
            writer.WriteNumber("HelpfulVotes", i % 50)
            writer.WriteString("Title", "Verified Performance")
            writer.WriteString("CustomerCountry", "Global")
            writer.WriteString("Sentiment", if stars >= 4 then "Positive" else "Neutral")
            writer.WriteEndObject()
        writer.WriteEndArray()

        writer.WriteEndObject()
        writer.Flush()

    let createSampleXmlFile (filePath: string) : unit =
        let dir = Path.GetDirectoryName(filePath)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        use fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite ||| FileShare.Delete, 65536)
        use writer = XmlWriter.Create(fs, XmlWriterSettings(OmitXmlDeclaration = false, Encoding = Encoding.UTF8))
        writer.WriteStartDocument()
        writer.WriteStartElement("LogisticsHub")

        // 1. Carriers (1,000 carriers with expanded attributes)
        writer.WriteStartElement("Carriers")
        let knownCarriers = [|
            (1, "DHL-INT", "DHL Global Express", "Germany", 98.4)
            (2, "FDX-PRI", "FedEx Priority Air", "USA", 97.8)
            (3, "LAT-CAR", "Latam Cargo Logistics", "Brazil", 96.5)
        |]
        for cid, code, name, country, rate in knownCarriers do
            writer.WriteStartElement("Carrier")
            writer.WriteAttributeString("CarrierId", string cid)
            writer.WriteAttributeString("Code", code)
            writer.WriteAttributeString("Name", name)
            writer.WriteAttributeString("HubCountry", country)
            writer.WriteAttributeString("OnTimeRate", string rate)
            writer.WriteAttributeString("ContactEmail", $"ops@{code.ToLowerInvariant()}.com")
            writer.WriteAttributeString("FleetSize", "450")
            writer.WriteAttributeString("ServiceTier", "Tier-1")
            writer.WriteAttributeString("ActiveRoutes", "1200")
            writer.WriteEndElement()

        let countries = [| "Germany"; "USA"; "Brazil"; "Japan"; "UK"; "France"; "Singapore"; "Canada" |]
        for i = 4 to 1000 do
            let cIdx = i % 8
            writer.WriteStartElement("Carrier")
            writer.WriteAttributeString("CarrierId", string i)
            writer.WriteAttributeString("Code", $"CAR-{i:D4}")
            writer.WriteAttributeString("Name", $"Global Freight Logistics {i}")
            writer.WriteAttributeString("HubCountry", countries.[cIdx])
            writer.WriteAttributeString("OnTimeRate", string (95.0 + float (i % 50) * 0.1))
            writer.WriteAttributeString("ContactEmail", $"ops{i}@cargohub.com")
            writer.WriteAttributeString("FleetSize", string ((i % 200) + 50))
            writer.WriteAttributeString("ServiceTier", "Tier-2")
            writer.WriteAttributeString("ActiveRoutes", string ((i % 500) + 100))
            writer.WriteEndElement()
        writer.WriteEndElement() // Carriers

        // 2. Shipments (100,000 shipments with expanded attributes & sub-elements)
        writer.WriteStartElement("Shipments")
        let knownShipments = [|
            (9001, 1, "DHL-2026-8841", "Sao Paulo", 14.5, 128.90, "Delivered")
            (9002, 2, "FDX-2026-3319", "London", 8.2, 94.50, "In Transit")
            (9003, 3, "LAT-2026-5502", "Madrid", 22.0, 185.00, "Delivered")
            (9004, 1, "DHL-2026-9104", "Tokyo", 5.4, 112.40, "Customs Cleared")
        |]
        for sid, cid, track, dest, weight, cost, status in knownShipments do
            writer.WriteStartElement("Shipment")
            writer.WriteAttributeString("ShipmentId", string sid)
            writer.WriteAttributeString("CarrierId", string cid)
            writer.WriteElementString("TrackingNumber", track)
            writer.WriteElementString("Destination", dest)
            writer.WriteElementString("WeightKg", string weight)
            writer.WriteElementString("ShippingCost", string cost)
            writer.WriteElementString("Status", status)
            writer.WriteElementString("OriginCity", "Berlin")
            writer.WriteElementString("Priority", "Express")
            writer.WriteElementString("CustomsFee", "25.00")
            writer.WriteElementString("InsuranceValue", "500.00")
            writer.WriteElementString("EstimatedDays", "3")
            writer.WriteEndElement()

        let cities = [| "Sao Paulo"; "London"; "Madrid"; "Tokyo"; "Berlin"; "New York"; "Paris"; "Singapore" |]
        let shipmentStatuses = [| "Delivered"; "In Transit"; "Customs Cleared"; "Out for Delivery" |]
        for i = 5 to 100000 do
            let cid = (i % 3) + 1
            let sid = 9000 + i
            let dest = cities.[i % 8]
            let weight = float ((i % 45) + 1) + 0.5
            let cost = float ((i % 300) + 35) + 0.90
            let status = shipmentStatuses.[i % 4]
            writer.WriteStartElement("Shipment")
            writer.WriteAttributeString("ShipmentId", string sid)
            writer.WriteAttributeString("CarrierId", string cid)
            writer.WriteElementString("TrackingNumber", $"TRK-2026-{i:D7}")
            writer.WriteElementString("Destination", dest)
            writer.WriteElementString("WeightKg", string weight)
            writer.WriteElementString("ShippingCost", string cost)
            writer.WriteElementString("Status", status)
            writer.WriteElementString("OriginCity", "Global Cargo Terminal")
            writer.WriteElementString("Priority", "Standard")
            writer.WriteElementString("CustomsFee", "18.50")
            writer.WriteElementString("InsuranceValue", "350.00")
            writer.WriteElementString("EstimatedDays", "4")
            writer.WriteEndElement()
        writer.WriteEndElement() // Shipments

        writer.WriteEndElement() // LogisticsHub
        writer.WriteEndDocument()
        writer.Flush()

    let createSampleDemoNotebook (filePath: string) : unit =
        let dir = Path.GetDirectoryName(filePath)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        File.WriteAllText(filePath, getSampleDemoEnglishNotebook ())

    let createSampleDemoNotebookPt (filePath: string) : unit =
        let dir = Path.GetDirectoryName(filePath)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        File.WriteAllText(filePath, getSampleDemoPortugueseNotebook ())

    let generateAllSamples (targetDir: string) : SampleFilesResult =
        let sampleDir = Path.Combine(targetDir, "data_sources")
        if not (Directory.Exists sampleDir) then
            Directory.CreateDirectory sampleDir |> ignore

        let readmePath = Path.Combine(sampleDir, "README.txt")
        if not (File.Exists readmePath) then
            File.WriteAllText(
                readmePath,
                "Place your .csv, .xlsx, .xls, .json, .xml, or .sqlite/.db files in this directory.\nF# Query Studio will automatically detect and register them as queryable data sources.\n"
            )

        let dbPath = Path.Combine(sampleDir, "sample_store.db")
        let db2Path = Path.Combine(sampleDir, "inventory_store.db")
        let csvPath = Path.Combine(sampleDir, "sales_feed.csv")
        let csv2Path = Path.Combine(sampleDir, "customers_feed.csv")
        let excelPath = Path.Combine(sampleDir, "regional_targets.xlsx")
        let excel2Path = Path.Combine(sampleDir, "commerce_ops.xlsx")
        let jsonPath = Path.Combine(sampleDir, "product_catalog.json")
        let xmlPath = Path.Combine(sampleDir, "logistics_hub.xml")
        let notebookPath = Path.Combine(targetDir, "sample_demo.fsqnb")
        let notebookPtPath = Path.Combine(targetDir, "sample_demo_pt.fsqnb")

        let isFileValid (path: string) (minBytes: int64) =
            try File.Exists path && (FileInfo path).Length >= minBytes with _ -> false

        if not (isFileValid dbPath 1000000L) then createSampleStoreDatabase dbPath
        if not (isFileValid db2Path 1000000L) then createInventoryDatabase db2Path
        if not (isFileValid csvPath 1000000L) then createSampleCsv csvPath
        if not (isFileValid csv2Path 1000000L) then createSampleCustomersCsv csv2Path
        if not (isFileValid excelPath 100000L) then createSampleExcelWorkbook excelPath
        if not (isFileValid excel2Path 100000L) then createSampleCommerceExcelWorkbook excel2Path
        if not (isFileValid jsonPath 1000000L) then createSampleJsonFile jsonPath
        if not (isFileValid xmlPath 1000000L) then createSampleXmlFile xmlPath
        if not (File.Exists notebookPath) then createSampleDemoNotebook notebookPath
        if not (File.Exists notebookPtPath) then createSampleDemoNotebookPt notebookPtPath

        { SqlitePath = dbPath
          Sqlite2Path = db2Path
          CsvPath = csvPath
          Csv2Path = csv2Path
          ExcelPath = excelPath
          Excel2Path = excel2Path
          JsonPath = jsonPath
          XmlPath = xmlPath
          RestUrl = DefaultGitHubRestUrl
          NotebookPath = notebookPath
          NotebookPtPath = notebookPtPath }

