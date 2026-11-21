# Histórico de Alterações (Changelog)

Todas as alterações notáveis da extensão **F# Query Studio** para o VS Code. O formato segue as diretrizes do [Keep a Changelog](https://keepachangelog.com/); os números de versão refletem o campo `version` do `package.json` e mapeiam para as tags Git `vscode-v<versão>`.

[🇺🇸 English](CHANGELOG.md) | [🇧🇷 Versão em Português](CHANGELOG_PT.md)

## [Não Lançado]

### Adicionado

- **Condições Dedicadas `isNotNull`, `isNotEmpty` e `isNotNullOrEmpty`** — Filtragem de valores não nulos e não vazios em cláusulas `where (...)` no `fsql` com pushdown para SQL nativo (`IS NOT NULL` e `<> ''` / `LENGTH(...) > 0` no Oracle).
- **Descoberta Multi-Database e Mapeamento em Árvore (`Conexão -> Database -> [Schema ->] Tabela -> Coluna`)** — Conectores SQL de servidor (`SqlServer`, `PostgreSql`, `MySql`, `MariaDb`, `Oracle`) agora descobrem todos os databases acessíveis na instância e os mapeiam em estrutura de árvore tanto na TreeView do VS Code (`tree.ts`) quanto nas entidades F# geradas (`Connections.sql.<Conn>.<Database>.[<Schema>.]<Table>`), mantendo atalhos na raiz (`Connections.sql.<Conn>.<Table>`) para o database padrão.
- **Consultas SQLite Diretas em Conexões CSV, XML, JSON e Excel Mapeadas** — Adicionados `.rawQuery`, `.query<'T>` e `.Execute` diretamente em `Connections.csv.<Conn>`, `Connections.xml.<Conn>`, `Connections.json.<Conn>` e `Connections.excel.<Conn>`, com auto-staging sob demanda em `.fsqs/cache/engine.db` e `VIEW`s de alias no SQLite (`<FileBaseName>`, `<TableName>`, `<Conn>_<TableName>`, `<Conn>`).

### Corrigido

- **Tradução de `not` e `isNull` na Cláusula `where`** — Corrigido `ExpressionPushdownTranslator` (`SqlPushdownQuery.fs`) para traduzir `Operators.not` (`op_BooleanNot`), `Operators.isNull`, `Object.ReferenceEquals`, `BlockExpression` e `ConditionalExpression` booleanas para que expressões como `where (not (isNull tabela.Coluna))` gerem pushdown SQL nativo (`WHERE (tabela.Coluna IS NOT NULL)`).
- **Queries Diretas no SQLite para Fontes XML e CSV Mapeadas** — Corrigido erro `no such table` ao executar `Connections.sql.rawQuery`, `Connections.sql.query`, `Connections.queryEngine` ou `Connections.sql.<SqliteConn>.rawQuery` sobre tabelas mapeadas de CSV e XML antes de uma consulta LINQ realizar o staging.

## [0.7.0] — 2026-11-16

### Adicionado

- **Novos Conectores de Bancos de Dados Relacionais: MySQL, MariaDB e Oracle** — Suporte de primeira classe para bancos de dados SQL corporativos e open-source:
  - **Conectores MySQL e MariaDB (`FSharpQueryStudio.Connectors.MySql` & `FSharpQueryStudio.Connectors.MariaDb`)**: Conectividade ADO.NET de alto desempenho com `MySqlConnector`, introspecção de schemas, delimitadores em backticks, paginação com `LIMIT`/`OFFSET` e pushdown SQL completo.
  - **Conector Oracle Database (`FSharpQueryStudio.Connectors.Oracle`)**: Suporte nativo ao Oracle 19c, 21c e 23c (incluindo Oracle Database Free / 23ai) via `Oracle.ManagedDataAccess.Core`. Suporte a schemas multi-tenant PDB (`FREEPDB1`), escape de identificadores em aspas duplas, paginação `OFFSET n ROWS FETCH NEXT m ROWS ONLY` e filtragem automática de tabelas de sistema internas (`$`, `#`, `BIN$`, `MVIEW$`).
