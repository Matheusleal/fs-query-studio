# Delta Specification: Release v0.8.0

## Requirement Delta

### ADDED

Where the build and release pipelines package the extension,
the build metadata shall define version 0.8.0 across manifest configurations and compatibility constants.

Where the documentation files reference current release capabilities,
the project changelogs and readmes shall document all v0.8.0 enhancements and additions:
- Interactive Raw Query Tab in notebook cell results with SQL, parameters, and one-click copy.
- Dedicated `isNotNull`, `isNotEmpty`, and `isNotNullOrEmpty` clauses with pushdown.
- Multi-database server discovery and hierarchical tree mapping (`Connection -> Database -> [Schema ->] Table -> Column`).
- Direct SQLite engine queries on mapped CSV, XML, JSON & Excel connections.
- Architecture decoupling: single-stream SQLite writer, async secret storage, and modular frontend components.
- Graphify knowledge graph integration with `.agents/rules/graphify.md`, `.agents/workflows/graphify.md`, and local ignore rules in `.gitignore`.
- Strict 100% test coverage enforcement across all 68 classes.

### MODIFIED

In `editors/vscode/package.json`:
- Version bumped from `0.7.0` to `0.8.0`.

In `editors/vscode/package-lock.json`:
- Version bumped from `0.7.0` to `0.8.0`.

In `src/FSharpQueryStudio.Storage/ProjectUpgrader.fs`:
- Default fallback extension version bumped to `0.8.0`.

In `tests/FSharpQueryStudio.Storage.Tests/Tests.fs`:
- Test assertions updated to verify `0.8.0`.

In `docs/CHANGELOG.md` & `docs/CHANGELOG_PT.md`:
- Unreleased section promoted to `0.8.0`.

In root `README.md` & `README_PT.md`:
- Version status and release links bumped to `0.8.0`.
