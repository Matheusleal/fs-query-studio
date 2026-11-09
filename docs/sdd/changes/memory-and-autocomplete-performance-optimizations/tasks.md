# Implementation Tasks: Memory and Autocomplete Performance Optimizations

- [x] TASK-401: Configure Workstation GC and environment variables in `FSharpQueryStudio.Worker.fsproj`, `FsacProcess.fs`, and `spawn.ts`
- [x] TASK-402: Implement timestamp-based caching for entity readiness and generated entities refresh (`Handlers.fs`, `EntityBackgroundGenerator.fs`, `FsiSession.fs`)
- [x] TASK-403: Prune stale document version diagnostics in `FsacBridge.fs`
- [x] TASK-404: Prevent memory retention in `SessionStore.fs` and trim `StringBuilder` capacity in `Evaluation.fs`
- [x] TASK-405: Add connection list caching in `connection-name-completion.ts` and cancellation check in `providers.ts`
- [x] TASK-406: Add tests for memory/performance optimizations (F# unit tests & TypeScript tests)
- [x] TASK-407: Run full test suite locally and in Linux container (`run-all-tests.ps1`)
- [x] TASK-408: Build extension and sync artifacts to `~/.vscode/extensions/...`
- [x] TASK-409: Commit, push branch, and create Pull Request