- **Gerador Multi-Engine de Demonstrações & Ambiente Docker Compose** — Criação instantânea de ambiente de testes em um clique:
  - Comando `createAdvancedSamples` expandido com seleção granular de banco ou `"all"` engines (`PostgreSQL`, `SQL Server`, `MySQL`, `MariaDB`, `Oracle`).
  - Geração automática de `docker-compose.fsqs.yml` com configurações prontas para produção (`postgres:16-alpine`, `mcr.microsoft.com/mssql/server:2022-latest`, `mysql:8.4`, `mariadb:11.4`, `gvenzl/oracle-free:23-slim`).
  - Scripts SQL de inicialização específicos por motor (`init_postgres.sql`, `init_sqlserver.sql`, `init_mysql.sql`, `init_mariadb.sql`, `init_oracle.sql`) criando tabelas e dados de exemplo.
  - 12 notebooks completos de demonstração (6 em inglês: `sample_demo.fsqnb`, `sample_postgresql.fsqnb`, `sample_sqlserver.fsqnb`, `sample_mysql.fsqnb`, `sample_mariadb.fsqnb`, `sample_oracle.fsqnb`, e 6 equivalentes em português: `*_pt.fsqnb`).
- **Suíte Automatizada de Testes de Amostras de Ponta a Ponta (`npm run test:samples`)** — Runner E2E completo (`editors/vscode/test_all_samples_e2e.mjs`):
  - Execução em diretório temporário isolado com ciclo de vida completo de containers Docker.
  - Health checks automáticos nos bancos, criação e inicialização de schemas, e validação de 100% das células (190+ células) em todos os 12 cadernos (em inglês e português).
  - Limpeza compulsória com `docker compose down -v` e remoção do diretório temporário ao final.
- **Validação Obrigatória de Samples no CI e Diretrizes de IA** — Regras de entrega canônicas registradas no `AGENTS.md` exigindo build Release validado (`dotnet fsi .\build.fsx release`) e 100% de sucesso nos testes E2E antes da abertura de qualquer Pull Request.

### Corrigido

- **Higienização de Identificadores para Entidades F# Geradas** — Sanitização Unicode (`[^\p{L}0-9_]`) no gerador de código, evitando nomes inválidos de módulos e tipos ao encontrar caracteres especiais em colunas ou tabelas.
- **Redução de Ruído de Catálogos Internos no Oracle** — Filtragem automática de tabelas de sistema e flashback contendo `$`, `#`, `BIN$` e `MVIEW$` durante a inspeção de esquema.
- **Resiliência no Timeout de Descoberta de Esquema** — Aumento do timeout de introspecção de 5.0s para 20.0s para suportar inicializações concorrentes de containers sob carga.


## [0.6.0] — 2026-11-15

### Adicionado

- **Common Table Expressions (CTEs) Modernas na CE** — Suporte nativo completo para declaração de CTEs diretamente dentro da computation expression `fsql { ... }`:
  - **CTEs Sem Nome (`withCte subquery`)**: Injetadas de forma transparente no plano SQL sem necessidade de registrar tabelas fictícias ou fornecer nomes explícitos.
  - **CTEs com Nome Explícito (`withCte "Nome" subquery`)**: Sobrecarga para consultas estruturadas com múltiplos blocos WITH.
  - **CTEs Recursivas em SQL Bruto (`withRecursiveCteSql "Nome" sql` e `withRecursiveCteSql sql`)**: Suporte nativo para consultas hierárquicas e de grafos com geração de `WITH RECURSIVE` no PostgreSQL e SQLite, e `WITH` no SQL Server.
  - **Composição Transparente de CTEs**: Consultas declaradas em variáveis separadas combinam suas CTEs automaticamente no `WITH` principal de nível superior sem duplicações de cláusulas.
