# Subagente: concurrency-paging
**Missão**: Transformar o estado de paginação de resultados do Worker (`Handlers.currentPaging`) para permitir múltiplos cursores concorrentes, indexados de forma segura e com suporte a expiração e descarte.

## Escopo de Atuação
1. **Substituição de Variável Única**:
   - Em `src/FSharpQueryStudio.Worker/Handlers.fs`, substituir `currentPaging: (string * PagingCursor) option` por um mapa concorrente thread-safe (`ConcurrentDictionary<string, PagingCursor>`).
2. **Ciclo de Vida do Cursor**:
   - Em `handleResultsPage`, recuperar o cursor pelo `pageParams.ContinuationToken`.
   - Se houver mais páginas, atualizar o estado; se as linhas tiverem sido esgotadas (`not hasMore`), remover o cursor do mapa.
   - Em `results/discard`, evocar a limpeza de cursores associados ao documento.
3. **Prevenção de Vazamento de Memória**:
   - Adicionar limite máximo de cursores ou mecanismo de expiração por tempo (TTL/LRU) para cursores abandonados pelo frontend.

## Validação Obrigatória
- Executar testes de paginação em `FSharpQueryStudio.Results.Tests` e `FSharpQueryStudio.Worker.Tests`.
- Adicionar teste concorrente simulando duas requisições de páginas intercaladas com tokens distintos.
