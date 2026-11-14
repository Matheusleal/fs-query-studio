# SDD Task Ledger: Advanced Database Containers & Sample Notebooks

- **Change ID**: `advanced-database-containers-and-sample-notebooks`
- **Total Tasks**: 5
- **Completed**: 5
- **In Progress**: 0

---

### TASK-ADV-01: Backend Advanced Sample & Docker Environment Generator
- **Goal**: Implement `AdvancedSampleGenerator` producing standardized seed datasets, Docker Compose orchestration (`docker-compose.fsqs.yml`), SQL initialization scripts (`init-scripts/init_sqlserver.sql`, `init-scripts/init_postgres.sql`), and connection auto-registration.
- **Related Requirements**: REQ-ADB-02, REQ-ADB-03
- **Done When**: Calling `WorkspaceOperations.createAdvancedSamples` generates valid docker-compose configuration, SQL scripts, connection definitions, and SQLite database with standardized tables (`Categories`, `Products`, `Customers`, `Orders`, `OrderItems`).
- **Status**: Completed

---

### TASK-ADV-02: Dialect-Specific and Upgraded Sample Notebook Generators
- **Goal**: Implement generators for `sample_sqlserver.fsqnb`, `sample_postgresql.fsqnb`, and `sample_sqlite.fsqnb`, and upgrade `sample_demo.fsqnb` / `sample_demo_pt.fsqnb` with modern FSQL cells (Window Functions, JSON extraction, CTEs, Set Operations, Explain).
- **Related Requirements**: REQ-ADB-04, REQ-ADB-05
- **Done When**: All generated notebooks execute successfully, showcase new FSQL capabilities, and pass notebook evaluation validation.
- **Status**: Completed

---

### TASK-ADV-03: Worker Protocol & RPC Handlers Integration
- **Goal**: Add `workspace/createAdvancedSamples` method to `Handlers.fs` with payload accepting target directory and selected database list `["sqlite", "postgresql", "sqlserver"]`.
- **Related Requirements**: REQ-ADB-01, REQ-ADB-03
- **Done When**: Worker handles `workspace/createAdvancedSamples` frame, dispatches to generator, and returns generated file and notebook paths.
- **Status**: Completed

---

### TASK-ADV-04: VS Code Extension UI, QuickPick Prompt & Command Integration
- **Goal**: Add `fs-query-studio.createAdvancedSamples` command to `package.json`, `viewsWelcome` ("Create Advanced Database Samples"), view title toolbar, and register interactive multi-select QuickPick in `extension.ts`.
- **Related Requirements**: REQ-ADB-01, REQ-ADB-06
- **Done When**: User can trigger command, select target databases from prompt, see confirmation, and have the generated notebook automatically opened in VS Code.
- **Status**: Completed

---

### TASK-ADV-05: Unit, Integration & E2E Verification
- **Goal**: Add unit tests in `FSharpQueryStudio.Worker.Tests` and `FSharpQueryStudio.Connectors.Tests`, verify `sample_demo.fsqnb` and new sample notebooks, and verify extension build.
- **Related Requirements**: REQ-ADB-01, REQ-ADB-02, REQ-ADB-03, REQ-ADB-04, REQ-ADB-05
- **Done When**: 100% of tests pass locally and in container tests.
- **Status**: Completed
