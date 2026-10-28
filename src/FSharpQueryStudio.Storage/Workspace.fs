namespace FSharpQueryStudio.Storage

open System
open System.IO
open System.Text.Json
open FSharpQueryStudio.Protocol

/// Strongly-typed model of `workspace.json` (Schema v2).
type WorkspaceConfiguration =
    { SchemaVersion: int
      ExtensionVersion: string
      UpdatedAt: DateTimeOffset option
      Connections: ConnectionDefinition list }

/// Reads and writes `workspace.json` — Section 13's connector names and
/// non-secret settings file. `ConnectionDefinition` never carries a raw
/// credential value (only a `SecretReference` pointer into the OS
/// credential store), so this module needs no redaction of its own; the
/// type itself is what keeps secrets out.
module Workspace =

    let private fileName = "workspace.json"

    let CurrentSchemaVersion = ProjectUpgrader.CurrentSchemaVersion
    let currentExtensionVersion () = ProjectUpgrader.currentExtensionVersion ()

    /// Runs the project upgrader if the target workspace requires migration.
    let ensureProjectUpgraded (workspaceDir: string) : UpgradeReport =
        ProjectUpgrader.upgradeWorkspace workspaceDir

    /// Resolves the canonical internal storage directory for a workspace.
    /// For any project directory, storage lives under `<workspaceDir>/.fsqs`,
    /// except when the directory is already `.fsqs` or the user profile storage `FSharpQueryStudio`.
    let resolveStorageDir (workspaceDir: string) : string =
        if String.IsNullOrWhiteSpace workspaceDir then workspaceDir
        else
            let trimmed = workspaceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            let leaf = Path.GetFileName trimmed
            if leaf.Equals(".fsqs", StringComparison.OrdinalIgnoreCase) then
                workspaceDir
            elif leaf.Equals("FSharpQueryStudio", StringComparison.OrdinalIgnoreCase) then
                workspaceDir
            else
                Path.Combine(workspaceDir, ".fsqs")

    let workspaceFilePath (workspaceDir: string) : string =
        if not (String.IsNullOrWhiteSpace workspaceDir) && ProjectUpgrader.needsUpgrade workspaceDir then
            ProjectUpgrader.upgradeWorkspace workspaceDir |> ignore

        let targetDir = resolveStorageDir workspaceDir
        let pathInTarget = Path.Combine(targetDir, fileName)
        if File.Exists pathInTarget then
            pathInTarget
        elif File.Exists(Path.Combine(workspaceDir, ".fsqs", fileName)) then
            Path.Combine(workspaceDir, ".fsqs", fileName)
        elif File.Exists(Path.Combine(workspaceDir, fileName)) then
            Path.Combine(workspaceDir, fileName)
        else
            pathInTarget

    /// Reads `workspace.json` tolerating both Schema v1 (plain JSON array of connections)
    /// and Schema v2 (JSON object with schemaVersion, extensionVersion, connections).
    let readConfiguration (workspaceDir: string) : WorkspaceConfiguration =
        if not (String.IsNullOrWhiteSpace workspaceDir) && ProjectUpgrader.needsUpgrade workspaceDir then
            ProjectUpgrader.upgradeWorkspace workspaceDir |> ignore

        let path = workspaceFilePath workspaceDir

        if not (File.Exists path) then
            { SchemaVersion = CurrentSchemaVersion
              ExtensionVersion = currentExtensionVersion ()
              UpdatedAt = None
              Connections = [] }
        else
            let json = File.ReadAllText path
            use doc = JsonDocument.Parse json

            match doc.RootElement.ValueKind with
            | JsonValueKind.Array ->
                let connections =
                    JsonSerializer.Deserialize<ConnectionDefinition list>(json, Serialization.options)
                { SchemaVersion = 1
                  ExtensionVersion = "0.1.0"
                  UpdatedAt = None
                  Connections = connections }
            | JsonValueKind.Object ->
                let schemaVersion =
                    match doc.RootElement.TryGetProperty("schemaVersion") with
                    | true, el when el.ValueKind = JsonValueKind.Number -> el.GetInt32()
                    | _ ->
                        match doc.RootElement.TryGetProperty("$schemaVersion") with
                        | true, el when el.ValueKind = JsonValueKind.Number -> el.GetInt32()
                        | _ -> 1

                let extVersion =
                    match doc.RootElement.TryGetProperty("extensionVersion") with
                    | true, el when el.ValueKind = JsonValueKind.String -> el.GetString()
                    | _ -> currentExtensionVersion ()

                let updatedAt =
                    match doc.RootElement.TryGetProperty("updatedAt") with
                    | true, el when el.ValueKind = JsonValueKind.String ->
                        match DateTimeOffset.TryParse(el.GetString()) with
                        | true, dt -> Some dt
                        | _ -> None
                    | _ -> None

                let connections =
                    match doc.RootElement.TryGetProperty("connections") with
                    | true, el when el.ValueKind = JsonValueKind.Array ->
                        JsonSerializer.Deserialize<ConnectionDefinition list>(el.GetRawText(), Serialization.options)
                    | _ -> []

                { SchemaVersion = schemaVersion
                  ExtensionVersion = extVersion
                  UpdatedAt = updatedAt
                  Connections = connections }
            | _ ->
                { SchemaVersion = CurrentSchemaVersion
                  ExtensionVersion = currentExtensionVersion ()
                  UpdatedAt = None
                  Connections = [] }

    /// The empty list if `workspace.json` doesn't exist yet — a brand-new
    /// workspace has no connections registered.
    let readConnections (workspaceDir: string) : ConnectionDefinition list =
        let config = readConfiguration workspaceDir
        config.Connections

    /// Persists the full configuration to `workspace.json` in Schema v2 format.
    let writeConfiguration (workspaceDir: string) (config: WorkspaceConfiguration) : unit =
        if not (String.IsNullOrWhiteSpace workspaceDir) && ProjectUpgrader.needsUpgrade workspaceDir then
            ProjectUpgrader.upgradeWorkspace workspaceDir |> ignore

        let storageDir = resolveStorageDir workspaceDir
        Directory.CreateDirectory storageDir |> ignore
        let targetPath = Path.Combine(storageDir, fileName)

        let payload =
            {| schemaVersion = CurrentSchemaVersion
               extensionVersion = if String.IsNullOrWhiteSpace config.ExtensionVersion then currentExtensionVersion () else config.ExtensionVersion
               updatedAt = DateTimeOffset.UtcNow.ToString("o")
               connections = config.Connections |}

        let json = JsonSerializer.Serialize(payload, Serialization.options)
        File.WriteAllText(targetPath, json)

    /// Persists connections to `workspace.json` wrapped in Schema v2 format.
    let writeConnections (workspaceDir: string) (connections: ConnectionDefinition list) : unit =
        let existing = readConfiguration workspaceDir
        let updated =
            { existing with
                SchemaVersion = CurrentSchemaVersion
                ExtensionVersion = currentExtensionVersion ()
                UpdatedAt = Some DateTimeOffset.UtcNow
                Connections = connections }
        writeConfiguration workspaceDir updated

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
