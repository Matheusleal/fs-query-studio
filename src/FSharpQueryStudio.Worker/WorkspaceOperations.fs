namespace FSharpQueryStudio.Worker

open System
open System.IO
open System.Text.Json
open Serilog
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Storage
open FSharpQueryStudio.Security
open FSharpQueryStudio.Connectors
open FSharpQueryStudio.Execution

[<CLIMutable>]
type SampleCreateParams =
    { TargetDirectory: string option
      TargetDir: string option }

type SampleCreateResult =
    { SqlitePath: string
      Sqlite2Path: string
      CsvPath: string
      Csv2Path: string
      ExcelPath: string
      Excel2Path: string
      RestUrl: string
      NotebookPath: string
      NotebookPtPath: string
      RegisteredConnections: string list }

type AutoImportSourceItem =
    { Name: string
      Kind: ConnectorKind
      FilePath: string
      RelativePath: string }

type AutoImportDataSourcesResult =
    { TargetDir: string
      DataSourcesDir: string
      FolderCreated: bool
      FoundFilesCount: int
      ImportedCount: int
      Imported: AutoImportSourceItem list
      RemovedCount: int
      SkippedCount: int }

/// Encapsulates workspace filesystem workflows (sample generation, auto-import, empty project initialization).
module WorkspaceOperations =

    /// Generates sample data files and registers standard sample connections.
    let createSampleData (dir: string) (secretStore: ISecretStore) : SampleCreateResult =
        let files = SampleDataGenerator.generateAllSamples dir

        let sqliteDef: ConnectionDefinition =
            { Id = Guid.NewGuid()
              Name = "SampleStore"
              Kind = ConnectorKind.Sqlite
              Configuration = JsonSerializer.SerializeToElement({| filePath = files.SqlitePath |}, Serialization.options)
              SecretReference = None
              IsTrusted = true }

        let sqlite2Def: ConnectionDefinition =
            { Id = Guid.NewGuid()
              Name = "InventoryStore"
              Kind = ConnectorKind.Sqlite
              Configuration = JsonSerializer.SerializeToElement({| filePath = files.Sqlite2Path |}, Serialization.options)
              SecretReference = None
              IsTrusted = true }

        let csvDef: ConnectionDefinition =
            { Id = Guid.NewGuid()
              Name = "SalesFeed"
              Kind = ConnectorKind.Csv
              Configuration = JsonSerializer.SerializeToElement({| filePath = files.CsvPath |}, Serialization.options)
              SecretReference = None
              IsTrusted = true }

        let csv2Def: ConnectionDefinition =
            { Id = Guid.NewGuid()
              Name = "CustomersFeed"
              Kind = ConnectorKind.Csv
              Configuration = JsonSerializer.SerializeToElement({| filePath = files.Csv2Path |}, Serialization.options)
              SecretReference = None
              IsTrusted = true }

        let excelDef: ConnectionDefinition =
            { Id = Guid.NewGuid()
              Name = "RegionalTargets"
              Kind = ConnectorKind.Excel
              Configuration = JsonSerializer.SerializeToElement({| filePath = files.ExcelPath; hasHeader = true |}, Serialization.options)
              SecretReference = None
              IsTrusted = true }

        let excel2Def: ConnectionDefinition =
            { Id = Guid.NewGuid()
              Name = "CommerceOps"
              Kind = ConnectorKind.Excel
              Configuration = JsonSerializer.SerializeToElement({| filePath = files.Excel2Path; hasHeader = true |}, Serialization.options)
              SecretReference = None
              IsTrusted = true }

        let restDef: ConnectionDefinition =
            { Id = Guid.NewGuid()
              Name = "GitHubFSharp"
              Kind = ConnectorKind.RestJson
              Configuration =
                  JsonSerializer.SerializeToElement(
                      {| url = files.RestUrl
                         method = "GET"
                         headers = [ ("Accept", "application/vnd.github+json"); ("User-Agent", "FSharpQueryStudio/1.0") ] |},
                      Serialization.options
                  )
              SecretReference = None
              IsTrusted = true }

        let _ = Workspace.registerConnection dir sqliteDef
        let _ = Workspace.registerConnection dir sqlite2Def
        let _ = Workspace.registerConnection dir csvDef
        let _ = Workspace.registerConnection dir csv2Def
        let _ = Workspace.registerConnection dir excelDef
        let _ = Workspace.registerConnection dir excel2Def
        let updatedInDir = Workspace.registerConnection dir restDef

        Connections.configure (WorkspaceConnectionResolver(updatedInDir, secretStore, dir))
        try
            (EntityBackgroundGenerator.generateForConnectionsAsync dir updatedInDir secretStore).GetAwaiter().GetResult() |> ignore
        with ex ->
            Log.Warning("FS Query Studio: Entity generation after sample creation failed: {Error}", ex.Message)

        Connections.triggerBackgroundSync dir updatedInDir secretStore
        SessionStore.resetAll ()

        { SqlitePath = files.SqlitePath
          Sqlite2Path = files.Sqlite2Path
          CsvPath = files.CsvPath
          Csv2Path = files.Csv2Path
          ExcelPath = files.ExcelPath
          Excel2Path = files.Excel2Path
          RestUrl = files.RestUrl
          NotebookPath = files.NotebookPath
          NotebookPtPath = files.NotebookPtPath
          RegisteredConnections = [ "SampleStore"; "InventoryStore"; "SalesFeed"; "CustomersFeed"; "RegionalTargets"; "CommerceOps"; "GitHubFSharp" ] }

    /// Initializes empty workspace directories and workspace.json.
    let initEmptyProject (rootDir: string) : {| workspacePath: string; targetDir: string; fsqsDir: string |} =
        let fsqsDir = Path.Combine(rootDir, ".fsqs")
        Directory.CreateDirectory(fsqsDir) |> ignore
        let wsFile = Path.Combine(fsqsDir, "workspace.json")
        if not (File.Exists wsFile) then
            Workspace.writeConnections rootDir []

        let generatedDir = Path.Combine(fsqsDir, "generated")
        Directory.CreateDirectory(generatedDir) |> ignore

        {| workspacePath = wsFile; targetDir = rootDir; fsqsDir = fsqsDir |}

    /// Automatically scans for tabular data files in data_sources and registers them in the workspace.
    let autoImportDataSources (rawDir: string) (secretStore: ISecretStore) : AutoImportDataSourcesResult =
        let rootDir, dataSourcesDir =
            let trimmed = rawDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            let leaf = Path.GetFileName trimmed
            if leaf.Equals("data_sources", StringComparison.OrdinalIgnoreCase) then
                let parent = Path.GetDirectoryName trimmed
                let actualRoot = if String.IsNullOrWhiteSpace parent then rawDir else parent
                actualRoot, rawDir
            else
                rawDir, Path.Combine(rawDir, "data_sources")

        let folderCreated = not (Directory.Exists dataSourcesDir)
        if folderCreated then
            Directory.CreateDirectory(dataSourcesDir) |> ignore
            let readmePath = Path.Combine(dataSourcesDir, "README.txt")
            if not (File.Exists readmePath) then
                File.WriteAllText(
                    readmePath,
                    "Place your .csv, .xlsx, .xls, or .sqlite/.db files in this directory.\nF# Query Studio will automatically detect and register them as queryable data sources.\n"
                )

        let fsqsDir = Path.Combine(rootDir, ".fsqs")
        if not (Directory.Exists fsqsDir) && not (rootDir.EndsWith(".fsqs", StringComparison.OrdinalIgnoreCase)) then
            Directory.CreateDirectory(fsqsDir) |> ignore
        let generatedDir = Path.Combine(fsqsDir, "generated")
        if not (Directory.Exists generatedDir) then
            Directory.CreateDirectory(generatedDir) |> ignore

        let supportedExtensions =
            [ ".csv", ConnectorKind("Csv")
              ".xlsx", ConnectorKind("Excel")
              ".xls", ConnectorKind("Excel")
              ".sqlite", ConnectorKind("Sqlite")
              ".sqlite3", ConnectorKind("Sqlite")
              ".db", ConnectorKind("Sqlite") ]

        let candidateFiles =
            if Directory.Exists dataSourcesDir then
                Directory.EnumerateFiles(dataSourcesDir, "*.*", SearchOption.AllDirectories)
                |> Seq.filter (fun file ->
                    let fileName = Path.GetFileName file
                    let ext = Path.GetExtension(file).ToLowerInvariant()
                    not (fileName.StartsWith("~$", StringComparison.OrdinalIgnoreCase))
                    && not (fileName.StartsWith(".", StringComparison.OrdinalIgnoreCase))
                    && (supportedExtensions |> List.exists (fun (e, _) -> e = ext)))
                |> Seq.toList
            else
                []

        let existingConnections = Workspace.readConnections rootDir

        let getExistingFilePath (conn: ConnectionDefinition) : string option =
            try
                if conn.Configuration.ValueKind = JsonValueKind.Object then
                    let mutable prop = Unchecked.defaultof<JsonElement>
                    if conn.Configuration.TryGetProperty("filePath", &prop) && prop.ValueKind = JsonValueKind.String then
                        Some (prop.GetString())
                    else
                        None
                elif conn.Configuration.ValueKind = JsonValueKind.String then
                    Some (conn.Configuration.GetString())
                else
                    None
            with _ ->
                None

        let isMatchingFile (registeredPath: string) (actualFile: string) : bool =
            try
                let full1 = Path.GetFullPath(if Path.IsPathRooted registeredPath then registeredPath else Path.Combine(rootDir, registeredPath))
                let full2 = Path.GetFullPath(actualFile)
                String.Equals(
                    full1,
                    full2,
                    (if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal)
                )
            with _ ->
                String.Equals(registeredPath, actualFile, StringComparison.OrdinalIgnoreCase)

        let isAlreadyRegistered (actualFile: string) =
            existingConnections
            |> List.exists (fun c ->
                match getExistingFilePath c with
                | Some p -> isMatchingFile p actualFile
                | None -> false)

        let mutable usedNames =
            existingConnections
            |> List.map (fun c -> c.Name)
            |> Set.ofList

        let makeUniqueName (candidate: string) : string =
            if not (Set.contains candidate usedNames) then
                usedNames <- Set.add candidate usedNames
                candidate
            else
                let mutable idx = 2
                while Set.contains $"{candidate}_{idx}" usedNames do
                    idx <- idx + 1
                let unique = $"{candidate}_{idx}"
                usedNames <- Set.add unique usedNames
                unique

        let toPascalCase (s: string) : string =
            let parts = s.Split([| '_'; '-'; ' '; '.' |], StringSplitOptions.RemoveEmptyEntries)
            if parts.Length = 0 then
                "DataSource"
            else
                let capitalized =
                    parts
                    |> Array.map (fun part ->
                        let clean = CodeGenerationHelpers.cleanIdentifier part
                        if clean.Length > 0 && Char.IsLower(clean.[0]) then
                            Char.ToUpper(clean.[0]).ToString() + clean.Substring(1)
                        else
                            clean)
                    |> String.concat ""
                if String.IsNullOrWhiteSpace capitalized then "DataSource" else capitalized

        let mutable newlyImported: AutoImportSourceItem list = []
        let mutable newConnections: ConnectionDefinition list = []

        for file in candidateFiles do
            if not (isAlreadyRegistered file) then
                let ext = Path.GetExtension(file).ToLowerInvariant()
                let kind =
                    supportedExtensions
                    |> List.tryFind (fun (e, _) -> e = ext)
                    |> Option.map snd
                    |> Option.defaultValue ConnectorKind.Csv

                let baseName = Path.GetFileNameWithoutExtension file
                let connName = makeUniqueName (toPascalCase baseName)

                let relPath =
                    try
                        Path.GetRelativePath(rootDir, file).Replace('\\', '/')
                    with _ ->
                        file

                let configElement =
                    match kind.Id with
                    | "Excel" ->
                        JsonSerializer.SerializeToElement({| filePath = relPath; hasHeader = true |}, Serialization.options)
                    | "Sqlite" ->
                        JsonSerializer.SerializeToElement({| filePath = relPath |}, Serialization.options)
                    | _ -> // Csv
                        JsonSerializer.SerializeToElement({| filePath = relPath |}, Serialization.options)

                let def: ConnectionDefinition =
                    { Id = Guid.NewGuid()
                      Name = connName
                      Kind = kind
                      Configuration = configElement
                      SecretReference = None
                      IsTrusted = true }

                newConnections <- newConnections @ [ def ]
                newlyImported <- newlyImported @ [ { Name = connName; Kind = kind; FilePath = file; RelativePath = relPath } ]

        let missingDataSourcesConnections =
            existingConnections
            |> List.filter (fun conn ->
                match getExistingFilePath conn with
                | Some pathStr ->
                    let norm = pathStr.Replace('\\', '/')
                    let isUnderDataSources =
                        norm.StartsWith("data_sources/", StringComparison.OrdinalIgnoreCase)
                        || norm.Contains("/data_sources/")
                        || (try
                                let full = Path.GetFullPath(if Path.IsPathRooted pathStr then pathStr else Path.Combine(rootDir, pathStr))
                                let dsFull = Path.GetFullPath(dataSourcesDir)
                                full.StartsWith(dsFull, StringComparison.OrdinalIgnoreCase)
                            with _ -> false)
                    if isUnderDataSources then
                        let full = if Path.IsPathRooted pathStr then pathStr else Path.Combine(rootDir, pathStr)
                        not (File.Exists full)
                    else
                        false
                | None -> false)

        let remainingConnections =
            if missingDataSourcesConnections.Length > 0 then
                let missingIds = missingDataSourcesConnections |> List.map (fun c -> c.Id) |> Set.ofList
                existingConnections |> List.filter (fun c -> not (Set.contains c.Id missingIds))
            else
                existingConnections

        if newConnections.Length > 0 || missingDataSourcesConnections.Length > 0 then
            let merged = remainingConnections @ newConnections
            Workspace.writeConnections rootDir merged
            Connections.configure (WorkspaceConnectionResolver(merged, secretStore, rootDir))
            try
                (EntityBackgroundGenerator.generateForConnectionsAsync rootDir merged secretStore).GetAwaiter().GetResult() |> ignore
            with ex ->
                Log.Warning("FS Query Studio: Entity generation after auto-import failed: {Error}", ex.Message)
            Connections.triggerBackgroundSync rootDir merged secretStore
            SessionStore.resetAll ()

        { TargetDir = rootDir
          DataSourcesDir = dataSourcesDir
          FolderCreated = folderCreated
          FoundFilesCount = candidateFiles.Length
          ImportedCount = newlyImported.Length
          Imported = newlyImported
          RemovedCount = missingDataSourcesConnections.Length
          SkippedCount = candidateFiles.Length - newlyImported.Length }

