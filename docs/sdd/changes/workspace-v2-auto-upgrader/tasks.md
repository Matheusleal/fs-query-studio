# Task Ledger: Project Auto-Upgrader & .fsqs Workspace Schema v2

## Tasks

- [x] **TASK-01: Implement Workspace Schema v2 & Backward-Compatible Deserializer in Storage**
  - **Goal**: Define `WorkspaceConfiguration` record with `schemaVersion`, `extensionVersion`, and `connections`. Update `Workspace.fs` to read both legacy array (v1) and object (v2), and always write Schema v2.
  - **Related Requirements**: FR-WS-SCHEMA-V2, FR-WS-DESERIALIZE-COMPAT, FR-STORAGE-FSQS
  - **Done When**: `Workspace.readConnections` parses both v1 and v2 files without errors; `Workspace.writeConnections` outputs valid Schema v2 JSON; `resolveStorageDir` targets `<project>/.fsqs`.
  - **Status**: Completed

- [x] **TASK-02: Implement ProjectUpgrader Module with Extensible Migration Pipeline**
  - **Goal**: Create `ProjectUpgrader.fs` with `IMigrationStep` abstraction, implementing Step 1 -> 2 (structural move of `workspace.json` and `generated/` into `.fsqs/`, plus JSON schema v1 -> v2 upgrade).
  - **Related Requirements**: FR-UPGRADE-STRUCTURAL, FR-UPGRADE-SCHEMA, FR-UPGRADE-EXTENSIBLE-PIPELINE
  - **Done When**: Invoking `ProjectUpgrader.upgradeWorkspace` on a legacy project root moves legacy files into `.fsqs/`, updates `workspace.json` to Schema v2, and cleans up the root directory.
  - **Status**: Completed

- [x] **TASK-03: Integrate Automatic Upgrade into Workspace Access & Worker Dispatch**
  - **Goal**: Hook automatic upgrader execution into `Workspace` file operations and add `workspace/upgrade` worker RPC method; update `workspace/initEmptyProject` to generate Schema v2.
  - **Related Requirements**: FR-UPGRADE-WORKER-RPC, FR-PROJ-INIT-FSQS
  - **Done When**: Any read/write to a workspace triggers automatic upgrade if needed; `workspace/upgrade` returns an `UpgradeReport`; `workspace/initEmptyProject` outputs Schema v2.
  - **Status**: Completed

- [x] **TASK-04: Update VS Code Extension Integration & File Watchers**
  - **Goal**: Update extension commands and watchers to support the upgraded schema and log upgrade events when detected.
  - **Related Requirements**: FR-UPGRADE-WORKER-RPC, FR-WS-SCHEMA-V2
  - **Done When**: VS Code extension correctly reflects connections, triggers refresh, logs upgrade operations if applicable, and all integration tests pass.
  - **Status**: Completed

- [x] **TASK-05: Comprehensive Unit & Integration Test Suite**
  - **Goal**: Add complete tests for v1-to-v2 migration, structural file movement, clean up of root, round-tripping, and regression verification across all test suites (.NET and Vitest).
  - **Related Requirements**: FR-WS-SCHEMA-V2, FR-WS-DESERIALIZE-COMPAT, FR-UPGRADE-STRUCTURAL, FR-UPGRADE-SCHEMA, FR-UPGRADE-EXTENSIBLE-PIPELINE, FR-UPGRADE-WORKER-RPC, FR-PROJ-INIT-FSQS
  - **Done When**: All Storage tests, Handlers tests, and Vitest tests pass with 100% success rate.
  - **Status**: Completed
