namespace FSharpQueryStudio.Connectors

open System
open System.Text

module AdvancedSampleInitScripts =

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
ALTER SESSION SET CONTAINER = FREEPDB1;
ALTER SESSION SET CURRENT_SCHEMA = SYSTEM;

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
