namespace FSharpQueryStudio.Connectors

open AdvancedSampleNotebookCommon

module AdvancedSampleNotebooksSqlServer =

    let getSqlServerNotebook () : string =
        let cells =
            [
                mkMarkdown
                    """
# 🛡️ SQL Server (T-SQL) Advanced FSQL Showcase & Diagnostics

This notebook demonstrates the full power of **FSQL** pushed down to **SQL Server 2022**:
- **Analytical Window Functions** (`ROW_NUMBER`, `RANK`, `DENSE_RANK`, running aggregates)
- **T-SQL 412 Mitigation** (Safe auto-injected `ORDER BY (SELECT NULL)`)
- **Positional & Value Window Functions** (`lag`, `lead`, `firstValue`, `lastValue`, running `avg`, `max`)
- **Semi-Structured JSON Navigation** (`fsql.jsonValue` -> `JSON_VALUE`)
- **Correlated Subqueries** (`whereExists`, `notExists`, scalar subqueries in projection)
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkMarkdown "## 4. 🎯 Correlated Subqueries (`whereExists`, `notExists` & Scalar Projection)"
                mkCode
                    """
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
                mkCode
                    """
// 4.2 Customers with NO pending orders using notExists
fsql {
    for c in Connections.sql.SqlServer_Advanced.Customers do
    where (notExists (fsql { for o in Connections.sql.SqlServer_Advanced.Orders do where (o.CustomerId = c.Id && o.Status = "Pending") }))
    select {|
        Customer = c.FirstName + " " + c.LastName
        City = c.City
        Country = c.Country
    |}
}
"""
                mkMarkdown "## 5. 📊 Grouping & Aggregate Filter (`having`)"
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkMarkdown
                    """
## 10. 🌐 Multi-Table Relational Projection & Whole Table Wildcards (`alias.*`)

Join multiple relational tables and project selected scalar fields, or project entire mapped tables (e.g. `Customer = c`) alongside scalar columns to emit `${alias}.*` in SQL pushdown.
"""
                mkCode
                    """
// 10. Multi-table join with whole mapped table wildcard projection (Customer = fsql.extract c) alongside order details
fsql {
    for o in Connections.sql.SqlServer_Advanced.Orders do
    join c in Connections.sql.SqlServer_Advanced.Customers on (o.CustomerId = c.Id)
    where (o.Status = "Completed")
    take 10
    select {|
        Customer = fsql.extract c
        OrderId = o.Id
        OrderDate = o.OrderDate
        TotalAmount = o.TotalAmount
    |}
}
"""
                mkMarkdown "## 11. 🔀 Conditional Expressions (CASE WHEN) & String Functions"
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkMarkdown "## 14. 🛡️ Null & Empty Filtering (`isNotNull`, `isNotEmpty`, `isNotNullOrEmpty`)"
                mkCode
                    """
// 14. Filter non-null and non-empty columns with full SQL pushdown in SQL Server
fsql {
    for c in Connections.sql.SqlServer_Advanced.Customers do
    where (isNotNull c.Email && isNotEmpty c.City && isNotNullOrEmpty c.Country)
    select {|
        Id = c.Id
        Customer = c.FirstName + " " + c.LastName
        Email = c.Email
        City = c.City
        Country = c.Country
    |}
}
"""
                mkMarkdown
                    "## 15. 🌳 Multi-Database Hierarchical Tree Mapping (`Connections.sql.<Conn>.<Database>.<Table>`)"
                mkCode
                    """
// 15. Query tables directly via the discovered database tree hierarchy (master)
fsql {
    for p in Connections.sql.SqlServer_Advanced.master.Products do
    join c in Connections.sql.SqlServer_Advanced.master.Categories on (p.CategoryId = c.Id)
    where (isNotEmpty p.Sku)
    sortByDescending p.Price
    select {|
        Sku = p.Sku
        Product = p.Name
        Category = c.Name
        Price = p.Price
    |}
}
"""
            ]

        serializeNotebook cells



    let getSqlServerNotebookPt () : string =
        let cells =
            [
                mkMarkdown
                    """
# 🛡️ Demonstração Avançada e Diagnósticos do FSQL com SQL Server (T-SQL)

Este notebook demonstra todo o poder do **FSQL** com pushdown no **SQL Server 2022**:
- **Funções de Janela Analíticas** (`ROW_NUMBER`, `RANK`, `DENSE_RANK`, agregações acumuladas)
- **Mitigação T-SQL 412** (`ORDER BY (SELECT NULL)` seguro injetado automaticamente quando necessário)
- **Funções Posicionais e de Valor** (`lag`, `lead`, `firstValue`, `lastValue`, `avg` e `max` acumulados)
- **Navegação em JSON Semi-Estruturado** (`fsql.jsonValue` -> `JSON_VALUE`)
- **Subqueries Correlacionadas** (`whereExists`, `notExists`, subqueries escalares na projeção)
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkMarkdown "## 4. 🎯 Subqueries Correlacionadas (`whereExists`, `notExists` e Projeção Escalar)"
                mkCode
                    """
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
                mkCode
                    """
// 4.2 Clientes sem pedidos pendentes usando notExists
fsql {
    for c in Connections.sql.SqlServer_Advanced.Customers do
    where (notExists (fsql { for o in Connections.sql.SqlServer_Advanced.Orders do where (o.CustomerId = c.Id && o.Status = "Pending") }))
    select {|
        Customer = c.FirstName + " " + c.LastName
        City = c.City
        Country = c.Country
    |}
}
"""
                mkMarkdown "## 5. 📊 Agrupamento e Filtro por Agregação (`having`)"
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkMarkdown
                    """
## 10. 🌐 Projeção Relacional Multi-Tabela e Curinga de Tabela Inteira (`alias.*`)

Junte múltiplas tabelas relacionais e projete campos escalares específicos, ou projete tabelas mapeadas inteiras (ex: `Cliente = c`) em conjunto com colunas escalares para emitir `${alias}.*` no pushdown SQL Server.
"""
                mkCode
                    """
// 10. Junção multi-tabela com projeção curinga de tabela inteira (Cliente = fsql.extract c) junto aos dados do pedido
fsql {
    for o in Connections.sql.SqlServer_Advanced.Orders do
    join c in Connections.sql.SqlServer_Advanced.Customers on (o.CustomerId = c.Id)
    where (o.Status = "Completed")
    take 10
    select {|
        Cliente = fsql.extract c
        PedidoId = o.Id
        DataPedido = o.OrderDate
        ValorTotal = o.TotalAmount
    |}
}
"""
                mkMarkdown "## 11. 🔀 Expressões Condicionais (CASE WHEN) e Funções de String"
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkMarkdown "## 14. 🛡️ Filtros `isNotNull`, `isNotEmpty` e `isNotNullOrEmpty`"
                mkCode
                    """
// 14. Filtra colunas não nulas e não vazias com pushdown SQL completo no SQL Server
fsql {
    for c in Connections.sql.SqlServer_Advanced.Customers do
    where (isNotNull c.Email && isNotEmpty c.City && isNotNullOrEmpty c.Country)
    select {|
        Id = c.Id
        Customer = c.FirstName + " " + c.LastName
        Email = c.Email
        City = c.City
        Country = c.Country
    |}
}
"""
                mkMarkdown
                    "## 15. 🌳 Mapeamento Hierárquico Multi-Database em Árvore (`Connections.sql.<Conn>.<Database>.<Table>`)"
                mkCode
                    """
// 15. Consulta tabelas diretamente pela hierarquia em árvore do database descoberto (master)
fsql {
    for p in Connections.sql.SqlServer_Advanced.master.Products do
    join c in Connections.sql.SqlServer_Advanced.master.Categories on (p.CategoryId = c.Id)
    where (isNotEmpty p.Sku)
    sortByDescending p.Price
    select {|
        Sku = p.Sku
        Product = p.Name
        Category = c.Name
        Price = p.Price
    |}
}
"""
            ]

        serializeNotebook cells
