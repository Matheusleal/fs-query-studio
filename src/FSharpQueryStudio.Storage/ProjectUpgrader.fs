namespace FSharpQueryStudio.Storage

open System
open System.IO
open System.Text.Json
open FSharpQueryStudio.Protocol

/// Report describing the actions taken during an automatic or manual project upgrade.
type UpgradeReport =
    { Upgraded: bool
      PreviousVersion: int
      CurrentVersion: int
      Actions: string list }

/// Extensible interface for step-wise project and schema migrations.
type IMigrationStep =
    abstract member FromVersion: int
    abstract member ToVersion: int
    abstract member Description: string
    abstract member Apply: projectRoot: string -> Result<string list, string>

module ProjectUpgradeConstants =
    [<Literal>]
    let CurrentSchemaVersion = 2

    let currentExtensionVersion () : string =
        match System.Reflection.Assembly.GetExecutingAssembly().GetName().Version with
        | null -> "0.3.2"
        | v when v.Major = 0 && v.Minor = 0 && v.Build = 0 -> "0.3.2"
        | v when v.Major = 1 && v.Minor = 0 && v.Build = 0 -> "0.3.2"
        | v -> sprintf "%d.%d.%d" v.Major v.Minor v.Build

    let parseConnectionsFromRawJson (json: string) : ConnectionDefinition list =
        try
            use doc = JsonDocument.Parse json
            match doc.RootElement.ValueKind with
            | JsonValueKind.Array ->
                JsonSerializer.Deserialize<ConnectionDefinition list>(json, Serialization.options)
            | JsonValueKind.Object ->
                match doc.RootElement.TryGetProperty("connections") with
                | true, el when el.ValueKind = JsonValueKind.Array ->
                    JsonSerializer.Deserialize<ConnectionDefinition list>(el.GetRawText(), Serialization.options)
                | _ -> []
            | _ -> []
        with _ ->
            []

    let detectSchemaVersion (json: string) : int =
        try
            use doc = JsonDocument.Parse json
            match doc.RootElement.ValueKind with
            | JsonValueKind.Array -> 1
            | JsonValueKind.Object ->
                match doc.RootElement.TryGetProperty("schemaVersion") with
                | true, el when el.ValueKind = JsonValueKind.Number -> el.GetInt32()
                | _ ->
                    match doc.RootElement.TryGetProperty("$schemaVersion") with
                    | true, el when el.ValueKind = JsonValueKind.Number -> el.GetInt32()
                    | _ -> 1
            | _ -> 1
        with _ ->
            1

    let writeV2WorkspaceJson (filePath: string) (connections: ConnectionDefinition list) : unit =
        let dir = Path.GetDirectoryName filePath
        if not (String.IsNullOrWhiteSpace dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory dir |> ignore

        let payload =
            {| schemaVersion = CurrentSchemaVersion
               extensionVersion = currentExtensionVersion ()
               updatedAt = DateTimeOffset.UtcNow.ToString("o")
               connections = connections |}

        let json = JsonSerializer.Serialize(payload, Serialization.options)
        File.WriteAllText(filePath, json)

/// Migration step for Legacy / Schema v1 -> Schema v2.
/// 1. Relocates legacy `<projectRoot>/workspace.json` into `<projectRoot>/.fsqs/workspace.json`.
/// 2. Relocates legacy `<projectRoot>/generated/` into `<projectRoot>/.fsqs/generated/`.
/// 3. Migrates `workspace.json` content to Schema v2 with `schemaVersion = 2` and `extensionVersion`.
type MigrationStep1To2() =
    interface IMigrationStep with
        member _.FromVersion = 1
        member _.ToVersion = 2
        member _.Description = "Migrate legacy root files to .fsqs/ and upgrade workspace.json to Schema v2"

        member _.Apply(projectRoot: string) =
            try
                let actions = ResizeArray<string>()
                let trimmed = projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                let isGlobalAppData =
                    (Path.GetFileName trimmed).Equals("FSharpQueryStudio", StringComparison.OrdinalIgnoreCase)

                let actualRoot, fsqsDir =
                    if isGlobalAppData then
                        trimmed, trimmed
                    elif (Path.GetFileName trimmed).Equals(".fsqs", StringComparison.OrdinalIgnoreCase) then
                        let parent = Path.GetDirectoryName trimmed
                        if String.IsNullOrWhiteSpace parent then trimmed, trimmed else parent, trimmed
                    else
                        trimmed, Path.Combine(trimmed, ".fsqs")

                let rootWs = Path.Combine(actualRoot, "workspace.json")
                let targetWs = Path.Combine(fsqsDir, "workspace.json")
                let rootGenerated = Path.Combine(actualRoot, "generated")
                let targetGenerated = Path.Combine(fsqsDir, "generated")

                // 1. Relocate legacy root workspace.json if in project folder
                if not isGlobalAppData && File.Exists rootWs then
                    Directory.CreateDirectory fsqsDir |> ignore
                    if File.Exists targetWs then
                        // Merge connections from root into target if target already exists
                        let rootConns = ProjectUpgradeConstants.parseConnectionsFromRawJson (File.ReadAllText rootWs)
                        let targetConns = ProjectUpgradeConstants.parseConnectionsFromRawJson (File.ReadAllText targetWs)
                        let merged =
                            let targetIds = targetConns |> List.map (fun c -> c.Id) |> Set.ofList
                            let missingInTarget = rootConns |> List.filter (fun c -> not (Set.contains c.Id targetIds))
                            targetConns @ missingInTarget

                        ProjectUpgradeConstants.writeV2WorkspaceJson targetWs merged
                        actions.Add "Merged legacy root workspace.json into .fsqs/workspace.json"
                        try File.Delete rootWs with _ -> ()
                    else
                        // Read root content; if v1, convert immediately to v2
                        let rootConns = ProjectUpgradeConstants.parseConnectionsFromRawJson (File.ReadAllText rootWs)
                        ProjectUpgradeConstants.writeV2WorkspaceJson targetWs rootConns
                        try File.Delete rootWs with _ -> ()
                        actions.Add "Relocated root workspace.json to .fsqs/workspace.json (upgraded to Schema v2)"

                // 2. Relocate legacy root generated/ directory if present
                if not isGlobalAppData && Directory.Exists rootGenerated then
                    Directory.CreateDirectory targetGenerated |> ignore
                    for file in Directory.GetFiles rootGenerated do
                        let dest = Path.Combine(targetGenerated, Path.GetFileName file)
                        if not (File.Exists dest) then
                            File.Move(file, dest)
                        else
                            File.Copy(file, dest, true)
                            try File.Delete file with _ -> ()

                    try Directory.Delete(rootGenerated, true) with _ -> ()
                    actions.Add "Relocated legacy generated/ directory to .fsqs/generated/"

                // 3. Upgrade schema content of workspace.json in .fsqs (or global) if still v1
                if File.Exists targetWs then
                    let json = File.ReadAllText targetWs
                    let ver = ProjectUpgradeConstants.detectSchemaVersion json
                    if ver < 2 then
                        let conns = ProjectUpgradeConstants.parseConnectionsFromRawJson json
                        ProjectUpgradeConstants.writeV2WorkspaceJson targetWs conns
                        actions.Add $"Upgraded workspace.json to Schema v2 (extension version {ProjectUpgradeConstants.currentExtensionVersion ()})"

                Result.Ok (Seq.toList actions)
            with ex ->
                Result.Error ex.Message

/// Project upgrade engine that executes registered migration steps sequentially.
module ProjectUpgrader =

    let CurrentSchemaVersion = ProjectUpgradeConstants.CurrentSchemaVersion
    let currentExtensionVersion () = ProjectUpgradeConstants.currentExtensionVersion ()

    let private migrationSteps : IMigrationStep list =
        [ MigrationStep1To2() ]

    /// Checks whether a given project directory contains legacy artifacts or an outdated schema version.
    let needsUpgrade (projectRoot: string) : bool =
        if String.IsNullOrWhiteSpace projectRoot || not (Directory.Exists projectRoot) then
            false
        else
            let trimmed = projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            let isGlobalAppData =
                (Path.GetFileName trimmed).Equals("FSharpQueryStudio", StringComparison.OrdinalIgnoreCase)

            if not isGlobalAppData then
                let rootWs = Path.Combine(trimmed, "workspace.json")
                let rootGen = Path.Combine(trimmed, "generated")
                if File.Exists rootWs || Directory.Exists rootGen then
                    true
                else
                    let fsqsWs = Path.Combine(trimmed, ".fsqs", "workspace.json")
                    if File.Exists fsqsWs then
                        let ver = ProjectUpgradeConstants.detectSchemaVersion (File.ReadAllText fsqsWs)
                        ver < CurrentSchemaVersion
                    else
                        false
            else
                let globalWs = Path.Combine(trimmed, "workspace.json")
                if File.Exists globalWs then
                    let ver = ProjectUpgradeConstants.detectSchemaVersion (File.ReadAllText globalWs)
                    ver < CurrentSchemaVersion
                else
                    false

    /// Upgrades the project at `projectRoot` sequentially through all migration steps.
    let upgradeWorkspace (projectRoot: string) : UpgradeReport =
        if String.IsNullOrWhiteSpace projectRoot || not (Directory.Exists projectRoot) then
            { Upgraded = false
              PreviousVersion = CurrentSchemaVersion
              CurrentVersion = CurrentSchemaVersion
              Actions = [] }
        else
            let trimmed = projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            let isGlobalAppData =
                (Path.GetFileName trimmed).Equals("FSharpQueryStudio", StringComparison.OrdinalIgnoreCase)

            let fsqsWs =
                if isGlobalAppData then Path.Combine(trimmed, "workspace.json")
                elif (Path.GetFileName trimmed).Equals(".fsqs", StringComparison.OrdinalIgnoreCase) then Path.Combine(trimmed, "workspace.json")
                else Path.Combine(trimmed, ".fsqs", "workspace.json")

            let rootWs = Path.Combine(trimmed, "workspace.json")

            let initialVersion =
                if File.Exists fsqsWs then
                    ProjectUpgradeConstants.detectSchemaVersion (File.ReadAllText fsqsWs)
                elif File.Exists rootWs then
                    ProjectUpgradeConstants.detectSchemaVersion (File.ReadAllText rootWs)
                else
                    CurrentSchemaVersion

            let allActions = ResizeArray<string>()
            let mutable currentVer = initialVersion

            let rec runSteps () =
                let nextStep =
                    migrationSteps
                    |> List.tryFind (fun s -> s.FromVersion = currentVer)

                match nextStep with
                | Some step ->
                    match step.Apply projectRoot with
                    | Result.Ok stepActions ->
                        allActions.AddRange stepActions
                        currentVer <- step.ToVersion
                        runSteps ()
                    | Result.Error err ->
                        allActions.Add $"Migration from v{step.FromVersion} to v{step.ToVersion} failed: {err}"
                | None ->
                    ()

            runSteps ()

            { Upgraded = (allActions.Count > 0)
              PreviousVersion = initialVersion
              CurrentVersion = currentVer
              Actions = Seq.toList allActions }
