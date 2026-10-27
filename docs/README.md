# `docs/`

Everything written about F# Query Studio. Split into what you read to *use or
ship* the project and what exists as a *historical record*.

## Current — read these

| File | For |
|---|---|
| [`../README.md`](../README.md) | Project overview, requirements, install, repo layout. Start here. |
| [`../editors/vscode/USER_GUIDE.md`](../editors/vscode/USER_GUIDE.md) | Comprehensive User Guide covering LINQ, all connectors, UI and commands (English). |
| [`../editors/vscode/USER_GUIDE_PT.md`](../editors/vscode/USER_GUIDE_PT.md) | Guia do Usuário Completo em Português cobrindo LINQ, todos os conectores e interface. |
| [`../editors/vscode/README.md`](../editors/vscode/README.md) | Full feature and API reference for the extension (also the Marketplace description). |
| [`TUTORIAL.md`](TUTORIAL.md) | Hands-on walkthrough: first notebook → connection → query → CSV export. |
| [`vscode-extension-release.md`](vscode-extension-release.md) | The manual runbook for packaging and publishing a release. |
| [`../editors/vscode/scripts/README.md`](../editors/vscode/scripts/README.md) | Reference for the `build` / `package` / `publish` / `doctor` npm scripts. |
| [`../editors/vscode/CHANGELOG.md`](../editors/vscode/CHANGELOG.md) | Per-version list of user-visible changes. |

## Historical — record, not documentation

These describe how the project got here. They are **not** kept in sync with the
current code and should not be edited as if they were.

| Path | What it is |
|---|---|
| [`DEVLOG.md`](DEVLOG.md) | Dated, append-only engineering log — one entry per task since the project began. Add to it; do not rewrite it. |
| `superpowers/specs/` | The design spec produced before each phase (SDD workflow). Frozen once its phase shipped. |
| `superpowers/plans/` | The implementation plan produced from each spec. Frozen likewise. |
| `tasks/` | Per-task working notes for individual fixes and features. |
| [`f6-parity-checklist.md`](f6-parity-checklist.md) | The point-by-point check that the VS Code extension matched the retired desktop app. |
| [`fs-studio-plan.md`](fs-studio-plan.md), [`fs-studio-development-plan.md`](fs-studio-development-plan.md) | The original architecture and phase plan from the **Avalonia desktop** era. Superseded by the VS Code extension; kept for context. |

## How the project is built

F# Query Studio began as an Avalonia desktop application. It was rebuilt as a VS
Code extension in phases **F1–F9**; the desktop app, its UI projects, and the
Windows installer were then deleted. The `src/FSharpQueryStudio.*` worker and
libraries carried straight over — only the front end changed.

The design → plan → implement cycle for each phase is recorded under
`superpowers/`; the running narrative is in `DEVLOG.md`.
