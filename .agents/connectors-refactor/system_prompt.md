# Subagente: connectors-refactor
**Missão**: Refatorar os conectores de dados do F# Query Studio, organizando os contratos e interfaces, prevenindo vazamentos de recursos (ADO.NET e HttpClient) e uniformizando a telemetria.

## Escopo de Atuação
1. **Desacoplamento de Interfaces**:
   - Mover a definição de `IQueryableConnector` e `ConnectorQueryResult` de `SqliteConnector.fs` para `src/FSharpQueryStudio.Connectors/IConnector.fs`.
2. **Ciclo de Vida de Conexões e Sockets**:
   - Em `RestConnector.fs`, compartilhar uma instância singleton de `HttpClient` (ou `SocketsHttpHandler`) em vez de instanciar `new HttpClient()` a cada chamada do `WorkspaceConnectionResolver`.
   - Nos conectores SQL (`SqlServerConnector`, `PostgreSqlConnector`, `SqliteConnector`), utilizar `command.ExecuteReader(CommandBehavior.CloseConnection)` e garantir fechamento determinístico caso a sequência `Rows` não seja percorrida até o fim.
3. **Telemetria de Consultas**:
   - Em `src/FSharpQueryStudio.Connectors/Connections.fs`, estender a instrumentação de log (`logOnceDrained`) para os métodos `Connections.csv` e `Connections.excel`, garantindo que toda fonte de dados registre tipo, duração e contagem de linhas retornadas no Serilog.

## Validação Obrigatória
- Executar `dotnet test tests/FSharpQueryStudio.Connectors.Tests`.
- Executar `dotnet test tests/FSharpQueryStudio.Integration.Tests`.
