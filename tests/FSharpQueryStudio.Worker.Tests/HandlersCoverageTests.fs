module HandlersCoverageTests

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors
open FSharpQueryStudio.Results
open FSharpQueryStudio.Security
open FSharpQueryStudio.Storage
open FSharpQueryStudio.Worker

let private req (id: string) (method_: string) (paramsJson: string) : WorkerRequestFrame =
    { Id = id
      Method = method_
      Params = JsonDocument.Parse(paramsJson).RootElement }

let private tempDir (label: string) =
    let d = Path.Combine(Path.GetTempPath(), $"fqs-hcov-{label}-{Guid.NewGuid():N}")
    Directory.CreateDirectory d |> ignore
    d

let private cleanup (d: string) =
    try
        Handlers.waitForPendingGeneration ()
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
        if Directory.Exists d then Directory.Delete(d, true)
    with _ -> ()

let private jsonPath (p: string) = p.Replace("\\", "\\\\")

[<Fact>]
let ``configureHostChannel wires entity-mapping and staging-progress callbacks to the host`` () =
    let sent = System.Collections.Generic.List<WorkerRequestFrame>()
    let channel = HostChannel(fun f -> sent.Add f; Task.CompletedTask)
    let prevStatus = EntityBackgroundGenerator.onStatusChanged
    let prevProgress = FileSqliteEngine.GlobalProgressCallback
    try
        Handlers.configureHostChannel channel

        let cb = EntityBackgroundGenerator.onStatusChanged.Value
        cb EntityBackgroundGenerator.EntityMappingStatus.Started
        cb (EntityBackgroundGenerator.EntityMappingStatus.Completed 2)
        cb (EntityBackgroundGenerator.EntityMappingStatus.Failed "err")

        let progress = FileSqliteEngine.GlobalProgressCallback.Value
        progress
            { TableName = "t"
              FilePath = "f.csv"
              Phase = "staging"
              RowCount = Some 10
              CurrentIndex = Some 1
              TotalCount = Some 2
              ElapsedMs = Some 5L
              Message = Some "hi" }
        progress
            { TableName = "t"
              FilePath = "f.csv"
              Phase = "done"
              RowCount = None
              CurrentIndex = None
              TotalCount = None
              ElapsedMs = None
              Message = None }

        Assert.Equal(5, sent.Count)
        Assert.Equal(3, sent |> Seq.filter (fun f -> f.Method = "entityMapping/status") |> Seq.length)
        Assert.Equal(2, sent |> Seq.filter (fun f -> f.Method = "staging/progress") |> Seq.length)
    finally
        EntityBackgroundGenerator.onStatusChanged <- prevStatus
        FileSqliteEngine.GlobalProgressCallback <- prevProgress

[<Fact>]
let ``results/exportCsv returns exportFailed when the target path is a directory`` () =
    let dir = tempDir "export"
    try
        let uri = $"file:///export-fail-{Guid.NewGuid():N}.fsx"
        ResultRetention.set uri { Columns = [ "A" ]; Rows = [| [| box 1 |] |] }
        let resp = Handlers.handleResultsExportCsv (req "x1" "results/exportCsv" $"""{{"documentUri":"{uri}","filePath":"{jsonPath dir}"}}""")
        Assert.True(resp.Error.IsSome)
        Assert.Equal("results/exportFailed", resp.Error.Value.Code)
    finally
        cleanup dir

