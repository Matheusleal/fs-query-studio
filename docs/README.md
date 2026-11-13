# F# Query Studio Documentation (`docs/`)

[🇺🇸 English](README.md) | [🇧🇷 Versão em Português](README_PT.md)

Welcome to the central documentation index for **F# Query Studio**. This folder is the single canonical source of truth for all documentation, technical specifications, user guides, benchmarks, engineering runbooks, proposals, and task records.

---

## 1. User Guides & Walkthroughs

| Document | Language | Description |
|---|---|---|
| [`USER_GUIDE.md`](USER_GUIDE.md) | 🇺🇸 English | **Comprehensive User Guide** — Complete reference for F# Query Notebooks (`.fsqnb`), all data connectors (Postgres, SQL Server, MySQL, SQLite, CSV, Excel, JSON, REST), LINQ pushdown queries, custom formulas, UI and shortcuts. |
| [`USER_GUIDE_PT.md`](USER_GUIDE_PT.md) | 🇧🇷 Português | **Guia Completo do Usuário** — Manual detalhado de uso, conexões, consultas LINQ, fórmulas de higienização de colunas e comandos da extensão. |
| [`TUTORIAL.md`](TUTORIAL.md) | 🇺🇸 English | **Hands-On Tutorial** — Step-by-step walkthrough: first notebook → connection registration → pushdown query → CSV export. |
| [`TUTORIAL_PT.md`](TUTORIAL_PT.md) | 🇧🇷 Português | **Tutorial Prático Passo a Passo** — Do zero ao primeiro notebook, conexões e exportação. |

---

## 2. Technical & Language Specifications

| Document | Description |
|---|---|
| [`FSQL_LANGUAGE_SPECIFICATION.md`](FSQL_LANGUAGE_SPECIFICATION.md) | **FSQL Language Specification** — Formal syntax and semantics of the `fsql { ... }` computation expression across relational engines, joins, subqueries, aggregations, window functions, and pushdown rules. |
| [`FSQL_AI_AGENT_GUIDE.md`](FSQL_AI_AGENT_GUIDE.md) | **FSQL AI Agent Guide** — Strict rules, canonical patterns, syntax guidelines, and anti-patterns for AI Agents generating queries in F# Query Studio. |

---

## 3. Performance Benchmarks

| Document | Language | Description |
|---|---|---|
| [`BENCHMARK_REPORT.md`](BENCHMARK_REPORT.md) | 🇺🇸 English | **Execution & Resource Benchmarks** — Detailed measurements for cold start, warm query execution, high-volume scaling (100 to 100,000+ rows), memory consumption (GC), and UI responsiveness. |
| [`BENCHMARK_REPORT_PT.md`](BENCHMARK_REPORT_PT.md) | 🇧🇷 Português | **Relatório de Desempenho e Benchmarks** — Métricas completas de tempo de CPU, latência, alocações de memória e cobertura de testes. |

---

## 4. Releases, Changelogs & DevOps Runbooks

| Document | Description |
|---|---|
| [`CHANGELOG.md`](CHANGELOG.md) | **Release History (English)** — Chronological release notes and user-visible improvements per version. |
| [`CHANGELOG_PT.md`](CHANGELOG_PT.md) | **Histórico de Lançamentos (Português)** — Notas de versão em português. |
| [`vscode-extension-release.md`](vscode-extension-release.md) | **Extension Release Runbook** — Preflight checklist, version tagging, VSIX packaging verification, and Marketplace publishing procedure. |
| [`../editors/vscode/scripts/README.md`](../editors/vscode/scripts/README.md) | **Packaging Scripts Documentation** — Reference for the npm commands (`doctor`, `build`, `package`, `publish`). |

---

## 5. Technical Proposals & RFCs

| Directory / Link | Description |
|---|---|
| [`proposals/`](proposals/README.md) | **Architecture Proposals & RFCs** — High-level technical proposals and design improvements for worker resilience, performance, and code cleanup. |

---

## 6. Specification & Change Management (SDD)

| Directory / Link | Description |
|---|---|
| [`sdd/`](sdd/README.md) | **Spec-Driven Development (SDD)** — Formal Delta Specs, proposals, and task ledgers under `sdd/changes/` (active sprint changes) and `sdd/archive/` (consolidated changes). |
| [`DOCUMENTATION_AUDIT_REPORT.md`](DOCUMENTATION_AUDIT_REPORT.md) | **Documentation Audit & Inventory Report** — Comprehensive audit of all tasks, specifications, proposals, and archive files across the codebase. |

---

## 7. Historical Records & Architecture Evolution

These documents preserve the history and design evolution of F# Query Studio:

| Document / Directory | Description |
|---|---|
| [`DEVLOG.md`](DEVLOG.md) | **Append-Only Engineering Devlog** — Dated chronological log of every development step taken since project inception. |
| [`archive/`](archive/README.md) | **Historical Archive** — Foundational VS Code extension design specifications (`archive/specs/`), desktop architecture plans, and the F6 parity checklist. |
