# `docs/archive/` — Arquivo Histórico / Historical Archive

Este diretório preserva documentos históricos, especificações de design fundacionais e registros de transição arquitetural do **F# Query Studio**.

> [!NOTE]
> Esses arquivos são mantidos exclusivamente como registro histórico de decisões de arquitetura e evolução da base de código. Eles **não** são mantidos em sincronia com as versões atuais do software.

---

## 1. Transição Desktop Avalonia → VS Code

| Arquivo | Descrição |
|---|---|
| [`fs-studio-plan.md`](fs-studio-plan.md) | Especificação de arquitetura e protocolo da aplicação desktop original em Avalonia UI. Supersedida pela extensão VS Code. |
| [`f6-parity-checklist.md`](f6-parity-checklist.md) | Checklist ponto a ponto comprovando a paridade funcional entre a extensão VS Code e a aplicação desktop antes da descontinuação definitiva do Avalonia. |

---

## 2. Especificações de Design da Migração VS Code (`specs/`)

Especificações técnicas fundacionais produzidas durante a construção da arquitetura atual da extensão VS Code:

| Arquivo | Fase / Tópico | Descrição |
|---|---|---|
| [`specs/2026-09-03-vscode-extension-migration-design.md`](specs/2026-09-03-vscode-extension-migration-design.md) | Fundação | **Documento Mestre de Migração** — Arquitetura de worker .NET sobre stdio NDJSON, LSP e extensão VS Code. |
| [`specs/2026-09-06-vscode-extension-f4-cell-intelligence-design.md`](specs/2026-09-06-vscode-extension-f4-cell-intelligence-design.md) | Fase F4 | Design do roteamento de inteligência de linguagem F# (FSAC) para células de notebooks. |
| [`specs/2026-09-07-vscode-extension-f5-connections-design.md`](specs/2026-09-07-vscode-extension-f5-connections-design.md) | Fase F5 | Design da árvore de conexões no painel lateral e canal reverso de segredos. |
| [`specs/2026-09-07-vscode-extension-f6-parity-retire-design.md`](specs/2026-09-07-vscode-extension-f6-parity-retire-design.md) | Fase F6 | Design e critérios formais para aposentadoria do host desktop. |
| [`specs/2026-09-08-notebook-tail-value-results-design.md`](specs/2026-09-08-notebook-tail-value-results-design.md) | Resultados | Design da captura e projeção automática da última expressão avaliada na célula. |

---

Para a documentação atual e ativa do projeto, consulte [`../README.md`](../README.md) e o [`../USER_GUIDE.md`](../USER_GUIDE.md).
