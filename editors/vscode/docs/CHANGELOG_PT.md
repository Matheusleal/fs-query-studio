# Histórico de Alterações (Changelog)

Todas as alterações notáveis da extensão **F# Query Studio** para o VS Code. O formato segue as diretrizes do [Keep a Changelog](https://keepachangelog.com/); os números de versão refletem o campo `version` do `package.json` e mapeiam para as tags Git `vscode-v<versão>`.

[🇺🇸 English](CHANGELOG.md) | [🇧🇷 Versão em Português](CHANGELOG_PT.md)

## [Não Lançado]

## [Unreleased]

## [0.5.0] — 2026-11-06

### Adicionado

- **Conector Nativo de Fontes de Dados JSON** — Suporte completo para documentos locais `.json` (`FSharpQueryStudio.Connectors.Json`) utilizando `System.Text.Json`. Inspeciona automaticamente coleções de array ou propriedades de objetos em nível raiz, realizando o staging em SQLite para consultas LINQ ultra-rápidas com SQL pushdown.
- **Conector Nativo de Fontes de Dados XML** — Suporte completo para documentos locais `.xml` (`FSharpQueryStudio.Connectors.Xml`) utilizando `System.Xml.Linq` com proteção total contra XXE (`DtdProcessing.Prohibit`). Mapeia automaticamente elementos recorrentes como tabelas relacionais consultáveis.
- **Descoberta de Esquema com Sub-Propriedades no Estilo Excel** — Arquivos tabulares multi-coleção expõem sub-propriedades fortemente tipadas no F# interativo (`Connections.json.<Conn>.<Tabela>`, `Connections.xml.<Conn>.<Tabela>` e `Connections.excel.<Planilha>.<Aba>`), respaldadas por tabelas de staging no SQLite (`<Conn>_<Tabela>`).
- **Auto-Importação de JSON e XML em `data_sources/`** — Arquivos `.json` e `.xml` adicionados à pasta `data_sources/` são detectados em tempo real, registrados nas conexões do workspace e pré-carregados em background.
- **SQL Pushdown por Aba em Planilhas Excel Multi-Aba** — Resolução precisa do nome da aba no motor de SQL pushdown para pastas de trabalho Excel com múltiplas abas.
- **Conjuntos de Dados de Amostra CommerceOps, Product Catalog e Logistics Hub** — Notebooks de exemplo (`sample_demo.fsqnb` e `sample_demo_pt.fsqnb`) atualizados com consultas interativas a Excel multi-aba, catálogo de produtos em JSON e hub de logística em XML.

### Corrigido

- **Deadlock de Carregamento Dinâmico de Assemblies no FSI** — Pré-carregamento explícito de todos os assemblies companion do estúdio (`FSharpQueryStudio.*.dll`) e `FSharpQueryStudio.Security.dll` nas diretivas `#r` do `FsiSession`, prevenindo travamentos de recursão de locks em `TcImports.ImplicitLoadIfAllowed`.
- **Sincronização do Staging Tabular em Segundo Plano** — Implementado `waitForPendingSync()` para assegurar que tarefas de staging assíncrono finalizem antes de reinicializações de conexão ou testes automatizados.

## [0.4.0] — 2026-11-03

### Adicionado

