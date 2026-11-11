# Proposal: Release v0.5.3

## Problem Statement
Following the resolution of CSV export relative paths, working directory restoration, complete multi-cell file lock elimination on Windows, language service cell analysis order optimization, and interactive demo file pipeline cases, all tests are passing 100% and the release candidate has been fully verified and approved. The project is ready for the official `v0.5.3` production release.

## Proposed Changes
1. Finalize extension version as `0.5.3` across `package.json`, `package-lock.json`, and fallback metadata in `ProjectUpgrader.fs`.
2. Update English and Portuguese changelogs (`CHANGELOG.md`, `docs/CHANGELOG.md`, `docs/CHANGELOG_PT.md`) to mark version `0.5.3` as released.
3. Update `README.md` and `README_PT.md` status badges and release references to `0.5.3`.
4. Validate 100% test passing locally and in Linux container (`mcr.microsoft.com/dotnet/sdk:10.0`).
5. Open PR `chore(release): prepare v0.5.3 release` for merge into `main` and trigger official release tag `v0.5.3`.

