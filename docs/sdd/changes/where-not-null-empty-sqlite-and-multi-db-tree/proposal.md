# Proposal: Correção de `not` e `isNull` no `where`, Condições `notNull`/`notEmpty`, Query Direta no SQLite para XML/CSV e Mapeamento Multi-Database em Árvore

## 1. Contexto e Problema
1. **Falha do `not` com `isNull` na cláusula `where`**: Consultas `fsql` que utilizam `where (not (isNull tabela.coluna))` não traduzem `isNull` em `translatePredicate` / `translateScalarCondition`, caindo em fallback in-memory (que falha quando há projeção `select` ou quando `string` nulo é mapeado para `""`).
2. **Ausência de condições dedicadas para `not null` e `not empty`**: Usuários precisam de funções e operações declarativas no `fsql` para validar campos não nulos (`notNull` / `isNotNull` / `whereNotNull`), não vazios (`notEmpty` / `isNotEmpty` / `whereNotEmpty`) e ambos (`notNullOrEmpty` / `isNotNullOrEmpty` / `whereNotNullOrEmpty`).
3. **Query direta no SQLite não funciona para XML e CSV mapeados**: Quando o usuário executa queries SQL diretas no SQLite (`Connections.sql.query`, `Connections.sql.rawQuery`, `Connections.queryEngine`, ou `.rawQuery` nas conexões mapeadas) ou consulta diretamente um XML/CSV mapeado usando o nome da conexão ou o nome exibido na árvore de conexões (`sales_feed`, `Shipments`, `Carriers`), a tabela pode não estar pré-stageada no `engine.db` ou possui apenas o nome prefixado `<Conn>_<Table>`, resultando em erro `no such table`.
4. **Mapeamento de todos os databases existentes em árvore**: Ao adicionar uma conexão de banco de dados (SQL Server, PostgreSQL, MySQL, MariaDB, Oracle, SQLite), o conector deve buscar todos os databases acessíveis no servidor e mapeá-los seguindo o padrão de estrutura em árvore (tanto na TreeView do VS Code quanto nas entidades geradas `Connections.sql.<Conn>.<Database>.<Table>`).

---

## 2. Intent Block

```text
Intent Block:
Goal:        Garantir tradução SQL pushdown completa para negações e checagens de nulo/vazio, habilitar queries diretas no SQLite sobre fontes XML/CSV mapeadas e mapear todos os databases existentes em estrutura de árvore ao adicionar conexões de banco de dados.
Expectation: 100% das expressões `where (not (isNull ...))`, `notNull`, `notEmpty` e `notNullOrEmpty` devem gerar SQL nativo parametrizado nos 6 dialetos; queries diretas no SQLite sobre XML e CSV mapeados devem executar sem erro de tabela inexistente; conexões de banco de dados devem descobrir e expor todos os databases em árvore na UI e no código F#.
Action:      Expandir `ExpressionPushdownTranslator` e `FSqlQueryBuilder` em `SqlPushdownQuery.fs`; garantir staging sob demanda e views de alias em `FileSqliteEngine.fs` e `Connections.fs`; atualizar `DiscoverSchemaAsync` nos conectores SQL, `GenerateEntities` em `Connections.fs` e `ConnectionsTreeProvider` em `tree.ts`.
Result:      Consultas FSQL expressivas contra valores nulos/vazios, consultas SQLite diretas transparentes sobre XML/CSV e navegação multi-database em árvore completa.
```
