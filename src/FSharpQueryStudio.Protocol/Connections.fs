namespace FSharpQueryStudio.Protocol

open System
open System.Text.Json

/// The six first-day connector targets — see Section 10.
type ConnectorKind =
    | SqlServer
    | PostgreSql
    | Sqlite
    | Csv
    | Excel
    | RestJson

/// A registered, non-secret connection binding — see Section 10 and the
/// `connection/register` handler in Section 7. `Configuration` is
/// connector-specific (a SQLite file path, a REST base URL and headers,
/// ...); `SecretReference` points into the OS credential store rather
/// than carrying any credential value itself, so this type is always
/// safe to write to `workspace.json` as-is.
type ConnectionDefinition =
    { Id: Guid
      Name: string
      Kind: ConnectorKind
      Configuration: JsonElement
      SecretReference: string option
      IsTrusted: bool }

/// `connection/remove` request payload — see Section 7's method table.
type ConnectionRemoveParams = { Id: Guid }

/// `connection/schema` request payload — see Section 7's method table.
type ConnectionSchemaParams = { Id: Guid }

/// One discovered column — the wire-facing mirror of
/// `FSharpQueryStudio.Connectors.ConnectorColumn`. A separate type, not a
/// reused one: `Protocol` never depends on `Connectors` (the connector
/// implementations depend on `Protocol`, not the other way around), the
/// same layering `ConnectionDefinition`/`ConnectorKind` already keep.
type SchemaColumn = { Name: string; DataType: string }

/// One discovered table/sheet/resource — mirrors `ConnectorTable`.
type SchemaTable = { Name: string; Columns: SchemaColumn list }

/// `connection/schema` result payload.
type ConnectionSchemaResult = { Tables: SchemaTable list }
