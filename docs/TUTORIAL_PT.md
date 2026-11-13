# Tutorial — seu primeiro F# Query Notebook

[🇺🇸 English](TUTORIAL.md) | [🇧🇷 Português](TUTORIAL_PT.md)

Este tutorial guia você desde uma instalação limpa do VS Code até a execução de F#, configuração de conexões com bancos de dados e arquivos, e exportação de resultados. Leva cerca de 15 minutos. Todos os passos são práticos e rápidos.

Ao final deste tutorial, você terá:

- Executado código F# em células de notebook e compreendido como a sessão interativa persiste;
- Registrado uma conexão SQLite (ou importado via pasta `data_sources`);
- Consultado o banco com expressões LINQ tipadas e pushdown total para SQL;
- Inspecionado o SQL gerado com `Connections.toRawSql`;
- Renderizado o resultado em uma grade interativa e exportado para CSV;
- Explorado conexões com CSV, Excel (.xlsx) com cache SQLite persistente e APIs REST.

---

## 0. Pré-requisitos

**O .NET 10 SDK deve estar no seu `PATH`.** Verifique no terminal:

```bash
dotnet --version
```

É necessário que retorne `10.` ou superior. Se estiver ausente ou desatualizado, baixe e instale o .NET 10 SDK em <https://dotnet.microsoft.com/download> e reinicie o VS Code.

**Instale a extensão.** Se for compilar localmente:

```bash
cd editors/vscode
npm install
npm run package
code --install-extension fs-query-studio.vsix --force
```

> **Dica:** Se algo se comportar de forma inesperada, execute o comando **F# Query Studio: Check Worker** na Command Palette (`Ctrl+Shift+P` / `Cmd+Shift+P`). Ele verifica se o processo worker iniciou e relata diagnósticos.

---

## 1. Crie um notebook e execute uma célula

Abra a Command Palette (`Ctrl+Shift+P`) e selecione **F# Query Studio: New F# Query Notebook**. Um notebook sem título se abrirá com uma célula de código F# vazia.

Você também pode:
- Usar *Arquivo > Novo Arquivo…* e escolher **F# Query Notebook**, ou
- Clicar com o botão direito em uma pasta no Explorer e escolher **New F# Query Notebook Here**, ou
- Criar qualquer arquivo com a extensão `.fsqnb` e abri-lo.

Digite o código abaixo na célula e pressione `Ctrl+Enter` (ou o botão ▶):

```fsharp
printfn "Olá do FSI!"
1 + 1
```

O resultado renderizado exibirá:
- O texto `Olá do FSI!` (saída padrão stdout/stderr);
- O valor `2` — a última expressão da célula, renderizada pela visualização de resultados.

---

## 2. Persistência de Sessão entre Células

Adicione uma segunda célula (pressione `Esc` e depois `B`, ou o botão `+ Code`) e execute:

```fsharp
let saudacao = "notebook interativo"
```

Adicione uma terceira célula e execute:

```fsharp
printfn "Bem-vindo ao %s!" saudacao
```

Ela imprimirá `Bem-vindo ao notebook interativo!`. A vinculação `let` da segunda célula continua em escopo — o notebook inteiro compartilha **uma única sessão persistente do F# Interactive (FSI)**.

Quando você desejar reiniciar a sessão para limpar estados em memória, execute **F# Query Studio: Restart Notebook Session** na Command Palette.

---

## 3. Renderização de Grade de Resultados

Qualquer célula cuja última expressão seja uma sequência de records, tuplas ou dicionários é renderizada automaticamente como uma tabela interativa virtualizada:

```fsharp
[ for i in 1..5 -> {| Numero = i; Quadrado = i * i; Par = (i % 2 = 0) |} ]
```

A saída é um grid com cabeçalhos de coluna, valores numéricos alinhados à direita, contador de linhas e aba **Esquema (Schema)** ao lado de **Dados (Data)**.

---

## 4. Registrando uma Conexão SQLite

Abra o painel **F# Query Studio** na barra de atividades lateral (ícone do F# Query Studio). O painel exibirá as conexões cadastradas.

Você pode:
1. Clicar em **Create Sample Data** (`$(database)`) para gerar instantaneamente bancos de exemplo (`sample_store.db`, `inventory.db`) e CSVs (`sales_feed.csv`, `customers_feed.csv`), OU
2. Clicar em **Add Connection** (`+`) e preencher:
   - **Tipo**: `Sqlite`
   - **Nome da Conexão**: `Chinook`
   - **Caminho do Arquivo**: o caminho absoluto ou relativo para seu arquivo `.db` / `.sqlite`.

A conexão aparecerá na árvore. Ao expandi-la, ela carrega tabelas sob demanda, e cada tabela expande para suas respectivas colunas.

---

## 5. Consultando com `fsql` (F# Query Language) e Pushdown Completo para SQL

