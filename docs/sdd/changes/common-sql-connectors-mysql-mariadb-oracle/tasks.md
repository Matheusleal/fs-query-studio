# SDD Tasks: Suporte a Conectores SQL Comuns (MySQL, MariaDB, Oracle) & Demos Docker

## Milestone 1: Pacotes & Conectores Backend
- [x] **TASK-01**: Atualizar `Directory.Packages.props` com `MySqlConnector`, `Oracle.ManagedDataAccess.Core`, `Testcontainers.MySql`, `Testcontainers.MariaDb`, `Testcontainers.Oracle`.
  - **Requisito**: REQ-CONN-MYSQL-01, REQ-CONN-MARIADB-01, REQ-CONN-ORACLE-01, REQ-TEST-01
  - **Done When**: Pacotes declarados e restauráveis sem conflitos.
- [x] **TASK-02**: Criar `src/FSharpQueryStudio.Connectors.MySql` com `MySqlConnector.fs` e `MySqlDescriptor.fs`.
  - **Requisito**: REQ-CONN-MYSQL-01
  - **Done When**: Implementa `IConnector`, `IQueryableConnector`, `IStreamingQueryableConnector`, `IConnectorFactory`, `IConnectorDescriptor`.
- [x] **TASK-03**: Criar `src/FSharpQueryStudio.Connectors.MariaDb` com `MariaDbConnector.fs` e `MariaDbDescriptor.fs`.
  - **Requisito**: REQ-CONN-MARIADB-01
  - **Done When**: Implementa `IConnector`, `IQueryableConnector`, `IStreamingQueryableConnector`, `IConnectorFactory`, `IConnectorDescriptor`.
- [x] **TASK-04**: Criar `src/FSharpQueryStudio.Connectors.Oracle` com `OracleConnector.fs` e `OracleDescriptor.fs`.
  - **Requisito**: REQ-CONN-ORACLE-01
  - **Done When**: Implementa `IConnector`, `IQueryableConnector`, `IStreamingQueryableConnector`, `IConnectorFactory`, `IConnectorDescriptor`.
- [x] **TASK-05**: Adicionar os novos projetos na solução `FSharpQueryStudio.slnx` e referências nos projetos dependentes (`FSharpQueryStudio.Connectors`, `FSharpQueryStudio.Worker`).
  - **Requisito**: REQ-CONN-MYSQL-01, REQ-CONN-MARIADB-01, REQ-CONN-ORACLE-01
  - **Done When**: `dotnet build` compila 100% limpo sem erros.

## Milestone 2: Pushdown SQL & Dialetos
- [x] **TASK-06**: Estender `SqlDialect.fs`, `SqlPushdownPlan.fs` e `SqlPushdownQuery.fs` com suporte a `MySql`, `MariaDb` e `Oracle` (aspas/backticks, LIMIT/OFFSET vs OFFSET/FETCH, JSON extraction, CTEs, concatenação e explain).
  - **Requisito**: REQ-DIALECT-01
  - **Done When**: Testes unitários de pushdown cobrem sintaxes específicas dos 3 novos dialetos.
- [x] **TASK-07**: Registrar os 3 novos conectores em `BuiltinConnectors.fs` e `WorkspaceConnectionResolver.fs`.
  - **Requisito**: REQ-CONN-MYSQL-01, REQ-CONN-MARIADB-01, REQ-CONN-ORACLE-01
  - **Done When**: Resolvem conexões dinamicamente no catálogo embutido.

## Milestone 3: Orquestração Docker & Gerador de Demonstrações
- [x] **TASK-08**: Atualizar `AdvancedSampleGenerator.fs` com suporte a `mysql`, `mariadb` e `oracle`:
  - Scripts SQL de inicialização em `init-scripts/` (`01-mysql-init.sql`, `01-mariadb-init.sql`, `01-oracle-init.sql`)
  - Serviços com healthcheck em `docker-compose.fsqs.yml`
  - Registro automático em `.fsqs/connections.json` (`MySQL_Advanced`, `MariaDB_Advanced`, `Oracle_Advanced`)
  - Geração de notebooks bilíngues `sample_mysql.fsqnb` / `sample_mysql_pt.fsqnb`, `sample_mariadb.fsqnb` / `sample_mariadb_pt.fsqnb`, `sample_oracle.fsqnb` / `sample_oracle_pt.fsqnb`.
  - **Requisito**: REQ-DOCKER-DEMO-01
  - **Done When**: A geração cria todos os arquivos e cadernos válidos com sintaxe FSQL correta.

## Milestone 4: Módulos Frontend VS Code
- [x] **TASK-09**: Criar `editors/vscode/src/connections/modules/mysql.ts`, `mariadb.ts`, `oracle.ts` e registrar em `index.ts`.
  - **Requisito**: REQ-FRONTEND-01
  - **Done When**: Conectores aparecem na UI do VS Code com ícone, sanitização de senhas e validação.
- [x] **TASK-10**: Atualizar QuickPick de criação de dados demo em `editors/vscode/src/extension.ts` para oferecer opções de MySQL, MariaDB e Oracle.
  - **Requisito**: REQ-MOD-QUICKPICK-01
  - **Done When**: Seleção aciona a criação dos respectivos containers e cadernos.

## Milestone 5: Testes & Validação Ponta a Ponta
- [x] **TASK-11**: Implementar testes de integração Testcontainers (`MySqlConnectorTests.fs`, `MariaDbConnectorTests.fs`, `OracleConnectorTests.fs`) em `tests/FSharpQueryStudio.Integration.Tests`.
  - **Requisito**: REQ-TEST-01
  - **Done When**: Testes cobrem conexão, descoberta de esquemas, execução de consultas parametrizadas e Kind.
- [x] **TASK-12**: Executar suíte completa de testes (unitários, integração, worker, sample notebooks) e validar localmente.
  - **Requisito**: Regra de Ouro do Repositório (100% testes passando).
  - **Done When**: Todos os testes passam com sucesso.
