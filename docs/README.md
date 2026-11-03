# `docs/`

Everything written about F# Query Studio. Split into what you read to *use or
ship* the project and what exists as a *historical record*.

## Current — read these

| File | For |
|---|---|
| [`../README.md`](../README.md) | Project overview, requirements, install, repo layout (English). Start here. |
| [`../README_PT.md`](../README_PT.md) | Visão geral do projeto, requisitos, instalação e arquitetura (Português). |
| [`../editors/vscode/docs/USER_GUIDE.md`](../editors/vscode/docs/USER_GUIDE.md) | Comprehensive User Guide covering LINQ, all connectors, UI and commands (English). |
| [`../editors/vscode/docs/USER_GUIDE_PT.md`](../editors/vscode/docs/USER_GUIDE_PT.md) | Guia do Usuário Completo em Português cobrindo LINQ, todos os conectores e interface. |
| [`BENCHMARK_REPORT.md`](BENCHMARK_REPORT.md) | Comprehensive performance benchmarks (CPU, memory, latency) and UI test coverage (English). |
| [`BENCHMARK_REPORT_PT.md`](BENCHMARK_REPORT_PT.md) | Relatório abrangente de desempenho, consumo de recursos e cobertura de testes (Português). |
| [`../editors/vscode/docs/README.md`](../editors/vscode/docs/README.md) | Full feature and API reference for the extension (also the Marketplace description). |
| [`../editors/vscode/docs/README_PT.md`](../editors/vscode/docs/README_PT.md) | Referência completa de recursos e API da extensão para o VS Code (Português). |
| [`TUTORIAL.md`](TUTORIAL.md) | Hands-on walkthrough: first notebook → connection → pushdown query → CSV export (English). |
| [`TUTORIAL_PT.md`](TUTORIAL_PT.md) | Tutorial prático passo a passo: primeiro notebook → conexões → consultas pushdown → exportação CSV (Português). |
| [`vscode-extension-release.md`](vscode-extension-release.md) | The manual runbook for packaging and publishing a release. |
| [`../editors/vscode/scripts/README.md`](../editors/vscode/scripts/README.md) | Reference for the `build` / `package` / `publish` / `doctor` npm scripts. |
| [`../editors/vscode/docs/CHANGELOG.md`](../editors/vscode/docs/CHANGELOG.md) | Per-version list of user-visible changes. |

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
