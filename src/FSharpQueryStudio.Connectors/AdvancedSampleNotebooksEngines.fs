namespace FSharpQueryStudio.Connectors

open AdvancedSampleNotebookCommon

module AdvancedSampleNotebooksEngines =

    let private buildEngineNotebook
        (engineDisplayName: string)
        (connName: string)
        (isUpper: bool)
        (dockerService: string)
        (icon: string)
        (isPt: bool)
        : string =
        let defaultDb = if isUpper then "SYSTEM" else "fsqs_demo"
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
                    mkMarkdown
                        $"""
# {icon} {engineDisplayName} Advanced FSQL Showcase & Diagnostics

This notebook demonstrates the full power of **FSQL** pushed down to **{engineDisplayName}**:
- **Analytical Window Functions** (`OVER (PARTITION BY ... ORDER BY ...)`, `rowNumber`, `rank`, `denseRank`, running aggregates)
- **Positional & Value Window Functions** (`lag`, `lead`, `firstValue`, `lastValue`, running `avg`, `max`)
- **Semi-Structured JSON Navigation** (`fsql.jsonValue`)
- **Correlated Subqueries & Existence Predicates** (`whereExists`, `notExists`, scalar projection subqueries)
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
                    mkCode
                        $"""
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
                    mkCode
                        $"""
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
                    mkCode
                        $"""
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
                    mkCode
                        $"""
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
                    mkMarkdown "## 4. 🎯 Correlated Subqueries & Existence Predicates (`whereExists` / `notExists`)"
                    mkCode
                        $"""
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
                    mkCode
                        $"""
// 4.2 Finding customers with no pending orders
fsql {{
    for c in Connections.sql.{connName}.{tblCust} do
    where (notExists (fsql {{ for o in Connections.sql.{connName}.{tblOrd} do where (o.{colCustId} = c.{colId} && o.{colStatus} = "Pending") }}))
    select {{|
        CustomerId = c.{colId}
        Customer = c.{colFirstName}
    |}}
}}
"""
                    mkMarkdown "## 5. 🔗 Relational Joins & Grouping with Having"
                    mkCode
                        $"""
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
                    mkCode
                        $"""
// 5.2 Grouping orders by status with HAVING aggregate filtering
fsql {{
    for o in Connections.sql.{connName}.{tblOrd} do
    groupBy o.{colStatus} into g
    having (fsql {{ for item in g do count }} >= 1)
    select {{|
        Status = g.Key
        TotalVolume = g.Sum(fun x -> x.{colTotalAmount})
        OrderCount = fsql {{ for item in g do count }}
    |}}
}}
"""
                    mkMarkdown "## 6. 🧱 Common Table Expressions (CTE) & Set Operations"
                    mkCode
                        $"""
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
                    mkCode
                        $"""
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
                    mkMarkdown "## 8. 🛡️ Null & Empty Filtering (`isNotNull`, `isNotEmpty`, `isNotNullOrEmpty`)"
                    mkCode
                        $"""
// 8. Filter non-null and non-empty columns with full SQL pushdown in {engineDisplayName}
fsql {{
    for c in Connections.sql.{connName}.{tblCust} do
    where (isNotNull c.{colFirstName} && isNotEmpty c.{colFirstName} && isNotNullOrEmpty c.{colFirstName})
    select {{|
        CustomerId = c.{colId}
        Customer = c.{colFirstName}
    |}}
}}
"""
                    mkMarkdown
                        "## 9. 🌳 Multi-Database Hierarchical Tree Mapping (`Connections.sql.<Conn>.<Database>.<Table>`)"
                    mkCode
                        $"""
// 9. Query tables directly via the discovered database tree hierarchy ({defaultDb})
fsql {{
    for p in Connections.sql.{connName}.{defaultDb}.{tblProd} do
    join c in Connections.sql.{connName}.{defaultDb}.{tblCat} on (p.{colCatId} = c.{colId})
    where (isNotEmpty p.{colName})
    sortByDescending p.{colPrice}
    select {{|
        Product = p.{colName}
        Category = c.{colName}
        Price = p.{colPrice}
    |}}
}}
"""
                ]
            else
                [
                    mkMarkdown
                        $"""
# {icon} Demonstração Avançada e Diagnósticos FSQL com {engineDisplayName}

