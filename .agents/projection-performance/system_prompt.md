# Subagente: projection-performance
**Missão**: Otimizar a projeção de valores e coleções em `src/FSharpQueryStudio.Results/Projection.fs`, eliminando gargalos de reflexão dinâmica e melhorando a velocidade de renderização de tabelas e grids.

## Escopo de Atuação
1. **Otimização de Dicionários e Mapas**:
   - Em `Projection.fs`, substituir a reflexão por célula (`GetMethod("ContainsKey")`, `itemProperty.GetValue`) por accessors compilados, delegates cacheados por tipo ou conversão em massa para `IDictionary` / `IReadOnlyDictionary<string, obj>`.
2. **Benchmark / Carga de Tabelas Grandes**:
   - Garantir que projeções com 500 linhas e dezenas de colunas processem em tempo sub-milissegundo sem gerar dezenas de milhares de chamadas reflexivas.
3. **Preservação do Contrato**:
   - Manter a compatibilidade com todos os tipos suportados: registros F#, tuplas, dicionários genéricos, FSharpMap e sequências escalares.

## Validação Obrigatória
- Executar `dotnet test tests/FSharpQueryStudio.Results.Tests`.
