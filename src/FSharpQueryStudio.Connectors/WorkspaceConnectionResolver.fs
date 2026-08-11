namespace FSharpQueryStudio.Connectors

open System.Text.Json
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security

/// `ConnectionDefinition.Configuration`'s shape for file-path-only
/// connectors — Sqlite, Csv, Excel all just need a path.
type FileConnectionConfig = { FilePath: string }

/// `ConnectionDefinition.Configuration`'s shape for SqlServer/PostgreSql —
/// a plain ADO.NET connection string that may embed a literal `{password}`
/// token, substituted from the secret store at resolve time. Never holds
/// a raw credential value itself: that's exactly what `SecretReference`
/// exists to keep out of `workspace.json`.
type SqlConnectionStringConfig = { ConnectionString: string }

/// Substitutes `template`'s `{password}` token from `secretStore`, keyed
/// by `secretReference` — a pure, directly-testable extraction of the one
/// piece of `WorkspaceConnectionResolver` that isn't "deserialize and
/// construct a connector". Returns `template` unchanged when there's no
/// reference to resolve, or the reference has nothing stored under it
/// (never throws — a missing secret surfaces as a connection failure at
/// actual use, the same as any other bad connection string would).
module ConnectionStringSecrets =
    let resolve (secretStore: ISecretStore) (secretReference: string option) (template: string) : string =
        match secretReference with
        | None -> template
        | Some reference ->
            match secretStore.TryRead reference with
            | Some secret -> template.Replace("{password}", secret)
            | None -> template

/// Builds a live `IConnector` from a registered `ConnectionDefinition` —
/// Section 13's workspace connection registry, the real implementation
/// `Connections.IConnectionResolver` was designed against but, until this
/// task, only ever exercised by hand-built test doubles (see
/// `MilestoneCAcceptanceTests.SingleConnectionResolver`). One instance
/// wraps a snapshot of `workspace.json`'s connections for a single
/// resolve pass; the Worker builds a fresh one before every
/// `execution/evaluate` (Task 3) so a connection registered mid-session
/// is usable on the very next run, no worker restart required.
type WorkspaceConnectionResolver(connections: ConnectionDefinition list, secretStore: ISecretStore) =

    let build (definition: ConnectionDefinition) : IConnector =
        match definition.Kind with
        | Sqlite ->
            let config =
                JsonSerializer.Deserialize<FileConnectionConfig>(definition.Configuration, Serialization.options)

            SqliteConnector(config.FilePath)
        | Csv ->
            let config =
                JsonSerializer.Deserialize<FileConnectionConfig>(definition.Configuration, Serialization.options)

            CsvConnector(config.FilePath)
        | Excel ->
            let config =
                JsonSerializer.Deserialize<FileConnectionConfig>(definition.Configuration, Serialization.options)

            ExcelConnector(config.FilePath)
        | SqlServer ->
            let config =
                JsonSerializer.Deserialize<SqlConnectionStringConfig>(definition.Configuration, Serialization.options)

            SqlServerConnector(ConnectionStringSecrets.resolve secretStore definition.SecretReference config.ConnectionString)
        | PostgreSql ->
            let config =
                JsonSerializer.Deserialize<SqlConnectionStringConfig>(definition.Configuration, Serialization.options)

            PostgreSqlConnector(
                ConnectionStringSecrets.resolve secretStore definition.SecretReference config.ConnectionString
            )
        | RestJson ->
            let options =
                JsonSerializer.Deserialize<RestRequestOptions>(definition.Configuration, Serialization.options)

            RestConnector(options)

    interface IConnectionResolver with
        member _.Resolve(name: string) : IConnector =
            match connections |> List.tryFind (fun c -> c.Name = name) with
            | Some definition -> build definition
            | None -> failwith $"No connection named '{name}' is registered."
