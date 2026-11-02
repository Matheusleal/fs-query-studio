# F# Query Studio

[🇺🇸 English](README.md) | [🇧🇷 Português](README_PT.md) | [📖 User Guide (EN)](USER_GUIDE.md) | [📖 Guia do Usuário (PT)](USER_GUIDE_PT.md) | [📊 Relatório de Benchmark](BENCHMARK_REPORT_PT.md)

Escreva, analise e execute scripts de consulta F# em bancos de dados SQL, arquivos locais e
APIs REST — diretamente como notebooks do VS Code.

Abra qualquer arquivo `.fsqnb` como um **Notebook F# Query Studio**. Cada célula de código executa F# em
uma sessão FSI persistente em um processo worker .NET integrado. Uma API controlada conecta-se aos
bancos de dados e arquivos registrados na barra lateral, renderizando resultados tabulares em uma
grade ordenável e filtrável com aba de esquema e exportação para CSV.

## Requisitos

- **.NET 10 SDK** no seu `PATH` (`dotnet --version` ≥ 10). A extensão executa um
  processo worker F# integrado dependente de framework.
- **VS Code 1.90** ou superior.
- Windows, macOS ou Linux. Um processo worker por janela do VS Code.

## Início rápido

1. Execute **F# Query Studio: New F# Query Notebook** na Command Palette (também em
   *File > New File…*, e no menu de contexto do Explorer como **New F# Query Notebook Here**).
   Ou simplesmente crie e abra qualquer arquivo com extensão `.fsqnb`.
2. Digite código F# em uma célula e execute-a. As variáveis e funções definidas com `let` persistem
   para células subsequentes no mesmo notebook — é uma sessão FSI por arquivo.
   O comando **F# Query Studio: Restart Notebook Session** reinicia a sessão.
3. Abra a visão **F# Query Studio** na Activity Bar e clique em **Add Connection** (ou adicione arquivos na pasta `data_sources/` e execute **Auto-Import Data Sources Directory**).
   Credenciais são armazenadas no chaveiro seguro do sistema operacional via `SecretStorage` do VS Code — nunca gravadas no `workspace.json` ou no notebook.
4. Referencie uma conexão em uma célula e execute; o resultado tabular é renderizado como
   uma grade interativa com aba de tipos/esquema e botão **Export CSV**.