- **Arquitetura Modular de Conectores** — Desacoplamento completo dos conectores em projetos plugáveis especializados (`Abstractions`, `Csv`, `Excel`, `Sqlite`, `PostgreSql`, `SqlServer`, `Rest`). Interface orientada a esquemas que renderiza formulários dinâmicos de conexão com base nas capacidades declaradas em tempo de execução.
- **Motor Universal de SQL Pushdown** — Tradução nativa de alta performance de consultas LINQ para SQL com suporte a `where`, `select`, `sortBy`/`thenBy`, `take`/`skip`, `join`, `groupBy`, agregações (`count`, `sum`, `avg`, `min`, `max`), funções escalares (`Substring`, `Replace`, `Math`, `DateTime`), formatação de texto/datas via `.ToString()` e pivot.
- **Motor de Staging Persistente em SQLite** — Pré-carregamento e indexação B-tree composta de arquivos CSV e planilhas Excel (`.xlsx`) em `.fsqs/cache/engine.db`, viabilizando joins multi-fonte entre 3 ou mais origens sem explosão de consumo de memória RAM.
- **Controle de Ciclo de Vida de Watchers e Worker em Segundo Plano** — Botões de Pausa e Retomada na barra de ferramentas de Conexões e na barra de status para liberação instantânea de 100% de CPU e memória; inicialização sob demanda do observador de `data_sources/`.
- **Auto-Descoberta e Observador em Tempo Real de `data_sources/`** — Detecção instantânea e sincronização de arquivos na pasta de fontes de dados do projeto.
- **Notebooks de Demonstração Bilíngues de Ponta a Ponta** — Notebooks interativos prontos para uso (`sample_demo.fsqnb` e `sample_demo_pt.fsqnb`) abrangendo SQLite, CSV, Excel e REST seguro via GitHub.
- **Preservação da Ordem Declarada de Colunas** — A ordem de propriedades em registros anônimos e projeções explícitas é mantida com rigor na exibição de tabelas e na exportação CSV.

### Corrigido

- **Picos de Memória e Timeouts em Joins de Arquivos** — Eliminação de loops aninhados em memória através do pushdown nativo para o motor SQLite.
- **Desalinhamento de Colunas em Projeções Customizadas** — Resolução do mapeamento posicional de colunas em expressões traduzidas.
- **Compatibilidade Multiplataforma** — Tratamento robusto de URIs de arquivo entre Windows, Linux e macOS.

## [0.4.0-rc6] — 2026-11-03

### Adicionado

- **Preservação da Ordem de Colunas na Tabela e Exportação CSV** — A grade de visualização de resultados, a exportação para CSV e o mapeamento de registros agora preservam com precisão a ordem declarada das propriedades em anonymous records e projeções explícitas (`select {| A = ...; B = ... |}`), eliminando a reordenação alfabética compulsória.
- **Pushdown de Formatação de Texto e Datas (`.ToString`)** — Suporte a tradução nativa em SQL pushdown para `.ToString()` (`CAST` para texto) e `.ToString("formato")` com máscaras de data/hora (ex.: `"dd-MM-yyyy"`, `"yyyy-MM-dd HH:mm:ss"`), traduzidas para `strftime` no SQLite, `FORMAT` no SQL Server e `to_char` no PostgreSQL.
- **Funções Escalares e Propriedades de Data Expandidas no Pushdown** — Suporte nativo a `String.Substring`, `String.Replace`, `Math.Abs`, `Math.Round` e propriedades de componentes de `DateTime`/`DateOnly` (`Year`, `Month`, `Day`, `Date`, `Hour`, `Minute`, `Second`).

### Corrigido

- **Desalinhamento de Valores de Colunas em Projeções Customizadas** — Corrigida anomalia onde expressões de projeção não traduzidas faziam o mapeador de registros recorrer ao índice posicional bruto das colunas da tabela de origem, causando inversão e desalinhamento entre o cabeçalho e os valores das células.

## [0.4.0-rc5] — 2026-11-02

### Adicionado

- **Pushdown Nativo de Joins Multi-Tabelas (3+ Fontes)** — Resolução e mapeamento completo de aliases de tabelas (`_t0`, `_t1`, `_t2`, etc.) em consultas LINQ com múltiplos joins entre 3 ou mais fontes (CSVs + planilhas Excel) direto no SQLite. Elimina loops lentos em memória (`Cartesian products`) e tempos de execução excessivos.
- **Controle de Pausa/Play de Watchers e Workers** — Adicionados botões dedicados de Pausa (`$(debug-pause)`) e Retomar/Play (`$(play)`) na barra de ferramentas da visão de Conexões e na barra de status, acompanhados dos comandos `fs-query-studio.pauseWatchersAndWorkers`, `fs-query-studio.resumeWatchersAndWorkers` e `fs-query-studio.togglePauseResume`. Ao pausar, o processo do worker F# em segundo plano é encerrado e os observadores de arquivo são desativados, liberando imediatamente 100% da memória RAM e CPU em segundo plano até a retomada ou execução sob demanda de consultas.
- **Inicialização Sob Demanda da Detecção em `data_sources/`** — O observador em tempo real da pasta `data_sources/` agora é inicializado estritamente após o usuário selecionar a opção de Auto-Import no menu principal da visão de Conexões, na tela de boas-vindas ou na Paleta de Comandos. Abrir pastas ou projetos no VS Code não mais inicia workers em segundo plano nem consome recursos enquanto a extensão não estiver em uso ativo.
- **Documentação Abrangente de Mapeamentos e Fórmulas** — Inclusão de guias práticos bilíngues com exemplos de casos reais para sobrescrita de tipos (`columnTypes`) e catálogo de fórmulas de higienização (`columnTransforms`) em todos os manuais (USER_GUIDE, TUTORIAL e READMEs).

