# Delta Specification: Memory and Autocomplete Performance Optimizations

## ADDED Requirements

Where the worker process is compiled or spawned,
the runtime configuration shall enforce Workstation Garbage Collection (ServerGarbageCollection=false) and Concurrent Garbage Collection (ConcurrentGarbageCollection=true) to minimize memory heap footprint.

Where the worker process launches the FsacProcess child process,
the worker process shall configure child process environment variables with DOTNET_GCServer=0 and DOTNET_gcConcurrent=1 to ensure the language service operates in Workstation GC mode.

While language analysis requests (completion, hover, definition, diagnostics check) are processed,
the analysis handler shall verify entity readiness using in-memory cached file modification timestamps (LastWriteTimeUtc), bypassing disk read and JSON deserialization when workspace and generated entity files remain unchanged.

While a document version increases during text analysis,
the language service bridge shall prune historical diagnostic entries from memory for all older versions of that document.

Where query output or evaluation diagnostics are buffered in memory,
the evaluation pipeline shall reset and trim StringBuilder capacity to at most 4096 characters when execution completes.

Where connection name completions are requested inside connection argument strings,
the completion provider shall cache registered connection definitions for active completion sessions (2000ms TTL), preventing redundant worker IPC roundtrips on consecutive keystrokes.

## MODIFIED Requirements

While appending cell fragments to an active workspace session in notebook mode,
the session store shall retain only the most recent fragment instead of growing an unbounded historical list of previous fragment strings.

While refreshing generated entities within an evaluation session,
the session manager shall compare file modification timestamps before reading generated entity file contents from disk.
