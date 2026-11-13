# `docs/sdd/` — Spec-Driven Development (SDD)

Este diretório contém os artefatos de especificação formal, propostas de mudança e tarefas governados pela metodologia **Spec-Driven Development (SDD)** do F# Query Studio.

---

## Estrutura do SDD

- **`changes/`**: Pacotes de Delta Spec **ativos em desenvolvimento**. Devem conter apenas as mudanças da sprint ou branch em andamento.
- **`archive/`**: Pacotes de Delta Spec **concluídos, testados e consolidados no código-fonte**. Preservados para rastreabilidade formal de decisões e requisitos.

Cada pacote de mudança (seja em `changes/` ou `archive/`) é estruturado em três partes canônicas:
1. `proposal.md`: Contextualização de negócio, intenção e motivação da mudança (Intent Block).
2. `delta-spec.md`: Especificação técnica canônica formalizada em sintaxe **GEARS** (`ADDED`, `MODIFIED`, `REMOVED`).
3. `tasks.md`: Ledger de tarefas atômicas estritamente rastreadas aos requisitos da delta-spec.

---

## Mudanças Ativas (`changes/`)

| Mudança | Status | Escopo / Descrição |
|---|---|---|
| [`advanced-dql-support-sqlserver-postgres-sqlite`](changes/advanced-dql-support-sqlserver-postgres-sqlite/) | **Em Andamento** | Expansão de suporte a consultas DQL avançadas (CTEs, subqueries, agregações de janela) em SQL Server, PostgreSQL e SQLite. |

---

## Mudanças Arquivadas e Consolidadas (`archive/`)

As mudanças abaixo foram totalmente implementadas, cobertas por testes automatizados e integradas à base de código principal:

| Mudança Arquivada | Escopo / Descrição |
|---|---|
| [`backend-code-cleanup-and-modularization`](archive/backend-code-cleanup-and-modularization/) | Limpeza de módulos obsoletos e modularização dos componentes do Worker backend. |
| [`comprehensive-query-engine-features`](archive/comprehensive-query-engine-features/) | Expansão abrangente das capacidades da engine de consulta e documentação bilíngue. |
| [`connector-modularization-and-plugin-architecture`](archive/connector-modularization-and-plugin-architecture/) | Arquitetura plugável e isolamento de dependências nos conectores de banco de dados. |
| [`database-query-pushdown-and-performance`](archive/database-query-pushdown-and-performance/) | Otimizações de tradução de LINQ para SQL com pushdown completo para o banco relacional. |
| [`demo-data-100k-rows-and-expanded-columns`](archive/demo-data-100k-rows-and-expanded-columns/) | Notebook de demonstração e geração de dados sintéticos de alta volumetria (100.000 linhas). |
| [`excel-schema-prefix-mapping`](archive/excel-schema-prefix-mapping/) | Mapeamento de prefixos de esquema para abas de planilhas Excel. |
| [`extension-publish-security-safeguards`](archive/extension-publish-security-safeguards/) | Salvaguardas no pipeline de empacotamento VSIX contra vazamento de código-fonte, símbolos de debug ou credenciais. |
| [`fix-csv-export-and-working-directory-file-lock`](archive/fix-csv-export-and-working-directory-file-lock/) | Correção de travas de arquivo no diretório de trabalho ao exportar resultados para CSV. |
| [`fix-table-header-column-width-sync`](archive/fix-table-header-column-width-sync/) | Sincronização e persistência de largura de colunas no cabeçalho da tabela de resultados. |
| [`fix-table-scroll-excel-tree-option-query`](archive/fix-table-scroll-excel-tree-option-query/) | Correção de scroll virtual em grandes tabelas e consultas com `Option` em arquivos Excel. |
| [`fsqs-folder-and-sqlserver-windows-auth`](archive/fsqs-folder-and-sqlserver-windows-auth/) | Suporte a autenticação integrada do Windows em SQL Server e pasta `.fsqs` no workspace. |
| [`local-workspace-project-support`](archive/local-workspace-project-support/) | Suporte nativo a projetos locais de workspace com inicialização determinística. |
| [`memory-and-autocomplete-performance-optimizations`](archive/memory-and-autocomplete-performance-optimizations/) | Otimização de consumo de memória no FSI e latência do provedor de autocomplete. |
| [`sqlite-engine-fast-file-joins`](archive/sqlite-engine-fast-file-joins/) | Joins rápidos entre arquivos locais (CSV, XLSX) acelerados pela engine SQLite in-memory/cache. |
| [`typed-connections-by-category`](archive/typed-connections-by-category/) | Categorização tipada de conexões no painel lateral do VS Code. |
| [`workspace-v2-auto-upgrader`](archive/workspace-v2-auto-upgrader/) | Atualizador automático de workspaces legado (`workspace.json` v1 para schema v2 em `.fsqs`). |
| [`xml-and-json-data-sources`](archive/xml-and-json-data-sources/) | Suporte a leitura e consulta direta de fontes de dados semiestruturadas em XML e JSON. |
