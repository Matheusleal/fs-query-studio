# Proposal: Release v0.6.0

## Problem Statement
Following the delivery of advanced DQL pushdown (CROSS APPLY, LATERAL, column-level aggregates, HAVING, subqueries, EXISTS), modern in-CE Common Table Expressions (CTEs: unnamed transparent registration, named, raw recursive CTEs), in-CE Set Operations (union, unionAll, intersect, except), 100% idiomatic outer joins using LINQ syntax, live query plan diagnostics (explain, toRawQuery, explainQuery, .Explain()), unified multi-engine demo generators with language selection, and full bilingual documentation parity, all tests are passing 100% and the features are fully merged into `main`. The project is ready for the official `v0.6.0` production release.

## Proposed Changes
1. Finalize extension version as `0.6.0` across `package.json`, `package-lock.json`, and fallback metadata in `ProjectUpgrader.fs`.
2. Update English and Portuguese changelogs (`CHANGELOG.md`, `docs/CHANGELOG.md`, `docs/CHANGELOG_PT.md`, `editors/vscode/CHANGELOG.md`, `editors/vscode/docs/CHANGELOG*.md`) to document all version `0.6.0` additions.
3. Update `README.md` and `README_PT.md` status badges and release references to `0.6.0`.
4. Validate 100% test passing locally and in Linux container (`mcr.microsoft.com/dotnet/sdk:10.0`).
5. Open PR `chore(release): prepare v0.6.0 release` for merge into `main` and trigger official release tag `v0.6.0`.