No notebook, você pode escrever consultas puramente funcionais com segurança de tipo usando a computation expression unificada **`fsql`**. O backend gera entidades em segundo plano sob `Connections.sql.<NomeDaConexao>`:

```fsharp
fsql {
    for t in Connections.sql.Chinook.Track do
    where (t.Milliseconds > 300000)
    sortByDescending t.Milliseconds
    take 20
    select {| Titulo = t.Name; DuracaoMs = t.Milliseconds; PrecoUnitario = t.UnitPrice |}
}
```

### Pushdown Total para SQL
Observe o que acontece em segundo plano:
- Apenas os campos selecionados (`Name`, `Milliseconds`, `UnitPrice`) são solicitados ao banco (`SELECT _t0.Name AS Titulo, ...`).
- O filtro (`WHERE`), a ordenação (`ORDER BY`) e o limite (`LIMIT 20` ou `OFFSET/FETCH`) são executados **diretamente na engine do banco de dados**, evitando puxar milhares de linhas para a memória RAM.

### Recursos Avançados de DQL: Subqueries Correlacionadas e EXISTS
Com `fsql`, operações avançadas de SQL são expressas de forma nativa e tipada:

```fsharp
// Subquery correlacionada EXISTS
fsql {
    for a in Connections.sql.Chinook.Album do
    whereExists (fsql { for t in Connections.sql.Chinook.Track do where (t.AlbumId = a.AlbumId && t.Milliseconds > 600000) })
    select {| AlbumId = a.AlbumId; Titulo = a.Title |}
}
```

### Inspecionando o SQL Formatado com `toRawQuery`
Quer verificar a query SQL exata que o compilador gerou com identação e documentação de parâmetros antes de enviá-la ao banco?

```fsharp
let q =
    fsql {
        for t in Connections.sql.Chinook.Track do
        where (t.Milliseconds > 300000)
        select t
    }

printfn "%s" (toRawQuery q)
```

Para agentes de inteligência artificial e assistentes de código, consulte o guia detalhado em [`docs/FSQL_AI_AGENT_GUIDE.md`](FSQL_AI_AGENT_GUIDE.md).

### Consultas SQL Tradicionais Parametrizadas
Você também pode executar comandos SQL parametrizados diretamente:

```fsharp
Connections.sql.Chinook.rawQuery
    "select Name, Milliseconds from Track where GenreId = @genero order by Milliseconds desc limit 20"
    [ "genero", box 1 ]
```

---

## 6. Exportando Resultados para CSV

Ao executar qualquer consulta cujo resultado seja uma grade na última expressão:

```fsharp
Connections.sql.Chinook.rawQuery "select * from Track limit 200" []
```

O botão **Export CSV** fica disponível no canto superior da grade de resultados. Clique nele, escolha o local para salvar e o arquivo CSV completo será gerado instantaneamente.

---

## 7. O Que uma Célula Renderiza

Uma célula renderiza o valor de sua **última expressão** automaticamente:

| Última Expressão | Renderização |
|---|---|
| Consulta via `Connections.*` | Grade de dados com botão **Export CSV** ativo |
| Sequência de records (`{| ... |}` ou tipos record) | Grade de dados com nomes das propriedades nas colunas |
| Sequência de tuplas (`(a, b)`) | Grade de dados com cabeçalhos `Item1`, `Item2`, … |
| Sequência de dicionários / Maps | Grade de dados com a união das chaves nas colunas |
| Valor escalar (`int`, `string`, `DateTime`) | Texto formatado |
| Declaração `let` ou `printfn` | Saída textual padrão do FSI |

---

## 8. Conexões de Arquivos CSV e Excel (.xlsx) com Cache em Disco

### Auto-Importação via Pasta `data_sources/`
A maneira mais prática de registrar fontes de arquivos é utilizar a pasta `data_sources/`:
1. Coloque seus arquivos `.csv`, `.xlsx`, `.db` ou `.sqlite` na pasta `data_sources/` na raiz do workspace.
2. Execute o comando **F# Query Studio: Auto-Import Data Sources Directory** na Command Palette.
3. Os arquivos são detectados, ganham tipos fortemente tipados e são pré-carregados de forma assíncrona em cache persistente SQLite em `.fsqs/cache/engine.db`.

### Consultando Arquivos com `fsql`
```fsharp
fsql {
    for tx in Connections.csv.SalesFeed do
    where (tx.Quantity > 2)
    sortByDescending tx.TotalAmount
    select (tx.OrderId, tx.Product, tx.TotalAmount)
}
```

```fsharp
// Planilhas Excel expõem cada aba individualmente com tipagem forte
fsql {
    for meta in Connections.excel.RegionalTargets.RegionalTargets do
    where (meta.GrowthRate >= 10.0M)
    select meta
}
```

### Joins Otimizados Multi-Propriedades entre Arquivos
Como os arquivos são cacheados no SQLite com índices B-tree compostos, você pode cruzar múltiplos arquivos em múltiplas chaves sem risco de congelamento por produto cartesiano em memória:

