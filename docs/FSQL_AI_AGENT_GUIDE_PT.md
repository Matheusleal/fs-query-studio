# FSQL (F# Query Language) e Extensões — Guia para Agentes de IA

> **Público-Alvo**: Assistentes de Código com IA, LLMs e Agentes Autônomos desenvolvendo consultas para o **F# Query Studio**.  
> **Regra**: Ao gerar código F# que interage com bancos de dados ou fontes de dados no F# Query Studio, agentes **DEVEM** seguir rigorosamente esta especificação.

---

## 1. Sumário Executivo e Filosofia

No F# Query Studio, **`fsql`** (**F# Query Language**) é a computation expression unificada e de primeira classe projetada para consultas funcionais, fortemente tipadas e com pushdown completo para os motores de banco de dados.

### Por que `fsql` em vez do LINQ padrão `query { ... }`?
- **Pushdown de Agregados a Nível de Coluna**: Resolve a limitação do SQL Server onde `count` em coluna projetada falhava com SQL inválido.
- **Operações Correlacionadas Dedicadas**: Palavras-chave nativas para `whereExists`, `whereNotExists`, `having`, `crossApply` e `lateral`.
- **Aninhamento Transparente de Subqueries**: A mesma computation expression `fsql { ... }` funciona perfeitamente para queries de alto nível e subqueries profundamente aninhadas (sub-selects escalares, `EXISTS`, `IN`).
- **Zero Concatenação de Strings SQL Cruas**: Agentes de IA **nunca** devem emitir templates em strings SQL (`"SELECT * FROM..."`); escreva 100% de DSL pura em F#.
- **Tradução Inteligente de Dialetos**: Emite automaticamente T-SQL (SQL Server 2016+ / 2022), PL/pgSQL (PostgreSQL 10+ / 17) e SQLite 3 altamente otimizados.

---

## 2. Imports Padrão da Sessão e Acesso a Conexões

Ao escrever scripts ou células de notebook, os agentes devem incluir:

```fsharp
open System
open System.Linq
open FSharpQueryStudio.Connectors
open FSharpQueryStudio.Generated
```

### Padrão de Resolução de Tabelas: `Connections.sql`
Bancos configurados no workspace são tipados e expostos através de `Connections.sql.<NomeDaConexao>.<NomeDaTabela>`:

```fsharp
// SQL Server
let mssqlProducts = Connections.sql.SqlServer_Demo.Products
let mssqlOrders   = Connections.sql.SqlServer_Demo.Orders

// PostgreSQL (respeita convenções minúsculas do Postgres)
let pgProducts = Connections.sql.PostgreSQL_Demo.products
let pgOrders   = Connections.sql.PostgreSQL_Demo.orders

// SQLite
let sqliteProducts = Connections.sql.SQLite_Demo.Products
let sqliteOrders   = Connections.sql.SQLite_Demo.Orders
```

---

## 3. Catálogo Completo da Sintaxe `fsql`

### 3.1 Consultas Básicas, Filtros, Ordenação e Paginação
```fsharp
let paginaDeProdutos =
    fsql {
        for p in mssqlProducts do
        where (p.Price >= 50.0M && p.InStock)
        sortByDescending p.Price
        thenBy p.Name
        skip 10
        take 5
        select {| Id = p.Id; Name = p.Name; Price = p.Price |}
    } |> Seq.toList
```

### 3.2 Agregações (Pushdown para `COUNT`, `MAX`, `MIN`, `SUM`, `AVG`)
```fsharp
// Contagem a nível de tabela
let totalLinhas = fsql { for p in mssqlProducts do count }

// Contagem a nível de coluna (pushdown seguro garantido em todos os dialetos)
let totalIds = fsql { for p in mssqlProducts do select p.Id; count }

// Extremos e somas
let precoMaximo = fsql { for p in mssqlProducts do maxBy p.Price }
let precoMinimo = fsql { for p in mssqlProducts do minBy p.Price }
let somaPrecos  = fsql { for p in mssqlProducts do sumBy p.Price }
let precoMedio  = fsql { for p in mssqlProducts do averageBy p.Price }
```

### 3.3 Filtros com Lista (`IN` com Coleções e Subqueries)
```fsharp
// 1. IN parametrizado com coleções locais em F#
let categoriasAlvo = [ "Electronics"; "Hardware" ]
let filtradosPorLista =
    fsql {
        for p in mssqlProducts do
        where (categoriasAlvo.Contains(p.Category))
        select p.Name
    }

// 2. IN transpilado com subquery fsql aninhada:
// Compila para: WHERE (p.Id IN (SELECT o.ProductId FROM Orders o WHERE ...))
let produtosComGrandesPedidos =
    fsql {
        for p in mssqlProducts do
        where ((fsql { for o in mssqlOrders do where (o.Quantity >= 5); select o.ProductId }).Contains(p.Id))
        select p.Name
    }
```

