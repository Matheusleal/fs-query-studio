# Relatório de Auditoria e Diagnóstico de Documentação

**Data da Auditoria**: Novembro de 2026  
**Repositório**: `fs-query-studio`  
**Escopo**: Análise profunda dos diretórios `docs/tasks/`, `docs/superpowers/`, `docs/sdd/`, `docs/proposals/` e `docs/archive/`.  
**Objetivo**: Identificar quais documentos fazem sentido **manter** (ativos ou em arquivo histórico) e quais fazem sentido **remover** (obsoletos, código morto, redundantes ou efêmeros), sem aplicar alterações destrutivas neste momento.

---

## 1. Sumário Executivo

A auditoria cobriu **106 arquivos** distribuídos entre tarefas, planos de migração, especificações do fluxo SDD, propostas de melhoria técnica e arquivos legados.

### Panorama Quantitativo

| Diretório | Total de Arquivos | Tamanho Total | Estado Predominante |
|---|---|---|---|
| `docs/tasks/` | 7 arquivos | ~53 KB | Concluído / Histórico pós-migração |
| `docs/superpowers/` | 26 arquivos | ~1.1 MB | Concluído / Obsoleto (era desktop e migração) |
| `docs/sdd/` | 73 arquivos | ~238 KB | 1 ativo (`advanced-dql...`), 23 já entregues |
| `docs/proposals/` | 3 arquivos | ~44 KB | Ativo / Roadmap futuro |
| `docs/archive/` | 4 arquivos | ~80 KB | Concluído / Histórico (era Avalonia) |
| **Total Auditado** | **113 arquivos** | **~1.5 MB** | — |

### Resumo das Recomendações

```text
┌────────────────────────────────────────────────────────┐
│ Total de Arquivos Analisados: 113                     │
├────────────────────────────┬───────────────────────────┤
│ MANTER (Ativos / Vivos)    │  6 arquivos  (  5.3 %)    │
│ ARQUIVAR (Consolidados)    │ 28 arquivos  ( 24.8 %)    │
│ REMOVER (Obsoletos/Ruído)  │ 79 arquivos  ( 69.9 %)    │
└────────────────────────────┴───────────────────────────┘
```

A grande maioria dos documentos no repositório (~70%) é composta por **artefatos efêmeros de execução passada** (checklists diários de tarefas finalizadas, planos de implementação de funcionalidades da interface Avalonia que já foi 100% deletada do repositório, e dezenas de pastas de delta-specs do SDD que já foram incorporadas ao código e nunca foram arquivadas/limpas).

---

## 2. Análise Detalhada por Diretório

---

### 2.1. `docs/tasks/` (7 arquivos)

Cadernos de notas de tarefas pontuais executadas durante as fases F7 a F9 e polimento de UI após a migração do VS Code.

| Arquivo | Tamanho | Descrição | Status Real | Recomendação |
|---|---|---|---|---|
| `f7-1-connection-name-completion.md` | 7.4 KB | Autocomplete de conexões em strings | Entregue | **REMOVER** (ou consolidar em `archive/tasks/`) |
| `f7-7-fsac-notebook-cell-uri-diagnostics.md` | 7.3 KB | Mapeamento de diagnósticos de células | Entregue | **REMOVER** (ou consolidar em `archive/tasks/`) |
| `f8-build-publish-scripts.md` | 10.2 KB | Scripts de empacotamento VSIX | Entregue | **REMOVER** |
| `f9-new-notebook-command.md` | 8.8 KB | Comando de novo notebook | Entregue | **REMOVER** |
| `fix-notebook-output-mime-priority.md` | 10.9 KB | Fix de prioridade MIME do grid | Entregue | **REMOVER** |
| `notebook-grid-visual-polish.md` | 6.5 KB | Polimento de estilos e cores do grid | Entregue | **REMOVER** |
| `README.md` | 2.1 KB | Índice local das tarefas | Organizacional | **MANTER** se as tasks forem arquivadas |

#### Justificativa Técnica:
- **Por que remover?** Todos esses itens são notas de execução de trabalho pontual encerrado. O código implementado já está coberto por testes automatizados e o histórico cronológico de cada um está preservado em `docs/DEVLOG.md`. Manter esses arquivos soltos na árvore ativa passa a falsa impressão de que são tarefas abertas ou pendentes.
- **Alternativa suave**: Se o time preferir manter o registro dessas 6 notas, movê-las em lote para `docs/archive/tasks/` para despoluir a documentação de primeiro nível.