- **Operações de Conjunto na CE (`union`, `unionAll`, `intersect`, `except`)** — Operações de conjunto escritas diretamente dentro de `fsql { ... }`, com paridade total via builder currificado (`fsql.union`), funções de módulo (`union`) e métodos de extensão em `IQueryable` (`.Union()`).
- **Joins Externos 100% Idiomáticos** — Sintaxe LINQ pura com `GroupJoin` e `DefaultIfEmpty()` para `fullOuterJoin` e `rightOuterJoin` diretamente na CE `fsql`, eliminando templates de joins em strings cruas e garantindo tipagem forte total.
- **Motor Avançado de DQL com Pushdown Universal** — Expansão do suporte em SQL Server, PostgreSQL e SQLite:
  - Pushdown de agregados em colunas (`count`, `sum`, `avg`, `min`, `max`) contornando a restrição T-SQL 412 do SQL Server.
  - Filtro dedicado `having` para agregações agrupadas.
  - Joins correlacionados por linha: `CROSS APPLY` (SQL Server), `CROSS JOIN LATERAL` (PostgreSQL) e fallback em memória transparente (SQLite).
  - Subqueries correlacionadas (`whereExists`, `whereNotExists`, subqueries escalares na projeção `select`).
  - Funções de janela analíticas (`fsql.window`, `rowNumber`, `rank`, `lag`, `lead`) com suporte a `OVER (PARTITION BY ... ORDER BY ...)`.
  - Navegação em dados semi-estruturados JSON (`fsql.jsonValue`) mapeada para extração nativa em cada dialeto (`JSON_VALUE`, `->>`, `json_extract`).
- **Diagnóstico de Plano de Execução ao Vivo e SQL Formatado** — Novas ferramentas de inspeção:
  - Operação customizada `explain` diretamente na CE.
  - Função `toRawQuery` gerando SQL formatado com indentação por dialeto e cabeçalho de documentação de parâmetros.
  - `explainQuery` e `.Explain()` para inspeção direta do plano de execução do banco de dados (`EXPLAIN QUERY PLAN`, `EXPLAIN`, `SET SHOWPLAN_TEXT`).
- **Gerador de Demos Unificado Multi-Banco com Seleção de Idioma** — Botão unificado na barra de ferramentas e de status (`$(database)`) e comando (`F# Query Studio: Create Sample Data & Demo Notebooks`) com seletor de engine (Original [SQLite, CSV, Excel, REST], PostgreSQL, SQL Server) e idioma opcional (English, Português, Both / Ambos), gerando notebooks de demonstração prontos para rodar com conexões pré-cadastradas.
- **Documentação Bilíngue e Especificação Canônica do FSQL** — Paridade 100% sincronizada entre inglês e português em tutoriais, manuais do usuário, especificações de linguagem e guia de boas práticas para agentes de IA (`FSQL_AI_AGENT_GUIDE.md` e `FSQL_AI_AGENT_GUIDE_PT.md`).

## [0.5.3] — 2026-11-11

### Corrigido

- **Resolução de Caminho na Exportação CSV** — Correção na exportação de resultados para CSV para resolver caminhos relativos de destino a partir do diretório raiz do workspace do notebook ativo, em vez do diretório do executável do worker. Criação automática de diretórios pais ausentes antes da escrita do arquivo.
- **Alinhamento do Diretório de Trabalho (`System.Environment.CurrentDirectory`)** — Ajuste do diretório de trabalho do worker diretamente para a raiz do workspace do documento de notebook ativo durante a avaliação de células, garantindo que operações de arquivo e resoluções relativas operem consistentemente no diretório do workspace.
- **Eliminação de Bloqueios de Arquivos Multi-Célula no Windows** — Resolução de locks de arquivo na execução sequencial de células: `PagingCursor` implementa `IDisposable` para liberar enumeradores, conectores tabulares (`Csv`, `Excel`, `Json`, `Xml`) e gerador de amostras utilizam `FileShare.Delete`, cursores e valores `IDisposable` superseded são proativamente descartados, e `SqliteConnection.ClearAllPools()` com `GC.WaitForPendingFinalizers()` libera handles de sistema operacional.
- **Otimização de Prefixo na Análise de Células de Notebook** — Correção em `buildAnalysisInput` para excluir células executadas após a célula sob foco (`executionOrder < targetOrder`), prevenindo avisos falsos de definição duplicada (`FS0037`) na navegação ou reavaliação de células.
- **Casos de Uso de Manipulação de Arquivos no Projeto Demo** — Inclusão das seções 15 e 16 em `sample_demo.fsqnb` e `sample_demo_pt.fsqnb` demonstrando streaming a partir de `data_sources/sales_feed.csv`, escrita via `File.Create`, descarte da stream e validação multi-célula de releitura e append livre de locks sem parênteses redundantes.
- **Normalização de URIs na Retenção de Resultados** — Normalização de representações de URI de documento (letras de unidade com percent-encoding versus decodificadas) no `ResultRetention.fs`, garantindo recuperação e exportação confiáveis de resultados independentemente das convenções de URI do cliente.
- **Prevenção de Locks no Conector SQLite** — Desativação do pool de conexões (`Pooling=false`) na string de conexão do SQLite e liberação imediata de recursos de reader, command e connection na execução de comandos DDL/DML sem colunas de retorno.
- **Referências Antecipadas de Assemblies no FSI** — Pré-passagem de referências de assemblies do studio e conectores diretamente via argumentos CLI (`-r:`) na inicialização do `FsiSession`, prevenindo travamentos e deadlocks por inversão de locks no `TcImports` do FCS em tempo de execução.

