namespace FSharpQueryStudio.Connectors

open System
open System.IO
open System.Text.Encodings.Web
open System.Text.Json
open Microsoft.Data.Sqlite
open FSharpQueryStudio.Protocol

module SampleDataGenerator =

    type SampleFilesResult =
        { SqlitePath: string
          Sqlite2Path: string
          CsvPath: string
          Csv2Path: string
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

Access strongly-typed entities using `(Connections.connect "SampleStore").asEntities`. Notice how `c.Country`, `c.FirstName`, and `c.Email` have complete IntelliSense autocomplete, hover documentation, and type safety!

> 💡 **Formatting Tip**: Projecting into anonymous records (`{| ... |}`) generates clean, labeled column headers in the interactive table grid.
"""

                mkCode """
// 1. Connect to SQLite (SampleStore) and query customers from Brazil
let store = (Connections.connect "SampleStore").asEntities

query {
    for c in store.Customers do
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

Join tables cleanly and naturally using standard F# LINQ `join ... on (...)` syntax.
"""

                mkCode """
// 2. Join Orders and Customers to view transaction details with customer info
query {
    for o in store.Orders do
    join c in store.Customers on (o.CustomerId = c.Id)
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
Here we query the second database `InventoryStore`.
"""

                mkCode """
// 3. Connect to the second SQLite database (InventoryStore)
let inventory = (Connections.connect "InventoryStore").asEntities

query {
    for w in inventory.Warehouses do
    join s in inventory.StockLevels on (w.Id = s.WarehouseId)
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
let orders = store.Orders |> Seq.toList

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
"""

                mkCode """
// 5. Connect to the first CSV feed (SalesFeed) and filter electronics purchases
let salesFeed = (Connections.connectCsv "SalesFeed").SalesFeed

query {
    for row in salesFeed.Rows do
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

A second CSV source is registered alongside `SalesFeed`. Both CSV sources generate independent, unique strongly-typed models without collision.
"""

                mkCode """
// 6. Connect to the second CSV feed (CustomersFeed)
let loyaltyFeed = (Connections.connectCsv "CustomersFeed").CustomersFeed

query {
    for row in loyaltyFeed.Rows do
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
let salesRows = salesFeed.Rows |> Seq.toList
let loyaltyRows = loyaltyFeed.Rows |> Seq.toList

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
## 8. 🛡️ Safe Parameterized SQL (`SampleStore`)

For complex database-specific queries, use `Connections.sql` with parameterized inputs to guard against SQL injection while maintaining raw database performance.
"""

                mkCode """
// 8. Parameterized SQL query joining Products and Categories
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

Connections.sql "SampleStore" catalogSql [ "minPrice", box 100.0 ]
"""

                mkMarkdown """
## 9. 🌐 Cross-Database & Cross-Kind Overview: Multi-Source Pipeline

Because F# Query Studio executes queries in an integrated F# Interactive session, you can aggregate metadata and counts across all 4 sources simultaneously.
"""

                mkCode """
// 9. Comprehensive summary across all 4 registered data sources
let sampleStoreOrders = store.Orders |> Seq.length
let inventoryWarehouses = inventory.Warehouses |> Seq.length
let salesCount = salesFeed.Rows |> Seq.length
let loyaltyCount = loyaltyFeed.Rows |> Seq.length

[
    {| Source = "SampleStore";    Kind = "SQLite (Relational)"; RecordCount = sampleStoreOrders; PrimaryEntity = "Orders" |}
    {| Source = "InventoryStore"; Kind = "SQLite (Relational)"; RecordCount = inventoryWarehouses; PrimaryEntity = "Warehouses" |}
    {| Source = "SalesFeed";      Kind = "CSV (Flat-File)";     RecordCount = salesCount; PrimaryEntity = "Transactions" |}
    {| Source = "CustomersFeed";  Kind = "CSV (Flat-File)";     RecordCount = loyaltyCount; PrimaryEntity = "Loyalty Profiles" |}
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

Ao chamar `(Connections.connect "SampleStore").asEntities`, o **F# Query Studio** fornece suporte a autocompletar completo com IntelliSense e tipos fortemente tipados gerados em segundo plano.

> 💡 **Dica de Formatação**: Retornar registros anônimos (`{| ... |}`) produz cabeçalhos limpos com nomes descritivos e tipos explícitos na tabela interativa.
"""

                mkCode """
// 1. Conecta ao banco SQLite (SampleStore) e consulta clientes do Brasil
let store = (Connections.connect "SampleStore").asEntities

query {
    for c in store.Customers do
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

Combine tabelas relacionais de forma natural utilizando a sintaxe `join ... on (...)` do F# LINQ.
"""

                mkCode """
// 2. Junção relacional entre Pedidos (Orders) e Clientes (Customers)
query {
    for o in store.Orders do
    join c in store.Customers on (o.CustomerId = c.Id)
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
Aqui conectamos e consultamos a segunda base SQLite: `InventoryStore`.
"""

                mkCode """
// 3. Conecta à segunda base SQLite (InventoryStore)
let inventory = (Connections.connect "InventoryStore").asEntities

query {
    for w in inventory.Warehouses do
    join s in inventory.StockLevels on (w.Id = s.WarehouseId)
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
let orders = store.Orders |> Seq.toList

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
"""

                mkCode """
// 5. Conecta ao primeiro arquivo CSV (SalesFeed) e filtra compras de Eletrônicos
let salesFeed = (Connections.connectCsv "SalesFeed").SalesFeed

query {
    for row in salesFeed.Rows do
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

Uma segunda fonte CSV registrada ao lado de `SalesFeed`. Ambas possuem inferência de tipos e módulos independentes sem sobrescrita.
"""

                mkCode """
// 6. Conecta ao segundo arquivo CSV (CustomersFeed)
let loyaltyFeed = (Connections.connectCsv "CustomersFeed").CustomersFeed

query {
    for row in loyaltyFeed.Rows do
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
let salesRows = salesFeed.Rows |> Seq.toList
let loyaltyRows = loyaltyFeed.Rows |> Seq.toList

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
## 8. 🛡️ Consultas SQL Parametrizadas Seguras (`SampleStore`)

Para queries complexas ou instruções SQL nativas, use `Connections.sql` com passagem segura de parâmetros nomeados para evitar injeção de SQL.
"""

                mkCode """
// 8. Query SQL nativa com parâmetros nomeados seguros
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

Connections.sql "SampleStore" sqlCatalogo [ "precoMinimo", box 100.0 ]
"""

                mkMarkdown """
## 9. 🌐 Visão Consolidada Multi-Fontes: SQLite + CSV

Como as consultas no F# Query Studio rodam em uma sessão FSI unificada, você pode cruzar e comparar dados das 4 fontes simultaneamente:
"""

                mkCode """
// 9. Resumo consolidado das 4 fontes de dados registradas
let sampleStoreOrders = store.Orders |> Seq.length
let inventoryWarehouses = inventory.Warehouses |> Seq.length
let salesCount = salesFeed.Rows |> Seq.length
let loyaltyCount = loyaltyFeed.Rows |> Seq.length

[
    {| Fonte = "SampleStore";    Tipo = "SQLite (Relacional)"; TotalRegistros = sampleStoreOrders; EntidadePrincipal = "Orders" |}
    {| Fonte = "InventoryStore"; Tipo = "SQLite (Relacional)"; TotalRegistros = inventoryWarehouses; EntidadePrincipal = "Warehouses" |}
    {| Fonte = "SalesFeed";      Tipo = "CSV (Arquivo Plano)"; TotalRegistros = salesCount; EntidadePrincipal = "Transações" |}
    {| Fonte = "CustomersFeed";  Tipo = "CSV (Arquivo Plano)"; TotalRegistros = loyaltyCount; EntidadePrincipal = "Perfis de Fidelidade" |}
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

    let createSampleCsv (filePath: string) : unit =
        let dir = Path.GetDirectoryName(filePath)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        File.WriteAllText(filePath, sampleCsvContent)

    let createSampleCustomersCsv (filePath: string) : unit =
        let dir = Path.GetDirectoryName(filePath)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        File.WriteAllText(filePath, sampleCustomersCsvContent)

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
        let sampleDir = Path.Combine(targetDir, "sample_data")
        if not (Directory.Exists sampleDir) then
            Directory.CreateDirectory sampleDir |> ignore

        let dbPath = Path.Combine(sampleDir, "sample_store.db")
        let db2Path = Path.Combine(sampleDir, "inventory.db")
        let csvPath = Path.Combine(sampleDir, "sales_feed.csv")
        let csv2Path = Path.Combine(sampleDir, "customers_feed.csv")
        let notebookPath = Path.Combine(targetDir, "sample_demo.fsqnb")
        let notebookPtPath = Path.Combine(targetDir, "sample_demo_pt.fsqnb")

        createSampleStoreDatabase dbPath
        createInventoryDatabase db2Path
        createSampleCsv csvPath
        createSampleCustomersCsv csv2Path
        createSampleDemoNotebook notebookPath
        createSampleDemoNotebookPt notebookPtPath

        { SqlitePath = dbPath
          Sqlite2Path = db2Path
          CsvPath = csvPath
          Csv2Path = csv2Path
          NotebookPath = notebookPath
          NotebookPtPath = notebookPtPath }
