# SDD Proposal: Universal SQL Pushdown (EF-style Select/Join/Where) & File SQLite Engine (>200k Linhas)

## 1. Intent Block

```text
Intent Block:
Goal:        Transformar o pipeline de execução do F# Query Studio em um mecanismo de consulta de banco de dados de alto desempenho e estilo Entity Framework, com pushdown completo (Select com projeção de colunas, Where, Join relacional nativo, OrderBy, Paginação) para todos os bancos (SQLite, PostgreSQL, SQL Server) e arquivos locais (CSV/Excel via SQLite Staging Engine).
Expectation: Queries LINQ com projeção (`select {| ... |}`), filtros (`where`) e junções (`join`) compilam integralmente para SQL nativo com projeção estrita de colunas (sem `SELECT *` e sem joins em memória .NET), executando em milissegundos mesmo com mais de 200.000 linhas.
Action:      (1) Expandir o compilador de árvores de expressão LINQ (SqlPushdownQuery) para traduzir `Select` (projeção de colunas/campos), `Join` (INNER/LEFT JOIN em SQL) e funções para dialetos SQLite, PostgreSQL e SQL Server. (2) Refatorar a geração de entidades (GeneratedEntities.fs) para referenciar tabelas limpas sem subqueries redundantes. (3) Implementar o Workspace SQLite Staging Engine (.fsqs/cache/engine.db) com cache de invalidação para CSV e Excel, integrando-os ao mesmo pipeline de pushdown.
Result:      Consultas com joins complexos e projeções em qualquer banco de dados ou entre arquivos volumosos executam 100% no motor relacional com zero in-memory join, tráfego de rede e consumo de memória mínimos, projetando diretamente em ResultKind.Table.
```

---

## 2. Why (Contexto & Problema Atual)

1. **Ausência de Pushdown de Projeção (`Select`)**:
   - Atualmente, `SqlPushdownQuery.fs` ignora completamente o método `Select` da árvore de expressão LINQ.
   - O código gerado em `GeneratedEntities.fs` cria uma query base com todas as colunas (`SELECT col1, col2, ... FROM Table`) e a envolve em uma subquery `(SELECT col1, col2 FROM Table) AS _src`.
   - Quando o usuário escreve `select {| Id = c.Id; Name = c.FirstName |}`, o banco de dados continua retornando **todas** as colunas da tabela via rede/disco, e a projeção só é feita posteriormente no cliente .NET por reflexão.
   - Isso desperdiça largura de banda, memória e impede o banco de dados de usar planos de execução baseados em índices cobridores (covering index scans).

2. **Ausência de Pushdown de Junções (`Join`) em Bancos de Dados**:
   - `ExpressionPushdownTranslator.hasJoin` verifica se existe `Join` na expressão. Se existir, o provedor **aborta o pushdown e faz fallback para `LinqInMemoryFallback.evaluate`**!
   - Ou seja: qualquer `fsql { for o in Orders join c in Customers ... }` em PostgreSQL, SQL Server ou SQLite baixa as tabelas inteiras para a memória do processo worker e executa um `Enumerable.Join` no heap do .NET.
   - Em tabelas ou arquivos com mais de 200.000 linhas, isso causa alto consumo de RAM, pausas de GC e lentidão crítica.

3. **Arquivos CSV e Excel sem Engine Relacional**:
   - Arquivos CSV e Excel são processados puramente linha a linha em memória. Cruzar dois arquivos de 200.000 linhas no .NET causa estouro de memória e lentidão inaceitável por falta de índices e otimizador de consultas.

---

## 3. What (A Solução Proposta)

### A. Universal SQL Pushdown Engine (Estilo Entity Framework)
Válido para **todos os bancos de dados** (SQLite, PostgreSQL, SQL Server) e arquivos locais:
1. **Pushdown de `Select` (Projeção de Colunas)**:
   - Analisa lambdas de projeção (`c -> {| Id = c.Id; Nome = c.FirstName |}`, tuplas `(c.Id, c.Name)` ou expressões de membros).
   - Traduz para `SELECT c.Id AS Id, c.FirstName AS Nome` no dialeto do banco de dados alvo.
   - Suporte a expressões simples em projeção (ex.: concatenação de strings `c.FirstName + " " + c.LastName` -> `||` no SQLite/Postgres ou `+` no SQL Server; operações aritméticas básicas).
   - O leitor de dados (`CompiledRecordMapper` ou `IStreamingQueryableConnector`) lê apenas as colunas estritamente solicitadas na projeção.

2. **Pushdown de `Join` (Junções Relacionais Nativas)**:
   - Compila `Queryable.Join` para `INNER JOIN <Table2> <alias2> ON <condição>` no SQL enviado ao banco.
   - Permite consultas relacionais fortemente tipadas multi-tabela em um único comando SQL compilado.
   - Se os joins forem entre entidades do mesmo banco de dados (ou dentro do mesmo SQLite engine para arquivos), o pushdown é 100% nativo.

3. **Geração de Entidades Limpa (`GeneratedEntities.fs`)**:
   - Em vez de embutir queries SQL fixas com todas as colunas como strings (`SELECT col1, col2 FROM Table`), as propriedades de tabela expõem referências diretas à tabela (`conn.Table<schema.Table>("TableName")`).
   - A query SQL é sintetizada de forma canônica: `SELECT <projection> FROM <TableName> <alias> [JOIN ...] [WHERE ...] [ORDER BY ...] [LIMIT/OFFSET]`.

