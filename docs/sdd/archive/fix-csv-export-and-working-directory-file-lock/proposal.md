# Change Proposal: Fix CSV Export Path Resolution and Working Directory File Access

## 1. Context & Problem Statement
When working with F# Query Studio notebooks (`.fsqnb`):
1. **CSV Export Failure**: When clicking **Export CSV** in the results table, an error occurs or the file is not generated in the current workspace directory. Specifically:
   - In VS Code, `pickSaveFile` builds `defaultUri` using `vscode.Uri.joinPath(vscode.Uri.parse(documentUri), "..", defaultName)`. When `documentUri` has an `untitled:` scheme (or non-file scheme), `showSaveDialog` fails because `defaultUri` has an invalid scheme.
   - When a relative path is passed to `results/exportCsv`, the worker writes relative to `Directory.GetCurrentDirectory()` (which was pointing to temp or the worker binary folder), instead of resolving relative to the document's workspace directory.
   - `ResultRetention.tryGet` keys can fail to match when the caller passes a raw URL-encoded URI (e.g. `file:///d%3A/...` on Windows) versus an unescaped URI.
2. **Current Directory & File Locking**: When creating a file via user code in an F# notebook cell (e.g. `let stream = System.IO.File.Create(...)` followed by `stream.Dispose()`):
   - `System.Environment.CurrentDirectory` returned the worker's binary path or a transient execution directory in `%TEMP%`, rather than the notebook's containing workspace directory.
   - Files created with relative paths were created in the transient temp directory, and when evaluation finished, `finally` called `Directory.Delete(workDir, true)`. On Windows, recent file creation/disposal or background indexers caused `Directory.Delete` to fail, leaving files trapped in temp or locked.
   - Files created in the notebook/workspace folder could also remain locked if connections (such as SQLite) did not disable connection pooling (`Pooling = false`).

## 2. Intent Block
```text
Intent Block:
Goal:        Fix CSV export path resolution and ensure user-created files and System.Environment.CurrentDirectory strictly resolve to the notebook's workspace directory without file locks.
Expectation: Exporting CSV results creates the CSV file in the current workspace directory without error, System.Environment.CurrentDirectory matches the notebook/workspace directory, and files created via System.IO.File.Create are released immediately upon stream.Dispose().
Action:      (1) Fix pickSaveFile in extension.ts to support untitled and non-file schemes with fallback to workspace folder; (2) In Handlers.handleResultsExportCsv and ResultRetention, normalize URIs, resolve relative export paths against the workspace directory, and auto-create missing directories; (3) In Handlers.runEvaluate, bind working directory to targetWs so Environment.CurrentDirectory reflects the workspace/notebook folder; (4) Ensure SqliteConnector disables pooling; (5) Add comprehensive unit and E2E integration tests.
Result:      Reliable CSV export across all notebook states, correct working directory in FSI sessions, and safe file lifecycle without locks.
```

