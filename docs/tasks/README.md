# `docs/tasks/` — Notas de Execução de Tarefas / Task Execution Notes

Este diretório contém os cadernos de trabalho e notas de execução técnica de tarefas específicas realizadas durante as fases pós-migração (F7 a F9) e correções de interface do **F# Query Studio**.

---

## Tarefas Registradas / Registered Tasks

| Tarefa | Fase / Contexto | Descrição |
|---|---|---|
| [`f7-1-connection-name-completion.md`](f7-1-connection-name-completion.md) | Fase F7 | Autocomplete inteligente de nomes de conexões em strings de células de notebook (`fsql connection="..."`). |
| [`f7-7-fsac-notebook-cell-uri-diagnostics.md`](f7-7-fsac-notebook-cell-uri-diagnostics.md) | Fase F7 | Roteamento e mapeamento de diagnósticos do FSAC para URIs de células de notebook no VS Code. |
| [`f8-build-publish-scripts.md`](f8-build-publish-scripts.md) | Fase F8 | Implementação e validação dos scripts multiplataforma de build, empacotamento VSIX e publicação (`doctor`, `build`, `package`, `publish`). |
| [`f9-new-notebook-command.md`](f9-new-notebook-command.md) | Fase F9 | Comando e template padrão para criação de novos cadernos de consulta (`F# Query Studio: New Query Notebook`). |
| [`fix-notebook-output-mime-priority.md`](fix-notebook-output-mime-priority.md) | Correção | Ajuste da prioridade de renderização MIME para tabelas de resultados interativas e textos de saída. |
| [`notebook-grid-visual-polish.md`](notebook-grid-visual-polish.md) | Interface | Polimento visual e de usabilidade do componente webview de tabela de dados (grid de resultados). |

---

## Outros Rastreamentos de Tarefas no Projeto

- **Mudanças Formais (SDD)**: Para alterações arquiteturais e novas funcionalidades governadas pelo fluxo Spec-Driven Development, consulte [`../sdd/changes/`](../sdd/).
- **Fases de Migração (Superpowers)**: Para os planos e especificações das fases fundamentais (F1 a F6), consulte [`../superpowers/`](../superpowers/).
- **Histórico Completo de Engenharia**: O diário contínuo de tarefas desde o início do projeto está registrado cronologicamente em [`../DEVLOG.md`](../DEVLOG.md).

