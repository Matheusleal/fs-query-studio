namespace FSharpQueryStudio.Connectors

open AdvancedSampleNotebookCommon

module SampleDemoNotebookEn =

    let getSampleDemoEnglishNotebook () : string =
        let cells =
            [
                mkMarkdown
                    """
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

                mkMarkdown
                    """
## 1. ⚡ Strongly-Typed LINQ: Filtering Customers (`SampleStore`)

Access strongly-typed entities directly using `Connections.sql.SampleStore.Customers`. Notice how `c.Country`, `c.FirstName`, and `c.Email` have complete IntelliSense autocomplete, hover documentation, and type safety!

> 💡 **Formatting Tip**: Projecting into anonymous records (`{| ... |}`) generates clean, labeled column headers in the interactive table grid.
"""

                mkCode
                    """
// 1. Query SQLite (SampleStore) customers from Brazil directly via Connections.sql
fsql {
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

                mkMarkdown
                    """
## 2. 🔗 Relational Joins: Orders with Customer Profiles (`SampleStore`)

Join tables cleanly and naturally using standard F# LINQ `join ... on (...)` syntax within the same connection.
"""

                mkCode
                    """
// 2. Join Orders and Customers within SampleStore to view transaction details with customer info
fsql {
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

                mkMarkdown
                    """
## 3. 🏬 Second SQLite Source: Querying Warehouses & Stocks (`InventoryStore`)

Notice that multiple SQLite data sources coexist seamlessly without naming or module collisions!
Here we query the second database directly via `Connections.sql.InventoryStore`.
"""

                mkCode
                    """
// 3. Query the second SQLite database (InventoryStore) directly via Connections.sql
fsql {
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

                mkMarkdown
                    """
## 4. 📊 Analytical Aggregations & Grouping (GroupBy)

Compute business metrics, order counts, and revenue statistics grouped by order status across 100,000+ orders.
"""

                mkCode
                    """
// 4. Financial breakdown: Total revenue and average ticket by order status across 100k orders
Connections.sql.SampleStore.Orders
|> Seq.groupBy (fun o -> o.Status)
|> Seq.map (fun (status, items) ->
    let list = Seq.toList items
    {|
        Status = status
        OrderCount = list.Length
        TotalRevenue = list |> List.sumBy (fun i -> i.TotalAmount)
        AverageTicket = list |> List.averageBy (fun i -> i.TotalAmount)
    |})
"""

                mkMarkdown
                    """
## 5. 📁 First Flat-File CSV: Sales Transactions (`SalesFeed`)

CSV files are automatically discovered, inferred, and mapped to strongly-typed record rows. Decimal values, integers, and text fields are parsed culture-invariantly.
Access the feed directly via `Connections.csv.SalesFeed`.
"""

                mkCode
                    """
// 5. Query the first CSV feed (SalesFeed) directly and filter electronics purchases
fsql {
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

                mkMarkdown
                    """
## 6. 📋 Second Flat-File CSV: Customer Loyalty Feed (`CustomersFeed`)

A second CSV source is registered alongside `SalesFeed`. Both CSV sources generate independent, unique strongly-typed models without collision or context leakage.
"""

                mkCode
                    """
// 6. Query the second CSV feed (CustomersFeed) directly
fsql {
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

                mkMarkdown
                    """
## 7. 🔄 Cross-Source LINQ: Correlating Multiple CSV Feeds in Memory

Seamlessly join and correlate data from both CSV files (`SalesFeed` + `CustomersFeed`) in a single F# query!
"""

                mkCode
                    """
// 7. In-memory correlation between SalesFeed transactions and CustomersFeed loyalty
let salesRows = Connections.csv.SalesFeed |> Seq.truncate 2000 |> Seq.toList
let loyaltyRows = Connections.csv.CustomersFeed |> Seq.truncate 2000 |> Seq.toList

fsql {
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

                mkMarkdown
                    """
### ⚡ Pushdown & Multi-Property Joins with SQL Preview (`toRawSql`)

F# Query Studio compiles multi-property joins `on ((a.param1, a.param2) = (b.param1, b.param2))` directly to index-backed SQL `_t0.col1 = _t1.col1 AND _t0.col2 = _t1.col2`, avoiding Cartesian products and excessive memory usage.
Use `ResultKind.toRawSql` to preview the pushdown SQL before or alongside execution!
"""

                mkCode
                    """
// 7.1 Preview generated pushdown SQL for relational joins before execution
let complexJoinQuery =
    fsql {
        for o in Connections.sql.SampleStore.Orders do
        join i in Connections.sql.SampleStore.OrderItems on (o.Id = i.OrderId)
        where (o.TotalAmount >= 100.0f)
        select {| OrderId = o.Id; ItemId = i.Id; Amount = o.TotalAmount |}
    }

printfn "Pushdown SQL Query:\n%s" (ResultKind.toRawSql complexJoinQuery)
complexJoinQuery
"""

                mkMarkdown
                    """
## 8. 🛡️ Safe Parameterized SQL (`SampleStore`)

For complex database-specific queries, use `Connections.sql.<Conn>.rawQuery` with parameterized inputs to guard against SQL injection while maintaining raw database performance.
"""

                mkCode
                    """
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

                mkMarkdown
                    """
## 9. 📊 Excel Workbook (`.xlsx`): Regional Revenue Targets (`RegionalTargets`)

Excel workbooks (`.xlsx`) are inspected per worksheet and exposed with strongly-typed sheet accessors via `Connections.excel.RegionalTargets`. You can query individual sheets (`RegionalTargets` or `QuarterlyBudget`) directly with LINQ!
"""

                mkCode
                    """
// 9. Query the RegionalTargets worksheet from the Excel (.xlsx) workbook
fsql {
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

                mkMarkdown
                    """
## 10. 📦 4-Sheet Excel Workbook (`.xlsx`): Customers, Products Stock, Sales & Delivery Logs (`CommerceOps`)

Each Excel connection acts as a schema container (`Connections.excel.CommerceOps.<Sheet>`) mapped to prefixed SQLite tables (`CommerceOps_Customers`, `CommerceOps_ProductsStock`, `CommerceOps_Sales`, `CommerceOps_DeliveryLogs`). You can join all four worksheets in a single SQL-pushed LINQ query!
"""

                mkCode
                    """
// 10. Join all 4 sheets (Sales, Customers, ProductsStock, DeliveryLogs) in CommerceOps (.xlsx)
fsql {
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

                mkMarkdown
                    """
## 11. 🗂️ Local JSON Document (`.json`): Multi-Collection Catalog & Reviews (`ProductCatalog`)

Local JSON files (`.json`) work just like Excel workbooks: each discovered array collection is exposed as a strongly-typed sub-property (`Connections.json.ProductCatalog.Products` and `Connections.json.ProductCatalog.Reviews`) staged in SQLite (`ProductCatalog_Products`, `ProductCatalog_Reviews`) with full LINQ-to-SQL pushdown!
"""

                mkCode
                    """
// 11. Join Products and Reviews inside the local JSON file (ProductCatalog.json) via Connections.json
fsql {
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

                mkMarkdown
                    """
## 12. 📄 Local XML Document (`.xml`): Carrier Network & Shipments (`LogisticsHub`)

Local XML files (`.xml`) automatically extract both XML attributes (`@CarrierId`, `@Code`) and child elements (`<TrackingNumber>`, `<Destination>`, `<ShippingCost>`) into strongly-typed sub-properties (`Connections.xml.LogisticsHub.Carriers` and `Connections.xml.LogisticsHub.Shipments`).
"""

                mkCode
                    """
// 12. Join Shipments and Carriers inside the local XML file (logistics_hub.xml) via Connections.xml
fsql {
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

                mkMarkdown
                    """
## 13. 🔒 Secure GitHub REST API (`https://`): F# Community Repositories (`GitHubFSharp`)

REST/JSON endpoints over HTTPS are automatically sampled and mapped into strongly-typed F# records. Here we query public F# repositories from `https://api.github.com/orgs/fsprojects/repos` via `Connections.rest.GitHubFSharp`.
"""

                mkCode
                    """
// 13. Query public F# repositories from GitHub HTTPS REST API via Connections.rest
fsql {
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

                mkMarkdown
                    """
## 14. 🌐 High-Scale Overview: 9-Source Pipeline (100k+ Records Each)

Because F# Query Studio executes queries in an integrated F# Interactive session, you can aggregate metadata and counts across all 9 sources (`SQLite`, `CSV`, `Excel`, `JSON`, `XML`, and `REST/JSON`) simultaneously.
"""

                mkCode
                    """
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

                mkMarkdown
                    """
## 15. 📁 File Operations & Custom Pipelines with `System.IO` (`data_sources`)

F# Query Studio keeps the process working directory (`System.Environment.CurrentDirectory`) strictly aligned with your workspace root. You can create, stream, and manipulate local files using standard .NET APIs (`System.IO.File`, `FileStream`, `StreamWriter`) completely free of OS file locks across notebook cells!

In this example, we stream raw records from `data_sources/sales_feed.csv`, extract top transactions, and generate a new summary file `data_sources/top_sales_exported.csv` using `System.IO.File.Create`.
"""

                mkCode
                    """
// 15. Create a filtered summary file from data_sources/sales_feed.csv using System.IO.File.Create
open System.IO
open System.Text

let salesFeedPath = Path.Combine("data_sources", "sales_feed.csv")
let exportedSummaryPath = Path.Combine("data_sources", "top_sales_exported.csv")

// Stream lines from data_sources and write with explicit stream disposal
let summaryRows =
    File.ReadLines salesFeedPath
    |> Seq.skip 1
    |> Seq.take 100
    |> Seq.map (fun line -> line.Split ',')
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
    use stream = File.Create exportedSummaryPath
    use writer = new StreamWriter(stream, Encoding.UTF8)
    writer.WriteLine "OrderId,Customer,Product,Amount,Status"
    for r in summaryRows do
        writer.WriteLine $"{r.OrderId},{r.Customer},{r.Product},{r.Amount},{r.Status}"
    writer.Flush()

printfn "Successfully generated: %s (Total records: %d, File size: %d bytes)"
    exportedSummaryPath summaryRows.Length (FileInfo exportedSummaryPath).Length

summaryRows
"""

                mkMarkdown
                    """
## 16. 🔄 Multi-Cell Lock-Free Verification Across Notebook Sessions

Next, in this subsequent cell, we inspect and update the `top_sales_exported.csv` file created in the previous cell. F# Query Studio actively releases superseded continuation cursors and finalizes file handles between cell evaluations, ensuring that reading (`File.ReadAllLines`) and appending (`File.AppendAllText`) proceed seamlessly without sharing violations (`IOException`).
"""

                mkCode
                    """
// 16. Verify lock-free access across cells: read, append metadata, and project to table
open System.IO

let verifiedSummaryPath = Path.Combine("data_sources", "top_sales_exported.csv")

// Read lines back from the file created in the previous cell
let lines = File.ReadAllLines verifiedSummaryPath

// Append metadata to verify write access is immediately available
File.AppendAllText(verifiedSummaryPath, "# Processed by F# Query Studio\n")

printfn "Verified lock-free access to %s: read %d lines, updated size %d bytes"
    verifiedSummaryPath lines.Length (FileInfo verifiedSummaryPath).Length

lines
|> Seq.skip 1
|> Seq.filter (fun l -> not (l.StartsWith "#"))
|> Seq.take 20
|> Seq.map (fun l ->
    let p = l.Split ','
    {|
        OrderId = p.[0]
        Customer = p.[1]
        Product = p.[2]
        Amount = p.[3]
        Status = p.[4]
    |})
|> Seq.toList
"""

                mkMarkdown
                    """
## 17. 🎯 Advanced DQL: Correlated Subqueries with `whereExists` & `notExists` (`SampleStore`)

Using **`fsql`**, you can write correlated `EXISTS` and `NOT EXISTS` subqueries directly in pure F# syntax without string queries or manual count comparisons.
Here we filter Brazilian and US customers who have placed high-value orders (>= $250).
"""

                mkCode
                    """
// 17. Query customers who have high-value orders (>= $250) using whereExists via fsql
fsql {
    for c in Connections.sql.SampleStore.Customers do
    where (c.Country = "Brazil" || c.Country = "USA")
    whereExists (fsql { for o in Connections.sql.SampleStore.Orders do where (o.CustomerId = c.Id && o.TotalAmount >= 250.0f) })
    sortBy c.FirstName
    take 10
    select {|
        Id = c.Id
        Customer = c.FirstName + " " + c.LastName
        City = c.City
        Country = c.Country
    |}
}
"""

                mkMarkdown
                    """
## 18. 🧮 Advanced DQL: Scalar Subqueries in SELECT Projections (`SampleStore`)

Compute per-row correlated metrics (such as total order counts) directly within the `select` projection of an `fsql` query. The subquery compiles to a single database-side scalar sub-select.
"""

                mkCode
                    """
// 18. Enrich customer profiles with correlated order counts directly in the SELECT projection
fsql {
    for c in Connections.sql.SampleStore.Customers do
    where (c.Country = "Brazil")
    sortBy c.FirstName
    take 10
    select {|
        Customer = c.FirstName + " " + c.LastName
        Email = c.Email
        City = c.City
        TotalOrders = fsql { for o in Connections.sql.SampleStore.Orders do where (o.CustomerId = c.Id); count }
    |}
}
"""

                mkMarkdown
                    """
## 19. 📊 Advanced DQL: GroupBy with `having` Clause (`SampleStore`)

Filter grouped aggregations directly using the dedicated `having` operation in `fsql`. The condition compiles to a native SQL `GROUP BY ... HAVING (COUNT(*) >= @p0)` clause.
"""

                mkCode
                    """
// 19. Group orders by customer and filter high-frequency buyers using having (>= 10 orders)
fsql {
    for o in Connections.sql.SampleStore.Orders do
    groupBy o.CustomerId into g
    having (fsql { for item in g do count } >= 10)
    select {|
        CustomerId = g.Key
        TotalOrders = fsql { for item in g do count }
    |}
}
"""

                mkMarkdown
                    """
## 20. 🔍 Advanced DQL: Formatted SQL Inspection via `toRawQuery`

Inspect the generated SQL query before or alongside execution. The **`toRawQuery`** function produces beautifully indented, multi-dialect SQL with clause indentation and parameter headers.
"""

                mkCode
                    """
// 20. Inspect formatted SQL pushdown with clause indentation and parameter documentation
let inspectedQuery =
    fsql {
        for c in Connections.sql.SampleStore.Customers do
        where (c.Country = "USA")
        whereExists (fsql { for o in Connections.sql.SampleStore.Orders do where (o.CustomerId = c.Id && o.TotalAmount >= 100.0f) })
        select {|
            Customer = c.FirstName + " " + c.LastName
            TotalOrders = fsql { for o in Connections.sql.SampleStore.Orders do where (o.CustomerId = c.Id); count }
        |}
    }

printfn "Pushdown SQL Inspection via toRawQuery:\n%s" (toRawQuery inspectedQuery)
inspectedQuery
"""

                mkMarkdown
                    """
## 21. ⚡ Analytical Window Functions (`fsql.window`, `rowNumber`, `rank`, `runningTotal`)

Execute analytical window pushdown natively in SQLite 3.25+ using `fsql.window`.
Here we rank products in their category by price and calculate running revenue totals.
"""

                mkCode
                    """
// 21. Window Functions: rowNumber, rank and running sum over category partition
fsql {
    for p in Connections.sql.SampleStore.Products do
    select {|
        Category = p.CategoryId
        Product = p.Name
        Price = p.Price
        RowInCat = fsql.rowNumber(fsql.window(partitionBy = p.CategoryId, sortByDescending = p.Price))
        RankInCat = fsql.rank(fsql.window(partitionBy = p.CategoryId, sortByDescending = p.Price))
        RunningSum = fsql.sum(p.Price, over = fsql.window(partitionBy = p.CategoryId, sortBy = p.Id))
    |}
}
"""

                mkMarkdown
                    """
## 22. 🧩 Common Table Expressions (CTEs) & Set Operations (`union`, `unionAll`, `intersect`, `except`)

Combine multiple query projections using SQL pushdown set operations and CTEs directly inside the **`fsql { ... }`** computation expression.
"""

                mkCode
                    """
// 22.1 Named CTE (Common Table Expression) consumed directly via JOIN
let highPriceProducts =
    fsql {
        for p in Connections.sql.SampleStore.Products do
        where (p.Price >= 200.0f)
        select p
    }

let catalogWithCategory =
    fsql {
        for c in Connections.sql.SampleStore.Categories do
        withCte "HighPriceProducts" highPriceProducts
        join p in highPriceProducts on (c.Id = p.CategoryId)
        select (c.Name, p.Name, p.Price)
    }

printfn "Formatted SQL (Named CTE + JOIN):\n%s" (toRawQuery catalogWithCategory)
catalogWithCategory
"""

                mkCode
                    """
// 22.2 Unnamed CTE with transparent injection and JOIN
let highStockProducts =
    fsql {
        for p in Connections.sql.SampleStore.Products do
        where (p.StockQuantity >= 100)
        select p
    }

let catalogHighStock =
    fsql {
        for c in Connections.sql.SampleStore.Categories do
        withCte highStockProducts
        join p in highStockProducts on (c.Id = p.CategoryId)
        select (c.Name, p.Name, p.StockQuantity)
    }

printfn "Formatted SQL (Unnamed CTE + JOIN):\n%s" (toRawQuery catalogHighStock)
catalogHighStock
"""

                mkCode
                    """
// 22.3 Set Operations: intersect and except directly inside fsql CE
let stockExcept =
    fsql {
        for p in Connections.sql.SampleStore.Products do
        where (p.StockQuantity >= 100)
        select p
        except (fsql {
            for p2 in Connections.sql.SampleStore.Products do
            where (p2.Price > 300.0f)
            select p2
        })
    }

printfn "Formatted SQL (EXCEPT):\n%s" (toRawQuery stockExcept)
stockExcept
"""

                mkMarkdown
                    """
## 23. 🔗 Relational Outer Joins & Conditional CASE WHEN Expressions

Query tables with `leftOuterJoin ... into ... for ... in ... .DefaultIfEmpty()` and format columns with inline `if ... then ... else` branching compiled to native SQL `CASE WHEN`.
"""

                mkCode
                    """
// 23. Left outer join with conditional classification (CASE WHEN)
fsql {
    for c in Connections.sql.SampleStore.Customers do
    where (c.Country = "Brazil" || c.Country = "USA")
    leftOuterJoin o in Connections.sql.SampleStore.Orders on (c.Id = o.CustomerId) into orders
    for o in orders.DefaultIfEmpty() do
    take 15
    select {|
        Customer = c.FirstName + " " + c.LastName
        Country = c.Country
        OrderId = if isNull (box o) then 0 else o.Id
        Amount = if isNull (box o) then 0.0f else o.TotalAmount
        Status = if isNull (box o) then "No Orders" else o.Status
        CustomerCategory = if c.LoyaltyPoints >= 1000 then "VIP" else "Regular"
    |}
}
"""

                mkMarkdown
                    """
## 24. 🔍 Correlated Non-Existence (`notExists`) & Engine Execution Plan (`fsql.explain`)

Find entities without related records using `notExists` and inspect the database execution plan directly.
"""

                mkCode
                    """
// 24. Customers with no pending orders and live execution plan report
let pendingAudit =
    fsql {
        for c in Connections.sql.SampleStore.Customers do
        where (c.Country = "Brazil")
        where (notExists (fsql { for o in Connections.sql.SampleStore.Orders do where (o.CustomerId = c.Id && o.Status = "Pending") }))
        take 10
        select {|
            Cliente = c.FirstName + " " + c.LastName
            Cidade = c.City
            Email = c.Email
        |}
    }

let planReport = fsql.explain pendingAudit
printfn "Engine Execution Plan Report:\n%s" planReport.PlanText
printfn "Formatted SQL:\n%s" (toRawQuery pendingAudit)
pendingAudit
"""

                mkMarkdown
                    """
## 25. 🛡️ Null & Empty Filtering (`isNotNull`, `isNotEmpty`, `isNotNullOrEmpty`)

Filter null and empty columns directly inside `where (...)` using `isNotNull`, `isNotEmpty`, and `isNotNullOrEmpty` — all pushed down directly to native SQL (`IS NOT NULL` and `<> ''`).
"""

                mkCode
                    """
// 25. Filter non-null and non-empty fields using isNotNull, isNotEmpty, and isNotNullOrEmpty
let validOrdersQuery =
    fsql {
        for o in Connections.sql.SampleStore.Orders do
        where (isNotNull o.Notes && isNotEmpty o.Status && isNotNullOrEmpty o.PaymentMethod && o.TotalAmount >= 150.0f)
        sortByDescending o.TotalAmount
        take 10
        select {|
            OrderId = o.Id
            OrderNumber = o.OrderNumber
            Status = o.Status
            PaymentMethod = o.PaymentMethod
            Channel = o.Channel
            TotalAmount = o.TotalAmount
            Notes = o.Notes
        |}
    }

printfn "Formatted SQL (Null & Empty Pushdown):\n%s" (toRawQuery validOrdersQuery)
validOrdersQuery
"""

                mkMarkdown
                    """
## 26. 🗄️ Direct SQLite Engine Queries on Mapped CSV & XML Connections (`rawQuery` & `queryEngine`)

Mapped flat-file sources (`CSV`, `XML`, `JSON`, `Excel`) are automatically staged on-demand in the workspace SQLite engine (`.fsqs/cache/engine.db`) with alias views matching their connection name, file name, and table name (`SalesFeed`, `sales_feed`, `LogisticsHub`, `Shipments`, `Carriers`). You can run direct parameterized SQLite queries via `Connections.sql.rawQuery`, `Connections.csv.<Conn>.rawQuery`, or `Connections.xml.<Conn>.rawQuery`.
"""

                mkCode
                    """
// 26. Direct parameterized SQLite queries on mapped XML (Shipments & Carriers) and CSV (sales_feed)
let xmlDirectRows =
    Connections.xml.LogisticsHub.rawQuery(
        "SELECT s.ShipmentId, s.TrackingNumber, c.Name AS Carrier, s.Destination, s.ShippingCost FROM Shipments s JOIN Carriers c ON s.CarrierId = c.CarrierId WHERE s.ShippingCost >= @minCost ORDER BY s.ShippingCost DESC LIMIT 5",
        [ "@minCost", box 100.0 ]
    )

printfn "Direct XML SQLite query returned %d rows" (xmlDirectRows.Rows |> Seq.length)

Connections.sql.rawQuery(
    "SELECT OrderId, Customer, Product, Category, TotalAmount, Status FROM sales_feed WHERE Category = @cat AND TotalAmount >= @minTotal ORDER BY TotalAmount DESC LIMIT 10",
    [ "@cat", box "Electronics"; "@minTotal", box 100.0 ]
)
"""

                mkMarkdown
                    """
---

### 📚 Official Documentation & References

- 📘 [F# Documentation (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/fsharp/)
- 🔍 [F# Query Expressions / LINQ Guide](https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/query-expressions)
- ⚡ [F# Interactive (FSI) Reference](https://learn.microsoft.com/en-us/dotnet/fsharp/tools/fsharp-interactive/)
"""
            ]

        serializeNotebook cells

