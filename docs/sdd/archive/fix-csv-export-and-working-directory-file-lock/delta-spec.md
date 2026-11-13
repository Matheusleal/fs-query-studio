# Delta Specification: Fix CSV Export Path Resolution and Working Directory File Access

## 1. System Requirements (GEARS)

### ADDED Requirements

```text
When the user requests CSV export from a notebook results table,
the VS Code extension shall resolve the default save URI to the notebook's parent folder when the document has a file scheme, or to the workspace root folder when the document has an untitled or non-file scheme.

When results/exportCsv is received with a relative target file path,
the worker shall resolve the file path relative to the document's resolved workspace directory and create any non-existent destination directories before writing.

When ResultRetention looks up a retained result by DocumentUri,
the worker shall match both the raw URI and the unescaped normalized URI case-insensitively, guaranteeing retention lookup succeeds across URL-encoded Windows file URIs.

When an F# notebook cell is evaluated in the worker,
the worker shall set the process current working directory (System.Environment.CurrentDirectory) to the document's resolved workspace directory, ensuring user code relative paths resolve to the workspace.

When user code creates and disposes a file stream via System.IO.File.Create,
the runtime and connector infrastructure shall not retain locks on the created file, permitting immediate subsequent deletion, reading, or renaming.
```

### MODIFIED Requirements

```text
Where the worker manages the working directory during cell evaluation,
the worker shall set the session working directory to the document's resolved workspace directory rather than a transient temp directory, and preserve this working directory in the evaluation finally block.

Where SqliteConnector opens SQLite connections,
the connector shall configure connection pooling to false (Pooling = false), preventing connection pool locks on underlying SQLite database files on Windows.
```

### REMOVED Requirements
None.

