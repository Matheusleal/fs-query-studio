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

Abra a Command Palette (`Ctrl+Shift+P` / `Cmd+Shift+P`) e selecione **F# Query Studio: New F# Query Notebook**. Um notebook sem título se abrirá com uma célula de código vazia.

Você também pode:
- Usar *Arquivo > Novo Arquivo…* e escolher **F# Query Notebook**, ou
- Clicar com o botão direito em uma pasta no Explorer e escolher **New F# Query Notebook Here** (esta opção cria um arquivo `.fsqnb` real imediatamente), ou
- Criar qualquer arquivo cujo nome termine em `.fsqnb` e abri-lo.

Digite o código abaixo na célula e pressione `Ctrl+Enter` (ou o botão ▶):

```fsharp
printfn "Olá do FSI!"
1 + 1
```

O resultado renderizado exibirá:
- O texto `Olá do FSI!` — tudo o que for escrito em stdout/stderr,
- O valor `2` — a última expressão da célula, renderizada pela visualização de resultados.

Enquanto uma célula executa, sua saída é transmitida ao vivo e o botão ▶ se transforma em um botão de parada (stop). Apenas uma célula é executada por vez, considerando todos os arquivos `.fsqnb` abertos na janela.

---

## 2. A sessão persiste entre células

Adicione uma segunda célula (`Esc` e depois `B`, ou o botão `+ Code`) e execute:

```fsharp
let saudacao = "notebook interativo"
```

Adicione uma terceira célula e execute:

```fsharp
printfn "Bem-vindo ao %s!" saudacao
```

Ela imprimirá `Bem-vindo ao notebook interativo!`. A vinculação `let` da segunda célula continua em escopo — o notebook inteiro compartilha **uma única sessão persistente do FSI**, de cima a baixo.

Isso também significa que o estado se acumula. Quando você quiser começar do zero — um tipo redefinido, um novo `open`, ou uma variável mutável que você estava alterando —, execute **F# Query Studio: Restart Notebook Session** na Command Palette. A próxima célula que você executar começará do zero absoluto.

---

## 3. Renderize uma grade

Qualquer célula cuja última expressão seja uma sequência de records é renderizada como uma tabela. Execute:

```fsharp
[ for i in 1..5 -> {| Numero = i; Quadrado = i * i; Par = (i % 2 = 0) |} ]
```

A saída é uma grade: uma coluna por campo do record, colunas numéricas alinhadas à direita, uma aba **Esquema (Schema)** ao lado de **Dados (Data)** e uma contagem de linhas. Para um resultado pequeno assim não há paginação; resultados grandes carregam uma página por vez conforme você rola a tela.

Observe o seletor de apresentação da saída (o menu `⋯` / "Alterar apresentação" no canto da saída). A grade é o padrão; o eco `val it = …` do próprio FSI fica guardado na visualização **dados brutos (raw data)**. Você raramente precisará dele.

---

## 4. Registre uma conexão SQLite

Você tem duas formas fáceis de configurar um banco de dados:

### Opção A: Gerador de Demo em 1 Clique (Mais Rápido)
Clique no botão **Create Sample Data & Demo Notebooks** (`$(database)`) na barra de ferramentas da visualização de Conexões (ou execute-o a partir da Command Palette). Selecione seu banco de dados (ex: `Original`) e idioma de preferência (`English` ou `Português`). Ele gera `sample_store.db`, CSVs, planilhas Excel e notebooks de demonstração prontos para rodar com todas as conexões pré-cadastradas!

### Opção B: Registrar um Banco SQLite Existente
Você também pode apontar para qualquer arquivo `.sqlite` ou `.db` que já possua — o clássico banco SQLite **Chinook** é uma excelente escolha. Anote o seu **caminho absoluto**.

Agora abra a barra lateral do **F# Query Studio**: clique em seu ícone na Barra de Atividades (a faixa lateral esquerda). Clique em **Add Connection** (o `+` na barra de título da visualização). Responda às instruções:

1. **Tipo (Kind)** — escolha `Sqlite`.
2. **Nome da Conexão (Connection name)** — `Chinook` (este é o identificador usado a partir das células).
3. **Caminho absoluto do arquivo (Absolute file path)** — o caminho completo para seu arquivo `.db`.

A conexão aparecerá na árvore. Ao expandi-la, ela carrega tabelas sob demanda, e cada tabela expande para suas respectivas colunas. Nenhuma consulta é executada até que você expanda um nó.

