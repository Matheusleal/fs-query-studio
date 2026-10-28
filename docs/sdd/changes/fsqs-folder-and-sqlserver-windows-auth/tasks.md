# Task Ledger: .fsqs Internal Storage Folder & SQL Server Windows Authentication

## Tasks

- [x] **TASK-01: Update .NET Storage, Generator, Execution & Handlers for `.fsqs` folder**
  - **Goal**: Persist `workspace.json` and `generated/GeneratedEntities.fs` inside `<root>/.fsqs/` while maintaining read compatibility for legacy locations.
  - **Related Requirements**: FR-STORAGE-FSQS, FR-STORAGE-COMPAT, FR-PROJ-INIT-FSQS
  - **Done When**: `Workspace.fs` writes to `.fsqs/workspace.json`, `EntityBackgroundGenerator.fs` writes to `.fsqs/generated/`, `FsiSession.fs` and `Prologue.fs` load from `.fsqs/generated/`, and `Handlers.fs` initializes `.fsqs/`.
  - **Status**: Completed

- [x] **TASK-02: Update VS Code Extension commands & tests for `.fsqs`**
  - **Goal**: Align extension project initialization and references with the `.fsqs` directory structure.
  - **Related Requirements**: FR-STORAGE-FSQS, FR-PROJ-INIT-FSQS
  - **Done When**: `extension.ts` descriptions and integration tests check `<projectRoot>/.fsqs/workspace.json` and `<projectRoot>/.fsqs/generated/`.
  - **Status**: Completed

- [x] **TASK-03: Implement SQL Server Windows Authentication in UI & Definition**
  - **Goal**: Add UI option to select Windows Authentication (Integrated Security) vs SQL Server Authentication, configure connection string, and omit password.
  - **Related Requirements**: FR-SQL-WINAUTH, FR-SQL-WINAUTH-NO-SECRET
  - **Done When**: When adding/editing a SQL Server connection, user can pick Windows Auth, receives an Integrated Security connection string, is not asked for a password, and no secret is stored.
  - **Status**: Completed

- [x] **TASK-04: Full Test Suite Validation & Regression Testing**
  - **Goal**: Ensure 100% test pass on .NET and Vitest test suites.
  - **Related Requirements**: FR-STORAGE-FSQS, FR-STORAGE-COMPAT, FR-PROJ-INIT-FSQS, FR-SQL-WINAUTH, FR-SQL-WINAUTH-NO-SECRET
  - **Done When**: All Vitest tests and `dotnet test` tests pass with 0 failures.
  - **Status**: Completed
