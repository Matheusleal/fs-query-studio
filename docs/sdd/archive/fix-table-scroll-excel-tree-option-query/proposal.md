# Change Proposal: Fix Table Header Scroll, Eager Excel Tree/Mapping Sync & Option Property Query Translation

## 1. Context & Problem Statement
Users identified three critical bugs in FS Query Studio:
1. **Table Header Horizontal Scroll Desynchronization**:
   - In notebook query output grids with many columns (e.g. 10+ columns), horizontal scrolling in `.fsq-viewport` does not move the column headers because `.fsq-head` was rendered outside `.fsq-viewport` inside `.fsq-panel`. The headers remained fixed while the data rows scrolled horizontally underneath.
2. **Lazy Loading of Excel Data and Mappings in Connections Tree**:
   - Excel workbook connections (`.xlsx`) were relying on lazy loading when the connection was expanded in the tree view to discover worksheets and trigger `EntityBackgroundGenerator.triggerBackgroundGeneration`. If the user did not expand the connection node in the tree, strongly-typed F# entity accessors were not generated on workspace startup or data sync. Furthermore, expanding the tree had to wait for multiple ClosedXML workbook file re-opens.
3. **Option Property Translation Bug in LINQ-to-SQL Pushdown**:
   - When a row property is typed as an `option` (e.g. `x.Coluna: string option`), F# queries require accessing `x.Coluna.Value` (e.g. `where (x.Coluna.Value = "valor")`). The LINQ expression translator in `SqlPushdownQuery.fs` treated `m.Member.Name` ("Value") as the database column name, generating `SELECT ... FROM table WHERE Value = "valor"` instead of `WHERE Coluna = "valor"`.

## 2. Intent Block
```text
Intent Block:
Goal:        Fix table header horizontal scroll synchronization, eliminate lazy loading for Excel data/mappings in the connections tree, and resolve option/nullable .Value property unwrapping in SQL pushdown translation.
Expectation: Table headers in the notebook grid follow horizontal scroll smoothly and stick vertically at top: 0; Excel connections have their schema pre-fetched and entity mappings generated eagerly during startup/sync; and queries using `x.Coluna.Value` or Option checks translate to the correct column name `Coluna` in SQL.
Action:      (1) Move `.fsq-head` inside `.fsq-viewport` with sticky top: 0 and width: max-content in `editors/vscode/src/renderer/index.ts`; (2) Trigger entity background generation during `workspace/syncDataSources`, pre-fetch connection schemas in `tree.ts`, and optimize `ExcelEntityGenerator` to use cached schema columns; (3) Implement `unwrapOptionOrNullableMember` and Option predicate translation in `SqlPushdownQuery.fs` so `x.Coluna.Value` resolves to `Coluna`.
Result:      Seamless horizontal table scrolling, instant Excel tree navigation and eager F# entity generation, and 100% accurate SQL translation for option-typed properties.
```