Um tutorial prático completo está disponível em
[`docs/TUTORIAL_PT.md`](https://github.com/Matheusleal/fs-query-studio/blob/main/docs/TUTORIAL_PT.md) ([English version](https://github.com/Matheusleal/fs-query-studio/blob/main/docs/TUTORIAL.md)).

## Escrevendo células de código

Toda célula de código executa em uma sessão onde este namespace já está aberto — sem necessidade de `#r` ou `open`:

```fsharp
open FSharpQueryStudio.Connectors   // API Connections.*
```

### `Connections` — conexões fortemente tipadas por categoria

| Categoria | Tipos | Uso |
|---|---|---|
| `Connections.sql.<Conn>` | SQLite, SQL Server, PostgreSQL, MySQL | Tabelas tipadas (`.Users`) e queries parametrizadas `.rawQuery "..." [ ... ]` |
| `Connections.csv.<Conn>` | CSV | Linhas tipadas via LINQ `query { for r in Connections.csv.<Conn> do ... }` ou `.Rows` |
| `Connections.excel.<Conn>` | Excel (XLSX) | Planilhas tipadas (`.<NomePlanilha>`) e `.Raw()` |
| `Connections.rest.<Conn>` | REST JSON | Itens tipados (`.Items`) e `.SendAsync()` |

### Pushdown Universal de Consultas para SQL (Estilo EF)

O F# Query Studio traduz expressões de consulta LINQ em F# diretamente em SQL nativo otimizado no SQLite, PostgreSQL e SQL Server. Apenas as colunas projetadas e as linhas filtradas trafegam do banco para a memória do cliente:

```fsharp
query {
    for o in Connections.sql.Sales.Orders do
    where (o.TotalAmount >= 100.0f && o.Status = "Completed")
    sortByDescending o.CreatedAt
    take 10
    select {| OrderId = o.Id; Total = o.TotalAmount; Date = o.CreatedAt |}
}
```

Essa consulta é traduzida e executada no banco como:
```sql
SELECT _t0.Id AS OrderId, _t0.TotalAmount AS Total, _t0.CreatedAt AS Date
FROM Orders _t0
WHERE (_t0.TotalAmount >= 100.0 AND _t0.Status = 'Completed')
ORDER BY _t0.CreatedAt DESC
LIMIT 10
```

### Inspeção de SQL Gerado (`toRawSql`)

Para inspecionar o SQL e os parâmetros traduzidos antes do envio ao banco de dados:

```fsharp
query {
    for o in Connections.sql.Sales.Orders do
    where (o.TotalAmount >= 100.0f)
    select o
}
|> Connections.toRawSql
```

### Engine de Staging de Alta Performance para CSV & Excel (`.fsqs/cache/engine.db`)

Arquivos CSV e Excel (`.xlsx`) são automaticamente armazenados em cache num banco SQLite embutido em `<workspace>/.fsqs/cache/engine.db`:
- **Ingestão em Lote com Transação Única**: Transfere centenas de milhares de linhas a mais de 100.000 linhas/segundo.
- **Validação Inteligente por MTime e Tamanho**: Se o arquivo de origem não sofreu alteração, as consultas executam imediatamente no cache em 0 ms.
- **Índices Compostos Automáticos e Joins Multi-Propriedades**: Joins entre arquivos diferentes com chaves compostas rodam direto no SQLite em subsegundos (~18 ms para 5.000×40 registros) sem explosões de memória RAM:

```fsharp
query {
    for t in Connections.csv.Transactions do
    join tgt in Connections.excel.Targets.Sheet1 on ((t.StoreId, t.Region) = (tgt.StoreId, tgt.Region))
    where (t.Amount > 1000.0M)
    select {| Tx = t.TransactionId; Loja = t.StoreId; Meta = tgt.TargetAmount |}
}
```

### Descoberta Automática via Pasta `data_sources/`

Basta colocar seus bancos SQLite (`.db`, `.sqlite`), arquivos CSV (`.csv`) ou planilhas Excel (`.xlsx`) em uma pasta chamada `data_sources/` na raiz do projeto. Clique em **Auto-Import Sources** na barra de ferramentas do painel Connections ou execute **F# Query Studio: Auto-Import Data Sources Directory** para registrá-los instantaneamente.

### Consultas SQL Brutas (Raw SQL)

O parâmetro `parameters` é uma lista de pares `(string * obj)` — sempre vinculado através da API de parâmetros segura do driver, nunca concatenado como texto:

```fsharp
Connections.sql
    "Sales"
    "select * from Orders where CreatedAt >= @from"
    [ "from", box (System.DateTime(2026, 1, 1)) ]
```

### O que uma célula renderiza

Uma célula renderiza o valor de sua **última expressão** — o mesmo valor que o F# Interactive imprimiria. Não é preciso chamar nenhuma função especial.

| Última expressão | Renderização |
|---|---|
| Consulta de conector (`Connections.sql …`, `Connections.csv …`, …) | Grade interativa com botão **Export CSV** funcional |
| Sequência de registros / resultado LINQ | Grade onde as colunas são os campos do registro |
| Sequência de tuplas | Grade com colunas `Item1`, `Item2`, … |
| Sequência de `Map`s / dicionários | Grade com união das chaves como colunas |
| Sequência de coleções (ex: `int[] list`, linhas de arrays) | Grade onde a primeira linha se torna o cabeçalho e as demais são dados |
| Sequência de escalares | Grade de coluna única paginada com botão **Load more** |
| Um único registro | Grade com uma única linha |
| Valor escalar (`int`, `string`, `DateTime`, …) | Texto simples |
| Binding `let` ou chamada `printfn` (sem valor de retorno) | Saída de texto do terminal |

Para forçar a saída como texto estruturado simples, termine a célula com `sprintf "%A" valor`.

O botão **Export CSV** está disponível para todas as grades materializadas acima (exceto sequências escalares infinitas em streaming).

## Recursos

- **Execução em Notebooks** — cada célula de código `.fsqnb` executa em uma sessão FSI persistente por notebook, com streaming de stdout/stderr em tempo real e cancelamento de execução.
- **Pushdown Universal de SQL** — tradução integral de consultas LINQ (`select`, `where`, `join`, `sortBy`, `take`, `skip`, `count`, `sum`) para SQL nativo em todos os bancos relacionais e arquivos locais, eliminando sobrecarga de memória no cliente.
- **Engine de Staging de Alta Performance** — cache persistente em disco em SQLite (`.fsqs/cache/engine.db`) com validação inteligente de timestamp e suporte ultra rápido a joins multi-chaves em arquivos CSV e Excel volumosos.
- **Inspeção Prévia de SQL** — visualize o SQL e parâmetros gerados via `Connections.toRawSql` antes de executar na base de dados.
- **Descoberta Automática via `data_sources`** — importação com 1 clique de todos os arquivos de dados contidos na pasta `data_sources/`.
- **Grade de Resultados Virtualizada** — tabela no estilo nativo do VS Code com colunas numéricas alinhadas à direita, linhas zebradas, aba de esquema, paginação progressiva e exportação CSV.
- **Inteligência F# na Célula** — autocompletion, hover com documentação, signature help, diagnósticos de erro e navegação para definição (**F12**) em tipos gerados.
- **Conexões Categorizadas com Autocomplete** — navegação intuitiva `Connections.sql.<Conn>`, `Connections.csv.<Conn>`, etc. com validação em tempo de compilação.
- **Gerenciamento de Conexões** — árvore na Activity Bar listando conexões → esquemas → tabelas → colunas, com suporte a adição, edição e exclusão guiadas.
- **Documentação Bilíngue Embutida** — ícone `$(book)` na barra do painel para abrir a documentação completa lado a lado em português ou inglês.

## Tipos de conexão suportados

| Tipo | O que fornecer | Segredo |
|---|---|---|
| SQLite | caminho absoluto ou relativo ao workspace do arquivo `.db` | — |
| CSV | caminho absoluto ou relativo ao workspace do arquivo `.csv` | — |
| Excel | caminho absoluto ou relativo ao workspace do arquivo `.xlsx` | — |
| JSON | caminho absoluto ou relativo ao workspace do arquivo `.json` | — |
| PostgreSQL | string de conexão (utilize `{password}` no lugar da senha) | senha opcional, solicitada em caixa segura e armazenada no keychain do OS |
| SQL Server | string de conexão (utilize `{password}` no lugar da senha) | senha opcional, solicitada em caixa segura e armazenada no keychain do OS |
| MySQL | string de conexão (utilize `{password}` no lugar da senha) | senha opcional, solicitada em caixa segura e armazenada no keychain do OS |
| REST JSON | URL base da API | — |

As informações públicas são gravadas em `workspace.json`. Quando uma senha é configurada, apenas o placeholder `{password}` permanece no arquivo salvo; o valor real é recuperado via `SecretStorage` no momento da execução e nunca é exposto no workspace ou nos notebooks.

## Comandos da extensão

| Comando | Descrição |
|---|---|
| **F# Query Studio: Open Documentation** | Abre o Guia do Usuário interativo na visualização markdown (ícone `$(book)`). |
| **F# Query Studio: New F# Query Notebook** | Cria e abre um novo notebook `.fsqnb` com uma célula em branco. |
| **F# Query Studio: New F# Query Notebook Here** | Menu de contexto de pastas no Explorer — cria um `.fsqnb` na pasta selecionada. |
| **F# Query Studio: Auto-Import Data Sources Directory** | Detecta e adiciona automaticamente arquivos compatíveis da pasta `data_sources/`. |
| **F# Query Studio: Sync Data Sources Cache** | Dispara a sincronização e cache assíncrono em disco (`.fsqs/cache/engine.db`) para arquivos CSV e Excel. |
| **F# Query Studio: Create Sample Data (SQLite & CSV)** | Gera arquivos de dados de exemplo e os notebooks de demonstração (`sample_demo.fsqnb` e `sample_demo_pt.fsqnb`). |
| **F# Query Studio: Restart Notebook Session** | Reinicia a sessão FSI do notebook atual, limpando o estado em memória. |
| **F# Query Studio: Check Worker** | Diagnostica a integridade do processo worker F# e a compatibilidade do ambiente .NET. |
| **F# Query Studio: Connections: Add / Edit / Remove / Refresh** | Gerencia as conexões de dados cadastradas na árvore. |
| **F# Query Studio: Initialize Empty Project** | Inicializa o arquivo de projeto `.fsqs/workspace.json` no workspace aberto. |
| **F# Query Studio: Upgrade Project Schema** | Migra configurações de workspaces legados para o esquema v2 atual. |
| **F# Query Studio: Show Output Logs** | Abre o canal de saída do VS Code exibindo os logs detalhados do worker. |

Para tutoriais passo a passo e exemplos de consultas, consulte o [`USER_GUIDE_PT.md`](USER_GUIDE_PT.md) ([English User Guide](USER_GUIDE.md)).
