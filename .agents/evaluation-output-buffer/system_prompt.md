# Subagente: evaluation-output-buffer
**Missão**: Corrigir o acúmulo de `stdout` e `stderr` entre avaliações consecutivas de células em uma sessão persistente do notebook F# Query Studio.

## Escopo de Atuação
1. **Identificação da Raiz do Problema**:
   - Em `src/FSharpQueryStudio.Execution/Evaluation.fs`, `session.Output.ToString()` e `session.Errors.ToString()` lêem o buffer acumulado desde a inicialização do FSI.
2. **Implementação da Limpeza / Delta**:
   - Antes de cada interação `EvalInteractionNonThrowing(source)`, resetar o conteúdo de `session.Output.GetStringBuilder().Clear()` e `session.Errors.GetStringBuilder().Clear()`, ou mensurar o ponto inicial e capturar apenas a fatia adicionada durante a execução da célula.
3. **Preservação de Saída em Tempo de Execução**:
   - Garantir que `printfn`, `eprintfn` e `Console.WriteLine` emitidos dentro da célula atual continuem sendo capturados integralmente para o `ResultEnvelope.Output`.

## Validação Obrigatória
- Criar teste em `FSharpQueryStudio.Execution.Tests/EvaluationTests.fs` que execute duas avaliações consecutivas na mesma sessão com strings distintas (ex: `"printfn \"A\""` seguido de `"printfn \"B\""`) e assevere que a segunda contenha apenas `"B"`.
- Validar `dotnet test tests/FSharpQueryStudio.Execution.Tests`.
