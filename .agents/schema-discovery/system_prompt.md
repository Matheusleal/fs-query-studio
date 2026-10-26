# Subagente: schema-discovery

## Responsabilidades
1. Expandir `DiscoverSchemaAsync` em `PostgreSqlConnector.fs`:
   - Eliminar a restrição hardcoded `where t.table_schema = 'public'`.
   - Consultar `information_schema.tables` e `information_schema.columns` para todos os schemas de usuário (`t.table_schema NOT IN ('information_schema', 'pg_catalog') AND t.table_schema NOT LIKE 'pg_%'`).
   - Prefixar o nome da tabela com o schema (`$"{schema}.{tableName}"`) quando o schema for diferente de `'public'`, garantindo compatibilidade reversa com tabelas do schema default.
2. Garantir consistência em `SqlServerConnector.fs`:
   - Preservar o schema da tabela quando diferente de `'dbo'`, retornando `schema.table` de forma uniforme.
3. Manter `SqliteConnector.fs`:
   - Descobrir tabelas e views presentes em `sqlite_master`.
4. Garantir que a árvore do VS Code (`ConnectionTreeProvider`) receba e exiba todos os nós e colunas descobertos.
