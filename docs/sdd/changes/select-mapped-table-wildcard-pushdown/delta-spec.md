# Delta Specification: Suporte a Projeção de Tabelas Mapeadas Inteiras no `select` (`alias.*`)

## 1. ADDED Requirements

### [REQ-SELECT-TABLE-WILDCARD-001] Resolução e Projeção Curinga de Tabela Mapeada em Records
```text
When a query projection contains a record or anonymous record expression where one or more members are assigned to a mapped table reference (range variable, tuple element, or parameter),
the query translator shall translate each mapped table member into `<alias>.*` in the SQL `SELECT` list without a column alias, where `<alias>` corresponds to the resolved base table or joined table alias in the execution plan.
```

### [REQ-SELECT-TABLE-MIXED-002] Projeção Mista de Tabelas Curinga e Propriedades Escalares
```text
When a query projection contains both mapped table members and scalar property expressions in any order,
the query translator shall emit `<alias>.*` for each mapped table member and `<expression> AS [<Alias>]` (using the target dialect's quoted identifier) for each scalar property expression.
```

### [REQ-SELECT-TABLE-MULTI-003] Suporte a Múltiplas Tabelas Mapeadas Curinga no Mesmo Select
```text
When a query projection references multiple mapped tables as members of the projected record,
the query translator shall emit the corresponding `<alias>.*` clause for each referenced table in the order declared in the projection.
```

### [REQ-SELECT-DIRECT-TABLE-004] Projeção Direta de Tabela Mapeada em Queries Relacionais
```text
When a query with one or more joins projects a mapped table variable directly (e.g., `select t0` or `select t1`),
the query translator shall set the SQL projection to `<alias>.*` corresponding to the selected table's alias.
```

### [REQ-MAPPER-NESTED-RECORD-005] Hidratação de Records Aninhados em Tempo de Execução
```text
When an executed query projects a mapped table into a record field whose declared type is an F# record or entity class,
the record mapper shall instantiate the nested record type by mapping columns from the result set matching the nested record's field names.
```

---

## 2. MODIFIED Requirements

### [REQ-PUSHDOWN-PROJ-DISPATCH] Despacho de Projeção em `NewExpression`
- **MODIFIED FROM**: O despachante de projeção de `NewExpression` assume que todos os argumentos são expressões escalares e tenta traduzir cada argumento via `translateScalarExpr`.
- **MODIFIED TO (GEARS)**:
```text
When the pushdown translator processes arguments of a `NewExpression` in query projection,
the query translator shall first inspect each argument with `tryResolveTableAlias`; if a table alias is resolved, it shall emit `<alias>.*`, and if no table alias is resolved, it shall evaluate the argument as a scalar expression via `translateScalarExpr` and emit `<sqlExpr> AS [<Alias>]`.
```