---

## 5. Consultando com `fsql` (F# Query Language) e Pushdown Completo para SQL

No notebook, você pode escrever consultas puramente funcionais com segurança de tipo usando a computation expression unificada **`fsql`**. O backend gera entidades em segundo plano sob `Connections.sql.<NomeDaConexao>`:

```fsharp
fsql {
    for t in Connections.sql.Chinook.Track do
    where (t.Milliseconds > 300000 && isNotNull t.Composer && isNotEmpty t.Name)
    sortByDescending t.Milliseconds
    take 20
    select {| Titulo = t.Name; Compositor = t.Composer; DuracaoMs = t.Milliseconds; PrecoUnitario = t.UnitPrice |}
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

### Joins Externos 100% Idiomáticos com `fullOuterJoin`
Escreva joins externos completos sem strings cruas, usando a sintaxe LINQ com `GroupJoin` e `DefaultIfEmpty()`:

```fsharp
fsql {
    for t in Connections.sql.Chinook.Track do
    fullOuterJoin g in Connections.sql.Chinook.Genre on (t.GenreId = g.GenreId) into generos
    for g in generos.DefaultIfEmpty() do
    select {| Faixa = t.Name; Genero = (if isNull (box g) then "Sem Genero" else g.Name) |}
}
```

### Common Table Expressions (`withCte`) & Operações de Conjunto
O FSQL permite declarar CTEs e operações de conjunto diretamente na CE, sem tabelas fictícias:

```fsharp
// CTE Sem Nome: Injetada de forma transparente no plano SQL
let faixasLongas =
    fsql {
        for t in Connections.sql.Chinook.Track do
        where (t.Milliseconds > 400000)
        select t
    }

fsql {
    for a in Connections.sql.Chinook.Album do
    withCte faixasLongas
    join t in faixasLongas on (a.AlbumId = t.AlbumId)
    select {| Album = a.Title; Faixa = t.Name |}
}

// Operações de Conjunto na CE (union, unionAll, intersect, except)
let todasFaixas =
    fsql {
        for t in faixasDigitais do
        union faixasFisicas
    }
```

### Diagnóstico de Planos com `explain` e SQL com `toRawQuery`
Quer verificar a query SQL exata gerada pelo compilador com identação e documentação de parâmetros ou inspecionar o plano de execução?

```fsharp
// 1. SQL formatado com cabeçalho de parâmetros
let q =
    fsql {
        for t in Connections.sql.Chinook.Track do
        where (t.Milliseconds > 300000)
        select t
    }

printfn "%s" (toRawQuery q)

// 2. Diagnóstico de plano de execução diretamente na CE
fsql {
    for t in Connections.sql.Chinook.Track do
    where (t.Milliseconds > 300000)
    explain
    select t
}
```

Para agentes de inteligência artificial e assistentes de código, consulte as regras formais no **[FSQL AI Agent Guide](FSQL_AI_AGENT_GUIDE_PT.md)**.

### Consultas SQL Tradicionais Parametrizadas
Você também pode executar comandos SQL parametrizados diretamente:

```fsharp
Connections.sql.Chinook.rawQuery
    "select Name, Milliseconds from Track where GenreId = @genero order by Milliseconds desc limit 20"
    [ "genero", box 1 ]
