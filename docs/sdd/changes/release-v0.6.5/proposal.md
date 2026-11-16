# Proposal: Release v0.6.5

## Problem Statement
Following the delivery and merge of MySQL, MariaDB, and Oracle database connectors (PR #55), multi-engine Docker Compose demo generators with SQL seed scripts, automated E2E sample testing harness (`npm run test:samples`), identifier sanitization, and Oracle catalog noise reduction, all features have been merged into `main`. The project is now ready for the official `v0.6.5` production release.

## Proposed Changes
1. Finalize extension version as `0.6.5` across `package.json`, `package-lock.json`, and fallback metadata in `ProjectUpgrader.fs`.
2. Update English and Portuguese changelogs (`CHANGELOG.md`, `docs/CHANGELOG.md`, `docs/CHANGELOG_PT.md`, `editors/vscode/CHANGELOG.md`, `editors/vscode/docs/CHANGELOG*.md`) to document all version `0.6.5` additions.
3. Update `README.md` and `README_PT.md` status badges and release references to `0.6.5`.
4. Validate 100% build and packaging (`dotnet fsi .\build.fsx release`).
5. Open PR `chore(release): prepare v0.6.5 release` for merge into `main` and trigger official release tag `v0.6.5`.
