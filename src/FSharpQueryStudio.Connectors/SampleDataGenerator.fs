namespace FSharpQueryStudio.Connectors

open System
open System.IO
open System.Text.Encodings.Web
open System.Text.Json
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
    Description TEXT
);

CREATE TABLE IF NOT EXISTS Products (
    Id INTEGER PRIMARY KEY,
    Name TEXT NOT NULL,
    CategoryId INTEGER NOT NULL,
    Price REAL NOT NULL,
    StockQuantity INTEGER NOT NULL,
    FOREIGN KEY (CategoryId) REFERENCES Categories(Id)
);

CREATE TABLE IF NOT EXISTS Customers (
    Id INTEGER PRIMARY KEY,
    FirstName TEXT NOT NULL,
    LastName TEXT NOT NULL,
    Email TEXT NOT NULL,
    City TEXT NOT NULL,
    Country TEXT NOT NULL,
    CreatedAt TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS Orders (
    Id INTEGER PRIMARY KEY,
    CustomerId INTEGER NOT NULL,
    OrderDate TEXT NOT NULL,
    TotalAmount REAL NOT NULL,
    Status TEXT NOT NULL,
    FOREIGN KEY (CustomerId) REFERENCES Customers(Id)
);

CREATE TABLE IF NOT EXISTS OrderItems (
    Id INTEGER PRIMARY KEY,
    OrderId INTEGER NOT NULL,
    ProductId INTEGER NOT NULL,
    Quantity INTEGER NOT NULL,
    UnitPrice REAL NOT NULL,
    FOREIGN KEY (OrderId) REFERENCES Orders(Id),
    FOREIGN KEY (ProductId) REFERENCES Products(Id)
);
"""

    let private insertDataSql = """
INSERT OR IGNORE INTO Categories (Id, Name, Description) VALUES
(1, 'Electronics', 'Gadgets, devices, and computer peripherals'),
(2, 'Books', 'Technical and programming books'),
(3, 'Home & Kitchen', 'Appliances and household items'),
(4, 'Sports & Fitness', 'Sports apparel, footwear, and accessories');

INSERT OR IGNORE INTO Products (Id, Name, CategoryId, Price, StockQuantity) VALUES
(1, 'Laptop Pro 16', 1, 1499.99, 25),
(2, 'Wireless Ergonomic Mouse', 1, 49.90, 150),
(3, 'Mechanical Keyboard RGB', 1, 119.50, 75),
(4, 'Noise-Cancelling Headphones', 1, 249.00, 40),
(5, 'Expert F# 4.0', 2, 54.99, 30),
(6, 'Domain Modeling Made Functional', 2, 45.00, 60),
(7, 'F# Deep Dives', 2, 42.50, 20),
(8, 'Espresso Coffee Machine', 3, 189.90, 15),
(9, 'Insulated Stainless Tumbler', 3, 24.99, 200),
(10, 'Running Shoes Pro', 4, 129.99, 50);

INSERT OR IGNORE INTO Customers (Id, FirstName, LastName, Email, City, Country, CreatedAt) VALUES
(1, 'Carlos', 'Silva', 'carlos.silva@example.com', 'São Paulo', 'Brazil', '2025-11-10T10:00:00Z'),
(2, 'Alice', 'Smith', 'alice.smith@example.co.uk', 'London', 'UK', '2025-12-01T14:30:00Z'),
(3, 'John', 'Doe', 'john.doe@example.com', 'New York', 'USA', '2026-01-05T09:15:00Z'),
(4, 'Maria', 'Garcia', 'maria.garcia@example.es', 'Madrid', 'Spain', '2026-01-12T16:45:00Z'),
(5, 'Yuki', 'Tanaka', 'yuki.tanaka@example.jp', 'Tokyo', 'Japan', '2026-02-01T11:20:00Z'),
(6, 'Ana', 'Pereira', 'ana.pereira@example.com', 'Rio de Janeiro', 'Brazil', '2026-02-18T13:10:00Z');

INSERT OR IGNORE INTO Orders (Id, CustomerId, OrderDate, TotalAmount, Status) VALUES
(101, 1, '2026-03-01T10:00:00Z', 1549.89, 'Completed'),
(102, 2, '2026-03-05T15:20:00Z', 99.99, 'Completed'),
(103, 3, '2026-03-10T11:00:00Z', 298.90, 'Completed'),
(104, 1, '2026-03-15T09:40:00Z', 189.90, 'Processing'),
(105, 4, '2026-03-20T17:15:00Z', 129.99, 'Shipped'),
(106, 5, '2026-03-25T12:00:00Z', 119.50, 'Completed'),
(107, 6, '2026-04-01T14:50:00Z', 87.50, 'Processing'),
(108, 3, '2026-04-05T16:30:00Z', 249.00, 'Pending');

INSERT OR IGNORE INTO OrderItems (Id, OrderId, ProductId, Quantity, UnitPrice) VALUES
(1, 101, 1, 1, 1499.99),
(2, 101, 2, 1, 49.90),
(3, 102, 5, 1, 54.99),
(4, 102, 6, 1, 45.00),
(5, 103, 2, 1, 49.90),
(6, 103, 4, 1, 249.00),
(7, 104, 8, 1, 189.90),
(8, 105, 10, 1, 129.99),
(9, 106, 3, 1, 119.50),
(10, 107, 6, 1, 45.00),
(11, 107, 7, 1, 42.50),
(12, 108, 4, 1, 249.00);
"""

    let private createInventoryTablesSql = """
CREATE TABLE IF NOT EXISTS Warehouses (
    Id INTEGER PRIMARY KEY,
    Code TEXT NOT NULL,
    City TEXT NOT NULL,
    Country TEXT NOT NULL,
    Capacity INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS StockLevels (
    Id INTEGER PRIMARY KEY,
    WarehouseId INTEGER NOT NULL,
    ProductSku TEXT NOT NULL,
    QuantityOnHand INTEGER NOT NULL,
    ReorderLevel INTEGER NOT NULL,
    LastRestocked TEXT NOT NULL,
    FOREIGN KEY (WarehouseId) REFERENCES Warehouses(Id)
);

CREATE TABLE IF NOT EXISTS Suppliers (
    Id INTEGER PRIMARY KEY,
    CompanyName TEXT NOT NULL,
    ContactEmail TEXT NOT NULL,
    Country TEXT NOT NULL,
    Rating REAL NOT NULL
);
"""

    let private insertInventoryDataSql = """
INSERT OR IGNORE INTO Warehouses (Id, Code, City, Country, Capacity) VALUES
(1, 'WH-SP-01', 'São Paulo', 'Brazil', 50000),
(2, 'WH-LON-01', 'London', 'UK', 35000),
(3, 'WH-NYC-01', 'New York', 'USA', 45000),
(4, 'WH-MAD-01', 'Madrid', 'Spain', 25000);

INSERT OR IGNORE INTO StockLevels (Id, WarehouseId, ProductSku, QuantityOnHand, ReorderLevel, LastRestocked) VALUES
(1, 1, 'LAPTOP-PRO-16', 45, 10, '2026-03-01T08:00:00Z'),
(2, 1, 'MOUSE-WL-ERG', 220, 50, '2026-03-05T09:30:00Z'),
(3, 2, 'BOOK-EXP-FS4', 80, 20, '2026-02-28T14:00:00Z'),
(4, 2, 'BOOK-DM-FUNC', 110, 30, '2026-03-10T11:15:00Z'),
(5, 3, 'HEADPHONES-NC', 65, 15, '2026-03-12T16:00:00Z'),
(6, 4, 'SHOES-PRO-RUN', 90, 25, '2026-03-15T10:00:00Z');

INSERT OR IGNORE INTO Suppliers (Id, CompanyName, ContactEmail, Country, Rating) VALUES
(1, 'Global Tech Logistics', 'contact@globaltech.com', 'Brazil', 4.8),
(2, 'EuroPublishing Group', 'supply@europub.co.uk', 'UK', 4.9),
(3, 'Apex Electronics Corp', 'sales@apexelectronics.com', 'USA', 4.6),
(4, 'Iberia Sports Supply', 'info@iberiasports.es', 'Spain', 4.7);
"""

    let private sampleCsvContent = """Date,OrderId,Customer,Product,Category,Quantity,UnitPrice,TotalAmount,Status
2026-03-01,101,Carlos Silva,Laptop Pro 16,Electronics,1,1499.99,1499.99,Completed
2026-03-05,102,Alice Smith,Expert F# 4.0,Books,1,54.99,54.99,Completed
2026-03-05,102,Alice Smith,Domain Modeling Made Functional,Books,1,45.00,45.00,Completed
2026-03-10,103,John Doe,Wireless Ergonomic Mouse,Electronics,1,49.90,49.90,Completed
2026-03-10,103,John Doe,Noise-Cancelling Headphones,Electronics,1,249.00,249.00,Completed
2026-03-15,104,Carlos Silva,Espresso Coffee Machine,Home & Kitchen,1,189.90,189.90,Processing
2026-03-20,105,Maria Garcia,Running Shoes Pro,Sports & Fitness,1,129.99,129.99,Shipped
2026-03-25,106,Yuki Tanaka,Mechanical Keyboard RGB,Electronics,1,119.50,119.50,Completed
2026-04-01,107,Ana Pereira,F# Deep Dives,Books,1,42.50,42.50,Processing
2026-04-05,108,John Doe,Noise-Cancelling Headphones,Electronics,1,249.00,249.00,Pending
"""

    let private sampleCustomersCsvContent = """CustomerId,CustomerName,LoyaltyTier,TotalSpent,Score,ActiveSince,Feedback
1,Carlos Silva,Platinum,1549.89,9.8,2025-11-10,Excelente atendimento e entrega rapida
2,Alice Smith,Gold,499.50,9.5,2025-12-01,Great book selection and fast dispatch
3,John Doe,Silver,298.90,8.7,2026-01-05,Reliable wireless accessories and audio gear
4,Maria Garcia,Gold,389.90,9.2,2026-01-12,Muy buena calidad en productos deportivos
5,Yuki Tanaka,Silver,119.50,9.0,2026-02-01,High quality mechanical keyboards
6,Ana Pereira,Bronze,87.50,8.9,2026-02-18,Atendimento cordial e catalogo diversificado
"""

    let private sampleJsonContent = """{
  "Products": [
    { "ProductId": 101, "Sku": "PRD-AI-01", "Title": "Neural Compute Stick X2", "Category": "AI Hardware", "Price": 249.90, "InStock": true, "Rating": 4.9 },
    { "ProductId": 102, "Sku": "PRD-NET-02", "Title": "10GbE Smart Switch", "Category": "Networking", "Price": 389.50, "InStock": true, "Rating": 4.8 },
    { "ProductId": 103, "Sku": "PRD-DEV-03", "Title": "Ergonomic Split Keyboard", "Category": "Peripherals", "Price": 179.00, "InStock": true, "Rating": 4.7 },
    { "ProductId": 104, "Sku": "PRD-CLD-04", "Title": "NVMe Gen5 Raid Enclosure", "Category": "Storage", "Price": 319.99, "InStock": false, "Rating": 4.6 }
  ],
  "Reviews": [
    { "ReviewId": 1, "ProductId": 101, "Reviewer": "Elena Rostova", "Stars": 5, "VerifiedPurchase": true, "Comment": "Blazing fast local inference throughput" },
    { "ReviewId": 2, "ProductId": 102, "Reviewer": "Marcus Vance", "Stars": 5, "VerifiedPurchase": true, "Comment": "Rock-solid VLAN tagging and low latency" },
    { "ReviewId": 3, "ProductId": 103, "Reviewer": "Camila Rocha", "Stars": 4, "VerifiedPurchase": true, "Comment": "Great tactile switches for long coding sessions" },
    { "ReviewId": 4, "ProductId": 101, "Reviewer": "Kenji Sato", "Stars": 5, "VerifiedPurchase": true, "Comment": "Seamless ONNX runtime integration" }
  ]
}
"""

    let private sampleXmlContent = """<?xml version="1.0" encoding="utf-8"?>
<LogisticsHub>
  <Carriers>
    <Carrier CarrierId="1" Code="DHL-INT" Name="DHL Global Express" HubCountry="Germany" OnTimeRate="98.4" />
    <Carrier CarrierId="2" Code="FDX-PRI" Name="FedEx Priority Air" HubCountry="USA" OnTimeRate="97.8" />
    <Carrier CarrierId="3" Code="LAT-CAR" Name="Latam Cargo Logistics" HubCountry="Brazil" OnTimeRate="96.5" />
  </Carriers>
  <Shipments>
    <Shipment ShipmentId="9001" CarrierId="1">
      <TrackingNumber>DHL-2026-8841</TrackingNumber>
      <Destination>Sao Paulo</Destination>
      <WeightKg>14.5</WeightKg>
      <ShippingCost>128.90</ShippingCost>
      <Status>Delivered</Status>
    </Shipment>
    <Shipment ShipmentId="9002" CarrierId="2">
      <TrackingNumber>FDX-2026-3319</TrackingNumber>
      <Destination>London</Destination>
      <WeightKg>8.2</WeightKg>
      <ShippingCost>94.50</ShippingCost>
      <Status>In Transit</Status>
    </Shipment>
    <Shipment ShipmentId="9003" CarrierId="3">
      <TrackingNumber>LAT-2026-5502</TrackingNumber>
      <Destination>Madrid</Destination>
      <WeightKg>22.0</WeightKg>
      <ShippingCost>185.00</ShippingCost>
      <Status>Delivered</Status>
    </Shipment>
    <Shipment ShipmentId="9004" CarrierId="1">
      <TrackingNumber>DHL-2026-9104</TrackingNumber>
      <Destination>Tokyo</Destination>
      <WeightKg>5.4</WeightKg>
      <ShippingCost>112.40</ShippingCost>
      <Status>Customs Cleared</Status>
    </Shipment>
  </Shipments>
</LogisticsHub>
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
# 🚀 F# Query Studio — Multi-Source Interactive Notebook

> **Welcome to F# Query Studio!** This interactive notebook demonstrates querying multiple data sources of the same type without collisions, alongside strongly-typed LINQ, joins, analytical aggregations, and flat files.

---

### 📋 Pre-Connected Data Sources

| Connection | Kind | Description | Primary Tables & Columns |
| :--- | :--- | :--- | :--- |
| **`SampleStore`** | SQLite | Relational e-commerce orders & catalog | `Categories`, `Products`, `Customers`, `Orders`, `OrderItems` |
| **`InventoryStore`** | SQLite | Warehouse inventory & logistics | `Warehouses`, `StockLevels`, `Suppliers` |
| **`SalesFeed`** | CSV | Flat-file transaction export | `Date`, `OrderId`, `Customer`, `Product`, `Category`, `TotalAmount`, `Status` |
| **`CustomersFeed`** | CSV | Flat-file loyalty & satisfaction feed | `CustomerId`, `CustomerName`, `LoyaltyTier`, `TotalSpent`, `Score`, `Feedback` |
| **`RegionalTargets`** | Excel (`.xlsx`) | Multi-sheet regional revenue targets & budgets | `RegionalTargets`, `QuarterlyBudget` |
| **`CommerceOps`** | Excel (`.xlsx`) | 4-sheet e-commerce operations & logistics workbook | `Customers`, `ProductsStock`, `Sales`, `DeliveryLogs` |
| **`ProductCatalog`** | JSON (`.json`) | Multi-collection product catalog & customer reviews | `Products`, `Reviews` |
| **`LogisticsHub`** | XML (`.xml`) | Multi-collection carrier network & international shipments | `Carriers`, `Shipments` |
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

Compute business metrics, order counts, and revenue statistics grouped by order status.
"""

                mkCode """
// 4. Financial breakdown: Total revenue and average ticket by order status
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
## 14. 🌐 Cross-Database & Cross-Kind Overview: 9-Source Pipeline

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
# 🚀 F# Query Studio — Guia Interativo Multi-Fontes

> **Bem-vindo ao F# Query Studio!** Este notebook interativo demonstra o funcionamento com múltiplas fontes de dados do mesmo tipo simultâneas, sem colisões de nomes, além de consultas LINQ fortemente tipadas, agregações, joins e análise de arquivos planos.

---

### 📋 Fontes de Dados Pré-Conectadas

| Conexão | Tipo | Descrição | Tabelas / Colunas Principais |
| :--- | :--- | :--- | :--- |
| **`SampleStore`** | SQLite | E-commerce relacional (pedidos e catálogo) | `Categories`, `Products`, `Customers`, `Orders`, `OrderItems` |
| **`InventoryStore`** | SQLite | Gestão de armazéns e logística | `Warehouses`, `StockLevels`, `Suppliers` |
| **`SalesFeed`** | CSV | Feed diário de transações de vendas | `Date`, `OrderId`, `Customer`, `Product`, `Category`, `TotalAmount`, `Status` |
| **`CustomersFeed`** | CSV | Feed de fidelidade e avaliação de clientes | `CustomerId`, `CustomerName`, `LoyaltyTier`, `TotalSpent`, `Score`, `Feedback` |
| **`RegionalTargets`** | Excel (`.xlsx`) | Planilha multi-abas de metas regionais e orçamento | `RegionalTargets`, `QuarterlyBudget` |
| **`CommerceOps`** | Excel (`.xlsx`) | Planilha em inglês com 4 abas (clientes, estoque, vendas e delivery) | `Customers`, `ProductsStock`, `Sales`, `DeliveryLogs` |
| **`ProductCatalog`** | JSON (`.json`) | Catálogo multi-coleções de produtos e avaliações | `Products`, `Reviews` |
| **`LogisticsHub`** | XML (`.xml`) | Hub logístico com transportadoras e remessas internacionais | `Carriers`, `Shipments` |
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

Realize agregações analíticas (`sumBy`, `count`, `averageBy`) agrupando os dados por status do pedido.
"""

                mkCode """
// 4. Resumo financeiro: Faturamento total e ticket médio por Status do Pedido
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
## 14. 🌐 Visão Consolidada Multi-Fontes: SQLite + CSV + Excel + JSON + XML + REST

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

        let connectionString = $"Data Source={filePath}"
        use connection = new SqliteConnection(connectionString)
        connection.Open()

        use cmd1 = connection.CreateCommand()
        cmd1.CommandText <- createTablesSql
        cmd1.ExecuteNonQuery() |> ignore

        use cmd2 = connection.CreateCommand()
        cmd2.CommandText <- insertDataSql
        cmd2.ExecuteNonQuery() |> ignore

        SqliteConnection.ClearAllPools()

    let createInventoryDatabase (filePath: string) : unit =
        let dir = Path.GetDirectoryName(filePath)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        let connectionString = $"Data Source={filePath}"
        use connection = new SqliteConnection(connectionString)
        connection.Open()

        use cmd1 = connection.CreateCommand()
        cmd1.CommandText <- createInventoryTablesSql
        cmd1.ExecuteNonQuery() |> ignore

        use cmd2 = connection.CreateCommand()
        cmd2.CommandText <- insertInventoryDataSql
        cmd2.ExecuteNonQuery() |> ignore

        SqliteConnection.ClearAllPools()

    let private writeTextShared (filePath: string) (content: string) =
        let bytes = System.Text.Encoding.UTF8.GetBytes(content)
        use fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)
        fs.Write(bytes, 0, bytes.Length)

    let private saveWorkbookShared (workbook: XLWorkbook) (filePath: string) =
        use ms = new MemoryStream()
        workbook.SaveAs(ms)
        let bytes = ms.ToArray()
        use fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)
        fs.Write(bytes, 0, bytes.Length)

    let createSampleCsv (filePath: string) : unit =
        let dir = Path.GetDirectoryName(filePath)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        writeTextShared filePath sampleCsvContent

    let createSampleCustomersCsv (filePath: string) : unit =
        let dir = Path.GetDirectoryName(filePath)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        writeTextShared filePath sampleCustomersCsvContent

    let createSampleExcelWorkbook (filePath: string) : unit =
        let dir = Path.GetDirectoryName(filePath)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        use workbook = new XLWorkbook()

        let targetsSheet = workbook.Worksheets.Add("RegionalTargets")
        let targetHeaders = [| "Region"; "Country"; "AnnualTarget"; "AchievedRevenue"; "GrowthRate"; "LeadManager" |]
        for colIdx in 0 .. targetHeaders.Length - 1 do
            targetsSheet.Cell(1, colIdx + 1).Value <- targetHeaders.[colIdx]

        let targetRows: (string * string * decimal * decimal * decimal * string) array =
            [| ("LATAM", "Brazil", 850000.00M, 912400.50M, 14.5M, "Camila Rocha")
               ("EMEA", "UK", 620000.00M, 645800.25M, 9.2M, "Oliver Grant")
               ("NA", "USA", 1200000.00M, 1185000.75M, 11.8M, "Sarah Jenkins")
               ("EMEA", "Spain", 410000.00M, 438900.10M, 12.4M, "Diego Morales")
               ("APAC", "Japan", 530000.00M, 559200.40M, 10.6M, "Kenji Sato") |]

        for rowIdx in 0 .. targetRows.Length - 1 do
            let region, country, target, achieved, growth, manager = targetRows.[rowIdx]
            let r = rowIdx + 2
            targetsSheet.Cell(r, 1).Value <- region
            targetsSheet.Cell(r, 2).Value <- country
            targetsSheet.Cell(r, 3).Value <- target
            targetsSheet.Cell(r, 4).Value <- achieved
            targetsSheet.Cell(r, 5).Value <- growth
            targetsSheet.Cell(r, 6).Value <- manager

        let budgetSheet = workbook.Worksheets.Add("QuarterlyBudget")
        let budgetHeaders = [| "Quarter"; "Department"; "AllocatedBudget"; "ActualSpend"; "Status" |]
        for colIdx in 0 .. budgetHeaders.Length - 1 do
            budgetSheet.Cell(1, colIdx + 1).Value <- budgetHeaders.[colIdx]

        let budgetRows: (string * string * decimal * decimal * string) array =
            [| ("2026-Q1", "Engineering", 320000.00M, 312500.00M, "On Track")
               ("2026-Q1", "Cloud Infrastructure", 145000.00M, 141200.50M, "Optimized")
               ("2026-Q2", "Data Analytics", 210000.00M, 198400.25M, "Under Budget")
               ("2026-Q2", "Developer Relations", 95000.00M, 94100.75M, "On Track") |]

        for rowIdx in 0 .. budgetRows.Length - 1 do
            let quarter, dept, allocated, actual, status = budgetRows.[rowIdx]
            let r = rowIdx + 2
            budgetSheet.Cell(r, 1).Value <- quarter
            budgetSheet.Cell(r, 2).Value <- dept
            budgetSheet.Cell(r, 3).Value <- allocated
            budgetSheet.Cell(r, 4).Value <- actual
            budgetSheet.Cell(r, 5).Value <- status

        saveWorkbookShared workbook filePath

    let createSampleCommerceExcelWorkbook (filePath: string) : unit =
        let dir = Path.GetDirectoryName(filePath)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        use workbook = new XLWorkbook()

        // Sheet 1: Customers
        let customersSheet = workbook.Worksheets.Add("Customers")
        let customerHeaders = [| "CustomerId"; "FullName"; "Email"; "Country"; "City"; "CustomerSegment" |]
        for colIdx in 0 .. customerHeaders.Length - 1 do
            customersSheet.Cell(1, colIdx + 1).Value <- customerHeaders.[colIdx]

        let customerRows: (int * string * string * string * string * string) array =
            [| (1, "Emma Watson", "emma.watson@ukmail.co.uk", "UK", "London", "Enterprise")
               (2, "Liam Carter", "liam.carter@techcorp.com", "USA", "Austin", "Enterprise")
               (3, "Lucas Andrade", "lucas.andrade@brshop.com.br", "Brazil", "Sao Paulo", "SMB")
               (4, "Sofia Martinez", "sofia.m@madridretail.es", "Spain", "Madrid", "SMB")
               (5, "Kenji Takahashi", "kenji.t@tokyocloud.jp", "Japan", "Tokyo", "Enterprise") |]

        for rowIdx in 0 .. customerRows.Length - 1 do
            let id, fullName, email, country, city, segment = customerRows.[rowIdx]
            let r = rowIdx + 2
            customersSheet.Cell(r, 1).Value <- id
            customersSheet.Cell(r, 2).Value <- fullName
            customersSheet.Cell(r, 3).Value <- email
            customersSheet.Cell(r, 4).Value <- country
            customersSheet.Cell(r, 5).Value <- city
            customersSheet.Cell(r, 6).Value <- segment

        // Sheet 2: ProductsStock
        let productsSheet = workbook.Worksheets.Add("ProductsStock")
        let productHeaders = [| "ProductId"; "Sku"; "ProductName"; "Category"; "UnitPrice"; "StockQuantity"; "WarehouseZone" |]
        for colIdx in 0 .. productHeaders.Length - 1 do
            productsSheet.Cell(1, colIdx + 1).Value <- productHeaders.[colIdx]

        let productRows: (int * string * string * string * decimal * int * string) array =
            [| (101, "HW-LAP-16", "Ultrabook Pro 16", "Hardware", 1599.00M, 45, "Zone-A")
               (102, "HW-MON-27", "4K IPS Monitor 27", "Hardware", 449.50M, 80, "Zone-A")
               (103, "ACC-KBD-01", "Mechanical Tactile Keyboard", "Accessories", 129.00M, 150, "Zone-B")
               (104, "ACC-MSE-02", "Ergonomic Wireless Mouse", "Accessories", 69.90M, 210, "Zone-B")
               (105, "NET-RTR-05", "Wi-Fi 7 Mesh Router", "Networking", 299.00M, 60, "Zone-C") |]

        for rowIdx in 0 .. productRows.Length - 1 do
            let pid, sku, pname, cat, price, stock, zone = productRows.[rowIdx]
            let r = rowIdx + 2
            productsSheet.Cell(r, 1).Value <- pid
            productsSheet.Cell(r, 2).Value <- sku
            productsSheet.Cell(r, 3).Value <- pname
            productsSheet.Cell(r, 4).Value <- cat
            productsSheet.Cell(r, 5).Value <- price
            productsSheet.Cell(r, 6).Value <- stock
            productsSheet.Cell(r, 7).Value <- zone

        // Sheet 3: Sales
        let salesSheet = workbook.Worksheets.Add("Sales")
        let salesHeaders = [| "SaleId"; "CustomerId"; "ProductId"; "Quantity"; "TotalAmount"; "SaleDate"; "PaymentMethod" |]
        for colIdx in 0 .. salesHeaders.Length - 1 do
            salesSheet.Cell(1, colIdx + 1).Value <- salesHeaders.[colIdx]

        let salesRows: (int * int * int * int * decimal * string * string) array =
            [| (1001, 1, 101, 2, 3198.00M, "2026-03-10", "Corporate Card")
               (1002, 2, 102, 3, 1348.50M, "2026-03-12", "Wire Transfer")
               (1003, 3, 103, 5, 645.00M, "2026-03-15", "Credit Card")
               (1004, 4, 105, 2, 598.00M, "2026-03-18", "Credit Card")
               (1005, 5, 101, 1, 1599.00M, "2026-03-20", "Wire Transfer")
               (1006, 1, 104, 4, 279.60M, "2026-03-22", "Corporate Card") |]

        for rowIdx in 0 .. salesRows.Length - 1 do
            let sid, cid, pid, qty, total, sdate, method_ = salesRows.[rowIdx]
            let r = rowIdx + 2
            salesSheet.Cell(r, 1).Value <- sid
            salesSheet.Cell(r, 2).Value <- cid
            salesSheet.Cell(r, 3).Value <- pid
            salesSheet.Cell(r, 4).Value <- qty
            salesSheet.Cell(r, 5).Value <- total
            salesSheet.Cell(r, 6).Value <- sdate
            salesSheet.Cell(r, 7).Value <- method_

        // Sheet 4: DeliveryLogs
        let deliverySheet = workbook.Worksheets.Add("DeliveryLogs")
        let deliveryHeaders = [| "DeliveryId"; "SaleId"; "Carrier"; "TrackingCode"; "DeliveryStatus"; "DispatchedDate"; "EstimatedArrival" |]
        for colIdx in 0 .. deliveryHeaders.Length - 1 do
            deliverySheet.Cell(1, colIdx + 1).Value <- deliveryHeaders.[colIdx]

        let deliveryRows: (int * int * string * string * string * string * string) array =
            [| (5001, 1001, "DHL Express", "DHL-99201-UK", "Delivered", "2026-03-11", "2026-03-13")
               (5002, 1002, "FedEx Priority", "FDX-88412-US", "Delivered", "2026-03-13", "2026-03-15")
               (5003, 1003, "UPS Worldwide", "UPS-77109-BR", "In Transit", "2026-03-16", "2026-03-21")
               (5004, 1004, "DHL Express", "DHL-66320-ES", "Delivered", "2026-03-19", "2026-03-21")
               (5005, 1005, "Yamato Global", "YMT-55401-JP", "In Transit", "2026-03-21", "2026-03-24")
               (5006, 1006, "DHL Express", "DHL-99540-UK", "Out for Delivery", "2026-03-23", "2026-03-25") |]

        for rowIdx in 0 .. deliveryRows.Length - 1 do
            let did, sid, carrier, tracking, status, dispatched, arrival = deliveryRows.[rowIdx]
            let r = rowIdx + 2
            deliverySheet.Cell(r, 1).Value <- did
            deliverySheet.Cell(r, 2).Value <- sid
            deliverySheet.Cell(r, 3).Value <- carrier
            deliverySheet.Cell(r, 4).Value <- tracking
            deliverySheet.Cell(r, 5).Value <- status
            deliverySheet.Cell(r, 6).Value <- dispatched
            deliverySheet.Cell(r, 7).Value <- arrival

        saveWorkbookShared workbook filePath

    let createSampleJsonFile (filePath: string) : unit =
        let dir = Path.GetDirectoryName(filePath)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        writeTextShared filePath sampleJsonContent

    let createSampleXmlFile (filePath: string) : unit =
        let dir = Path.GetDirectoryName(filePath)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        writeTextShared filePath sampleXmlContent

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

        createSampleStoreDatabase dbPath
        createInventoryDatabase db2Path
        createSampleCsv csvPath
        createSampleCustomersCsv csv2Path
        createSampleExcelWorkbook excelPath
        createSampleCommerceExcelWorkbook excel2Path
        createSampleJsonFile jsonPath
        createSampleXmlFile xmlPath
        createSampleDemoNotebook notebookPath
        createSampleDemoNotebookPt notebookPtPath

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
