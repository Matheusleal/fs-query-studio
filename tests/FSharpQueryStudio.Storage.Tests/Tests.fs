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
            Assert.Equal(e.Configuration.GetRawText(), a.Configuration.GetRawText())
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
let ``the written workspace.json exposes only non-secret connection metadata`` () =
    let dir = tempWorkspaceDir ()
    let connection = sampleConnection "Sales"

    Workspace.writeConnections dir [ connection ]

    let json = File.ReadAllText(Path.Combine(dir, "workspace.json"))
    let doc = JsonDocument.Parse(json)
    let element = doc.RootElement.EnumerateArray() |> Seq.exactlyOne

    let propertyNames =
        element.EnumerateObject() |> Seq.map (fun p -> p.Name) |> Set.ofSeq

    Assert.Equal<Set<string>>(
        Set.ofList [ "id"; "name"; "kind"; "configuration"; "secretReference"; "isTrusted" ],
        propertyNames
    )

    Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase)

[<Fact>]
let ``registerConnection appends a new connection and persists it`` () =
    let dir = tempWorkspaceDir ()
    let connection = sampleConnection "Sales"

    let result = Workspace.registerConnection dir connection

    assertSameConnections [ connection ] result
    assertSameConnections [ connection ] (Workspace.readConnections dir)

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

    let json = File.ReadAllText(Path.Combine(dir, "workspace.json"))
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
