# Proposal: Memory and Autocomplete Performance Optimizations

## Problem Statement
During typical usage of F# Query Studio (especially with large datasets, long-running interactive sessions, or active editing in notebooks), memory consumption grows and responsiveness during autocomplete and language analysis can experience friction:
1. **Worker Process GC Overhead**: Worker processes inherit default .NET Server GC on multi-core workstations, allocating multi-heap memory segments per CPU core and retaining tens/hundreds of megabytes of idle heap.
2. **FsAutoComplete (FSAC) Memory & Cache Accumulation**: FSAC runs as a sub-process without workstation GC constraints, and `FsacBridge` accumulates historical diagnostics across every version number without pruning stale document versions.
3. **Repeated Synchronous Disk I/O & Parsing on Keystroke**: On every autocomplete keystroke, hover, definition, and check request, `ensureEntitiesReadyForDocument` repeatedly reads `workspace.json` and `GeneratedEntities.fs` from disk using synchronous I/O and string operations (`File.ReadAllText`), even when neither file has changed.
4. **Session History and Buffer Retention**: `SessionStore` continuously accumulates past executed fragments in an unbounded list (`LastFragments @ [ fragment ]`), and `Evaluation` reuses `StringBuilder` instances without capping large accumulated capacities from large query outputs.
5. **Connection Autocomplete IPC Churn**: Keystrokes inside `Connections.xxx("...")` repeatedly query the worker process over IPC for the connection list on every character typed without caching.

## Proposed Changes
1. **Worker & FSAC Process Memory Tuning**:
   - Configure Workstation GC (`<ServerGarbageCollection>false</ServerGarbageCollection>`, `<ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>`, `<TieredPGO>true</TieredPGO>`) in `FSharpQueryStudio.Worker.fsproj`.
   - Pass `DOTNET_GCServer=0`, `DOTNET_gcConcurrent=1`, and `DOTNET_TieredPGO=1` when spawning Worker processes from VS Code and when spawning FSAC from the worker.
2. **Analysis & Autocomplete Hot-Path Caching**:
   - Implement fast metadata-based caching (checking `LastWriteTimeUtc`) in `Handlers.fs` / `EntityBackgroundGenerator.fs` to bypass `workspace.json` reading and `GeneratedEntities.fs` text scanning when files have not changed.
   - Cache `refreshGeneratedEntities` in `FsiSession.fs` based on file write time rather than re-reading the entire file string on every cell run.
3. **FSAC Diagnostics Pruning**:
   - In `FsacBridge.fs`, prune diagnostics for older versions when newer versions of a document arrive.
4. **Buffer & Session Footprint Optimization**:
   - Bound `LastFragments` in `SessionStore.fs` to prevent unbounded string accumulation in notebook mode.
   - Trim oversized `StringBuilder` capacities in `Evaluation.fs` (cap to 4KB).
   - In `connection-name-completion.ts`, add a short-lived cache (2s TTL) for connection items during active completion to eliminate repeated worker IPC calls.

## Expected Outcomes
- Significant reduction in baseline memory footprint per worker process (~50-70% lower idle/working set).
- Near-zero disk I/O on keystrokes during code editing and autocomplete.
- Snappier autocomplete responses and lower CPU/GC pause times.
- 100% preservation of reliability, correctness, and test suite pass rate.
