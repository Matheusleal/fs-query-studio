# Proposal: Aba de Raw Query e Parâmetros na Janela de Resultados da Query

## 1. Contexto e Problema
Atualmente, a janela de visualização dos resultados de uma consulta em cadernos interativos (`.fsqnb`) renderiza uma barra de ferramentas com apenas duas abas: **Data** (grade tabular de linhas com paginação virtual) e **Schema** (lista de colunas com tipos de dados).

Quando desenvolvedores e analistas executam consultas pushdown (`fsql { ... }`), consultas diretas ou métodos de banco, não há visibilidade imediata da instrução SQL gerada nem dos parâmetros enviados ao banco de dados. Para inspecionar esses detalhes, o usuário é obrigado a:
1. Alterar a célula para chamar `toRawQuery query` ou `explain query`.
2. Usar `printfn "%s" (toRawQuery query)` forçando a impressão no console de saída.
3. Chamar manualmente `.rawQuery("...")` ou `toRawSqlWithParameters`.

Essa fricção interrompe o fluxo de exploração interativa de dados.

## 2. Intent Block

```text
Intent Block:
Goal:        Adicionar uma aba dedicada de "Raw Query" na janela de resultados das queries (ao lado de Data e Schema) exibindo a query SQL bruta formatada e os parâmetros enviados.
Expectation: Toda execução de consulta que resulte em visualização tabular (pushdown LINQ, FSQL ou rawQuery) deve disponibilizar na janela de resultados a aba "Raw Query" com a query SQL formatada, a relação completa de parâmetros enviados e botão de cópia rápida.
Action:      Estender o protocolo `ResultEnvelope` e `TablePayload` para transportar `RawQuery` e `Parameters`; capturar a instrução SQL e os parâmetros executados no Worker backend (`Projection`, conectores e rastreador de execução); e adicionar o botão e container de visualização de Raw Query no renderizador do VS Code (`renderer/index.ts`).
Result:      Visibilidade instantânea do SQL e dos parâmetros de qualquer consulta diretamente na interface do notebook, eliminando a necessidade de forçar `toRawQuery` ou alterar código apenas para depuração.
```

## 3. Escopo

### No Escopo (In Scope)
- **Protocolo & Backend**:
  - Campo `RawQuery: string option` e `Parameters: QueryParameter list` em `ResultEnvelope`.
  - Captura da query gerada e dos parâmetros em `Projection.fs` para `SqlPushdownQueryable`, `ConnectorQueryResult` e via `LastQueryTracker` para consultas executadas durante o ciclo de vida da célula.
  - Registro de `sql` e `parameters` em conectores relacionais e SQLite engine.
- **Extensão VS Code & Renderer**:
  - Atualização dos tipos `ResultEnvelope`, `TablePayload` e `QueryParameter` em `protocol.ts` e `types.ts`.
  - Atualização de `buildTablePayload` em `table-payload.ts`.
  - Adição do botão segmented `Raw Query` (`data-view="rawQuery"`) na toolbar do renderizador.
  - Painel de visualização `.fsq-raw-query` exibindo:
    - Código SQL formatado em bloco `pre/code` monoespaçado com visual nativo do VS Code.
    - Seção de parâmetros com tabela detalhada (`Parameter`, `Value`).
    - Botão de cópia para a área de transferência com feedback visual ("Copied!").
    - Mensagem amigável caso a célula não envolva uma consulta SQL direta.
  - Alternância fluida entre as 3 abas (**Data**, **Schema**, **Raw Query**).
- **Testes & Qualidade**:
  - Testes unitários e de integração no backend F# (mínimo 2 testes distintos por cenário, 100% de cobertura nos arquivos tocados).
  - Testes de renderizador TypeScript (mínimo 2 testes distintos por cenário, 100% de cobertura nos arquivos tocados).
  - Validação completa com `dotnet fsi .\build.fsx release` e testes E2E dos notebooks de amostra.

### Fora de Escopo (Out of Scope)
- Edição do SQL diretamente na aba de Raw Query (a aba é estritamente para inspeção/diagnóstico dos resultados já computados).
- Execução de planos de execução gráficos (o plano textual já é suportado via `explain`).