## [0.5.2] — 2026-11-10

### Corrigido

- **Mapeamento de Projeção Escalar Option** — Correção na projeção de colunas escalares opcionais sem `.Value` (ex.: `select l.Isins` retornando `None` em vez de `Some "..."`). Atualizados `CompiledRecordMapper.fs` e `SqlPushdownQueryable.fs` para direcionar tipos opcionais via `RecordReaders.ReadOption<'T>`, prevenindo `InvalidCastException` na conversão de DBNull ou valores escalares Option.
- **Destaque de Sintaxe para Expressões de Consulta F#** — Injeção de gramática TextMate para palavras-chave de query expressions (`query`, `select`, `where`, `join`, `on`, `equals`, `into`, `groupBy`, etc.), destacando-as consistentemente como palavras-chave de controle do F#.
- **Destaque de Interpolação de Strings e Preservação de Texto** — Destaque de sintaxe para strings interpoladas do F# (`$"..."`, `$@"..."`, `$"""..."""`), colorindo o `$` e as chaves `{...}` como tokens de interpolação em rosa enquanto preserva a cor nativa de strings do VS Code (`#CE9178`) para o conteúdo literal e as aspas.
- **Desambiguação de Módulos, Funções e Propriedades** — Escopo de módulos restrito a módulos reais conhecidos (`Connections`, `Results`, `Seq`, `List`, `Array`, `Option`, etc.), evitando que propriedades encadeadas (ex.: `Connections.excel.RegionalTargets.RegionalTargets`) sejam marcadas como módulos. Padronização de cores de funções para `Seq.length`, `Seq.Length`, `Seq.filter` e `.rawQuery(...)`.
- **Proteção Contra Injeção em Strings e Comentários** — Restrição da injeção TextMate com `-comment -string` para evitar vazamento de regras de query e membros para dentro de comentários ou strings SQL literais/multilinha (como `@"SELECT ..."`).

## [0.5.1] — 2026-11-09

### Adicionado

- **Datasets e Schemas de Benchmark com 100k+ Linhas** — Expansão dos notebooks de exemplo (`sample_demo.fsqnb` e `sample_demo_pt.fsqnb`) com schemas mais ricos e 100k+ linhas por fonte de dados (SQLite, CSV, Excel `.xlsx`, JSON e XML).
- **Workstation Garbage Collection nos Workers e FSAC** — Configuração explícita de Workstation GC (`ServerGarbageCollection=false`), Concurrent GC e Tiered PGO nos workers e no processo filho do FSAC (`DOTNET_GCServer=0`, `DOTNET_gcConcurrent=1`, `DOTNET_TieredPGO=1`), reduzindo drasticamente o consumo base de memória em máquinas multi-core.
- **Otimização de Autocomplete com I/O de Disco Quase Zero** — Cache de timestamp de modificação de arquivos para prontidão de entidades geradas (`Handlers.fs`, `EntityBackgroundGenerator.fs`, `FsiSession.fs`), eliminando leituras de disco e parsing de JSON repetidos a cada caractere digitado.
- **Deduplicação e Cache no Autocomplete de Conexões** — Cache de curta duração (TTL de 2s) para conexões registradas e checagem antecipada de cancelamento no provedor de autocompletion do VS Code, eliminando round-trips de IPC redundantes.

