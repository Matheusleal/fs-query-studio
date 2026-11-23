module Tests

open System
open System.IO
open System.Text.Json
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Storage
open FSharpQueryStudio.Security

let private tempWorkspaceDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "fqs-workspace-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

let private sampleConnection (name: string) : ConnectionDefinition =
    {
        Id = Guid.NewGuid()
        Name = name
        Kind = ConnectorKind("Sqlite")
        Configuration = JsonDocument.Parse("""{"filePath":"C:/data/sales.db"}""").RootElement
        SecretReference = Some "vault://sales-db-cred"
        IsTrusted = true
    }

/// `JsonElement` doesn't have value-based `Equals` across separately
/// parsed documents (it compares the underlying `JsonDocument` reference),
/// so a plain record-list `Assert.Equal` would fail on `Configuration`
/// even when the JSON itself is identical. Compare every field
/// explicitly instead, using `GetRawText()` for `Configuration`.
let private assertSameConnections (expected: ConnectionDefinition list) (actual: ConnectionDefinition list) =
    Assert.Equal(expected.Length, actual.Length)

    List.iter2
        (fun (e: ConnectionDefinition) (a: ConnectionDefinition) ->
            Assert.Equal(e.Id, a.Id)
            Assert.Equal(e.Name, a.Name)
            Assert.Equal(e.Kind, a.Kind)
            Assert.Equal(JsonSerializer.Serialize(e.Configuration), JsonSerializer.Serialize(a.Configuration))
            Assert.Equal(e.SecretReference, a.SecretReference)
            Assert.Equal(e.IsTrusted, a.IsTrusted))
        expected
        actual

[<Fact>]
let ``writeConnections then readConnections round-trips a ConnectionDefinition list`` () =
    let dir = tempWorkspaceDir ()
    let connections = [ sampleConnection "Sales"; sampleConnection "Inventory" ]

    Workspace.writeConnections dir connections

    assertSameConnections connections (Workspace.readConnections dir)

[<Fact>]
let ``readConnections returns an empty list when workspace.json does not yet exist`` () =
    let dir = tempWorkspaceDir ()

    Assert.Empty(Workspace.readConnections dir)

[<Fact>]
let ``the written workspace.json exposes only non-secret connection metadata in Schema v2`` () =
    let dir = tempWorkspaceDir ()
    let connection = sampleConnection "Sales"

    Workspace.writeConnections dir [ connection ]

    let wsFile = Workspace.workspaceFilePath dir
    Assert.True(File.Exists wsFile)
    Assert.Contains(".fsqs", wsFile)

    let json = File.ReadAllText wsFile
    let doc = JsonDocument.Parse(json)

    Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind)
    Assert.Equal(2, doc.RootElement.GetProperty("schemaVersion").GetInt32())
    Assert.False(String.IsNullOrWhiteSpace(doc.RootElement.GetProperty("extensionVersion").GetString()))

    let topLevelPropertyNames =
        doc.RootElement.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Seq.toList

    Assert.Equal("connections", List.last topLevelPropertyNames)

    let element =
        doc.RootElement.GetProperty("connections").EnumerateArray() |> Seq.exactlyOne

    let propertyNames =
        element.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Set.ofSeq

    Assert.Equal<Set<string>>(
        Set.ofList [ "id"; "name"; "kind"; "configuration"; "secretReference"; "isTrusted" ],
        propertyNames
    )

    Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase)

[<Fact>]
let ``registerConnection appends a new connection and persists it in Schema v2`` () =
    let dir = tempWorkspaceDir ()
    let connection = sampleConnection "Sales"

    let result = Workspace.registerConnection dir connection

    assertSameConnections [ connection ] result
    assertSameConnections [ connection ] (Workspace.readConnections dir)

    let config = Workspace.readConfiguration dir
    Assert.Equal(2, config.SchemaVersion)

[<Fact>]
let ``registerConnection replaces an existing connection with the same Id instead of duplicating it`` () =
    let dir = tempWorkspaceDir ()
    let original = sampleConnection "Sales"
    Workspace.writeConnections dir [ original ]

    let renamed =
        { original with
            Name = "Sales (renamed)"
        }

    let result = Workspace.registerConnection dir renamed

    assertSameConnections [ renamed ] result
    assertSameConnections [ renamed ] (Workspace.readConnections dir)

