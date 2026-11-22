# Delta Specification: Melhorias de Qualidade de Código, Concorrência e Desacoplamento

## 1. ADDED Requirements

### [REQ-ASYNC-SECRET-001] Resolução Assíncrona de Segredos (ISecretStore)
```text
When the worker or connection resolver requests a connection password or token via `ISecretStore`,
the `ISecretStore` implementation shall resolve the secret asynchronously via non-blocking `Task<string option>` or `Task<Result<string option, SecretError>>` without executing blocking thread waits (`.Wait()` or `.Result`).
```

### [REQ-SQLITE-SINGLE-EXEC-002] Execução em Stream Único no SQLite
```text
When `SqliteConnector.ExecuteQuery` executes a SQL statement,
the connector shall open the command and reader once, extract column metadata from the active reader, and yield rows in a lazy sequence that utilizes the existing reader for the initial iteration without executing the SQL query a second time.
```

### [REQ-LSP-BUFFER-003] Leitura em Buffer de Cabeçalhos LSP
```text
When `LspTransport` reads messages from the language server standard input/output stream,
the transport shall read bytes using a buffered reader (`BufferedStream` or byte chunks) rather than individual 1-byte read operations, minimizing Task allocations and thread-pool context switches.
```

### [REQ-PROTOCOL-PURITY-004] Pureza Estrutural do FSharpQueryStudio.Protocol
```text
Where `FSharpQueryStudio.Protocol` defines data contract types and envelopes,
the protocol library shall contain exclusively pure, immutable DTO records and discriminated unions without global mutable state (`ResultKindState`) or database query reflection hooks (`ToRawSql`), delegating SQL inspection to the connectors domain.
```

### [REQ-FRONTEND-SERVICES-005] Decomposição Modular de `extension.ts`
```text
Where the VS Code extension activates,
the extension entry point shall delegate status bar management, data source watching, background process orchestration, and sample data scaffolding to dedicated single-responsibility service classes implementing `vscode.Disposable`.
```

### [REQ-CONN-HEALTH-006] Isolamento do Gerenciador de Saúde de Conexões
```text
When connection health checks and retries are performed,
the `ConnectionHealthService` shall manage retry timers and connection status independently of the UI TreeDataProvider, ensuring all scheduled timers are cancelled upon connection deletion or extension disposal.
```

---

## 2. MODIFIED Requirements

### [REQ-SECRET-STORE-MOD-001] Interface `ISecretStore`
- **MODIFIED FROM**: `ISecretStore` expondo `TryRead: reference: string -> string option` (síncrona).
- **MODIFIED TO (GEARS)**:
```text
When querying secrets in `ISecretStore`,
the interface shall expose asynchronous methods (`TryReadAsync: string * CancellationToken -> Task<string option>`) and the `BridgeSecretStore` shall await the host RPC response asynchronously without blocking thread pool threads.
```

### [REQ-SQLITE-CONN-MOD-002] Ciclo de Vida do Leitor em `SqliteConnector`
- **MODIFIED FROM**: `SqliteConnector.ExecuteQuery` fechando o leitor inicial e abrindo uma segunda conexão dentro de `Seq.delay`.
- **MODIFIED TO (GEARS)**:
```text
When `SqliteConnector.ExecuteQuery` is invoked,
the connector shall preserve the open reader for the first sequence consumption and only re-execute if the returned `Rows` sequence is evaluated multiple times.
```