```

---

## 6. Exportando Resultados para CSV

Execute uma célula cuja **última expressão** seja uma consulta de conector:

```fsharp
Connections.sql.Chinook.rawQuery "select * from Track limit 200" []
```

A grade possui o botão **Export CSV**. O valor de cauda retornado por uma consulta de conector é totalmente materializado e mantido por notebook: o botão **Export CSV** grava o resultado da célula de grade materializada mais recente — o conjunto completo de resultados, não apenas a página visível na tela. Executar outra célula que retorne uma grade substitui o que o **Export CSV** irá gravar.

Clique em **Export CSV**, escolha o local para salvar e pronto.

O botão **Export CSV** fica disponível sempre que a última expressão da célula for renderizada como uma grade apoiada por linhas totalmente materializadas — uma consulta de conector (`Connections.sql` / `csv` / `excel`) ou qualquer sequência computada de records, tuplas ou dicionários. Escalares, texto simples e sequências escalares transmitidas sob demanda não possuem nada a exportar.

---

## 7. O que uma célula exibe

Uma célula renderiza o valor de sua **última expressão** — o mesmo valor que o F# Interactive ecoaria. Você não precisa chamar nenhuma função especial.

| Última expressão | Renderiza como |
|---|---|
| Uma consulta de conector (`Connections.sql …`, `Connections.csv …`, …) | Uma grade, com botão **Export CSV** ativo |
| Uma sequência de records | Uma grade; as colunas são os campos do record |
| Uma sequência de tuplas | Uma grade; as colunas são `Item1`, `Item2`, … |
| Uma sequência de `Map`s / dicionários | Uma grade; as colunas são a união das chaves |
| Uma sequência de coleções (ex: `int[] list`, linhas de arrays) | Uma grade; a primeira coleção interna se torna os cabeçalhos das colunas, o restante são as linhas |
| Uma sequência de escalares | Uma grade de coluna única, transmitida sob demanda com **Carregar mais** |
| Um único record | Uma grade de linha única |
| Um escalar (`int`, `string`, `DateTime`, …) | Texto simples |
| Uma vinculação `let` ou um `printfn` (sem valor de retorno) | Apenas a saída impressa |

Para forçar a exibição em texto estruturado simples, termine a célula com `sprintf "%A" valor`.

Exemplos:

```fsharp
// uma coleção computada — uma lista de tuplas renderiza com cabeçalhos Item1 / Item2
[ "alice", 3; "bob", 7; "carol", 1 ]
```

Uma lista de tuplas é renderizada com cabeçalhos posicionais `Item1` / `Item2` — projete-a em um record se quiser colunas nomeadas.

```fsharp
// força a visualização em texto para algo que normalmente seria renderizado como grade
sprintf "%A" (Connections.sql "chinook" "select * from Album limit 3" [])
```

---

## 8. Conexões de Arquivos CSV e Excel (.xlsx) com Cache em Disco

### Auto-Importação via Pasta `data_sources/`
A maneira mais prática de registrar fontes de arquivos é utilizar a pasta `data_sources/`:
- Coloque seus arquivos `.csv`, `.xlsx`, `.db` ou `.sqlite` na pasta `data_sources/`.
- Execute o comando **F# Query Studio: Auto-Import Data Sources Directory** na Command Palette (ou clique no botão na barra lateral de Conexões).
- Os arquivos são detectados, ganham tipos fortemente tipados e são pré-carregados de forma assíncrona em cache persistente SQLite em `.fsqs/cache/engine.db`.

### Consultando Arquivos com `fsql`
Consulte seus arquivos diretamente usando acessadores tipados:

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
    for row in Connections.excel.RegionalTargets.RegionalTargets do
    where (row.GrowthRate >= 10.0M)
    select row
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

## 9. Conexões com Bancos Relacionais (SQL Server, PostgreSQL, MySQL, MariaDB, Oracle)

Escolha o tipo `SqlServer`, `PostgreSql`, `MySql`, `MariaDb` ou `Oracle`. As seguintes informações serão solicitadas:

1. **Connection string** — informe-a com `{password}` onde a credencial deve entrar:
   - **PostgreSQL**: `Host=localhost;Database=mydb;Username=postgres;Password={password}`
   - **SQL Server**: `Server=localhost;Database=master;User Id=sa;Password={password};TrustServerCertificate=True`
   - **MySQL**: `Server=localhost;Database=fsqs_demo;Uid=root;Pwd={password};`
   - **MariaDB**: `Server=localhost;Port=3307;Database=fsqs_demo;Uid=root;Pwd={password};`
   - **Oracle**: `Data Source=localhost:1521/FREEPDB1;User Id=system;Password={password}`
2. **Senha (opcional)** — digitada de forma mascarada. Ela é armazenada no **chaveiro do sistema operacional** através da API `SecretStorage` do VS Code.

Apenas a connection string com o marcador literal `{password}` é salva no `workspace.json`. O segredo real é recuperado do chaveiro em tempo de execução da consulta e nunca é gravado no arquivo do workspace nem no notebook.

Consulte-os de forma fortemente tipada com `fsql { ... }`:

```fsharp
fsql {
    for o in Connections.sql.Sales.Orders do
    where (o.TotalAmount >= 100.0f)
    select {| OrderId = o.Id; Total = o.TotalAmount |}
}
```

Ou consulte-os utilizando SQL parametrizado bruto:

```fsharp
Connections.sql "Sales" "select top 100 * from Orders" []
```

---

## 10. Conexões REST JSON

Adicione uma conexão do tipo `REST JSON`; o prompt solicitará a **URL base** (ex: `https://api.github.com`).