### Corrigido

- **Scroll Horizontal e Alinhamento de Colunas do Header** — Correção da dessincronização do scroll horizontal entre o cabeçalho e o corpo da tabela com especificação de trilhas CSS Grid unificadas (`--fsq-grid-cols` e `--fsq-grid-width`) e larguras proporcionais ao conteúdo.
- **Opacidade e Z-Index do Cabeçalho Fixo (Sticky Header)** — Substituição do fundo semitransparente por fundo sólido opaco e elevação de `z-index` (10), impedindo que linhas rolem por trás e se misturem visualmente com os nomes das colunas.
- **Sincronização Eager da Árvore de Conexões Excel** — Carregamento imediato de abas e mapeamentos de pastas de trabalho Excel na barra lateral sem depender de expansões lazy manuais.
- **Pushdown SQL para Propriedades Option (`.Value`)** — Correção na tradução de expressões LINQ SQL para propriedades anuláveis/opcionais que acessam `.Value` no filtro (ex.: `where (x.Coluna.Value = "valor")`), traduzindo para o nome real da coluna em vez do literal `Value`.
- **Retenção de Memória no Serviço de Linguagem** — Prunagem de diagnósticos e tarefas pendentes de versões antigas do documento no `FsacBridge.fs`, evitando acúmulo de instâncias ao longo da edição.
- **Poda de Capacidade de Buffers de Console e Histórico de Sessão** — Poda automática da capacidade do `StringBuilder` em `session.Output` e `session.Errors` para 4 KB após a execução de queries volumosas e controle do tamanho de `LastFragments` no `SessionStore.fs`.

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

- **Arquitetura Modular de Conectores (Backend & Frontend)** — Desacoplamento de conectores em módulos independentes e autocontidos (`FSharpQueryStudio.Connectors.Abstractions`, `.Sqlite`, `.SqlServer`, `.PostgreSql`, `.Csv`, `.Excel`, `.Rest`) no backend e módulos declarativos no frontend (`editors/vscode/src/connections/modules/*.ts`) registrados via `ConnectorRegistry` e `ConnectorFrontRegistry`.
- **Assistente Declarativo de Conexão Orientado a Esquemas (`connector/list`)** — Adicionado endpoint RPC `connector/list` expondo descritores de conectores e esquemas de campos de formulário para que a extensão VS Code renderize formulários dinâmicos de conexão, manuseio seguro de credenciais e regras de validação por conector.
- **Notebooks de Exemplo Multi-Fonte Expandidos (`sample_demo.fsqnb` & `sample_demo_pt.fsqnb`)** — Adicionadas conexões de **Excel (`.xlsx`)** multi-aba (`RegionalTargets`) e **API REST HTTPS do GitHub (`https://`)** (`GitHubFSharp`), além de células de consulta interativa LINQ (11 células de código em 6 fontes de dados: 2x SQLite, 2x CSV, Excel `.xlsx` e GitHub HTTPS REST).
- **Monitor Externo de Execução de Testes com Timeout** — Adicionados scripts `scripts/watch-tests.ps1`, `scripts/run-all-tests.ps1` e `scripts/run-all-tests.sh` aplicando timeout externo estrito de 5 minutos e encerramento da árvore de processos em todas as suítes de teste.

### Corrigido

- **Preservação de Codificação UTF-8 na Célula e Grade de Resultados** — Aplicação de UTF-8 sem BOM em streams NDJSON stdio no worker e processo host, preservando caracteres acentuados (ex: `Transações`, `São Paulo`) sem corrupção (mojibake).
- **Deduplicação, Auto-Cura e Recarga de Entidades FSI de Amostra** — Correção no registro duplicado de conexões no `workspace.json`, adição de auto-cura para arquivos de amostra zerados ou ausentes, aplicação de geração síncrona quando `GeneratedEntities.fs` está ausente ou defasado, e recarga automática via `#load` de entidades atualizadas em sessões FSI persistentes.
- **Ordenação de Campos no Esquema v2 do Workspace** — Posicionamento da propriedade `connections` como última chave em `.fsqs/workspace.json` para melhor legibilidade dos metadados de nível superior (`schemaVersion`, `extensionVersion`, `updatedAt`).

