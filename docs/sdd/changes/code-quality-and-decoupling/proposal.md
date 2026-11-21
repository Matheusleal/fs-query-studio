# Proposal: Melhorias de Qualidade de Código, Confiabilidade de Concorrência e Desacoplamento Arquitetural (Backend e Frontend)

## 1. Contexto e Problema

Uma auditoria abrangente realizada com subagentes especializados identificou pontos críticos de acoplamento, concorrência e manutenibilidade nas camadas do backend .NET e frontend VS Code:

1. **Anti-pattern Sync-over-Async em `ISecretStore`**: `ISecretStore.TryRead` é síncrona, obrigando `BridgeSecretStore` a bloquear a thread (`request.Wait(5000)`) em chamadas RPC assíncronas do `HostChannel`. Isso causa risco iminente de *ThreadPool Starvation* e trava avaliações de notebooks sob concorrência.
2. **Dupla Execução SQL em `SqliteConnector` e Duplicação ADO.NET**: `SqliteConnector` executa a mesma query duas vezes (uma para colunas, outra para linhas). Além disso, os 6 conectores relacionais compartilham mais de 70% de código idêntico sem abstração base.
3. **Leitura Ineficiente Byte-a-Byte no `LspTransport`**: A recepção de cabeçalhos LSP lê 1 byte por vez com `ReadAsync`, gerando alocações massivas e context switching na comunicação com o `fsautocomplete`.
4. **Mutabilidade Global e Contaminação no `Protocol`**: `Protocol/Results.fs` possui estado mutável compartilhado (`ResultKindState`) e métodos de reflexão de banco de dados (`ToRawSql`), violando a pureza de DTOs de transporte.
5. **Monólito no Frontend (`extension.ts` com 1.365 linhas)**: O arquivo de ativação orquestra timers de status bar, file watcher, estado de pausa e menus complexos de Docker, dificultando testes unitários e manutenção.
6. **Acoplamento na Árvore de Conexões (`tree.ts`)**: `ConnectionsTreeProvider` orquestra retries com timers órfãos e dispara alertas modais do VS Code junto da renderização da árvore.

---

## 2. Intent Block

```text
Intent Block:
Goal:        Eliminar anti-patterns de concorrência e gargalos de I/O, unificar infraestrutura de conectores relacionais e decompor módulos monolíticos tanto no backend quanto no frontend, garantindo zero quebras de compatibilidade e cobertura >= 90%.
Expectation: ISecretStore será 100% não bloqueante (Task/Async); SqliteConnector eliminará a dupla execução preservando o cursor de streaming; LspTransport fará leitura em buffer; Protocol será composto exclusivamente de DTOs puros; extension.ts será fatiado em serviços autônomos; todos os testes existentes e novos passarão com 100% de sucesso.
Action:      Executar um plano faseado de refatoração: Fase 1 (Confiabilidade Crítica: SecretStore assíncrono, Leitor SQLite em stream único, Buffer no LspTransport e DTOs puros no Protocol); Fase 2 (Frontend: Decomposição de extension.ts, ConnectionHealthService e tipagem segura no Worker); Fase 3 (Conectores: AdoNetConnectorBase e ITabularDataSourceConnector).
Result:      Arquitetura desacoplada, alta estabilidade de concorrência, eliminação de vazamento de memória/timers e manutenibilidade superior.
```
