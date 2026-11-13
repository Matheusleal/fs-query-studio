# SDD Delta Spec: Backend Code Cleanup, Modularization & Decoupling

## 1. Requirements (GEARS)

### ADDED Requirements

```text
Where the FSharpQueryStudio.Fsac project is built,
the build system shall compile FsacUri.fs and FsacParsing.fs before FsacBridge.fs in the project file.

When resolving document URIs for language server interactions,
the FsacUri module shall provide canonicalization and deterministic synthetic file URI mapping for unsaved and notebook buffers.

When parsing LSP responses from fsautocomplete,
the FsacParsing module shall convert raw JSON elements into protocol domain types for diagnostics, hovers, definitions, signatures, and completions.

Where the FSharpQueryStudio.Connectors project is built,
the build system shall compile SqlDialect.fs, SqlPushdownPlan.fs, and RecordReaders.fs before SqlPushdownQuery.fs and Connections.fs.

When reading relational database records from an IDataRecord,
the RecordReaders module shall provide direct unboxed typed readers and compiled reflection-free record mappers.

When executing workspace filesystem management commands in the Worker,
the WorkspaceOperations module shall handle sample workspace creation, automatic data sources discovery, and empty workspace initialization.
```

### MODIFIED Requirements

```text
When FsacBridge coordinates language analysis,
the FsacBridge module shall delegate URI canonicalization to FsacUri and JSON payload parsing to FsacParsing, maintaining identical public API signatures and behavior.

When documentation and comments are rendered in backend source files,
all source files shall contain concise, business-intent-focused XML docstrings and code comments without historical task references or development diary logs.

When SqlPushdownQuery compiles LINQ expressions into pushdown SQL,
the SqlPushdownQuery module shall use SqlDialect, SqlPushdownPlan, and RecordReaders while maintaining 100% dialect and execution compatibility.

When Handlers dispatches worker requests,
the Handlers module shall delegate workspace mutation workflows to WorkspaceOperations.
```

### REMOVED Requirements

```text
None (all existing features, public interfaces, and protocol payloads remain strictly unchanged).
```