```fsharp
fsql {
    for s in Connections.csv.SalesFeed do
    join c in Connections.csv.CustomersFeed on (s.Customer = c.CustomerName)
    select {| PedidoId = s.OrderId; Cliente = s.Customer; CategoriaFidelidade = c.LoyaltyTier |}
}
```

### Mapeamento Customizado de Colunas & Fórmulas de Transformação
Ao importar arquivos CSV ou planilhas Excel com moedas (`R$ 1.234,56`), separadores decimais regionais, percentuais ou números com muitas casas decimais, configure a conexão pelo **painel lateral de Conexões** (*Add Connection* / *Edit Connection*) ou no arquivo `.fsqs/connections.json`:

- **`columnTypes`**: Força o tipo F# de destino, como `decimal`, `double`, `int`, `int64`, `bool`, `DateTime` ou `string`. Exemplo: `"ValorTotal=decimal, Aliquota=double"`.
- **`columnTransforms`**: Aplica fórmulas de tratamento antes da sincronização no SQLite:
  - `replace('.', '').replace(',', '.')`: Converte números/moedas no padrão brasileiro para ponto decimal.
  - `replace('R$', '').trim`: Remove símbolos de moeda e espaços em branco.
  - `multiply(100)` ou `divide(100)`: Multiplica ou divide valores por fatores numéricos.

```json
{
  "name": "RelatorioVendas",
  "kind": "Excel",
  "configuration": {
    "filePath": "data_sources/vendas.xlsx",
    "sheetName": "Plan1",
    "columnTypes": "Receita=decimal, Unidades=int",
    "columnTransforms": "Receita=replace('R$', '').replace('.', '').replace(',', '.').trim"
  }
}
```

---

## 9. Conexões SQL Server, PostgreSQL e MySQL

Ao cadastrar conexões do tipo `SqlServer` ou `PostgreSql`, você informa:
1. **Connection String**: com o marcador literal `{password}`, por exemplo:
   `Server=localhost;Database=Vendas;User Id=app;Password={password};`
2. **Senha**: digitada de forma mascarada. Ela é armazenada com segurança no **chaveiro do sistema operacional** através da API `SecretStorage` do VS Code.

As senhas **nunca** são gravadas em texto puro nos arquivos do projeto.

---

## 10. Conexões REST JSON (HTTPS)

Ao adicionar uma conexão `REST JSON`, informe a URL base (por exemplo, `https://api.github.com/orgs/fsprojects/repos`).
O F# Query Studio mapeia as respostas JSON em records fortemente tipados que você pode consultar com `fsql`:

```fsharp
fsql {
    for repo in Connections.rest.GitHubFSharp do
    sortByDescending repo.stargazers_count
    select {| Repositorio = repo.full_name; Estrelas = repo.stargazers_count; Url = repo.html_url |}
}
```

---

## 11. Suporte e Inteligência no Editor

Conforme você digita nas células, você recebe assistência completa:
- **Autocomplete**: valores em escopo, propriedades de entidades e nomes de conexões em `Connections.*`;
- **Hover & Tooltips**: tipos de dados e documentação XML ao passar o cursor;
- **Signature Help**: dicas de parâmetros durante chamadas de métodos;
- **Diagnósticos em Tempo Real**: sublinhado vermelho de erros de tipo ou sintaxe;
- **Go to Definition (`F12`)**: navegação direta para definições de tipos e records gerados.

---

## 12. Solução de Problemas Frequentes

| Sintoma | Solução Recomendada |
|---|---|
| Célula não executa ou erro de "worker" | Execute **F# Query Studio: Check Worker**. Geralmente o .NET 10 SDK não foi encontrado no PATH. Instale-o e reinicie o VS Code. |
| Erro `Connections has not been configured` | A conexão informada não foi registrada ou o workspace ainda não possui conexões salvas. Cadastre-a pela barra lateral. |
| O botão **Export CSV** não aparece | A última expressão da célula não foi uma coleção de dados (foi um comando imperativo, texto ou escalar). Certifique-se de que a última linha retorne a coleção. |
| Alteração em arquivo CSV não refletiu | O motor valida data e tamanho. Se necessário, execute o comando **F# Query Studio: Sync Data Sources Cache** para forçar a sincronização. |
| Sessão acumulou variáveis indesejadas | Execute **F# Query Studio: Restart Notebook Session** para reiniciar a sessão FSI do zero. |

---

## Próximos Passos

- [`../README_PT.md`](../README_PT.md) — Visão geral da arquitetura do repositório.
- [`USER_GUIDE_PT.md`](USER_GUIDE_PT.md) — Guia aprofundado do usuário.
- [`BENCHMARK_REPORT_PT.md`](BENCHMARK_REPORT_PT.md) — Relatório detalhado de benchmarks e métricas de execução.