## [0.3.2] — 2026-10-28

### Removido

- **Limpeza de Dependências Não Utilizadas e Referências Externas** — Remoção da dependência não utilizada do pacote `SSH.NET` da suíte de testes de integração e do gerenciamento central de pacotes (`Directory.Packages.props`). Limpeza de referências obsoletas ao Ionide em toda a documentação para refletir o worker de serviço de linguagem integrado e autônomo do F# Query Studio.

## [0.3.1] — 2026-10-28

### Corrigido

- **Links do Relatório de Benchmark na Documentação** — Empacotamento de `BENCHMARK_REPORT.md` e `BENCHMARK_REPORT_PT.md` diretamente no pacote da extensão e atualização de links em `USER_GUIDE.md` e `USER_GUIDE_PT.md` para que a abertura do relatório a partir da pré-visualização de documentação no VS Code funcione diretamente sem erros de navegação.

## [0.3.0] — 2026-10-28

### Adicionado

- **API de Conexões Categorizadas por Tipo** — Substituição do vazamento global de conexões por categorias estritamente isoladas sob `Connections`:
  - `Connections.sql.<NomeDaConexao>.<Tabela>` para bancos de dados relacionais (SQLite, PostgreSQL, SQL Server, MySQL).
  - `Connections.sql.<NomeDaConexao>.rawQuery` com escopo restrito àquela conexão específica.
  - `Connections.csv.<NomeDaConexao>` para streams de linhas CSV fortemente tipadas e consultas LINQ diretas.
  - `Connections.excel.<NomeDaConexao>` para planilhas Excel.
  - `Connections.rest.<NomeDaConexao>` para endpoints de APIs REST.
  - Métodos legados (`connect`, `connectCsv`, `connectExcel`, `connectRest`) e `.asEntities` foram descontinuados.
- **Badges de Tipo Alinhados à Direita no Autocomplete** — Sugestões no dropdown de autocompletar agora exibem seu tipo exato (`int`, `string`, `bool`, `decimal`, `DateTime`, etc.) ou categoria alinhados à direita:
  - Campos e propriedades resolvem e exibem seu tipo real subjacente.
  - Métodos exibem `method`.
  - Conexões exibem a categoria do seu conector (`sql`, `csv`, `excel`, `rest`).
- **Progresso de Mapeamento de Entidades na Barra de Status** — Indicador animado na barra de status (`$(sync~spin) Mapeando entidades...`) durante a inspeção de esquemas e geração de tipos F# em segundo plano, fornecendo feedback visual claro ao atualizar conexões.
- **Resolução de Workspace de Células de Notebook no FSAC** — O motor de análise F# em segundo plano agora mapeia automaticamente os URIs virtuais de células de notebook (`vscode-notebook-cell:`) para a pasta do workspace e carrega `GeneratedEntities.fs` no FSAC, eliminando falsos positivos de sublinhados vermelhos e fornecendo IntelliSense completo e navegação Go to Definition (`F12`) entre células.

## [0.2.3] — 2026-10-27

### Melhorias

- **Formatação Rica dos Notebooks de Exemplo e Suporte Bilíngue** — Reformulação completa dos notebooks de exemplo gerados por `Create Sample Data` (`sample_demo.fsqnb` em inglês e `sample_demo_pt.fsqnb` em português):
  - Células markdown formatadas com badges visuais, tabelas de fontes de dados, atalhos de teclado e orientações.
  - Substituição de projeções de tupla por registros anônimos (`{| Id = ...; Nome = ... |}`) para que o renderizador de tabela interativa exiba cabeçalhos nomeados com ordenação, filtros e badges de tipo em vez de `Item1`, `Item2` genéricos.
  - Adição de exemplos de agregação analítica (`groupBy`, `sumBy`, `averageBy`) e consultas multi-fonte cruzando dados relacionais SQLite com transações em arquivos CSV planos.
  - Detecção automática de idioma que abre `sample_demo_pt.fsqnb` para usuários em português e `sample_demo.fsqnb` para usuários em inglês, criando ambos no workspace.

