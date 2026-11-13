# SDD Tasks: Backend Code Cleanup, Modularization & Decoupling

## Milestone 1: FSharpQueryStudio.Fsac Modularization & Comment Cleanup
- [x] TASK-101: Extract `FsacUri.fs` from `FsacBridge.fs` and update `FSharpQueryStudio.Fsac.fsproj`.
- [x] TASK-102: Extract `FsacParsing.fs` from `FsacBridge.fs` with all response mapping helpers and update `FSharpQueryStudio.Fsac.fsproj`.
- [x] TASK-103: Refactor `FsacBridge.fs` to delegate to `FsacUri` and `FsacParsing`, cleaning up verbose diary comments and preserving the public API.
- [x] TASK-104: Validate `FSharpQueryStudio.Fsac.Tests` against refactored FSAC modules.

## Milestone 2: FSharpQueryStudio.Connectors Modularization & Comment Cleanup
- [x] TASK-201: Extract `SqlDialect.fs` and `SqlPushdownPlan.fs` from `SqlPushdownQuery.fs`.
- [x] TASK-202: Extract `RecordReaders.fs` (including `CompiledRecordMapper`) from `SqlPushdownQuery.fs`.
- [x] TASK-203: Refactor `SqlPushdownQuery.fs` to focus on LINQ AST translation and `IQueryable` implementation, trimming diary comments.
- [x] TASK-204: Clean up repetitive diary comments and redundant conversions in `Connections.fs`.
- [x] TASK-205: Update `FSharpQueryStudio.Connectors.fsproj` and validate `FSharpQueryStudio.Connectors.Tests`.

## Milestone 3: FSharpQueryStudio.Worker Modularization & Comment Cleanup
- [x] TASK-301: Extract `WorkspaceOperations.fs` from `Handlers.fs` to encapsulate sample creation, auto-import, and empty workspace setup.
- [x] TASK-302: Refactor `Handlers.fs` to delegate workspace operations and clean up verbose comments.
- [x] TASK-303: Update `FSharpQueryStudio.Worker.fsproj` and validate `FSharpQueryStudio.Worker.Tests`.

## Milestone 4: Comprehensive Verification & Regression Gate
- [x] TASK-401: Run full test suite across the entire solution (`dotnet test`).
- [x] TASK-402: Run `sample_demo` integration validation tests.
