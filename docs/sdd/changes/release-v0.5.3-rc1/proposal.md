# Proposal: Release Candidate v0.5.3-rc1

## Problem Statement
Following the resolution of CSV export path handling, working directory restoration, file lock prevention on user-created files in persistent FSI sessions, URI decoding in result retention, and upfront FCS assembly references, the project is ready for the `v0.5.3-rc1` release candidate.

## Proposed Changes
1. Bump package version to `0.5.3` in `editors/vscode/package.json` and `editors/vscode/package-lock.json`, and set fallback extension version to `0.5.3-rc1` in `ProjectUpgrader.fs`.
2. Document all v0.5.3-rc1 fixes in English and Portuguese changelogs (`CHANGELOG.md`, `docs/CHANGELOG.md`, `docs/CHANGELOG_PT.md`).
3. Update status and release references in `README.md` and `README_PT.md`.
4. Validate 100% test passing locally and verify packaged VSIX artifacts.
5. Create release candidate tag `v0.5.3-rc1` and publish pre-release via GitHub Actions release workflow.

