# `docs/proposals/` — Propostas Técnicas e RFCs / Technical Proposals & RFCs

Este diretório centraliza propostas arquiteturais, RFCs (Request for Comments) e análises de melhorias técnicas de médio e longo prazo para o **F# Query Studio**.

---

## Propostas Registradas / Registered Proposals

| Proposta / Documento | Data / Status | Escopo / Descrição |
|---|---|---|
| [`proposta-melhorias-qualidade-e-performance.md`](proposta-melhorias-qualidade-e-performance.md) | Novembro 2026 — Em Andamento | Proposta técnica de melhorias de qualidade, resiliência e performance do F# Worker, cache de esquemas e engine analítica. |
| [`proposta-melhorias-arquiteturais-e-remocao-codigo-morto.md`](proposta-melhorias-arquiteturais-e-remocao-codigo-morto.md) | Novembro 2026 — Planejado | Análise integral pós-migração desktop → VS Code: inventário e remoção de código não utilizado da era Avalonia (~1.200 linhas) e modernização arquitetural. |

---

## Ciclo de Vida de uma Proposta

1. **Elaboração (`docs/proposals/`)**: Documento inicial contendo motivação, diagnóstico de código, cenários de falha e soluções propostas.
2. **Revisão e Discussão**: Alinhamento com os mantenedores e análise de impacto.
3. **Conversão em SDD Delta Specs**: Uma vez aprovada, a proposta é desdobrada em mudanças formais no diretório [`../sdd/changes/`](../sdd/) com `proposal.md`, `delta-spec.md` (requisitos GEARS) e `tasks.md`.