[<Fact>]
let ``a registered connection's credential lives in the secret store, never in workspace.json`` () =
    let dir = tempWorkspaceDir ()
    let secretStore = InMemorySecretStore() :> ISecretStore
    let reference = "vault://sales-db-cred"
    secretStore.Save reference "correct horse battery staple"

    let connection =
        { sampleConnection "Sales" with
            SecretReference = Some reference
        }

    Workspace.registerConnection dir connection |> ignore

    let json = File.ReadAllText(Workspace.workspaceFilePath dir)
    Assert.DoesNotContain("correct horse battery staple", json)
    Assert.Equal(Some "correct horse battery staple", secretStore.TryRead reference)

[<Fact>]
let ``registerConnection preserves other connections when adding a second one`` () =
    let dir = tempWorkspaceDir ()
    let first = sampleConnection "Sales"
    Workspace.writeConnections dir [ first ]

    let second = sampleConnection "Inventory"
    let result = Workspace.registerConnection dir second

    assertSameConnections [ first; second ] result

[<Fact>]
let ``removeConnection drops the matching Id and persists the result`` () =
    let dir = tempWorkspaceDir ()
    let target = sampleConnection "Sales"
    Workspace.writeConnections dir [ target ]

    let result = Workspace.removeConnection dir target.Id

    Assert.Empty(result)
    Assert.Empty(Workspace.readConnections dir)

[<Fact>]
let ``removeConnection preserves every other connection`` () =
    let dir = tempWorkspaceDir ()
    let keep = sampleConnection "Sales"
    let drop = sampleConnection "Inventory"
    Workspace.writeConnections dir [ keep; drop ]

    let result = Workspace.removeConnection dir drop.Id

    assertSameConnections [ keep ] result
    assertSameConnections [ keep ] (Workspace.readConnections dir)

[<Fact>]
let ``removeConnection for an Id that isn't registered is a no-op, not an error`` () =
    let dir = tempWorkspaceDir ()
    let existing = sampleConnection "Sales"
    Workspace.writeConnections dir [ existing ]

    let result = Workspace.removeConnection dir (System.Guid.NewGuid())

    assertSameConnections [ existing ] result

[<Fact>]
let ``readConfiguration on legacy Schema v1 plain JSON array automatically upgrades it to Schema v2 and preserves connections``
    ()
    =
    let dir = tempWorkspaceDir ()
    let conn = sampleConnection "LegacyConn"
    let legacyArrayJson = JsonSerializer.Serialize([ conn ], Serialization.options)
    let fsqsDir = Path.Combine(dir, ".fsqs")
    Directory.CreateDirectory fsqsDir |> ignore
    File.WriteAllText(Path.Combine(fsqsDir, "workspace.json"), legacyArrayJson)

    let config = Workspace.readConfiguration dir
    Assert.Equal(2, config.SchemaVersion)
    Assert.Single(config.Connections) |> ignore
    Assert.Equal("LegacyConn", config.Connections.[0].Name)

[<Fact>]
let ``ProjectUpgrader relocates root workspace.json and generated folder to .fsqs and upgrades to Schema v2`` () =
    let dir = tempWorkspaceDir ()
    let conn = sampleConnection "RootConn"
    let rootWs = Path.Combine(dir, "workspace.json")
    let legacyArrayJson = JsonSerializer.Serialize([ conn ], Serialization.options)
    File.WriteAllText(rootWs, legacyArrayJson)

    let rootGen = Path.Combine(dir, "generated")
    Directory.CreateDirectory rootGen |> ignore
    File.WriteAllText(Path.Combine(rootGen, "GeneratedEntities.fs"), "// generated code")

    Assert.True(ProjectUpgrader.needsUpgrade dir)

    let report = ProjectUpgrader.upgradeWorkspace dir
    Assert.True(report.Upgraded)
    Assert.Equal(1, report.PreviousVersion)
    Assert.Equal(2, report.CurrentVersion)
    Assert.True(report.Actions.Length >= 2)

    // Root legacy files must be removed
    Assert.False(File.Exists rootWs)
    Assert.False(Directory.Exists rootGen)

    // Destination .fsqs files must exist
    let targetWs = Path.Combine(dir, ".fsqs", "workspace.json")
    let targetGen = Path.Combine(dir, ".fsqs", "generated", "GeneratedEntities.fs")
    Assert.True(File.Exists targetWs)
    Assert.True(File.Exists targetGen)

    // workspace.json must now be Schema v2
    let config = Workspace.readConfiguration dir
    Assert.Equal(2, config.SchemaVersion)
    Assert.Single(config.Connections) |> ignore
    Assert.Equal("RootConn", config.Connections.[0].Name)

    // Re-running upgrade must be idempotent
    Assert.False(ProjectUpgrader.needsUpgrade dir)
    let secondReport = ProjectUpgrader.upgradeWorkspace dir
    Assert.False(secondReport.Upgraded)