### Corrigido

- **Remapeamento Redundante de Entidades a Cada Consulta** — Corrigida a condição em `needsSynchronousGeneration` que forçava regeneração síncrona contínua e releitura de planilhas Excel pelo ClosedXML a cada execução de célula.
- **Exibição Explícita da Causa Raiz de Erros** — Desempacotamento de exceções encapsuladas (`TargetInvocationException` e `AggregateException`) nas projeções de erro, exibindo a mensagem e detalhes reais do SQLite/compilador em vez de mensagens genéricas.

## [0.4.0-rc4] — 2026-11-02

### Adicionado

- **Inferência Precisa de Tipos Tabulares na Árvore & Entidades** — Esquemas de Excel (`.xlsx`) e CSV agora inferem automaticamente tipos .NET/F# concretos (`int`, `decimal`, `double`, `bool`, `DateTime`) por amostragem das linhas, eliminando o mapeamento genérico fixo para `string`.
- **Sobrescrita Explícita de Tipos de Coluna (`columnTypes`)** — Usuários podem declarar explicitamente tipos customizados para colunas via configuração ou formulário (ex: `UnitPrice=decimal, Quantity=int, Active=bool` ou objeto JSON), respeitados na descoberta de esquema, geração de entidades e ingestão no SQLite.
- **Fórmulas e Transformações de Colunas (`columnTransforms`)** — Avaliação de fórmulas com suporte a `replace('de', 'para')`, substituições encadeadas (ex: `replace('.', '').replace(',', '.')` para moedas/valores brasileiros), `trim`, `multiply(fator)` e `divide(divisor)` para higienização dos dados antes da conversão de tipo.
- **Paridade Total de Mapeamento e Pushdown de Excel com CSV** — Planilhas Excel (`.xlsx`, `.xls`) agora seguem exatamente o mesmo comportamento de mapeamento e staging assíncrono antecipado dos arquivos CSV: todas as abas (`<Conn>_<Sheet>` e `<Conn>`) são pré-carregadas e indexadas no banco persistente `.fsqs/cache/engine.db` logo no momento da descoberta ou sincronização. Acessar `Connections.excel.<Conn>.<Sheet>`, `<Conn>.<Sheet>.rows()` ou `<Conn>.rows()` agora executa em 0ms direto no cache SQLite com pushdown completo e índices B-tree, eliminando o consumo excessivo de memória RAM e a lentidão no primeiro acesso que ocorria ao abrir o arquivo via ClosedXML em tempo de execução.
- **Observador de Arquivos em Tempo Real para `data_sources/`** — Monitoramento ativo do sistema de arquivos (`vscode.workspace.createFileSystemWatcher("**/data_sources/**")`) que detecta quando arquivos são criados, movidos, modificados ou excluídos dentro de `data_sources/`. Dispara automaticamente a importação com debounce, atualiza `workspace.json`, remove conexões órfãs quando arquivos são excluídos e sincroniza o cache SQLite persistente com feedback visual na barra de status.
- **Auto-Descoberta na Inicialização** — Ao ativar a extensão, verifica automaticamente as pastas do workspace em busca de `data_sources/` e executa a descoberta inicial e aquecimento de cache em segundo plano.

### Corrigido

- **Garantia de Precisão Decimal & Prevenção de Truncamento** — Corrigida a perda de casas decimais onde números com separador `.` eram inadvertidamente arredondados ou convertidos para inteiros no SQLite; valores fracionários agora são preservados integralmente como `NUMERIC` no SQLite e `decimal`/`double` no F#.

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
