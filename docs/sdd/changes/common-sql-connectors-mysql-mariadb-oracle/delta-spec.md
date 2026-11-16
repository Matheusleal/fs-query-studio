# SDD Delta Spec: Suporte a Conectores SQL Comuns (MySQL, MariaDB, Oracle) & Demos Docker

## 1. ADDED Requirements (GEARS Syntax)

### REQ-CONN-MYSQL-01: MySQL Connector Implementation
```text
When a MySQL connection definition is registered,
the MySQL connector shall establish connection using MySqlConnector, bind parameters as MySqlParameter instances, discover schema through information_schema, and execute queries returning streamable results.
```

### REQ-CONN-MARIADB-01: MariaDB Connector Implementation
```text
When a MariaDB connection definition is registered,
the MariaDB connector shall establish connection using MySqlConnector, bind parameters safely, discover schema through information_schema, and execute queries returning streamable results.
```

### REQ-CONN-ORACLE-01: Oracle Connector Implementation
```text
When an Oracle connection definition is registered,
the Oracle connector shall establish connection using Oracle.ManagedDataAccess.Core, bind parameters as OracleParameter instances, discover user tables and columns through all_tables/all_tab_columns, and execute queries returning streamable results.
```

### REQ-DIALECT-01: Expanded SqlDialect Pushdown Generation
```text
Where a pushdown query targets MySQL, MariaDB, or Oracle,
the SQL generation engine shall produce dialect-accurate syntax for identifier quoting, LIMIT/OFFSET vs OFFSET/FETCH, JSON extraction, string concatenation, CTE clauses, and explain diagnostics.
```

### REQ-FRONTEND-01: Frontend Connector Modules Registration
```text
When the connection management panel in VS Code is displayed,
the UI shall provide dedicated connection forms and credential sanitizers for MySQL, MariaDB, and Oracle in ConnectorFrontRegistry.
```

### REQ-DOCKER-DEMO-01: Multi-Engine Docker Compose and Init Scripts Generation
```text
Where the user selects MySQL, MariaDB, or Oracle in the sample creation dialog,
the backend generator shall generate valid service definitions in docker-compose.fsqs.yml, create engine-specific seeding scripts in init-scripts/, register workspace connections in .fsqs/connections.json, and generate corresponding interactive FSQL notebooks.
```

### REQ-TEST-01: Testcontainers Integration Parity
```text
Where Docker is available,
the integration test suite shall verify connection testing, schema discovery, parameterized queries, and FSQL execution against real containerized instances of MySQL, MariaDB, and Oracle.
```

## 2. MODIFIED Requirements

### REQ-MOD-QUICKPICK-01: Engine Selection Expansion
```text
When the user triggers "Create Advanced Database Samples",
the extension shall include "MySQL Advanced Demo", "MariaDB Advanced Demo", and "Oracle Advanced Demo" alongside existing PostgreSQL, SQL Server, and SQLite options.
```

## 3. REMOVED Requirements
None.
