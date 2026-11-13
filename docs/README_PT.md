# Documentação do F# Query Studio (`docs/`)

[🇺🇸 English](README.md) | [🇧🇷 Versão em Português](README_PT.md)

Bem-vindo ao índice central de documentação do **F# Query Studio**. Este diretório é a fonte da verdade canônica e oficial de toda a documentação, especificações técnicas, manuais do usuário, relatórios de benchmark, runbooks de publicação, propostas e registros de tarefas do projeto.

---

## 1. Guias do Usuário & Tutoriais

| Documento | Idioma | Descrição |
|---|---|---|
| [`USER_GUIDE_PT.md`](USER_GUIDE_PT.md) | 🇧🇷 Português | **Guia Completo do Usuário** — Manual detalhado de uso: cadernos `.fsqnb`, todos os conectores de dados (Postgres, SQL Server, MySQL, SQLite, CSV, Excel, JSON, REST), consultas LINQ com pushdown SQL, fórmulas de higienização de dados, atalhos e interface. |
| [`USER_GUIDE.md`](USER_GUIDE.md) | 🇺🇸 English | **Comprehensive User Guide** — Manual completo de uso em inglês. |
| [`TUTORIAL_PT.md`](TUTORIAL_PT.md) | 🇧🇷 Português | **Tutorial Prático Passo a Passo** — Do zero ao primeiro notebook: criação de conexões, consultas LINQ pushdown e exportação para CSV. |
| [`TUTORIAL.md`](TUTORIAL.md) | 🇺🇸 English | **Hands-On Tutorial** — Tutorial prático passo a passo em inglês. |

---

## 2. Especificações Técnicas & Linguagem FSQL

| Documento | Descrição |
|---|---|
| [`FSQL_LANGUAGE_SPECIFICATION.md`](FSQL_LANGUAGE_SPECIFICATION.md) | **Especificação da Linguagem FSQL** — Especificação formal canônica de sintaxe e semântica da computation expression `fsql { ... }` para todos os bancos relacionais e engines analíticas suportadas. |
| [`FSQL_AI_AGENT_GUIDE.md`](FSQL_AI_AGENT_GUIDE.md) | **Guia Canônico de FSQL para Agentes de IA** — Regras estritas, padrões recomendados e anti-padrões para agentes de IA gerando código FSQL. |

---

## 3. Desempenho & Benchmarks

| Documento | Idioma | Descrição |
|---|---|---|
| [`BENCHMARK_REPORT_PT.md`](BENCHMARK_REPORT_PT.md) | 🇧🇷 Português | **Relatório de Desempenho e Benchmarks** — Métricas detalhadas de tempo de inicialização (cold start), execução aquecida (warm query), testes de alta volumetria (100 a 100.000+ linhas), uso de memória (GC) e responsividade de UI. |
| [`BENCHMARK_REPORT.md`](BENCHMARK_REPORT.md) | 🇺🇸 English | **Execution & Resource Benchmarks** — Relatório de benchmarks em inglês. |

---

## 4. Lançamentos, Changelogs & Engenharia DevOps

| Documento | Descrição |
|---|---|
| [`CHANGELOG_PT.md`](CHANGELOG_PT.md) | **Histórico de Lançamentos (Português)** — Notas de versão cronológicas e melhorias visíveis para o usuário. |
| [`CHANGELOG.md`](CHANGELOG.md) | **Release History (English)** — Notas de versão em inglês. |
| [`vscode-extension-release.md`](vscode-extension-release.md) | **Runbook de Publicação da Extensão** — Procedimento manual de checagem prévia, versionamento, validação de integridade do VSIX e publicação no Marketplace. |
| [`../editors/vscode/scripts/README.md`](../editors/vscode/scripts/README.md) | **Scripts de Build e Packaging** — Referência para os comandos npm (`doctor`, `build`, `package`, `publish`). |

---

## 5. Propostas Técnicas & RFCs

| Diretório / Link | Descrição |
|---|---|
| [`proposals/`](proposals/README.md) | **Propostas Técnicas e RFCs** — Propostas de melhorias arquiteturais, resiliência do F# Worker, otimização de performance e limpeza de código legado. |

---

## 6. Gestão de Especificações e Mudanças (SDD)

| Diretório / Link | Descrição |
|---|---|
| [`sdd/`](sdd/README.md) | **Spec-Driven Development (SDD)** — Delta Specs, propostas e tarefas formais governadas pela metodologia SDD em `sdd/changes/` (mudanças ativas) e `sdd/archive/` (consolidadas). |
| [`DOCUMENTATION_AUDIT_REPORT.md`](DOCUMENTATION_AUDIT_REPORT.md) | **Relatório de Auditoria e Diagnóstico da Documentação** — Inventário completo e análise profunda de relevância e descarte de documentos do projeto. |

---

## 7. Registros Históricos & Arquivo

Estes documentos preservam o histórico e evolução do projeto:

| Documento / Diretório | Descrição |
|---|---|
| [`DEVLOG.md`](DEVLOG.md) | **Diário de Desenvolvimento (Devlog)** — Registro cronológico e contínuo de cada tarefa e decisão de engenharia desde o primeiro commit. |
| [`archive/`](archive/README.md) | **Arquivo Histórico** — Especificações fundacionais de design da extensão VS Code (`archive/specs/`), arquitetura da era desktop e checklist de paridade F6. |

