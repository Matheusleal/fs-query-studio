namespace FSharpQueryStudio.Connectors

open AdvancedSampleNotebookCommon

module AdvancedSampleNotebooksPostgres =

    let getPostgresNotebook () : string =
        let cells =
            [
                mkMarkdown
                    """
# 🐘 PostgreSQL Advanced FSQL Showcase & Diagnostics

This notebook demonstrates the full power of **FSQL** pushed down to **PostgreSQL**:
- **Analytical Window Functions** (`OVER (PARTITION BY ... ORDER BY ...)`, `rowNumber`, `rank`, `denseRank`, running aggregates)
- **Positional & Value Window Functions** (`lag`, `lead`, `firstValue`, `lastValue`, running `avg`, `max`)
- **Semi-Structured JSON Navigation** (`fsql.jsonValue` -> `specs ->> 'brand'`)
- **Correlated Subqueries** (`whereExists`, `notExists`, scalar subqueries in projection)
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkMarkdown "## 4. 🎯 Correlated Subqueries (`whereExists`, `notExists` & Scalar Projection)"
                mkCode
                    """
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
                mkCode
                    """
// 4.2 Customers with NO pending orders using notExists
fsql {
    for c in Connections.sql.PostgreSQL_Advanced.customers do
    where (notExists (fsql { for o in Connections.sql.PostgreSQL_Advanced.orders do where (o.customer_id = c.id && o.status = "Pending") }))
    select {|
        Customer = c.first_name + " " + c.last_name
        City = c.city
        Country = c.country
    |}
}
"""
                mkMarkdown "## 5. 📊 Grouping & Aggregate Filter (`having`)"
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkMarkdown "## 14. 🛡️ Null & Empty Filtering (`isNotNull`, `isNotEmpty`, `isNotNullOrEmpty`)"
                mkCode
                    """
// 14. Filter non-null and non-empty columns with full SQL pushdown in PostgreSQL
fsql {
    for c in Connections.sql.PostgreSQL_Advanced.customers do
    where (isNotNull c.email && isNotEmpty c.city && isNotNullOrEmpty c.country)
    select {|
        Id = c.id
        Customer = c.first_name + " " + c.last_name
        Email = c.email
        City = c.city
        Country = c.country
    |}
}
"""
                mkMarkdown
                    "## 15. 🌳 Multi-Database Hierarchical Tree Mapping (`Connections.sql.<Conn>.<Database>.<Table>`)"
                mkCode
                    """
// 15. Query tables directly via the discovered database tree hierarchy (fsqs_demo)
fsql {
    for p in Connections.sql.PostgreSQL_Advanced.fsqs_demo.products do
    join c in Connections.sql.PostgreSQL_Advanced.fsqs_demo.categories on (p.category_id = c.id)
    where (isNotEmpty p.sku)
    sortByDescending p.price
    select {|
        Sku = p.sku
        Product = p.name
        Category = c.name
        Price = p.price
    |}
}
"""
            ]

        serializeNotebook cells


    let getPostgresNotebookPt () : string =
        let cells =
            [
                mkMarkdown
                    """
# 🐘 Demonstração Avançada e Diagnósticos do FSQL com PostgreSQL

Este notebook demonstra todo o poder do **FSQL** com pushdown no **PostgreSQL**:
- **Funções de Janela Analíticas** (`OVER (PARTITION BY ... ORDER BY ...)`, `rowNumber`, `rank`, `denseRank`, agregações acumuladas)
- **Funções Posicionais e de Valor** (`lag`, `lead`, `firstValue`, `lastValue`, `avg` e `max` acumulados)
- **Navegação em JSON Semi-Estruturado** (`fsql.jsonValue` -> `specs ->> 'brand'`)
- **Subqueries Correlacionadas** (`whereExists`, `notExists`, subqueries escalares na projeção)
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkMarkdown "## 4. 🎯 Subqueries Correlacionadas (`whereExists`, `notExists` e Projeção Escalar)"
                mkCode
                    """
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
                mkCode
                    """
// 4.2 Clientes sem pedidos pendentes usando notExists
fsql {
    for c in Connections.sql.PostgreSQL_Advanced.customers do
    where (notExists (fsql { for o in Connections.sql.PostgreSQL_Advanced.orders do where (o.customer_id = c.id && o.status = "Pending") }))
    select {|
        Customer = c.first_name + " " + c.last_name
        City = c.city
        Country = c.country
    |}
}
"""
                mkMarkdown "## 5. 📊 Agrupamento e Filtro por Agregação (`having`)"
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkCode
                    """
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
                mkMarkdown "## 14. 🛡️ Filtros `isNotNull`, `isNotEmpty` e `isNotNullOrEmpty`"
                mkCode
                    """
// 14. Filtra colunas não nulas e não vazias com pushdown SQL completo no PostgreSQL
fsql {
    for c in Connections.sql.PostgreSQL_Advanced.customers do
    where (isNotNull c.email && isNotEmpty c.city && isNotNullOrEmpty c.country)
    select {|
        Id = c.id
        Customer = c.first_name + " " + c.last_name
        Email = c.email
        City = c.city
        Country = c.country
    |}
}
"""
                mkMarkdown
                    "## 15. 🌳 Mapeamento Hierárquico Multi-Database em Árvore (`Connections.sql.<Conn>.<Database>.<Table>`)"
                mkCode
                    """
// 15. Consulta tabelas diretamente pela hierarquia em árvore do database descoberto (fsqs_demo)
fsql {
    for p in Connections.sql.PostgreSQL_Advanced.fsqs_demo.products do
    join c in Connections.sql.PostgreSQL_Advanced.fsqs_demo.categories on (p.category_id = c.id)
    where (isNotEmpty p.sku)
    sortByDescending p.price
    select {|
        Sku = p.sku
        Product = p.name
        Category = c.name
        Price = p.price
    |}
}
"""
            ]

        serializeNotebook cells