### 3.4 Subqueries Correlacionadas (`whereExists` e `whereNotExists`)
Agentes devem preferir `whereExists` / `whereNotExists` em vez de comparações manuais de contagem:

```fsharp
// Compila para: WHERE EXISTS (SELECT 1 FROM Orders _sub WHERE _sub.ProductId = _t0.Id)
let produtosAtivos =
    fsql {
        for p in mssqlProducts do
        whereExists (fsql { for o in mssqlOrders do where (o.ProductId = p.Id) })
        select {| Id = p.Id; Name = p.Name |}
    }

// Compila para: WHERE NOT EXISTS (SELECT 1 FROM Orders _sub WHERE _sub.ProductId = _t0.Id)
let produtosSemPedidos =
    fsql {
        for p in mssqlProducts do
        whereNotExists (fsql { for o in mssqlOrders do where (o.ProductId = p.Id) })
        select {| Id = p.Id; Name = p.Name |}
    }
```

### 3.5 Subqueries Escalares em Projeções `select`
Calcule contagens ou somas correlacionadas por linha diretamente na projeção `SELECT`:

```fsharp
// Compila para:
// SELECT _t0.Name AS ProductName,
//        (SELECT COUNT(*) FROM Orders _sub WHERE _sub.ProductId = _t0.Id) AS OrderCount
// FROM Products _t0
let catalogo =
    fsql {
        for p in mssqlProducts do
        select {|
            ProductName = p.Name
            Price = p.Price
            OrderCount = fsql { for o in mssqlOrders do where (o.ProductId = p.Id); count }
        |}
    }
```

### 3.6 Joins Correlacionados (`crossApply` e `lateral`)
Use operações explícitas para expansão linha a linha ou top-N correlacionado:

- **SQL Server (`crossApply`)**: Transpila para `CROSS APPLY (SELECT ... OFFSET 0 ROWS FETCH NEXT N ROWS ONLY) AS _t1`.
- **PostgreSQL (`lateral`)**: Transpila para `CROSS JOIN LATERAL (SELECT ... LIMIT N) AS _t1`.
- **SQLite**: Executa com fallback transparente em memória.

```fsharp
// 1. SQL Server: CROSS APPLY
let topPedidoPorProduto =
    fsql {
        for p in mssqlProducts do
        for o in (crossApply (fsql { for o in mssqlOrders do where (o.ProductId = p.Id); sortByDescending o.UnitPrice; take 1 })) do
        select {|
            ProductName = p.Name
            TopOrderPrice = o.UnitPrice
            Quantity = o.Quantity
        |}
    }

// 2. PostgreSQL: CROSS JOIN LATERAL
let pgTopPedidoPorProduto =
    fsql {
        for p in pgProducts do
        for o in (lateral (fsql { for o in pgOrders do where (o.product_id = p.id); sortByDescending o.unit_price; take 1 })) do
        select {|
            ProductName = p.name
            TopOrderPrice = o.unit_price
        |}
    }
```

### 3.7 Agrupamentos e Cláusula `having` Dedicada
Filtros em agregados agrupados devem usar `having`:

```fsharp
// Compila para:
// GROUP BY _t0.ProductId
// HAVING (COUNT(*) >= @p0)
let produtosComMuitosPedidos =
    fsql {
        for o in mssqlOrders do
        groupBy o.ProductId into g
        having (fsql { for item in g do count } >= 2)
        select {|
            ProductId = g.Key
            TotalOrders = fsql { for item in g do count }
        |}
    }
```

### 3.8 Funções de Janela e Analytics (`fsql.window`, `rowNumber`, `rank`, `lag`, `lead`)
Funções de ranqueamento e análise mapeiam diretamente para o padrão SQL `OVER (PARTITION BY ... ORDER BY ...)`:

```fsharp
let produtosRanqueados =
    fsql {
        for p in mssqlProducts do
        select {|
            Category = p.Category
            Name = p.Name
            Price = p.Price
            RowInCat = fsql.rowNumber(fsql.window(partitionBy = p.Category, sortByDescending = p.Price))
            RankInCat = fsql.rank(fsql.window(partitionBy = p.Category, sortByDescending = p.Price))
            RunningTotal = fsql.sum(p.Price, over = fsql.window(partitionBy = p.Category, sortBy = p.Id))
        |}
    }
```