## [0.2.2] — 2026-10-27

### Corrigido

- **Geração de Código Multi-Fonte de Dados** — Correção na geração de entidades em segundo plano quando múltiplas fontes de dados do mesmo tipo (ex: dois arquivos CSV) são cadastradas. Nomes de módulos únicos e acessadores tipados são gerados, e `.asEntities` realiza pattern matching correto em todas as conexões registradas sem perder extensões ou gerar colisões de nomes.
- **Inferência de Tipo Decimal em CSV** — Colunas de arquivos planos com números decimais / ponto flutuante (ex: `355272835.53` ou `355272835,53`) agora são inferidas com precisão como `decimal` em vez de regredirem para `string`. Aspas e espaços em branco nas linhas de amostragem são removidos antes da inferência.
- **Parsing Numérico e Decimal Invariante de Cultura** — Adicionada conversão invariante de cultura em `Connections.convertValue` suportando separadores decimais com ponto (`.`) e vírgula (`,`) sob qualquer cultura de sistema (como `pt-BR`), resolvendo o erro `FormatException: Input string was not in a correct format`.
- **Sanitização de Nomes de Colunas** — Cabeçalhos de coluna vazios ou duplicados em arquivos CSV e planos são sanitizados automaticamente (`Column1`, `Column2`, sufixos de deduplicação) para evitar erros de sintaxe nos tipos de registro gerados em F#.

## [0.2.0] — 2026-10-27

### Adicionado

- **Benchmarks de Execução e Recursos** — Suíte dedicada de benchmark (`tests/FSharpQueryStudio.Benchmarks`) e testes automatizados de regressão medindo latência, tempo de CPU e alocações de GC desde a conexão até a execução em cenários de inicialização a frio, consultas a quente, escalabilidade de volume (100 a 50.000 linhas), consultas LINQ e arquivos CSV. Consulte `docs/BENCHMARK_REPORT_PT.md`.
- **Testes Abrangentes de Elementos de Interface** — Suíte completa de testes (`editors/vscode/src/renderer/ui-elements.test.ts` e `connections/ui-elements.test.ts`) cobrindo 100% dos componentes de UI: layout de grade, barra de ferramentas, visualizações segmentadas, contadores de linhas, cabeçalhos fixos, dimensionador de virtualização, estilos alternados de linhas, formatadores de células (`NULL`, numérico, string, booleano), inspetor de esquemas, paginação "carregar mais", banners de notificação e itens da árvore do Explorador de Conexões.
- **Comandos de Criação de Notebooks F# Query** — **F# Query Studio: New F# Query Notebook** (na Paleta de Comandos e em *Arquivo > Novo Arquivo…*) abre um novo notebook sem título com uma célula F# vazia. O menu de contexto do Explorer ganha a opção **New F# Query Notebook Here**, que grava e abre um arquivo `.fsqnb` real na pasta selecionada.
- **Descoberta Multi-Esquema e Views** — Descoberta expandida para PostgreSQL, SQL Server e SQLite incluindo esquemas de usuário (ex: `dbo`, esquemas customizados) e views, agrupando-os em namespaces limpos de esquemas na árvore de Conexões.
- **LINQ Fortemente Tipado e Gerador de Entidades em Segundo Plano** — Geração automática em segundo plano de tipos de registro tipados e contextos de consulta LINQ para bancos de dados e arquivos planos (CSV, Excel, JSON) com integração `SQLProvider`.
- **Autocomplete Contextual de Nomes de Conexão** — Autocompletar de nomes de conexão em `Connections.connect(...)`, `Connections.get(...)`, `Connections.query(...)` e conectores específicos (`sql`, `csv`, `excel`, `rest`), filtrados pelo tipo compatível de conector.
- **Go to Definition (`F12`)** — Navegação direta para definições entre células de notebook, arquivos externos e modelos de entidade gerados em segundo plano.
- **Sanitização Segura de Senhas** — Detecção e extração automática de senhas em texto puro de strings de conexão para o chaveiro seguro do sistema operacional (`SecretStorage`), substituídas por marcadores `{password}`.
- **Gerador de Dados de Amostra** — Geração em 1 clique de banco SQLite de exemplo (`sample_store.db`), feed CSV (`sales_feed.csv`) e notebook de demonstração interativo (`sample_demo.fsqnb`).
- **Destaque de Sintaxe para Expressões de Consulta LINQ** — Gramática de injeção TextMate e provedor de tokens semânticos para palavras-chave de consulta F# (`select`, `where`, `join`, `groupBy`, `sortBy`, etc.).
- **Documentação Bilíngue** — Manuais completos do usuário em inglês (`USER_GUIDE.md`) e português (`USER_GUIDE_PT.md`), com detecção automática de idioma e links oficiais da documentação F# da Microsoft.
- **Ícone da Extensão** — Ícone customizado com símbolo lambda de F# brilhante e emblema de cilindro holográfico de banco de dados.
- Scripts de compilação / empacotamento / publicação em `editors/vscode/scripts/` (`npm run build` / `package` / `publish` / `doctor`). O CI empacota o VSIX através do mesmo caminho `npm run package`.