[<Fact>]
let ``Workspace readConnections automatically runs upgrader on legacy root project`` () =
    let dir = tempWorkspaceDir ()
    let conn = sampleConnection "AutoUpgradedConn"
    let rootWs = Path.Combine(dir, "workspace.json")
    File.WriteAllText(rootWs, JsonSerializer.Serialize([ conn ], Serialization.options))

    // Reading connections should automatically trigger the migration
    let conns = Workspace.readConnections dir
    Assert.Single(conns) |> ignore
    Assert.Equal("AutoUpgradedConn", conns.[0].Name)

    // Root workspace.json is cleaned up and moved to .fsqs/workspace.json
    Assert.False(File.Exists rootWs)
    Assert.True(File.Exists(Path.Combine(dir, ".fsqs", "workspace.json")))

[<Fact>]
let ``ProjectUpgradeConstants parses connections from array, object, missing properties, and invalid json`` () =
    let conn = sampleConnection "TestConn"
    let arrayJson = JsonSerializer.Serialize([ conn ], Serialization.options)
    let parsedFromArray = ProjectUpgradeConstants.parseConnectionsFromRawJson arrayJson
    Assert.Single(parsedFromArray) |> ignore
    Assert.Equal("TestConn", parsedFromArray.[0].Name)

    let objJson = sprintf """{"schemaVersion": 2, "connections": %s}""" arrayJson
    let parsedFromObj = ProjectUpgradeConstants.parseConnectionsFromRawJson objJson
    Assert.Single(parsedFromObj) |> ignore
    Assert.Equal("TestConn", parsedFromObj.[0].Name)

    let objNoConnections = """{"schemaVersion": 2, "other": 123}"""
    Assert.Empty(ProjectUpgradeConstants.parseConnectionsFromRawJson objNoConnections)

    let invalidJson = "{ not valid json }"
    Assert.Empty(ProjectUpgradeConstants.parseConnectionsFromRawJson invalidJson)

    let primitiveJson = "42"
    Assert.Empty(ProjectUpgradeConstants.parseConnectionsFromRawJson primitiveJson)

[<Fact>]
let ``ProjectUpgradeConstants detectSchemaVersion handles array, schemaVersion, dollarSchemaVersion, missing, and invalid json``
    ()
    =
    Assert.Equal(1, ProjectUpgradeConstants.detectSchemaVersion "[]")
    Assert.Equal(2, ProjectUpgradeConstants.detectSchemaVersion """{"schemaVersion": 2}""")
    Assert.Equal(2, ProjectUpgradeConstants.detectSchemaVersion """{"$schemaVersion": 2}""")
    Assert.Equal(1, ProjectUpgradeConstants.detectSchemaVersion """{"other": "value"}""")
    Assert.Equal(1, ProjectUpgradeConstants.detectSchemaVersion "{ invalid json }")
    Assert.Equal(1, ProjectUpgradeConstants.detectSchemaVersion "\"string\"")

