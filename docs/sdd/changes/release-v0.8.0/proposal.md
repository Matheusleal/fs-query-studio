# Proposal: Release v0.8.0

## Problem Statement
Following the delivery and merge of Raw Query tab in notebook cell results (#61), enhanced FSQL query clauses (`isNotNull`, `isNotEmpty`, `where (not ...)`), multi-database discovery and tree hierarchy mapping (#58), single-stream SQLite writer and async secret storage (#59), full 100% test coverage elevation (#60), and knowledge graph (`graphify`) integration with automated ignore rules, all features are ready for the official `v0.8.0` production release.

## Proposed Changes
1. Finalize extension version as `0.8.0` across `package.json`, `package-lock.json`, and fallback metadata in `ProjectUpgrader.fs`.
2. Update English and Portuguese changelogs (`CHANGELOG.md`, `docs/CHANGELOG.md`, `docs/CHANGELOG_PT.md`, `editors/vscode/CHANGELOG.md`, `editors/vscode/docs/CHANGELOG*.md`) to document all version `0.8.0` additions.
3. Update `README.md` and `README_PT.md` status badges and release references to `0.8.0`.
4. Run full test suite across backend and frontend.
5. Validate 100% build and packaging (`dotnet fsi .\build.fsx release`).
6. Push feature branch `feature/release-v0.8.0`.