### Consultas Fortemente Tipadas com `fsql`
O F# Query Studio gera records tipados automaticamente sob `Connections.rest.<Conn>`:

```fsharp
fsql {
    for repo in Connections.rest.GitHubFSharp do
    sortByDescending repo.stargazers_count
    select {| Repositorio = repo.full_name; Estrelas = repo.stargazers_count; Url = repo.html_url |}
}
```

### Requisições HTTP Customizadas de Baixo Nível
Para requisições REST customizadas que exigem tokens de autenticação, cabeçalhos personalizados ou parâmetros de consulta específicos, `Connections.rest` retorna um `System.Text.Json.JsonElement` a partir de uma `Task` assíncrona:

```fsharp
open System.Threading

let payload =
    Connections.rest
        "my-api"
        { RestRequestOptions.Get "https://api.example.com/v1/orders" with
            QueryParams = [ "status", "open" ]
            Headers = [ "Accept", "application/json" ] }
        CancellationToken.None
    |> fun t -> t.Result

[ for el in payload.EnumerateArray() ->
    {| id = el.GetProperty("id").GetInt32()
       total = el.GetProperty("total").GetDecimal() |} ]
```

---

## 11. Assistência F# na célula

Conforme você digita em uma célula, você recebe:

- **autocomplete** — membros, valores em escopo e nomes de conexões dentro de literais de string em `Connections.*`,
- **hover** — tipos de dados e comentários de documentação,
- **signature help** — dicas de parâmetros durante a digitação de chamadas,
- **diagnósticos** — sublinhados ondulados vermelhos para erros, por célula,
- **Go to Definition (`F12`)** — navegação direta para definições entre células, conexões e records de entidades gerados.

Essa inteligência é fornecida diretamente pelo worker do serviço de linguagem integrado da extensão.

---

## 12. Solução de Problemas Frequentes

| Sintoma | Solução Recomendada |
|---|---|
| Célula não executa ou erro de "worker" | Execute **F# Query Studio: Check Worker**. Geralmente o .NET 10 SDK não foi encontrado no PATH. Instale-o e reinicie o VS Code. |
| "The worker exited before responding" | A sessão do FSI travou e foi encerrada. Ela será reiniciada na sua próxima execução; execute novamente as células que precisar. |
| `Connections has not been configured` | A conexão informada não foi registrada ou o workspace ainda não possui conexões salvas (`workspace.json`). Cadastre-a pela barra lateral. |
| Alterações em arquivos CSV / Excel não refletem | O motor valida data e tamanho. Se necessário, execute o comando **F# Query Studio: Sync Data Sources Cache** na Command Palette para re-estagiar os arquivos no cache SQLite. |
| O botão **Export CSV** informa que não há nada para exportar | A última expressão da célula não produziu uma grade materializada — foi um escalar, texto simples ou sequência escalar transmitida. Certifique-se de que a última linha seja uma consulta de conector ou uma sequência computada de records, tuplas ou dicionários. |
| Uma redefinição ou novo `open` "não tem efeito" | Execute **Restart Notebook Session** — o FSI retém a definição anterior durante toda a vida útil da sessão. |
| Uma célula parece travada | Apenas uma avaliação é executada por vez em toda a janela. Interrompa a célula em execução (botão ▪) ou aguarde seu término. |
| Uma célula de um notebook antigo falha com `error FS0039: The value, namespace, type or module 'Results' is not defined` | A API `Results.raw` / `table` / `tableWithColumns` / `text` foi removida. Exclua o pipe `\|> Results.…` (ou a chamada `Results.…`) — a última expressão da célula agora renderiza por conta própria. |

---

## Onde ir em seguida

- [`../README_PT.md`](../README_PT.md) — visão geral do projeto e layout do repositório.
- [`USER_GUIDE_PT.md`](USER_GUIDE_PT.md) — guia do usuário abrangente e referência de conectores.
- [`BENCHMARK_REPORT_PT.md`](BENCHMARK_REPORT_PT.md) — benchmarks de desempenho de execução, latência e recursos.
- [`../editors/vscode/docs/README_PT.md`](../editors/vscode/docs/README_PT.md) — referência completa de recursos e APIs da extensão.
- [`README_PT.md`](README_PT.md) — índice de toda a documentação sob `docs/`.
