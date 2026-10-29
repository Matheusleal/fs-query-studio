namespace FSharpQueryStudio.Connectors

open System
open System.Text.Json
open System.Text.RegularExpressions
open System.Data.Common
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security

/// `ConnectionDefinition.Configuration`'s shape for file-path-only
/// connectors — Sqlite, Csv, Excel all need a path, Csv also supports an optional Delimiter.
[<CLIMutable>]
type FileConnectionConfig =
    { FilePath: string
      Delimiter: string option }

module private PathHelpers =
    let clean (p: string) =
        if String.IsNullOrWhiteSpace p then ""
        else p.Trim().Trim('"', '\'')

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
            | Some secret when not (String.IsNullOrEmpty secret) ->
                if template.IndexOf("{password}", StringComparison.OrdinalIgnoreCase) >= 0 then
                    Regex.Replace(template, "\{password\}", secret, RegexOptions.IgnoreCase)
                elif template.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) || template.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) then
                    try
                        let uri = Uri(template)
                        let userInfo = uri.UserInfo
                        if String.IsNullOrEmpty userInfo then
                            template
                        elif userInfo.Contains(":") then
                            let user = userInfo.Substring(0, userInfo.IndexOf(':'))
                            template.Replace($"{userInfo}@", $"{user}:{Uri.EscapeDataString(secret)}@")
                        else
                            template.Replace($"{userInfo}@", $"{userInfo}:{Uri.EscapeDataString(secret)}@")
                    with _ ->
                        template
                else
                    try
                        let builder = DbConnectionStringBuilder()
                        builder.ConnectionString <- template
                        builder["Password"] <- secret
                        builder.ConnectionString
                    with _ ->
                        let trimmed = template.TrimEnd(';')
                        $"{trimmed};Password={secret}"
            | _ -> template

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
    do BuiltinConnectors.registerDefaults ()

    let build (definition: ConnectionDefinition) : IConnector =
        let kindId = ConnectorRegistry.KindIdOf definition.Kind
        match ConnectorRegistry.Instance.TryGet kindId with
        | Some _ ->
            ConnectorRegistry.Instance.Resolve(definition, secretStore)
        | None ->
            failwith $"No connector descriptor registered for kind '{kindId}' (connection '{definition.Name}')."

    interface IConnectionResolver with
        member _.Resolve(name: string) : IConnector =
            match connections |> List.tryFind (fun c -> c.Name = name) with
            | Some definition -> build definition
            | None -> failwith $"No connection named '{name}' is registered."
