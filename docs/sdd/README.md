# `docs/sdd/` — Spec-Driven Development (SDD)

Este diretório contém os artefatos de especificação formal, propostas de mudança e tarefas governados pela metodologia **Spec-Driven Development (SDD)** do F# Query Studio.

---

## Estrutura do SDD

- `changes/`: Contém os pacotes de Delta Spec para cada ciclo de mudança do projeto. Cada subdiretório representa uma feature, melhoria ou refatoração estruturada em três partes:
  1. `proposal.md`: Contextualização, intenção de negócio e resumo da mudança (Intent Block).
  2. `delta-spec.md`: Especificação técnica canônica formalizada com sintaxe **GEARS** (`ADDED`, `MODIFIED`, `REMOVED`).
  3. `tasks.md`: Ledger de tarefas atômicas rastreadas a cada requisito da delta-spec.

---

## Mudanças Registradas em `changes/`

| Mudança | Escopo / Descrição |
|---|---|
| `advanced-dql-support-sqlserver-postgres-sqlite` | Expansão de suporte a consultas DQL avançadas (CTEs, subqueries, agregações de janela) em SQL Server, PostgreSQL e SQLite. |
| `backend-code-cleanup-and-modularization` | Limpeza de módulos obsoletos e modularização dos componentes do Worker backend. |
| `comprehensive-query-engine-features` | Expansão abrangente das capacidades da engine de consulta e documentação bilíngue. |
| `connector-modularization-and-plugin-architecture` | Arquitetura plugável e isolamento de dependências nos conectores de banco de dados. |
| `database-query-pushdown-and-performance` | Otimizações de tradução de LINQ para SQL com pushdown completo para o banco relacional. |
| `demo-data-100k-rows-and-expanded-columns` | Notebook de demonstração e geração de dados sintéticos de alta volumetria (100.000 linhas). |
| `dependabot-security-upgrades` | Atualizações de segurança de dependências npm e .NET. |
| `excel-schema-prefix-mapping` | Mapeamento de prefixos de esquema para abas de planilhas Excel. |
| `extension-publish-security-safeguards` | Salvaguardas no pipeline de empacotamento VSIX contra vazamento de código-fonte, símbolos de debug ou credenciais. |
| `fix-ci-windows-integration-test-timeout` | Correção de timeouts em testes de integração de longa duração no runner Windows do CI. |
| `fix-csv-export-and-working-directory-file-lock` | Correção de travas de arquivo no diretório de trabalho ao exportar resultados para CSV. |
| `fix-table-header-column-width-sync` | Sincronização e persistência de largura de colunas no cabeçalho da tabela de resultados. |
| `fix-table-scroll-excel-tree-option-query` | Correção de scroll virtual em grandes tabelas e consultas com `Option` em arquivos Excel. |
| `fsqs-folder-and-sqlserver-windows-auth` | Suporte a autenticação integrada do Windows em SQL Server e pasta `.fsqs` no workspace. |
| `local-workspace-project-support` | Suporte nativo a projetos locais de workspace com inicialização determinística. |
| `memory-and-autocomplete-performance-optimizations` | Otimização de consumo de memória no FSI e latência do provedor de autocomplete. |
| `release-v0.5.1`, `release-v0.5.2`, `release-v0.5.3`, `release-v0.5.3-rc1` | Ciclos formais de empacotamento, validação e lançamento de versões estáveis. |
| `sqlite-engine-fast-file-joins` | Joins rápidos entre arquivos locais (CSV, XLSX) acelerados pela engine SQLite in-memory/cache. |
| `typed-connections-by-category` | Categorização tipada de conexões no painel lateral do VS Code. |
| `workspace-v2-auto-upgrader` | Atualizador automático de workspaces legado (`workspace.json` v1 para schema v2 em `.fsqs`). |
| `xml-and-json-data-sources` | Suporte a leitura e consulta direta de fontes de dados semiestruturadas em XML e JSON. |

