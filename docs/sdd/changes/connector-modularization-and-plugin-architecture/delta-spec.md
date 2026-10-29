# Delta Specification: Modularização Desacoplada de Conectores / Decoupled Connector Modularization (Micro-Kernel & Plugin SPI)

---

## 🇧🇷 Português (PT-BR) — Requisitos GEARS

### ADDED Requirements (Adicionados)

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

#### FR-CONN-SINGLE-REGISTRATION: Ponto Único no Backend e no Frontend
```text
Where a new connector module is added to the solution,
the backend shall register the connector exclusively in BuiltinConnectors.fs and the frontend shall reference connector-specific metadata exclusively in connector-registry.ts.
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
the connections feature shall fetch available connector descriptors via 'connector/list' and dynamically render input prompts based on the descriptor's declarative form fields.
```

### REMOVED Requirements (Removidos)

#### FR-CONN-HARDCODED-TYPE-TESTS: Remoção de Type Tests Imperativos em `EntityBackgroundGenerator`
```text
The EntityBackgroundGenerator shall not contain type-test pattern matches (:?) against concrete connector implementations.
```

#### FR-VSCODE-HARDCODED-KIND-BRANCHES: Remoção de Branches Espalhados no VS Code
```text
The VS Code connection wizards, definitions, and completion providers shall delegate all connector-specific logic to ConnectorFrontRegistry.
```

### MODIFIED Requirements (Modificados)

#### FR-CONN-KIND-EXTENSIBILITY: Extensibilidade de `ConnectorKind`
```text
When serializing or deserializing a connection definition,
the protocol serialization shall support arbitrary connector kind string identifiers while preserving backward compatibility with legacy union cases.
```

#### FR-CONN-COMPOSITION-ROOT: `BuiltinConnectors.fs` como Ponto Único de Registro
```text
When the application or worker initializes,
the BuiltinConnectors module shall register all native connector modules (Sqlite, SqlServer, PostgreSql, Csv, Excel, Rest) into the default ConnectorRegistry.
```

---

## 🇺🇸 English (EN) — GEARS Requirements

### ADDED Requirements

#### FR-CONN-SPI-ABSTRACTIONS: Core Connector Abstractions and Contracts
```text
The FSharpQueryStudio.Connectors.Abstractions assembly shall expose connector interfaces (IConnector, IQueryableConnector, IStreamingQueryableConnector), entity code generator interfaces (IConnectorEntityGenerator), and registry abstractions (IConnectorDescriptor, IConnectorFactory, IConnectorRegistry) without referencing any third-party database driver package.
```

#### FR-CONN-DESCRIPTOR-SCHEMA: Declarative UI Metadata for Connectors
```text
When a connector module registers with the connector registry,
the connector module shall provide an IConnectorDescriptor containing a unique kind identifier, display name, category, icon name, and a declarative list of form fields defining connection configuration inputs, required validations, and secret handling.
```

#### FR-CONN-REGISTRY-RESOLUTION: Dynamic Connector Registration and Resolution
```text
When resolving a connection definition,
the ConnectorRegistry shall locate the matching registered descriptor by kind identifier and instantiate the connector instance using the descriptor's factory, configuration JSON, and secret store.
```

#### FR-CONN-MODULAR-PACKAGING: Decomposition into Isolated Connector Projects
```text
The solution shall provide isolated projects for each native connector (FSharpQueryStudio.Connectors.Sqlite, FSharpQueryStudio.Connectors.SqlServer, FSharpQueryStudio.Connectors.PostgreSql, FSharpQueryStudio.Connectors.Csv, FSharpQueryStudio.Connectors.Excel, FSharpQueryStudio.Connectors.Rest), each depending strictly on FSharpQueryStudio.Connectors.Abstractions and its own minimal driver package.
```

#### FR-CONN-SINGLE-REGISTRATION: Single Registration Point in Backend and Frontend
```text
Where a new connector module is added to the solution,
the backend shall register the connector exclusively in BuiltinConnectors.fs and the frontend shall reference connector-specific metadata exclusively in connector-registry.ts.
```

#### FR-CONN-MODULAR-ENTITY-GEN: Modular Typed Entity Generation
```text
When generating typed entities for registered workspace connections,
the EntityBackgroundGenerator shall query the connector registry for each connection's descriptor and invoke the descriptor's IConnectorEntityGenerator to emit typed accessors without inspecting concrete connector types.
```

#### FR-PROTOCOL-CONNECTOR-LIST: `connector/list` RPC in Protocol
```text
When the host environment sends a 'connector/list' request frame,
the worker shall return the list of all registered connector descriptors, including their kind identifiers, display names, categories, icons, and declarative form field schemas.
```

#### FR-VSCODE-SCHEMA-DRIVEN-UI: Declarative Connection Forms in VS Code
```text
When the user initiates connection creation or editing in VS Code,
the connections feature shall fetch available connector descriptors via 'connector/list' and dynamically render input prompts based on the descriptor's declarative form fields.
```

### REMOVED Requirements

#### FR-CONN-HARDCODED-TYPE-TESTS: Removal of Imperative Type Tests in `EntityBackgroundGenerator`
```text
The EntityBackgroundGenerator shall not contain type-test pattern matches (:?) against concrete connector implementations.
```

#### FR-VSCODE-HARDCODED-KIND-BRANCHES: Removal of Scattered Connector Branches in VS Code
```text
The VS Code connection wizards, definitions, and completion providers shall delegate all connector-specific logic to ConnectorFrontRegistry.
```

### MODIFIED Requirements

#### FR-CONN-KIND-EXTENSIBILITY: `ConnectorKind` Extensibility
```text
When serializing or deserializing a connection definition,
the protocol serialization shall support arbitrary connector kind string identifiers while preserving backward compatibility with legacy union cases.
```

#### FR-CONN-COMPOSITION-ROOT: `BuiltinConnectors.fs` as Single Registration Point
```text
When the application or worker initializes,
the BuiltinConnectors module shall register all native connector modules (Sqlite, SqlServer, PostgreSql, Csv, Excel, Rest) into the default ConnectorRegistry.
```
