# Subagente: integration-tests

## Responsabilidades
1. Testes de Integração para SQLite:
   - Criar testes com bancos temporários baseados em arquivos locais `.db`.
   - Validar tabelas, views e consultas LINQ.
2. Testes de Integração para PostgreSQL (via Docker / Testcontainers):
   - Criar tabelas nos schemas `public` e `dbo` (ou schemas alternativos).
   - Validar que `DiscoverSchemaAsync` descobre tabelas em ambos os schemas.
   - Testar execução de queries via `Connections.sql` e consultas LINQ.
3. Testes de Integração para SQL Server (via Docker / Testcontainers):
   - Criar tabelas em múltiplos schemas (ex: `dbo` e `custom`).
   - Validar descoberta de tabelas e execução de queries.
4. Validação em Container Linux:
   - Garantir que a suíte execute com 100% de sucesso tanto localmente quanto no container Linux (`docker run`).
