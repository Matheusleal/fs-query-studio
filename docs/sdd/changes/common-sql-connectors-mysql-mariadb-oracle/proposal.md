# SDD Proposal: Suporte a Conectores SQL Comuns (MySQL, MariaDB, Oracle) & Demos Docker

## 1. Intent Block
```text
Intent Block:
Goal:        Expandir o F# Query Studio com suporte nativo de primeira classe aos principais bancos de dados relacionais do mercado (MySQL, MariaDB e Oracle), mantendo a arquitetura modular de conectores, pushdown de consultas SQL otimizadas com tipagem estrita via `fsql { ... }`, orquestração docker-compose para demos interativas em 1-clique e testes automatizados com Testcontainers.
Expectation: Desenvolvedores e engenheiros de dados poderão conectar-se e consultar instâncias de MySQL, MariaDB e Oracle com descoberta automática de esquemas, navegação semiestruturada em JSON, CTEs, funções de janela e diagnósticos de plano de execução. Além disso, poderão gerar instantaneamente ambientes Docker isolados pré-populados com dados sintéticos padronizados e notebooks interativos bilíngues (`sample_mysql.fsqnb`, `sample_mariadb.fsqnb`, `sample_oracle.fsqnb`).
Action:      Implementar os novos módulos de conector `FSharpQueryStudio.Connectors.MySql`, `FSharpQueryStudio.Connectors.MariaDb` e `FSharpQueryStudio.Connectors.Oracle`; estender a camada de dialetos SQL (`SqlDialect.fs`, `SqlPushdownPlan.fs`, `SqlPushdownQuery.fs`); registrar conectores na UI do VS Code e no `BuiltinConnectors`; atualizar `AdvancedSampleGenerator` com serviços Docker Compose e scripts de inicialização; e implementar suíte de testes de integração via Testcontainers e testes unitários de geração.
Result:      F# Query Studio cobre integralmente os 6 principais bancos relacionais de mercado (SQLite, PostgreSQL, SQL Server, MySQL, MariaDB, Oracle) com consistência estrutural, 100% de paridade de testes e experiência de demonstração plug-and-play.
```

## 2. Escopo & Tecnologias
1. **Motores Suportados**:
   - **MySQL**: ADO.NET provider `MySqlConnector`, container oficial `mysql:8.4`, porta 3306, dialeto com escape em backticks, `JSON_EXTRACT`/`JSON_UNQUOTE`, `LIMIT/OFFSET`, `WITH RECURSIVE`, `LATERAL`.
   - **MariaDB**: ADO.NET provider `MySqlConnector`, container oficial `mariadb:11.4`, porta 3307 (evitando conflito com MySQL), dialeto MariaDB 10.6+ (`JSON_EXTRACT`, `LIMIT/OFFSET`, CTEs, `LATERAL`).
   - **Oracle**: ADO.NET provider `Oracle.ManagedDataAccess.Core`, container oficial `gvenzl/oracle-free:23-slim`, porta 1521, dialeto com escape em aspas duplas/maiúsculas, `OFFSET ... ROWS FETCH NEXT ... ROWS ONLY`, `JSON_VALUE`, `CROSS APPLY` / `OUTER APPLY`, CTEs `WITH`.
2. **Esquema Padronizado**:
   - `Categories` (`Id`, `Name`, `Description`)
   - `Products` (`Id`, `Name`, `CategoryId`, `Price`, `StockQuantity`, `AttributesJson`)
   - `Customers` (`Id`, `Name`, `Email`, `Country`, `Tier`)
   - `Orders` (`Id`, `CustomerId`, `OrderDate`, `TotalAmount`, `Status`)
   - `OrderItems` (`Id`, `OrderId`, `ProductId`, `Quantity`, `UnitPrice`)
3. **Artefatos e Integrações**:
   - Projetos `src/FSharpQueryStudio.Connectors.MySql`, `src/FSharpQueryStudio.Connectors.MariaDb`, `src/FSharpQueryStudio.Connectors.Oracle`.
   - Módulos declarativos frontend: `editors/vscode/src/connections/modules/mysql.ts`, `mariadb.ts`, `oracle.ts`.
   - Scripts de inicialização em `init-scripts/`: `01-mysql-init.sql`, `01-mariadb-init.sql`, `01-oracle-init.sql`.
   - `docker-compose.fsqs.yml` expandido dinamicamente conforme motores selecionados.
   - Notebooks dedicados bilíngues em inglês e português: `sample_mysql.fsqnb`, `sample_mariadb.fsqnb`, `sample_oracle.fsqnb`.
   - Testes de integração com Testcontainers: `MySqlConnectorTests.fs`, `MariaDbConnectorTests.fs`, `OracleConnectorTests.fs`.