### 3.9 Common Table Expressions (CTEs) e Operações de Conjunto (Composição Transparente)
O FSQL suporta declarar CTEs e operações de conjunto em partes como variáveis separadas, combinando-as de forma transparente com **zero tabelas fictícias**:

```fsharp
// 1. CTE Sem Nome (Preferencial): Injetada de forma transparente no plano SQL sem registrar tabela fictícia
let produtosAltoEstoque =
    fsql {
        for p in Connections.sql.SampleStore.Products do
        where (p.StockQuantity >= 100)
        select p
    }

let catalogoComCteSemNome =
    fsql {
        for c in Connections.sql.SampleStore.Categories do
        withCte produtosAltoEstoque
        join p in produtosAltoEstoque on (c.Id = p.CategoryId)
        select {| Categoria = c.Name; Produto = p.Name; Estoque = p.StockQuantity |}
    }

// 2. CTE com Nome Explícito
let produtosCaros =
    fsql {
        for p in Connections.sql.SampleStore.Products do
        withCte "HighEnd" (fsql { for p2 in Connections.sql.SampleStore.Products do where (p2.Price >= 500.0f); select p2 })
        where (p.Price >= 200.0f)
        select p
    }

let produtosEconomicos =
    fsql {
        for p in Connections.sql.SampleStore.Products do
        withCte "Budget" (fsql { for p2 in Connections.sql.SampleStore.Products do where (p2.Price <= 30.0f); select p2 })
        where (p.Price < 15.0f)
        select p
    }

// 3. CTEs Recursivas em SQL Bruto (T-SQL, PostgreSQL, SQLite)
let hierarquiaOrganizacional =
    fsql {
        for e in Connections.sql.SampleStore.Employees do
        withRecursiveCteSql "Hierarquia" """
            SELECT Id, Name, ManagerId, 1 AS Nivel
            FROM Employees WHERE ManagerId IS NULL
            UNION ALL
            SELECT e.Id, e.Name, e.ManagerId, h.Nivel + 1
            FROM Employees e
            INNER JOIN Hierarquia h ON e.ManagerId = h.Id
        """
        select e
    }

// 4. Composição em 4 Formas Equivalentes (as CTEs sobem automaticamente para o WITH principal sem duplicações):

// Forma A: Dentro da Computation Expression (fsql)
let combinadosA =
    fsql {
        for p in produtosCaros do
        union produtosEconomicos
    }

// Forma B: Builder currificado
let combinadosB = fsql.union produtosCaros produtosEconomicos

// Forma C: Função de módulo
let combinadosC = union produtosCaros produtosEconomicos

// Forma D: Método de extensão IQueryable
let combinadosD = produtosCaros.Union(produtosEconomicos)

// Operações de Conjunto na CE: union, unionAll, intersect, except
let todosItens =
    fsql {
        for a in itensAtivos do
        unionAll itensArquivados
    }

let itensComuns =
    fsql {
        for r in itensVarejo do
        intersect itensAtacado
    }

let itensExclusivos =
    fsql {
        for r in itensVarejo do
        except itensAtacado
    }

// 5. Composição de Subqueries em whereExists e crossApply:
let pedidosConcluidos =
    fsql {
        for o in Connections.sql.SampleStore.Orders do
        where (o.Status = "Completed")
        select o
    }

let clientesComPedidos =
    fsql {
        for c in Connections.sql.SampleStore.Customers do
        whereExists (fsql { for o in pedidosConcluidos do where (o.CustomerId = c.Id) })
        select c
    }
```

### 3.10 Joins Externos Idiomáticos na CE `fsql`
Agentes de IA devem sempre gerar a sintaxe LINQ `GroupJoin` 100% idiomática com `into <grupo>` e `for <var> in <grupo>.DefaultIfEmpty() do`:

```fsharp
// 1. Full Outer Join diretamente dentro da fsql
let fornecedoresProdutos =
    fsql {
        for p in Connections.sql.SampleStore.Products do
        fullOuterJoin s in Connections.sql.SampleStore.Suppliers on (p.SupplierId = s.Id) into suppliers
        for s in suppliers.DefaultIfEmpty() do
        select {|
            Product = p.Name
            Supplier = (if isNull (box s) then "Desconhecido" else s.Name)
        |}
    }

// 2. Right Outer Join diretamente dentro da fsql
let rightJoined =
    fsql {
        for p in Connections.sql.SampleStore.Products do
        rightOuterJoin c in Connections.sql.SampleStore.Categories on (p.CategoryId = c.Id) into categories
        for c in categories.DefaultIfEmpty() do
        select {| Category = c.Name; Product = (if isNull (box p) then "Nenhum" else p.Name) |}
    }
```

