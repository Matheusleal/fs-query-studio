# Histórico de Alterações (Changelog)

Todas as alterações notáveis da extensão **F# Query Studio** para o VS Code. O formato segue as diretrizes do [Keep a Changelog](https://keepachangelog.com/); os números de versão refletem o campo `version` do `package.json` e mapeiam para as tags Git `vscode-v<versão>`.

[🇺🇸 English](CHANGELOG.md) | [🇧🇷 Versão em Português](CHANGELOG_PT.md)

## [Não Lançado]

### Adicionado

- **Paridade Total de Mapeamento e Pushdown de Excel com CSV** — Planilhas Excel (`.xlsx`, `.xls`) agora seguem exatamente o mesmo comportamento de mapeamento e staging assíncrono antecipado dos arquivos CSV: todas as abas (`<Conn>_<Sheet>` e `<Conn>`) são pré-carregadas e indexadas no banco persistente `.fsqs/cache/engine.db` logo no momento da descoberta ou sincronização. Acessar `Connections.excel.<Conn>.<Sheet>`, `<Conn>.<Sheet>.rows()` ou `<Conn>.rows()` agora executa em 0ms direto no cache SQLite com pushdown completo e índices B-tree, eliminando o consumo excessivo de memória RAM e a lentidão no primeiro acesso que ocorria ao abrir o arquivo via ClosedXML em tempo de execução.
- **Observador de Arquivos em Tempo Real para `data_sources/`** — Monitoramento ativo do sistema de arquivos (`vscode.workspace.createFileSystemWatcher("**/data_sources/**")`) que detecta quando arquivos são criados, movidos, modificados ou excluídos dentro de `data_sources/`. Dispara automaticamente a importação com debounce, atualiza `workspace.json`, remove conexões órfãs quando arquivos são excluídos e sincroniza o cache SQLite persistente com feedback visual na barra de status.
- **Auto-Descoberta na Inicialização** — Ao ativar a extensão, verifica automaticamente as pastas do workspace em busca de `data_sources/` e executa a descoberta inicial e aquecimento de cache em segundo plano.

## [0.4.0-rc3] — 2026-11-02

### Adicionado

- **Joins Relacionais Multi-Propriedades de Alta Performance** — Joins entre arquivos diferentes utilizando chaves compostas (`on ((a.p1, a.p2) = (b.p1, b.p2))` e LINQ `SelectMany` com predicados compostos) rodam direto no SQLite em subsegundos (~18 ms para 5.000×40 registros) sem explosões de consumo de memória, com apoio de índices B-tree compostos automáticos.
- **Descoberta Automática de Arquivos via Pasta `data_sources/`** — Adicionado o comando `fs-query-studio.autoImportFolder` e botão na barra de ferramentas para auto-detectar e registrar arquivos compatíveis (`.csv`, `.xlsx`, `.db`, `.sqlite`) contidos na pasta `data_sources/`.
- **Sincronização Assíncrona de Cache com Feedback Visual** — Item animado na barra de status do VS Code (`$(sync~spin) Sincronizando fontes de dados...` ➔ `$(database) Fontes de dados prontas`) e comando dedicado `F# Query Studio: Sync Data Sources Cache` para carregar arquivos planos no cache em segundo plano com validação de timestamp (`mtime`).
- **Inspeção de Consulta Pré-Execução (`ResultKind.toRawSql`)** — Métodos `ResultKind.toRawSql`, `Connections.toRawSql` e `.ToRawSql()` permitindo ao usuário inspecionar o SQL e lista de parâmetros (`-- Parameters: ...`) gerados para qualquer query pushdown antes de enviá-la ao banco de dados.
- **Notebooks de Demonstração Expandidos** — Inclusão da célula 7.1 demonstrando joins multi-propriedades com pushdown e inspeção de SQL com `toRawSql`, além de planilhas Excel (`.xlsx`) e API REST do GitHub em `sample_demo.fsqnb` e `sample_demo_pt.fsqnb`.
- **Documentação Bilíngue Completa** — Paridade 100% de documentação em inglês e português em todos os guias de usuário, tutoriais, benchmarks e changelogs.