[<Fact>]
let ``ProjectUpgrader handles invalid or empty paths cleanly`` () =
    Assert.False(ProjectUpgrader.needsUpgrade "")
    Assert.False(ProjectUpgrader.needsUpgrade "   ")
    Assert.False(ProjectUpgrader.needsUpgrade (Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))))

    let reportEmpty = ProjectUpgrader.upgradeWorkspace ""
    Assert.False(reportEmpty.Upgraded)
    Assert.Equal(2, reportEmpty.CurrentVersion)

    let reportNonExistent =
        ProjectUpgrader.upgradeWorkspace (Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")))

    Assert.False(reportNonExistent.Upgraded)

[<Fact>]
let ``MigrationStep1To2 exposes metadata properties`` () =
    let step = MigrationStep1To2() :> IMigrationStep
    Assert.Equal(1, step.FromVersion)
    Assert.Equal(2, step.ToVersion)
    Assert.False(String.IsNullOrWhiteSpace step.Description)

[<Fact>]
let ``ProjectUpgrader merges connections when target .fsqs workspace.json already exists`` () =
    let dir = tempWorkspaceDir ()
    let connA = sampleConnection "ConnA"
    let connB_root = sampleConnection "ConnB"

    let connB_target =
        { connB_root with
            Name = "ConnB_UpdatedInTarget"
        }

    let connC = sampleConnection "ConnC"

    let fsqsDir = Path.Combine(dir, ".fsqs")
    Directory.CreateDirectory fsqsDir |> ignore
    let targetWs = Path.Combine(fsqsDir, "workspace.json")
    ProjectUpgradeConstants.writeV2WorkspaceJson targetWs [ connB_target; connC ]

    let rootWs = Path.Combine(dir, "workspace.json")

    let rootJson =
        JsonSerializer.Serialize([ connA; connB_root ], Serialization.options)

    File.WriteAllText(rootWs, rootJson)

    Assert.True(ProjectUpgrader.needsUpgrade dir)
    let report = ProjectUpgrader.upgradeWorkspace dir
    Assert.True(report.Upgraded)
    Assert.Contains(report.Actions, fun a -> a.Contains("Merged legacy root workspace.json into .fsqs/workspace.json"))

    Assert.False(File.Exists rootWs)
    let conns = Workspace.readConnections dir
    Assert.Equal(3, conns.Length)
    // Target's connB was preserved, connA was added from root, connC remained
    Assert.Contains(conns, fun c -> c.Name = "ConnA")
    Assert.Contains(conns, fun c -> c.Name = "ConnB_UpdatedInTarget")
    Assert.Contains(conns, fun c -> c.Name = "ConnC")

[<Fact>]
let ``ProjectUpgrader handles generated folder when destination files already exist`` () =
    let dir = tempWorkspaceDir ()
    let fsqsDir = Path.Combine(dir, ".fsqs", "generated")
    Directory.CreateDirectory fsqsDir |> ignore
    let existingFile = Path.Combine(fsqsDir, "Entities.fs")
    File.WriteAllText(existingFile, "// old version")

    let rootGen = Path.Combine(dir, "generated")
    Directory.CreateDirectory rootGen |> ignore
    let rootFile = Path.Combine(rootGen, "Entities.fs")
    File.WriteAllText(rootFile, "// new version from root")

    Assert.True(ProjectUpgrader.needsUpgrade dir)
    let report = ProjectUpgrader.upgradeWorkspace dir
    Assert.True(report.Upgraded)
    Assert.False(Directory.Exists rootGen)
    Assert.True(File.Exists existingFile)
    Assert.Equal("// new version from root", File.ReadAllText existingFile)

[<Fact>]
let ``Global AppData directory ending in FSharpQueryStudio upgrades in-place without creating .fsqs folder`` () =
    let baseDir =
        Path.Combine(Path.GetTempPath(), "test-global-" + Guid.NewGuid().ToString("N"))

    let globalDir = Path.Combine(baseDir, "FSharpQueryStudio")
    Directory.CreateDirectory globalDir |> ignore

    try
        let conn = sampleConnection "GlobalConn"
        let wsFile = Path.Combine(globalDir, "workspace.json")
        File.WriteAllText(wsFile, JsonSerializer.Serialize([ conn ], Serialization.options))

        Assert.Equal(globalDir, Workspace.resolveStorageDir globalDir)
        Assert.True(ProjectUpgrader.needsUpgrade globalDir)

        let report = ProjectUpgrader.upgradeWorkspace globalDir
        Assert.True(report.Upgraded)
        Assert.False(Directory.Exists(Path.Combine(globalDir, ".fsqs")))

        let conns = Workspace.readConnections globalDir
        Assert.Single(conns) |> ignore
        Assert.Equal("GlobalConn", conns.[0].Name)

        // Test ensureProjectUpgraded
        let reportAgain = Workspace.ensureProjectUpgraded globalDir
        Assert.False(reportAgain.Upgraded)
    finally
        try
            Directory.Delete(baseDir, true)
        with _ ->
            ()

[<Fact>]
let ``Storage and Workspace handle directories ending in .fsqs directly`` () =
    let baseDir = tempWorkspaceDir ()
    let fsqsDir = Path.Combine(baseDir, ".fsqs")
    Directory.CreateDirectory fsqsDir |> ignore

    Assert.Equal(fsqsDir, Workspace.resolveStorageDir fsqsDir)
    let withSlash = fsqsDir + Path.DirectorySeparatorChar.ToString()
    Assert.Equal(withSlash, Workspace.resolveStorageDir withSlash)

    let conn = sampleConnection "DirectFsqsConn"
    let wsFile = Path.Combine(fsqsDir, "workspace.json")
    File.WriteAllText(wsFile, JsonSerializer.Serialize([ conn ], Serialization.options))

    let report = ProjectUpgrader.upgradeWorkspace fsqsDir
    Assert.True(report.Upgraded)

[<Fact>]
let ``Workspace readConfiguration handles varied formats: dollarSchemaVersion, missing extension, invalid date, primitive JSON``
    ()
    =
    let dir = tempWorkspaceDir ()
    let storageDir = Path.Combine(dir, ".fsqs")
    Directory.CreateDirectory storageDir |> ignore
    let wsFile = Path.Combine(storageDir, "workspace.json")

    // Format with $schemaVersion, missing extensionVersion, invalid updatedAt, no connections array
    let json1 =
        """
    {
        "$schemaVersion": 2,
        "updatedAt": "invalid-date-string"
    }
    """

    File.WriteAllText(wsFile, json1)
    let cfg1 = Workspace.readConfiguration dir
    Assert.Equal(2, cfg1.SchemaVersion)
    Assert.Equal("0.8.0", cfg1.ExtensionVersion)
    Assert.True(cfg1.UpdatedAt.IsNone)
    Assert.Empty(cfg1.Connections)

    // Format with primitive JSON string
    File.WriteAllText(wsFile, "\"primitive string\"")
    let cfg2 = Workspace.readConfiguration dir
    Assert.Equal(2, cfg2.SchemaVersion)
    Assert.Empty(cfg2.Connections)

    // writeConfiguration with empty ExtensionVersion falls back to currentExtensionVersion
    let emptyExtConfig =
        {
            SchemaVersion = 2
            ExtensionVersion = ""
            UpdatedAt = None
            Connections = []
        }

    Workspace.writeConfiguration dir emptyExtConfig
    let cfg3 = Workspace.readConfiguration dir
    Assert.Equal("0.8.0", cfg3.ExtensionVersion)

[<Fact>]
let ``workspaceFilePath and writeConfiguration invoke upgrader when needsUpgrade is true`` () =
    let dir = tempWorkspaceDir ()
    File.WriteAllText(Path.Combine(dir, "workspace.json"), "[]")
    Assert.True(ProjectUpgrader.needsUpgrade dir)

    // workspaceFilePath triggers upgrade
    let p = Workspace.workspaceFilePath dir
    Assert.True(File.Exists p)
    Assert.Contains(".fsqs", p)

    // writeConfiguration on a dir with legacy root file triggers upgrade
    let dir2 = tempWorkspaceDir ()
    File.WriteAllText(Path.Combine(dir2, "workspace.json"), "[]")

    let cfg =
        {
            SchemaVersion = 2
            ExtensionVersion = "0.8.0"
            UpdatedAt = None
            Connections = []
        }

    Workspace.writeConfiguration dir2 cfg
    Assert.True(File.Exists(Path.Combine(dir2, ".fsqs", "workspace.json")))

[<Fact>]
let ``readConfiguration on empty workspaceDir reads relative workspace.json array and primitive directly`` () =
    let originalCurrentDir = Directory.GetCurrentDirectory()
    let tempDir = tempWorkspaceDir ()
    Directory.SetCurrentDirectory tempDir

    try
        // Array directly in current directory
        File.WriteAllText("workspace.json", "[]")
        let cfgArray = Workspace.readConfiguration ""
        Assert.Equal(1, cfgArray.SchemaVersion)
        Assert.Equal("0.1.0", cfgArray.ExtensionVersion)
        Assert.Empty(cfgArray.Connections)

        // Primitive directly in current directory
        File.WriteAllText("workspace.json", "123")
        let cfgPrimitive = Workspace.readConfiguration ""
        Assert.Equal(2, cfgPrimitive.SchemaVersion)
        Assert.Empty(cfgPrimitive.Connections)
    finally
        Directory.SetCurrentDirectory originalCurrentDir

[<Fact>]
let ``workspaceFilePath fallback branches hit line 55 and line 57`` () =
    // Fallback line 55: targetDir is FSharpQueryStudio, but workspace.json is inside .fsqs
    let baseDir = tempWorkspaceDir ()
    let globalDir = Path.Combine(baseDir, "FSharpQueryStudio")
    let fsqsSubDir = Path.Combine(globalDir, ".fsqs")
    Directory.CreateDirectory fsqsSubDir |> ignore
    let fsqsWs = Path.Combine(fsqsSubDir, "workspace.json")
    ProjectUpgradeConstants.writeV2WorkspaceJson fsqsWs []

    let resolvedPath55 = Workspace.workspaceFilePath globalDir
    Assert.Equal(fsqsWs, resolvedPath55)

    // Fallback line 57: relative directory with empty string where workspace.json exists in root
    let originalCurrentDir = Directory.GetCurrentDirectory()
    let tempDir = tempWorkspaceDir ()
    Directory.SetCurrentDirectory tempDir

    try
        File.WriteAllText("workspace.json", "{}")
        let resolvedPath57 = Workspace.workspaceFilePath ""
        Assert.Equal("workspace.json", resolvedPath57)
    finally
        Directory.SetCurrentDirectory originalCurrentDir