---

### 2.2. `docs/superpowers/` (26 arquivos)

Contém planos e especificações técnicas de duas fases distintas:
1. **Era Desktop Avalonia (Agosto/2026)**: 16 arquivos
2. **Migração VS Code F1–F6 (Setembro/2026)**: 10 arquivos

#### A. Era Desktop Avalonia (CÓDIGO MORTO)
Estes documentos descrevem componentes, classes e layouts da aplicação Avalonia UI (`DockControl`, `Dock.Avalonia`, `LogViewerWindow.axaml`, `WelcomePage`, Monaco WebView bridge, etc.) que **foram completamente deletados do repositório** na fase F6.

| Arquivo | Tipo | Tamanho | Referência de Código | Recomendação |
|---|---|---|---|---|
| `plans/2026-08-06-ui-foundation.md` | Plan | 78.3 KB | `Dock.Avalonia` (Deletado) | **REMOVER** |
| `specs/2026-08-06-ui-foundation-design.md` | Spec | 10.4 KB | Layout desktop (Deletado) | **REMOVER** |
| `plans/2026-08-11-connections-ui.md` | Plan | 54.9 KB | UI Desktop Avalonia (Deletado) | **REMOVER** |
| `plans/2026-08-11-editor-intelligence-wiring.md` | Plan | 52.4 KB | Monaco bridge WebView (Deletado) | **REMOVER** |
| `plans/2026-08-12-log-viewer.md` | Plan | 42.9 KB | Janela XAML LogViewer (Deletado) | **REMOVER** |
| `specs/2026-08-12-log-viewer-design.md` | Spec | 10.7 KB | Janela XAML LogViewer (Deletado) | **REMOVER** |
| `plans/2026-08-12-results-export.md` | Plan | 52.5 KB | Export desktop (Deletado) | **REMOVER** |
| `specs/2026-08-12-results-export-design.md` | Spec | 16.3 KB | Export desktop (Deletado) | **REMOVER** |
| `plans/2026-08-12-results-table-generalization.md` | Plan | 35.3 KB | DataGrid Avalonia (Deletado) | **REMOVER** |
| `specs/2026-08-12-results-table-generalization-design.md` | Spec | 9.3 KB | DataGrid Avalonia (Deletado) | **REMOVER** |
| `plans/2026-08-12-welcome-page.md` | Plan | 14.2 KB | Welcome tab XAML (Deletado) | **REMOVER** |
| `specs/2026-08-12-welcome-page-design.md` | Spec | 9.4 KB | Welcome tab XAML (Deletado) | **REMOVER** |
| `plans/2026-08-18-fsac-analysis-backend.md` | Plan | 85.2 KB | Troca inicial FSAC | **REMOVER** |
| `specs/2026-08-18-fsac-analysis-backend-design.md` | Spec | 13.3 KB | Design inicial FSAC | **REMOVER** |

#### B. Migração VS Code F1 a F6 (ARQUITETURA ATUAL)
Estes documentos registraram a migração que construiu a extensão do VS Code hoje em produção.

| Arquivo | Tipo | Tamanho | Relevância | Recomendação |
|---|---|---|---|---|
| `specs/2026-09-03-vscode-extension-migration-design.md` | Spec | 26.3 KB | **Alta**: Documento fundacional da arquitetura atual (Worker stdio + LSP + Renderer) | **MANTER NO ARQUIVO** (`docs/archive/specs/`) |
| `plans/2026-09-03-vscode-extension-f1-scaffold.md` | Plan | 73.6 KB | Baixa: Checklist de scaffolding inicial | **REMOVER** |
| `plans/2026-09-05-vscode-extension-f2-notebook.md` | Plan | 82.7 KB | Baixa: Checklist de controller/serializer | **REMOVER** |
| `plans/2026-09-06-vscode-extension-f3-renderer.md` | Plan | 80.8 KB | Baixa: Checklist de webview/grid | **REMOVER** |
| `plans/2026-09-06-vscode-extension-f4-cell-intelligence.md` | Plan | 81.1 KB | Baixa: Checklist do bridge de inteligência | **REMOVER** |
| `specs/2026-09-06-vscode-extension-f4-cell-intelligence-design.md` | Spec | 19.2 KB | Média: Design de diagnóstico/URI | **MANTER NO ARQUIVO** (`docs/archive/specs/`) |
| `plans/2026-09-07-vscode-extension-f5-connections.md` | Plan | 125.3 KB | Baixa: Checklist do Connection TreeView | **REMOVER** |
| `specs/2026-09-07-vscode-extension-f5-connections-design.md` | Spec | 25.4 KB | Média: Design da árvore de conexões | **MANTER NO ARQUIVO** (`docs/archive/specs/`) |
| `plans/2026-09-07-vscode-extension-f6-parity-retire.md` | Plan | 52.0 KB | Baixa: Checklist de deleção do Avalonia | **REMOVER** |
| `specs/2026-09-07-vscode-extension-f6-parity-retire-design.md` | Spec | 21.2 KB | Média: Critérios de paridade F6 | **MANTER NO ARQUIVO** (`docs/archive/specs/`) |
| `plans/2026-09-08-notebook-tail-value-results.md` | Plan | 50.5 KB | Baixa: Checklist de retorno de tail value | **REMOVER** |
| `specs/2026-09-08-notebook-tail-value-results-design.md` | Spec | 10.3 KB | Alta: Citado em `src/.../Projection.fs` | **MANTER NO ARQUIVO** (`docs/archive/specs/`) |

