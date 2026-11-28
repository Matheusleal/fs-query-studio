namespace FSharpQueryStudio.Connectors

open AdvancedSampleNotebookCommon

module SampleDemoNotebookPt =

    let getSampleDemoPortugueseNotebook () : string =
        let cells =
            [
                mkMarkdown
                    """
# 🚀 F# Query Studio — Guia Interativo Multi-Fontes de Alta Escala

> **Bem-vindo ao F# Query Studio!** Este notebook interativo demonstra o funcionamento com múltiplas fontes de dados heterogêneas sem colisões de nomes, consultas LINQ fortemente tipadas, pushdown para SQL de alto desempenho, joins, agregações analíticas e renderização de dados virtualizada com **mais de 100 mil registros em cada data source**.

---

### 📋 Fontes de Dados Pré-Conectadas (Ao menos 100k registros cada com colunas expandidas)

| Conexão | Tipo | Descrição | Tabelas / Colunas Expandidas Principais |
| :--- | :--- | :--- | :--- |
| **`SampleStore`** | SQLite | E-commerce relacional com mais de 100k pedidos | `Categories`, `Products`, `Customers`, `Orders`, `OrderItems` |
| **`InventoryStore`** | SQLite | Gestão de armazéns e logística com 100k+ registros | `Warehouses`, `StockLevels`, `Suppliers` |
| **`SalesFeed`** | CSV | Feed diário de vendas com 100k+ linhas | `Date`, `OrderId`, `Customer`, `Product`, `Category`, `TotalAmount`, `Status`, `GrossMargin`, etc. |
| **`CustomersFeed`** | CSV | Feed de fidelidade e avaliação com 100k+ linhas | `CustomerId`, `CustomerName`, `LoyaltyTier`, `TotalSpent`, `Score`, `TotalOrders`, `Segment`, etc. |
| **`RegionalTargets`** | Excel (`.xlsx`) | Planilha multi-abas de metas com 100k+ linhas | `RegionalTargets`, `QuarterlyBudget` |
| **`CommerceOps`** | Excel (`.xlsx`) | Planilha com 4 abas e 100k+ vendas e entregas | `Customers`, `ProductsStock`, `Sales`, `DeliveryLogs` |
| **`ProductCatalog`** | JSON (`.json`) | Catálogo multi-coleções com 100k+ produtos e reviews | `Products`, `Reviews` |
| **`LogisticsHub`** | XML (`.xml`) | Hub logístico com 100k+ remessas e transportadoras | `Carriers`, `Shipments` |
| **`GitHubFSharp`** | REST (`https://`) | API REST segura do GitHub (`fsprojects` repos) | `name`, `full_name`, `language`, `stargazers_count`, `forks_count`, `html_url` |

---

### ⌨️ Dicas de Execução e Atalhos

- **Executar Célula Ativa**: `Ctrl + Enter` (Windows/Linux) ou `Cmd + Enter` (macOS)
- **Executar e Avançar**: `Shift + Enter`
- **IntelliSense / Autocompletar**: `Ctrl + Space`
- **Ir para Definição dos Tipos**: `F12` em qualquer campo (ex: `.Country`, `.TotalAmount`)
- **Tabela Interativa**: No rodapé dos resultados, alterne entre **Grid** (ordenação/paginação interativa), **JSON** e **Raw Text**.
"""

                mkMarkdown
                    """
## 1. ⚡ Consultas LINQ Fortemente Tipadas: Filtrando Clientes (`SampleStore`)

Ao usar `Connections.sql.SampleStore.Customers`, o **F# Query Studio** fornece suporte a autocompletar completo com IntelliSense e tipos fortemente tipados gerados em segundo plano.

> 💡 **Dica de Formatação**: Retornar registros anônimos (`{| ... |}`) produz cabeçalhos limpos com nomes descritivos e tipos explícitos na tabela interativa.
"""

                mkCode
                    """
// 1. Conecta e consulta clientes do Brasil diretamente via Connections.sql
fsql {
    for c in Connections.sql.SampleStore.Customers do
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

                mkMarkdown
                    """
## 2. 🔗 Relacionamentos e Joins Relacionais (`SampleStore`)

Combine tabelas relacionais de forma natural utilizando a sintaxe `join ... on (...)` do F# LINQ dentro da mesma conexão.
"""

                mkCode
                    """
// 2. Junção relacional entre Pedidos (Orders) e Clientes (Customers) dentro de SampleStore
fsql {
    for o in Connections.sql.SampleStore.Orders do
    join c in Connections.sql.SampleStore.Customers on (o.CustomerId = c.Id)
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

                mkMarkdown
                    """
## 3. 🏬 Segunda Fonte SQLite: Armazéns e Estoque (`InventoryStore`)

Múltiplas fontes SQLite coexistem sem colisão de módulos ou tipos gerados.
Aqui consultamos a segunda base SQLite diretamente via `Connections.sql.InventoryStore`.
"""

                mkCode
                    """
// 3. Consulta a segunda base SQLite (InventoryStore) diretamente via Connections.sql
fsql {
    for w in Connections.sql.InventoryStore.Warehouses do
    join s in Connections.sql.InventoryStore.StockLevels on (w.Id = s.WarehouseId)
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

                mkMarkdown
                    """
## 4. 📊 Agregações Analíticas e Agrupamento (GroupBy)

Realize agregações analíticas (`sumBy`, `count`, `averageBy`) agrupando os dados por status do pedido sobre 100 mil registros.
"""

                mkCode
                    """
// 4. Resumo financeiro: Faturamento total e ticket médio por Status do Pedido sobre 100k pedidos
Connections.sql.SampleStore.Orders
|> Seq.groupBy (fun o -> o.Status)
|> Seq.map (fun (status, items) ->
    let list = Seq.toList items
    {|
        Status = status
        QtdPedidos = list.Length
        ValorTotal = list |> List.sumBy (fun i -> i.TotalAmount)
        TicketMedio = list |> List.averageBy (fun i -> i.TotalAmount)
    |})
"""

                mkMarkdown
                    """
## 5. 📁 Primeiro Arquivo CSV: Transações de Vendas (`SalesFeed`)

Arquivos CSV são mapeados como registros fortemente tipados com inferência automática de colunas numéricas, decimais e texto.
Acesse o feed diretamente via `Connections.csv.SalesFeed`.
"""

                mkCode
                    """
// 5. Consulta o primeiro arquivo CSV (SalesFeed) diretamente
fsql {
    for row in Connections.csv.SalesFeed do
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

                mkMarkdown
                    """
## 6. 📋 Segundo Arquivo CSV: Feed de Fidelidade (`CustomersFeed`)

Uma segunda fonte CSV registrada ao lado de `SalesFeed`. Ambas possuem inferência de tipos e módulos independentes sem vazamento de contexto.
"""

                mkCode
                    """
// 6. Consulta o segundo arquivo CSV (CustomersFeed) diretamente
fsql {
    for row in Connections.csv.CustomersFeed do
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

                mkMarkdown
                    """
## 7. 🔄 Cruzamento Multi-CSV com LINQ em Memória

Cruze dados de múltiplos arquivos CSV distintos (`SalesFeed` + `CustomersFeed`) com um join tipado em memória:
"""

                mkCode
                    """
// 7. Correlação em memória entre transações de vendas e fidelidade de clientes
let salesRows = Connections.csv.SalesFeed |> Seq.truncate 2000 |> Seq.toList
let loyaltyRows = Connections.csv.CustomersFeed |> Seq.truncate 2000 |> Seq.toList

fsql {
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

                mkMarkdown
                    """
### ⚡ Pushdown e Joins com Múltiplas Propriedades com Visualização SQL (`toRawSql`)

O F# Query Studio traduz joins com múltiplas propriedades no formato `on ((a.param1, a.param2) = (b.param1, b.param2))` diretamente para SQL com índices (`_t0.col1 = _t1.col1 AND _t0.col2 = _t1.col2`), evitando produtos cartesianos e consumo excessivo de memória RAM.
Use `ResultKind.toRawSql` para visualizar a query SQL gerada antes de enviá-la ao banco de dados!
"""

                mkCode
                    """
// 7.1 Inspeciona o SQL gerado com pushdown para joins antes da execução
let consultaComplexa =
    fsql {
        for o in Connections.sql.SampleStore.Orders do
        join i in Connections.sql.SampleStore.OrderItems on (o.Id = i.OrderId)
        where (o.TotalAmount >= 100.0f)
        select {| PedidoId = o.Id; ItemId = i.Id; ValorTotal = o.TotalAmount |}
    }

printfn "SQL Gerado pelo Pushdown:\n%s" (ResultKind.toRawSql consultaComplexa)
consultaComplexa
"""

                mkMarkdown
                    """
## 8. 🛡️ Consultas SQL Parametrizadas Seguras (`SampleStore`)

Para queries complexas ou instruções SQL nativas, use `Connections.sql.<Conn>.rawQuery` com passagem segura de parâmetros nomeados para evitar injeção de SQL.
"""

                mkCode
                    """
// 8. Query SQL nativa com parâmetros nomeados seguros executada via SampleStore
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

Connections.sql.SampleStore.rawQuery(sqlCatalogo, [ "precoMinimo", box 100.0 ])
"""

                mkMarkdown
                    """
## 9. 📊 Planilha Excel (`.xlsx`): Metas Regionais de Receita (`RegionalTargets`)

Arquivos Excel (`.xlsx`) têm suas abas (worksheets) inspecionadas e tipadas automaticamente via `Connections.excel.RegionalTargets`. Você pode consultar cada aba (`RegionalTargets` ou `QuarterlyBudget`) diretamente com LINQ!
"""

                mkCode
                    """
// 9. Consulta a aba RegionalTargets da planilha Excel (.xlsx) diretamente via Connections.excel
fsql {
    for row in Connections.excel.RegionalTargets.RegionalTargets do
    where (row.GrowthRate >= 10.0M)
    sortByDescending row.AchievedRevenue
    select {|
        Regiao = row.Region
        Pais = row.Country
        Responsavel = row.LeadManager
        MetaAnual = row.AnnualTarget
        ReceitaRealizada = row.AchievedRevenue
        TaxaCrescimentoPct = row.GrowthRate
    |}
}
"""

                mkMarkdown
                    """
## 10. 📦 Planilha Excel 4 Abas (`.xlsx`): Clientes, Produtos com Estoque, Vendas e Logs de Delivery (`CommerceOps`)

Cada conexão Excel funciona como um schema/container (`Connections.excel.CommerceOps.<Sheet>`) mapeado para tabelas prefixadas no SQLite (`CommerceOps_Customers`, `CommerceOps_ProductsStock`, `CommerceOps_Sales`, `CommerceOps_DeliveryLogs`). Veja como unir as 4 abas em uma única consulta LINQ com pushdown para SQL:
"""

                mkCode
                    """
// 10. Join entre as 4 abas da planilha em inglês CommerceOps (Sales, Customers, ProductsStock, DeliveryLogs)
fsql {
    for s in Connections.excel.CommerceOps.Sales do
    join c in Connections.excel.CommerceOps.Customers on (s.CustomerId = c.CustomerId)
    join p in Connections.excel.CommerceOps.ProductsStock on (s.ProductId = p.ProductId)
    join d in Connections.excel.CommerceOps.DeliveryLogs on (s.SaleId = d.SaleId)
    sortByDescending s.TotalAmount
    select {|
        VendaId = s.SaleId
        Cliente = c.FullName
        Pais = c.Country
        Produto = p.ProductName
        EstoqueDisponivel = p.StockQuantity
        Quantidade = s.Quantity
        ValorTotal = s.TotalAmount
        Transportadora = d.Carrier
        CodigoRastreio = d.TrackingCode
        StatusEntrega = d.DeliveryStatus
    |}
}
"""

                mkMarkdown
                    """
## 11. 🗂️ Documento JSON Local (`.json`): Catálogo Multi-Coleções e Avaliações (`ProductCatalog`)

Arquivos JSON locais (`.json`) funcionam exatamente como planilhas Excel: cada array descoberto é exposto como uma sub-propriedade fortemente tipada (`Connections.json.ProductCatalog.Products` e `Connections.json.ProductCatalog.Reviews`) com staging em SQLite (`ProductCatalog_Products`, `ProductCatalog_Reviews`) e pushdown de LINQ para SQL!
"""

                mkCode
                    """
// 11. Join entre Products e Reviews dentro do arquivo JSON local (product_catalog.json) via Connections.json
fsql {
    for p in Connections.json.ProductCatalog.Products do
    join r in Connections.json.ProductCatalog.Reviews on (p.ProductId = r.ProductId)
    where (r.Stars >= 4)
    sortByDescending p.Price
    select {|
        Sku = p.Sku
        Produto = p.Title
        Categoria = p.Category
        Preco = p.Price
        Avaliador = r.Reviewer
        Estrelas = r.Stars
        Comentario = r.Comment
    |}
}
"""

                mkMarkdown
                    """
## 12. 📄 Documento XML Local (`.xml`): Rede de Transportadoras e Remessas (`LogisticsHub`)

Arquivos XML locais (`.xml`) extraem automaticamente tanto atributos XML (`@CarrierId`, `@Code`) quanto elementos filhos (`<TrackingNumber>`, `<Destination>`, `<ShippingCost>`) em sub-propriedades fortemente tipadas (`Connections.xml.LogisticsHub.Carriers` e `Connections.xml.LogisticsHub.Shipments`).
"""

                mkCode
                    """
// 12. Join entre Shipments e Carriers dentro do arquivo XML local (logistics_hub.xml) via Connections.xml
fsql {
    for s in Connections.xml.LogisticsHub.Shipments do
    join c in Connections.xml.LogisticsHub.Carriers on (s.CarrierId = c.CarrierId)
    sortByDescending s.ShippingCost
    select {|
        RemessaId = s.ShipmentId
        CodigoRastreio = s.TrackingNumber
        Transportadora = c.Name
        PaisHub = c.HubCountry
        Destino = s.Destination
        PesoKg = s.WeightKg
        CustoEnvio = s.ShippingCost
        Status = s.Status
    |}
}
"""

                mkMarkdown
                    """
## 13. 🔒 API REST Segura do GitHub (`https://`): Repositórios da Comunidade F# (`GitHubFSharp`)

Endpoints REST/JSON em páginas seguras (`https://`) são inspecionados e mapeados em registros F# fortemente tipados. Aqui consultamos os repositórios públicos de F# em `https://api.github.com/orgs/fsprojects/repos` via `Connections.rest.GitHubFSharp`.
"""

                mkCode
                    """
// 13. Consulta repositórios públicos da comunidade F# via API HTTPS do GitHub em Connections.rest
fsql {
    for repo in Connections.rest.GitHubFSharp do
    sortByDescending repo.stargazers_count
    select {|
        Repositorio = repo.full_name
        Linguagem = repo.language
        Estrelas = repo.stargazers_count
        Forks = repo.forks_count
        IssuesAbertas = repo.open_issues_count
        BranchPadrao = repo.default_branch
        Url = repo.html_url
    |}
}
"""

                mkMarkdown
                    """
## 14. 🌐 Visão Consolidada Multi-Fontes de Alta Escala (100k+ Registros Cada)

Como as consultas no F# Query Studio rodam em uma sessão FSI unificada, você pode cruzar e comparar dados das 9 fontes simultaneamente:
"""

                mkCode
                    """
// 14. Resumo consolidado das 9 fontes de dados registradas
let sampleStoreOrders = Connections.sql.SampleStore.Orders |> Seq.length
let inventoryWarehouses = Connections.sql.InventoryStore.Warehouses |> Seq.length
let salesCount = Connections.csv.SalesFeed |> Seq.length
let loyaltyCount = Connections.csv.CustomersFeed |> Seq.length
let excelTargetsCount = Connections.excel.RegionalTargets.RegionalTargets |> Seq.length
let commerceSalesCount = Connections.excel.CommerceOps.Sales |> Seq.length
let jsonProductsCount = Connections.json.ProductCatalog.Products |> Seq.length
let xmlShipmentsCount = Connections.xml.LogisticsHub.Shipments |> Seq.length
let githubReposCount = Connections.rest.GitHubFSharp |> Seq.length

[
    {| Fonte = "SampleStore";     Tipo = "SQLite (Relacional)"; TotalRegistros = sampleStoreOrders;   EntidadePrincipal = "Orders" |}
    {| Fonte = "InventoryStore";  Tipo = "SQLite (Relacional)"; TotalRegistros = inventoryWarehouses; EntidadePrincipal = "Warehouses" |}
    {| Fonte = "SalesFeed";       Tipo = "CSV (Arquivo Plano)"; TotalRegistros = salesCount;          EntidadePrincipal = "Transações" |}
    {| Fonte = "CustomersFeed";   Tipo = "CSV (Arquivo Plano)"; TotalRegistros = loyaltyCount;        EntidadePrincipal = "Perfis de Fidelidade" |}
    {| Fonte = "RegionalTargets"; Tipo = "Excel (.xlsx)";       TotalRegistros = excelTargetsCount;   EntidadePrincipal = "RegionalTargets" |}
    {| Fonte = "CommerceOps";     Tipo = "Excel (.xlsx)";       TotalRegistros = commerceSalesCount;  EntidadePrincipal = "Customers / Stock / Sales / Delivery" |}
    {| Fonte = "ProductCatalog";  Tipo = "JSON (.json)";        TotalRegistros = jsonProductsCount;   EntidadePrincipal = "Products / Reviews" |}
    {| Fonte = "LogisticsHub";    Tipo = "XML (.xml)";          TotalRegistros = xmlShipmentsCount;   EntidadePrincipal = "Carriers / Shipments" |}
    {| Fonte = "GitHubFSharp";    Tipo = "REST JSON (HTTPS)";   TotalRegistros = githubReposCount;    EntidadePrincipal = "Repositórios GitHub" |}
]
"""

                mkMarkdown
                    """
## 15. 📁 Operações com Arquivos e Manipulação com `System.IO` (`data_sources`)

O F# Query Studio garante que o diretório de trabalho (`System.Environment.CurrentDirectory`) esteja sempre alinhado à raiz do seu workspace. Você pode criar, escrever e manipular arquivos locais utilizando as APIs nativas do .NET (`System.IO.File`, `FileStream`, `StreamWriter`) sem bloqueios de arquivo (*file locks*) entre células!

Neste exemplo, lemos registros brutos de `data_sources/sales_feed.csv`, processamos o cabeçalho e criamos um novo arquivo consolidado `data_sources/top_sales_exportado.csv` via `System.IO.File.Create`.
"""

                mkCode
                    """
// 15. Cria um arquivo resumo a partir de data_sources/sales_feed.csv usando System.IO.File.Create
open System.IO
open System.Text

let salesFeedPath = Path.Combine("data_sources", "sales_feed.csv")
let caminhoExportado = Path.Combine("data_sources", "top_sales_exportado.csv")

// Lê linhas de data_sources e processa dados com descarte explícito da stream
let summaryRows =
    File.ReadLines salesFeedPath
    |> Seq.skip 1
    |> Seq.take 100
    |> Seq.map (fun line -> line.Split ',')
    |> Seq.filter (fun p -> p.Length >= 7)
    |> Seq.map (fun p ->
        {|
            PedidoId = p.[1]
            Cliente = p.[2]
            Produto = p.[3]
            Valor = p.[5]
            Status = p.[6]
        |})
    |> Seq.toList

do
    use stream = File.Create caminhoExportado
    use writer = new StreamWriter(stream, Encoding.UTF8)
    writer.WriteLine "PedidoId,Cliente,Produto,Valor,Status"
    for r in summaryRows do
        writer.WriteLine $"{r.PedidoId},{r.Cliente},{r.Produto},{r.Valor},{r.Status}"
    writer.Flush()

printfn "Arquivo gerado com sucesso: %s (Total de registros: %d, Tamanho: %d bytes)"
    caminhoExportado summaryRows.Length (FileInfo caminhoExportado).Length

summaryRows
"""

                mkMarkdown
                    """
## 16. 🔄 Validação de Arquivo Livre de Lock Entre Células do Notebook

Em seguida, nesta célula subsequente, lemos, inspecionamos e atualizamos o arquivo `top_sales_exportado.csv` criado na célula anterior. O F# Query Studio descarta automaticamente cursores anteriores e handles de sistema operacional, garantindo que operações de releitura (`File.ReadAllLines`) e escrita/adição (`File.AppendAllText`) ocorram sem violações de compartilhamento (*sharing violation / IOException*).
"""

                mkCode
                    """
// 16. Valida acesso livre de locks entre células: releitura, acréscimo e projeção em tabela
open System.IO

let caminhoVerificado = Path.Combine("data_sources", "top_sales_exportado.csv")

// Relê o arquivo gerado na célula anterior
let lines = File.ReadAllLines caminhoVerificado

// Acrescenta metadados ao final para certificar permissão de escrita imediata
File.AppendAllText(caminhoVerificado, "# Processado pelo F# Query Studio\n")

printfn "Acesso sem lock confirmado para %s: lidas %d linhas, tamanho atualizado %d bytes"
    caminhoVerificado lines.Length (FileInfo caminhoVerificado).Length

lines
|> Seq.skip 1
|> Seq.filter (fun l -> not (l.StartsWith "#"))
|> Seq.take 20
|> Seq.map (fun l ->
    let p = l.Split ','
    {|
        PedidoId = p.[0]
        Cliente = p.[1]
        Produto = p.[2]
        Valor = p.[3]
        Status = p.[4]
    |})
|> Seq.toList
"""

                mkMarkdown
                    """
## 17. 🎯 DQL Avançado: Subqueries Correlacionadas com `whereExists` e `notExists` (`SampleStore`)

Com a computation expression **`fsql`**, você pode escrever testes de existência correlacionados (`EXISTS` e `NOT EXISTS`) em sintaxe funcional pura F#, sem concatenação de strings SQL.
Aqui filtramos clientes do Brasil e dos EUA que possuem pedidos de alto valor (>= $250).
"""

                mkCode
                    """
// 17. Consulta clientes com pedidos de alto valor (>= $250) usando whereExists via fsql
fsql {
    for c in Connections.sql.SampleStore.Customers do
    where (c.Country = "Brazil" || c.Country = "USA")
    whereExists (fsql { for o in Connections.sql.SampleStore.Orders do where (o.CustomerId = c.Id && o.TotalAmount >= 250.0f) })
    sortBy c.FirstName
    take 10
    select {|
        Id = c.Id
        Cliente = c.FirstName + " " + c.LastName
        Cidade = c.City
        Pais = c.Country
    |}
}
"""

                mkMarkdown
                    """
## 18. 🧮 DQL Avançado: Subqueries Escalares na Projeção SELECT (`SampleStore`)

Calcule métricas correlacionadas por linha (como contagem total de pedidos) diretamente na cláusula `select` do `fsql`. A subquery é compilada para um sub-select escalar nativo no banco de dados.
"""

                mkCode
                    """
// 18. Enriquece os clientes com a contagem correlacionada de pedidos na projeção SELECT
fsql {
    for c in Connections.sql.SampleStore.Customers do
    where (c.Country = "Brazil")
    sortBy c.FirstName
    take 10
    select {|
        Cliente = c.FirstName + " " + c.LastName
        Email = c.Email
        Cidade = c.City
        TotalPedidos = fsql { for o in Connections.sql.SampleStore.Orders do where (o.CustomerId = c.Id); count }
    |}
}
"""

                mkMarkdown
                    """
## 19. 📊 DQL Avançado: GroupBy com Cláusula `having` (`SampleStore`)

Filtre agregações agrupadas diretamente usando a operação customizada `having` do `fsql`. A condição é compilada diretamente para `GROUP BY ... HAVING (COUNT(*) >= @p0)`.
"""

                mkCode
                    """
// 19. Agrupa pedidos por cliente e filtra compradores frequentes usando having (>= 10 pedidos)
fsql {
    for o in Connections.sql.SampleStore.Orders do
    groupBy o.CustomerId into g
    having (fsql { for item in g do count } >= 10)
    select {|
        ClienteId = g.Key
        TotalPedidos = fsql { for item in g do count }
    |}
}
"""

                mkMarkdown
                    """
## 20. 🔍 DQL Avançado: Inspeção do SQL Formatado via `toRawQuery`

Inspecione a consulta SQL gerada antes ou durante a execução. O método **`toRawQuery`** produz SQL identado com quebra de cláusulas e cabeçalho de parâmetros vinculados.
"""

                mkCode
                    """
// 20. Inspeção do SQL Pushdown formatado com identação e documentação de parâmetros
let queryInspecionada =
    fsql {
        for c in Connections.sql.SampleStore.Customers do
        where (c.Country = "USA")
        whereExists (fsql { for o in Connections.sql.SampleStore.Orders do where (o.CustomerId = c.Id && o.TotalAmount >= 100.0f) })
        select {|
            Cliente = c.FirstName + " " + c.LastName
            TotalPedidos = fsql { for o in Connections.sql.SampleStore.Orders do where (o.CustomerId = c.Id); count }
        |}
    }

printfn "SQL Pushdown via toRawQuery:\n%s" (toRawQuery queryInspecionada)
queryInspecionada
"""

                mkMarkdown
                    """
## 21. ⚡ Funções de Janela Analíticas (`fsql.window`, `rowNumber`, `rank`, `runningTotal`)

Execute funções de janela analíticas nativas no SQLite 3.25+ usando `fsql.window`.
Aqui ranqueamos os produtos por preço em cada categoria e calculamos a soma acumulada de valores.
"""

                mkCode
                    """
// 21. Funções de Janela: rowNumber, rank e soma acumulada particionada por categoria
fsql {
    for p in Connections.sql.SampleStore.Products do
    select {|
        Categoria = p.CategoryId
        Produto = p.Name
        Preco = p.Price
        LinhaNaCategoria = fsql.rowNumber(fsql.window(partitionBy = p.CategoryId, sortByDescending = p.Price))
        RankNaCategoria = fsql.rank(fsql.window(partitionBy = p.CategoryId, sortByDescending = p.Price))
        SomaAcumulada = fsql.sum(p.Price, over = fsql.window(partitionBy = p.CategoryId, sortBy = p.Id))
    |}
}
"""

                mkMarkdown
                    """
## 22. 🧩 Expressões de Tabela Comuns (CTEs) & Operações de Conjunto (`union`, `unionAll`, `intersect`, `except`)

Combine múltiplas projeções de consulta usando operações de conjunto e CTEs diretamente dentro da computation expression **`fsql { ... }`**.
"""

                mkCode
                    """
// 22.1 CTE nomeada consumida diretamente via JOIN
let produtosCaros =
    fsql {
        for p in Connections.sql.SampleStore.Products do
        where (p.Price >= 200.0f)
        select p
    }

let catalogoComCategoria =
    fsql {
        for c in Connections.sql.SampleStore.Categories do
        withCte "ProdutosCaros" produtosCaros
        join p in produtosCaros on (c.Id = p.CategoryId)
        select (c.Name, p.Name, p.Price)
    }

printfn "SQL Formatado (CTE nomeada + JOIN):\n%s" (toRawQuery catalogoComCategoria)
catalogoComCategoria
"""

                mkCode
                    """
// 22.2 CTE sem nome com injeção transparente e JOIN
let produtosEstoque =
    fsql {
        for p in Connections.sql.SampleStore.Products do
        where (p.StockQuantity >= 100)
        select p
    }

let catalogoEstoque =
    fsql {
        for c in Connections.sql.SampleStore.Categories do
        withCte produtosEstoque
        join p in produtosEstoque on (c.Id = p.CategoryId)
        select (c.Name, p.Name, p.StockQuantity)
    }

printfn "SQL Formatado (CTE sem nome + JOIN):\n%s" (toRawQuery catalogoEstoque)
catalogoEstoque
"""

                mkCode
                    """
// 22.3 Operações de Conjunto: intersect e except diretamente dentro da CE fsql
let estoqueExcept =
    fsql {
        for p in Connections.sql.SampleStore.Products do
        where (p.StockQuantity >= 100)
        select p
        except (fsql {
            for p2 in Connections.sql.SampleStore.Products do
            where (p2.Price > 300.0f)
            select p2
        })
    }

printfn "SQL Formatado (EXCEPT):\n%s" (toRawQuery estoqueExcept)
estoqueExcept
"""

                mkMarkdown
                    """
## 23. 🔗 Outer Joins Relacionais & Expressões Condicionais (CASE WHEN)

Consulte tabelas com `leftOuterJoin ... into ... for ... in ... .DefaultIfEmpty()` e formate colunas com ramificações `if ... then ... else` compiladas para `CASE WHEN` nativo em SQL.
"""

                mkCode
                    """
// 23. Left outer join com classificação condicional inline (CASE WHEN)
fsql {
    for c in Connections.sql.SampleStore.Customers do
    where (c.Country = "Brazil" || c.Country = "USA")
    leftOuterJoin o in Connections.sql.SampleStore.Orders on (c.Id = o.CustomerId) into orders
    for o in orders.DefaultIfEmpty() do
    take 15
    select {|
        Cliente = c.FirstName + " " + c.LastName
        Pais = c.Country
        PedidoId = if isNull (box o) then 0 else o.Id
        Valor = if isNull (box o) then 0.0f else o.TotalAmount
        Status = if isNull (box o) then "Sem Pedidos" else o.Status
        CategoriaCliente = if c.LoyaltyPoints >= 1000 then "VIP" else "Regular"
    |}
}
"""

                mkMarkdown
                    """
## 24. 🔍 Não-Existência Correlacionada (`notExists`) & Diagnóstico do Plano de Execução (`fsql.explain`)

Encontre entidades sem registros relacionados usando `notExists` e inspecione o relatório do plano de execução do banco de dados em tempo real.
"""

                mkCode
                    """
// 24. Clientes sem pedidos pendentes e diagnóstico do plano de execução ao vivo
let auditoriaPendentes =
    fsql {
        for c in Connections.sql.SampleStore.Customers do
        where (c.Country = "Brazil")
        where (notExists (fsql { for o in Connections.sql.SampleStore.Orders do where (o.CustomerId = c.Id && o.Status = "Pending") }))
        take 10
        select {|
            Cliente = c.FirstName + " " + c.LastName
            Cidade = c.City
            Email = c.Email
        |}
    }

let relatorioPlano = fsql.explain auditoriaPendentes
printfn "Relatório do Plano de Execução:\n%s" relatorioPlano.PlanText
printfn "SQL Formatado:\n%s" (toRawQuery auditoriaPendentes)
auditoriaPendentes
"""

                mkMarkdown
                    """
## 25. 🛡️ Filtros `isNotNull`, `isNotEmpty` e `isNotNullOrEmpty`

Filtre colunas nulas ou vazias diretamente em `where (...)` usando `isNotNull`, `isNotEmpty` e `isNotNullOrEmpty` — todas traduzidas com pushdown direto para SQL (`IS NOT NULL` e `<> ''`).
"""

                mkCode
                    """
// 25. Filtra campos não nulos e não vazios usando isNotNull, isNotEmpty e isNotNullOrEmpty
let pedidosValidosQuery =
    fsql {
        for o in Connections.sql.SampleStore.Orders do
        where (isNotNull o.Notes && isNotEmpty o.Status && isNotNullOrEmpty o.PaymentMethod && o.TotalAmount >= 150.0f)
        sortByDescending o.TotalAmount
        take 10
        select {|
            PedidoId = o.Id
            NumeroPedido = o.OrderNumber
            Status = o.Status
            MetodoPagamento = o.PaymentMethod
            Canal = o.Channel
            ValorTotal = o.TotalAmount
            Observacoes = o.Notes
        |}
    }

printfn "SQL Formatado (Pushdown de Null e Empty):\n%s" (toRawQuery pedidosValidosQuery)
pedidosValidosQuery
"""

                mkMarkdown
                    """
## 26. 🗄️ Consultas SQLite Diretas sobre Conexões CSV e XML Mapeadas (`rawQuery` & `queryEngine`)

Fontes de arquivos mapeadas (`CSV`, `XML`, `JSON`, `Excel`) realizam auto-staging sob demanda no motor SQLite do workspace (`.fsqs/cache/engine.db`) com views para o nome da conexão, nome do arquivo e nome da tabela (`SalesFeed`, `sales_feed`, `LogisticsHub`, `Shipments`, `Carriers`). Você pode executar consultas SQLite diretas via `Connections.sql.rawQuery`, `Connections.csv.<Conn>.rawQuery` ou `Connections.xml.<Conn>.rawQuery`.
"""

                mkCode
                    """
// 26. Consultas SQLite diretas e parametrizadas sobre XML (Shipments & Carriers) e CSV (sales_feed) mapeados
let linhasXmlDireto =
    Connections.xml.LogisticsHub.rawQuery(
        "SELECT s.ShipmentId, s.TrackingNumber, c.Name AS Carrier, s.Destination, s.ShippingCost FROM Shipments s JOIN Carriers c ON s.CarrierId = c.CarrierId WHERE s.ShippingCost >= @minCost ORDER BY s.ShippingCost DESC LIMIT 5",
        [ "@minCost", box 100.0 ]
    )

printfn "Consulta SQLite direta no XML retornou %d linhas" (linhasXmlDireto.Rows |> Seq.length)

Connections.sql.rawQuery(
    "SELECT OrderId AS PedidoId, Customer AS Cliente, Product AS Produto, Category AS Categoria, TotalAmount AS ValorTotal, Status FROM sales_feed WHERE Category = @cat AND TotalAmount >= @minTotal ORDER BY TotalAmount DESC LIMIT 10",
    [ "@cat", box "Electronics"; "@minTotal", box 100.0 ]
)
"""

                mkMarkdown
                    """
## 27. 🌟 Projeção de Tabelas Mapeadas Inteiras no SELECT (`alias.*`) e `fsql.extract`

No F# Query Studio, você pode projetar a entidade inteira de uma tabela mapeada (ex: `Cliente = c`) diretamente dentro do registro anônimo `select {| ... |}` ou tupla em conjunto com propriedades escalares de outras tabelas relacionadas.

> 💡 **Dica de Desmembramento (Grid Plana)**: Ao projetar uma tabela inteira dentro de um registro anônimo, use `Cliente = fsql.extract c` (ou `extract c`) para que a Grid interativa extraia automaticamente todas as propriedades (`Id`, `FirstName`, `LastName`...) em colunas individuais de primeiro nível, em vez de exibir um objeto JSON dentro da célula!
"""

                mkCode
                    """
// 27. Projeção de tabela mapeada com fsql.extract para exibir todas as colunas desmembradas na grid
let detalhesClientePedido =
    fsql {
        for c in Connections.sql.SampleStore.Customers do
        join o in Connections.sql.SampleStore.Orders on (c.Id = o.CustomerId)
        where (c.Country = "Brazil" && o.TotalAmount >= 100.0f)
        sortByDescending o.TotalAmount
        take 10
        select {|
            Cliente = fsql.extract c
            NumeroPedido = o.OrderNumber
            DataPedido = o.OrderDate
            ValorTotal = o.TotalAmount
            Status = o.Status
        |}
    }

printfn "SQL Gerado com Pushdown Curinga (c.*):\n%s" (toRawQuery detalhesClientePedido)
detalhesClientePedido
"""

                mkMarkdown
                    """
---

### 📚 Documentação & Recursos Oficiais

- 📖 [Documentação Oficial de F# (Microsoft Learn)](https://learn.microsoft.com/pt-br/dotnet/fsharp/)
- 🔍 [Guia de Expressões de Consulta F# / LINQ](https://learn.microsoft.com/pt-br/dotnet/fsharp/language-reference/query-expressions)
- ⚡ [Visão Geral do F# Interactive (FSI)](https://learn.microsoft.com/pt-br/dotnet/fsharp/tools/fsharp-interactive/)
"""
            ]

        serializeNotebook cells
