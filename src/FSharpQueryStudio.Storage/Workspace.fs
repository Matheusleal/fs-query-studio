namespace FSharpQueryStudio.Storage

open System.IO
open System.Text.Json
open FSharpQueryStudio.Protocol

/// Reads and writes `workspace.json` — Section 13's connector names and
/// non-secret settings file. `ConnectionDefinition` never carries a raw
/// credential value (only a `SecretReference` pointer into the OS
/// credential store), so this module needs no redaction of its own; the
/// type itself is what keeps secrets out.
module Workspace =

    let private fileName = "workspace.json"

    let private workspaceFilePath (workspaceDir: string) = Path.Combine(workspaceDir, fileName)

    /// The empty list if `workspace.json` doesn't exist yet — a brand-new
    /// workspace has no connections registered.
    let readConnections (workspaceDir: string) : ConnectionDefinition list =
        let path = workspaceFilePath workspaceDir

        if File.Exists path then
            let json = File.ReadAllText path
            JsonSerializer.Deserialize<ConnectionDefinition list>(json, Serialization.options)
        else
            []

    let writeConnections (workspaceDir: string) (connections: ConnectionDefinition list) : unit =
        Directory.CreateDirectory workspaceDir |> ignore
        let json = JsonSerializer.Serialize(connections, Serialization.options)
        File.WriteAllText(workspaceFilePath workspaceDir, json)

    /// `connection/register` — Section 7. Adds `connection` as a new
    /// binding, or replaces the existing one sharing its `Id`, then
    /// persists the full list back to `workspace.json`. Returns the
    /// updated list.
    let registerConnection (workspaceDir: string) (connection: ConnectionDefinition) : ConnectionDefinition list =
        let updated =
            readConnections workspaceDir
            |> List.filter (fun (c: ConnectionDefinition) -> c.Id <> connection.Id)
            |> fun rest -> rest @ [ connection ]

        writeConnections workspaceDir updated
        updated

    /// `connection/remove` — drops the binding with the matching `Id` and
    /// persists the result. A no-op, not an error, when nothing is
    /// registered under that `Id` (already removed, or never existed) —
    /// the caller can't distinguish the two and shouldn't need to.
    let removeConnection (workspaceDir: string) (id: System.Guid) : ConnectionDefinition list =
        let updated =
            readConnections workspaceDir |> List.filter (fun (c: ConnectionDefinition) -> c.Id <> id)

        writeConnections workspaceDir updated
        updated