### B. Workspace SQLite Staging Engine para CSV & Excel
1. **Staging Transacional com Cache**:
   - Arquivos CSV e planilhas Excel do workspace são indexados no SQLite local (`.fsqs/cache/engine.db` ou temp DB).
   - Cache com base em `LastWriteTimeUtc` e tamanho: se o arquivo não foi alterado no disco, custo de inicialização = **0 ms**.
   - Ingestão em lote de altíssima velocidade (transação única, WAL, pragmas otimizados, 200k linhas em < 500ms).
   - Criação automática de índices em colunas chave (`Id`, `*_id`, etc.).

2. **Acesso Direto SQL e LINQ**:
   - `Connections.query """SELECT ... FROM SalesFeed s JOIN CustomersFeed c ON s.CustomerId = c.Id"""` para queries SQL diretas cross-file.
   - As coleções `Connections.csv.<Name>` e `Connections.excel.<Name>` conectam-se às tabelas do staging engine, herdando automaticamente o pushdown de `Select`, `Where` e `Join`!

---

## 4. Scope

### In-Scope
- Refatoração de `SqlPushdownQuery.fs`:
  - Tradução de `Select` com projeção estrita de colunas para SQLite, PostgreSQL e SQL Server.
  - Tradução de `Join` relacional nativo para queries multi-tabela no mesmo banco de dados ou no SQLite engine.
  - Mapeamento dinâmico e compilado de registros anônimos (`{| ... |}`) e tuplas baseado nas colunas projetadas.
- Atualização de `EntityBackgroundGenerator.fs` e `Connections.fs`:
  - Modelos de tabela apontando para `TableQueryable` em vez de strings estáticas `SELECT col1, ...`.
- Implementação de `FileSqliteEngine.fs`:
  - Gerenciador de cache SQLite no workspace (`.fsqs/cache/engine.db`).
  - Ingestão otimizada em lote de CSVs e planilhas Excel.
  - Exposição de `Connections.query` para SQL direto.
- Bateria de testes extensiva:
  - Testes com datasets > 200.000 linhas em CSV, Excel e SQLite/Bancos relacionais.
  - Testes de projeção (`Select {| ... |}`) verificando que apenas as colunas projetadas são enviadas no SQL.
  - Testes de `Join` verificando que o SQL gerado contém `JOIN` e não cai no fallback em memória.
  - Garantia de 100% de aprovação na suíte de testes existente e nos notebooks de demonstração (`sample_demo.fsqnb` e `sample_demo_pt.fsqnb`).

### Out-of-Scope
- Pushdown de joins entre bancos remotos heterogêneos distintos (ex: PostgreSQL remoto juntando com SQL Server remoto na mesma query).
- Modificação no formato de persistência do `workspace.json` (o engine é transparente e gerenciado na pasta `.fsqs/cache/`).

---

## 5. Success Criteria

1. **Projeção Estrita (`Select`)**: Em qualquer banco relacional (SQLite, Postgres, SQL Server), ao selecionar 2 colunas de uma tabela com 50 colunas, o comando SQL enviado ao banco contém apenas `SELECT t.col1, t.col2 FROM ...`, comprovado por telemetria e testes de query plan.
2. **Pushdown Nativo de `Join`**: Consultas LINQ com `join` entre tabelas do mesmo banco ou entre arquivos no engine executam via comando SQL único (`JOIN ... ON ...`) sem invocar `LinqInMemoryFallback`.
3. **Escala > 200.000 Linhas**: Um join entre dois arquivos locais de 200.000+ linhas executa no SQLite Staging Engine em poucos segundos, sem travamento de GC e sem consumir centenas de megabytes no heap.
4. **Comunicação Direta**: `Connections.query` permite consultas SQL nativas unificadas sobre os arquivos do workspace, retornando `ResultKind.Table` paginado.
5. **Compatibilidade Total**: Todos os testes do repositório e notebooks `sample_demo` executam com 100% de sucesso.

---

## 6. Impacted Artifacts

- `src/FSharpQueryStudio.Connectors/SqlPushdownQuery.fs`: Pipeline de compilação LINQ para SQL (Select, Join, Where, OrderBy, Dialects).
- `src/FSharpQueryStudio.Connectors/FileSqliteEngine.fs`: Novo motor de staging e cache SQLite para arquivos locais.
- `src/FSharpQueryStudio.Connectors/Connections.fs`: Integração de `Connections.query`, `TableQueryable` e vinculação de CSV/Excel ao engine.
- `src/FSharpQueryStudio.Connectors/EntityBackgroundGenerator.fs`: Ajuste na geração de código tipado de tabelas.
- `src/FSharpQueryStudio.Worker/Handlers.fs`: Inicialização e ciclo de vida do `FileSqliteEngine`.
- `tests/FSharpQueryStudio.Connectors.Tests/`: Testes de projeção SQL, joins nativos e escala > 200k linhas.
- `tests/FSharpQueryStudio.Worker.Tests/`: Testes de avaliação ponta a ponta.
