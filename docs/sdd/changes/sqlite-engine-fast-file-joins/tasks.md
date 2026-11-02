# Tasks: Universal SQL Pushdown & File SQLite Engine

## Status Legend
- `[ ]` Not Started
- `[/]` In Progress
- `[x]` Completed

---

## Phase 1: Universal SQL Pushdown (EF-Style Projection & Joins)

### [TASK-001] Pushdown de Projeção (`Select`) e Expressões Escalares
- **Status**: `[x] Completed`
- **Goal**: Implementar a tradução de expressões `Select` para colunas explícitas no SQL para SQLite, PostgreSQL e SQL Server, eliminando `SELECT *`.
- **Related Requirements**: `[REQ-PUSHDOWN-001]`, `[REQ-PUSHDOWN-004]`
- **Done When**:
  - `ExpressionPushdownTranslator` detecta chamadas a `Select` e constrói a lista de colunas projetadas (`plan.Projection = Some "col1 AS Alias1, col2 AS Alias2"`).
  - Suporta membros simples, tuplas e registros anônimos (`{| A = c.A; B = c.B |}`).
  - Emite operadores específicos de dialeto para concatenações (ex: `||` em SQLite/Postgres, `+` em SQL Server).

### [TASK-002] Mapeador Dinâmico de Registros Projetados
- **Status**: `[x] Completed`
- **Goal**: Garantir que o leitor de registros consiga deserializar tipos projetados (`'TResult`) diretamente das colunas retornadas pela query customizada.
- **Related Requirements**: `[REQ-PUSHDOWN-002]`
- **Done When**:
  - `CompiledRecordMapper` ou `RecordReaders` mapeia os campos de `'TResult` para os índices das colunas que vieram do `IDataRecord`, sem depender das colunas originais da tabela base.

### [TASK-003] Pushdown de `Join` Relacional Nativo
- **Status**: `[x] Completed`
- **Goal**: Compilar `Queryable.Join` para `INNER JOIN` em SQL quando ambas as fontes pertencem ao mesmo banco de dados ou ao mesmo engine.
- **Related Requirements**: `[REQ-PUSHDOWN-003]`, `[REQ-FALLBACK-001]`
- **Done When**:
  - `ExpressionPushdownTranslator` inspeciona a árvore de junção e extrai tabelas, aliases e predicados de chave (`ON t0.Key = t1.Key`).
  - Gera comando SQL contendo `FROM TableA t0 JOIN TableB t1 ON ...`.
  - Fallback em memória só é acionado se as fontes forem de bancos fisicamente incompatíveis e não federados.

---

## Phase 2: Workspace File SQLite Staging Engine

### [TASK-004] Implementação do `FileSqliteEngine` com Cache Inteligente
- **Status**: `[x] Completed`
- **Goal**: Criar o serviço de staging SQLite em `.fsqs/cache/engine.db` para gerenciar tabelas espelhadas de CSV e Excel.
- **Related Requirements**: `[REQ-ENGINE-001]`, `[REQ-ENGINE-002]`, `[REQ-ENGINE-003]`
- **Done When**:
  - O engine verifica mtime e tamanho do arquivo para evitar reingestão desnecessária.
  - Ingestão em lote rápida via transação única e WAL, atingindo altas velocidades em 200k+ linhas.
  - Cria índices automáticos em colunas `Id` e `*_id`.

### [TASK-005] Exposição de `Connections.query` e Integração de CSV/Excel
- **Status**: `[x] Completed`
- **Goal**: Conectar `Connections.csv` e `Connections.excel` ao `FileSqliteEngine` e disponibilizar a API `Connections.query(sql)`.
- **Related Requirements**: `[REQ-ENGINE-004]`, `[REQ-CON-001]`
- **Done When**:
  - `Connections.query` executa consultas SQL diretamente no SQLite do workspace, retornando tabelas.
  - `Connections.csv.<Name>` e `Connections.excel.<Name>` fornecem `IQueryable` apontando para o `FileSqliteEngine`, herdando o pushdown de Where, Select e Join.

---

## Phase 3: Geração de Código Limpa & Validação em Larga Escala

### [TASK-006] Refatoração da Geração de Entidades (`GeneratedEntities.fs`)
- **Status**: `[x] Completed`
- **Goal**: Gerar chamadas limpas `conn.Table<...>("TableName")` para que a geração de SQL use referências limpas de tabela.
- **Related Requirements**: `[REQ-GEN-001]`
- **Done When**:
  - `GeneratedEntities.fs` não emite queries fixas com todas as colunas.
  - Testes de compilação do FSAC continuam 100% íntegros.

### [TASK-007] Testes Automatizados com >200.000 Linhas e Validação E2E
- **Status**: `[x] Completed`
- **Goal**: Validar joins complexos entre arquivos de 200k+ linhas, consultas SQL diretas e garantir 100% de sucesso nos testes existentes e notebooks demo.
- **Related Requirements**: `[REQ-ENGINE-005]`
- **Done When**:
  - Criados testes com >200.000 linhas em CSV e Excel com joins relacionais complexos, agregações e projeções.
  - Todos os testes .NET e vitest rodam e passam com 100% de sucesso.
  - `sample_demo.fsqnb` e `sample_demo_pt.fsqnb` continuam funcionando perfeitamente.

