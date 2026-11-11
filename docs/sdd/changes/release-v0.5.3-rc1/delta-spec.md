# Delta Specification: Release Candidate v0.5.3-rc1

## MODIFIED Requirements

Where extension and worker packages are versioned,
the build metadata shall define version 0.5.3 across manifest configurations and compatibility constants.

Where release documentation is maintained,
the project changelogs and readmes shall document all v0.5.3-rc1 enhancements and fixes:
- Relative CSV export path resolution against notebook workspace root with automatic parent directory creation.
- Working directory (`System.Environment.CurrentDirectory`) alignment with active document workspace folder.
- File lock prevention on user-created files (`System.IO.File.Create` / `.Dispose()`) in persistent FSI sessions by preserving worker process CWD.
- File lock elimination across cell evaluations: `PagingCursor` implements `IDisposable`, tabular connectors and sample generators enable `FileShare.Delete`, superseded continuation cursors and expression results are eagerly disposed, and `SqliteConnection.ClearAllPools()` with `GC.WaitForPendingFinalizers()` flushes OS file handles.
- Result retention URI normalization supporting unescaped and percent-encoded drive letters.
- SQLite connector connection pooling disabled (`Pooling=false`) and instant cleanup on 0-column non-query commands.
- Upfront FSI session assembly referencing via CLI arguments (`-r:`) to eliminate runtime `TcImports` lock-inversion deadlocks.

