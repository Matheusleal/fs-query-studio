# Task Ledger: Melhorias de Qualidade de Código e Desacoplamento

## Fase 1: Confiabilidade Crítica do Backend (Quick Wins & Thread Safety)
- [x] TASK-001: Implementar `TryReadAsync` em `ISecretStore` e `BridgeSecretStore` (eliminar `.Wait()`)
- [x] TASK-002: Atualizar `IConnectionResolver` e `WorkspaceConnectionResolver` para consumo assíncrono de segredos
- [x] TASK-003: Corrigir dupla execução no `SqliteConnector` preservando cursor ativo em stream único
- [x] TASK-004: Implementar leitura em buffer no `LspTransport.fs` do `Fsac`
- [ ] TASK-005: Purificar `FSharpQueryStudio.Protocol/Results.fs` (mover `ToRawSql` para Connectors e remover `ResultKindState`)
- [ ] TASK-006: Validar testes unitários do Backend (`FSharpQueryStudio.Worker.Tests`) e adicionar testes para cada melhoria (mínimo 2 por cenário)

## Fase 2: Modularização e Confiabilidade do Frontend
- [x] TASK-007: Extrair `ConnectionHealthService` e desacoplar retries e timers da `ConnectionsTreeProvider`
- [x] TASK-008: Extrair `StatusBarManager`, `DataSourcesWatcher` e `BackgroundOrchestrator` de `extension.ts`
- [x] TASK-009: Extrair `registerSampleDataCommands` e modularizar `extension.ts` para < 200 linhas
- [x] TASK-010: Implementar limpeza de event listeners com `AbortController` e estilo único no `renderer/index.ts`
- [x] TASK-011: Validar testes do Frontend (`npm test` / Vitest) e cobertura

## Fase 3: Validação Completa de Release e E2E
- [x] TASK-012: Executar validação E2E com `node editors/vscode/test_all_samples_e2e.mjs`
- [x] TASK-013: Executar build completo de release e empacotamento VSIX (`dotnet fsi .\build.fsx release`)
- [x] TASK-014: Auditoria de conformidade SDD e atualização de documentação
