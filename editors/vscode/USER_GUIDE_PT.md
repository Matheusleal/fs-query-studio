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
   - [Junção de Múltiplas Tabelas (Joins)](#junção-de-múltiplas-tabelas-joins)
   - [Agrupamentos e Agregações](#agrupamentos-e-agregações)
   - [Consultas em Arquivos CSV com LINQ](#consultas-em-arquivos-csv-com-linq)
   - [Combinando Banco de Dados e CSV em Memória](#combinando-banco-de-dados-e-csv-em-memória)
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
   - `$(database)` **Criar Base de Dados de Teste**: Gera automaticamente bases SQLite e CSV fictícias com notebook demonstrativo em 1 clique.
   - `$(add)` **Adicionar Conexão**: Abre o assistente interativo para cadastrar bancos, arquivos ou APIs.
   - `$(refresh)` **Atualizar**: Recarrega o esquema e regenera os tipos de entidades.
3. **Command Palette (`Ctrl+Shift+P` / `Cmd+Shift+P`)**:
   - `F# Query Studio: Create Sample Data (SQLite & CSV)`
   - `F# Query Studio: Open Documentation`
   - `F# Query Studio: New F# Query Notebook`
   - `F# Query Studio: Restart Notebook Session`
   - `F# Query Studio: Check Worker`

---

## 3. Início Rápido: Criando Dados de Exemplo (SQLite e CSV)

Quer começar a explorar consultas LINQ imediatamente sem precisar configurar um banco externo?

Clique no botão **`Create Sample Data`** (`$(database)`) no cabeçalho do painel **Connections**, ou selecione `F# Query Studio: Create Sample Data (SQLite & CSV)` no Command Palette.

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

### Filtragem Básica e Projeção

```fsharp
// 1. Consulta LINQ direta no banco via Connections.sql
query {
    for p in Connections.sql.SampleStore.Products do
    where (p.UnitPrice > 50.0M)
    sortByDescending p.UnitPrice
    select (p.Id, p.Name, p.UnitPrice, p.StockQuantity)
}
```

### Junção de Múltiplas Tabelas (Joins)

Realize junções relacionais com segurança de tipo em tempo de compilação usando a cláusula `join`:

```fsharp
// Junção entre Clientes e Pedidos
query {
    for c in db.Customers do
    join o in db.Orders on (c.Id = o.CustomerId)
    where (o.Status = "Completed")
    select (c.FullName, c.Country, o.Id, o.OrderDate, o.TotalAmount)
}
```

```fsharp
// Junção tripla: Itens de Pedido + Produtos + Categorias
query {
    for item in db.OrderItems do
    join prod in db.Products on (item.ProductId = prod.Id)
    join cat in db.Categories on (prod.CategoryId = cat.Id)
    select (item.OrderId, prod.Name, cat.Name, item.Quantity, item.UnitPrice)
}
```

### Agrupamentos e Agregações

```fsharp
// Agrupamento de clientes por país com contagem
query {
    for c in db.Customers do
    groupValBy c.Id into g
    select (g.Key, count g)
}
```

### Consultas em Arquivos CSV com LINQ

Arquivos planos como CSV, Excel e JSON recebem esquemas inferidos automaticamente:

```fsharp
// Consulta LINQ direta no feed CSV de vendas via Connections.csv
query {
    for row in Connections.csv.SalesFeed do
    where (row.StoreCity = "New York" && row.Quantity >= 2)
    sortByDescending row.Total
    select (row.TransactionId, row.Product, row.Quantity, row.Total)
}
```

### Combinando Banco de Dados e CSV em Memória

Como as células compartilham o mesmo estado no F# Interactive, você pode cruzar dados do banco relacional com feeds CSV usando funções funcionais do F#:

```fsharp
let dbProducts = 
    query { for p in Connections.sql.SampleStore.Products do select (p.Name, p.UnitPrice) }
    |> Seq.toList

let csvTransactions =
    query { for r in Connections.csv.SalesFeed do select (r.Product, r.Total) }
    |> Seq.toList

printfn "Total de produtos no banco: %d | Total de vendas no CSV: %d" dbProducts.Length csvTransactions.Length
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
| `Connections.excel.<Conn>` | `ExcelConnector.Data` | Acesso às planilhas fortemente tipadas do Excel e `.Raw()`. |
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

Para detalhes da metodologia, métricas brutas e especificações de hardware, consulte o **[Relatório Completo de Benchmarks](../../docs/BENCHMARK_REPORT.md)**.

### Suíte de Confiabilidade de Elementos de UI
A interface visual da extensão é coberta por testes automatizados de componentes:
- **Grid de Resultados e Toolbar**: Botões segmentados, contadores de linhas (incluindo truncamento como `3+ rows`), folhas de estilo dinâmicas e ação de exportação para CSV.
- **Cabeçalhos Fixos e Virtualização**: Cabeçalho fixo `.fsqhead` com tipos de colunas, alinhamento numérico `.fsq-num`, linhas zebradas `.fsq-odd` e cálculo dinâmico de altura do sizer virtual (`N * 24px`).
- **Formatadores de Células**: Destaque visual para nulos `.fsq-null` ("NULL"), alinhamento numérico à direita, booleanos e strings.
- **Inspetor de Esquema**: Nomes de coluna, tipos de dados de banco e flags de nulabilidade.
- **Paginação e Banners**: Continuações ("Load More"), desativação de botão durante requisição, avisos de truncamento e notificações do host.
- **Árvore do Explorador de Conexões**: Ícones dos TreeItems (`database`, `symbol-namespace`, `table`, `symbol-field`, `warning`), descrições contextuais e valores de contexto.

