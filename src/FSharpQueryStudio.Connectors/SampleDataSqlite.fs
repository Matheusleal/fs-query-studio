namespace FSharpQueryStudio.Connectors

open System
open System.IO
open Microsoft.Data.Sqlite

module SampleDataSqlite =

    let private createTablesSql =
        """
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

CREATE INDEX IF NOT EXISTS IX_Customers_Country ON Customers (Country, FirstName);
CREATE INDEX IF NOT EXISTS IX_Customers_Email ON Customers (Email);
CREATE INDEX IF NOT EXISTS IX_Orders_CustomerId ON Orders (CustomerId);
CREATE INDEX IF NOT EXISTS IX_Orders_Status ON Orders (Status);
CREATE INDEX IF NOT EXISTS IX_Orders_OrderDate ON Orders (OrderDate);
CREATE INDEX IF NOT EXISTS IX_OrderItems_OrderId ON OrderItems (OrderId);
CREATE INDEX IF NOT EXISTS IX_OrderItems_ProductId ON OrderItems (ProductId);
CREATE INDEX IF NOT EXISTS IX_Products_CategoryId ON Products (CategoryId);
"""

    let private createInventoryTablesSql =
        """
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

CREATE INDEX IF NOT EXISTS IX_StockLevels_WarehouseId ON StockLevels (WarehouseId);
CREATE INDEX IF NOT EXISTS IX_StockLevels_ProductSku ON StockLevels (ProductSku);
CREATE INDEX IF NOT EXISTS IX_Suppliers_Country ON Suppliers (Country);
"""

    let private createIndexesSql =
        """
CREATE INDEX IF NOT EXISTS IX_Customers_Country ON Customers (Country, FirstName);
CREATE INDEX IF NOT EXISTS IX_Customers_Email ON Customers (Email);
CREATE INDEX IF NOT EXISTS IX_Orders_CustomerId ON Orders (CustomerId);
CREATE INDEX IF NOT EXISTS IX_Orders_Status ON Orders (Status);
CREATE INDEX IF NOT EXISTS IX_Orders_OrderDate ON Orders (OrderDate);
CREATE INDEX IF NOT EXISTS IX_OrderItems_OrderId ON OrderItems (OrderId);
CREATE INDEX IF NOT EXISTS IX_OrderItems_ProductId ON OrderItems (ProductId);
CREATE INDEX IF NOT EXISTS IX_Products_CategoryId ON Products (CategoryId);
"""

    let private createInventoryIndexesSql =
        """
CREATE INDEX IF NOT EXISTS IX_StockLevels_WarehouseId ON StockLevels (WarehouseId);
CREATE INDEX IF NOT EXISTS IX_StockLevels_ProductSku ON StockLevels (ProductSku);
CREATE INDEX IF NOT EXISTS IX_Suppliers_Country ON Suppliers (Country);
"""

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

        catCmd.CommandText <-
            "INSERT OR REPLACE INTO Categories VALUES (@id, @name, @desc, @code, @dept, @active, @created);"

        let pCatId = catCmd.Parameters.Add("@id", SqliteType.Integer)
        let pCatName = catCmd.Parameters.Add("@name", SqliteType.Text)
        let pCatDesc = catCmd.Parameters.Add("@desc", SqliteType.Text)
        let pCatCode = catCmd.Parameters.Add("@code", SqliteType.Text)
        let pCatDept = catCmd.Parameters.Add("@dept", SqliteType.Text)
        let pCatActive = catCmd.Parameters.Add("@active", SqliteType.Integer)
        let pCatCreated = catCmd.Parameters.Add("@created", SqliteType.Text)

        let knownCategories =
            [|
                (1, "Electronics", "Gadgets, devices, and computer peripherals", "CAT-ELEC", "Technology")
                (2, "Books", "Technical and programming books", "CAT-BOOK", "Media")
                (3, "Home & Kitchen", "Appliances and household items", "CAT-HOME", "Home")
                (4, "Sports & Fitness", "Sports apparel, footwear, and accessories", "CAT-SPRT", "Leisure")
            |]

        for id, name, desc, code, dept in knownCategories do
            pCatId.Value <- id
            pCatName.Value <- name
            pCatDesc.Value <- desc
            pCatCode.Value <- code
            pCatDept.Value <- dept
            pCatActive.Value <- 1
            pCatCreated.Value <- "2025-01-01T00:00:00Z"
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

        prodCmd.CommandText <-
            "INSERT OR REPLACE INTO Products VALUES (@id, @name, @catId, @price, @stock, @sku, @brand, @cost, @weight, @disc, @rating);"

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

        let knownProducts =
            [|
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
            pProdId.Value <- id
            pProdName.Value <- name
            pProdCatId.Value <- catId
            pProdPrice.Value <- price
            pProdStock.Value <- stock
            pProdSku.Value <- sku
            pProdBrand.Value <- brand
            pProdCost.Value <- cost
            pProdWeight.Value <- weight
            pProdDisc.Value <- disc
            pProdRating.Value <- rating
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

        custCmd.CommandText <-
            "INSERT OR REPLACE INTO Customers VALUES (@id, @fn, @ln, @email, @city, @country, @created, @phone, @post, @state, @points, @type, @active);"

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

        let knownCustomers =
            [|
                (1,
                 "Carlos",
                 "Silva",
                 "carlos.silva@example.com",
                 "São Paulo",
                 "Brazil",
                 "2025-11-10T10:00:00Z",
                 "+55 11 98765-4321",
                 "01310-100",
                 "SP",
                 2500,
                 "Platinum",
                 1)
                (2,
                 "Alice",
                 "Smith",
                 "alice.smith@example.co.uk",
                 "London",
                 "UK",
                 "2025-12-01T14:30:00Z",
                 "+44 20 7946 0912",
                 "EC1A 1BB",
                 "Greater London",
                 1800,
                 "Gold",
                 1)
                (3,
                 "John",
                 "Doe",
                 "john.doe@example.com",
                 "New York",
                 "USA",
                 "2026-01-05T09:15:00Z",
                 "+1 212-555-0143",
                 "10001",
                 "NY",
                 950,
                 "Silver",
                 1)
                (4,
                 "Maria",
                 "Garcia",
                 "maria.garcia@example.es",
                 "Madrid",
                 "Spain",
                 "2026-01-12T16:45:00Z",
                 "+34 91 123 4567",
                 "28001",
                 "Madrid",
                 1450,
                 "Gold",
                 1)
                (5,
                 "Yuki",
                 "Tanaka",
                 "yuki.tanaka@example.jp",
                 "Tokyo",
                 "Japan",
                 "2026-02-01T11:20:00Z",
                 "+81 3 5555 0198",
                 "100-0001",
                 "Tokyo",
                 800,
                 "Silver",
                 1)
                (6,
                 "Ana",
                 "Pereira",
                 "ana.pereira@example.com",
                 "Rio de Janeiro",
                 "Brazil",
                 "2026-02-18T13:10:00Z",
                 "+55 21 99876-5432",
                 "20040-002",
                 "RJ",
                 650,
                 "Bronze",
                 1)
            |]

        for id, fn, ln, email, city, country, created, phone, post, state, points, ctype, active in knownCustomers do
            pCustId.Value <- id
            pCustFn.Value <- fn
            pCustLn.Value <- ln
            pCustEmail.Value <- email
            pCustCity.Value <- city
            pCustCountry.Value <- country
            pCustCreated.Value <- created
            pCustPhone.Value <- phone
            pCustPost.Value <- post
            pCustState.Value <- state
            pCustPoints.Value <- points
            pCustType.Value <- ctype
            pCustActive.Value <- active
            custCmd.ExecuteNonQuery() |> ignore

        let countries =
            [| "USA"; "UK"; "Spain"; "Japan"; "Germany"; "Canada"; "Australia"; "France" |]

        let cities =
            [|
                "New York"
                "London"
                "Madrid"
                "Tokyo"
                "Berlin"
                "Toronto"
                "Sydney"
                "Paris"
            |]

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

        ordCmd.CommandText <-
            "INSERT OR REPLACE INTO Orders VALUES (@id, @cid, @date, @total, @status, @num, @pay, @ship, @disc, @cur, @chan, @notes);"

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

        let knownOrders =
            [|
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
            pOrdId.Value <- id
            pOrdCid.Value <- cid
            pOrdDate.Value <- dt
            pOrdTotal.Value <- tot
            pOrdStatus.Value <- st
            pOrdNum.Value <- $"ORD-2026-{id:D6}"
            pOrdPay.Value <- "Credit Card"
            pOrdShip.Value <- 15.0
            pOrdDisc.Value <- 0.0
            pOrdCur.Value <- "USD"
            pOrdChan.Value <- "Web"
            pOrdNotes.Value <- "Express standard delivery"
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

        itemCmd.CommandText <-
            "INSERT OR REPLACE INTO OrderItems VALUES (@id, @oid, @pid, @qty, @price, @disc, @total, @tax, @sku);"

        let pItId = itemCmd.Parameters.Add("@id", SqliteType.Integer)
        let pItOid = itemCmd.Parameters.Add("@oid", SqliteType.Integer)
        let pItPid = itemCmd.Parameters.Add("@pid", SqliteType.Integer)
        let pItQty = itemCmd.Parameters.Add("@qty", SqliteType.Integer)
        let pItPrice = itemCmd.Parameters.Add("@price", SqliteType.Real)
        let pItDisc = itemCmd.Parameters.Add("@disc", SqliteType.Real)
        let pItTotal = itemCmd.Parameters.Add("@total", SqliteType.Real)
        let pItTax = itemCmd.Parameters.Add("@tax", SqliteType.Real)
        let pItSku = itemCmd.Parameters.Add("@sku", SqliteType.Text)

        let knownItems =
            [|
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
            pItId.Value <- id
            pItOid.Value <- oid
            pItPid.Value <- pid
            pItQty.Value <- qty
            pItPrice.Value <- price
            pItDisc.Value <- 0.0
            pItTotal.Value <- price * float qty
            pItTax.Value <- price * 0.08
            pItSku.Value <- $"SKU-PRD-{pid:D5}"
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

        use idxCmd = connection.CreateCommand()
        idxCmd.CommandText <- createIndexesSql
        idxCmd.ExecuteNonQuery() |> ignore

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

        whCmd.CommandText <-
            "INSERT OR REPLACE INTO Warehouses VALUES (@id, @code, @city, @country, @cap, @name, @addr, @post, @mgr, @stat, @refcap);"

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

        let knownWarehouses =
            [|
                (1,
                 "WH-SP-01",
                 "São Paulo",
                 "Brazil",
                 50000,
                 "Paulista Hub Central",
                 "Av. Paulista, 1000",
                 "01310-100",
                 "Rodrigo Alves",
                 "Active",
                 15000)
                (2,
                 "WH-LON-01",
                 "London",
                 "UK",
                 35000,
                 "Thames Logistics Gateway",
                 "12 Royal Docks",
                 "E16 2QU",
                 "Victoria Sterling",
                 "Active",
                 10000)
                (3,
                 "WH-NYC-01",
                 "New York",
                 "USA",
                 45000,
                 "Hudson River Distribution",
                 "500 West 34th St",
                 "10001",
                 "Michael Chang",
                 "Active",
                 12000)
                (4,
                 "WH-MAD-01",
                 "Madrid",
                 "Spain",
                 25000,
                 "Iberia Air Express Hub",
                 "Calle de Alcala, 45",
                 "28014",
                 "Elena Fernandez",
                 "Active",
                 8000)
            |]

        for id, code, city, country, cap, name, addr, post, mgr, stat, refcap in knownWarehouses do
            pWhId.Value <- id
            pWhCode.Value <- code
            pWhCity.Value <- city
            pWhCountry.Value <- country
            pWhCap.Value <- cap
            pWhName.Value <- name
            pWhAddr.Value <- addr
            pWhPost.Value <- post
            pWhMgr.Value <- mgr
            pWhStat.Value <- stat
            pWhRefCap.Value <- refcap
            whCmd.ExecuteNonQuery() |> ignore

        let whCities =
            [|
                "São Paulo"
                "London"
                "New York"
                "Madrid"
                "Tokyo"
                "Berlin"
                "Paris"
                "Singapore"
            |]

        let whCountries =
            [| "Brazil"; "UK"; "USA"; "Spain"; "Japan"; "Germany"; "France"; "Singapore" |]

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

        stockCmd.CommandText <-
            "INSERT OR REPLACE INTO StockLevels VALUES (@id, @wid, @sku, @qty, @reorder, @restocked, @bin, @res, @avail, @cost, @val, @safety, @stat);"

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

        let knownStocks =
            [|
                (1, 1, "LAPTOP-PRO-16", 45, 10, "2026-03-01T08:00:00Z")
                (2, 1, "MOUSE-WL-ERG", 220, 50, "2026-03-05T09:30:00Z")
                (3, 2, "BOOK-EXP-FS4", 80, 20, "2026-02-28T14:00:00Z")
                (4, 2, "BOOK-DM-FUNC", 110, 30, "2026-03-10T11:15:00Z")
                (5, 3, "HEADPHONES-NC", 65, 15, "2026-03-12T16:00:00Z")
                (6, 4, "SHOES-PRO-RUN", 90, 25, "2026-03-15T10:00:00Z")
            |]

        for id, wid, sku, qty, reorder, restocked in knownStocks do
            pStkId.Value <- id
            pStkWid.Value <- wid
            pStkSku.Value <- sku
            pStkQty.Value <- qty
            pStkReorder.Value <- reorder
            pStkRestocked.Value <- restocked
            pStkBin.Value <- $"BIN-{wid:D2}-A01"
            pStkRes.Value <- 5
            pStkAvail.Value <- qty - 5
            pStkCost.Value <- 100.0
            pStkVal.Value <- float qty * 100.0
            pStkSafety.Value <- reorder * 2
            pStkStat.Value <- "Optimal"
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

        supCmd.CommandText <-
            "INSERT OR REPLACE INTO Suppliers VALUES (@id, @comp, @email, @country, @rating, @cname, @phone, @city, @terms, @tax, @pref);"

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

        let knownSuppliers =
            [|
                (1, "Global Tech Logistics", "contact@globaltech.com", "Brazil", 4.8)
                (2, "EuroPublishing Group", "supply@europub.co.uk", "UK", 4.9)
                (3, "Apex Electronics Corp", "sales@apexelectronics.com", "USA", 4.6)
                (4, "Iberia Sports Supply", "info@iberiasports.es", "Spain", 4.7)
            |]

        for id, comp, email, country, rating in knownSuppliers do
            pSupId.Value <- id
            pSupComp.Value <- comp
            pSupEmail.Value <- email
            pSupCountry.Value <- country
            pSupRating.Value <- rating
            pSupCName.Value <- "Key Account Lead"
            pSupPhone.Value <- "+1 800-555-0199"
            pSupCity.Value <- "Logistics Center"
            pSupTerms.Value <- 30
            pSupTax.Value <- $"TAX-{id:D6}"
            pSupPref.Value <- 1
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

        use idxCmd = connection.CreateCommand()
        idxCmd.CommandText <- createInventoryIndexesSql
        idxCmd.ExecuteNonQuery() |> ignore

        SqliteConnection.ClearAllPools()

    let ensureSampleStoreIndexes (filePath: string) : unit =
        try
            if File.Exists filePath then
                use connection = new SqliteConnection($"Data Source={filePath};Pooling=False")
                connection.Open()
                use cmd = connection.CreateCommand()
                cmd.CommandText <- createIndexesSql
                cmd.ExecuteNonQuery() |> ignore
        with _ ->
            ()

    let ensureInventoryIndexes (filePath: string) : unit =
        try
            if File.Exists filePath then
                use connection = new SqliteConnection($"Data Source={filePath};Pooling=False")
                connection.Open()
                use cmd = connection.CreateCommand()
                cmd.CommandText <- createInventoryIndexesSql
                cmd.ExecuteNonQuery() |> ignore
        with _ ->
            ()
