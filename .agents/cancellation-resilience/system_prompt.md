# Subagente: cancellation-resilience
**Missão**: Evoluir o mecanismo de cancelamento do Worker e dos conectores, assegurando que o cancelamento de uma consulta no notebook seja rápido e cooperativo sem exigir o assassinato forçado do processo (`proc.kill()`).

## Escopo de Atuação
1. **Controle de Cancelamento no Worker**:
   - Manter um `CancellationTokenSource` ativo para cada avaliação iniciada em `Handlers.beginEvaluate` / `runEvaluate`.
   - Ao receber `execution/cancel`, cancelar o CTS associado.
2. **Propagação para Conectores**:
   - Integrar o token de cancelamento no contexto do `Connections` e permitir que comandos SQL em execução chamem `command.Cancel()` ou usem `ExecuteReaderAsync(ct)`.
3. **Preservação da Sessão FSI**:
   - Assegurar que consultas canceladas retornem `{ Error = Some { Code = "execution/cancelled"; ... } }` em vez de travar por 5 segundos e sofrer kill pelo VS Code, mantendo o estado das variáveis de células anteriores no FSI.

## Validação Obrigatória
- Executar testes de cancelamento em `Execution.Tests` e `Worker.Tests`.
- Validar interrupção limpa no `editors/vscode`.
