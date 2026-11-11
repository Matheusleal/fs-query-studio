# Delta Specification: Release v0.5.3

## MODIFIED Requirements

Where extension and worker packages are versioned,
the build metadata shall define version 0.5.3 across manifest configurations and compatibility constants.

Where release documentation is maintained,
the project changelogs and readmes shall document all v0.5.3 enhancements and fixes:
- Relative CSV export path resolution against notebook workspace root with automatic parent directory creation.
- Working directory (`System.Environment.CurrentDirectory`) alignment with active document workspace folder.
- Complete multi-cell file lock elimination on Windows: `PagingCursor` implements `IDisposable`, tabular connectors and sample generators enable `FileShare.Delete`, superseded continuation cursors and unassigned `IDisposable` expressions are actively disposed, and `SqliteConnection.ClearAllPools()` with `GC.WaitForPendingFinalizers()` flushes OS file handles.
- Language service cell analysis prefix optimization: `buildAnalysisInput` excludes cells executed after the target cell (`executionOrder < targetOrder`), preventing duplicate definition warnings (`FS0037`) when navigating or re-evaluating cells.
- Interactive demo file pipeline cases: Sections 15 & 16 in `sample_demo.fsqnb` and `sample_demo_pt.fsqnb` illustrating file creation, streaming, disposal, and lock-free multi-cell re-reading/appending without redundant parentheses.
- Result retention URI normalization supporting unescaped and percent-encoded drive letters.
- SQLite connector connection pooling disabled (`Pooling=false`) and instant cleanup on 0-column non-query commands.
- Upfront FSI session assembly referencing via CLI arguments (`-r:`) to eliminate runtime `TcImports` lock-inversion deadlocks.