#### Justificativa Técnica:
- **Redução de ~1 MB de ruído**: Os 15 arquivos `.plan` são checklists diários gigantescos (cada um com 50–120 KB) contendo caixas `[x]` passo a passo de commits que foram feitos há meses. Eles não servem como documentação de consulta técnica e poluem as buscas globais.
- Apenas as **especificações de design fundacionais** (`specs/*migration-design*`, `tail-value-results-design*`) possuem valor de consulta arquitetural e devem ser preservadas em `docs/archive/specs/`.

---

### 2.3. `docs/sdd/` (73 arquivos em 24 mudanças)

O diretório `docs/sdd/changes/` acumula 24 pastas de Delta Spec (cada uma contendo `proposal.md`, `delta-spec.md` e `tasks.md`).

#### Diagnóstico Frente às Regras do SDD:
Conforme a regra **RULE[sdd-core-rules.md]**:
> *"Mudanças pós-baseline são Delta Specs... Use o ciclo de vida de mudanças: `proposal.md` ➔ `delta-spec.md` ➔ `tasks.md` ➔ Implementação ➔ Validação ➔ Consolidação nas specs e arquivamento em `archive/`."*

Atualmente, **nenhuma das mudanças concluídas foi arquivada**, mantendo 24 pastas lado a lado como se fossem mudanças concorrentes em andamento.

#### Classificação das Mudanças:

