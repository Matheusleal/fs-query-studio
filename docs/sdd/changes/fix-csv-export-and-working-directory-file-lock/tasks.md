# Tasks Ledger: Fix CSV Export Path Resolution and Working Directory File Access

- [x] TASK-401: In `src/FSharpQueryStudio.Results/ResultRetention.fs`, support normalized/unescaped URI lookup and case-insensitivity.
- [x] TASK-402: In `src/FSharpQueryStudio.Worker/Handlers.fs`, resolve relative export paths against `targetWs` and auto-create parent directories in `handleResultsExportCsv`.
- [x] TASK-403: In `src/FSharpQueryStudio.Worker/Handlers.fs`, set `effectiveWorkDir = targetWs` in `runEvaluate` and update `finally` block to preserve `targetWs` as `CurrentDirectory`.
- [x] TASK-404: In `src/FSharpQueryStudio.Connectors.Sqlite/SqliteConnector.fs`, set `Pooling = false` on `SqliteConnectionStringBuilder`.
- [x] TASK-405: In `editors/vscode/src/extension.ts`, update `pickSaveFile` to safely handle untitled/non-file `documentUri` by defaulting to workspace root.
- [x] TASK-406: In `editors/vscode/src/worker/spawn.ts` and `session.ts`, allow passing `cwd` so worker spawns in the workspace folder.
- [x] TASK-407: Add unit tests in `FSharpQueryStudio.Worker.Tests` covering `System.Environment.CurrentDirectory`, file creation with `System.IO.File.Create`, immediate disposal & deletion, and CSV export with relative/unescaped paths.
- [x] TASK-408: Add unit and E2E integration tests in `editors/vscode` covering CSV export and notebook file lifecycle.
- [x] TASK-409: Run all tests locally and in Linux container (`docker run`), validating `sample_demo.fsqnb` and `sample_demo_pt.fsqnb`.