Este caderno demonstra todo o poder do **FSQL** com pushdown completo para o **{engineDisplayName}**:
- **Funções de Janela Analíticas** (`OVER (PARTITION BY ... ORDER BY ...)`, `rowNumber`, `rank`, `denseRank`, agregações acumuladas)
- **Funções Posicionais e de Valor** (`lag`, `lead`, `firstValue`, `lastValue`, `avg` e `max` acumulados)
- **Navegação em JSON Semi-Estruturado** (`fsql.jsonValue`)
- **Subqueries Correlacionadas** (`whereExists`, `notExists`, subqueries escalares na projeção)
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
                    mkCode
                        $"""
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
                    mkMarkdown
                        "## 2. 🧭 Funções de Janela Posicionais e de Valor (`lag`, `lead`, `firstValue`, `lastValue`)"
                    mkCode
                        $"""
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
                    mkCode
                        $"""
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
                    mkCode
                        $"""
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
                    mkMarkdown
                        "## 4. 🎯 Subqueries Correlacionadas e Predicados de Existência (`whereExists` / `notExists`)"
                    mkCode
                        $"""
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
                    mkCode
                        $"""
// 4.2 Clientes sem pedidos pendentes
fsql {{
    for c in Connections.sql.{connName}.{tblCust} do
    where (notExists (fsql {{ for o in Connections.sql.{connName}.{tblOrd} do where (o.{colCustId} = c.{colId} && o.{colStatus} = "Pending") }}))
    select {{|
        CustomerId = c.{colId}
        Customer = c.{colFirstName}
    |}}
}}
"""
                    mkMarkdown "## 5. 🔗 Junções Relacionais e Agrupamento com Having"
                    mkCode
                        $"""
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
                    mkCode
                        $"""
// 5.2 Agrupamento por status com filtro agregado HAVING
fsql {{
    for o in Connections.sql.{connName}.{tblOrd} do
    groupBy o.{colStatus} into g
    having (fsql {{ for item in g do count }} >= 1)
    select {{|
        Status = g.Key
        TotalVolume = g.Sum(fun x -> x.{colTotalAmount})
        OrderCount = fsql {{ for item in g do count }}
    |}}
}}
"""
                    mkMarkdown "## 6. 🧱 Common Table Expressions (CTE) & Operações de Conjunto"
                    mkCode
                        $"""
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
                    mkCode
                        $"""
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
                    mkMarkdown "## 8. 🛡️ Filtros `isNotNull`, `isNotEmpty` e `isNotNullOrEmpty`"
                    mkCode
                        $"""
// 8. Filtra colunas não nulas e não vazias com pushdown SQL completo no {engineDisplayName}
fsql {{
    for c in Connections.sql.{connName}.{tblCust} do
    where (isNotNull c.{colFirstName} && isNotEmpty c.{colFirstName} && isNotNullOrEmpty c.{colFirstName})
    select {{|
        CustomerId = c.{colId}
        Customer = c.{colFirstName}
    |}}
}}
"""
                    mkMarkdown
                        "## 9. 🌳 Mapeamento Hierárquico Multi-Database em Árvore (`Connections.sql.<Conn>.<Database>.<Table>`)"
                    mkCode
                        $"""
// 9. Consulta tabelas diretamente pela hierarquia em árvore do database descoberto ({defaultDb})
fsql {{
    for p in Connections.sql.{connName}.{defaultDb}.{tblProd} do
    join c in Connections.sql.{connName}.{defaultDb}.{tblCat} on (p.{colCatId} = c.{colId})
    where (isNotEmpty p.{colName})
    sortByDescending p.{colPrice}
    select {{|
        Product = p.{colName}
        Category = c.{colName}
        Price = p.{colPrice}
    |}}
}}
"""
                ]

        serializeNotebook cells

    let getMySqlNotebook () =
        buildEngineNotebook "MySQL" "MySQL_Advanced" false "fsqs-mysql" "🐬" false

    let getMySqlNotebookPt () =
        buildEngineNotebook "MySQL" "MySQL_Advanced" false "fsqs-mysql" "🐬" true

    let getMariaDbNotebook () =
        buildEngineNotebook "MariaDB" "MariaDB_Advanced" false "fsqs-mariadb" "🦭" false

    let getMariaDbNotebookPt () =
        buildEngineNotebook "MariaDB" "MariaDB_Advanced" false "fsqs-mariadb" "🦭" true

    let getOracleNotebook () =
        buildEngineNotebook "Oracle" "Oracle_Advanced" true "fsqs-oracle" "🔴" false

    let getOracleNotebookPt () =
        buildEngineNotebook "Oracle" "Oracle_Advanced" true "fsqs-oracle" "🔴" true