### 3.11 Inspeção de Plano de Execução (`explain` & `fsql.explain`)
Agentes de IA podem inspecionar o plano de execução diretamente na CE:

```fsharp
let inspecaoPlano =
    fsql {
        for p in Connections.sql.SampleStore.Products do
        where (p.StockQuantity > 50)
        explain
        select p
    }
```

---

## 4. Ferramentas de Inspeção e Diagnóstico de Queries

Para depuração, validação e verificação de consultas, os agentes dispõem de ferramentas nativas:

### 4.1 `toRawQuery` (Recomendado para Exibição e Logs)
Formata e indenta a query SQL com declaração de parâmetros em cabeçalho de comentário:

```fsharp
let sqlText = toRawQuery produtosComMuitosPedidos
printfn "%s" sqlText
```

### 4.2 `toRawSql` (String em linha única)
```fsharp
let raw = toRawSql produtosComMuitosPedidos
```

### 4.3 `toRawSqlWithParameters` (Tupla para asserções em testes)
```fsharp
let (sql, parameters) = toRawSqlWithParameters produtosComMuitosPedidos
```

### 4.4 `explainQuery` e `.Explain()` (Diagnóstico de Plano no Motor)
Obtém o plano de execução real gerado pelo motor do banco:
- SQLite: `EXPLAIN QUERY PLAN`
- PostgreSQL: `EXPLAIN`
- SQL Server: `SET SHOWPLAN_TEXT ON ... OFF`

---

## 5. Regras de Pushdown por Dialeto

| Recurso | SQL Server | PostgreSQL | SQLite |
| :--- | :--- | :--- | :--- |
| **Limitação / Paginação** | `OFFSET n ROWS FETCH NEXT m ROWS ONLY` | `LIMIT m OFFSET n` | `LIMIT m OFFSET n` |
| **Cross Apply** | Nativo `CROSS APPLY (...)` | Emitido como `CROSS JOIN LATERAL` | Fallback em memória transparente |
| **Lateral Join** | Emitido como `CROSS APPLY` | Nativo `CROSS JOIN LATERAL (...)` | Fallback em memória transparente |
| **Count Escalar sem GroupBy** | Omite `ORDER BY` (evita erro T-SQL 412) | Pushdown nativo | Pushdown nativo |
| **Funções de Janela OVER** | Nativo (Auto `ORDER BY (SELECT NULL)` para ranking) | Nativo `OVER (...)` | Nativo `OVER (...)` (SQLite 3.25+) |
| **Extração JSON** | `JSON_VALUE(col, '$.path')` | `col ->> 'path'` / `col #>> '{path}'` | `json_extract(col, '$.path')` |
| **Common Table Expressions** | `WITH [name] AS (...)` | `WITH [RECURSIVE] "name" AS (...)` | `WITH [RECURSIVE] [name] AS (...)` |
| **Operações de Conjunto** | `UNION [ALL]`, `INTERSECT`, `EXCEPT` | `UNION [ALL]`, `INTERSECT`, `EXCEPT` | `UNION [ALL]`, `INTERSECT`, `EXCEPT` |
| **Outer Joins** | `FULL OUTER JOIN`, `RIGHT OUTER JOIN` | `FULL OUTER JOIN`, `RIGHT OUTER JOIN` | `FULL OUTER JOIN`, `RIGHT OUTER JOIN` |
| **Parâmetros de String** | `N'valor'` / `@pN` | `'valor'` / `@pN` | `'valor'` / `@pN` |
| **Tipos Booleanos** | Transpilado para `1 / 0` (bit) | Nativo `true / false` | `1 / 0` |

---

## 6. Regras de Ouro para Agentes de IA (Guardrails)

1. **NUNCA use `query { ... }`**: O LINQ padrão carece de `whereExists`, `having`, `crossApply`, `lateral` e correções de pushdown. Use sempre `fsql { ... }`.
2. **NUNCA use `fquery`**: `fquery` é um alias legado. Use consistentemente `fsql`.
3. **NÃO escreva queries em strings SQL cruas**: Evite `Connections.query<T> "SELECT * FROM..."`. Expresse toda a lógica DQL usando `fsql { ... }`.
4. **Use Anonymous Records para Projeções**: Projeções multi-coluna devem usar `{| Col1 = x.Prop1; Col2 = x.Prop2 |}`. Produz registros fortemente tipados sem declarações prévias.
5. **Inspecione Queries Complexas com `toRawQuery`**: O truncamento de strings é desabilitado em sessões FSI; `toRawQuery` exibe a query completa com indentação e documentação de parâmetros.
