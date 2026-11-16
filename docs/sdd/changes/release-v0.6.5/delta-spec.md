# Delta Specification: Release v0.6.5

## Requirement Delta

### ADDED

Where the build and release pipelines package the extension,
the build metadata shall define version 0.6.5 across manifest configurations and compatibility constants.

Where the documentation files reference current release capabilities,
the project changelogs and readmes shall document all v0.6.5 enhancements and additions:
- MySQL, MariaDB, and Oracle SQL connectors.
- Multi-engine Docker Compose demo generator and sample notebooks (12 notebooks total, EN and PT).
- Automated E2E sample testing suite (`npm run test:samples`).
- Identifier sanitization and Oracle system catalog noise filtering.

### MODIFIED

In `editors/vscode/package.json`:
- Version bumped from `0.6.0` to `0.6.5`.

In `src/FSharpQueryStudio.Storage/ProjectUpgrader.fs`:
- Default fallback extension version bumped to `0.6.5`.

In `docs/CHANGELOG.md` & `docs/CHANGELOG_PT.md`:
- Unreleased section promoted to `0.6.5`.

In root `README.md` & `README_PT.md`:
- Version status and release links bumped to `0.6.5`.
