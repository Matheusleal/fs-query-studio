# F# Query Studio

[🇺🇸 English](README.md) | [🇧🇷 Português](README_PT.md)

Escreva, analise e execute scripts de consulta F# contra bancos de dados SQL, arquivos locais e APIs REST — diretamente em notebooks do VS Code.

O F# Query Studio é uma **extensão para o VS Code**. Você abre arquivos `.fsqnb` como *F# Query Notebooks*; cada célula de código executa F# em uma sessão FSI persistente hospedada em um processo worker .NET nativo. Uma API unificada e segura (`Connections.*`) conecta-se aos bancos de dados e arquivos registrados na barra lateral, renderizando os resultados em um grid interativo virtualizado com visualização de esquema e exportação para CSV.

> **Status:** `0.6.0`, pronto para distribuição. Estúdio de consultas e exploração multi-fonte de alta performance com pushdown completo de SQL, conectores nativos para JSON e XML com sub-propriedades no estilo Excel, cache persistente assíncrono em disco SQLite com índices B-tree compostos, otimização de joins multi-propriedade sem explosão de memória, descoberta automatizada de arquivos em `data_sources/`, visualização de SQL gerado (`toRawSql`), benchmarks sub-milissegundo comprovados e 100% de cobertura de testes.
> Veja o [Release v0.6.0](https://github.com/Matheusleal/fs-query-studio/releases/tag/v0.6.0) e o guia de publicação em [`docs/vscode-extension-release.md`](docs/vscode-extension-release.md).

---

## Requisitos

- **.NET 10 SDK** no seu `PATH` — `dotnet --version` deve reportar `10` ou superior. A extensão inicializa um processo worker F# com ele.
- **VS Code 1.90** ou superior.

Suporte nativo a Windows, macOS e Linux. Um único processo worker é executado por janela do VS Code.

## Instalação

Disponível via build local VSIX ou download direto dos artifacts do GitHub Releases:

```bash
cd editors/vscode
npm install
npm run package          # gera editors/vscode/fs-query-studio.vsix
code --install-extension fs-query-studio.vsix --force
```

O pipeline de CI também publica o pacote VSIX automaticamente (`fs-query-studio-vsix`) a cada push nas branches `main` e `main-vscode` que alteram a extensão ou o worker — você pode baixá-lo em vez de compilar localmente.

## Início Rápido

1. **Novo notebook ou gerador de demonstrações** — execute o comando **F# Query Studio: New F# Query Notebook** na Command Palette (`Ctrl+Shift+P`), ou clique no botão **Create Sample Data & Demo Notebooks** (`$(database)`) na barra de conexões para gerar notebooks de demonstração prontos para execução (`sample_demo.fsqnb`, PostgreSQL ou SQL Server) com escolha de idioma (**English**, **Português** ou **Both / Ambos**).
2. **Execute F#** — digite código F# em uma célula e execute-a. Declarações `let` persistem para as células seguintes (uma sessão FSI por notebook). O comando **F# Query Studio: Restart Notebook Session** reinicializa a sessão quando desejado.
3. **Adicione ou importe conexões automaticamente** — adicione arquivos `.csv`, `.xlsx`, `.json`, `.xml` ou `.db` na pasta `data_sources/` do projeto e execute **F# Query Studio: Auto-Import Data Sources Directory**, ou use **Add Connection** no painel da barra lateral. Fontes de arquivos são sincronizadas de forma assíncrona em cache persistente SQLite (`.fsqs/cache/engine.db`) com índices B-tree compostos automáticos.
4. **Consulte com Pushdown Completo para SQL & fsql** — consultas `fsql` são traduzidas diretamente para SQL nativo no banco de dados (`SELECT`, `WHERE`, `JOIN`, `LEFT/RIGHT/FULL OUTER JOIN`, `ORDER BY`, `LIMIT`, `SKIP`, `COUNT`, `SUM`, `EXISTS`, `HAVING`, `CROSS APPLY`, `LATERAL`, `CTEs`, `UNION`, `INTERSECT`, `EXCEPT`, `WINDOW`), sem puxar tabelas inteiras para a memória RAM:
   ```fsharp
   // DQL funcional puro com fsql
   fsql {
       for u in Connections.sql.MinhaConexao.Users do
       where (u.IsActive && u.Age >= 21)
       whereExists (fsql { for o in Connections.sql.MinhaConexao.Orders do where (o.UserId = u.Id) })
       sortBy u.LastName
       select {|
           Nome = u.FirstName + " " + u.LastName
           Email = u.Email
           TotalPedidos = fsql { for o in Connections.sql.MinhaConexao.Orders do where (o.UserId = u.Id); count }
       |}
   }
   ```
5. **Inspecione a Query SQL Gerada (`toRawQuery`) ou Diagnósticos (`explain`)** — visualize a query SQL parametrizada exata com formatação profissional ou plano de execução antes de rodar:
   ```fsharp
   printfn "%s" (toRawQuery minhaQuery)
   ```
   Ou execute comandos SQL brutos: `Connections.sql.MinhaConexao.rawQuery "select * from Orders" []`.
   O resultado é exibido em uma grade virtualizada interativa com aba de esquema, filtros e botão **Export CSV**.

Guia prático passo a passo: **[`docs/TUTORIAL_PT.md`](docs/TUTORIAL_PT.md)** (English: [`docs/TUTORIAL.md`](docs/TUTORIAL.md)).  
Guia Completo do Usuário: **[`docs/USER_GUIDE_PT.md`](docs/USER_GUIDE_PT.md)** (ou clique no ícone `$(book)` no VS Code).  
Guia para Agentes de IA: **[`docs/FSQL_AI_AGENT_GUIDE_PT.md`](docs/FSQL_AI_AGENT_GUIDE_PT.md)** (English: [`docs/FSQL_AI_AGENT_GUIDE.md`](docs/FSQL_AI_AGENT_GUIDE.md)).  
Especificação Formal da Linguagem: **[`docs/FSQL_LANGUAGE_SPECIFICATION.md`](docs/FSQL_LANGUAGE_SPECIFICATION.md)**.

## O que a Extensão Oferece

- **Execução em Notebooks** — cada célula `.fsqnb` executa em uma sessão FSI isolada por notebook; saída ao vivo de stdout / stderr; cancelamento de células demoradas pela barra de ferramentas.
- **Pushdown Completo de Queries para SQL** — tradução estilo Entity Framework para bancos relacionais e arquivos planos. Transforma projeções (`select`), filtros (`where`), joins (`join`, `leftOuterJoin`, `rightOuterJoin` e o 100% idiomático `fullOuterJoin` com `into ... DefaultIfEmpty()`), joins multi-propriedades (`on ((a.p1, a.p2) = (b.p1, b.p2))`), ordenação, fatiamento (`take`/`skip`), agregações (`count`/`sum`) e funções analíticas de janela (`rowNumber`, `rank`, `sumOver`, etc.) em instruções SQL nativas ultra-rápidas.
- **Common Table Expressions (CTEs) & Operações de Conjunto** — Declare CTEs de forma transparente usando `withCte subquery` sem nome (sem necessidade de registrar tabelas fictícias) ou `withCte "Nome" subquery`, além de `withRecursiveCteSql` para consultas recursivas. Componha consultas e operações de conjunto (`union`, `unionAll`, `intersect`, `except`) em variáveis separadas com propagação automática de CTEs para a query principal.
- **Gerador Unificado de Demonstrações Multi-Engine e Multi-Idioma** — Ponto único de entrada na barra de status e paleta de comandos. Gera workspaces completos para SQLite/CSV/Excel/REST, PostgreSQL ou SQL Server com seleção de idioma (**English**, **Português** ou **Both / Ambos**) e até 27 seções interativas de consulta em cada notebook.
- **Cache Persistente em Disco SQLite & Joins Rápidos em Arquivos** — arquivos planos (CSV e Excel `.xlsx`) são pré-carregados em background em um banco SQLite persistente em disco (`.fsqs/cache/engine.db`). A sincronização valida a data de modificação (`mtime`) e o tamanho em bytes para evitar reprocessamento desnecessário. Índices B-tree compostos automáticos eliminam produtos cartesianos e consumo excessivo de RAM em arquivos com mais de 200.000 linhas.
- **Inferência Precisa de Tipos Tabulares & Fórmulas** — Esquemas de Excel e CSV detectam tipos reais (inteiros, decimais, datas, booleanos) na árvore e nas entidades. Suporta tipos customizados (`columnTypes`) e fórmulas de transformação (`columnTransforms`, ex: `replace('.', '').replace(',', '.')` ou `trim`) com preservação total de números decimais de alta precisão. Instruções detalhadas e exemplos reais: [Guia do Usuário - Mapeamentos e Fórmulas](docs/USER_GUIDE_PT.md#tipos-de-coluna-personalizados--transformações-com-fórmulas-excel--csv).
- **Diagnósticos de Plano de Execução & Visualização Prévia (`explain` / `toRawQuery`)** — inspecione o SQL bruto parametrizado ou o plano de execução a qualquer momento via `toRawQuery`, `toRawSql` ou a operação customizada `explain` dentro da CE.
- **Descoberta Automática de Fontes (`data_sources/`)** — adicione arquivos `.csv`, `.xlsx`, `.json`, `.xml`, `.db` ou `.sqlite` na pasta `data_sources/` do projeto e clique em **Auto-Import Data Sources** para detectar, registrar e tipar conexões instantaneamente.
- **Status Visual de Sincronização** — indicador animado na barra de status (`$(sync~spin) Sincronizando fontes de dados...` ➔ `$(database) Fontes prontas`) informa o progresso e prontidão das fontes em tempo real.
- **Consultas LINQ Fortemente Tipadas** — geração automática de tipos record e contextos LINQ em background para bancos de dados (PostgreSQL, SQL Server, MySQL, SQLite) e arquivos (CSV, Excel com múltiplas abas, JSON, XML).
- **API `Connections` Unificada** — `Connections.sql` / `csv` / `excel` / `json` / `xml` / `rest` acessam conexões diretamente com tipos gerados em background e execução `rawQuery` isolada por conexão. O valor da última expressão é renderizado automaticamente — coleções geram o grid, conectores adicionam **Export CSV**, valores escalares são exibidos como texto formatado.
- **Grid de Resultados Interativo** — tabela virtualizada de alta performance, alternância para visualização de metadados/esquema, exportação para CSV e paginação contínua ("Load More") para grandes volumes.
- **Inteligência de Linguagem no Editor** — autocompletação, hover/tooltips, signature help, diagnósticos em tempo real, realce semântico de tokens e navegação **Go to Definition (`F12`)** entre células, tipos gerados e modelos de dados, além de autocomplete contextual de conexões filtrado por tipo.
- **Explorador de Conexões** — visualização em árvore na Activity Bar organizada por conexões → schemas → tabelas/views → colunas, com carregamento sob demanda e wizards de criação/edição.
- **Documentação Embutida** — clique no ícone de livro `$(book)` na barra de conexões ou no título do editor para acessar o Guia do Usuário traduzido sem sair do editor.

Detalhes e limitações: **[`editors/vscode/docs/README_PT.md`](editors/vscode/docs/README_PT.md)** (English: [`editors/vscode/README.md`](editors/vscode/README.md)).

## Benchmarks de Execução e Confiabilidade

O F# Query Studio foi projetado com foco em latência mínima, consumo previsível de memória e ausência de travamentos de interface:

- **Consultas Sub-milissegundo**: Conexões reutilizadas via pool ADO.NET executam queries entre `0.05 ms` e `0.20 ms` com uso negligenciável de CPU e zero coletas Gen 1/2 no GC.
- **Escalabilidade com Streaming**: Processa conjuntos de 100 até 50.000+ linhas via streaming iterativo de cursores (`Results.fs`), mantendo uma linha de base estável de `1.5 KB` sem materializar tabelas gigantescas na memória heap.
- **LINQ Compilado com Cache de Reflexão**: Mapeamento de records F# (`Connections.query<'T>`) e expressões `fsql { ... }` processam 1.000 registros tipados em `~2.0 ms` usando delegates construtores pré-compilados.
- **Alto Desempenho com Arquivos CSV**: Taxa de transferência contínua de `~500.000 linhas/segundo`.
- **100% de Verificação de Elementos de UI**: Todos os componentes visuais (grid virtualizado, cabeçalhos fixos, destaque de nulos e numéricos, aba de esquema, paginação progressiva, mensagens de aviso e itens do explorador de conexões) possuem testes automatizados dedicados.

Relatório detalhado com tabelas completas: **[`docs/BENCHMARK_REPORT_PT.md`](docs/BENCHMARK_REPORT_PT.md)** (English: [`docs/BENCHMARK_REPORT.md`](docs/BENCHMARK_REPORT.md)).

## Estrutura do Repositório

| Caminho | Descrição |
|---|---|
| `src/FSharpQueryStudio.*` | O worker F# e suas bibliotecas (.NET 10) — hosting FSI, conectores, projeção de resultados, análise FSAC, protocolo e cofre de senhas. |
| `editors/vscode/` | A extensão do VS Code (TypeScript) — controlador de notebooks, renderer do grid de resultados, painel de conexões e cliente do worker. |
| `tests/` | Projetos de testes em .NET e benchmarks de performance. |
| `editors/vscode/test/` | Testes unitários (vitest) e testes no host do VS Code (`@vscode/test-electron`). |
| `docs/` | Documentação do projeto — veja [`docs/README_PT.md`](docs/README_PT.md) para o índice. |

A extensão empacota o worker (`dist/worker/**`) no momento do empacotamento; em tempo de execução o usuário ainda precisa do `dotnet` no `PATH`. Ainda não há uma versão autossuficiente (self-contained).

## Desenvolvimento Local

```bash
cd editors/vscode
npm install
npm run build          # compila o worker em .NET + validação de tipos TypeScript + bundle com esbuild
npm test               # executa todos os testes unitários (vitest)
npm run test:vscode    # executa testes de integração dentro do VS Code
```

Pressione **F5** no VS Code para abrir o Extension Development Host. Defina `FSQS_WORKER_DLL` para apontar para um worker compilado em outro local em vez de `dist/worker`.

Para compilar e testar os projetos .NET:

```bash
dotnet build FSharpQueryStudio.slnx
dotnet test  FSharpQueryStudio.slnx
```

A mecânica de compilação, empacotamento e publicação está documentada em [`editors/vscode/scripts/README.md`](editors/vscode/scripts/README.md); o processo de release está em [`docs/vscode-extension-release.md`](docs/vscode-extension-release.md).

## Licença

[MIT](LICENSE) © 2026 Matheus Leal.
