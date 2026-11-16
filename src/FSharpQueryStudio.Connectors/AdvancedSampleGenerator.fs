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

    let getMySqlInitSql () = """
-- MySQL Advanced Schema Initialization
CREATE DATABASE IF NOT EXISTS fsqs_demo;
USE fsqs_demo;

CREATE TABLE IF NOT EXISTS categories (
    id INT PRIMARY KEY,
    name VARCHAR(100) NOT NULL,
    description TEXT,
    code VARCHAR(20) NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT true
);

CREATE TABLE IF NOT EXISTS products (
    id INT PRIMARY KEY,
    category_id INT NOT NULL,
    name VARCHAR(150) NOT NULL,
    sku VARCHAR(50) NOT NULL,
    price DECIMAL(10,2) NOT NULL,
    cost_price DECIMAL(10,2) NOT NULL,
    stock_quantity INT NOT NULL,
    specs JSON NOT NULL,
    FOREIGN KEY (category_id) REFERENCES categories(id)
);

CREATE TABLE IF NOT EXISTS customers (
    id INT PRIMARY KEY,
    first_name VARCHAR(100) NOT NULL,
    last_name VARCHAR(100) NOT NULL,
    email VARCHAR(150) NOT NULL,
    city VARCHAR(100) NOT NULL,
    country VARCHAR(100) NOT NULL,
    profile JSON NOT NULL
);

CREATE TABLE IF NOT EXISTS orders (
    id INT PRIMARY KEY,
    customer_id INT NOT NULL,
    order_date DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    status VARCHAR(50) NOT NULL,
    total_amount DECIMAL(12,2) NOT NULL,
    FOREIGN KEY (customer_id) REFERENCES customers(id)
);

CREATE TABLE IF NOT EXISTS order_items (
    id INT PRIMARY KEY,
    order_id INT NOT NULL,
    product_id INT NOT NULL,
    quantity INT NOT NULL,
    unit_price DECIMAL(10,2) NOT NULL,
    line_total DECIMAL(12,2) NOT NULL,
    FOREIGN KEY (order_id) REFERENCES orders(id),
    FOREIGN KEY (product_id) REFERENCES products(id)
);

INSERT INTO categories (id, name, description, code, is_active) VALUES
(1, 'Electronics', 'Gadgets and computing devices', 'ELEC', true),
(2, 'Furniture', 'Office and home furniture', 'FURN', true),
(3, 'Stationery', 'Office supplies and writing gear', 'STAT', true)
ON DUPLICATE KEY UPDATE name=VALUES(name);

INSERT INTO products (id, category_id, name, sku, price, cost_price, stock_quantity, specs) VALUES
(1, 1, 'Pro Mechanical Keyboard', 'ELEC-KB-01', 129.99, 65.00, 150, '{"brand": "KeyMaster", "wireless": true, "switch": "Cherry MX Brown"}'),
(2, 1, 'UltraWide Gaming Monitor', 'ELEC-MN-02', 499.99, 310.00, 45, '{"brand": "VisionMax", "resolution": "3440x1440", "refresh_hz": 144}'),
(3, 1, 'Ergonomic Precision Mouse', 'ELEC-MS-03', 79.50, 32.00, 200, '{"brand": "PrecisionTech", "wireless": true, "dpi": 16000}'),
(4, 2, 'Adjustable Standing Desk', 'FURN-DK-01', 349.00, 180.00, 30, '{"brand": "ErgoLift", "motor": "Dual", "max_kg": 120}'),
(5, 2, 'Mesh Ergonomic Task Chair', 'FURN-CH-02', 249.99, 120.00, 60, '{"brand": "AeroComfort", "armrest": "4D", "tilt": true}'),
(6, 3, 'Smart Digital Notebook', 'STAT-NB-01', 35.00, 14.00, 300, '{"brand": "PaperSync", "pages": 120, "cloud_sync": true}')
ON DUPLICATE KEY UPDATE name=VALUES(name);

INSERT INTO customers (id, first_name, last_name, email, city, country, profile) VALUES
(1, 'Alice', 'Silva', 'alice.silva@example.com', 'Sao Paulo', 'Brazil', '{"tier": "Gold", "loyalty_points": 1450, "preferred_channel": "App"}'),
(2, 'Bob', 'Miller', 'bob.miller@example.com', 'Austin', 'USA', '{"tier": "Platinum", "loyalty_points": 3200, "preferred_channel": "Web"}'),
(3, 'Carla', 'Santos', 'carla.santos@example.com', 'Curitiba', 'Brazil', '{"tier": "Silver", "loyalty_points": 650, "preferred_channel": "Email"}'),
(4, 'David', 'Kowalski', 'david.k@example.com', 'Warsaw', 'Poland', '{"tier": "Bronze", "loyalty_points": 210, "preferred_channel": "Web"}')
ON DUPLICATE KEY UPDATE email=VALUES(email);

INSERT INTO orders (id, customer_id, order_date, status, total_amount) VALUES
(1, 1, '2026-01-15 10:30:00', 'Completed', 629.98),
(2, 1, '2026-02-10 14:15:00', 'Completed', 129.99),
(3, 2, '2026-02-20 18:45:00', 'Completed', 598.99),
(4, 3, '2026-03-01 09:00:00', 'Pending', 79.50)
ON DUPLICATE KEY UPDATE status=VALUES(status);

INSERT INTO order_items (id, order_id, product_id, quantity, unit_price, line_total) VALUES
(1, 1, 1, 1, 129.99, 129.99),
(2, 1, 2, 1, 499.99, 499.99),
(3, 2, 1, 1, 129.99, 129.99),
(4, 3, 2, 1, 499.99, 499.99),
(5, 3, 3, 1, 79.50, 79.50),
(6, 4, 3, 1, 79.50, 79.50)
ON DUPLICATE KEY UPDATE quantity=VALUES(quantity);
"""

    let getMariaDbInitSql () = getMySqlInitSql ()

    let getOracleInitSql () = """
-- Oracle Advanced Schema Initialization
BEGIN
   EXECUTE IMMEDIATE 'DROP TABLE ORDER_ITEMS CASCADE CONSTRAINTS';
EXCEPTION WHEN OTHERS THEN NULL;
END;
/
BEGIN
   EXECUTE IMMEDIATE 'DROP TABLE ORDERS CASCADE CONSTRAINTS';
EXCEPTION WHEN OTHERS THEN NULL;
END;
/
BEGIN
   EXECUTE IMMEDIATE 'DROP TABLE PRODUCTS CASCADE CONSTRAINTS';
EXCEPTION WHEN OTHERS THEN NULL;
END;
/
BEGIN
   EXECUTE IMMEDIATE 'DROP TABLE CATEGORIES CASCADE CONSTRAINTS';
EXCEPTION WHEN OTHERS THEN NULL;
END;
/
BEGIN
   EXECUTE IMMEDIATE 'DROP TABLE CUSTOMERS CASCADE CONSTRAINTS';
EXCEPTION WHEN OTHERS THEN NULL;
END;
/

CREATE TABLE CATEGORIES (
    ID NUMBER(10) PRIMARY KEY,
    NAME VARCHAR2(100) NOT NULL,
    DESCRIPTION VARCHAR2(500),
    CODE VARCHAR2(20) NOT NULL,
    IS_ACTIVE NUMBER(1) DEFAULT 1 NOT NULL
);

CREATE TABLE PRODUCTS (
    ID NUMBER(10) PRIMARY KEY,
    CATEGORY_ID NUMBER(10) NOT NULL REFERENCES CATEGORIES(ID),
    NAME VARCHAR2(150) NOT NULL,
    SKU VARCHAR2(50) NOT NULL,
    PRICE NUMBER(10,2) NOT NULL,
    COST_PRICE NUMBER(10,2) NOT NULL,
    STOCK_QUANTITY NUMBER(10) NOT NULL,
    SPECS VARCHAR2(4000) CHECK (SPECS IS JSON)
);

CREATE TABLE CUSTOMERS (
    ID NUMBER(10) PRIMARY KEY,
    FIRST_NAME VARCHAR2(100) NOT NULL,
    LAST_NAME VARCHAR2(100) NOT NULL,
    EMAIL VARCHAR2(150) NOT NULL,
    CITY VARCHAR2(100) NOT NULL,
    COUNTRY VARCHAR2(100) NOT NULL,
    PROFILE VARCHAR2(4000) CHECK (PROFILE IS JSON)
);

CREATE TABLE ORDERS (
    ID NUMBER(10) PRIMARY KEY,
    CUSTOMER_ID NUMBER(10) NOT NULL REFERENCES CUSTOMERS(ID),
    ORDER_DATE TIMESTAMP DEFAULT CURRENT_TIMESTAMP NOT NULL,
    STATUS VARCHAR2(50) NOT NULL,
    TOTAL_AMOUNT NUMBER(12,2) NOT NULL
);

CREATE TABLE ORDER_ITEMS (
    ID NUMBER(10) PRIMARY KEY,
    ORDER_ID NUMBER(10) NOT NULL REFERENCES ORDERS(ID),
    PRODUCT_ID NUMBER(10) NOT NULL REFERENCES PRODUCTS(ID),
    QUANTITY NUMBER(10) NOT NULL,
    UNIT_PRICE NUMBER(10,2) NOT NULL,
    LINE_TOTAL NUMBER(12,2) NOT NULL
);

INSERT INTO CATEGORIES (ID, NAME, DESCRIPTION, CODE, IS_ACTIVE) VALUES (1, 'Electronics', 'Gadgets and computing devices', 'ELEC', 1);
INSERT INTO CATEGORIES (ID, NAME, DESCRIPTION, CODE, IS_ACTIVE) VALUES (2, 'Furniture', 'Office and home furniture', 'FURN', 1);
INSERT INTO CATEGORIES (ID, NAME, DESCRIPTION, CODE, IS_ACTIVE) VALUES (3, 'Stationery', 'Office supplies and writing gear', 'STAT', 1);

INSERT INTO PRODUCTS (ID, CATEGORY_ID, NAME, SKU, PRICE, COST_PRICE, STOCK_QUANTITY, SPECS) VALUES (1, 1, 'Pro Mechanical Keyboard', 'ELEC-KB-01', 129.99, 65.00, 150, '{"brand": "KeyMaster", "wireless": true, "switch": "Cherry MX Brown"}');
INSERT INTO PRODUCTS (ID, CATEGORY_ID, NAME, SKU, PRICE, COST_PRICE, STOCK_QUANTITY, SPECS) VALUES (2, 1, 'UltraWide Gaming Monitor', 'ELEC-MN-02', 499.99, 310.00, 45, '{"brand": "VisionMax", "resolution": "3440x1440", "refresh_hz": 144}');
INSERT INTO PRODUCTS (ID, CATEGORY_ID, NAME, SKU, PRICE, COST_PRICE, STOCK_QUANTITY, SPECS) VALUES (3, 1, 'Ergonomic Precision Mouse', 'ELEC-MS-03', 79.50, 32.00, 200, '{"brand": "PrecisionTech", "wireless": true, "dpi": 16000}');
INSERT INTO PRODUCTS (ID, CATEGORY_ID, NAME, SKU, PRICE, COST_PRICE, STOCK_QUANTITY, SPECS) VALUES (4, 2, 'Adjustable Standing Desk', 'FURN-DK-01', 349.00, 180.00, 30, '{"brand": "ErgoLift", "motor": "Dual", "max_kg": 120}');
INSERT INTO PRODUCTS (ID, CATEGORY_ID, NAME, SKU, PRICE, COST_PRICE, STOCK_QUANTITY, SPECS) VALUES (5, 2, 'Mesh Ergonomic Task Chair', 'FURN-CH-02', 249.99, 120.00, 60, '{"brand": "AeroComfort", "armrest": "4D", "tilt": true}');
INSERT INTO PRODUCTS (ID, CATEGORY_ID, NAME, SKU, PRICE, COST_PRICE, STOCK_QUANTITY, SPECS) VALUES (6, 3, 'Smart Digital Notebook', 'STAT-NB-01', 35.00, 14.00, 300, '{"brand": "PaperSync", "pages": 120, "cloud_sync": true}');

INSERT INTO CUSTOMERS (ID, FIRST_NAME, LAST_NAME, EMAIL, CITY, COUNTRY, PROFILE) VALUES (1, 'Alice', 'Silva', 'alice.silva@example.com', 'Sao Paulo', 'Brazil', '{"tier": "Gold", "loyalty_points": 1450, "preferred_channel": "App"}');
INSERT INTO CUSTOMERS (ID, FIRST_NAME, LAST_NAME, EMAIL, CITY, COUNTRY, PROFILE) VALUES (2, 'Bob', 'Miller', 'bob.miller@example.com', 'Austin', 'USA', '{"tier": "Platinum", "loyalty_points": 3200, "preferred_channel": "Web"}');
INSERT INTO CUSTOMERS (ID, FIRST_NAME, LAST_NAME, EMAIL, CITY, COUNTRY, PROFILE) VALUES (3, 'Carla', 'Santos', 'carla.santos@example.com', 'Curitiba', 'Brazil', '{"tier": "Silver", "loyalty_points": 650, "preferred_channel": "Email"}');
INSERT INTO CUSTOMERS (ID, FIRST_NAME, LAST_NAME, EMAIL, CITY, COUNTRY, PROFILE) VALUES (4, 'David', 'Kowalski', 'david.k@example.com', 'Warsaw', 'Poland', '{"tier": "Bronze", "loyalty_points": 210, "preferred_channel": "Web"}');

INSERT INTO ORDERS (ID, CUSTOMER_ID, ORDER_DATE, STATUS, TOTAL_AMOUNT) VALUES (1, 1, TIMESTAMP '2026-01-15 10:30:00', 'Completed', 629.98);
INSERT INTO ORDERS (ID, CUSTOMER_ID, ORDER_DATE, STATUS, TOTAL_AMOUNT) VALUES (2, 1, TIMESTAMP '2026-02-10 14:15:00', 'Completed', 129.99);
INSERT INTO ORDERS (ID, CUSTOMER_ID, ORDER_DATE, STATUS, TOTAL_AMOUNT) VALUES (3, 2, TIMESTAMP '2026-02-20 18:45:00', 'Completed', 598.99);
INSERT INTO ORDERS (ID, CUSTOMER_ID, ORDER_DATE, STATUS, TOTAL_AMOUNT) VALUES (4, 3, TIMESTAMP '2026-03-01 09:00:00', 'Pending', 79.50);

INSERT INTO ORDER_ITEMS (ID, ORDER_ID, PRODUCT_ID, QUANTITY, UNIT_PRICE, LINE_TOTAL) VALUES (1, 1, 1, 1, 129.99, 129.99);
INSERT INTO ORDER_ITEMS (ID, ORDER_ID, PRODUCT_ID, QUANTITY, UNIT_PRICE, LINE_TOTAL) VALUES (2, 1, 2, 1, 499.99, 499.99);
INSERT INTO ORDER_ITEMS (ID, ORDER_ID, PRODUCT_ID, QUANTITY, UNIT_PRICE, LINE_TOTAL) VALUES (3, 2, 1, 1, 129.99, 129.99);
INSERT INTO ORDER_ITEMS (ID, ORDER_ID, PRODUCT_ID, QUANTITY, UNIT_PRICE, LINE_TOTAL) VALUES (4, 3, 2, 1, 499.99, 499.99);
INSERT INTO ORDER_ITEMS (ID, ORDER_ID, PRODUCT_ID, QUANTITY, UNIT_PRICE, LINE_TOTAL) VALUES (5, 3, 3, 1, 79.50, 79.50);
INSERT INTO ORDER_ITEMS (ID, ORDER_ID, PRODUCT_ID, QUANTITY, UNIT_PRICE, LINE_TOTAL) VALUES (6, 4, 3, 1, 79.50, 79.50);

COMMIT;
"""

    let getDockerComposeYamlWithEngines (hasPostgres: bool) (hasSqlServer: bool) (hasMySql: bool) (hasMariaDb: bool) (hasOracle: bool) : string =
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

        if hasMySql then
            sb.AppendLine("  fsqs-mysql:") |> ignore
            sb.AppendLine("    image: mysql:8.4") |> ignore
            sb.AppendLine("    container_name: fsqs-mysql") |> ignore
            sb.AppendLine("    restart: always") |> ignore
            sb.AppendLine("    environment:") |> ignore
            sb.AppendLine("      MYSQL_ROOT_PASSWORD: Password123!") |> ignore
            sb.AppendLine("      MYSQL_DATABASE: fsqs_demo") |> ignore
            sb.AppendLine("    ports:") |> ignore
            sb.AppendLine("      - \"3306:3306\"") |> ignore
            sb.AppendLine("    volumes:") |> ignore
            sb.AppendLine("      - ./init-scripts/init_mysql.sql:/docker-entrypoint-initdb.d/init.sql") |> ignore
            sb.AppendLine("    healthcheck:") |> ignore
            sb.AppendLine("      test: [\"CMD\", \"mysqladmin\", \"ping\", \"-h\", \"localhost\", \"-u\", \"root\", \"-pPassword123!\"]") |> ignore
            sb.AppendLine("      interval: 5s") |> ignore
            sb.AppendLine("      timeout: 5s") |> ignore
            sb.AppendLine("      retries: 5") |> ignore
            sb.AppendLine() |> ignore

        if hasMariaDb then
            sb.AppendLine("  fsqs-mariadb:") |> ignore
            sb.AppendLine("    image: mariadb:11.4") |> ignore
            sb.AppendLine("    container_name: fsqs-mariadb") |> ignore
            sb.AppendLine("    restart: always") |> ignore
            sb.AppendLine("    environment:") |> ignore
            sb.AppendLine("      MARIADB_ROOT_PASSWORD: Password123!") |> ignore
            sb.AppendLine("      MARIADB_DATABASE: fsqs_demo") |> ignore
            sb.AppendLine("    ports:") |> ignore
            sb.AppendLine("      - \"3307:3306\"") |> ignore
            sb.AppendLine("    volumes:") |> ignore
            sb.AppendLine("      - ./init-scripts/init_mariadb.sql:/docker-entrypoint-initdb.d/init.sql") |> ignore
            sb.AppendLine("    healthcheck:") |> ignore
            sb.AppendLine("      test: [\"CMD\", \"healthcheck.sh\", \"--connect\", \"--innodb_initialized\"]") |> ignore
            sb.AppendLine("      interval: 5s") |> ignore
            sb.AppendLine("      timeout: 5s") |> ignore
            sb.AppendLine("      retries: 5") |> ignore
            sb.AppendLine() |> ignore

        if hasOracle then
            sb.AppendLine("  fsqs-oracle:") |> ignore
            sb.AppendLine("    image: gvenzl/oracle-free:23-slim") |> ignore
            sb.AppendLine("    container_name: fsqs-oracle") |> ignore
            sb.AppendLine("    restart: always") |> ignore
            sb.AppendLine("    environment:") |> ignore
            sb.AppendLine("      ORACLE_PASSWORD: Password123!") |> ignore
            sb.AppendLine("    ports:") |> ignore
            sb.AppendLine("      - \"1521:1521\"") |> ignore
            sb.AppendLine("    volumes:") |> ignore
            sb.AppendLine("      - ./init-scripts/init_oracle.sql:/container-entrypoint-initdb.d/init.sql") |> ignore
            sb.AppendLine("    healthcheck:") |> ignore
            sb.AppendLine("      test: [\"CMD\", \"healthcheck.sh\"]") |> ignore
            sb.AppendLine("      interval: 10s") |> ignore
            sb.AppendLine("      timeout: 10s") |> ignore
            sb.AppendLine("      retries: 10") |> ignore
            sb.AppendLine() |> ignore

        sb.ToString()

    let getDockerComposeYaml (hasPostgres: bool) (hasSqlServer: bool) : string =
        getDockerComposeYamlWithEngines hasPostgres hasSqlServer false false false

    let getPostgresNotebook () : string =
        let cells =
            [
                mkMarkdown """
# 🐘 PostgreSQL Advanced FSQL Showcase & Diagnostics

This notebook demonstrates the full power of **FSQL** pushed down to **PostgreSQL**:
- **Analytical Window Functions** (`OVER (PARTITION BY ... ORDER BY ...)`, `rowNumber`, `rank`, `denseRank`, running aggregates)
- **Positional & Value Window Functions** (`lag`, `lead`, `firstValue`, `lastValue`, running `avg`, `max`)
- **Semi-Structured JSON Navigation** (`fsql.jsonValue` -> `specs ->> 'brand'`)
- **Correlated Subqueries** (`whereExists`, `whereNotExists`, scalar subqueries in projection)
- **Group Aggregations with Filter** (`groupBy` + `having`)
- **Common Table Expressions** (Multi-variable composition, unnamed `withCte`, recursive `withRecursiveCte`)
- **Set Operations** (`union`, `unionAll`, `intersect`, `except`)
- **Relational Joins & Outer Joins** (`join`, `leftOuterJoin`, `fullOuterJoin`)
- **Correlated LATERAL Apply** (`lateral (fsql { ... })`)
- **Subqueries with IN** (`.Contains(...)`)
- **Conditional Expressions** (`if ... then ... else ...` -> `CASE WHEN`)
- **Query Inspection & Parameter Diagnostics** (`toRawQuery`, `toRawSqlWithParameters`)
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
        RunningAvg = fsql.avg(p.price, over = fsql.window(partitionBy = p.category_id, sortBy = p.id))
    |}
}
"""
                mkMarkdown "## 2. 🧭 Positional & Value Window Functions (`lag`, `lead`, `firstValue`, `lastValue`)"
                mkCode """
// 2.1 Comparing order values with previous and subsequent orders using lag and lead
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
                mkCode """
// 2.2 First and last values within each category window
fsql {
    for p in Connections.sql.PostgreSQL_Advanced.products do
    select {|
        Category = p.category_id
        Product = p.name
        Price = p.price
        CheapestInCat = fsql.firstValue(p.price, over = fsql.window(partitionBy = p.category_id, sortBy = p.price))
        MostExpensiveInCat = fsql.lastValue(p.price, over = fsql.window(partitionBy = p.category_id, sortBy = p.price))
        CatMaxPrice = fsql.max(p.price, over = fsql.window(partitionBy = p.category_id))
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
                mkMarkdown "## 4. 🎯 Correlated Subqueries (`whereExists`, `whereNotExists` & Scalar Projection)"
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
                mkMarkdown "## 6. 🧩 Common Table Expressions (CTEs) & Transparent Multi-Query Composition"
                mkCode """
// 6.1 Multi-variable CTE composition in parts (transparent CTE bubble-up, zero dummy tables)
let premiumProducts =
    fsql {
        for p in Connections.sql.PostgreSQL_Advanced.products do
        where (p.price >= 300.0M)
        select p
    }

let budgetProducts =
    fsql {
        for p in Connections.sql.PostgreSQL_Advanced.products do
        where (p.price <= 50.0M)
        select p
    }

// Combining two independent query variables using unionAll inside fsql
let allSpecialTiers =
    fsql {
        for p in premiumProducts do
        unionAll budgetProducts
    }

printfn "Formatted SQL (Multi-part CTE Union):\n%s" (toRawQuery allSpecialTiers)
allSpecialTiers
"""
                mkCode """
// 6.2 Transparent CTE with direct JOIN (without manual table registration)
let highValueOrders =
    fsql {
        for o in Connections.sql.PostgreSQL_Advanced.orders do
        where (o.total_amount >= 300.0M)
        select o
    }

let ordersWithCustomer =
    fsql {
        for c in Connections.sql.PostgreSQL_Advanced.customers do
        withCte highValueOrders
        join o in highValueOrders on (c.id = o.customer_id)
        select (c.first_name + " " + c.last_name, o.id, o.total_amount)
    }

printfn "Formatted SQL (CTE + JOIN):\n%s" (toRawQuery ordersWithCustomer)
ordersWithCustomer
"""
                mkCode """
// 6.3 Set Operations: intersect and except directly inside fsql CE
let midTierProducts =
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

printfn "Formatted SQL (EXCEPT):\n%s" (toRawQuery midTierProducts)
midTierProducts
"""
                mkCode """
// 6.4 Recursive CTE query (hierarchical / sequence generation)
let sequenceCte =
    fsql {
        for p in Connections.sql.PostgreSQL_Advanced.products do
        withRecursiveCteSql "NumSeries" "SELECT 1 AS n UNION ALL SELECT n + 1 FROM NumSeries WHERE n < 5"
        take 5
        select p
    }

printfn "Formatted SQL (Recursive CTE):\n%s" (toRawQuery sequenceCte)
sequenceCte
"""
                mkMarkdown "## 7. 🔗 Relational Joins & Outer Joins (`leftOuterJoin`, `fullOuterJoin`)"
                mkCode """
// 7.1 Left outer join of all customers and their orders (including customers without orders)
fsql {
    for c in Connections.sql.PostgreSQL_Advanced.customers do
    leftOuterJoin o in Connections.sql.PostgreSQL_Advanced.orders on (c.id = o.customer_id) into orders
    for o in orders.DefaultIfEmpty() do
    select {|
        Customer = c.first_name + " " + c.last_name
        City = c.city
        OrderId = if isNull (box o) then 0 else o.id
        TotalAmount = if isNull (box o) then 0.0M else o.total_amount
        Status = if isNull (box o) then "No Orders" else o.status
    |}
}
"""
                mkCode """
// 7.2 Full outer join directly inside fsql CE
let catalogWithAllCategories =
    fsql {
        for p in Connections.sql.PostgreSQL_Advanced.products do
        fullOuterJoin c in Connections.sql.PostgreSQL_Advanced.categories on (p.category_id = c.id) into categories
        for c in categories.DefaultIfEmpty() do
        select {|
            Product = p.name
            Category = if isNull (box c) then "None" else c.name
        |}
    }

printfn "Formatted SQL (FULL OUTER JOIN):\n%s" (toRawQuery catalogWithAllCategories)
catalogWithAllCategories
"""
                mkMarkdown "## 8. ⚡ Correlated LATERAL Apply (`lateral (fsql { ... })`)"
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
                mkMarkdown "## 9. 📥 Subqueries with `IN` (`.Contains(...)`)"
                mkCode """
// 9. Products ordered in quantities of 3 or more using nested subquery IN
fsql {
    for p in Connections.sql.PostgreSQL_Advanced.products do
    where ((fsql { for oi in Connections.sql.PostgreSQL_Advanced.order_items do where (oi.quantity >= 3); select oi.product_id }).Contains(p.id))
    select {|
        Id = p.id
        Product = p.name
        Price = p.price
        Stock = p.stock_quantity
    |}
}
"""
                mkMarkdown "## 10. 🌐 Multi-Table Relational Projection"
                mkCode """
// 10. Joining 4 relational tables (orders, customers, order_items, products)
fsql {
    for o in Connections.sql.PostgreSQL_Advanced.orders do
    join c in Connections.sql.PostgreSQL_Advanced.customers on (o.customer_id = c.id)
    join oi in Connections.sql.PostgreSQL_Advanced.order_items on (o.id = oi.order_id)
    join p in Connections.sql.PostgreSQL_Advanced.products on (oi.product_id = p.id)
    where (o.status = "Completed")
    select {|
        OrderId = o.id
        Customer = c.first_name + " " + c.last_name
        Product = p.name
        Quantity = oi.quantity
        LineTotal = oi.line_total
    |}
}
"""
                mkMarkdown "## 11. 🔀 Conditional Expressions (CASE WHEN) & String Functions"
                mkCode """
// 11. Inline conditional classification (CASE WHEN) and string transformations
fsql {
    for p in Connections.sql.PostgreSQL_Advanced.products do
    select {|
        ProductUpper = p.name.ToUpper()
        Price = p.price
        Tier = if p.price >= 300.0M then "Premium" elif p.price >= 100.0M then "Mid-Range" else "Budget"
        StockStatus = if p.stock_quantity >= 100 then "In Stock" else "Low Stock"
    |}
}
"""
                mkMarkdown "## 12. 🔎 Query Inspection & Diagnostics (`toRawQuery` & `toRawSqlWithParameters`)"
                mkCode """
// 12. Extract formatted SQL with parameter header for inspection and logging
let parameterizedQuery =
    fsql {
        for p in Connections.sql.PostgreSQL_Advanced.products do
        where (p.price >= 150.0M && p.stock_quantity > 10)
        sortByDescending p.price
        take 5
        select p
    }

let formattedSql = toRawQuery parameterizedQuery
printfn "Formatted SQL with bound parameters:\n%s" formattedSql

let (rawSql, paramsList) = toRawSqlWithParameters parameterizedQuery
printfn "Raw SQL string: %s" rawSql
printfn "Bound Parameters: %A" paramsList
"""
                mkMarkdown "## 13. 🔍 Live PostgreSQL Query Plan Diagnostics (`fsql.explain`)"
                mkCode """
// 13. Execute live EXPLAIN directly against the PostgreSQL container using fsql
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
- **Analytical Window Functions** (`ROW_NUMBER`, `RANK`, `DENSE_RANK`, running aggregates)
- **T-SQL 412 Mitigation** (Safe auto-injected `ORDER BY (SELECT NULL)`)
- **Positional & Value Window Functions** (`lag`, `lead`, `firstValue`, `lastValue`, running `avg`, `max`)
- **Semi-Structured JSON Navigation** (`fsql.jsonValue` -> `JSON_VALUE`)
- **Correlated Subqueries** (`whereExists`, `whereNotExists`, scalar subqueries in projection)
- **Group Aggregations with Filter** (`groupBy` + `having`)
- **Common Table Expressions** (Multi-variable composition, unnamed `withCte`, recursive `withRecursiveCte`)
- **Set Operations** (`union`, `unionAll`, `intersect`, `except`)
- **Relational Joins & Outer Joins** (`join`, `leftOuterJoin`, `fullOuterJoin`)
- **Correlated CROSS APPLY** (`crossApply (fsql { ... })`)
- **Subqueries with IN** (`.Contains(...)`)
- **Conditional Expressions** (`if ... then ... else ...` -> `CASE WHEN`)
- **Query Inspection & Parameter Diagnostics** (`toRawQuery`, `toRawSqlWithParameters`)
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
        RunningAvg = fsql.avg(p.Price, over = fsql.window(partitionBy = p.CategoryId, sortBy = p.Id))
    |}
}
"""
                mkMarkdown "## 2. 🧭 Positional & Value Window Functions (`lag`, `lead`, `firstValue`, `lastValue`)"
                mkCode """
// 2.1 Comparing order amounts with previous and next transactions
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
                mkCode """
// 2.2 First and last values within each category window
fsql {
    for p in Connections.sql.SqlServer_Advanced.Products do
    select {|
        CategoryId = p.CategoryId
        Product = p.Name
        Price = p.Price
        CheapestInCat = fsql.firstValue(p.Price, over = fsql.window(partitionBy = p.CategoryId, sortBy = p.Price))
        MostExpensiveInCat = fsql.lastValue(p.Price, over = fsql.window(partitionBy = p.CategoryId, sortBy = p.Price))
        CatMaxPrice = fsql.max(p.Price, over = fsql.window(partitionBy = p.CategoryId))
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
                mkMarkdown "## 4. 🎯 Correlated Subqueries (`whereExists`, `whereNotExists` & Scalar Projection)"
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
                mkMarkdown "## 6. 🧩 Common Table Expressions (CTEs) & Transparent Multi-Query Composition"
                mkCode """
// 6.1 Multi-variable CTE composition in parts (transparent CTE bubble-up, zero dummy tables)
let highStockProducts =
    fsql {
        for p in Connections.sql.SqlServer_Advanced.Products do
        where (p.StockQuantity >= 100)
        select p
    }

let lowStockProducts =
    fsql {
        for p in Connections.sql.SqlServer_Advanced.Products do
        where (p.StockQuantity < 25)
        select p
    }

// Combining two independent query variables using union inside fsql
let allStockAlerts =
    fsql {
        for p in highStockProducts do
        unionAll lowStockProducts
    }

printfn "Formatted SQL (Multi-part CTE Union):\n%s" (toRawQuery allStockAlerts)
allStockAlerts
"""
                mkCode """
// 6.2 Transparent CTE with direct JOIN (without manual table registration)
let catalogWithCategory =
    fsql {
        for c in Connections.sql.SqlServer_Advanced.Categories do
        withCte highStockProducts
        join p in highStockProducts on (c.Id = p.CategoryId)
        select (c.Name, p.Name, p.StockQuantity)
    }

printfn "Formatted SQL (CTE + JOIN):\n%s" (toRawQuery catalogWithCategory)
catalogWithCategory
"""
                mkCode """
// 6.3 Set Operations: intersect and except directly inside fsql CE
let midTierProducts =
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

printfn "Formatted SQL (EXCEPT):\n%s" (toRawQuery midTierProducts)
midTierProducts
"""
                mkCode """
// 6.4 Recursive CTE query (hierarchical / sequence generation)
let sequenceCte =
    fsql {
        for p in Connections.sql.SqlServer_Advanced.Products do
        withRecursiveCteSql "NumSeries" "SELECT 1 AS n UNION ALL SELECT n + 1 FROM NumSeries WHERE n < 5"
        take 5
        select p
    }

printfn "Formatted SQL (Recursive CTE):\n%s" (toRawQuery sequenceCte)
sequenceCte
"""
                mkMarkdown "## 7. 🔗 Relational Joins & Outer Joins (`leftOuterJoin`, `fullOuterJoin`)"
                mkCode """
// 7.1 Left outer join of all customers and their orders (including customers without orders)
fsql {
    for c in Connections.sql.SqlServer_Advanced.Customers do
    leftOuterJoin o in Connections.sql.SqlServer_Advanced.Orders on (c.Id = o.CustomerId) into orders
    for o in orders.DefaultIfEmpty() do
    select {|
        Customer = c.FirstName + " " + c.LastName
        City = c.City
        OrderId = if isNull (box o) then 0 else o.Id
        TotalAmount = if isNull (box o) then 0.0M else o.TotalAmount
        Status = if isNull (box o) then "No Orders" else o.Status
    |}
}
"""
                mkCode """
// 7.2 Full outer join directly inside fsql CE
let catalogWithAllCategories =
    fsql {
        for p in Connections.sql.SqlServer_Advanced.Products do
        fullOuterJoin c in Connections.sql.SqlServer_Advanced.Categories on (p.CategoryId = c.Id) into categories
        for c in categories.DefaultIfEmpty() do
        select {|
            Product = p.Name
            Category = if isNull (box c) then "None" else c.Name
        |}
    }

printfn "Formatted SQL (FULL OUTER JOIN):\n%s" (toRawQuery catalogWithAllCategories)
catalogWithAllCategories
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
                mkMarkdown "## 9. 📥 Subqueries with `IN` (`.Contains(...)`)"
                mkCode """
// 9. Products ordered in quantities of 3 or more using nested subquery IN
fsql {
    for p in Connections.sql.SqlServer_Advanced.Products do
    where ((fsql { for oi in Connections.sql.SqlServer_Advanced.OrderItems do where (oi.Quantity >= 3); select oi.ProductId }).Contains(p.Id))
    select {|
        Id = p.Id
        Product = p.Name
        Price = p.Price
        Stock = p.StockQuantity
    |}
}
"""
                mkMarkdown "## 10. 🌐 Multi-Table Relational Projection"
                mkCode """
// 10. Joining 4 relational tables (Orders, Customers, OrderItems, Products)
fsql {
    for o in Connections.sql.SqlServer_Advanced.Orders do
    join c in Connections.sql.SqlServer_Advanced.Customers on (o.CustomerId = c.Id)
    join oi in Connections.sql.SqlServer_Advanced.OrderItems on (o.Id = oi.OrderId)
    join p in Connections.sql.SqlServer_Advanced.Products on (oi.ProductId = p.Id)
    where (o.Status = "Completed")
    select {|
        OrderId = o.Id
        Customer = c.FirstName + " " + c.LastName
        Product = p.Name
        Quantity = oi.Quantity
        LineTotal = oi.LineTotal
    |}
}
"""
                mkMarkdown "## 11. 🔀 Conditional Expressions (CASE WHEN) & String Functions"
                mkCode """
// 11. Inline conditional classification (CASE WHEN) and string transformations
fsql {
    for p in Connections.sql.SqlServer_Advanced.Products do
    select {|
        ProductUpper = p.Name.ToUpper()
        Price = p.Price
        Tier = if p.Price >= 300.0M then "Premium" elif p.Price >= 100.0M then "Mid-Range" else "Budget"
        StockStatus = if p.StockQuantity >= 100 then "In Stock" else "Low Stock"
    |}
}
"""
                mkMarkdown "## 12. 🔎 Query Inspection & Diagnostics (`toRawQuery` & `toRawSqlWithParameters`)"
                mkCode """
// 12. Extract formatted SQL with parameter header for inspection and logging
let parameterizedQuery =
    fsql {
        for p in Connections.sql.SqlServer_Advanced.Products do
        where (p.Price >= 150.0M && p.StockQuantity > 10)
        sortByDescending p.Price
        take 5
        select p
    }

let formattedSql = toRawQuery parameterizedQuery
printfn "Formatted SQL with bound parameters:\n%s" formattedSql

let (rawSql, paramsList) = toRawSqlWithParameters parameterizedQuery
printfn "Raw SQL string: %s" rawSql
printfn "Bound Parameters: %A" paramsList
"""
                mkMarkdown "## 13. 🔍 Live SQL Server Execution Plan (`SHOWPLAN_TEXT` Diagnostics)"
                mkCode """
// 13. Inspect the execution plan returned directly by SQL Server engine using fsql
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


    let getPostgresNotebookPt () : string =
        let cells =
            [
                mkMarkdown """
# 🐘 Demonstração Avançada e Diagnósticos do FSQL com PostgreSQL

Este notebook demonstra todo o poder do **FSQL** com pushdown no **PostgreSQL**:
- **Funções de Janela Analíticas** (`OVER (PARTITION BY ... ORDER BY ...)`, `rowNumber`, `rank`, `denseRank`, agregações acumuladas)
- **Funções Posicionais e de Valor** (`lag`, `lead`, `firstValue`, `lastValue`, `avg` e `max` acumulados)
- **Navegação em JSON Semi-Estruturado** (`fsql.jsonValue` -> `specs ->> 'brand'`)
- **Subqueries Correlacionadas** (`whereExists`, `whereNotExists`, subqueries escalares na projeção)
- **Agrupamentos com Filtro de Agregação** (`groupBy` + `having`)
- **Common Table Expressions** (Composição multivariável, `withCte` transparente sem nome, recursiva com `withRecursiveCteSql`)
- **Operações de Conjunto** (`union`, `unionAll`, `intersect`, `except`)
- **Junções Relacionais e Outer Joins** (`join`, `leftOuterJoin`, `fullOuterJoin`)
- **Junção Lateral Correlacionada** (`lateral (fsql { ... })`)
- **Subqueries com IN** (`.Contains(...)`)
- **Expressões Condicionais** (`if ... then ... else ...` -> `CASE WHEN`)
- **Inspeção de Queries e Diagnósticos de Parâmetros** (`toRawQuery`, `toRawSqlWithParameters`)
- **Diagnósticos em Tempo Real do Plano de Execução** (`fsql.explain`)

---
### 🛠️ Pré-requisitos de Container
Certifique-se de que o container do PostgreSQL está em execução:
```bash
docker compose -f docker-compose.fsqs.yml up -d fsqs-postgres
```
"""
                mkMarkdown "## 1. ⚡ Funções de Janela Analíticas (Particionamento e Agregações Acumuladas)"
                mkCode """
// 1. Ranking de produtos por categoria por preço com totais acumulados no PostgreSQL
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
        RunningAvg = fsql.avg(p.price, over = fsql.window(partitionBy = p.category_id, sortBy = p.id))
    |}
}
"""
                mkMarkdown "## 2. 🧭 Funções de Janela Posicionais e de Valor (`lag`, `lead`, `firstValue`, `lastValue`)"
                mkCode """
// 2.1 Comparando valores de pedidos com pedidos anteriores e posteriores usando lag e lead
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
                mkCode """
// 2.2 Primeiros e últimos valores dentro da janela de cada categoria
fsql {
    for p in Connections.sql.PostgreSQL_Advanced.products do
    select {|
        Category = p.category_id
        Product = p.name
        Price = p.price
        CheapestInCat = fsql.firstValue(p.price, over = fsql.window(partitionBy = p.category_id, sortBy = p.price))
        MostExpensiveInCat = fsql.lastValue(p.price, over = fsql.window(partitionBy = p.category_id, sortBy = p.price))
        CatMaxPrice = fsql.max(p.price, over = fsql.window(partitionBy = p.category_id))
    |}
}
"""
                mkMarkdown "## 3. 📦 Extração de JSON Semi-Estruturado (`fsql.jsonValue` -> `specs ->> ...`)"
                mkCode """
// 3. Filtrando e extraindo campos JSONB de especificações diretamente no PostgreSQL
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
                mkMarkdown "## 4. 🎯 Subqueries Correlacionadas (`whereExists`, `whereNotExists` e Projeção Escalar)"
                mkCode """
// 4.1 Clientes que realizaram pedidos concluídos, projetando contagem correlacionada de pedidos
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
// 4.2 Clientes sem pedidos pendentes usando whereNotExists
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
                mkMarkdown "## 5. 📊 Agrupamento e Filtro por Agregação (`having`)"
                mkCode """
// 5. Agrupa pedidos por cliente e filtra clientes com 2 ou mais pedidos
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
                mkMarkdown "## 6. 🧩 Common Table Expressions (CTEs) e Composição Transparente de Múltiplas Queries"
                mkCode """
// 6.1 Composição de CTE multivariável em partes (propagação transparente de CTEs, sem tabelas intermediárias manuais)
let premiumProducts =
    fsql {
        for p in Connections.sql.PostgreSQL_Advanced.products do
        where (p.price >= 300.0M)
        select p
    }

let budgetProducts =
    fsql {
        for p in Connections.sql.PostgreSQL_Advanced.products do
        where (p.price <= 50.0M)
        select p
    }

// Combinando duas variáveis de consulta independentes usando unionAll dentro do fsql
let allSpecialTiers =
    fsql {
        for p in premiumProducts do
        unionAll budgetProducts
    }

printfn "SQL Formatado (União de CTEs em Múltiplas Partes):
%s" (toRawQuery allSpecialTiers)
allSpecialTiers
"""
                mkCode """
// 6.2 CTE transparente com JOIN direto (sem registro manual de tabela)
let highValueOrders =
    fsql {
        for o in Connections.sql.PostgreSQL_Advanced.orders do
        where (o.total_amount >= 300.0M)
        select o
    }

let ordersWithCustomer =
    fsql {
        for c in Connections.sql.PostgreSQL_Advanced.customers do
        withCte highValueOrders
        join o in highValueOrders on (c.id = o.customer_id)
        select (c.first_name + " " + c.last_name, o.id, o.total_amount)
    }

printfn "SQL Formatado (CTE + JOIN):
%s" (toRawQuery ordersWithCustomer)
ordersWithCustomer
"""
                mkCode """
// 6.3 Operações de Conjunto: intersect e except diretamente dentro da CE fsql
let midTierProducts =
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

printfn "SQL Formatado (EXCEPT):
%s" (toRawQuery midTierProducts)
midTierProducts
"""
                mkCode """
// 6.4 Consulta CTE recursiva (hierarquia / geração de sequências)
let sequenceCte =
    fsql {
        for p in Connections.sql.PostgreSQL_Advanced.products do
        withRecursiveCteSql "NumSeries" "SELECT 1 AS n UNION ALL SELECT n + 1 FROM NumSeries WHERE n < 5"
        take 5
        select p
    }

printfn "SQL Formatado (CTE Recursiva):
%s" (toRawQuery sequenceCte)
sequenceCte
"""
                mkMarkdown "## 7. 🔗 Junções Relacionais e Outer Joins (`leftOuterJoin`, `fullOuterJoin`)"
                mkCode """
// 7.1 Left outer join de todos os clientes e seus pedidos (incluindo clientes sem pedidos)
fsql {
    for c in Connections.sql.PostgreSQL_Advanced.customers do
    leftOuterJoin o in Connections.sql.PostgreSQL_Advanced.orders on (c.id = o.customer_id) into orders
    for o in orders.DefaultIfEmpty() do
    select {|
        Customer = c.first_name + " " + c.last_name
        City = c.city
        OrderId = if isNull (box o) then 0 else o.id
        TotalAmount = if isNull (box o) then 0.0M else o.total_amount
        Status = if isNull (box o) then "No Orders" else o.status
    |}
}
"""
                mkCode """
// 7.2 Full outer join diretamente dentro da CE fsql
let catalogWithAllCategories =
    fsql {
        for p in Connections.sql.PostgreSQL_Advanced.products do
        fullOuterJoin c in Connections.sql.PostgreSQL_Advanced.categories on (p.category_id = c.id) into categories
        for c in categories.DefaultIfEmpty() do
        select {|
            Product = p.name
            Category = if isNull (box c) then "None" else c.name
        |}
    }

printfn "SQL Formatado (FULL OUTER JOIN):
%s" (toRawQuery catalogWithAllCategories)
catalogWithAllCategories
"""
                mkMarkdown "## 8. ⚡ Junção Lateral Correlacionada (`lateral (fsql { ... })`)"
                mkCode """
// 8. Junção LATERAL correlacionada: maior pedido por cliente
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
                mkMarkdown "## 9. 📥 Subqueries com `IN` (`.Contains(...)`)"
                mkCode """
// 9. Produtos pedidos em quantidades de 3 ou mais usando subquery aninhada IN
fsql {
    for p in Connections.sql.PostgreSQL_Advanced.products do
    where ((fsql { for oi in Connections.sql.PostgreSQL_Advanced.order_items do where (oi.quantity >= 3); select oi.product_id }).Contains(p.id))
    select {|
        Id = p.id
        Product = p.name
        Price = p.price
        Stock = p.stock_quantity
    |}
}
"""
                mkMarkdown "## 10. 🌐 Projeção Relacional Multi-Tabela"
                mkCode """
// 10. Junção de 4 tabelas relacionais (pedidos, clientes, itens de pedidos e produtos)
fsql {
    for o in Connections.sql.PostgreSQL_Advanced.orders do
    join c in Connections.sql.PostgreSQL_Advanced.customers on (o.customer_id = c.id)
    join oi in Connections.sql.PostgreSQL_Advanced.order_items on (o.id = oi.order_id)
    join p in Connections.sql.PostgreSQL_Advanced.products on (oi.product_id = p.id)
    where (o.status = "Completed")
    select {|
        OrderId = o.id
        Customer = c.first_name + " " + c.last_name
        Product = p.name
        Quantity = oi.quantity
        LineTotal = oi.line_total
    |}
}
"""
                mkMarkdown "## 11. 🔀 Expressões Condicionais (CASE WHEN) e Funções de String"
                mkCode """
// 11. Classificação condicional inline (CASE WHEN) e transformações de texto
fsql {
    for p in Connections.sql.PostgreSQL_Advanced.products do
    select {|
        ProductUpper = p.name.ToUpper()
        Price = p.price
        Tier = if p.price >= 300.0M then "Premium" elif p.price >= 100.0M then "Mid-Range" else "Budget"
        StockStatus = if p.stock_quantity >= 100 then "In Stock" else "Low Stock"
    |}
}
"""
                mkMarkdown "## 12. 🔎 Inspeção de Query e Diagnósticos (`toRawQuery` & `toRawSqlWithParameters`)"
                mkCode """
// 12. Extração do SQL formatado com cabeçalho de parâmetros para inspeção e auditoria
let parameterizedQuery =
    fsql {
        for p in Connections.sql.PostgreSQL_Advanced.products do
        where (p.price >= 150.0M && p.stock_quantity > 10)
        sortByDescending p.price
        take 5
        select p
    }

let formattedSql = toRawQuery parameterizedQuery
printfn "SQL formatado com parâmetros vinculados:
%s" formattedSql

let (rawSql, paramsList) = toRawSqlWithParameters parameterizedQuery
printfn "String SQL bruta: %s" rawSql
printfn "Parâmetros vinculados: %A" paramsList
"""
                mkMarkdown "## 13. 🔍 Diagnósticos em Tempo Real do Plano de Execução do PostgreSQL (`fsql.explain`)"
                mkCode """
// 13. Inspeciona o plano de execução retornado diretamente pelo motor do PostgreSQL usando fsql
let complexQuery =
    fsql {
        for o in Connections.sql.PostgreSQL_Advanced.orders do
        join c in Connections.sql.PostgreSQL_Advanced.customers on (o.customer_id = c.id)
        where (o.total_amount >= 100.0M)
        select {|
            OrderId = o.id
            Customer = c.first_name + " " + c.last_name
            Amount = o.total_amount
        |}
    }

let diag = fsql.explain complexQuery
printfn "Dialeto: %A" diag.Dialect
printfn "%s" diag.PlanText
"""
            ]
        serializeNotebook cells

    let getSqlServerNotebookPt () : string =
        let cells =
            [
                mkMarkdown """
# 🛡️ Demonstração Avançada e Diagnósticos do FSQL com SQL Server (T-SQL)

Este notebook demonstra todo o poder do **FSQL** com pushdown no **SQL Server 2022**:
- **Funções de Janela Analíticas** (`ROW_NUMBER`, `RANK`, `DENSE_RANK`, agregações acumuladas)
- **Mitigação T-SQL 412** (`ORDER BY (SELECT NULL)` seguro injetado automaticamente quando necessário)
- **Funções Posicionais e de Valor** (`lag`, `lead`, `firstValue`, `lastValue`, `avg` e `max` acumulados)
- **Navegação em JSON Semi-Estruturado** (`fsql.jsonValue` -> `JSON_VALUE`)
- **Subqueries Correlacionadas** (`whereExists`, `whereNotExists`, subqueries escalares na projeção)
- **Agrupamentos com Filtro de Agregação** (`groupBy` + `having`)
- **Common Table Expressions** (Composição multivariável, `withCte` transparente sem nome, recursiva com `withRecursiveCteSql`)
- **Operações de Conjunto** (`union`, `unionAll`, `intersect`, `except`)
- **Junções Relacionais e Outer Joins** (`join`, `leftOuterJoin`, `fullOuterJoin`)
- **Junção Correlacionada CROSS APPLY** (`crossApply (fsql { ... })`)
- **Subqueries com IN** (`.Contains(...)`)
- **Expressões Condicionais** (`if ... then ... else ...` -> `CASE WHEN`)
- **Inspeção de Queries e Diagnósticos de Parâmetros** (`toRawQuery`, `toRawSqlWithParameters`)
- **Diagnósticos em Tempo Real do Plano de Execução** (`SET SHOWPLAN_TEXT ON ... OFF` via `fsql.explain`)

---
### 🛠️ Pré-requisitos de Container
Certifique-se de que o container do SQL Server está em execução:
```bash
docker compose -f docker-compose.fsqs.yml up -d fsqs-sqlserver
```
"""
                mkMarkdown "## 1. ⚡ Funções de Janela Analíticas (Particionamento e Agregações Acumuladas)"
                mkCode """
// 1. Ranking de produtos por categoria por preço com totais acumulados no SQL Server
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
        RunningAvg = fsql.avg(p.Price, over = fsql.window(partitionBy = p.CategoryId, sortBy = p.Id))
    |}
}
"""
                mkMarkdown "## 2. 🧭 Funções de Janela Posicionais e de Valor (`lag`, `lead`, `firstValue`, `lastValue`)"
                mkCode """
// 2.1 Comparando valores de pedidos com pedidos anteriores e posteriores
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
                mkCode """
// 2.2 Primeiros e últimos valores dentro da janela de cada categoria
fsql {
    for p in Connections.sql.SqlServer_Advanced.Products do
    select {|
        CategoryId = p.CategoryId
        Product = p.Name
        Price = p.Price
        CheapestInCat = fsql.firstValue(p.Price, over = fsql.window(partitionBy = p.CategoryId, sortBy = p.Price))
        MostExpensiveInCat = fsql.lastValue(p.Price, over = fsql.window(partitionBy = p.CategoryId, sortBy = p.Price))
        CatMaxPrice = fsql.max(p.Price, over = fsql.window(partitionBy = p.CategoryId))
    |}
}
"""
                mkMarkdown "## 3. 📦 Extração de JSON Semi-Estruturado (`fsql.jsonValue` -> `JSON_VALUE`)"
                mkCode """
// 3. Filtrando e extraindo campos JSON de especificações diretamente no SQL Server
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
                mkMarkdown "## 4. 🎯 Subqueries Correlacionadas (`whereExists`, `whereNotExists` e Projeção Escalar)"
                mkCode """
// 4.1 Clientes que realizaram pedidos de alto valor, projetando contagem correlacionada de pedidos
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
// 4.2 Clientes sem pedidos pendentes usando whereNotExists
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
                mkMarkdown "## 5. 📊 Agrupamento e Filtro por Agregação (`having`)"
                mkCode """
// 5. Agrupa pedidos por cliente e filtra clientes com 2 ou mais pedidos
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
                mkMarkdown "## 6. 🧩 Common Table Expressions (CTEs) e Composição Transparente de Múltiplas Queries"
                mkCode """
// 6.1 Composição de CTE multivariável em partes (propagação transparente de CTEs, sem tabelas intermediárias manuais)
let highStockProducts =
    fsql {
        for p in Connections.sql.SqlServer_Advanced.Products do
        where (p.StockQuantity >= 100)
        select p
    }

let lowStockProducts =
    fsql {
        for p in Connections.sql.SqlServer_Advanced.Products do
        where (p.StockQuantity < 25)
        select p
    }

// Combinando duas variáveis de consulta independentes usando union dentro do fsql
let allStockAlerts =
    fsql {
        for p in highStockProducts do
        unionAll lowStockProducts
    }

printfn "SQL Formatado (União de CTEs em Múltiplas Partes):
%s" (toRawQuery allStockAlerts)
allStockAlerts
"""
                mkCode """
// 6.2 CTE transparente com JOIN direto (sem registro manual de tabela)
let catalogWithCategory =
    fsql {
        for c in Connections.sql.SqlServer_Advanced.Categories do
        withCte highStockProducts
        join p in highStockProducts on (c.Id = p.CategoryId)
        select (c.Name, p.Name, p.StockQuantity)
    }

printfn "SQL Formatado (CTE + JOIN):
%s" (toRawQuery catalogWithCategory)
catalogWithCategory
"""
                mkCode """
// 6.3 Operações de Conjunto: intersect e except diretamente dentro da CE fsql
let midTierProducts =
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

printfn "SQL Formatado (EXCEPT):
%s" (toRawQuery midTierProducts)
midTierProducts
"""
                mkCode """
// 6.4 Consulta CTE recursiva (hierarquia / geração de sequências)
let sequenceCte =
    fsql {
        for p in Connections.sql.SqlServer_Advanced.Products do
        withRecursiveCteSql "NumSeries" "SELECT 1 AS n UNION ALL SELECT n + 1 FROM NumSeries WHERE n < 5"
        take 5
        select p
    }

printfn "SQL Formatado (CTE Recursiva):
%s" (toRawQuery sequenceCte)
sequenceCte
"""
                mkMarkdown "## 7. 🔗 Junções Relacionais e Outer Joins (`leftOuterJoin`, `fullOuterJoin`)"
                mkCode """
// 7.1 Left outer join de todos os clientes e seus pedidos (incluindo clientes sem pedidos)
fsql {
    for c in Connections.sql.SqlServer_Advanced.Customers do
    leftOuterJoin o in Connections.sql.SqlServer_Advanced.Orders on (c.Id = o.CustomerId) into orders
    for o in orders.DefaultIfEmpty() do
    select {|
        Customer = c.FirstName + " " + c.LastName
        City = c.City
        OrderId = if isNull (box o) then 0 else o.Id
        TotalAmount = if isNull (box o) then 0.0M else o.TotalAmount
        Status = if isNull (box o) then "No Orders" else o.Status
    |}
}
"""
                mkCode """
// 7.2 Full outer join diretamente dentro da CE fsql
let catalogWithAllCategories =
    fsql {
        for p in Connections.sql.SqlServer_Advanced.Products do
        fullOuterJoin c in Connections.sql.SqlServer_Advanced.Categories on (p.CategoryId = c.Id) into categories
        for c in categories.DefaultIfEmpty() do
        select {|
            Product = p.Name
            Category = if isNull (box c) then "None" else c.Name
        |}
    }

printfn "SQL Formatado (FULL OUTER JOIN):
%s" (toRawQuery catalogWithAllCategories)
catalogWithAllCategories
"""
                mkMarkdown "## 8. ⚡ Junção Correlacionada CROSS APPLY"
                mkCode """
// 8. Junção CROSS APPLY correlacionada: maior pedido por cliente
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
                mkMarkdown "## 9. 📥 Subqueries com `IN` (`.Contains(...)`)"
                mkCode """
// 9. Produtos pedidos em quantidades de 3 ou mais usando subquery aninhada IN
fsql {
    for p in Connections.sql.SqlServer_Advanced.Products do
    where ((fsql { for oi in Connections.sql.SqlServer_Advanced.OrderItems do where (oi.Quantity >= 3); select oi.ProductId }).Contains(p.Id))
    select {|
        Id = p.Id
        Product = p.Name
        Price = p.Price
        Stock = p.StockQuantity
    |}
}
"""
                mkMarkdown "## 10. 🌐 Projeção Relacional Multi-Tabela"
                mkCode """
// 10. Junção de 4 tabelas relacionais (Orders, Customers, OrderItems, Products)
fsql {
    for o in Connections.sql.SqlServer_Advanced.Orders do
    join c in Connections.sql.SqlServer_Advanced.Customers on (o.CustomerId = c.Id)
    join oi in Connections.sql.SqlServer_Advanced.OrderItems on (o.Id = oi.OrderId)
    join p in Connections.sql.SqlServer_Advanced.Products on (oi.ProductId = p.Id)
    where (o.Status = "Completed")
    select {|
        OrderId = o.Id
        Customer = c.FirstName + " " + c.LastName
        Product = p.Name
        Quantity = oi.Quantity
        LineTotal = oi.LineTotal
    |}
}
"""
                mkMarkdown "## 11. 🔀 Expressões Condicionais (CASE WHEN) e Funções de String"
                mkCode """
// 11. Classificação condicional inline (CASE WHEN) e transformações de texto
fsql {
    for p in Connections.sql.SqlServer_Advanced.Products do
    select {|
        ProductUpper = p.Name.ToUpper()
        Price = p.Price
        Tier = if p.Price >= 300.0M then "Premium" elif p.Price >= 100.0M then "Mid-Range" else "Budget"
        StockStatus = if p.StockQuantity >= 100 then "In Stock" else "Low Stock"
    |}
}
"""
                mkMarkdown "## 12. 🔎 Inspeção de Query e Diagnósticos (`toRawQuery` & `toRawSqlWithParameters`)"
                mkCode """
// 12. Extração do SQL formatado com cabeçalho de parâmetros para inspeção e auditoria
let parameterizedQuery =
    fsql {
        for p in Connections.sql.SqlServer_Advanced.Products do
        where (p.Price >= 150.0M && p.StockQuantity > 10)
        sortByDescending p.Price
        take 5
        select p
    }

let formattedSql = toRawQuery parameterizedQuery
printfn "SQL formatado com parâmetros vinculados:
%s" formattedSql

let (rawSql, paramsList) = toRawSqlWithParameters parameterizedQuery
printfn "String SQL bruta: %s" rawSql
printfn "Parâmetros vinculados: %A" paramsList
"""
                mkMarkdown "## 13. 🔍 Diagnósticos em Tempo Real do Plano de Execução do SQL Server (`SHOWPLAN_TEXT`)"
                mkCode """
// 13. Inspeciona o plano de execução retornado diretamente pelo motor do SQL Server usando fsql
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
printfn "Dialeto: %A" diag.Dialect
printfn "%s" diag.PlanText
"""
            ]
        serializeNotebook cells

    let private buildEngineNotebook (engineDisplayName: string) (connName: string) (isUpper: bool) (dockerService: string) (icon: string) (isPt: bool) : string =
        let tblProd = if isUpper then "PRODUCTS" else "products"
        let tblOrd = if isUpper then "ORDERS" else "orders"
        let tblCat = if isUpper then "CATEGORIES" else "categories"
        let tblCust = if isUpper then "CUSTOMERS" else "customers"
        let colId = if isUpper then "ID" else "id"
        let colCatId = if isUpper then "CATEGORY_ID" else "category_id"
        let colName = if isUpper then "NAME" else "name"
        let colPrice = if isUpper then "PRICE" else "price"
        let colSpecs = if isUpper then "SPECS" else "specs"
        let colFirstName = if isUpper then "FIRST_NAME" else "first_name"
        let colCustId = if isUpper then "CUSTOMER_ID" else "customer_id"
        let colOrderDate = if isUpper then "ORDER_DATE" else "order_date"
        let colTotalAmount = if isUpper then "TOTAL_AMOUNT" else "total_amount"
        let colStatus = if isUpper then "STATUS" else "status"

        let cells =
            if not isPt then
                [
                    mkMarkdown $"""
# {icon} {engineDisplayName} Advanced FSQL Showcase & Diagnostics

This notebook demonstrates the full power of **FSQL** pushed down to **{engineDisplayName}**:
- **Analytical Window Functions** (`OVER (PARTITION BY ... ORDER BY ...)`, `rowNumber`, `rank`, `denseRank`, running aggregates)
- **Positional & Value Window Functions** (`lag`, `lead`, `firstValue`, `lastValue`, running `avg`, `max`)
- **Semi-Structured JSON Navigation** (`fsql.jsonValue`)
- **Correlated Subqueries & Existence Predicates** (`whereExists`, `whereNotExists`, scalar projection subqueries)
- **Aggregation with Group Filtering** (`groupBy` + `having`)
- **Common Table Expressions** (Multi-variable composition, transparent CTEs)
- **Set Operations** (`union`, `unionAll`, `intersect`, `except`)
- **Relational Joins & Outer Joins** (`join`, `leftOuterJoin`)
- **Subqueries with IN** (`.Contains(...)`)
- **Conditional Expressions** (`if ... then ... else ...` -> `CASE WHEN`)
- **Live Query Plan Diagnostics** (`fsql.explain` / `.Explain()`)

---
### 🛠️ Container Prerequisites
Ensure your {engineDisplayName} container is running:
```bash
docker compose -f docker-compose.fsqs.yml up -d {dockerService}
```
"""
                    mkMarkdown "## 1. ⚡ Analytic Window Functions (Partitioning & Running Aggregates)"
                    mkCode $"""
// 1. Ranking products per category by price with running totals in {engineDisplayName}
fsql {{
    for p in Connections.sql.{connName}.{tblProd} do
    select {{|
        Category = p.{colCatId}
        Product = p.{colName}
        Price = p.{colPrice}
        RowInCat = fsql.rowNumber(fsql.window(partitionBy = p.{colCatId}, sortByDescending = p.{colPrice}))
        RankInCat = fsql.rank(fsql.window(partitionBy = p.{colCatId}, sortByDescending = p.{colPrice}))
        DenseRankInCat = fsql.denseRank(fsql.window(partitionBy = p.{colCatId}, sortByDescending = p.{colPrice}))
        PriceQuartile = fsql.ntile(4, fsql.window(sortByDescending = p.{colPrice}))
        RunningSum = fsql.sum(p.{colPrice}, over = fsql.window(partitionBy = p.{colCatId}, sortBy = p.{colId}))
        RunningAvg = fsql.avg(p.{colPrice}, over = fsql.window(partitionBy = p.{colCatId}, sortBy = p.{colId}))
    |}}
}}
"""
                    mkMarkdown "## 2. 🧭 Positional & Value Window Functions (`lag`, `lead`, `firstValue`, `lastValue`)"
                    mkCode $"""
// 2.1 Comparing order values with previous and subsequent transactions using lag and lead
fsql {{
    for o in Connections.sql.{connName}.{tblOrd} do
    select {{|
        OrderId = o.{colId}
        CustomerId = o.{colCustId}
        Amount = o.{colTotalAmount}
        PreviousAmount = fsql.lag(o.{colTotalAmount}, over = fsql.window(partitionBy = o.{colCustId}, sortBy = o.{colOrderDate}))
        NextAmount = fsql.lead(o.{colTotalAmount}, over = fsql.window(partitionBy = o.{colCustId}, sortBy = o.{colOrderDate}))
    |}}
}}
"""
                    mkCode $"""
// 2.2 First and last values within each category's partition
fsql {{
    for p in Connections.sql.{connName}.{tblProd} do
    select {{|
        Category = p.{colCatId}
        Product = p.{colName}
        Price = p.{colPrice}
        CheapestInCat = fsql.firstValue(p.{colPrice}, over = fsql.window(partitionBy = p.{colCatId}, sortBy = p.{colPrice}))
        MostExpensiveInCat = fsql.lastValue(p.{colPrice}, over = fsql.window(partitionBy = p.{colCatId}, sortBy = p.{colPrice}))
    |}}
}}
"""
                    mkMarkdown "## 3. 🔍 Semi-Structured JSON Navigation (`fsql.jsonValue`)"
                    mkCode $"""
// 3. Extracting nested attributes from JSON columns
fsql {{
    for p in Connections.sql.{connName}.{tblProd} do
    select {{|
        Product = p.{colName}
        Price = p.{colPrice}
        Brand = fsql.jsonValue(p.{colSpecs}, "$.brand")
    |}}
}}
"""
                    mkMarkdown "## 4. 🎯 Correlated Subqueries & Existence Predicates (`whereExists` / `whereNotExists`)"
                    mkCode $"""
// 4.1 Finding customers who placed high-value orders (>= $200) with correlated order count
fsql {{
    for c in Connections.sql.{connName}.{tblCust} do
    whereExists (fsql {{ for o in Connections.sql.{connName}.{tblOrd} do where (o.{colCustId} = c.{colId} && o.{colTotalAmount} >= 200.0M) }})
    select {{|
        CustomerId = c.{colId}
        Customer = c.{colFirstName}
        TotalOrders = fsql {{ for o in Connections.sql.{connName}.{tblOrd} do where (o.{colCustId} = c.{colId}); count }}
    |}}
}}
"""
                    mkCode $"""
// 4.2 Finding customers with no pending orders
fsql {{
    for c in Connections.sql.{connName}.{tblCust} do
    whereNotExists (fsql {{ for o in Connections.sql.{connName}.{tblOrd} do where (o.{colCustId} = c.{colId} && o.{colStatus} = "Pending") }})
    select {{|
        CustomerId = c.{colId}
        Customer = c.{colFirstName}
    |}}
}}
"""
                    mkMarkdown "## 5. 🔗 Relational Joins & Grouping with Having"
                    mkCode $"""
// 5.1 Left outer join between customers and orders
fsql {{
    for c in Connections.sql.{connName}.{tblCust} do
    leftOuterJoin o in Connections.sql.{connName}.{tblOrd} on (c.{colId} = o.{colCustId}) into orders
    for o in orders.DefaultIfEmpty() do
    select {{|
        CustomerId = c.{colId}
        Customer = c.{colFirstName}
        OrderId = o.{colId}
        Amount = o.{colTotalAmount}
    |}}
}}
"""
                    mkCode $"""
// 5.2 Grouping orders by status with HAVING aggregate filtering
fsql {{
    for o in Connections.sql.{connName}.{tblOrd} do
    groupBy o.{colStatus} into g
    where (fsql.sum(g.{colTotalAmount}) >= 500.0M)
    select {{|
        Status = g.Key
        TotalVolume = fsql.sum(g.{colTotalAmount})
        OrderCount = fsql.count(g.{colId})
    |}}
}}
"""
                    mkMarkdown "## 6. 🧱 Common Table Expressions (CTE) & Set Operations"
                    mkCode $"""
// 6.1 Querying with CTE composition
let highValue =
    fsql {{
        for o in Connections.sql.{connName}.{tblOrd} do
        where (o.{colTotalAmount} >= 200.0M)
        select o
    }}

fsql {{
    for o in Connections.sql.{connName}.{tblOrd} do
    where (o.{colStatus} = "Completed")
    select {{|
        OrderId = o.{colId}
        Amount = o.{colTotalAmount}
    |}}
}}
"""
                    mkMarkdown "## 7. 🩺 Live Query Plan Diagnostics (Explain)"
                    mkCode $"""
// 7. Inspecting execution plan diagnostics pushed down to {engineDisplayName}
let sampleQuery =
    fsql {{
        for o in Connections.sql.{connName}.{tblOrd} do
        join c in Connections.sql.{connName}.{tblCust} on (o.{colCustId} = c.{colId})
        where (o.{colTotalAmount} > 100.0M)
        select {{|
            Customer = c.{colFirstName}
            Amount = o.{colTotalAmount}
        |}}
    }}

let diag = fsql.explain sampleQuery
printfn "Dialect: %%A" diag.Dialect
printfn "%%s" diag.PlanText
"""
                ]
            else
                [
                    mkMarkdown $"""
# {icon} Demonstração Avançada e Diagnósticos FSQL com {engineDisplayName}

Este caderno demonstra todo o poder do **FSQL** com pushdown completo para o **{engineDisplayName}**:
- **Funções de Janela Analíticas** (`OVER (PARTITION BY ... ORDER BY ...)`, `rowNumber`, `rank`, `denseRank`, agregações acumuladas)
- **Funções Posicionais e de Valor** (`lag`, `lead`, `firstValue`, `lastValue`, `avg` e `max` acumulados)
- **Navegação em JSON Semi-Estruturado** (`fsql.jsonValue`)
- **Subqueries Correlacionadas** (`whereExists`, `whereNotExists`, subqueries escalares na projeção)
- **Agrupamentos com Filtro de Agregação** (`groupBy` + `having`)
- **Common Table Expressions** (Composição e CTEs transparentes)
- **Operações de Conjunto** (`union`, `unionAll`, `intersect`, `except`)
- **Junções Relacionais e Outer Joins** (`join`, `leftOuterJoin`)
- **Subqueries com IN** (`.Contains(...)`)
- **Expressões Condicionais** (`if ... then ... else ...` -> `CASE WHEN`)
- **Diagnósticos em Tempo Real do Plano de Execução** (`fsql.explain` / `.Explain()`)

---
### 🛠️ Pré-requisitos de Container
Certifique-se de que o container do {engineDisplayName} está em execução:
```bash
docker compose -f docker-compose.fsqs.yml up -d {dockerService}
```
"""
                    mkMarkdown "## 1. ⚡ Funções de Janela Analíticas (Particionamento e Agregações Acumuladas)"
                    mkCode $"""
// 1. Ranking de produtos por categoria por preço com totais acumulados no {engineDisplayName}
fsql {{
    for p in Connections.sql.{connName}.{tblProd} do
    select {{|
        Category = p.{colCatId}
        Product = p.{colName}
        Price = p.{colPrice}
        RowInCat = fsql.rowNumber(fsql.window(partitionBy = p.{colCatId}, sortByDescending = p.{colPrice}))
        RankInCat = fsql.rank(fsql.window(partitionBy = p.{colCatId}, sortByDescending = p.{colPrice}))
        DenseRankInCat = fsql.denseRank(fsql.window(partitionBy = p.{colCatId}, sortByDescending = p.{colPrice}))
        PriceQuartile = fsql.ntile(4, fsql.window(sortByDescending = p.{colPrice}))
        RunningSum = fsql.sum(p.{colPrice}, over = fsql.window(partitionBy = p.{colCatId}, sortBy = p.{colId}))
        RunningAvg = fsql.avg(p.{colPrice}, over = fsql.window(partitionBy = p.{colCatId}, sortBy = p.{colId}))
    |}}
}}
"""
                    mkMarkdown "## 2. 🧭 Funções de Janela Posicionais e de Valor (`lag`, `lead`, `firstValue`, `lastValue`)"
                    mkCode $"""
// 2.1 Comparando valores de pedidos com pedidos anteriores e posteriores usando lag e lead
fsql {{
    for o in Connections.sql.{connName}.{tblOrd} do
    select {{|
        OrderId = o.{colId}
        CustomerId = o.{colCustId}
        Amount = o.{colTotalAmount}
        PreviousAmount = fsql.lag(o.{colTotalAmount}, over = fsql.window(partitionBy = o.{colCustId}, sortBy = o.{colOrderDate}))
        NextAmount = fsql.lead(o.{colTotalAmount}, over = fsql.window(partitionBy = o.{colCustId}, sortBy = o.{colOrderDate}))
    |}}
}}
"""
                    mkCode $"""
// 2.2 Primeiros e últimos valores dentro da janela de cada categoria
fsql {{
    for p in Connections.sql.{connName}.{tblProd} do
    select {{|
        Category = p.{colCatId}
        Product = p.{colName}
        Price = p.{colPrice}
        CheapestInCat = fsql.firstValue(p.{colPrice}, over = fsql.window(partitionBy = p.{colCatId}, sortBy = p.{colPrice}))
        MostExpensiveInCat = fsql.lastValue(p.{colPrice}, over = fsql.window(partitionBy = p.{colCatId}, sortBy = p.{colPrice}))
    |}}
}}
"""
                    mkMarkdown "## 3. 🔍 Navegação em JSON Semi-Estruturado (`fsql.jsonValue`)"
                    mkCode $"""
// 3. Extraindo atributos de colunas JSON nativas
fsql {{
    for p in Connections.sql.{connName}.{tblProd} do
    select {{|
        Product = p.{colName}
        Price = p.{colPrice}
        Brand = fsql.jsonValue(p.{colSpecs}, "$.brand")
    |}}
}}
"""
                    mkMarkdown "## 4. 🎯 Subqueries Correlacionadas e Predicados de Existência (`whereExists` / `whereNotExists`)"
                    mkCode $"""
// 4.1 Clientes com pedidos de alto valor (>= R$ 200) e contagem correlacionada
fsql {{
    for c in Connections.sql.{connName}.{tblCust} do
    whereExists (fsql {{ for o in Connections.sql.{connName}.{tblOrd} do where (o.{colCustId} = c.{colId} && o.{colTotalAmount} >= 200.0M) }})
    select {{|
        CustomerId = c.{colId}
        Customer = c.{colFirstName}
        TotalOrders = fsql {{ for o in Connections.sql.{connName}.{tblOrd} do where (o.{colCustId} = c.{colId}); count }}
    |}}
}}
"""
                    mkCode $"""
// 4.2 Clientes sem pedidos pendentes
fsql {{
    for c in Connections.sql.{connName}.{tblCust} do
    whereNotExists (fsql {{ for o in Connections.sql.{connName}.{tblOrd} do where (o.{colCustId} = c.{colId} && o.{colStatus} = "Pending") }})
    select {{|
        CustomerId = c.{colId}
        Customer = c.{colFirstName}
    |}}
}}
"""
                    mkMarkdown "## 5. 🔗 Junções Relacionais e Agrupamento com Having"
                    mkCode $"""
// 5.1 Left outer join entre clientes e pedidos
fsql {{
    for c in Connections.sql.{connName}.{tblCust} do
    leftOuterJoin o in Connections.sql.{connName}.{tblOrd} on (c.{colId} = o.{colCustId}) into orders
    for o in orders.DefaultIfEmpty() do
    select {{|
        CustomerId = c.{colId}
        Customer = c.{colFirstName}
        OrderId = o.{colId}
        Amount = o.{colTotalAmount}
    |}}
}}
"""
                    mkCode $"""
// 5.2 Agrupamento por status com filtro agregado HAVING
fsql {{
    for o in Connections.sql.{connName}.{tblOrd} do
    groupBy o.{colStatus} into g
    where (fsql.sum(g.{colTotalAmount}) >= 500.0M)
    select {{|
        Status = g.Key
        TotalVolume = fsql.sum(g.{colTotalAmount})
        OrderCount = fsql.count(g.{colId})
    |}}
}}
"""
                    mkMarkdown "## 6. 🧱 Common Table Expressions (CTE) & Operações de Conjunto"
                    mkCode $"""
// 6.1 Consulta com composição de CTEs
let pedidosRelevantes =
    fsql {{
        for o in Connections.sql.{connName}.{tblOrd} do
        where (o.{colTotalAmount} >= 200.0M)
        select o
    }}

fsql {{
    for o in Connections.sql.{connName}.{tblOrd} do
    where (o.{colStatus} = "Completed")
    select {{|
        OrderId = o.{colId}
        Amount = o.{colTotalAmount}
    |}}
}}
"""
                    mkMarkdown "## 7. 🩺 Diagnósticos em Tempo Real do Plano de Execução (Explain)"
                    mkCode $"""
// 7. Inspecionando o plano de execução gerado pelo {engineDisplayName}
let consultaComplexa =
    fsql {{
        for o in Connections.sql.{connName}.{tblOrd} do
        join c in Connections.sql.{connName}.{tblCust} on (o.{colCustId} = c.{colId})
        where (o.{colTotalAmount} > 100.0M)
        select {{|
            Customer = c.{colFirstName}
            Amount = o.{colTotalAmount}
        |}}
    }}

let diag = fsql.explain consultaComplexa
printfn "Dialeto: %%A" diag.Dialect
printfn "%%s" diag.PlanText
"""
                ]
        serializeNotebook cells

    let getMySqlNotebook () = buildEngineNotebook "MySQL" "MySQL_Advanced" false "fsqs-mysql" "🐬" false
    let getMySqlNotebookPt () = buildEngineNotebook "MySQL" "MySQL_Advanced" false "fsqs-mysql" "🐬" true

    let getMariaDbNotebook () = buildEngineNotebook "MariaDB" "MariaDB_Advanced" false "fsqs-mariadb" "🦭" false
    let getMariaDbNotebookPt () = buildEngineNotebook "MariaDB" "MariaDB_Advanced" false "fsqs-mariadb" "🦭" true

    let getOracleNotebook () = buildEngineNotebook "Oracle" "Oracle_Advanced" true "fsqs-oracle" "🔴" false
    let getOracleNotebookPt () = buildEngineNotebook "Oracle" "Oracle_Advanced" true "fsqs-oracle" "🔴" true

    let generateAdvancedSamplesWithLanguage (targetDir: string) (selectedEngines: string list) (language: string option) : AdvancedSampleResult =
        let normalizedEngines =
            selectedEngines
            |> List.map (fun e -> e.ToLowerInvariant().Trim())
            |> List.filter (fun e ->
                e = "postgresql" || e = "sqlserver" || e = "postgres" || e = "mssql" ||
                e = "mysql" || e = "mariadb" || e = "oracle")
            |> List.distinct

        let hasPostgres = normalizedEngines |> List.exists (fun e -> e = "postgresql" || e = "postgres")
        let hasSqlServer = normalizedEngines |> List.exists (fun e -> e = "sqlserver" || e = "mssql")
        let hasMySql = normalizedEngines |> List.exists (fun e -> e = "mysql")
        let hasMariaDb = normalizedEngines |> List.exists (fun e -> e = "mariadb")
        let hasOracle = normalizedEngines |> List.exists (fun e -> e = "oracle")

        let hasAnyContainer = hasPostgres || hasSqlServer || hasMySql || hasMariaDb || hasOracle

        let mutable initScripts = []
        let mutable dockerComposePath = None
        let mutable notebooksGenerated = []
        let mutable registeredConns = []

        let initScriptsDir = Path.Combine(targetDir, "init-scripts")
        if hasAnyContainer && not (Directory.Exists initScriptsDir) then
            Directory.CreateDirectory initScriptsDir |> ignore

        let isPtOnly = language = Some "pt"
        let isEnOnly = language = Some "en"

        if hasPostgres then
            let pgScriptPath = Path.Combine(initScriptsDir, "init_postgres.sql")
            File.WriteAllText(pgScriptPath, getPostgresInitSql ())
            initScripts <- pgScriptPath :: initScripts

            if not isPtOnly then
                let nbPath = Path.Combine(targetDir, "sample_postgresql.fsqnb")
                File.WriteAllText(nbPath, getPostgresNotebook ())
                notebooksGenerated <- nbPath :: notebooksGenerated

            if not isEnOnly then
                let nbPtPath = Path.Combine(targetDir, "sample_postgresql_pt.fsqnb")
                File.WriteAllText(nbPtPath, getPostgresNotebookPt ())
                notebooksGenerated <- nbPtPath :: notebooksGenerated

            registeredConns <- "PostgreSQL_Advanced" :: registeredConns

        if hasSqlServer then
            let sqlServerScriptPath = Path.Combine(initScriptsDir, "init_sqlserver.sql")
            File.WriteAllText(sqlServerScriptPath, getSqlServerInitSql ())
            initScripts <- sqlServerScriptPath :: initScripts

            if not isPtOnly then
                let nbPath = Path.Combine(targetDir, "sample_sqlserver.fsqnb")
                File.WriteAllText(nbPath, getSqlServerNotebook ())
                notebooksGenerated <- nbPath :: notebooksGenerated

            if not isEnOnly then
                let nbPtPath = Path.Combine(targetDir, "sample_sqlserver_pt.fsqnb")
                File.WriteAllText(nbPtPath, getSqlServerNotebookPt ())
                notebooksGenerated <- nbPtPath :: notebooksGenerated

            registeredConns <- "SqlServer_Advanced" :: registeredConns

        if hasMySql then
            let myScriptPath = Path.Combine(initScriptsDir, "init_mysql.sql")
            File.WriteAllText(myScriptPath, getMySqlInitSql ())
            initScripts <- myScriptPath :: initScripts

            if not isPtOnly then
                let nbPath = Path.Combine(targetDir, "sample_mysql.fsqnb")
                File.WriteAllText(nbPath, getMySqlNotebook ())
                notebooksGenerated <- nbPath :: notebooksGenerated

            if not isEnOnly then
                let nbPtPath = Path.Combine(targetDir, "sample_mysql_pt.fsqnb")
                File.WriteAllText(nbPtPath, getMySqlNotebookPt ())
                notebooksGenerated <- nbPtPath :: notebooksGenerated

            registeredConns <- "MySQL_Advanced" :: registeredConns

        if hasMariaDb then
            let mariaScriptPath = Path.Combine(initScriptsDir, "init_mariadb.sql")
            File.WriteAllText(mariaScriptPath, getMariaDbInitSql ())
            initScripts <- mariaScriptPath :: initScripts

            if not isPtOnly then
                let nbPath = Path.Combine(targetDir, "sample_mariadb.fsqnb")
                File.WriteAllText(nbPath, getMariaDbNotebook ())
                notebooksGenerated <- nbPath :: notebooksGenerated

            if not isEnOnly then
                let nbPtPath = Path.Combine(targetDir, "sample_mariadb_pt.fsqnb")
                File.WriteAllText(nbPtPath, getMariaDbNotebookPt ())
                notebooksGenerated <- nbPtPath :: notebooksGenerated

            registeredConns <- "MariaDB_Advanced" :: registeredConns

        if hasOracle then
            let oraScriptPath = Path.Combine(initScriptsDir, "init_oracle.sql")
            File.WriteAllText(oraScriptPath, getOracleInitSql ())
            initScripts <- oraScriptPath :: initScripts

            if not isPtOnly then
                let nbPath = Path.Combine(targetDir, "sample_oracle.fsqnb")
                File.WriteAllText(nbPath, getOracleNotebook ())
                notebooksGenerated <- nbPath :: notebooksGenerated

            if not isEnOnly then
                let nbPtPath = Path.Combine(targetDir, "sample_oracle_pt.fsqnb")
                File.WriteAllText(nbPtPath, getOracleNotebookPt ())
                notebooksGenerated <- nbPtPath :: notebooksGenerated

            registeredConns <- "Oracle_Advanced" :: registeredConns

        if hasAnyContainer then
            let composePath = Path.Combine(targetDir, "docker-compose.fsqs.yml")
            File.WriteAllText(composePath, getDockerComposeYamlWithEngines hasPostgres hasSqlServer hasMySql hasMariaDb hasOracle)
            dockerComposePath <- Some composePath

        { TargetDir = targetDir
          SelectedEngines = normalizedEngines
          DockerComposePath = dockerComposePath
          InitScripts = List.rev initScripts
          NotebooksGenerated = List.rev notebooksGenerated
          RegisteredConnections = List.rev registeredConns }

    let generateAdvancedSamples (targetDir: string) (selectedEngines: string list) : AdvancedSampleResult =
        generateAdvancedSamplesWithLanguage targetDir selectedEngines None
