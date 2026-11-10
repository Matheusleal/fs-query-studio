# Proposal: Release v0.5.2

## Problem Statement
Following the merge of PR #42 fixing scalar option projection mapping and refining F# query expressions and interpolated strings syntax highlighting in TextMate, the project is ready for the v0.5.2 patch release.

## Proposed Changes
1. Bump package version to `0.5.2` across `editors/vscode/package.json`, `editors/vscode/package-lock.json`, and `ProjectUpgrader.fs`.
2. Document all fixes in English and Portuguese changelogs (`CHANGELOG.md`, `docs/CHANGELOG.md`, `docs/CHANGELOG_PT.md`).
3. Update status and release references in `README.md` and `README_PT.md`.
4. Run full test suite validation locally and in Linux container to ensure 100% test passing before release.

