# Delta Specification: Fix Table Header Scroll, Eager Excel Tree/Mapping Sync & Option Property Query Translation

## ADDED / MODIFIED Requirements (GEARS)

### [REQ-UI-TABLE-SCROLL-001] Table Header Horizontal Scroll Synchronization
When a table payload contains columns that exceed the viewport width,
the notebook grid renderer shall place the `.fsq-head` element inside `.fsq-viewport` with sticky `top: 0` positioning and `width: max-content; min-width: 100%`, ensuring that the header scrolls horizontally in lockstep with table data rows and remains pinned at the top when scrolling vertically.

### [REQ-EXCEL-EAGER-SYNC-001] Eager Entity Mapping on Data Sources Sync
When `workspace/syncDataSources` executes in the worker,
the sync handler shall trigger `EntityBackgroundGenerator.triggerBackgroundGeneration` for all registered connections, ensuring strongly-typed F# entity accessors are generated immediately upon workspace startup and synchronization without requiring manual expansion in the connections tree view.

### [REQ-EXCEL-TREE-PREFETCH-002] Eager Tree Schema Pre-fetching
When the connections tree provider lists workspace connections,
the tree provider shall proactively pre-fetch and populate `schemaCache` in the background for all uncached connections, ensuring expanding an Excel connection or table in the tree renders instantaneously from cache.

### [REQ-EXCEL-GENERATOR-PERF-003] Optimized Excel Entity Generation
When `ExcelEntityGenerator` generates strongly-typed module code for a workbook connection,
the generator shall reuse column definitions and inferred data types from `DiscoverSchemaAsync` rather than re-reading the entire spreadsheet file via `ReadRows` for every worksheet.

### [REQ-PUSHDOWN-OPTION-UNWRAP-001] Option Property .Value Resolution
When a LINQ expression accesses `.Value` or `.get_Value()` on a member of type `option<'T>`, `voption<'T>`, or `Nullable<'T>`,
the SQL pushdown query translator shall unwrap the member access to the underlying column property expression, resolving the SQL column name to the database column instead of `"Value"`.

### [REQ-PUSHDOWN-OPTION-PREDICATES-002] Option and Nullable Predicate Translation
When a LINQ `where` predicate evaluates `.IsSome`, `.IsNone`, `.HasValue`, `Option.isSome`, or `Option.isNone` on an option-typed column, or compares an option-typed column against `None` or `Some value`,
the SQL pushdown query translator shall emit `IS NOT NULL`, `IS NULL`, or parameterized column equality clauses targeting the underlying column name.

