# Delta Specification: .fsqs Internal Storage Folder & SQL Server Windows Authentication

## Requisitos GEARS

### ADDED Requirements

#### FR-STORAGE-FSQS: Armazenamento Interno sob a pasta `.fsqs`
```text
Where a local workspace project directory is chosen or active,
the storage and worker components shall persist 'workspace.json' into '<projectRoot>/.fsqs/workspace.json'
and shall output generated code into '<projectRoot>/.fsqs/generated/GeneratedEntities.fs'.
```

#### FR-STORAGE-COMPAT: Compatibilidade Retroativa de Leitura
```text
Where '<projectRoot>/.fsqs/workspace.json' does not exist but '<projectRoot>/workspace.json' exists,
the storage component shall read connections from '<projectRoot>/workspace.json' and write updates into '<projectRoot>/.fsqs/workspace.json'.
```

#### FR-PROJ-INIT-FSQS: Inicialização de Projeto com Pasta `.fsqs`
```text
When the command 'fs-query-studio.initEmptyProject' is invoked,
the system shall create '<projectRoot>/.fsqs/workspace.json' with an empty list,
create the directory '<projectRoot>/.fsqs/generated/',
and initialize a starter notebook '<projectRoot>/notebook.fsqnb'.
```

#### FR-SQL-WINAUTH: Autenticação Windows para SQL Server
```text
Where the user configures a SQL Server connection,
when prompted for authentication method,
the connection UI shall offer 'Windows Authentication (Integrated Security)' and 'SQL Server Authentication'.
```

#### FR-SQL-WINAUTH-NO-SECRET: Conexão Windows sem Solicitação de Senha
```text
Where 'Windows Authentication' is selected for SQL Server,
the connection UI shall omit the password prompt,
shall configure 'Integrated Security=True;TrustServerCertificate=True;' in the connection string,
and the worker shall register the connection without creating a secret reference.
```
