# F# Query Studio — Guia Completo do Usuário

[🇧🇷 Versão em Português](USER_GUIDE_PT.md) | [🇺🇸 English Version](USER_GUIDE.md)

O **F# Query Studio** é uma extensão profissional para o Visual Studio Code que transforma o seu editor em um estúdio completo e interativo para análise, consulta e engenharia de dados utilizando a linguagem **F#**.

Escreva consultas com segurança de tipos em tempo de compilação, alta expressividade e performance através de **F# Query Notebooks (`.fsqnb`)**, integrando bancos de dados relacionais (**PostgreSQL, SQL Server, MySQL, SQLite**), arquivos locais (**CSV, Excel/XLSX, JSON**) e **APIs REST**.

---

## Documentação Oficial e Links Úteis

- 📘 **Documentação Oficial de F#**: [Microsoft Learn — Visão Geral do F#](https://learn.microsoft.com/dotnet/fsharp/)
- 🔍 **Expressões de Consulta F# (LINQ)**: [Microsoft Learn — Query Expressions Reference](https://learn.microsoft.com/dotnet/fsharp/language-reference/query-expressions)
- ⚡ **F# Interativo (FSI)**: [Microsoft Learn — Guia do F# Interactive](https://learn.microsoft.com/dotnet/fsharp/tools/fsharp-interactive/)
- 🗄️ **Documentação do SQLProvider**: [SQLProvider Documentation](https://fsprojects.github.io/SQLProvider/)
- 📦 **Repositório e Issues no GitHub**: [fs-query-studio no GitHub](https://github.com/Matheusleal/fs-query-studio)

---

## Sumário

1. [Requisitos e Instalação](#1-requisitos-e-instalação)
2. [Interface e Acesso Rápido](#2-interface-e-acesso-rápido)
3. [Início Rápido: Criando Dados de Exemplo (SQLite e CSV)](#3-início-rápido-criando-dados-de-exemplo-sqlite-e-csv)
4. [Gerenciamento de Conexões (Sidebar Explorer)](#4-gerenciamento-de-conexões-sidebar-explorer)
5. [Auto-complete Inteligente de Conexões](#5-auto-complete-inteligente-de-conexões)
6. [Consultas LINQ e Type Provider em Background](#6-consultas-linq-e-type-provider-em-background)
   - [Filtragem Básica e Projeção](#filtragem-básica-e-projeção)
   - [Pushdown Completo de Consultas para SQL](#pushdown-completo-de-consultas-para-sql)
   - [Joins Multi-Propriedades sem Explosão de Memória](#joins-multi-propriedades-sem-explosão-de-memória)
   - [Inspeção do SQL Gerado (toRawSql)](#inspeção-do-sql-gerado-torawsql)
   - [Agrupamentos e Agregações](#agrupamentos-e-agregações)
   - [Cache Persistente em Disco SQLite para CSV e Excel](#cache-persistente-em-disco-sqlite-para-csv-e-excel)
   - [Descoberta Automática via Pasta data_sources](#descoberta-automática-via-pasta-data_sources)
7. [Consultas SQL Tradicionais Parametrizadas (Raw SQL)](#7-consultas-sql-tradicionais-parametrizadas-raw-sql)
8. [Grid Interativo de Resultados](#8-grid-interativo-de-resultados)
9. [Recursos de Linguagem e Go to Definition (F12)](#9-recursos-de-linguagem-e-go-to-definition-f12)
10. [Ciclo de Vida do Notebook e Sessão FSI](#10-ciclo-de-vida-do-notebook-e-sessão-fsi)
11. [Diagnóstico e Solução de Problemas](#11-diagnóstico-e-solução-de-problemas)
12. [Benchmarks de Performance e Confiabilidade de UI](#12-benchmarks-de-performance-e-confiabilidade-de-ui)

---

## 1. Requisitos e Instalação

- **.NET 10 SDK** instalado e acessível no `PATH` do sistema (`dotnet --version` ≥ 10.0). O F# Query Studio executa um processo worker nativo empacotado que gerencia a sessão interativa FSI e o servidor de linguagem (FSAC).
- **VS Code 1.90** ou superior.
- Suporte nativo multiplataforma: **Windows, Linux e macOS**.

### Instalação Manual do Pacote VSIX

Para instalar localmente a partir do arquivo `.vsix`:
```bash
code --install-extension fs-query-studio.vsix --force
```

---

## 2. Interface e Acesso Rápido

O F# Query Studio se integra perfeitamente ao VS Code em três áreas principais:

1. **Barra de Atividades (Activity Bar)**: Ícone dedicado com o logo do F# Query Studio na barra lateral esquerda. Nele você visualiza todas as conexões registradas, esquemas, tabelas e metadados de colunas.
2. **Ícones de Ação no Menu**:
   - `$(book)` **Documentação**: Abre este guia lado a lado com seu código (disponível no cabeçalho do painel Connections e no editor do notebook).
   - `$(database)` **Criar Base de Dados de Teste**: Gera automaticamente bases SQLite, CSV e XLSX fictícias em `data_sources/` com notebook demonstrativo em 1 clique.
   - `$(add)` **Adicionar Conexão**: Abre o assistente interativo para cadastrar bancos, arquivos ou APIs.
   - `$(refresh)` **Atualizar**: Recarrega o esquema e regenera os tipos de entidades.
3. **Command Palette (`Ctrl+Shift+P` / `Cmd+Shift+P`)**:
   - `F# Query Studio: Create Sample Data (SQLite, CSV & XLSX)`
   - `F# Query Studio: Open Documentation`
   - `F# Query Studio: New F# Query Notebook`
   - `F# Query Studio: Restart Notebook Session`
   - `F# Query Studio: Check Worker`

---

## 3. Início Rápido: Criando Dados de Exemplo (SQLite, CSV e XLSX)

Quer começar a explorar consultas LINQ imediatamente sem precisar configurar um banco externo?

Clique no botão **`Create Sample Data`** (`$(database)`) no cabeçalho do painel **Connections**, ou selecione `F# Query Studio: Create Sample Data (SQLite, CSV & XLSX)` no Command Palette.

### O Que É Criado Automaticamente:
1. **`sample_store.db` (SQLite)**: Um banco relacional completo contendo 5 tabelas populadas com dados consistentes:
   - `Categories` (`Id`, `Name`, `Description`)
   - `Products` (`Id`, `CategoryId`, `Name`, `UnitPrice`, `StockQuantity`)
   - `Customers` (`Id`, `FullName`, `Email`, `Country`, `City`, `CreatedAt`)
   - `Orders` (`Id`, `CustomerId`, `OrderDate`, `Status`, `TotalAmount`)
   - `OrderItems` (`Id`, `OrderId`, `ProductId`, `Quantity`, `UnitPrice`)
2. **`sales_feed.csv`**: Arquivo CSV de vendas com 10 transações reais:
   - `TransactionId`, `Date`, `StoreCity`, `Category`, `Product`, `Quantity`, `UnitPrice`, `Total`
3. **`sample_demo_pt.fsqnb` e `sample_demo.fsqnb`**: Notebooks interativos executáveis com formatação rica, tabelas comparativas, atalhos de teclado e exemplos completos de LINQ, Joins relacionais, agregações analíticas (`sumBy`, `groupBy`), consultas em CSV e SQL tradicional.
4. As conexões `SampleStore` e `SalesFeed` são registradas no seu workspace e os tipos de entidades são mapeados automaticamente em segundo plano!

---

## 4. Gerenciamento de Conexões (Sidebar Explorer)

### Fontes de Dados Suportadas

| Tipo | Descrição | Exemplo de Configuração |
|---|---|---|
| **SQLite** | Banco local em arquivo | Caminho: `sample_store.db` ou `C:\dados\app.db` |
| **PostgreSQL** | Suporte completo a múltiplos esquemas (`public`, schemas customizados, views) | `Host=localhost;Database=mydb;Username=postgres;Password={password}` |
| **SQL Server** | Suporte corporativo a schemas (`dbo`, schemas customizados, views) | `Server=localhost;Database=master;User Id=sa;Password={password};TrustServerCertificate=True` |
| **MySQL** | Banco relacional (catálogos, tabelas e views) | `Server=localhost;Database=loja;Uid=root;Pwd={password};` |
| **CSV** | Arquivos delimitados (vírgula, ponto-e-vírgula, tabulação) | Caminho: `sales_feed.csv` |
| **Excel (XLSX)** | Planilhas Excel com múltiplas abas | Caminho: `financas.xlsx` |
| **JSON** | Arquivos JSON locais (objeto ou lista de objetos) | Caminho: `catalogo.json` |
| **REST API** | Endpoints HTTP/HTTPS retornando JSON | URL base: `https://api.github.com` |

### Segurança e Armazenamento Seguro de Credenciais

As senhas **nunca são armazenadas em texto puro**. Ao cadastrar uma conexão com `{password}`, a credencial é protegida com criptografia nativa no chaveiro seguro do seu sistema operacional através da API `SecretStorage` do VS Code.

---

## 5. Auto-complete Inteligente de Conexões e Badges de Tipo

Quando você edita células do notebook, o F# Query Studio oferece **IntelliSense contextual organizado de forma clara por categoria de conexão**:

- `Connections.sql.<NomeConexao>` ➔ Acesso direto às tabelas relacionais (SQLite, PostgreSQL, SQL Server, MySQL) e `.rawQuery` isolado.
- `Connections.csv.<NomeConexao>` ➔ Acesso direto a linhas tipadas de CSV e `.Raw()`.
- `Connections.excel.<NomeConexao>` ➔ Acesso direto a planilhas tipadas de Excel e `.Raw()`.
- `Connections.rest.<NomeConexao>` ➔ Acesso direto a endpoints de API REST e `.SendAsync()`.

### Badges de Tipo Alinhados à Direita no Autocomplete
A lista suspensa de sugestões exibe o tipo concreto ou categoria alinhado à direita para identificação imediata:
- **Campos e Propriedades de Records**: Exibe o tipo exato (`string`, `int`, `decimal`, `bool`, `DateTime`, etc.).
- **Métodos e Funções**: Exibe `method`.
- **Conexões**: Exibe a categoria do conector (`sql`, `csv`, `excel`, `rest`).

### Indicador de Mapeamento de Entidades em Tempo Real
Ao adicionar ou modificar conexões, um spinner animado é exibido na barra de status do VS Code (`$(sync~spin) Mapping entities...`), confirmando em tempo real a inspeção de esquema e geração dos tipos F#.

---

## 6. Consultas LINQ e Type Provider em Background

O F# Query Studio analisa as conexões do seu workspace e **gera automaticamente em segundo plano tipos F# fortemente tipados** para todas as tabelas e colunas.

Você não precisa configurar Type Providers manuais nem compilar projetos auxiliares: os tipos de entidade estão disponíveis imediatamente sob cada categoria!

### Filtragem Básica e Projeção com `fsql` (F# Query Language)

```fsharp
// 1. Consulta funcional direta no banco via Connections.sql e fsql
fsql {
    for p in Connections.sql.SampleStore.Products do
    where (p.UnitPrice > 50.0M)
    sortByDescending p.UnitPrice
    select (p.Id, p.Name, p.UnitPrice, p.StockQuantity)
}
```

### Pushdown Completo de Consultas para SQL

Em vez de executar queries ingênuas com `SELECT *` e filtrar tudo na memória do cliente, o F# Query Studio conta com uma **Engine de Pushdown para SQL nível Entity Framework**:
- **Projeções Específicas de Colunas**: `select {| Id = o.Id; Total = o.Total |}` gera `SELECT _t0.Id, _t0.Total FROM ...`
- **Filtros Parametrizados**: `where (o.TotalAmount >= 100.0f)` gera `WHERE (_t0.TotalAmount >= @p0)`
- **Ordenação e Paginação**: `sortBy`, `sortByDescending`, `take N`, `skip M` geram diretamente `ORDER BY ... LIMIT N OFFSET M` (ou `OFFSET N ROWS FETCH NEXT M ROWS ONLY` no SQL Server)
- **Agregações no Servidor**: `count` (de tabela ou coluna projetada), `maxBy`, `minBy`, `sumBy`, `averageBy` traduzem diretamente para `COUNT(*)`, `MAX(...)`, `MIN(...)`, `SUM(...)`, `AVG(...)`
- **Subqueries Correlacionadas**: `whereExists (fsql { ... })` e `whereNotExists (fsql { ... })` geram `WHERE EXISTS (SELECT 1 ...)`
- **Joins Correlacionados**: `crossApply (fsql { ... })` (SQL Server) e `lateral (fsql { ... })` (PostgreSQL)
- **Agrupamento e Having**: `groupBy ... into g; having (fsql { for item in g do count } >= n)` gera `GROUP BY ... HAVING (COUNT(*) >= @p0)`

### Joins Multi-Propriedades sem Explosão de Memória

As expressões de consulta F# suportam junções relacionais compostas usando tuplas:

```fsharp
// Join tipado multi-propriedade compilado para SQL sem produtos cartesianos
fsql {
    for s in Connections.sql.Transactions.Sales do
    join t in Connections.sql.Transactions.Targets on ((s.StoreId, s.Region) = (t.StoreId, t.Region))
    where (s.TotalAmount >= 250.0M)
    select {| Loja = s.StoreId; Regiao = s.Region; Valor = s.TotalAmount; Gestor = t.Manager |}
}
```

Esta query compila diretamente para a instrução SQL indexada:
```sql
SELECT _t0.StoreId AS Loja, _t0.Region AS Regiao, _t0.TotalAmount AS Valor, _t1.Manager AS Gestor
FROM Transactions _t0
INNER JOIN Targets _t1 ON _t0.StoreId = _t1.StoreId AND _t0.Region = _t1.Region
WHERE (_t0.TotalAmount >= @p0)
```

Índices B-tree compostos automáticos (`ix_tabela_StoreId_Region`) garantem tempos de resposta inferiores a 100ms, mesmo cruzando centenas de milhares de linhas.

### Inspeção do SQL Gerado (`toRawQuery` e `toRawSql`)

Deseja verificar o SQL parametrizado exato com indentação e documentação de parâmetros antes ou durante a execução contra a base de dados?
Use `toRawQuery`:

```fsharp
let minhaQuery =
    fsql {
        for o in Connections.sql.SampleStore.Orders do
        join c in Connections.sql.SampleStore.Customers on (o.CustomerId = c.Id)
        where (o.Status = "Completed")
        whereExists (fsql { for i in Connections.sql.SampleStore.OrderItems do where (i.OrderId = o.Id) })
        select {| PedidoId = o.Id; Cliente = c.FirstName + " " + c.LastName; Total = o.TotalAmount |}
    }

// Imprime o SQL formatado e seus parâmetros no terminal sem acessar o banco!
printfn "SQL Gerado:\n%s" (toRawQuery minhaQuery)

// Executa e exibe o grid de dados interativo
minhaQuery
```

> **Guia para Agentes de IA**: Consulte [`docs/FSQL_AI_AGENT_GUIDE.md`](../../docs/FSQL_AI_AGENT_GUIDE.md) para especificações formais de prompts e geração de queries.

### Recursos Avançados de Consulta (Joins, Agregações, Subqueries e Pivot)

O F# Query Studio suporta o conjunto completo de operações analíticas relacionais compiladas diretamente para SQL nativo ou processadas em memória com alta performance:

#### 1. Joins (INNER, LEFT OUTER JOIN e Encadeamento)
Para realizar `LEFT JOIN` com múltiplas tabelas, utilize a sintaxe canônica do F# `leftOuterJoin ... in ... on ... into g` combinado com `for ... in g.DefaultIfEmpty()`:

```fsharp
query {
    for order in csv.Orders do
    join orderItem in csv.OrderItems on (order.order_id = orderItem.order_id)
    join customer in csv.Customers on (order.customer_id = customer.customer_id)
    join product in csv.Products on (orderItem.product_id = product.product_id)
    join payment in csv.Payments on (order.order_id = payment.order_id)

    // Encadeamento de múltiplos Left Outer Joins
    leftOuterJoin shipmentGroup in csv.Shipments on (order.order_id = shipmentGroup.order_id) into shipmentGroup
    leftOuterJoin returns in csv.Returns on (orderItem.order_item_id = returns.order_item_id) into returnGroup

    for shipment in shipmentGroup.DefaultIfEmpty() do
    for returns in returnGroup.DefaultIfEmpty() do

    select {|
        Order = order.order_id
        Data = order.order_date
        Status = order.status
        Item = product.product_name
        Qtd = orderItem.quantity
        Total = orderItem.line_total
        // Expressões condicionais com tratamento de nulo compiladas para COALESCE / CASE WHEN no SQL
        Transportadora = (if isNull (box shipment) then "N/A" else shipment.carrier)
        MotivoDevolucao = (if isNull (box returns) then "N/A" else returns.reason)
    |}
}
```

#### 2. Agrupamentos e Funções Agregadoras (`groupBy`, `count`, `sum`, `min`, `max`, `average`)
Agrupamentos compilam diretamente para instruções `GROUP BY` e `HAVING` indexadas no banco:

```fsharp
query {
    for p in csv.Products do
    groupBy p.Category into g
    select {|
        Categoria = g.Key
        Quantidade = g.Count()
        PrecoMinimo = g.Min(fun x -> x.Price)
        PrecoMaximo = g.Max(fun x -> x.Price)
        Media = g.Average(fun x -> x.Price)
        Total = g.Sum(fun x -> x.Price)
    |}
}
```

Métodos terminais escalares também podem ser invocados diretamente:
```fsharp
let totalPedidos = csv.Orders.Count()
let faturamentoTotal = csv.OrderItems.Sum(fun i -> i.line_total)
```

#### 3. Limitadores e Paginação (`distinct`, `take`, `skip`, `head`)
```fsharp
query {
    for p in csv.Products do
    distinct
    sortByDescending p.Price
    skip 10
    take 5
    select p.Category
}
```

#### 4. Subqueries e Subselects (`WHERE col IN (SELECT ...)`)
Você pode compor subconsultas tipadas que são convertidas diretamente em subselects SQL nativos (`IN` e `EXISTS`):

```fsharp
let pedidosComDesconto =
    query {
        for item in csv.OrderItems do
        where (item.discount > 0.0M)
        select item.order_id
    }

query {
    for o in csv.Orders do
    where (pedidosComDesconto.Contains(o.order_id))
    select o
}
```

#### 5. Cross-tabulation e Pivot (Dinâmico e Em Memória)
- **SQL Pivot Dinâmico (`Connections.sql.pivot`)**: Executa agregação condicional com `CASE WHEN` dinamicamente no motor SQL:
  ```fsharp
  Connections.sql.pivot "Vendas" ["Ano"] "Produto" "Valor" ~aggFunc:"SUM"
  ```
- **Pivot em Memória (`Seq.pivot` / `Connections.pivot`)**: Realiza cross-tabulation direto em coleções tipadas:
  ```fsharp
  let tabelaPivot =
      dadosVendas
      |> Seq.pivot (fun r -> r.Ano) (fun r -> r.Produto) (fun r -> r.Valor) Seq.sum
  ```

### Cache Persistente em Disco SQLite para CSV e Excel

Ao cadastrar ou importar arquivos planos (`.csv`, `.xlsx`), o F# Query Studio pré-carrega os dados de forma assíncrona em um banco SQLite em disco em:
`<workspace>/.fsqs/cache/engine.db`

- **Validação Inteligente de Invalidação**: O motor verifica a data de modificação (`mtime`) e o tamanho em bytes do arquivo. Arquivos inalterados são reaproveitados instantaneamente com zero atraso nas próximas consultas.
- **Aquecimento Assíncrono Imediato**: A sincronização começa imediatamente em segundo plano ao registrar conexões ou abrir o workspace.
- **Feedback Visual na Barra de Status**: A barra do VS Code exibe `$(sync~spin) Sincronizando fontes de dados...` durante o carregamento e `$(database) Fontes prontas` ao finalizar. Você também pode rodar o comando **F# Query Studio: Sync Data Sources Cache** pela Command Palette.

### Descoberta Automática & Observador em Tempo Real via Pasta `data_sources`

Evite configurações manuais organizando seus arquivos na pasta `data_sources`:
1. **Ativação Sob Demanda**: Para evitar consumo desnecessário de CPU e memória RAM enquanto a extensão não estiver em uso ativo, a detecção automática na pasta `data_sources/` é inicializada somente após selecionar a opção **Auto-Import Data Sources** (`$(folder-library)`) no menu principal da visão de Conexões, na tela inicial de boas-vindas ou via comando `F# Query Studio: Auto-Import Data Sources Directory`.
2. **Observador em Tempo Real**: Uma vez ativado, a extensão monitora continuamente a pasta `data_sources/`. Sempre que você mover, colar, editar ou excluir arquivos `.csv`, `.xlsx`, `.db` ou `.sqlite`, o F# Query Studio detecta as alterações automaticamente, registra novas conexões, remove arquivos excluídos, atualiza a árvore de conexões e sincroniza os dados no cache SQLite em disco com feedback visual (`$(sync~spin)`).
3. **Botão de Pausa e Play (`$(debug-pause)` / `$(play)`)**: Um botão dedicado na barra de ferramentas da visão de Conexões (e no item da barra de status) permite pausar todos os observadores e encerrar imediatamente o processo worker em segundo plano a qualquer momento, liberando 100% da RAM e CPU até que você decida retomar ou executar uma célula de notebook.

### Tipos de Coluna Personalizados & Transformações com Fórmulas (Excel & CSV)

Ao trabalhar com arquivos tabulares (planilhas Excel `.xlsx` e arquivos CSV), dados brutos frequentemente contêm inconsistências de formatação: separadores decimais e de milhar localizados (`1.234,56`), símbolos de moeda (`R$`, `$`, `€`), percentuais, zeros à esquerda ou números decimais longos que poderiam ser truncados para inteiro. O F# Query Studio inclui um pipeline integrado de transformações que trata e converte os dados **antes** da inferência de tipos e da sincronização no SQLite, proporcionando total controle sobre os tipos F# e o esquema do banco de dados.

#### 1. Como e Onde Configurar

Você pode configurar a sobrescrita de tipos e as fórmulas de transformação de duas maneiras:

##### Opção A: Pela Interface do VS Code (GUI)
1. No painel lateral **Connections**, clique em **Add Connection** (`+`) e escolha **CSV** ou **Excel** (ou clique com o botão direito em uma conexão existente e selecione **Edit Connection**).
2. Preencha o caminho do arquivo e as opções de conexão.
3. No campo **Column Types Override (optional)**, informe os tipos F# desejados (ex: `PrecoUnitario=decimal, Quantidade=int`).
4. No campo **Column Transforms / Formulas (optional)**, informe as fórmulas de limpeza (ex: `PrecoUnitario=replace('.', '').replace(',', '.'), Codigo=trim`).
5. Clique em **Create Connection** (ou **Save**). O esquema tipado e o cache SQLite serão imediatamente atualizados com os dados tratados e os registros F# gerados.

##### Opção B: No Arquivo `.fsqs/connections.json` do Workspace
Todas as conexões do workspace são armazenadas em `.fsqs/connections.json`. Você pode editar esse arquivo diretamente. Ambas as sintaxes (texto delimitado por vírgulas ou objeto JSON estruturado) são suportadas:

```json
{
  "connections": [
    {
      "name": "RelatorioFinanceiro",
      "kind": "Excel",
      "configuration": {
        "filePath": "data_sources/relatorio_financeiro.xlsx",
        "sheetName": "Receitas_Q4",
        "columnTypes": {
          "Receita": "decimal",
          "TaxaCambio": "double",
          "UnidadesVendidas": "int",
          "AliquotaImposto": "double",
          "CodigoConta": "string"
        },
        "columnTransforms": {
          "Receita": "replace('R$', '').replace('.', '').replace(',', '.').trim",
          "TaxaCambio": "replace(',', '.')",
          "AliquotaImposto": "divide(100)",
          "CodigoConta": "trim"
        }
      }
    },
    {
      "name": "PedidosCsv",
      "kind": "Csv",
      "configuration": {
        "filePath": "data_sources/pedidos.csv",
        "delimiter": ";",
        "columnTypes": "ValorTotal=decimal, Desconto=double, IdPedido=int64",
        "columnTransforms": "ValorTotal=replace('.', '').replace(',', '.'), Desconto=divide(100)"
      }
    }
  ]
}
```

#### 2. Tipos F# Suportados em `columnTypes`

| Tipo Alvo | Tipo .NET / F# | Descrição & Caso de Uso Recomendado |
|---|---|---|
| `decimal` | `System.Decimal` | **Alta precisão (128-bit, 28-29 dígitos significativos)**. Obrigatório para moedas, finanças e números com muitas casas decimais (ex: `1234567.890123456`). Impede arredondamento indevido ou truncamento para inteiro. |
| `double` / `float` | `System.Double` | Ponto flutuante IEEE de 64 bits. Ideal para coordenadas (GPS latitude/longitude), medições científicas ou taxas contínuas. |
| `int` | `System.Int32` | Inteiro com sinal de 32 bits (`-2.147.483.648` a `2.147.483.647`). |
| `int64` | `System.Int64` | Inteiro com sinal de 64 bits (para IDs grandes, carimbos de tempo em milissegundos ou contadores de grande escala). |
| `bool` | `System.Boolean` | Valores lógicos booleanos. Reconhece `true`/`false`, `1`/`0`, `sim`/`nao`, `yes`/`no`. |
| `System.DateTime` | `System.DateTime` | Datas e carimbos de tempo formatados (`YYYY-MM-DD`, ISO 8601 ou formatos regionais). |
| `string` | `System.String` | Texto literal. Útil para preservar zeros à esquerda em CEPs, CPFs/CNPJs ou códigos de produto (ex: `"0042"`). |
| `<tipo> option` | `Option<'T>` | Permite explicitamente valores nulos, vazios ou marcadores sentinela (`N/A`, `-`, `null`). Ex: `decimal option`, `int option`. |

#### 3. Funções e Fórmulas em `columnTransforms`

As fórmulas podem ser aplicadas por coluna e encadeadas usando a sintaxe de ponto (`.`):

| Expressão da Fórmula | Descrição | Exemplo de Entrada | Saída Resultante |
|---|---|---|---|
| `replace('de', 'para')` | Substitui todas as ocorrências de um caractere ou texto por outro. Aspas simples (`'`) e duplas (`"`) são aceitas. | `replace(',', '.')` em `"12,50"` | `"12.50"` |
| `replace('f1', 't1').replace('f2', 't2')` | **Substituições encadeadas**: aplica as transformações sequencialmente da esquerda para a direita. | `replace('.', '').replace(',', '.')` em `"1.234,56"` | `"1234.56"` |
| `trim` | Remove espaços em branco do início e do final da string. | `trim` em `"  ABC123  "` | `"ABC123"` |
| `multiply(fator)` | Multiplica o valor numérico pelo fator informado (aceita decimais). | `multiply(100)` em `"0.15"` | `"15"` |
| `divide(divisor)` | Divide o valor numérico pelo divisor informado (ex: conversão de percentual para taxa fracionária). | `divide(100)` em `"25"` | `"0.25"` |
| `decimal` / `double` | Normaliza vírgulas para ponto decimal (`replace(',', '.')`). | `decimal` em `"99,99"` | `"99.99"` |
| `string` / `toString` | Trata o valor estritamente como texto sem conversão. | `string` em `0123` | `"0123"` |

#### 4. Cenários Práticos do Dia a Dia

##### Cenário 1: Formato Brasileiro de Moeda e Números (`"1.234,56"`)
No padrão brasileiro, o ponto (`.`) separa milhares e a vírgula (`,`) separa casas decimais. Leitores convencionais podem se perder com múltiplos pontos e vírgulas:
```json
{
  "columnTypes": "ValorTotal=decimal, Saldo=decimal",
  "columnTransforms": "ValorTotal=replace('.', '').replace(',', '.'), Saldo=replace('R$', '').replace('.', '').replace(',', '.').trim"
}
```

##### Cenário 2: Preservação de Precisão em Números com Muitas Casas Decimais
Quando o Excel ou CSV armazena números com muitas casas decimais (como `1234567.890123456789`), definir `columnTypes` garante que o F# Query Studio crie uma coluna `NUMERIC` no SQLite e um campo F# `decimal`, eliminando o risco de conversão incorreta para inteiro:
```json
{
  "columnTypes": "LeituraSensor=decimal, Latitude=double, Longitude=double",
  "columnTransforms": "LeituraSensor=trim, Latitude=replace(',', '.'), Longitude=replace(',', '.')"
}
```

##### Cenário 3: Conversão de Percentuais em Frações Numéricas
Se uma planilha contém percentuais informados como `15` ou `15%`, você pode remover o símbolo `%` e dividir por 100:
```json
{
  "columnTypes": "TaxaImposto=double, DescontoPercentual=double",
  "columnTransforms": "TaxaImposto=replace('%', '').divide(100), DescontoPercentual=divide(100)"
}
```

##### Cenário 4: Preservação de Zeros à Esquerda em Códigos e Identificadores
Se um CSV contém códigos de produtos, CPFs ou CEPs com zeros à esquerda (ex: `001234`):
```json
{
  "columnTypes": "Cep=string, Sku=string",
  "columnTransforms": "Cep=trim, Sku=trim"
}
```

#### 5. Utilizando as Colunas Tipadas em Consultas F#

Após configuradas, o F# Query Studio gera tipos fortes e inteligência de código (`autocomplete` e verificação em tempo de compilação) que refletem os tipos sobrescritos em `Connections.csv.<Conn>` e `Connections.excel.<Conn>`:

```fsharp
query {
    for pedido in Connections.excel.RelatorioFinanceiro.Receitas_Q4 do
    // pedido.Receita é tipado como System.Decimal, pedido.TaxaCambio como double
    where (pedido.Receita > 1000.0M && pedido.AliquotaImposto <= 0.20)
    sortByDescending pedido.Receita
    select {| Conta = pedido.CodigoConta; Receita = pedido.Receita; ReceitaLiquida = pedido.Receita * (1.0M - decimal pedido.AliquotaImposto) |}
}
```

---

## 7. Consultas SQL Tradicionais Parametrizadas (Raw SQL)

Quando você precisa executar queries SQL customizadas, CTEs ou funções nativas do banco de dados, use `Connections.sql`:

```fsharp
Connections.sql
    "SampleStore"
    """
    SELECT 
        c.FullName,
        COUNT(o.Id) AS OrderCount,
        SUM(o.TotalAmount) AS TotalSpent
    FROM Customers c
    LEFT JOIN Orders o ON c.Id = o.CustomerId
    WHERE c.Country = @country
    GROUP BY c.Id, c.FullName
    ORDER BY TotalSpent DESC
    """
    [ "country", box "USA" ]
```

Todos os parâmetros utilizam binding nativo de ADO.NET, protegendo contra injeção de SQL.

---

## 8. Referência da API do Módulo Connections

O módulo `Connections` fornece a interface programática principal dentro dos seus notebooks:

### Categorias de Conexão e Acesso Tipado
| Categoria / Função | Tipo / Assinatura | Descrição |
|---|---|---|
| `Connections.sql.<Conn>` | `DatabaseConnection` | Acesso às tabelas relacionais e execução isolada de `.rawQuery "..." [ ... ]`. |
| `Connections.csv.<Conn>` | `CsvConnector.Data` | Acesso às linhas fortemente tipadas do CSV (implementa `IQueryable<'Row>`) e `.Raw()`. |
| `Connections.excel.<Conn>` | `ExcelConnector.Data` | Acesso às abas fortemente tipadas do Excel (implementa `IQueryable<'Row>`), pushdown `SheetQueryable` e `.Raw()`. |
| `Connections.rest.<Conn>` | `RestConnector.Data` | Acesso aos itens tipados da REST API e `.SendAsync()`. |
| `Connections.getConnectionString` | `string -> string` | Retorna a connection string resolvida com as credenciais seguras injetadas. |

### Funções Auxiliares de Execução
| Função | Assinatura | Descrição |
|---|---|---|
| `Connections.query<'T>` | `string -> string -> (string * obj) list -> 'T seq` | Executa SQL e mapeia as linhas diretamente para uma sequência de registros tipados `'T seq`. |
| `Connections.queryCsv<'T>` | `string -> 'T seq` | Mapeia linhas do CSV diretamente para registros fortemente tipados `'T seq`. |
| `Connections.queryExcel<'T>` | `string -> int option -> 'T seq` | Mapeia linhas do Excel diretamente para registros `'T seq`. |
| `Connections.queryExcelSheet<'T>` | `string -> string option -> int option -> 'T seq` | Lê uma aba específica do Excel para registros `'T seq`. |
| `Connections.queryJson<'T>` | `string -> 'T seq` | Desserializa listas ou objetos JSON em registros fortemente tipados `'T seq`. |

### Métodos e Propriedades de DatabaseConnection
- **`db.rawQuery(sqlText, [params])`**: Executa SQL parametrizado estritamente isolado nesta conexão e retorna `ConnectorQueryResult` para o grid visual.
- **`db.Execute(sqlText, [params])`**: Executa uma query e retorna `ConnectorQueryResult` para o grid.
- **`db.Query<'T>(sqlText, [params])`**: Executa uma query retornando sequência tipada `'T seq`.
- **`db.Queryable<'T>(sqlText, [params])`**: Retorna um `IQueryable<'T>` para composição em LINQ.
- **`db.Tables`**: Retorna `string list` com todos os nomes de tabelas e views descobertas.
- **`db.Schema`**: Retorna `ConnectorTable list` com nomes e tipos de colunas do banco.

---

## 9. Grid Interativo de Resultados

Qualquer expressão que retornar dados tabulares ou coleções é renderizada em um **grid visual interativo**:

- **Ordenação**: Clique em qualquer coluna para ordenar de forma crescente ou decrescente.
- **Tipagem Visual**: Números alinhados à direita, textos à esquerda e nulos destacados visualmente.
- **Aba de Esquema (Schema View)**: Alterne para a aba **Schema** no topo do grid para inspecionar os tipos .NET e definições de banco de cada coluna.
- **Exportação para CSV**: Clique no botão **Export CSV** para salvar o resultado em arquivo local.

---

## 10. Recursos de Linguagem, Syntax Highlighting e Go to Definition (F12)

O F# Query Studio executa inteligência de código e realce de sintaxe completo para expressões de consulta F#:

- **Coloração Especial de Palavras Reservadas (Syntax Highlighting)**:
  - Palavras-chave de consultas LINQ como `query`, `for`, `in`, `do`, `select`, `where`, `join`, `on`, `equals`, `into`, `groupBy`, `sortBy`, `distinct`, `count`, `sumBy`, `take`, `skip` são destacadas com as cores de palavras-chave de controle do seu tema.
  - Provido nativamente tanto por injeção de gramática TextMate (`fsharp.query.injection`) quanto pelo provedor semântico (`FSharpQuerySemanticTokensProvider`).
- **Go to Definition (`F12`)**: Pressione `F12` em qualquer símbolo (`Connections.sql`, `Connections.csv`, campos tipados como `p.UnitPrice`, ou funções auxiliares). O editor salta diretamente para a linha de definição.
- **Hover / Tooltips**: Passe o cursor sobre variáveis e métodos para ver assinaturas e documentação XML detalhada de tipos.
- **Diagnósticos em Tempo Real**: Erros de sintaxe ou tipo são sublinhados antes da execução da célula.
- **Renomeação (`F2`)**: Renomeie variáveis e símbolos de forma consistente em todo o notebook.

---

## 11. Ciclo de Vida do Notebook e Sessão FSI

- **Estado Compartilhado**: Todas as células em um arquivo `.fsqnb` compartilham a mesma sessão interativa FSI.
- **Cancelamento**: Interrompa consultas longas a qualquer momento pelo botão de parada (⏹).
- **Reinício da Sessão**: Clique no ícone **Restart Notebook Session** (`$(refresh)`) no cabeçalho do notebook ou utilize `Ctrl+Shift+P > Restart Notebook Session` para reiniciar a sessão FSI de forma limpa.

---

## 12. Diagnóstico e Solução de Problemas

- **Conectando no PostgreSQL?**
  Você pode informar tanto o formato chave-valor (`Host=localhost;Database=mydb;Username=postgres`) quanto o formato URI (`postgresql://postgres@localhost:5432/mydb`). Caso a senha seja informada na connection string ou na caixa de diálogo de senha, a extensão extrai a senha automaticamente, armazena no cofre criptografado do sistema (`SecretStorage` do VS Code) e a substitui por `{password}`, garantindo total segurança e funcionamento transparente.
- **O worker não inicializa?**
  Execute `F# Query Studio: Check Worker` no Command Palette (`Ctrl+Shift+P`). Ele verifica se o .NET 10 SDK está presente e emite mensagens claras de correção.
- **Novas tabelas não aparecem no autocomplete?**
  Clique no ícone de atualização (`$(refresh)`) na barra lateral **Connections** para recarregar o esquema e regenerar os tipos de entidades.
- **Dúvidas ou Relato de Problemas?**
  Abra uma issue no repositório oficial: [https://github.com/Matheusleal/fs-query-studio/issues](https://github.com/Matheusleal/fs-query-studio/issues).

---

## 13. Benchmarks de Performance e Confiabilidade de UI

O F# Query Studio é submetido a rigorosos benchmarks automatizados de performance e verificação completa de elementos de interface:

### Benchmarks de Execução (CPU, Memória e Latência)
- **Latência Sub-milissegundo**: Consultas subsequentes com pool reutilizado executam entre **0.05 ms e 0.20 ms** com 0% de uso contínuo de CPU e zero coletas Gen 0/1/2 no Garbage Collector.
- **Escala Linear com Streaming**: Processa de 100 até 50.000+ linhas via streaming iterativo de cursores (`Results.fs`), com consumo de memória estável (~1.5 KB por aquisição de cursor) sem materialização antecipada no heap.
- **LINQ e Records F# Dinâmicos**: Mapeamento de records fortemente tipados (`Connections.query<'T>`) e expressões `query { for ... in ... do where ... select ... }` processam 1.000 linhas em **~2.0 ms** utilizando cache de construtores pré-compilados.
- **Processamento de Arquivos CSV**: Taxa de transferência contínua de **~500.000 registros/segundo**.

Para detalhes da metodologia, métricas brutas e especificações de hardware, consulte o **[Relatório Completo de Benchmarks](BENCHMARK_REPORT.md)** ([ver no GitHub](https://github.com/Matheusleal/fs-query-studio/blob/main/docs/BENCHMARK_REPORT.md)).

### Suíte de Confiabilidade de Elementos de UI
A interface visual da extensão é coberta por testes automatizados de componentes:
- **Grid de Resultados e Toolbar**: Botões segmentados, contadores de linhas (incluindo truncamento como `3+ rows`), folhas de estilo dinâmicas e ação de exportação para CSV.
- **Cabeçalhos Fixos e Virtualização**: Cabeçalho fixo `.fsqhead` com tipos de colunas, alinhamento numérico `.fsq-num`, linhas zebradas `.fsq-odd` e cálculo dinâmico de altura do sizer virtual (`N * 24px`).
- **Formatadores de Células**: Destaque visual para nulos `.fsq-null` ("NULL"), alinhamento numérico à direita, booleanos e strings.
- **Inspetor de Esquema**: Nomes de coluna, tipos de dados de banco e flags de nulabilidade.
- **Paginação e Banners**: Continuações ("Load More"), desativação de botão durante requisição, avisos de truncamento e notificações do host.
- **Árvore do Explorador de Conexões**: Ícones dos TreeItems (`database`, `symbol-namespace`, `table`, `symbol-field`, `warning`), descrições contextuais e valores de contexto.