### Alterado

- A grade de resultados agora é a visualização padrão para células que produzem tabelas. Anteriormente a grade ficava oculta no menu "Alterar apresentação" e a célula exibia apenas o eco `val … =` do FSI. Esse eco e saídas de `printf` agora são direcionados para a apresentação de dados brutos (`text/plain`).
- A grade de resultados foi reestilizada para seguir o design nativo de tabelas do VS Code: fonte padrão da UI, colunas numéricas alinhadas à direita, linhas zebradas, cabeçalho fixo contido, cartão arredondado, marcadores `NULL` sutis e aviso de "Nenhuma linha encontrada".

### Corrigido

- A saída padrão (stdout) e o resultado da célula agora são saídas separadas, garantindo que o resultado em tabela não seja suprimido por saídas de `printf` na mesma célula.
- O eco de declaração bruta do FSI agora é a visualização de fallback em texto, e não a padrão.

## [0.1.0] — 2026-10-27

Primeira versão empacotada da extensão — paridade de recursos com o aplicativo desktop Avalonia descontinuado para o fluxo de consultas e execução.

### Adicionado

- **F# Query Notebook** (`.fsqnb`) — tipo de notebook customizado; cada célula de código roda contra uma sessão interativa persistente do FSI em um processo worker F# em segundo plano. Transmissão ao vivo de stdout / stderr; cancelamento de células em execução; comando **Restart Notebook Session** para limpar a sessão.
- **Renderizador de Grade de Resultados** — tabela virtualizada para dados tabulares com aba de esquema, exportação para CSV e paginação "carregar mais" para grandes volumes de dados.
- **Inteligência F# na Célula** — autocomplete, hover, signature help e diagnósticos para células de notebook direcionados aos métodos `analysis/*` do worker (sem dependência do Ionide). Inclui completamento de nomes de conexão em literais de string sob `Connections.*`.
- **Painel de Conexões** — árvore na Barra de Atividades com conexões → tabelas → colunas (carregamento sob demanda); adição / edição / remoção através de assistentes guiados. Suporte a SQLite, CSV, Excel, SQL Server, PostgreSQL e REST JSON.
- **Armazenamento de Segredos Multiplataforma** — credenciais armazenadas no chaveiro do sistema operacional via `SecretStorage` do VS Code através de RPC reverso do worker. Nenhuma senha é gravada no `workspace.json` ou arquivo de notebook.
- Verificação de integridade do worker e versão do protocolo na ativação, com mensagem orientativa quando o `dotnet` (≥ 10) não for encontrado.

### Limitações Conhecidas

- Requer o **.NET 10 SDK** no `PATH`; o VSIX empacota um worker dependente do framework instalado.
- A inteligência na célula restringe-se a autocomplete / hover / signature help / diagnósticos — sem find-all-references, formatação ou renomeação entre células.
- Apenas uma avaliação é executada por vez, inclusive entre múltiplos arquivos `.fsqnb` abertos na mesma janela.
