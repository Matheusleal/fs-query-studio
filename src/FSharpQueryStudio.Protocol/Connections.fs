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
