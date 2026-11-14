# SDD Proposal: Advanced Multi-Database Containers & FSQL Demo Expansion

## 1. Intent Block
```text
Intent Block:
Goal:        Provide a 1-click experience in F# Query Studio to provision containerized sample environments (SQL Server, PostgreSQL, SQLite) with standardized enterprise schemas, docker-compose orchestration, automated connection registration, and dedicated showcase notebooks utilizing all cutting-edge FSQL features.
Expectation: When developers click "Create Advanced Database Samples" in VS Code, they are prompted via multi-select QuickPick to choose their target database engines (SQL Server, PostgreSQL, SQLite). The extension generates ready-to-run container configurations, seeds identical normalized schemas and records, registers workspace connections, and produces targeted interactive notebooks (.fsqnb) demonstrating the full spectrum of FSQL DQL (window functions, JSON navigation, CTEs, set operations, outer joins, explain diagnostics), alongside an upgraded universal sample_demo.
Action:      Implement backend workspace/createAdvancedSamples handler, sample database seeder scripts, docker-compose generator, dialect-specific notebook generators, VS Code menu entries (welcome view and view/title), and update default sample_demo notebooks with new FSQL capabilities.
Result:      Developers can instantly spin up real multi-database topologies locally with Docker, explore dialect-specific FSQL queries with 100% type safety and pushdown, and immediately evaluate SQL Server, PostgreSQL, and SQLite side-by-side.
```

## 2. Scope & Target Databases
1. **Engines Supported**:
   - **SQLite**: Local file-based, zero Docker prerequisites, updated with all SQLite 3.25+ FSQL features.
   - **PostgreSQL**: Official container (`postgres:16-alpine`), port 5432, schema `public` + `dbo`, full JSONB/LATERAL/RECURSIVE CTE pushdown.
   - **SQL Server (MSSQL)**: Official container (`mcr.microsoft.com/mssql/server:2022-latest`), port 1433, full T-SQL Window/JSON_VALUE/SHOWPLAN pushdown.
2. **Standardized Schema**:
   - `Categories`
   - `Products` (with semi-structured JSON attributes/specs column)
   - `Customers`
   - `Orders`
   - `OrderItems`
3. **Artifacts Generated**:
   - `docker-compose.fsqs.yml` (multi-container orchestration with health checks)
   - `init-scripts/` (SQL Server and PostgreSQL automated seed scripts)
   - `.fsqs/connections.json` (auto-configured connections for selected databases)
   - Dedicated notebooks:
     - `sample_demo.fsqnb` & `sample_demo_pt.fsqnb` (updated with FSQL window, JSON, CTE, and explain cells)
     - `sample_sqlserver.fsqnb` (when SQL Server selected)
     - `sample_postgresql.fsqnb` (when PostgreSQL selected)
     - `sample_sqlite.fsqnb` (when SQLite selected)

## 3. GEARS Requirements Overview
- Where Docker is available, when user triggers advanced samples creation, the system shall produce a valid docker-compose configuration.
- When generating dialect notebooks, the system shall use `fsql { ... }` computation expressions exercising dialect-compatible features.
