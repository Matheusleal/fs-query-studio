# Tasks: Release v0.8.0

- [x] TASK-1: Bump version in `editors/vscode/package.json` and `package-lock.json` to `0.8.0`.
- [x] TASK-2: Update fallback extension version in `src/FSharpQueryStudio.Storage/ProjectUpgrader.fs` and tests to `0.8.0`.
- [x] TASK-3: Update English and Portuguese changelogs to release `0.8.0`.
- [x] TASK-4: Update `README.md` and `README_PT.md` status and release references to `0.8.0`.
- [x] TASK-5: Format F# code with Fantomas (`dotnet fantomas build.fsx src tests`).
- [x] TASK-6: Execute tests (`dotnet test -c Release` and `npm test`).
- [x] TASK-7: Execute `dotnet fsi .\build.fsx release` to synchronize docs and package VSIX.
- [x] TASK-8: Commit changes and push branch `feature/release-v0.8.0`.
