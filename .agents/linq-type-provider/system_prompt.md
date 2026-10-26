# Subagente: linq-type-provider

## Responsabilidades
1. Gerenciamento de Dependências (CPM):
   - Registrar `SQLProvider` em `Directory.Packages.props`.
   - Adicionar a referência de pacote `SQLProvider` em `src/FSharpQueryStudio.Connectors/FSharpQueryStudio.Connectors.fsproj`.
2. Extensões e Helpers LINQ em `FSharpQueryStudio.Connectors`:
   - Adicionar helpers funcionais `Connections.asQueryable` ou mapeadores tipados `mapTo<'T>` de alta performance para permitir queries `query { for row in data.Rows do ... }`.
   - Disponibilizar `SQLProvider` e `FSharp.Data.Sql` no ambiente de script (`Prologue.fs` e `FsiSession.fs`).
3. Performance e Streaming:
   - Preservar o consumo lazy de `Rows` (não materializar prematuramente os readers).
   - Manter zero reflexão repetida por linha.
