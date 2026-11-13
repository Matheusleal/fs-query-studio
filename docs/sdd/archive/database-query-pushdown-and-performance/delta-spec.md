# Delta Specification: Database Query Pushdown & High-Performance Mapping

## Requisitos GEARS

### ADDED Requirements

#### FR-QUERY-PUSHDOWN-PROVIDER: Provedor de Query com SQL Pushdown
```text
When a LINQ query expression or method chain operates on a SQL database entity collection,
the query provider shall analyze the expression tree and translate supported clauses
(Where conditions, OrderBy, ThenBy, Take, Skip, Count, and Sum) into parameterized SQL command text
and execute the query directly against the target database connector.
```

#### FR-QUERY-PUSHDOWN-PARAM-BINDING: Parâmetros Seguros em Expressões de Filtro
```text
When a Where filter expression references local variables, constants, or method arguments,
the query provider shall extract the values and bind them as database parameters (@p0, @p1, etc.),
and shall not concatenate raw values into the generated SQL string.
```

#### FR-QUERY-PUSHDOWN-FALLBACK: Fallback Gracioso para Operações Não Suportadas
```text
When a query expression contains operations, methods, or predicates that cannot be translated into SQL,
the query provider shall push down the supported SQL clauses (such as base table and filters) to the database,
and shall evaluate the remaining expression nodes in memory over the resulting projected stream.
```

#### FR-STREAMING-REENUMERABLE-READER: Sequências SQL Re-enumeráveis
```text
When a caller enumerates a query result sequence multiple times,
the SQL connector shall instantiate and open a new DataReader on each enumeration
and shall dispose the reader and connection upon completion or cancellation of that enumeration,
without throwing ObjectDisposedException on subsequent enumerations.
```

#### FR-COMPILED-EXPRESSION-MAPPER: Mapeador Compilado de Alta Performance
```text
When mapping database rows to strongly-typed F# records or tuples,
the mapping engine shall compile an expression delegate that reads typed columns directly from IDataRecord
(such as GetInt32, GetString, GetDecimal, GetDateTime, GetBoolean, and IsDBNull),
and shall construct the target F# record without allocating intermediate object arrays per row.
```

#### FR-ENTITY-PUSHDOWN-HELPERS: Helpers de Filtro e Paginação em Entidades Geradas
```text
Where strongly-typed database entities are generated for a connection,
each generated table accessor shall expose methods for direct parameterized filtering (.Where)
and server-side pagination (.Page) that translate directly into database pushdown queries.
```

---

### MODIFIED Requirements

#### FR-CONN-QUERYABLE-IMPL: Implementação de `DatabaseConnection.Queryable`
```text
When 'DatabaseConnection.Queryable' is invoked with a base SQL query or table name,
the connection shall return a 'SqlPushdownQueryable' backed by the SQL pushdown provider
instead of returning an in-memory 'EnumerableQuery'.
```

#### FR-CONN-VALUE-CONVERSION: Conversão Estendida de Tipos em `convertValue`
```text
When mapping database values to target .NET types,
the conversion engine shall support bidirectional conversions between DateOnly, TimeOnly, DateTime, DateTimeOffset,
and numeric types without throwing InvalidCastException.
```

---

### REMOVED Requirements
*(Nenhum requisito removido; esta mudança aprimora e expande a infraestrutura existente de consultas SQL e conectores).*