## [0.4.0-rc2] — 2026-11-02

### Adicionado

- **Pushdown Universal de SQL (Estilo EF com Projeção e Joins Relacionais)** — Pushdown completo de expressões de projeção `Select`, acesso a membros, operações aritméticas, concatenação de texto (`||` / `+`) e `Join` relacional entre SQLite, PostgreSQL e SQL Server, evitando o tráfego excessivo de linhas para a memória do cliente e eliminando o `SELECT *`.
- **Mapeador Dinâmico de Registros Projetados** — Mapeamento dinâmico de registros anônimos e tipos projetados (`'TResult`) a partir das colunas do leitor SQL por meio de delegados de expressão compilados.
- **Engine Embutida de Staging SQLite (`.fsqs/cache/engine.db`)** — Engine de alta performance para arquivos CSV e Excel com ingestão em lote em transação única, modo WAL, validação inteligente de hash/mtime (recarga em 0ms quando em cache) e criação automática de índices B-tree nas chaves `Id` e `*_id`.
- **Ultra-Fast Cross-File Joins** — Joins relacionais complexos entre múltiplos arquivos em conjuntos de dados com mais de 200.000 linhas rodam nativamente no SQLite em subsegundos (~768 ms).
- **Execução Direta de SQL na Engine do Workspace** — Expostos `Connections.queryEngine(sqlText, ?parameters)` e `Connections.sql.query(sqlText, ?parameters)` retornando resultados tabulares interativos.
- **Geração Limpa de Entidades** — `DatabaseConnection.GenerateEntities` gera referências limpas a tabelas dinâmicas (`conn.Table<...>("TableName")`) em vez de consultas estáticas `SELECT *`.

### Corrigido

- **Resolução de Caminhos Relativos de Arquivos** — Resolução automática de caminhos relativos para conexões baseadas em arquivos locais (SQLite, CSV, Excel) a partir da pasta raiz do workspace.

## [0.4.0-rc1] — 2026-10-30

### Adicionado

- **Arquitetura Modular de Conectores** — Separação modular de conectores no backend e frontend.
- **Suporte a Novas Fontes nos Notebooks de Demonstração** — Demonstração integrada de Excel `.xlsx` e REST HTTPS do GitHub.
- **Watcher Externo de Testes com Timeout** — Scripts `watch-tests.ps1`, `run-all-tests.ps1` e `run-all-tests.sh` garantindo término limpo e controle de travamento em suites de teste.

### Corrigido

- **Preservação de Encoding UTF-8** — UTF-8 sem BOM em streams NDJSON eliminando problemas de caracteres acentuados (mojibake).
- **Auto-Cura e Deduplicação de Dados de Exemplo** — Tratamento de arquivos vazios e recarregamento síncrono de entidades FSI.

## [0.3.2] — 2026-10-28

### Removido

- **Limpeza de Dependências Não Utilizadas** — Remoção de pacotes desnecessários e atualização para arquitetura FSI autônoma.

## [0.3.1] — 2026-10-28

### Corrigido

- **Links da Documentação de Benchmark** — Inclusão de `BENCHMARK_REPORT.md` e `BENCHMARK_REPORT_PT.md` diretamente no pacote da extensão para navegação offline.

## [0.3.0] — 2026-10-28

### Adicionado

- **API de Conexões Categorizadas por Tipo** — Isolamento sob `Connections.sql`, `Connections.csv`, `Connections.excel` e `Connections.rest`.
- **Badges de Tipo Alinhados à Direita no Autocomplete** — Exibição do tipo real nas sugestões do IntelliSense.
- **Progresso de Mapeamento de Entidades na Barra de Status** — Feedback visual durante geração de tipos F#.
