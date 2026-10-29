module Tests

open System
open System.IO
open System.Text.Json
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Storage
open FSharpQueryStudio.Security

let private tempWorkspaceDir () =
    let dir = Path.Combine(Path.GetTempPath(), "fqs-workspace-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    dir

let private sampleConnection (name: string) : ConnectionDefinition =
    { Id = Guid.NewGuid()
      Name = name
      Kind = ConnectorKind.Sqlite
      Configuration = JsonDocument.Parse("""{"filePath":"C:/data/sales.db"}""").RootElement
      SecretReference = Some "vault://sales-db-cred"
      IsTrusted = true }

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

    let element = doc.RootElement.GetProperty("connections").EnumerateArray() |> Seq.exactlyOne

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

    let renamed = { original with Name = "Sales (renamed)" }
    let result = Workspace.registerConnection dir renamed

    assertSameConnections [ renamed ] result
    assertSameConnections [ renamed ] (Workspace.readConnections dir)

[<Fact>]
let ``a registered connection's credential lives in the secret store, never in workspace.json`` () =
    let dir = tempWorkspaceDir ()
    let secretStore = InMemorySecretStore() :> ISecretStore
    let reference = "vault://sales-db-cred"
    secretStore.Save reference "correct horse battery staple"

    let connection = { sampleConnection "Sales" with SecretReference = Some reference }
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
let ``readConfiguration on legacy Schema v1 plain JSON array automatically upgrades it to Schema v2 and preserves connections`` () =
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
