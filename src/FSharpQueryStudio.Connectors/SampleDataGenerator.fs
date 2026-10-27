namespace FSharpQueryStudio.Connectors

open System
open System.IO
open System.Text.Json
open Microsoft.Data.Sqlite
open FSharpQueryStudio.Protocol

module SampleDataGenerator =

    type SampleFilesResult =
        { SqlitePath: string
          CsvPath: string
          NotebookPath: string }

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

    let private sampleDemoNotebookContent = """{
  "version": 1,
  "cells": [
    {
      "kind": "markdown",
      "language": "markdown",
      "value": "# F# Query Studio — Interactive Demo Notebook\\n\\nWelcome to **F# Query Studio**! This notebook demonstrates how to query the generated sample SQLite database (`SampleStore`) and CSV feed (`SalesFeed`) using:\\n1. **Strongly-typed LINQ queries** with `conn.asEntities`\\n2. **Joins and aggregations**\\n3. **Flat-file queries on CSV**\\n4. **Raw parameterized SQL**\\n\\nPress **Ctrl+Enter** (or click ▶) to run each cell!\\n\\n---\\n\\n### Official Documentation Links\\n- [F# Documentation (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/fsharp/)\\n- [F# Query Expressions / LINQ (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/query-expressions)\\n- [F# Interactive (FSI) Guide](https://learn.microsoft.com/en-us/dotnet/fsharp/tools/fsharp-interactive/)"
    },
    {
      "kind": "markdown",
      "language": "markdown",
      "value": "### 1. Strongly-Typed LINQ: Filtering Customers\\nNotice how `c.Country`, `c.FirstName` and `c.Email` have full autocomplete, hover tooltips, and Go to Definition (`F12`)!"
    },
    {
      "kind": "code",
      "language": "fsharp",
      "value": "// Connect to the sample database and access strongly-typed entities\\nlet store = (Connections.connect \"SampleStore\").asEntities\\n\\nquery {\\n    for c in store.Customers do\\n    where (c.Country = \"Brazil\")\\n    sortBy c.FirstName\\n    select (c.Id, c.FirstName + \" \" + c.LastName, c.Email, c.City)\\n}"
    },
    {
      "kind": "markdown",
      "language": "markdown",
      "value": "### 2. LINQ Join: Orders with Customer Details\\nJoin tables naturally using standard F# `join ... on` syntax:"
    },
    {
      "kind": "code",
      "language": "fsharp",
      "value": "query {\\n    for o in store.Orders do\\n    join c in store.Customers on (o.CustomerId = c.Id)\\n    sortByDescending o.TotalAmount\\n    select (o.Id, c.FirstName + \" \" + c.LastName, c.Country, o.TotalAmount, o.Status)\\n}"
    },
    {
      "kind": "markdown",
      "language": "markdown",
      "value": "### 3. Querying Flat Files: CSV with LINQ\\nThe sample CSV connection `SalesFeed` is also automatically mapped to strongly typed rows:"
    },
    {
      "kind": "code",
      "language": "fsharp",
      "value": "let salesFeed = (Connections.connect \"SalesFeed\").asEntities\\n\\nquery {\\n    for row in salesFeed.Rows do\\n    where (row.Category = \"Electronics\")\\n    sortByDescending row.TotalAmount\\n    select (row.Date, row.Customer, row.Product, row.TotalAmount, row.Status)\\n}"
    },
    {
      "kind": "markdown",
      "language": "markdown",
      "value": "### 4. Raw SQL with Parameter Binding\\nExecute direct SQL statements with parameterized inputs:"
    },
    {
      "kind": "code",
      "language": "fsharp",
      "value": "Connections.sql\\n    \"SampleStore\"\\n    \"SELECT p.Name AS Product, c.Name AS Category, p.Price, p.StockQuantity\\n     FROM Products p\\n     JOIN Categories c ON p.CategoryId = c.Id\\n     WHERE p.Price >= @minPrice\\n     ORDER BY p.Price DESC\"\\n    [ \"minPrice\", box 100.0 ]"
    }
  ],
  "metadata": {}
}"""

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

    let createSampleCsv (filePath: string) : unit =
        let dir = Path.GetDirectoryName(filePath)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        File.WriteAllText(filePath, sampleCsvContent)

    let createSampleDemoNotebook (filePath: string) : unit =
        let dir = Path.GetDirectoryName(filePath)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        File.WriteAllText(filePath, sampleDemoNotebookContent)

    let generateAllSamples (targetDir: string) : SampleFilesResult =
        let sampleDir = Path.Combine(targetDir, "sample_data")
        if not (Directory.Exists sampleDir) then
            Directory.CreateDirectory sampleDir |> ignore

        let dbPath = Path.Combine(sampleDir, "sample_store.db")
        let csvPath = Path.Combine(sampleDir, "sales_feed.csv")
        let notebookPath = Path.Combine(targetDir, "sample_demo.fsqnb")

        createSampleStoreDatabase dbPath
        createSampleCsv csvPath
        createSampleDemoNotebook notebookPath

        { SqlitePath = dbPath
          CsvPath = csvPath
          NotebookPath = notebookPath }
