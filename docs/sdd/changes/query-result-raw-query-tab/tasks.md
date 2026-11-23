# Tasks Ledger: `query-result-raw-query-tab`

| ID | Status | Requisito | Descrição da Tarefa |
|---|---|---|---|
| **TASK-01** | `Done` | `REQ-RAWQUERY-PROTO-001` | Estender `ResultEnvelope` em `src/FSharpQueryStudio.Protocol/Results.fs` com `RawQuery: string option` e `Parameters: QueryParameter list`, definindo o tipo `QueryParameter`. |
| **TASK-02** | `Done` | `REQ-RAWQUERY-TRACK-002` | Implementar `LastQueryTracker` e atualizar `Projection.fs`, `ConnectorQueryResult` e conectores para capturar a instrução SQL formatada e os parâmetros em consultas pushdown (`SqlPushdownQueryable`) e execuções diretas (`rawQuery`/`ExecuteQuery`). |
| **TASK-03** | `Done` | `REQ-RAWQUERY-PAYLOAD-003` | Atualizar os tipos `ResultEnvelope`, `TablePayload` e `QueryParameter` em `protocol.ts`, `types.ts` e a projeção em `table-payload.ts`. |
| **TASK-04** | `Done` | `REQ-RAWQUERY-TAB-004`, `REQ-RAWQUERY-VIEW-005`, `REQ-RENDERER-TAB-TOGGLE` | Implementar o botão "Raw Query" (`data-view="rawQuery"`), alternância entre 3 abas, bloco monoespaçado de SQL formatado, tabela de parâmetros, botão de cópia com feedback visual e estado vazio em `editors/vscode/src/renderer/index.ts`. |
| **TASK-05** | `Done` | `REQ-RAWQUERY-PROTO-001`..`005` | Escrever testes automatizados completos no backend F# e frontend TypeScript (mínimo 2 testes distintos por cenário, mantendo 100.00% de cobertura nos arquivos modificados/criados). |
| **TASK-06** | `Done` | `REQ-RAWQUERY-PROTO-001`..`005` | Executar a compilação completa de release e empacotamento VSIX via `dotnet fsi .\build.fsx release` e validação E2E dos cadernos de amostra via `node editors/vscode/test_all_samples_e2e.mjs`. |
| **TASK-07** | `Done` | `REQ-RAWQUERY-PARAM-TYPE-006` | Adicionar campo `DisplayType` a `QueryParameter` no backend (`Results.fs`, inferência em `Projection.fs`), tipos no frontend (`protocol.ts`, `types.ts`, `table-payload.ts`), coluna `Type` no cabeçalho e corpo da tabela de parâmetros em `index.ts`, e testes correspondentes. |
