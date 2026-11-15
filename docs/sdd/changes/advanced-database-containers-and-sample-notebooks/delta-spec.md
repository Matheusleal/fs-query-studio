# SDD Delta Spec: Advanced Multi-Database Containers & FSQL Demo Expansion

## 1. ADDED Requirements (GEARS Syntax)

### REQ-ADB-01: User Selection Prompt for Target Databases
```text
When the user triggers the "Create Advanced Database Samples" command in VS Code,
the extension shall display a multi-select QuickPick prompt allowing the user to select one or more supported engines from ["SQLite", "PostgreSQL", "SQL Server"].
```

### REQ-ADB-02: Docker Compose Orchestration Generation
```text
Where the user selected "PostgreSQL" or "SQL Server",
the backend shall generate a runnable "docker-compose.fsqs.yml" in the workspace root containing service definitions, environment variables (passwords, ports), volume mounts, and automated database initialization scripts.
```

### REQ-ADB-03: Automated Connection Registration
```text
When advanced database samples are generated,
the system shall automatically register connection definitions in ".fsqs/connections.json" for each selected database engine with standardized connection names ("SQLite_Advanced", "PostgreSQL_Advanced", "SqlServer_Advanced").
```

### REQ-ADB-04: Dialect-Specific Notebook Generation
```text
When target databases are selected,
the system shall generate corresponding dedicated notebooks ("sample_sqlite.fsqnb", "sample_postgresql.fsqnb", "sample_sqlserver.fsqnb") containing rich bilingual/English Markdown explanations and interactive `fsql { ... }` code cells utilizing:
- Window Functions (`fsql.window`, `rowNumber`, `rank`, `sum` over partition)
- Semi-structured JSON extraction (`fsql.jsonValue`)
- Common Table Expressions (`WithCte` & `fsql.withCte`)
- Set operations (`Union`, `Intersect`, `Except`)
- Outer Joins (`FullOuterJoin`, `RightOuterJoin`)
- Live Query Plan Diagnostics (`explainQuery` and `.Explain()`)
```

### REQ-ADB-05: Standard Demo Notebook Upgrades
```text
When generating default sample notebooks ("sample_demo.fsqnb" and "sample_demo_pt.fsqnb"),
the generator shall include dedicated cells demonstrating the new FSQL analytics features (Window Functions, JSON extraction, and Query Plan Diagnostics) without breaking existing flat-file or REST tests.
```

### REQ-ADB-06: Extension UI Integration
```text
When the extension connections view is rendered with zero connections,
the viewsWelcome menu and the title menu of the Connections view shall include a direct action:
"[Create Advanced Database Samples (Docker & FSQL)](command:fs-query-studio.createAdvancedSamples)".
```

## 2. MODIFIED Requirements
None. Existing `fs-query-studio.createSampleData` remains unchanged and backward-compatible.

## 3. REMOVED Requirements
None.
