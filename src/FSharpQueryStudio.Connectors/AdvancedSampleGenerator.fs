namespace FSharpQueryStudio.Connectors

open System
open System.IO
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open FSharpQueryStudio.Protocol

module AdvancedSampleGenerator =

    type AdvancedSampleResult =
        { TargetDir: string
          SelectedEngines: string list
          DockerComposePath: string option
          InitScripts: string list
          NotebooksGenerated: string list
          RegisteredConnections: string list }

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

    let getPostgresInitSql () = """
-- PostgreSQL Advanced Schema Initialization
CREATE TABLE IF NOT EXISTS categories (
    id SERIAL PRIMARY KEY,
    name VARCHAR(100) NOT NULL,
    description TEXT,
    code VARCHAR(20) NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT true
);

CREATE TABLE IF NOT EXISTS products (
    id SERIAL PRIMARY KEY,
    category_id INT NOT NULL REFERENCES categories(id),
    name VARCHAR(150) NOT NULL,
    sku VARCHAR(50) NOT NULL,
    price NUMERIC(10,2) NOT NULL,
    cost_price NUMERIC(10,2) NOT NULL,
    stock_quantity INT NOT NULL,
    specs JSONB NOT NULL DEFAULT '{}'::jsonb
);

CREATE TABLE IF NOT EXISTS customers (
    id SERIAL PRIMARY KEY,
    first_name VARCHAR(100) NOT NULL,
    last_name VARCHAR(100) NOT NULL,
    email VARCHAR(150) NOT NULL,
    city VARCHAR(100) NOT NULL,
    country VARCHAR(100) NOT NULL,
    profile JSONB NOT NULL DEFAULT '{}'::jsonb
);

CREATE TABLE IF NOT EXISTS orders (
    id SERIAL PRIMARY KEY,
    customer_id INT NOT NULL REFERENCES customers(id),
    order_date TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    status VARCHAR(50) NOT NULL,
    total_amount NUMERIC(12,2) NOT NULL
);

CREATE TABLE IF NOT EXISTS order_items (
    id SERIAL PRIMARY KEY,
    order_id INT NOT NULL REFERENCES orders(id),
    product_id INT NOT NULL REFERENCES products(id),
    quantity INT NOT NULL,
    unit_price NUMERIC(10,2) NOT NULL,
    line_total NUMERIC(12,2) NOT NULL
);

-- Seed Data
INSERT INTO categories (id, name, description, code, is_active) VALUES
(1, 'Electronics', 'Gadgets and computing devices', 'ELEC', true),
(2, 'Furniture', 'Office and home furniture', 'FURN', true),
(3, 'Stationery', 'Office supplies and writing gear', 'STAT', true)
ON CONFLICT (id) DO NOTHING;

INSERT INTO products (id, category_id, name, sku, price, cost_price, stock_quantity, specs) VALUES
(1, 1, 'Pro Mechanical Keyboard', 'ELEC-KB-01', 129.99, 65.00, 150, '{"brand": "KeyMaster", "wireless": true, "switch": "Cherry MX Brown"}'::jsonb),
(2, 1, 'UltraWide Gaming Monitor', 'ELEC-MN-02', 499.99, 310.00, 45, '{"brand": "VisionMax", "resolution": "3440x1440", "refresh_hz": 144}'::jsonb),
(3, 1, 'Ergonomic Precision Mouse', 'ELEC-MS-03', 79.50, 32.00, 200, '{"brand": "PrecisionTech", "wireless": true, "dpi": 16000}'::jsonb),
(4, 2, 'Adjustable Standing Desk', 'FURN-DK-01', 349.00, 180.00, 30, '{"brand": "ErgoLift", "motor": "Dual", "max_kg": 120}'::jsonb),
(5, 2, 'Mesh Ergonomic Task Chair', 'FURN-CH-02', 249.99, 120.00, 60, '{"brand": "AeroComfort", "armrest": "4D", "tilt": true}'::jsonb),
(6, 3, 'Smart Digital Notebook', 'STAT-NB-01', 35.00, 14.00, 300, '{"brand": "PaperSync", "pages": 120, "cloud_sync": true}'::jsonb)
ON CONFLICT (id) DO NOTHING;

INSERT INTO customers (id, first_name, last_name, email, city, country, profile) VALUES
(1, 'Alice', 'Silva', 'alice.silva@example.com', 'Sao Paulo', 'Brazil', '{"tier": "Gold", "loyalty_points": 1450, "preferred_channel": "App"}'::jsonb),
(2, 'Bob', 'Miller', 'bob.miller@example.com', 'Austin', 'USA', '{"tier": "Platinum", "loyalty_points": 3200, "preferred_channel": "Web"}'::jsonb),
(3, 'Carla', 'Santos', 'carla.santos@example.com', 'Curitiba', 'Brazil', '{"tier": "Silver", "loyalty_points": 650, "preferred_channel": "Email"}'::jsonb),
(4, 'David', 'Kowalski', 'david.k@example.com', 'Warsaw', 'Poland', '{"tier": "Bronze", "loyalty_points": 210, "preferred_channel": "Web"}'::jsonb)
ON CONFLICT (id) DO NOTHING;

INSERT INTO orders (id, customer_id, order_date, status, total_amount) VALUES
(1, 1, '2026-01-15 10:30:00', 'Completed', 629.98),
(2, 1, '2026-02-10 14:15:00', 'Completed', 129.99),
(3, 2, '2026-02-20 18:45:00', 'Completed', 598.99),
(4, 3, '2026-03-01 09:00:00', 'Pending', 79.50)
ON CONFLICT (id) DO NOTHING;

INSERT INTO order_items (id, order_id, product_id, quantity, unit_price, line_total) VALUES
(1, 1, 1, 1, 129.99, 129.99),
(2, 1, 2, 1, 499.99, 499.99),
(3, 2, 1, 1, 129.99, 129.99),
(4, 3, 2, 1, 499.99, 499.99),
(5, 3, 3, 1, 79.50, 79.50),
(6, 4, 3, 1, 79.50, 79.50)
ON CONFLICT (id) DO NOTHING;
"""

    let getSqlServerInitSql () = """
-- SQL Server Advanced Schema Initialization
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Categories')
BEGIN
    CREATE TABLE Categories (
        Id INT PRIMARY KEY,
        Name NVARCHAR(100) NOT NULL,
        Description NVARCHAR(MAX),
        Code NVARCHAR(20) NOT NULL,
        IsActive BIT NOT NULL DEFAULT 1
    );
END

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Products')
BEGIN
    CREATE TABLE Products (
        Id INT PRIMARY KEY,
        CategoryId INT NOT NULL FOREIGN KEY REFERENCES Categories(Id),
        Name NVARCHAR(150) NOT NULL,
        Sku NVARCHAR(50) NOT NULL,
        Price DECIMAL(10,2) NOT NULL,
        CostPrice DECIMAL(10,2) NOT NULL,
        StockQuantity INT NOT NULL,
        Specs NVARCHAR(MAX) NOT NULL DEFAULT '{}'
    );
END

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Customers')
BEGIN
    CREATE TABLE Customers (
        Id INT PRIMARY KEY,
        FirstName NVARCHAR(100) NOT NULL,
        LastName NVARCHAR(100) NOT NULL,
        Email NVARCHAR(150) NOT NULL,
        City NVARCHAR(100) NOT NULL,
        Country NVARCHAR(100) NOT NULL,
        Profile NVARCHAR(MAX) NOT NULL DEFAULT '{}'
    );
END

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Orders')
BEGIN
    CREATE TABLE Orders (
        Id INT PRIMARY KEY,
        CustomerId INT NOT NULL FOREIGN KEY REFERENCES Customers(Id),
        OrderDate DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        Status NVARCHAR(50) NOT NULL,
        TotalAmount DECIMAL(12,2) NOT NULL
    );
END

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'OrderItems')
BEGIN
    CREATE TABLE OrderItems (
        Id INT PRIMARY KEY,
        OrderId INT NOT NULL FOREIGN KEY REFERENCES Orders(Id),
        ProductId INT NOT NULL FOREIGN KEY REFERENCES Products(Id),
        Quantity INT NOT NULL,
        UnitPrice DECIMAL(10,2) NOT NULL,
        LineTotal DECIMAL(12,2) NOT NULL
    );
END

-- Seed Data
IF NOT EXISTS (SELECT 1 FROM Categories WHERE Id = 1)
BEGIN
    INSERT INTO Categories (Id, Name, Description, Code, IsActive) VALUES
    (1, N'Electronics', N'Gadgets and computing devices', N'ELEC', 1),
    (2, N'Furniture', N'Office and home furniture', N'FURN', 1),
    (3, N'Stationery', N'Office supplies and writing gear', N'STAT', 1);
END

IF NOT EXISTS (SELECT 1 FROM Products WHERE Id = 1)
BEGIN
    INSERT INTO Products (Id, CategoryId, Name, Sku, Price, CostPrice, StockQuantity, Specs) VALUES
    (1, 1, N'Pro Mechanical Keyboard', N'ELEC-KB-01', 129.99, 65.00, 150, N'{"brand": "KeyMaster", "wireless": true, "switch": "Cherry MX Brown"}'),
    (2, 1, N'UltraWide Gaming Monitor', N'ELEC-MN-02', 499.99, 310.00, 45, N'{"brand": "VisionMax", "resolution": "3440x1440", "refresh_hz": 144}'),
    (3, 1, N'Ergonomic Precision Mouse', N'ELEC-MS-03', 79.50, 32.00, 200, N'{"brand": "PrecisionTech", "wireless": true, "dpi": 16000}'),
    (4, 2, N'Adjustable Standing Desk', N'FURN-DK-01', 349.00, 180.00, 30, N'{"brand": "ErgoLift", "motor": "Dual", "max_kg": 120}'),
    (5, 2, N'Mesh Ergonomic Task Chair', N'FURN-CH-02', 249.99, 120.00, 60, N'{"brand": "AeroComfort", "armrest": "4D", "tilt": true}'),
    (6, 3, N'Smart Digital Notebook', N'STAT-NB-01', 35.00, 14.00, 300, N'{"brand": "PaperSync", "pages": 120, "cloud_sync": true}');
END

IF NOT EXISTS (SELECT 1 FROM Customers WHERE Id = 1)
BEGIN
    INSERT INTO Customers (Id, FirstName, LastName, Email, City, Country, Profile) VALUES
    (1, N'Alice', N'Silva', N'alice.silva@example.com', N'Sao Paulo', N'Brazil', N'{"tier": "Gold", "loyalty_points": 1450, "preferred_channel": "App"}'),
    (2, N'Bob', N'Miller', N'bob.miller@example.com', N'Austin', N'USA', N'{"tier": "Platinum", "loyalty_points": 3200, "preferred_channel": "Web"}'),
    (3, N'Carla', N'Santos', N'carla.santos@example.com', N'Curitiba', N'Brazil', N'{"tier": "Silver", "loyalty_points": 650, "preferred_channel": "Email"}'),
    (4, N'David', N'Kowalski', N'david.k@example.com', N'Warsaw', N'Poland', N'{"tier": "Bronze", "loyalty_points": 210, "preferred_channel": "Web"}');
END

IF NOT EXISTS (SELECT 1 FROM Orders WHERE Id = 1)
BEGIN
    INSERT INTO Orders (Id, CustomerId, OrderDate, Status, TotalAmount) VALUES
    (1, 1, '2026-01-15T10:30:00', N'Completed', 629.98),
    (2, 1, '2026-02-10T14:15:00', N'Completed', 129.99),
    (3, 2, '2026-02-20T18:45:00', N'Completed', 598.99),
    (4, 3, '2026-03-01T09:00:00', N'Pending', 79.50);
END

IF NOT EXISTS (SELECT 1 FROM OrderItems WHERE Id = 1)
BEGIN
    INSERT INTO OrderItems (Id, OrderId, ProductId, Quantity, UnitPrice, LineTotal) VALUES
    (1, 1, 1, 1, 129.99, 129.99),
    (2, 1, 2, 1, 499.99, 499.99),
    (3, 2, 1, 1, 129.99, 129.99),
    (4, 3, 2, 1, 499.99, 499.99),
    (5, 3, 3, 1, 79.50, 79.50),
    (6, 4, 3, 1, 79.50, 79.50);
END
"""

    let getDockerComposeYaml (hasPostgres: bool) (hasSqlServer: bool) : string =
        let sb = StringBuilder()
        sb.AppendLine("version: '3.8'") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("services:") |> ignore

        if hasPostgres then
            sb.AppendLine("  fsqs-postgres:") |> ignore
            sb.AppendLine("    image: postgres:16-alpine") |> ignore
            sb.AppendLine("    container_name: fsqs-postgres") |> ignore
            sb.AppendLine("    restart: always") |> ignore
            sb.AppendLine("    environment:") |> ignore
            sb.AppendLine("      POSTGRES_USER: postgres") |> ignore
            sb.AppendLine("      POSTGRES_PASSWORD: Password123!") |> ignore
            sb.AppendLine("      POSTGRES_DB: fsqs_demo") |> ignore
            sb.AppendLine("    ports:") |> ignore
            sb.AppendLine("      - \"5432:5432\"") |> ignore
            sb.AppendLine("    volumes:") |> ignore
            sb.AppendLine("      - ./init-scripts/init_postgres.sql:/docker-entrypoint-initdb.d/init.sql") |> ignore
            sb.AppendLine("    healthcheck:") |> ignore
            sb.AppendLine("      test: [\"CMD-SHELL\", \"pg_isready -U postgres\"]") |> ignore
            sb.AppendLine("      interval: 5s") |> ignore
            sb.AppendLine("      timeout: 5s") |> ignore
            sb.AppendLine("      retries: 5") |> ignore
            sb.AppendLine() |> ignore

        if hasSqlServer then
            sb.AppendLine("  fsqs-sqlserver:") |> ignore
            sb.AppendLine("    image: mcr.microsoft.com/mssql/server:2022-latest") |> ignore
            sb.AppendLine("    container_name: fsqs-sqlserver") |> ignore
            sb.AppendLine("    restart: always") |> ignore
            sb.AppendLine("    environment:") |> ignore
            sb.AppendLine("      ACCEPT_EULA: \"Y\"") |> ignore
            sb.AppendLine("      MSSQL_SA_PASSWORD: \"Password123!\"") |> ignore
            sb.AppendLine("    ports:") |> ignore
            sb.AppendLine("      - \"1433:1433\"") |> ignore
            sb.AppendLine("    volumes:") |> ignore
            sb.AppendLine("      - ./init-scripts/init_sqlserver.sql:/init.sql") |> ignore
            sb.AppendLine("    command: >") |> ignore
            sb.AppendLine("      /bin/bash -c \"") |> ignore
            sb.AppendLine("      /opt/mssql/bin/sqlservr &") |> ignore
            sb.AppendLine("      echo 'Waiting for SQL Server to boot...' &&") |> ignore
            sb.AppendLine("      sleep 15 &&") |> ignore
            sb.AppendLine("      /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'Password123!' -C -i /init.sql &&") |> ignore
            sb.AppendLine("      echo 'SQL Server init script executed successfully.' &&") |> ignore
            sb.AppendLine("      wait") |> ignore
            sb.AppendLine("      \"") |> ignore
            sb.AppendLine() |> ignore

        sb.ToString()

    let getPostgresNotebook () : string =
        let cells =
            [
                mkMarkdown """
# 🐘 PostgreSQL Advanced FSQL Showcase & Diagnostics

This notebook demonstrates the full power of **FSQL** pushed down to **PostgreSQL**:
- **Analytical Window Functions** (`OVER (PARTITION BY ... ORDER BY ...)`, `rowNumber`, `rank`, `denseRank`, running totals)
- **Positional Window Navigation** (`lag`, `lead`)
- **Semi-Structured JSON Navigation** (`fsql.jsonValue` -> `specs ->> 'brand'`)
- **Correlated Subqueries** (`whereExists`, `whereNotExists`, scalar subqueries in projection)
- **Group Aggregations with Filter** (`groupBy` + `having`)
- **Common Table Expressions** (`withCte` / `WITH`)
- **Set Operations** (`union`, `unionAll`, `intersect`, `except`)
- **Relational Outer Joins** (`leftOuterJoin`)
- **Correlated LATERAL Apply** (`lateral (fsql { ... })`)
- **Conditional Expressions** (`if ... then ... else ...` -> `CASE WHEN`)
- **Live Query Plan Diagnostics** (`fsql.explain`)

---
### 🛠️ Container Prerequisites
Ensure your PostgreSQL container is running:
```bash
docker compose -f docker-compose.fsqs.yml up -d fsqs-postgres
```
"""
                mkMarkdown "## 1. ⚡ Analytic Window Functions (Partitioning & Running Aggregates)"
                mkCode """
// 1. Ranking products per category by price with running totals in PostgreSQL
fsql {
    for p in Connections.sql.PostgreSQL_Advanced.products do
    select {|
        Category = p.category_id
        Product = p.name
        Price = p.price
        RowInCat = fsql.rowNumber(fsql.window(partitionBy = p.category_id, sortByDescending = p.price))
        RankInCat = fsql.rank(fsql.window(partitionBy = p.category_id, sortByDescending = p.price))
        DenseRankInCat = fsql.denseRank(fsql.window(partitionBy = p.category_id, sortByDescending = p.price))
        PriceQuartile = fsql.ntile(4, fsql.window(sortByDescending = p.price))
        RunningSum = fsql.sum(p.price, over = fsql.window(partitionBy = p.category_id, sortBy = p.id))
    |}
}
"""
                mkMarkdown "## 2. 🧭 Positional Window Navigation (`lag` & `lead`)"
                mkCode """
// 2. Comparing order values with previous and subsequent orders using lag and lead
fsql {
    for o in Connections.sql.PostgreSQL_Advanced.orders do
    select {|
        OrderId = o.id
        CustomerId = o.customer_id
        Amount = o.total_amount
        PreviousAmount = fsql.lag(o.total_amount, over = fsql.window(partitionBy = o.customer_id, sortBy = o.order_date))
        NextAmount = fsql.lead(o.total_amount, over = fsql.window(partitionBy = o.customer_id, sortBy = o.order_date))
    |}
}
"""
                mkMarkdown "## 3. 📦 Semi-Structured JSON Extraction (`fsql.jsonValue` -> `specs ->> ...`)"
                mkCode """
// 3. Filtering and extracting specs JSONB fields directly on PostgreSQL
fsql {
    for p in Connections.sql.PostgreSQL_Advanced.products do
    where (fsql.jsonValue(p.specs, "brand") = "VisionMax" || fsql.jsonValue(p.specs, "wireless") = "true")
    select {|
        Sku = p.sku
        Name = p.name
        Brand = fsql.jsonValue(p.specs, "brand")
        Wireless = fsql.jsonValue(p.specs, "wireless")
    |}
}
"""
                mkMarkdown "## 4. 🎯 Correlated Subqueries (`whereExists`, `whereNotExists` & Scalar Count)"
                mkCode """
// 4.1 Customers who placed completed orders, projecting correlated order count
fsql {
    for c in Connections.sql.PostgreSQL_Advanced.customers do
    whereExists (fsql { for o in Connections.sql.PostgreSQL_Advanced.orders do where (o.customer_id = c.id && o.status = "Completed") })
    select {|
        Customer = c.first_name + " " + c.last_name
        Email = c.email
        CompletedOrders = fsql { for o in Connections.sql.PostgreSQL_Advanced.orders do where (o.customer_id = c.id && o.status = "Completed"); count }
    |}
}
"""
                mkCode """
// 4.2 Customers with NO pending orders using whereNotExists
fsql {
    for c in Connections.sql.PostgreSQL_Advanced.customers do
    whereNotExists (fsql { for o in Connections.sql.PostgreSQL_Advanced.orders do where (o.customer_id = c.id && o.status = "Pending") })
    select {|
        Customer = c.first_name + " " + c.last_name
        City = c.city
        Country = c.country
    |}
}
"""
                mkMarkdown "## 5. 📊 Grouping & Aggregate Filter (`having`)"
                mkCode """
// 5. Group orders by customer and filter customers with more than 1 order
fsql {
    for o in Connections.sql.PostgreSQL_Advanced.orders do
    groupBy o.customer_id into g
    having (fsql { for item in g do count } >= 2)
    select {|
        CustomerId = g.Key
        OrderCount = fsql { for item in g do count }
        TotalSpent = g.Sum(fun x -> x.total_amount)
    |}
}
"""
                mkMarkdown "## 6. 🧩 Common Table Expressions (CTEs) & Set Operations (`union`, `intersect`, `except`)"
                mkCode """
// 6.1 CTE combined with union directly inside fsql CE
let combinedOrders =
    fsql {
        for o in Connections.sql.PostgreSQL_Advanced.orders do
        withCte "HighValueOrders" (fsql {
            for o2 in Connections.sql.PostgreSQL_Advanced.orders do
            where (o2.total_amount >= 300.0M)
            select o2
        })
        where (o.total_amount >= 300.0M)
        select o
        union (fsql {
            for o3 in Connections.sql.PostgreSQL_Advanced.orders do
            where (o3.total_amount < 150.0M)
            select o3
        })
    }

printfn "Formatted SQL (UNION):\n%s" (toRawQuery combinedOrders)
combinedOrders
"""
                mkCode """
// 6.2 Set Operations: intersect and except directly inside fsql CE
let filteredProducts =
    fsql {
        for p in Connections.sql.PostgreSQL_Advanced.products do
        where (p.price >= 50.0M)
        select p
        except (fsql {
            for p2 in Connections.sql.PostgreSQL_Advanced.products do
            where (p2.price > 400.0M)
            select p2
        })
    }

printfn "Formatted SQL (EXCEPT):\n%s" (toRawQuery filteredProducts)
filteredProducts
"""
                mkMarkdown "## 7. 🔗 Relational Outer Joins (`leftOuterJoin`)"
                mkCode """
// 7. Left outer join of all customers and their orders (including customers without orders)
fsql {
    for c in Connections.sql.PostgreSQL_Advanced.customers do
    leftOuterJoin o in Connections.sql.PostgreSQL_Advanced.orders on (c.id = o.customer_id) into orders
    for o in orders.DefaultIfEmpty() do
    select {|
        Customer = c.first_name + " " + c.last_name
        City = c.city
        OrderId = (if isNull (box o) then 0 else o.id)
        TotalAmount = (if isNull (box o) then 0.0M else o.total_amount)
        Status = (if isNull (box o) then "No Orders" else o.status)
    |}
}
"""
                mkMarkdown "## 8. ⚡ Correlated LATERAL Apply"
                mkCode """
// 8. Correlated LATERAL join: Top order per customer
fsql {
    for c in Connections.sql.PostgreSQL_Advanced.customers do
    for topOrder in (lateral (fsql {
        for o in Connections.sql.PostgreSQL_Advanced.orders do
        where (o.customer_id = c.id)
        sortByDescending o.total_amount
        take 1
        select o
    })) do
    select {|
        Customer = c.first_name + " " + c.last_name
        TopOrderId = topOrder.id
        TopOrderAmount = topOrder.total_amount
    |}
}
"""
                mkMarkdown "## 9. 🔀 Conditional Expressions (CASE WHEN) & String Functions"
                mkCode """
// 9. Inline conditional classification (CASE WHEN) and string transformations
fsql {
    for p in Connections.sql.PostgreSQL_Advanced.products do
    select {|
        ProductUpper = p.name.ToUpper()
        Price = p.price
        Tier = (if p.price >= 300.0M then "Premium" elif p.price >= 100.0M then "Mid-Range" else "Budget")
        StockStatus = (if p.stock_quantity >= 100 then "In Stock" else "Low Stock")
    |}
}
"""
                mkMarkdown "## 10. 🔍 Live PostgreSQL Query Plan Diagnostics (`fsql.explain`)"
                mkCode """
// 10. Execute live EXPLAIN directly against the PostgreSQL container using fsql
let complexQuery =
    fsql {
        for o in Connections.sql.PostgreSQL_Advanced.orders do
        join c in Connections.sql.PostgreSQL_Advanced.customers on (o.customer_id = c.id)
        where (o.total_amount > 100.0M)
        select {|
            OrderId = o.id
            Customer = c.first_name + " " + c.last_name
            Amount = o.total_amount
        |}
    }

let report = fsql.explain complexQuery
printfn "Engine: %A" report.Dialect
printfn "%s" report.PlanText
"""
            ]
        serializeNotebook cells

    let getSqlServerNotebook () : string =
        let cells =
            [
                mkMarkdown """
# 🛡️ SQL Server (T-SQL) Advanced FSQL Showcase & Diagnostics

This notebook demonstrates the full power of **FSQL** pushed down to **SQL Server 2022**:
- **Analytical Window Functions** (`ROW_NUMBER`, `RANK`, `DENSE_RANK`, `LEAD`, `LAG`, running aggregates)
- **T-SQL 412 Mitigation** (Safe auto-injected `ORDER BY (SELECT NULL)`)
- **Positional Window Navigation** (`lag`, `lead`)
- **Semi-Structured JSON Navigation** (`fsql.jsonValue` -> `JSON_VALUE`)
- **Correlated Subqueries** (`whereExists`, `whereNotExists`, scalar subqueries in projection)
- **Group Aggregations with Filter** (`groupBy` + `having`)
- **Common Table Expressions** (`withCte` / `WITH`)
- **Set Operations** (`union`, `unionAll`, `intersect`, `except`)
- **Relational Outer Joins** (`leftOuterJoin`)
- **Correlated CROSS APPLY** (`crossApply (fsql { ... })`)
- **Conditional Expressions** (`if ... then ... else ...` -> `CASE WHEN`)
- **Live Query Plan Diagnostics** (`SET SHOWPLAN_TEXT ON ... OFF` via `fsql.explain`)

---
### 🛠️ Container Prerequisites
Ensure your SQL Server container is running:
```bash
docker compose -f docker-compose.fsqs.yml up -d fsqs-sqlserver
```
"""
                mkMarkdown "## 1. ⚡ Analytic Window Functions (Partitioning & Running Aggregates)"
                mkCode """
// 1. Ranking products per category by price with running totals in SQL Server
fsql {
    for p in Connections.sql.SqlServer_Advanced.Products do
    select {|
        CategoryId = p.CategoryId
        Product = p.Name
        Price = p.Price
        RowInCat = fsql.rowNumber(fsql.window(partitionBy = p.CategoryId, sortByDescending = p.Price))
        RankInCat = fsql.rank(fsql.window(partitionBy = p.CategoryId, sortByDescending = p.Price))
        DenseRankInCat = fsql.denseRank(fsql.window(partitionBy = p.CategoryId, sortByDescending = p.Price))
        PriceQuartile = fsql.ntile(4, fsql.window(sortByDescending = p.Price))
        RunningSum = fsql.sum(p.Price, over = fsql.window(partitionBy = p.CategoryId, sortBy = p.Id))
    |}
}
"""
                mkMarkdown "## 2. 🧭 Positional Window Navigation (`lag` & `lead`)"
                mkCode """
// 2. Comparing order amounts with previous and next transactions
fsql {
    for o in Connections.sql.SqlServer_Advanced.Orders do
    select {|
        OrderId = o.Id
        CustomerId = o.CustomerId
        Amount = o.TotalAmount
        PreviousAmount = fsql.lag(o.TotalAmount, over = fsql.window(partitionBy = o.CustomerId, sortBy = o.OrderDate))
        NextAmount = fsql.lead(o.TotalAmount, over = fsql.window(partitionBy = o.CustomerId, sortBy = o.OrderDate))
    |}
}
"""
                mkMarkdown "## 3. 📦 Semi-Structured JSON Extraction (`fsql.jsonValue` -> `JSON_VALUE`)"
                mkCode """
// 3. Filtering and extracting specs JSON fields directly on SQL Server
fsql {
    for p in Connections.sql.SqlServer_Advanced.Products do
    where (fsql.jsonValue(p.Specs, "wireless") = "true" || fsql.jsonValue(p.Specs, "brand") = "KeyMaster")
    select {|
        Sku = p.Sku
        Name = p.Name
        Brand = fsql.jsonValue(p.Specs, "brand")
        Wireless = fsql.jsonValue(p.Specs, "wireless")
    |}
}
"""
                mkMarkdown "## 4. 🎯 Correlated Subqueries (`whereExists`, `whereNotExists` & Scalar Count)"
                mkCode """
// 4.1 Customers who placed high-value orders, projecting correlated order count
fsql {
    for c in Connections.sql.SqlServer_Advanced.Customers do
    whereExists (fsql { for o in Connections.sql.SqlServer_Advanced.Orders do where (o.CustomerId = c.Id && o.TotalAmount >= 200.0M) })
    select {|
        Customer = c.FirstName + " " + c.LastName
        Email = c.Email
        TotalOrders = fsql { for o in Connections.sql.SqlServer_Advanced.Orders do where (o.CustomerId = c.Id); count }
    |}
}
"""
                mkCode """
// 4.2 Customers with NO pending orders using whereNotExists
fsql {
    for c in Connections.sql.SqlServer_Advanced.Customers do
    whereNotExists (fsql { for o in Connections.sql.SqlServer_Advanced.Orders do where (o.CustomerId = c.Id && o.Status = "Pending") })
    select {|
        Customer = c.FirstName + " " + c.LastName
        City = c.City
        Country = c.Country
    |}
}
"""
                mkMarkdown "## 5. 📊 Grouping & Aggregate Filter (`having`)"
                mkCode """
// 5. Group orders by customer and filter customers with more than 1 order
fsql {
    for o in Connections.sql.SqlServer_Advanced.Orders do
    groupBy o.CustomerId into g
    having (fsql { for item in g do count } >= 2)
    select {|
        CustomerId = g.Key
        OrderCount = fsql { for item in g do count }
        TotalSpent = g.Sum(fun x -> x.TotalAmount)
    |}
}
"""
                mkMarkdown "## 6. 🧩 Common Table Expressions (CTEs) & Set Operations (`union`, `intersect`, `except`)"
                mkCode """
// 6.1 CTE combined with union directly inside fsql CE
let combinedCatalog =
    fsql {
        for p in Connections.sql.SqlServer_Advanced.Products do
        withCte "HighStock" (fsql {
            for p2 in Connections.sql.SqlServer_Advanced.Products do
            where (p2.StockQuantity >= 100)
            select p2
        })
        where (p.StockQuantity >= 100)
        select p
        union (fsql {
            for p3 in Connections.sql.SqlServer_Advanced.Products do
            where (p3.StockQuantity < 50)
            select p3
        })
    }

printfn "Formatted SQL (UNION):\n%s" (toRawQuery combinedCatalog)
combinedCatalog
"""
                mkCode """
// 6.2 Set Operations: intersect and except directly inside fsql CE
let filteredProducts =
    fsql {
        for p in Connections.sql.SqlServer_Advanced.Products do
        where (p.Price >= 50.0M)
        select p
        except (fsql {
            for p2 in Connections.sql.SqlServer_Advanced.Products do
            where (p2.Price > 400.0M)
            select p2
        })
    }

printfn "Formatted SQL (EXCEPT):\n%s" (toRawQuery filteredProducts)
filteredProducts
"""
                mkMarkdown "## 7. 🔗 Relational Outer Joins (`leftOuterJoin`)"
                mkCode """
// 7. Left outer join of all customers and their orders (including customers without orders)
fsql {
    for c in Connections.sql.SqlServer_Advanced.Customers do
    leftOuterJoin o in Connections.sql.SqlServer_Advanced.Orders on (c.Id = o.CustomerId) into orders
    for o in orders.DefaultIfEmpty() do
    select {|
        Customer = c.FirstName + " " + c.LastName
        City = c.City
        OrderId = (if isNull (box o) then 0 else o.Id)
        TotalAmount = (if isNull (box o) then 0.0M else o.TotalAmount)
        Status = (if isNull (box o) then "No Orders" else o.Status)
    |}
}
"""
                mkMarkdown "## 8. ⚡ Correlated CROSS APPLY"
                mkCode """
// 8. Correlated CROSS APPLY join: Top order per customer
fsql {
    for c in Connections.sql.SqlServer_Advanced.Customers do
    for topOrder in (crossApply (fsql {
        for o in Connections.sql.SqlServer_Advanced.Orders do
        where (o.CustomerId = c.Id)
        sortByDescending o.TotalAmount
        take 1
        select o
    })) do
    select {|
        Customer = c.FirstName + " " + c.LastName
        TopOrderId = topOrder.Id
        TopOrderAmount = topOrder.TotalAmount
    |}
}
"""
                mkMarkdown "## 9. 🔀 Conditional Expressions (CASE WHEN) & String Functions"
                mkCode """
// 9. Inline conditional classification (CASE WHEN) and string transformations
fsql {
    for p in Connections.sql.SqlServer_Advanced.Products do
    select {|
        ProductUpper = p.Name.ToUpper()
        Price = p.Price
        Tier = (if p.Price >= 300.0M then "Premium" elif p.Price >= 100.0M then "Mid-Range" else "Budget")
        StockStatus = (if p.StockQuantity >= 100 then "In Stock" else "Low Stock")
    |}
}
"""
                mkMarkdown "## 10. 🔍 Live SQL Server Execution Plan (`SHOWPLAN_TEXT` Diagnostics)"
                mkCode """
// 10. Inspect the execution plan returned directly by SQL Server engine using fsql
let complexQuery =
    fsql {
        for o in Connections.sql.SqlServer_Advanced.Orders do
        join c in Connections.sql.SqlServer_Advanced.Customers on (o.CustomerId = c.Id)
        where (o.TotalAmount >= 100.0M)
        select {|
            OrderId = o.Id
            Customer = c.FirstName + " " + c.LastName
            Amount = o.TotalAmount
        |}
    }

let diag = fsql.explain complexQuery
printfn "Engine: %A" diag.Dialect
printfn "%s" diag.PlanText
"""
            ]
        serializeNotebook cells

    let getSqliteNotebook () : string =
        let cells =
            [
                mkMarkdown """
# 🪶 SQLite Advanced FSQL Showcase & Diagnostics

This notebook demonstrates the cutting-edge **FSQL** capabilities running locally on **SQLite 3.25+**:
- **Analytical Window Functions** (`OVER (PARTITION BY ... ORDER BY ...)`, `rowNumber`, `rank`, `denseRank`, running totals)
- **Positional Window Navigation** (`lag`, `lead`)
- **Semi-Structured JSON Navigation** (`fsql.jsonValue` -> `json_extract(Specs, '$.brand')`)
- **Correlated Subqueries** (`whereExists`, `whereNotExists`, scalar subqueries in projection)
- **Group Aggregations with Filter** (`groupBy` + `having`)
- **Common Table Expressions** (`withCte` / `WITH`)
- **Set Operations** (`union`, `unionAll`, `intersect`, `except`)
- **Relational Outer Joins** (`leftOuterJoin`)
- **Conditional Expressions** (`if ... then ... else ...` -> `CASE WHEN`)
- **Live Query Plan Diagnostics** (`EXPLAIN QUERY PLAN` via `fsql.explain`)
"""
                mkMarkdown "## 1. ⚡ Analytic Window Functions (Partitioning & Running Aggregates)"
                mkCode """
// 1. Ranking products per category by price with running totals in SQLite
fsql {
    for p in Connections.sql.SQLite_Advanced.Products do
    select {|
        CategoryId = p.CategoryId
        Product = p.Name
        Price = p.Price
        RowInCat = fsql.rowNumber(fsql.window(partitionBy = p.CategoryId, sortByDescending = p.Price))
        RankInCat = fsql.rank(fsql.window(partitionBy = p.CategoryId, sortByDescending = p.Price))
        DenseRankInCat = fsql.denseRank(fsql.window(partitionBy = p.CategoryId, sortByDescending = p.Price))
        PriceQuartile = fsql.ntile(4, fsql.window(sortByDescending = p.Price))
        RunningSum = fsql.sum(p.Price, over = fsql.window(partitionBy = p.CategoryId, sortBy = p.Id))
    |}
}
"""
                mkMarkdown "## 2. 🧭 Positional Window Navigation (`lag` & `lead`)"
                mkCode """
// 2. Comparing order values with previous and subsequent orders using lag and lead
fsql {
    for o in Connections.sql.SQLite_Advanced.Orders do
    select {|
        OrderId = o.Id
        CustomerId = o.CustomerId
        Amount = o.TotalAmount
        PreviousAmount = fsql.lag(o.TotalAmount, over = fsql.window(partitionBy = o.CustomerId, sortBy = o.OrderDate))
        NextAmount = fsql.lead(o.TotalAmount, over = fsql.window(partitionBy = o.CustomerId, sortBy = o.OrderDate))
    |}
}
"""
                mkMarkdown "## 3. 📦 Semi-Structured JSON Extraction (`fsql.jsonValue` -> `json_extract`)"
                mkCode """
// 3. Filtering and extracting specs JSON fields directly on SQLite
fsql {
    for p in Connections.sql.SQLite_Advanced.Products do
    where (fsql.jsonValue(p.Specs, "wireless") = "true" || fsql.jsonValue(p.Specs, "brand") = "VisionMax")
    select {|
        Sku = p.Sku
        Name = p.Name
        Brand = fsql.jsonValue(p.Specs, "brand")
        Wireless = fsql.jsonValue(p.Specs, "wireless")
    |}
}
"""
                mkMarkdown "## 4. 🎯 Correlated Subqueries (`whereExists`, `whereNotExists` & Scalar Count)"
                mkCode """
// 4.1 Customers who placed orders >= $200, projecting correlated order count
fsql {
    for c in Connections.sql.SQLite_Advanced.Customers do
    whereExists (fsql { for o in Connections.sql.SQLite_Advanced.Orders do where (o.CustomerId = c.Id && o.TotalAmount >= 200.0f) })
    select {|
        Customer = c.FirstName + " " + c.LastName
        Email = c.Email
        CompletedOrders = fsql { for o in Connections.sql.SQLite_Advanced.Orders do where (o.CustomerId = c.Id); count }
    |}
}
"""
                mkCode """
// 4.2 Customers with NO pending orders using whereNotExists
fsql {
    for c in Connections.sql.SQLite_Advanced.Customers do
    whereNotExists (fsql { for o in Connections.sql.SQLite_Advanced.Orders do where (o.CustomerId = c.Id && o.Status = "Pending") })
    select {|
        Customer = c.FirstName + " " + c.LastName
        City = c.City
        Country = c.Country
    |}
}
"""
                mkMarkdown "## 5. 📊 Grouping & Aggregate Filter (`having`)"
                mkCode """
// 5. Group orders by customer and filter customers with more than 1 order
fsql {
    for o in Connections.sql.SQLite_Advanced.Orders do
    groupBy o.CustomerId into g
    having (fsql { for item in g do count } >= 2)
    select {|
        CustomerId = g.Key
        OrderCount = fsql { for item in g do count }
        TotalSpent = g.Sum(fun x -> x.TotalAmount)
    |}
}
"""
                mkMarkdown "## 6. 🧩 Common Table Expressions (CTEs) & Set Operations (`union`, `intersect`, `except`)"
                mkCode """
// 6.1 CTE combined with union directly inside fsql CE
let combinedCatalog =
    fsql {
        for p in Connections.sql.SQLite_Advanced.Products do
        withCte "HighEnd" (fsql {
            for p2 in Connections.sql.SQLite_Advanced.Products do
            where (p2.Price >= 200.0f)
            select p2
        })
        where (p.Price >= 200.0f)
        select p
        union (fsql {
            for p3 in Connections.sql.SQLite_Advanced.Products do
            where (p3.Price < 100.0f)
            select p3
        })
    }

printfn "Formatted SQL (UNION):\n%s" (toRawQuery combinedCatalog)
combinedCatalog
"""
                mkCode """
// 6.2 Set Operations: intersect and except directly inside fsql CE
let exclusiveProducts =
    fsql {
        for p in Connections.sql.SQLite_Advanced.Products do
        where (p.StockQuantity > 40)
        select p
        except (fsql {
            for p2 in Connections.sql.SQLite_Advanced.Products do
            where (p2.Price > 300.0f)
            select p2
        })
    }

printfn "Formatted SQL (EXCEPT):\n%s" (toRawQuery exclusiveProducts)
exclusiveProducts
"""
                mkMarkdown "## 7. 🔗 Relational Outer Joins (`leftOuterJoin`)"
                mkCode """
// 7. Left Outer Join showing all customers and their orders (including customers without orders)
fsql {
    for c in Connections.sql.SQLite_Advanced.Customers do
    leftOuterJoin o in Connections.sql.SQLite_Advanced.Orders on (c.Id = o.CustomerId) into orders
    for o in orders.DefaultIfEmpty() do
    select {|
        Cliente = c.FirstName + " " + c.LastName
        Email = c.Email
        OrderId = (if isNull (box o) then 0 else o.Id)
        Valor = (if isNull (box o) then 0.0f else o.TotalAmount)
        Status = (if isNull (box o) then "Sem Pedidos" else o.Status)
    |}
}
"""
                mkMarkdown "## 8. 🔀 Conditional Expressions (CASE WHEN) & String Functions"
                mkCode """
// 8. Conditional classification (CASE WHEN) and string transformations directly in SQL pushdown
fsql {
    for p in Connections.sql.SQLite_Advanced.Products do
    select {|
        Nome = p.Name.ToUpper()
        Preco = p.Price
        FaixaPreco = (if p.Price >= 300.0f then "Premium" elif p.Price >= 100.0f then "Intermediário" else "Econômico")
        NivelEstoque = (if p.StockQuantity >= 100 then "Alto" else "Crítico")
    |}
}
"""
                mkMarkdown "## 9. 🔍 Live SQLite EXPLAIN QUERY PLAN"
                mkCode """
// 9. Live SQLite execution plan analysis using fsql
let complexQuery =
    fsql {
        for o in Connections.sql.SQLite_Advanced.Orders do
        join c in Connections.sql.SQLite_Advanced.Customers on (o.CustomerId = c.Id)
        where (o.TotalAmount > 100.0f)
        select {|
            Id = o.Id
            Customer = c.FirstName + " " + c.LastName
            Amount = o.TotalAmount
        |}
    }

let report = fsql.explain complexQuery
printfn "Engine: %A" report.Dialect
printfn "%s" report.PlanText
"""
            ]
        serializeNotebook cells

    let createAdvancedSqliteDatabase (dbPath: string) : unit =
        let dir = Path.GetDirectoryName(dbPath)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        if File.Exists dbPath then File.Delete dbPath

        use conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}")
        conn.Open()

        let createTables = """
CREATE TABLE Categories (
    Id INTEGER PRIMARY KEY,
    Name TEXT NOT NULL,
    Description TEXT,
    Code TEXT NOT NULL,
    IsActive INTEGER NOT NULL
);

CREATE TABLE Products (
    Id INTEGER PRIMARY KEY,
    CategoryId INTEGER NOT NULL,
    Name TEXT NOT NULL,
    Sku TEXT NOT NULL,
    Price REAL NOT NULL,
    CostPrice REAL NOT NULL,
    StockQuantity INTEGER NOT NULL,
    Specs TEXT NOT NULL,
    FOREIGN KEY (CategoryId) REFERENCES Categories(Id)
);

CREATE TABLE Customers (
    Id INTEGER PRIMARY KEY,
    FirstName TEXT NOT NULL,
    LastName TEXT NOT NULL,
    Email TEXT NOT NULL,
    City TEXT NOT NULL,
    Country TEXT NOT NULL,
    Profile TEXT NOT NULL
);

CREATE TABLE Orders (
    Id INTEGER PRIMARY KEY,
    CustomerId INTEGER NOT NULL,
    OrderDate TEXT NOT NULL,
    Status TEXT NOT NULL,
    TotalAmount REAL NOT NULL,
    FOREIGN KEY (CustomerId) REFERENCES Customers(Id)
);

CREATE TABLE OrderItems (
    Id INTEGER PRIMARY KEY,
    OrderId INTEGER NOT NULL,
    ProductId INTEGER NOT NULL,
    Quantity INTEGER NOT NULL,
    UnitPrice REAL NOT NULL,
    LineTotal REAL NOT NULL,
    FOREIGN KEY (OrderId) REFERENCES Orders(Id),
    FOREIGN KEY (ProductId) REFERENCES Products(Id)
);

INSERT INTO Categories (Id, Name, Description, Code, IsActive) VALUES
(1, 'Electronics', 'Gadgets and computing devices', 'ELEC', 1),
(2, 'Furniture', 'Office and home furniture', 'FURN', 1),
(3, 'Stationery', 'Office supplies and writing gear', 'STAT', 1);

INSERT INTO Products (Id, CategoryId, Name, Sku, Price, CostPrice, StockQuantity, Specs) VALUES
(1, 1, 'Pro Mechanical Keyboard', 'ELEC-KB-01', 129.99, 65.00, 150, '{"brand": "KeyMaster", "wireless": true, "switch": "Cherry MX Brown"}'),
(2, 1, 'UltraWide Gaming Monitor', 'ELEC-MN-02', 499.99, 310.00, 45, '{"brand": "VisionMax", "resolution": "3440x1440", "refresh_hz": 144}'),
(3, 1, 'Ergonomic Precision Mouse', 'ELEC-MS-03', 79.50, 32.00, 200, '{"brand": "PrecisionTech", "wireless": true, "dpi": 16000}'),
(4, 2, 'Adjustable Standing Desk', 'FURN-DK-01', 349.00, 180.00, 30, '{"brand": "ErgoLift", "motor": "Dual", "max_kg": 120}'),
(5, 2, 'Mesh Ergonomic Task Chair', 'FURN-CH-02', 249.99, 120.00, 60, '{"brand": "AeroComfort", "armrest": "4D", "tilt": true}'),
(6, 3, 'Smart Digital Notebook', 'STAT-NB-01', 35.00, 14.00, 300, '{"brand": "PaperSync", "pages": 120, "cloud_sync": true}');

INSERT INTO Customers (Id, FirstName, LastName, Email, City, Country, Profile) VALUES
(1, 'Alice', 'Silva', 'alice.silva@example.com', 'Sao Paulo', 'Brazil', '{"tier": "Gold", "loyalty_points": 1450, "preferred_channel": "App"}'),
(2, 'Bob', 'Miller', 'bob.miller@example.com', 'Austin', 'USA', '{"tier": "Platinum", "loyalty_points": 3200, "preferred_channel": "Web"}'),
(3, 'Carla', 'Santos', 'carla.santos@example.com', 'Curitiba', 'Brazil', '{"tier": "Silver", "loyalty_points": 650, "preferred_channel": "Email"}'),
(4, 'David', 'Kowalski', 'david.k@example.com', 'Warsaw', 'Poland', '{"tier": "Bronze", "loyalty_points": 210, "preferred_channel": "Web"}');

INSERT INTO Orders (Id, CustomerId, OrderDate, Status, TotalAmount) VALUES
(1, 1, '2026-01-15 10:30:00', 'Completed', 629.98),
(2, 1, '2026-02-10 14:15:00', 'Completed', 129.99),
(3, 2, '2026-02-20 18:45:00', 'Completed', 598.99),
(4, 3, '2026-03-01 09:00:00', 'Pending', 79.50);

INSERT INTO OrderItems (Id, OrderId, ProductId, Quantity, UnitPrice, LineTotal) VALUES
(1, 1, 1, 1, 129.99, 129.99),
(2, 1, 2, 1, 499.99, 499.99),
(3, 2, 1, 1, 129.99, 129.99),
(4, 3, 2, 1, 499.99, 499.99),
(5, 3, 3, 1, 79.50, 79.50),
(6, 4, 3, 1, 79.50, 79.50);
"""
        use cmd = conn.CreateCommand()
        cmd.CommandText <- createTables
        cmd.ExecuteNonQuery() |> ignore

    let generateAdvancedSamples (targetDir: string) (selectedEngines: string list) : AdvancedSampleResult =
        let normalizedEngines =
            selectedEngines
            |> List.map (fun e -> e.ToLowerInvariant().Trim())
            |> List.filter (fun e -> e = "sqlite" || e = "postgresql" || e = "sqlserver" || e = "postgres" || e = "mssql")
            |> List.distinct

        let hasPostgres = normalizedEngines |> List.exists (fun e -> e = "postgresql" || e = "postgres")
        let hasSqlServer = normalizedEngines |> List.exists (fun e -> e = "sqlserver" || e = "mssql")
        let hasSqlite = normalizedEngines |> List.exists (fun e -> e = "sqlite")

        let mutable initScripts = []
        let mutable dockerComposePath = None
        let mutable notebooksGenerated = []
        let mutable registeredConns = []

        let initScriptsDir = Path.Combine(targetDir, "init-scripts")
        if (hasPostgres || hasSqlServer) && not (Directory.Exists initScriptsDir) then
            Directory.CreateDirectory initScriptsDir |> ignore

        if hasPostgres then
            let pgScriptPath = Path.Combine(initScriptsDir, "init_postgres.sql")
            File.WriteAllText(pgScriptPath, getPostgresInitSql ())
            initScripts <- pgScriptPath :: initScripts

            let nbPath = Path.Combine(targetDir, "sample_postgresql.fsqnb")
            File.WriteAllText(nbPath, getPostgresNotebook ())
            notebooksGenerated <- nbPath :: notebooksGenerated
            registeredConns <- "PostgreSQL_Advanced" :: registeredConns

        if hasSqlServer then
            let sqlServerScriptPath = Path.Combine(initScriptsDir, "init_sqlserver.sql")
            File.WriteAllText(sqlServerScriptPath, getSqlServerInitSql ())
            initScripts <- sqlServerScriptPath :: initScripts

            let nbPath = Path.Combine(targetDir, "sample_sqlserver.fsqnb")
            File.WriteAllText(nbPath, getSqlServerNotebook ())
            notebooksGenerated <- nbPath :: notebooksGenerated
            registeredConns <- "SqlServer_Advanced" :: registeredConns

        if hasPostgres || hasSqlServer then
            let composePath = Path.Combine(targetDir, "docker-compose.fsqs.yml")
            File.WriteAllText(composePath, getDockerComposeYaml hasPostgres hasSqlServer)
            dockerComposePath <- Some composePath

        if hasSqlite then
            let sampleDir = Path.Combine(targetDir, "data_sources")
            if not (Directory.Exists sampleDir) then
                Directory.CreateDirectory sampleDir |> ignore
            let dbPath = Path.Combine(sampleDir, "sqlite_advanced.db")
            createAdvancedSqliteDatabase dbPath

            let nbPath = Path.Combine(targetDir, "sample_sqlite.fsqnb")
            File.WriteAllText(nbPath, getSqliteNotebook ())
            notebooksGenerated <- nbPath :: notebooksGenerated
            registeredConns <- "SQLite_Advanced" :: registeredConns

        let nbDemoPath = Path.Combine(targetDir, "sample_demo.fsqnb")
        let nbDemoPtPath = Path.Combine(targetDir, "sample_demo_pt.fsqnb")
        SampleDataGenerator.createSampleDemoNotebook nbDemoPath
        SampleDataGenerator.createSampleDemoNotebookPt nbDemoPtPath
        notebooksGenerated <- nbDemoPath :: nbDemoPtPath :: notebooksGenerated

        { TargetDir = targetDir
          SelectedEngines = normalizedEngines
          DockerComposePath = dockerComposePath
          InitScripts = List.rev initScripts
          NotebooksGenerated = List.rev notebooksGenerated
          RegisteredConnections = List.rev registeredConns }
