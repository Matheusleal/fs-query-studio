namespace FSharpQueryStudio.Connectors

open System
open System.IO
open System.Text
open System.Text.Json
open System.Xml
open System.Xml.Linq
open ClosedXML.Excel

module SampleDataFiles =

    let createSampleCsv (filePath: string) : unit =
        let dir = Path.GetDirectoryName(filePath)

        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        use fs =
            new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite ||| FileShare.Delete, 65536)

        use writer = new StreamWriter(fs, Encoding.UTF8, 65536)

        writer.WriteLine(
            "Date,OrderId,Customer,Product,Category,Quantity,UnitPrice,TotalAmount,Status,PaymentMethod,SalesChannel,Region,DiscountRate,TaxAmount,GrossMargin,SalesRep,Notes"
        )

        let knownCsv =
            [|
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

        let customers =
            [|
                "Carlos Silva"
                "Alice Smith"
                "John Doe"
                "Maria Garcia"
                "Yuki Tanaka"
                "Ana Pereira"
            |]

        let products =
            [|
                "Laptop Pro 16"
                "Wireless Ergonomic Mouse"
                "Mechanical Keyboard RGB"
                "Noise-Cancelling Headphones"
                "Expert F# 4.0"
                "Domain Modeling Made Functional"
            |]

        let statuses = [| "Completed"; "Processing"; "Shipped"; "Pending" |]

        let paymentMethods =
            [| "Credit Card"; "Debit Card"; "PayPal"; "Wire Transfer"; "Pix" |]

        let channels = [| "Web"; "Mobile App"; "Marketplace"; "In-Store" |]
        let regions = [| "LATAM"; "EMEA"; "NA"; "APAC" |]

        let reps =
            [|
                "Camila Rocha"
                "Oliver Grant"
                "Sarah Jenkins"
                "Diego Morales"
                "Kenji Sato"
            |]

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

            writer.WriteLine(
                $"2026-04-10,{108 + i},{cust},{prod},{cat},{qty},{price:F2},{total:F2},{st},{pay},{chan},{reg},0.00,10.00,30.00,{rep},High volume feed row"
            )

    let createSampleCustomersCsv (filePath: string) : unit =
        let dir = Path.GetDirectoryName(filePath)

        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        use fs =
            new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite ||| FileShare.Delete, 65536)

        use writer = new StreamWriter(fs, Encoding.UTF8, 65536)

        writer.WriteLine(
            "CustomerId,CustomerName,LoyaltyTier,TotalSpent,Score,ActiveSince,Feedback,Email,Phone,Country,City,State,PostalCode,Segment,TotalOrders,AverageOrderValue,PreferredChannel"
        )

        let knownCustomersCsv =
            [|
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

        let countries =
            [| "Brazil"; "UK"; "USA"; "Spain"; "Japan"; "Germany"; "Canada"; "Australia" |]

        let cities =
            [|
                "São Paulo"
                "London"
                "New York"
                "Madrid"
                "Tokyo"
                "Berlin"
                "Toronto"
                "Sydney"
            |]

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

            writer.WriteLine(
                $"{i},Customer Profile {i},{tier},{spent:F2},{score:F1},2026-01-01,Consistent positive user experience feedback,cust{i}@domain.com,+1 555-01{i % 100:D2},{countries.[cIdx]},{cities.[cIdx]},Region,10001,{seg},{totalOrders},{aov:F2},{chan}"
            )

    let private saveWorkbookShared (workbook: XLWorkbook) (filePath: string) =
        use ms = new MemoryStream()
        workbook.SaveAs(ms)
        let bytes = ms.ToArray()

        use fs =
            new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite ||| FileShare.Delete)

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

        let targetHeaders =
            [|
                "Region"
                "Country"
                "AnnualTarget"
                "AchievedRevenue"
                "GrowthRate"
                "LeadManager"
                "FiscalYear"
                "TargetMargin"
                "ActualMargin"
                "PipelineCoverage"
                "QuotaAttainmentPct"
                "Status"
            |]

        let headersData: obj array = targetHeaders |> Array.map (fun h -> box h)
        targetsSheet.Cell(1, 1).InsertData([| headersData |]) |> ignore

        let knownTargets: obj array array =
            [|
                [|
                    box "LATAM"
                    box "Brazil"
                    box 850000.00M
                    box 912400.50M
                    box 14.5M
                    box "Camila Rocha"
                    box "2026"
                    box 25.0M
                    box 26.5M
                    box 3.2M
                    box 107.3M
                    box "Exceeded"
                |]
                [|
                    box "EMEA"
                    box "UK"
                    box 620000.00M
                    box 645800.25M
                    box 9.2M
                    box "Oliver Grant"
                    box "2026"
                    box 25.0M
                    box 26.5M
                    box 3.2M
                    box 107.3M
                    box "Exceeded"
                |]
                [|
                    box "NA"
                    box "USA"
                    box 1200000.00M
                    box 1185000.75M
                    box 11.8M
                    box "Sarah Jenkins"
                    box "2026"
                    box 25.0M
                    box 26.5M
                    box 3.2M
                    box 107.3M
                    box "Exceeded"
                |]
                [|
                    box "EMEA"
                    box "Spain"
                    box 410000.00M
                    box 438900.10M
                    box 12.4M
                    box "Diego Morales"
                    box "2026"
                    box 25.0M
                    box 26.5M
                    box 3.2M
                    box 107.3M
                    box "Exceeded"
                |]
                [|
                    box "APAC"
                    box "Japan"
                    box 530000.00M
                    box 559200.40M
                    box 10.6M
                    box "Kenji Sato"
                    box "2026"
                    box 25.0M
                    box 26.5M
                    box 3.2M
                    box 107.3M
                    box "Exceeded"
                |]
            |]

        targetsSheet.Cell(2, 1).InsertData(knownTargets) |> ignore

        let regions = [| "LATAM"; "EMEA"; "NA"; "APAC" |]

        let countries =
            [| "Brazil"; "UK"; "USA"; "Spain"; "Japan"; "Germany"; "Canada"; "Australia" |]

        let managers =
            [|
                "Camila Rocha"
                "Oliver Grant"
                "Sarah Jenkins"
                "Diego Morales"
                "Kenji Sato"
            |]

        let generatedTargets =
            Array.init (100001 - 7 + 1) (fun idx ->
                let r = idx + 7
                let reg = regions.[r % 4]
                let ctry = countries.[r % 8]
                let mgr = managers.[r % 5]
                let target = decimal ((r % 500) * 1000 + 100000)
                let growth = decimal ((r % 25) + 5)
                let achieved = target * (1.0M + growth / 100.0M)

                [|
                    box reg
                    box ctry
                    box target
                    box achieved
                    box growth
                    box mgr
                    box "2026"
                    box 22.0M
                    box 24.5M
                    box 2.8M
                    box 105.0M
                    box "On Track"
                |])

        targetsSheet.Cell(7, 1).InsertData(generatedTargets) |> ignore

        // Sheet 2: QuarterlyBudget (1,000 rows with expanded columns)
        let budgetSheet = workbook.Worksheets.Add("QuarterlyBudget")

        let budgetHeaders =
            [|
                "Quarter"
                "Department"
                "AllocatedBudget"
                "ActualSpend"
                "Status"
                "BudgetCode"
                "CostCenter"
                "ApprovedBy"
                "VarianceAmount"
                "CommittedSpend"
                "ForecastSpend"
            |]

        let bHeadersData: obj array = budgetHeaders |> Array.map (fun h -> box h)
        budgetSheet.Cell(1, 1).InsertData([| bHeadersData |]) |> ignore

        let knownBudgets: obj array array =
            [|
                [|
                    box "2026-Q1"
                    box "Engineering"
                    box 320000.00M
                    box 312500.00M
                    box "On Track"
                    box "BDG-0001"
                    box "CC-0100"
                    box "CFO Executive"
                    box 7500.00M
                    box 281250.00M
                    box 326400.00M
                |]
                [|
                    box "2026-Q1"
                    box "Cloud Infrastructure"
                    box 145000.00M
                    box 141200.50M
                    box "Optimized"
                    box "BDG-0002"
                    box "CC-0200"
                    box "CFO Executive"
                    box 3799.50M
                    box 127080.45M
                    box 147900.00M
                |]
                [|
                    box "2026-Q2"
                    box "Data Analytics"
                    box 210000.00M
                    box 198400.25M
                    box "Under Budget"
                    box "BDG-0003"
                    box "CC-0300"
                    box "CFO Executive"
                    box 11599.75M
                    box 178560.22M
                    box 214200.00M
                |]
                [|
                    box "2026-Q2"
                    box "Developer Relations"
                    box 95000.00M
                    box 94100.75M
                    box "On Track"
                    box "BDG-0004"
                    box "CC-0400"
                    box "CFO Executive"
                    box 899.25M
                    box 84690.67M
                    box 96900.00M
                |]
            |]

        budgetSheet.Cell(2, 1).InsertData(knownBudgets) |> ignore

        let quarters = [| "2026-Q1"; "2026-Q2"; "2026-Q3"; "2026-Q4" |]

        let depts =
            [|
                "Engineering"
                "Cloud Infrastructure"
                "Data Analytics"
                "Developer Relations"
                "Operations"
                "Security"
            |]

        let generatedBudgets =
            Array.init (1001 - 6 + 1) (fun idx ->
                let r = idx + 6
                let q = quarters.[r % 4]
                let d = depts.[r % 6]
                let alloc = decimal ((r % 100) * 5000 + 50000)
                let act = alloc * 0.96M

                [|
                    box q
                    box d
                    box alloc
                    box act
                    box "On Track"
                    box $"BDG-{r:D4}"
                    box $"CC-{r:D4}"
                    box "Finance Controller"
                    box (alloc - act)
                    box (act * 0.95M)
                    box alloc
                |])

        budgetSheet.Cell(6, 1).InsertData(generatedBudgets) |> ignore

        saveWorkbookShared workbook filePath

    let createSampleCommerceExcelWorkbook (filePath: string) : unit =
        let dir = Path.GetDirectoryName(filePath)

        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        use workbook = new XLWorkbook()

        // Sheet 1: Customers (5,000 rows with expanded columns)
        let customersSheet = workbook.Worksheets.Add("Customers")

        let customerHeaders =
            [|
                "CustomerId"
                "FullName"
                "Email"
                "Country"
                "City"
                "CustomerSegment"
                "Phone"
                "PostalCode"
                "CreditLimit"
                "RegisteredDate"
                "AccountStatus"
                "LoyaltyPoints"
            |]

        let cHeadersData: obj array = customerHeaders |> Array.map (fun h -> box h)
        customersSheet.Cell(1, 1).InsertData([| cHeadersData |]) |> ignore

        let knownCustomers: obj array array =
            [|
                [|
                    box 1
                    box "Emma Watson"
                    box "emma.watson@ukmail.co.uk"
                    box "UK"
                    box "London"
                    box "Enterprise"
                    box "+1 555-0199"
                    box "10001"
                    box 50000.0M
                    box "2025-01-15"
                    box "Active"
                    box 2500
                |]
                [|
                    box 2
                    box "Liam Carter"
                    box "liam.carter@techcorp.com"
                    box "USA"
                    box "Austin"
                    box "Enterprise"
                    box "+1 555-0199"
                    box "10001"
                    box 50000.0M
                    box "2025-01-15"
                    box "Active"
                    box 2500
                |]
                [|
                    box 3
                    box "Lucas Andrade"
                    box "lucas.andrade@brshop.com.br"
                    box "Brazil"
                    box "Sao Paulo"
                    box "SMB"
                    box "+1 555-0199"
                    box "10001"
                    box 50000.0M
                    box "2025-01-15"
                    box "Active"
                    box 2500
                |]
                [|
                    box 4
                    box "Sofia Martinez"
                    box "sofia.m@madridretail.es"
                    box "Spain"
                    box "Madrid"
                    box "SMB"
                    box "+1 555-0199"
                    box "10001"
                    box 50000.0M
                    box "2025-01-15"
                    box "Active"
                    box 2500
                |]
                [|
                    box 5
                    box "Kenji Takahashi"
                    box "kenji.t@tokyocloud.jp"
                    box "Japan"
                    box "Tokyo"
                    box "Enterprise"
                    box "+1 555-0199"
                    box "10001"
                    box 50000.0M
                    box "2025-01-15"
                    box "Active"
                    box 2500
                |]
            |]

        customersSheet.Cell(2, 1).InsertData(knownCustomers) |> ignore

        let countries =
            [| "UK"; "USA"; "Brazil"; "Spain"; "Japan"; "Germany"; "France"; "Canada" |]

        let cities =
            [|
                "London"
                "Austin"
                "Sao Paulo"
                "Madrid"
                "Tokyo"
                "Berlin"
                "Paris"
                "Toronto"
            |]

        let segments = [| "Enterprise"; "SMB"; "Consumer"; "Startup" |]

        let generatedCustomers =
            Array.init (5001 - 7 + 1) (fun idx ->
                let r = idx + 7
                let cIdx = r % 8

                [|
                    box (r - 1)
                    box $"Enterprise Customer {r - 1}"
                    box $"client{r - 1}@corporation.com"
                    box countries.[cIdx]
                    box cities.[cIdx]
                    box segments.[r % 4]
                    box $"+1 555-{r:D4}"
                    box $"{20000 + (r % 70000)}"
                    box 25000.0M
                    box "2025-06-01"
                    box "Active"
                    box ((r * 20) % 5000)
                |])

        customersSheet.Cell(7, 1).InsertData(generatedCustomers) |> ignore

        // Sheet 2: ProductsStock (5,000 rows with expanded columns)
        let productsSheet = workbook.Worksheets.Add("ProductsStock")

        let productHeaders =
            [|
                "ProductId"
                "Sku"
                "ProductName"
                "Category"
                "UnitPrice"
                "StockQuantity"
                "WarehouseZone"
                "Brand"
                "CostPrice"
                "MarginPercent"
                "ReorderThreshold"
                "MaxStockCapacity"
                "SupplierId"
            |]

        let pHeadersData: obj array = productHeaders |> Array.map (fun h -> box h)
        productsSheet.Cell(1, 1).InsertData([| pHeadersData |]) |> ignore

        let knownProducts: obj array array =
            [|
                [|
                    box 101
                    box "HW-LAP-16"
                    box "Ultrabook Pro 16"
                    box "Hardware"
                    box 1599.00M
                    box 45
                    box "Zone-A"
                    box "OmniTech"
                    box 959.40M
                    box 40.0M
                    box 20
                    box 500
                    box 1
                |]
                [|
                    box 102
                    box "HW-MON-27"
                    box "4K IPS Monitor 27"
                    box "Hardware"
                    box 449.50M
                    box 80
                    box "Zone-A"
                    box "OmniTech"
                    box 269.70M
                    box 40.0M
                    box 20
                    box 500
                    box 1
                |]
                [|
                    box 103
                    box "ACC-KBD-01"
                    box "Mechanical Tactile Keyboard"
                    box "Accessories"
                    box 129.00M
                    box 150
                    box "Zone-B"
                    box "OmniTech"
                    box 77.40M
                    box 40.0M
                    box 20
                    box 500
                    box 1
                |]
                [|
                    box 104
                    box "ACC-MSE-02"
                    box "Ergonomic Wireless Mouse"
                    box "Accessories"
                    box 69.90M
                    box 210
                    box "Zone-B"
                    box "OmniTech"
                    box 41.94M
                    box 40.0M
                    box 20
                    box 500
                    box 1
                |]
                [|
                    box 105
                    box "NET-RTR-05"
                    box "Wi-Fi 7 Mesh Router"
                    box "Networking"
                    box 299.00M
                    box 60
                    box "Zone-C"
                    box "OmniTech"
                    box 179.40M
                    box 40.0M
                    box 20
                    box 500
                    box 1
                |]
            |]

        productsSheet.Cell(2, 1).InsertData(knownProducts) |> ignore

        let categories =
            [| "Hardware"; "Accessories"; "Networking"; "Storage"; "Peripherals" |]

        let zones = [| "Zone-A"; "Zone-B"; "Zone-C"; "Zone-D" |]

        let generatedProducts =
            Array.init (5001 - 7 + 1) (fun idx ->
                let r = idx + 7
                let pid = 100 + r - 1
                let price = decimal ((r % 300) + 20) + 0.99M

                [|
                    box pid
                    box $"SKU-OPS-{pid:D5}"
                    box $"Enterprise Hardware {pid}"
                    box categories.[r % 5]
                    box price
                    box ((r % 200) + 15)
                    box zones.[r % 4]
                    box "OmniTech"
                    box (price * 0.6M)
                    box 40.0M
                    box 25
                    box 600
                    box ((r % 10) + 1)
                |])

        productsSheet.Cell(7, 1).InsertData(generatedProducts) |> ignore

        // Sheet 3: Sales (100,000 rows with expanded columns)
        let salesSheet = workbook.Worksheets.Add("Sales")

        let salesHeaders =
            [|
                "SaleId"
                "CustomerId"
                "ProductId"
                "Quantity"
                "TotalAmount"
                "SaleDate"
                "PaymentMethod"
                "InvoiceNumber"
                "Discount"
                "NetAmount"
                "TaxRate"
                "SalesChannel"
                "Currency"
            |]

        let sHeadersData: obj array = salesHeaders |> Array.map (fun h -> box h)
        salesSheet.Cell(1, 1).InsertData([| sHeadersData |]) |> ignore

        let knownSales: obj array array =
            [|
                [|
                    box 1001
                    box 1
                    box 101
                    box 2
                    box 3198.00M
                    box "2026-03-10"
                    box "Corporate Card"
                    box "INV-2026-1001"
                    box 0.0M
                    box 3198.00M
                    box 0.08M
                    box "Enterprise Direct"
                    box "USD"
                |]
                [|
                    box 1002
                    box 2
                    box 102
                    box 3
                    box 1348.50M
                    box "2026-03-12"
                    box "Wire Transfer"
                    box "INV-2026-1002"
                    box 0.0M
                    box 1348.50M
                    box 0.08M
                    box "Enterprise Direct"
                    box "USD"
                |]
                [|
                    box 1003
                    box 3
                    box 103
                    box 5
                    box 645.00M
                    box "2026-03-15"
                    box "Credit Card"
                    box "INV-2026-1003"
                    box 0.0M
                    box 645.00M
                    box 0.08M
                    box "Enterprise Direct"
                    box "USD"
                |]
                [|
                    box 1004
                    box 4
                    box 105
                    box 2
                    box 598.00M
                    box "2026-03-18"
                    box "Credit Card"
                    box "INV-2026-1004"
                    box 0.0M
                    box 598.00M
                    box 0.08M
                    box "Enterprise Direct"
                    box "USD"
                |]
                [|
                    box 1005
                    box 5
                    box 101
                    box 1
                    box 1599.00M
                    box "2026-03-20"
                    box "Wire Transfer"
                    box "INV-2026-1005"
                    box 0.0M
                    box 1599.00M
                    box 0.08M
                    box "Enterprise Direct"
                    box "USD"
                |]
                [|
                    box 1006
                    box 1
                    box 104
                    box 4
                    box 279.60M
                    box "2026-03-22"
                    box "Corporate Card"
                    box "INV-2026-1006"
                    box 0.0M
                    box 279.60M
                    box 0.08M
                    box "Enterprise Direct"
                    box "USD"
                |]
            |]

        salesSheet.Cell(2, 1).InsertData(knownSales) |> ignore

        let payMethods =
            [| "Corporate Card"; "Wire Transfer"; "Credit Card"; "Direct Debit" |]

        let channels =
            [| "Enterprise Direct"; "Partner Network"; "Web Portal"; "Reseller" |]

        let generatedSales =
            Array.init (100001 - 8 + 1) (fun idx ->
                let r = idx + 8
                let sid = 1000 + r - 1
                let cid = (r % 5000) + 1
                let pid = 100 + (r % 5000) + 1
                let qty = (r % 4) + 1
                let total = decimal ((r % 400) * 10 + 50) + 0.50M

                [|
                    box sid
                    box cid
                    box pid
                    box qty
                    box total
                    box "2026-03-25"
                    box payMethods.[r % 4]
                    box $"INV-2026-{sid}"
                    box 15.0M
                    box (total - 15.0M)
                    box 0.08M
                    box channels.[r % 4]
                    box "USD"
                |])

        salesSheet.Cell(8, 1).InsertData(generatedSales) |> ignore

        // Sheet 4: DeliveryLogs (5,000 rows with expanded columns)
        let deliverySheet = workbook.Worksheets.Add("DeliveryLogs")

        let deliveryHeaders =
            [|
                "DeliveryId"
                "SaleId"
                "Carrier"
                "TrackingCode"
                "DeliveryStatus"
                "DispatchedDate"
                "EstimatedArrival"
                "OriginHub"
                "DestinationCity"
                "ShippingCost"
                "WeightKg"
                "DeliveryAttempts"
                "ActualArrival"
            |]

        let dHeadersData: obj array = deliveryHeaders |> Array.map (fun h -> box h)
        deliverySheet.Cell(1, 1).InsertData([| dHeadersData |]) |> ignore

        let knownDeliveries: obj array array =
            [|
                [|
                    box 5001
                    box 1001
                    box "DHL Express"
                    box "DHL-99201-UK"
                    box "Delivered"
                    box "2026-03-11"
                    box "2026-03-13"
                    box "Main Logistics Center"
                    box "London"
                    box 45.0M
                    box 12.5M
                    box 1
                    box "2026-03-13"
                |]
                [|
                    box 5002
                    box 1002
                    box "FedEx Priority"
                    box "FDX-88412-US"
                    box "Delivered"
                    box "2026-03-13"
                    box "2026-03-15"
                    box "Main Logistics Center"
                    box "London"
                    box 45.0M
                    box 12.5M
                    box 1
                    box "2026-03-15"
                |]
                [|
                    box 5003
                    box 1003
                    box "UPS Worldwide"
                    box "UPS-77109-BR"
                    box "In Transit"
                    box "2026-03-16"
                    box "2026-03-21"
                    box "Main Logistics Center"
                    box "London"
                    box 45.0M
                    box 12.5M
                    box 1
                    box "2026-03-21"
                |]
                [|
                    box 5004
                    box 1004
                    box "DHL Express"
                    box "DHL-66320-ES"
                    box "Delivered"
                    box "2026-03-19"
                    box "2026-03-21"
                    box "Main Logistics Center"
                    box "London"
                    box 45.0M
                    box 12.5M
                    box 1
                    box "2026-03-21"
                |]
                [|
                    box 5005
                    box 1005
                    box "Yamato Global"
                    box "YMT-55401-JP"
                    box "In Transit"
                    box "2026-03-21"
                    box "2026-03-24"
                    box "Main Logistics Center"
                    box "London"
                    box 45.0M
                    box 12.5M
                    box 1
                    box "2026-03-24"
                |]
                [|
                    box 5006
                    box 1006
                    box "DHL Express"
                    box "DHL-99540-UK"
                    box "Out for Delivery"
                    box "2026-03-23"
                    box "2026-03-25"
                    box "Main Logistics Center"
                    box "London"
                    box 45.0M
                    box 12.5M
                    box 1
                    box "2026-03-25"
                |]
            |]

        deliverySheet.Cell(2, 1).InsertData(knownDeliveries) |> ignore

        let carriers =
            [| "DHL Express"; "FedEx Priority"; "UPS Worldwide"; "Yamato Global" |]

        let deliveryStatuses =
            [| "Delivered"; "In Transit"; "Out for Delivery"; "Customs Cleared" |]

        let generatedDeliveries =
            Array.init (5001 - 8 + 1) (fun idx ->
                let r = idx + 8
                let did = 5000 + r - 1
                let sid = 1000 + r - 1
                let carrier = carriers.[r % 4]
                let status = deliveryStatuses.[r % 4]

                [|
                    box did
                    box sid
                    box carrier
                    box $"TRK-OPS-{did:D6}"
                    box status
                    box "2026-03-25"
                    box "2026-03-28"
                    box "Global Air Cargo Hub"
                    box "Destination City"
                    box 35.0M
                    box 8.5M
                    box 1
                    box "2026-03-28"
                |])

        deliverySheet.Cell(8, 1).InsertData(generatedDeliveries) |> ignore

        saveWorkbookShared workbook filePath

    let createSampleJsonFile (filePath: string) : unit =
        let dir = Path.GetDirectoryName(filePath)

        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        use fs =
            new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite ||| FileShare.Delete, 65536)

        use writer = new Utf8JsonWriter(fs, JsonWriterOptions(Indented = false))
        writer.WriteStartObject()

        // 1. Products (100,000 items with expanded attributes)
        writer.WriteStartArray("Products")

        let knownProducts =
            [|
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

        let categories =
            [| "AI Hardware"; "Networking"; "Peripherals"; "Storage"; "Compute Modules" |]

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

        let knownReviews =
            [|
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

        use fs =
            new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite ||| FileShare.Delete, 65536)

        use writer =
            XmlWriter.Create(fs, XmlWriterSettings(OmitXmlDeclaration = false, Encoding = Encoding.UTF8))

        writer.WriteStartDocument()
        writer.WriteStartElement("LogisticsHub")

        // 1. Carriers (1,000 carriers with expanded attributes)
        writer.WriteStartElement("Carriers")

        let knownCarriers =
            [|
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

        let countries =
            [| "Germany"; "USA"; "Brazil"; "Japan"; "UK"; "France"; "Singapore"; "Canada" |]

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

        let knownShipments =
            [|
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

        let cities =
            [|
                "Sao Paulo"
                "London"
                "Madrid"
                "Tokyo"
                "Berlin"
                "New York"
                "Paris"
                "Singapore"
            |]

        let shipmentStatuses =
            [| "Delivered"; "In Transit"; "Customs Cleared"; "Out for Delivery" |]

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

