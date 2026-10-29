# Delta Specification: Modularização Desacoplada de Conectores (Micro-Kernel & Plugin SPI)

## Requisitos GEARS

### ADDED Requirements

#### FR-CONN-SPI-ABSTRACTIONS: Abstrações e Contratos Centrais de Conector
```text
The FSharpQueryStudio.Connectors.Abstractions assembly shall expose connector interfaces (IConnector, IQueryableConnector, IStreamingQueryableConnector), entity code generator interfaces (IConnectorEntityGenerator), and registry abstractions (IConnectorDescriptor, IConnectorFactory, IConnectorRegistry) without referencing any third-party database driver package.
```

#### FR-CONN-DESCRIPTOR-SCHEMA: Metadados Declarativos de UI para Conectores
```text
When a connector module registers with the connector registry,
the connector module shall provide an IConnectorDescriptor containing a unique kind identifier, display name, category, icon name, and a declarative list of form fields defining connection configuration inputs, required validations, and secret handling.
```

#### FR-CONN-REGISTRY-RESOLUTION: Registro Dinâmico e Resolução de Conectores
```text
When resolving a connection definition,
the ConnectorRegistry shall locate the matching registered descriptor by kind identifier and instantiate the connector instance using the descriptor's factory, configuration JSON, and secret store.
```

#### FR-CONN-MODULAR-PACKAGING: Decomposição em Projetos Isolados por Conector
```text
The solution shall provide isolated projects for each native connector (FSharpQueryStudio.Connectors.Sqlite, FSharpQueryStudio.Connectors.SqlServer, FSharpQueryStudio.Connectors.PostgreSql, FSharpQueryStudio.Connectors.Csv, FSharpQueryStudio.Connectors.Excel, FSharpQueryStudio.Connectors.Rest), each depending strictly on FSharpQueryStudio.Connectors.Abstractions and its own minimal driver package.
```

#### FR-CONN-MODULAR-ENTITY-GEN: Geração Modular de Entidades Tipadas
```text
When generating typed entities for registered workspace connections,
the EntityBackgroundGenerator shall query the connector registry for each connection's descriptor and invoke the descriptor's IConnectorEntityGenerator to emit typed accessors without inspecting concrete connector types.
```

#### FR-PROTOCOL-CONNECTOR-LIST: RPC `connector/list` no Protocolo
```text
When the host environment sends a 'connector/list' request frame,
the worker shall return the list of all registered connector descriptors, including their kind identifiers, display names, categories, icons, and declarative form field schemas.
```

#### FR-VSCODE-SCHEMA-DRIVEN-UI: Formulários Declarativos de Conexão no VS Code
```text
When the user initiates connection creation or editing in VS Code,
the connections feature shall fetch available connector descriptors via 'connector/list' and dynamically render input prompts based on the descriptor's declarative form fields without hardcoded connector branching.
```

---

### REMOVED Requirements

#### FR-CONN-HARDCODED-TYPE-TESTS: Remoção de Type Tests Imperativos em `EntityBackgroundGenerator`
```text
The EntityBackgroundGenerator shall not contain type-test pattern matches (:?) against concrete connector implementations.
```

#### FR-VSCODE-HARDCODED-KIND-BRANCHES: Remoção de Branches Hardcoded por Conector no VS Code
```text
The VS Code connection wizards and input collectors shall not contain hardcoded switch or if-else statements branching on specific connector kind names.
```

---

### MODIFIED Requirements

#### FR-CONN-KIND-EXTENSIBILITY: Extensibilidade de `ConnectorKind`
```text
When serializing or deserializing a connection definition,
the protocol serialization shall support arbitrary connector kind string identifiers while preserving backward compatibility with legacy union cases.
```

#### FR-CONN-COMPOSITION-ROOT: `FSharpQueryStudio.Connectors` como Agregador
```text
When the application or worker initializes,
the composition root shall register all native connector modules (Sqlite, SqlServer, PostgreSql, Csv, Excel, Rest) into the default ConnectorRegistry.
```
