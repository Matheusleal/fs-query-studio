# Task Ledger: Local Workspace Project Support & Connection Scope

## Tasks

- [x] **TASK-01: Update Protocol & Worker `connection/register` to support `workspaceDir`**
  - **Goal**: Allow `connection/register` to target a local project directory instead of always writing to global `%LOCALAPPDATA%`.
  - **Related Requirements**: FR-CONN-REG, FR-PROJ-03
  - **Done When**: Worker deserializes optional `workspaceDir` in `handleConnectionRegister`, writes to that directory if present, and triggers background generation for that directory. Unit tests validate local registration.
  - **Status**: Completed

- [x] **TASK-02: Implement Command `fs-query-studio.initEmptyProject` in VS Code Extension**
  - **Goal**: Provide a clean command to initialize an empty project with `workspace.json` and a starter `notebook.fsqnb`.
  - **Related Requirements**: FR-PROJ-01
  - **Done When**: Command is contributed in `package.json`, implemented in `extension.ts`, and verified by tests.
  - **Status**: Completed

- [x] **TASK-03: Add Scope Selection Prompt (Local vs Global) to `connections/add.ts`**
  - **Goal**: Prompt the user to select Local or Global when adding a connection if a workspace folder/project is open.
  - **Related Requirements**: FR-PROJ-02, FR-PROJ-03
  - **Done When**: When adding a connection with open project, prompt asks "Local" or "Global"; passes `workspaceDir` to worker when Local is selected.
  - **Status**: Completed

- [x] **TASK-04: Test Suite & End-to-End Validation**
  - **Goal**: Ensure 100% test pass on Vitest and .NET suite.
  - **Related Requirements**: FR-PROJ-01, FR-PROJ-02, FR-PROJ-03, FR-CONN-REG
  - **Done When**: Vitest runs all tests passing, `dotnet test` runs all tests passing.
  - **Status**: Completed