| Pacote de Mudança | Tipo | Status no Código | Recomendação |
|---|---|---|---|
| `advanced-dql-support-sqlserver-postgres-sqlite` | Feature | **ATIVA (Em andamento)** | **MANTER (Ativa em `changes/`)** |
| `backend-code-cleanup-and-modularization` | Refactor | 100% Implementado / Mergeado | **CONSOLIDAR & ARQUIVAR** (`sdd/archive/`) |
| `comprehensive-query-engine-features` | Feature | 100% Implementado / Mergeado | **CONSOLIDAR & ARQUIVAR** (`sdd/archive/`) |
| `connector-modularization-and-plugin-architecture` | Architecture | 100% Implementado / Mergeado | **CONSOLIDAR & ARQUIVAR** (`sdd/archive/`) |
| `database-query-pushdown-and-performance` | Perf | 100% Implementado / Mergeado | **CONSOLIDAR & ARQUIVAR** (`sdd/archive/`) |
| `demo-data-100k-rows-and-expanded-columns` | Demo | 100% Implementado / Mergeado | **CONSOLIDAR & ARQUIVAR** (`sdd/archive/`) |
| `dependabot-security-upgrades` | Security | 100% Implementado / Mergeado | **REMOVER** (Dependabot comum) |
| `excel-schema-prefix-mapping` | Feature | 100% Implementado / Mergeado | **CONSOLIDAR & ARQUIVAR** (`sdd/archive/`) |
| `extension-publish-security-safeguards` | Security | 100% Implementado / Mergeado | **CONSOLIDAR & ARQUIVAR** (`sdd/archive/`) |
| `fix-ci-windows-integration-test-timeout` | Fix CI | 100% Implementado / Mergeado | **REMOVER** (Fix efêmero de CI) |
| `fix-csv-export-and-working-directory-file-lock` | Bugfix | 100% Implementado / Mergeado | **CONSOLIDAR & ARQUIVAR** (`sdd/archive/`) |
| `fix-table-header-column-width-sync` | UI Fix | 100% Implementado / Mergeado | **CONSOLIDAR & ARQUIVAR** (`sdd/archive/`) |
| `fix-table-scroll-excel-tree-option-query` | Bugfix | 100% Implementado / Mergeado | **CONSOLIDAR & ARQUIVAR** (`sdd/archive/`) |
| `fsqs-folder-and-sqlserver-windows-auth` | Feature | 100% Implementado / Mergeado | **CONSOLIDAR & ARQUIVAR** (`sdd/archive/`) |
| `local-workspace-project-support` | Feature | 100% Implementado / Mergeado | **CONSOLIDAR & ARQUIVAR** (`sdd/archive/`) |
| `memory-and-autocomplete-performance-optimizations` | Perf | 100% Implementado / Mergeado | **CONSOLIDAR & ARQUIVAR** (`sdd/archive/`) |
| `release-v0.5.1` | Release | Versão antiga lançada | **REMOVER** (Changelog já cobre) |
| `release-v0.5.2` | Release | Versão antiga lançada | **REMOVER** (Changelog já cobre) |
| `release-v0.5.3` | Release | Versão atual lançada | **REMOVER** (Changelog já cobre) |
| `release-v0.5.3-rc1` | Release | RC intermediário | **REMOVER** (Changelog já cobre) |
| `sqlite-engine-fast-file-joins` | Feature | 100% Implementado / Mergeado | **CONSOLIDAR & ARQUIVAR** (`sdd/archive/`) |
| `typed-connections-by-category` | Feature | 100% Implementado / Mergeado | **CONSOLIDAR & ARQUIVAR** (`sdd/archive/`) |
| `workspace-v2-auto-upgrader` | Migration | 100% Implementado / Mergeado | **CONSOLIDAR & ARQUIVAR** (`sdd/archive/`) |
| `xml-and-json-data-sources` | Feature | 100% Implementado / Mergeado | **CONSOLIDAR & ARQUIVAR** (`sdd/archive/`) |

#### Justificativa Técnica:
- Manter 23 mudanças antigas misturadas na pasta `changes/` viola o ciclo de vida SDD e impede que qualquer desenvolvedor veja rapidamente o que está sendo construído no momento.
- As mudanças de **release tags** (`release-v0.5.1`, `v0.5.2`, `v0.5.3`, `rc1`) e fixes efêmeros de CI/Dependabot têm conteúdo minúsculo (< 1 KB) repetitivo que já consta detalhadamente em `docs/CHANGELOG.md`. Devem ser **removidas**.
- As mudanças substanciais de arquitetura (pushdown, fast joins, conectores plugáveis) devem ser consolidadas e movidas para `docs/sdd/archive/` (ou `docs/archive/sdd-changes/`).

---

### 2.4. `docs/proposals/` (3 arquivos)

| Arquivo | Tamanho | Descrição | Status | Recomendação |
|---|---|---|---|---|
| `proposta-melhorias-qualidade-e-performance.md` | 16.8 KB | Proposta de melhoria contínua de performance e robustez | **Em Andamento / Ativa** | **MANTER (Ativa)** |
| `proposta-melhorias-arquiteturais-e-remocao-codigo-morto.md` | 26.3 KB | Proposta técnica de faxina de ~1.200 linhas de código morto do Avalonia | **Planejada / Ativa** | **MANTER (Ativa)** |
| `README.md` | 1.4 KB | Catálogo e governança das propostas técnicas | Organizacional | **MANTER (Ativo)** |

#### Justificativa Técnica:
- Ambos os documentos são propostas técnicas altamente qualificadas, recentes (Novembro de 2026), que guiam o roadmap imediato e médio prazo do projeto.
- Devem ser mantidas com prioridade máxima.

---

### 2.5. `docs/archive/` (4 arquivos)

| Arquivo | Tamanho | Descrição | Status | Recomendação |
|---|---|---|---|---|
| `fs-studio-plan.md` | 44.0 KB | Arquitetura original da aplicação Desktop Avalonia | Obsoleto | **MANTER NO ARQUIVO** ou **REMOVER** |
| `fs-studio-development-plan.md` | 25.7 KB | Plano de tarefas original da era desktop | Obsoleto | **REMOVER** |
| `f6-parity-checklist.md` | 9.3 KB | Checklist que comprovou paridade antes de deletar o desktop | Entregue | **MANTER NO ARQUIVO** |
| `README.md` | 1.3 KB | Contextualização do arquivo histórico | Organizacional | **MANTER** |

