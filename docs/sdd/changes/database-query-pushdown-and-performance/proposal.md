# Change Proposal: Database Query Pushdown & High-Performance Mapping

## Intent Block
```text
Intent Block:
Goal:        Eliminar a transferência e materialização desnecessária de grandes volumes de dados em memória nas consultas de banco de dados do F# Query Studio, provendo um mecanismo de SQL Pushdown para expressões LINQ (query { ... }), corrigindo o ciclo de vida e re-enumeração dos DataReaders, e otimizando o mapeamento de entidades F# com compilação de expressões.
Expectation: O usuário executa consultas com expressões LINQ (query { for o in db.sales.orders do where ... take ... select ... }) ou métodos LINQ sobre conexões de banco de dados reais (PostgreSQL, SQL Server, SQLite), e o F# Query Studio traduz automaticamente filtros, ordenação, limites e agregações em SQL com parâmetros seguros, trazendo do banco apenas as linhas e colunas necessárias, reduzindo o tempo de consulta em até 15-27x e o uso de memória em mais de 90%.
Action:      Implementar um provedor LINQ nativo com SQL Pushdown (`SqlPushdownQueryable` / `SqlPushdownQueryProvider`); refatorar o ciclo de vida de `ExecuteQuery` nos conectores SQL para suportar re-enumeração e streaming sob demanda; construir um mapeador compilado de alta performance (`Expression<Func<IDataRecord, 'T>>`) que lê tipos primitivos diretamente de `IDataRecord` sem alocar `obj[]` intermediários; atualizar `GenerateEntities` para conectar os novos queryables com suporte a SQL pushdown e helpers de paginação e filtro.
Result:      Consultas sobre bases de dados com dezenas ou centenas de milhares de linhas executam em milissegundos com baixo consumo de memória, sem risco de OutOfMemory ou travamento do worker.
```

## Contexto & Motivação
1. **Problema do Full Table Scan In-Memory**:
   - Atualmente, `DatabaseConnection.Queryable<'T>` executa `SELECT * FROM <table>` no banco de dados e simplesmente empacota o resultado em um `EnumerableQuery<'T>`.
   - Isso significa que qualquer expressão F# `query { for o in db.sales.orders do where (o.id = 42) select o }` carrega todas as linhas da tabela pela rede para a memória RAM do processo, instancia todas como records F# via reflexão e executa o filtro no cliente via LINQ to Objects.
   - Em testes de integração reais com PostgreSQL e SQL Server em Docker (tabelas de 10k e 15k linhas), a consulta em memória consumiu mais de 7 MB de RAM e 150-170ms, enquanto a execução com SQL WHERE consumiu apenas 390-466 KB de RAM e 9-22ms (ganho de mais de 15x em velocidade e 93.5% de economia de memória; em agregações, 99.98% de economia de memória).

2. **Problema do Ciclo de Vida do DataReader (Re-enumeração Quebrada)**:
   - Os conectores (`SqliteConnector`, `PostgreSqlConnector`, `SqlServerConnector`) instanciam o `DbConnection` e o `DbDataReader` antes da criação do bloco `seq { ... }`.
   - Ao iterar a primeira vez, o bloco `use` descarta o `reader` e fecha a conexão. Se a sequência for enumerada uma segunda vez (ou em operações como `Seq.length`, seguidas de iteração), o runtime lança `ObjectDisposedException` ou `InvalidOperationException: Invalid attempt to call Read when reader is closed`.

3. **Sobrecarga de Alocação e Reflexão no Mapeador**:
   - Para cada linha retornada, o conector aloca um `obj[]` inicial do leitor (`reader.GetValue(i)`), seguido de um segundo `obj[] args` para os argumentos do construtor, múltiplas chamadas dinâmicas a `convertValue` com boxing/unboxing e invocação reflexiva do construtor F#.
   - Em 15.000 linhas, o mapeamento adiciona ~2 MB de alocações transitórias apenas para instanciar os registros.

## Escopo
- **In Scope**:
  - Implementação de `SqlPushdownQueryable<'T>` e `SqlPushdownQueryProvider` para traduzir árvores de expressão LINQ (`Where`, `Take`, `Skip`, `OrderBy`, `OrderByDescending`, `ThenBy`, `ThenByDescending`, `Count`, `Sum`) em cláusulas SQL com parâmetros seguros (`@p0`, `@p1`).
  - Fallback transparente para avaliação em memória de predicados complexos ou funções F# arbitrárias não traduzíveis.
  - Refatoração de `ExecuteQuery` nos conectores (`PostgreSqlConnector`, `SqlServerConnector`, `SqliteConnector`) para fornecer sequências re-enumeráveis (`IEnumerable`) que gerenciam a conexão/leitor no ciclo de vida de cada enumerador.
  - Otimização do mapeador de registros F# via `System.Linq.Expressions`: compilação de delegado `Func<IDataRecord, 'T>` que lê tipos nativos direto do `IDataRecord` (`GetInt32`, `GetString`, `GetDecimal`, `GetDateTime`, etc.), eliminando arrays intermediários e reflexão por linha.
  - Atualização do gerador `GenerateEntities` para emitir propriedades tipadas usando `SqlPushdownQueryable` e expor helpers (`.Where(...)`, `.Page(...)`).
  - Validação em testes unitários e testes de integração com Docker para PostgreSQL e SQL Server.

- **Out of Scope**:
  - Joins automáticos em pushdown entre servidores de banco de dados distintos (consultas federadas heterogêneas).
  - Implementação de um ORM completo com tracking de estado para escrita/mutação (o F# Query Studio tem foco analítico e de consulta).

## Success Criteria
- [ ] Consultas com `query { for o in db.orders do where (...) take (...) select (...) }` executam com Pushdown de `WHERE`, `ORDER BY`, `LIMIT`/`TOP` e parâmetros seguros no banco de dados.
- [ ] Sequências de consulta SQL podem ser enumeradas mais de uma vez sem disparar `ObjectDisposedException` ou leitor fechado.
- [ ] O mapeamento de linhas para records F# utiliza delegado compilado via expressões, reduzindo alocações transitórias e tempo de CPU.
- [ ] Todas as 20+ suítes de testes de integração (incluindo testes com Docker em multi-schema de 10k e 15k linhas) passam com 100% de sucesso.
- [ ] Nenhum arquivo residual ou temporário é mantido em disco após os testes.