[<Fact>]
let ``sample and workspace handlers turn failures into structured errors and handle non-object params`` () =
    let lockDir = tempDir "locked"
    try
        let fsqsDir = Path.Combine(lockDir, ".fsqs")
        Directory.CreateDirectory fsqsDir |> ignore
        let wsFile = Path.Combine(fsqsDir, "workspace.json")
        File.WriteAllText(wsFile, """{"schemaVersion":2,"connections":[]}""")

        Handlers.setWorkspaceDirForTests (Some "\u0000bad")
        let s1 = Handlers.handleCreateSampleData (req "s1" "sample/create" "null")
        let s2 = Handlers.handleCreateAdvancedSamples (req "s2" "sample/createAdvanced" "null")
        Handlers.setWorkspaceDirForTests None

        let bad = """{"targetDir":"\u0000bad","workspaceDir":"\u0000bad"}"""
        let s3 = Handlers.handleInitEmptyProject (req "s3" "workspace/init" bad)
        let s4 = Handlers.handleAutoImportDataSources (req "s4" "workspace/autoImport" bad)

        let s5, s6 =
            use _lockStream = new FileStream(wsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            let pLocked = $"""{{"workspaceDir":"{jsonPath lockDir}"}}"""
            let r5 = Handlers.handleWorkspaceUpgrade (req "s5" "workspace/upgrade" pLocked)
            let r6 = (Handlers.handleSyncDataSources (req "s6" "workspace/syncDataSources" pLocked)).Result
            r5, r6

        let codeOf (r: WorkerResponseFrame) = match r.Error with Some e -> e.Code | None -> "<none>"
        let codes = [ codeOf s1; codeOf s2; codeOf s3; codeOf s4; codeOf s5; codeOf s6 ]
        Assert.Equal<string list>(
            [ "sample/createFailed"; "sample/createAdvancedFailed"; "workspace/initFailed"
              "workspace/autoImportFailed"; "workspace/upgradeFailed"; "workspace/syncDataSourcesFailed" ],
            codes)
    finally
        Handlers.setWorkspaceDirForTests None
        cleanup lockDir

[<Fact>]
let ``dispatch routes connector, connection and workspace methods`` () =
    task {
        let dir = tempDir "dispatch"
        Handlers.setWorkspaceDirForTests (Some dir)
        try
            let p = $"""{{"workspaceDir":"{jsonPath dir}"}}"""
            for m in [ "connector/list"; "connection/list"; "workspace/init"; "workspace/autoImport"; "workspace/syncDataSources"; "workspace/upgrade" ] do
                let! resp = Handlers.dispatch (req ("r-" + m) m p)
                Assert.Equal("r-" + m, resp.Id)
        finally
            Handlers.setWorkspaceDirForTests None
            cleanup dir
    }

[<Fact>]
let ``connection/list drops leaked global sample connections and merges global ones`` () =
    task {
        let globalDir = tempDir "global"
        let localDir = tempDir "local"
        Handlers.setWorkspaceDirForTests (Some globalDir)
        try
            let leaked: ConnectionDefinition =
                { Id = Guid.NewGuid()
                  Name = "SampleStore"
                  Kind = ConnectorKind("Sqlite")
                  Configuration = JsonDocument.Parse("""{"filePath":"missing/nowhere.db"}""").RootElement
                  SecretReference = None
                  IsTrusted = true }
            let kept: ConnectionDefinition =
                { leaked with Id = Guid.NewGuid(); Name = "GlobalOnly" }
            Workspace.writeConnections globalDir [ leaked; kept ]

            let! resp = Handlers.dispatch (req "cl1" "connection/list" $"""{{"workspaceDir":"{jsonPath localDir}"}}""")
            let names =
                resp.Result.Value.GetProperty("connections").EnumerateArray()
                |> Seq.map (fun e -> e.GetProperty("name").GetString())
                |> Seq.toList
            Assert.Contains("GlobalOnly", names)
            Assert.DoesNotContain("SampleStore", names)

            let! resp2 = Handlers.dispatch (req "cl2" "connection/list" $"""{{"workspaceDir":"{jsonPath globalDir}"}}""")
            Assert.True(resp2.Error.IsNone)
        finally
            Handlers.setWorkspaceDirForTests None
            cleanup globalDir
            cleanup localDir
    }

[<Fact>]
let ``evaluate heals missing or empty sample files declared in the workspace`` () =
    task {
        let dir = tempDir "heal"
        Handlers.setWorkspaceDirForTests (Some dir)
        try
            let mk (name: string) (file: string) (kind: string) : ConnectionDefinition =
                File.WriteAllBytes(Path.Combine(dir, file), [||])
                { Id = Guid.NewGuid()
                  Name = name
                  Kind = ConnectorKind(kind)
                  Configuration = JsonDocument.Parse($"""{{"filePath":"{file}"}}""").RootElement
                  SecretReference = None
                  IsTrusted = true }
            let conns =
                [ mk "SampleStore" "a.db" "Sqlite"
                  mk "InventoryStore" "b.db" "Sqlite"
                  mk "SalesFeed" "c.csv" "Csv"
                  mk "CustomersFeed" "d.csv" "Csv"
                  mk "RegionalTargets" "e.xlsx" "Excel"
                  mk "CommerceOps" "f.xlsx" "Excel"
                  mk "ProductCatalog" "g.json" "Json"
                  mk "LogisticsHub" "h.xml" "Xml" ]
            Workspace.writeConnections dir conns

            let docUri = "file:///" + dir.Replace("\\", "/") + "/script.fsx"
            let evalParams: EvaluateParams =
                { Source = "1 + 1"; SessionId = None; Mode = Isolated; DocumentUri = docUri; IsNotebookCell = false }
            let request: WorkerRequestFrame =
                { Id = "heal"
                  Method = "execution/evaluate"
                  Params = JsonSerializer.SerializeToElement(evalParams, Serialization.options) }
            let! _ = Handlers.dispatch request
            Handlers.waitForPendingGeneration ()

            for f in [ "a.db"; "c.csv"; "e.xlsx"; "g.json"; "h.xml" ] do
                Assert.True(FileInfo(Path.Combine(dir, f)).Length > 0L, $"{f} was not healed")
        finally
            Handlers.setWorkspaceDirForTests None
            cleanup dir
    }

[<Fact>]
let ``autoImportDataSources handles a data_sources root, name collisions and string configs`` () =
    let root = tempDir "autoimport"
    try
        let ds = Path.Combine(root, "data_sources")
        Directory.CreateDirectory ds |> ignore
        File.WriteAllText(Path.Combine(ds, "test_a.csv"), "Id,Name\n1,x\n")
        File.WriteAllText(Path.Combine(ds, "test-a.json"), """[{"Id":1}]""")

        let prior: ConnectionDefinition =
            { Id = Guid.NewGuid()
              Name = "Legacy"
              Kind = ConnectorKind("Csv")
              Configuration = JsonDocument.Parse("\"data_sources/gone.csv\"").RootElement
              SecretReference = None
              IsTrusted = true }
        Workspace.writeConnections root [ prior ]

        let result = WorkspaceOperations.autoImportDataSources ds (InMemorySecretStore())
        Handlers.waitForPendingGeneration ()
        Assert.Equal(2, result.ImportedCount)
        Assert.Equal(1, result.RemovedCount)
        let names = result.Imported |> List.map (fun i -> i.Name)
        Assert.Equal(2, names |> List.distinct |> List.length)
    finally
        cleanup root

[<Fact>]
let ``createSampleData and createAdvancedSamples wrappers use the default language`` () =
    let dir = tempDir "wrappers"
    try
        let s = WorkspaceOperations.createSampleData dir (InMemorySecretStore())
        Assert.True(File.Exists s.SqlitePath)
        let a = WorkspaceOperations.createAdvancedSamples dir [ "sqlite" ] (InMemorySecretStore())
        Assert.NotNull(box a)
    finally
        cleanup dir