#### Justificativa Técnica:
- `fs-studio-development-plan.md` é uma lista de tarefas dos Milestones A a E do desktop Avalonia (criado no início de 2026). Ele não tem nenhuma utilidade prática hoje e pode ser seguramente removido sem qualquer perda.
- `f6-parity-checklist.md` comprova o gate formal de aposentadoria do desktop e pode ser mantido arquivado.
- Se a preferência for um repositório limpo focado exclusivamente no produto atual (extensão VS Code), todos esses arquivos podem ser removidos, pois o Git history preserva todo o histórico de commits.

---

## 3. Matriz Consolidada de Decisão

| Categoria | Ação Recomendada | Quantidade | Espaço Economizado / Impacto |
|---|---|---|---|
| **Propostas Técnicas Ativas** (`docs/proposals/`) | **MANTER** | 3 arquivos | Nenhum impacto negativo. Documentos vivos e necessários. |
| **Especificações Fundacionais** (`superpowers/specs/`) | **CONSOLIDAR EM ARQUIVO** | 5 arquivos | Preserva a memória técnica da arquitetura do VS Code em `archive/specs/`. |
| **Mudanças SDD Concluídas** (`sdd/changes/`) | **MOVER PARA `sdd/archive/`** | 17 pastas | Desobstrui a pasta de trabalho ativo do SDD mantendo rastreabilidade histórica. |
| **Mudanças SDD Redundantes / Releases** | **REMOVER** | 6 pastas | Elimina boilerplate de releases passadas e dependabot já cobertos no Changelog. |
| **Planos de Tarefas / Checklists Diários** (`superpowers/plans/`) | **REMOVER** | 15 arquivos | Elimina ~1 MB de arquivos `.plan` com checklists diários de tarefas já comitadas. |
| **Specs de Código Morto Desktop** (`superpowers/specs/`) | **REMOVER** | 6 arquivos | Elimina specs de código XAML/Avalonia já deletado do projeto. |
| **Tarefas Pontuais Encerradas** (`docs/tasks/`) | **REMOVER** (ou arquivar) | 6 arquivos | Elimina cadernos de notas temporários das fases F7–F9 (histórico coberto no `DEVLOG.md`). |
| **Planos de Tarefas da Era Avalonia** (`docs/archive/`) | **REMOVER** | 1 arquivo | Remove `fs-studio-development-plan.md` (tarefas antigas do desktop). |

---

## 4. Plano de Ação Sugerido (Passo a Passo Futuro)

Quando for aprovada a execução da limpeza pelo usuário, este é o roteiro cirúrgico recomendado:

1. **Fase 1 — Eliminação de Ruído e Código Morto (79 arquivos removidos)**:
   - Deletar `docs/superpowers/plans/` (15 arquivos de checklists diários).
   - Deletar as 6 specs da era Avalonia em `docs/superpowers/specs/` (deixando apenas as 5 specs de design da extensão VS Code).
   - Deletar as pastas de releases antigas em `docs/sdd/changes/` (`release-v0.5.1`, `v0.5.2`, `v0.5.3`, `v0.5.3-rc1`, `dependabot-*`, `fix-ci-*`).
   - Deletar `docs/archive/fs-studio-development-plan.md`.
   - Deletar as notas avulsas de `docs/tasks/` (`f7-*`, `f8-*`, `f9-*`, `fix-*`, `notebook-*`).

2. **Fase 2 — Consolidação e Arquivamento Limpo (28 arquivos reorganizados)**:
   - Mover as 5 specs relevantes de `docs/superpowers/specs/` para `docs/archive/specs/` e remover a pasta `docs/superpowers/`.
   - Mover as 17 mudanças concluídas de `docs/sdd/changes/` para `docs/sdd/archive/`.
   - Manter em `docs/sdd/changes/` apenas as mudanças ativas em andamento.

3. **Resultado Final**:
   A pasta `docs/` ficará com estrutura cristalina:
   - **Documentação Oficial**: Guias, Tutoriais, Specs de Linguagem, Benchmarks, Changelog, Devlog.
   - **`docs/proposals/`**: Propostas ativas de melhoria e arquitetura.
   - **`docs/sdd/changes/`**: Apenas as mudanças formais ativas da sprint atual.
   - **`docs/archive/`**: Repositório ordenado de memória técnica histórica (sem poluir o dia a dia).

