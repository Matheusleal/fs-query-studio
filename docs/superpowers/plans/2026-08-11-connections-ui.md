# Connections UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user configure and use a database/CSV/Excel/REST connection through the real running app — today the six connectors (`FSharpQueryStudio.Connectors`) are fully implemented and tested, but nothing calls them from the UI: the `Connections` menu item is a disabled stub, the `WorkspacePanel` is an empty placeholder `TextBlock`, and `Connections.configure`/`Workspace.registerConnection` are never invoked from `App` or `Worker`. The only way to reach a connector today is hand-writing an `.fsx` script that references `FSharpQueryStudio.Connectors` directly.

**Architecture:** `ConnectionDefinition`/`ConnectorKind` (`Protocol`) and `Workspace.readConnections`/`writeConnections`/`registerConnection` (`Storage`) already exist and are correct — this plan wires them up, not redesigns them. A new `WorkspaceConnectionResolver` in `Connectors` builds a live `IConnector` from a `ConnectionDefinition` plus the OS credential store (`WindowsCredentialStore`, already implemented, currently uncalled). The Worker gains a `connection/register` dispatch case (persists to `workspace.json`) and, before every `execution/evaluate`, rebuilds the `Connections` module's resolver fresh from the current `workspace.json` — so a connection registered mid-session is usable on the very next run without a worker restart. The UI gains a real "New Connection" dialog (hand-rolled `Window`, the same pattern `MainWindow`'s existing delete-confirmation dialog already uses — Avalonia ships no dialog controls of its own) and a `WorkspacePanel` bound to the registered connection list.

**Tech Stack:** F#, Avalonia 12.1.1, xUnit, existing `FSharpQueryStudio.{Protocol,Storage,Security,Connectors,Worker,App,Ui}` projects.

## Global Constraints

- TDD throughout: a failing test before any production code, per this repo's established convention (every prior milestone task in `docs/DEVLOG.md` follows this).
- `workspace.json`/`trust.json`/`packages.json` all share one directory, computed identically wherever needed: `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FSharpQueryStudio")`. Never invent a second convention.
- A credential value never reaches `workspace.json` — only a `SecretReference` string does. `ConnectionDefinition.Configuration` is written as-is to disk; if you add a config field for a connector, ask "would this ever hold a raw secret?" before ever letting it flow into `Configuration` unmodified.
- Follow the codebase's existing `member val X = ... with get, set` no-op-when-unconfigured hook pattern on `ShellViewModel` (see `ExecuteAsync`, `CheckAsync`, `ResetSession`) for any new composition-root hook — don't invent a different wiring style.
- `dotnet build` clean (0 warnings/errors) and `dotnet test` green (excluding the pre-existing `RequiresDocker` `Integration.Tests` failures, unrelated to this work) after every task.
- Update `docs/DEVLOG.md` with one entry per task per this repo's established convention (see any existing entry for the expected shape: what changed, why, what was verified, files touched) — required by project convention, not optional polish.

---

### Task 1: `WorkspaceConnectionResolver` — build a live connector from a registered definition

**Files:**
- Create: `src/FSharpQueryStudio.Connectors/WorkspaceConnectionResolver.fs`
- Modify: `src/FSharpQueryStudio.Connectors/FSharpQueryStudio.Connectors.fsproj` (add the new file to `<Compile>`, before `Connections.fs` since `Connections.fs` doesn't need it but nothing later depends on ordering here except keeping `IConnector`/each connector type compiled first — insert right after `RestConnector.fs`; add a `ProjectReference` to `FSharpQueryStudio.Security`)
- Test: `tests/FSharpQueryStudio.Connectors.Tests/WorkspaceConnectionResolverTests.fs` (new; add to `FSharpQueryStudio.Connectors.Tests.fsproj`'s `<Compile>` list and add a `ProjectReference` to `FSharpQueryStudio.Security` there too)

**Interfaces:**
- Consumes: `FSharpQueryStudio.Protocol.ConnectionDefinition`/`ConnectorKind` (existing), `FSharpQueryStudio.Connectors.{IConnector, IConnectionResolver, SqliteConnector, SqlServerConnector, PostgreSqlConnector, CsvConnector, ExcelConnector, RestConnector, RestRequestOptions}` (existing), `FSharpQueryStudio.Security.ISecretStore`/`InMemorySecretStore` (existing).
- Produces: `FSharpQueryStudio.Connectors.ConnectionStringSecrets.resolve: ISecretStore -> string option -> string -> string` and `FSharpQueryStudio.Connectors.WorkspaceConnectionResolver(connections: ConnectionDefinition list, secretStore: ISecretStore)`, implementing `IConnectionResolver`. Task 3 (Worker) constructs one of these per `execution/evaluate` call.

- [ ] **Step 1: Write the failing tests**

```fsharp
module WorkspaceConnectionResolverTests

open System
open System.IO
open System.Text.Json
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors
open FSharpQueryStudio.Security

let private definition (name: string) (kind: ConnectorKind) (configuration: obj) (secretRef: string option) : ConnectionDefinition =
    { Id = Guid.NewGuid()
      Name = name
      Kind = kind
      Configuration = JsonSerializer.SerializeToElement(configuration, Serialization.options)
      SecretReference = secretRef
      IsTrusted = false }

[<Fact>]
let ``ConnectionStringSecrets.resolve substitutes the {password} token from the secret store`` () =
    let store = InMemorySecretStore()
    (store :> ISecretStore).Save "ref1" "s3cret"

    let result =
        ConnectionStringSecrets.resolve store (Some "ref1") "Server=.;Database=Orders;Password={password};"

    Assert.Equal("Server=.;Database=Orders;Password=s3cret;", result)

[<Fact>]
let ``ConnectionStringSecrets.resolve returns the template unchanged when SecretReference is None`` () =
    let result =
        ConnectionStringSecrets.resolve (InMemorySecretStore()) None "Server=.;Trusted_Connection=true;"

    Assert.Equal("Server=.;Trusted_Connection=true;", result)

[<Fact>]
let ``ConnectionStringSecrets.resolve returns the template unchanged when the reference has no stored secret`` () =
    let result =
        ConnectionStringSecrets.resolve (InMemorySecretStore()) (Some "never-saved") "Server=.;Password={password};"

    Assert.Equal("Server=.;Password={password};", result)

[<Fact>]
let ``resolves a registered SQLite connection to a real, correctly-kinded SqliteConnector`` () =
    let path = Path.Combine(Path.GetTempPath(), "fqs-resolver-" + Guid.NewGuid().ToString("N") + ".db")

    try
        let def = definition "Sales" Sqlite {| FilePath = path |} None
        let resolver = WorkspaceConnectionResolver([ def ], InMemorySecretStore())

        let connector = (resolver :> IConnectionResolver).Resolve("Sales")

        Assert.Equal(ConnectorKind.Sqlite, connector.Kind)
        Assert.IsType<SqliteConnector>(connector) |> ignore
    finally
        if File.Exists path then
            File.Delete path

[<Fact>]
let ``resolves a registered CSV connection to a real CsvConnector`` () =
    let def = definition "Feed" Csv {| FilePath = @"C:\data\feed.csv" |} None
    let resolver = WorkspaceConnectionResolver([ def ], InMemorySecretStore())

    let connector = (resolver :> IConnectionResolver).Resolve("Feed")

    Assert.Equal(ConnectorKind.Csv, connector.Kind)
    Assert.IsType<CsvConnector>(connector) |> ignore

[<Fact>]
let ``resolves a registered Excel connection to a real ExcelConnector`` () =
    let def = definition "Book" Excel {| FilePath = @"C:\data\book.xlsx" |} None
    let resolver = WorkspaceConnectionResolver([ def ], InMemorySecretStore())

    let connector = (resolver :> IConnectionResolver).Resolve("Book")

    Assert.Equal(ConnectorKind.Excel, connector.Kind)
    Assert.IsType<ExcelConnector>(connector) |> ignore

[<Fact>]
let ``resolves a registered REST connection to a real RestConnector using the stored request options`` () =
    let def =
        definition
            "Api"
            RestJson
            {| Url = "https://example.test/orders"
               Method = "GET"
               Headers = [||]
               QueryParams = [||]
               Body = None |}
            None

    let resolver = WorkspaceConnectionResolver([ def ], InMemorySecretStore())
    let connector = (resolver :> IConnectionResolver).Resolve("Api")

    Assert.Equal(ConnectorKind.RestJson, connector.Kind)
    Assert.IsType<RestConnector>(connector) |> ignore

[<Fact>]
let ``resolves a registered SQL Server connection with its {password} token substituted from the secret store`` () =
    let store = InMemorySecretStore()
    (store :> ISecretStore).Save "fqs-conn-sales" "s3cret"

    let def =
        definition
            "Sales"
            SqlServer
            {| ConnectionString = "Server=.;Database=Orders;User Id=sa;Password={password};" |}
            (Some "fqs-conn-sales")

    let resolver = WorkspaceConnectionResolver([ def ], store)
    let connector = (resolver :> IConnectionResolver).Resolve("Sales")

    Assert.Equal(ConnectorKind.SqlServer, connector.Kind)
    Assert.IsType<SqlServerConnector>(connector) |> ignore

[<Fact>]
let ``Resolve throws a clear error for a name that is not registered`` () =
    let resolver = WorkspaceConnectionResolver([], InMemorySecretStore())

    let ex =
        Assert.Throws<Exception>(fun () -> (resolver :> IConnectionResolver).Resolve("Missing") |> ignore)

    Assert.Contains("Missing", ex.Message)
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FSharpQueryStudio.Connectors.Tests --filter "FullyQualifiedName~WorkspaceConnectionResolverTests"`
Expected: FAIL to compile — `WorkspaceConnectionResolver`/`ConnectionStringSecrets` don't exist yet, and the test project has no reference to `FSharpQueryStudio.Security` yet (add that reference now, as part of this failing step, so the only remaining failure is the missing production types).

- [ ] **Step 3: Add the `FSharpQueryStudio.Security` reference and write the implementation**

In `tests/FSharpQueryStudio.Connectors.Tests/FSharpQueryStudio.Connectors.Tests.fsproj`, add next to the existing `FSharpQueryStudio.Connectors` reference:
```xml
<ProjectReference Include="..\..\src\FSharpQueryStudio.Security\FSharpQueryStudio.Security.fsproj" />
```

In `src/FSharpQueryStudio.Connectors/FSharpQueryStudio.Connectors.fsproj`, add the new compile item after `RestConnector.fs` and a `ProjectReference`:
```xml
<Compile Include="WorkspaceConnectionResolver.fs" />
```
```xml
<ProjectReference Include="..\FSharpQueryStudio.Security\FSharpQueryStudio.Security.fsproj" />
```

`src/FSharpQueryStudio.Connectors/WorkspaceConnectionResolver.fs`:
```fsharp
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
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FSharpQueryStudio.Connectors.Tests --filter "FullyQualifiedName~WorkspaceConnectionResolverTests"`
Expected: PASS, all 9 tests.

- [ ] **Step 5: Run the full Connectors test project and commit**

Run: `dotnet build && dotnet test tests/FSharpQueryStudio.Connectors.Tests`
Expected: clean build, all tests green (no regressions in the existing connector tests).

```bash
git add src/FSharpQueryStudio.Connectors/WorkspaceConnectionResolver.fs src/FSharpQueryStudio.Connectors/FSharpQueryStudio.Connectors.fsproj tests/FSharpQueryStudio.Connectors.Tests/WorkspaceConnectionResolverTests.fs tests/FSharpQueryStudio.Connectors.Tests/FSharpQueryStudio.Connectors.Tests.fsproj
git commit -m "Feature: build live connectors from registered workspace connections"
```

---

### Task 2: Worker gains `connection/register`, persisting to `workspace.json`

**Files:**
- Modify: `src/FSharpQueryStudio.Worker/Handlers.fs`
- Modify: `src/FSharpQueryStudio.Worker/FSharpQueryStudio.Worker.fsproj` (add a `ProjectReference` to `FSharpQueryStudio.Storage`)
- Test: `tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs`

**Interfaces:**
- Consumes: `FSharpQueryStudio.Storage.Workspace.registerConnection: string -> ConnectionDefinition -> ConnectionDefinition list` (existing, previously uncalled outside its own unit tests).
- Produces: `Handlers.workspaceDir: unit -> string`, `Handlers.setWorkspaceDirForTests: string option -> unit`, `Handlers.handleConnectionRegister: WorkerRequestFrame -> WorkerResponseFrame`, and a new `"connection/register"` case in `Handlers.dispatch`. Task 3 reuses `workspaceDir()`; Task 4 (App) sends `"connection/register"` requests through `WorkerClient`.

- [ ] **Step 1: Write the failing test**

Add to `tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs` (near the existing `session/reset` tests, same file, same `open`s already present):

```fsharp
open System
open System.IO
open FSharpQueryStudio.Storage

let private registerConnectionRequest (id: string) (connection: ConnectionDefinition) : WorkerRequestFrame =
    { Id = id
      Method = "connection/register"
      Params = JsonSerializer.SerializeToElement(connection, Serialization.options) }

let private testConnection (name: string) : ConnectionDefinition =
    { Id = Guid.NewGuid()
      Name = name
      Kind = Sqlite
      Configuration = JsonDocument.Parse("""{"FilePath":"C:\\data.db"}""").RootElement
      SecretReference = None
      IsTrusted = false }

[<Fact>]
let ``dispatch on connection/register persists the connection to workspace.json under the configured workspace directory`` () =
    task {
        let tempDir = Path.Combine(Path.GetTempPath(), "fqs-workspace-" + Guid.NewGuid().ToString("N"))
        Handlers.setWorkspaceDirForTests (Some tempDir)

        try
            let connection = testConnection "TestConn"
            let! response = Handlers.dispatch (registerConnectionRequest "reg1" connection)

            Assert.Equal("reg1", response.Id)
            Assert.True(response.Error.IsNone)
            Assert.True(response.Result.IsSome)

            let persisted = Workspace.readConnections tempDir
            Assert.Contains(persisted, fun c -> c.Name = "TestConn")
        finally
            Handlers.setWorkspaceDirForTests None

            if Directory.Exists tempDir then
                Directory.Delete(tempDir, true)
    }

[<Fact>]
let ``dispatch on connection/register replacing an existing connection keeps only one binding for that Id`` () =
    task {
        let tempDir = Path.Combine(Path.GetTempPath(), "fqs-workspace-" + Guid.NewGuid().ToString("N"))
        Handlers.setWorkspaceDirForTests (Some tempDir)

        try
            let original = testConnection "Original"
            let renamed = { original with Name = "Renamed" }

            let! _ = Handlers.dispatch (registerConnectionRequest "reg1" original)
            let! _ = Handlers.dispatch (registerConnectionRequest "reg2" renamed)

            let persisted = Workspace.readConnections tempDir
            Assert.Single(persisted) |> ignore
            Assert.Equal("Renamed", persisted.[0].Name)
        finally
            Handlers.setWorkspaceDirForTests None

            if Directory.Exists tempDir then
                Directory.Delete(tempDir, true)
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests --filter "FullyQualifiedName~connection/register"`
Expected: FAIL to compile — `Handlers.setWorkspaceDirForTests` doesn't exist, `"connection/register"` isn't a recognized dispatch case (would return `method/unknown` even if it compiled), and `Worker.Tests` has no reference to `FSharpQueryStudio.Storage` yet.

- [ ] **Step 3: Add the `FSharpQueryStudio.Storage` reference and implement**

In `tests/FSharpQueryStudio.Worker.Tests/FSharpQueryStudio.Worker.Tests.fsproj`, add a `ProjectReference` to `FSharpQueryStudio.Storage`.

In `src/FSharpQueryStudio.Worker/FSharpQueryStudio.Worker.fsproj`, add:
```xml
<ProjectReference Include="..\FSharpQueryStudio.Storage\FSharpQueryStudio.Storage.fsproj" />
```

In `src/FSharpQueryStudio.Worker/Handlers.fs`, add near the top (after the existing `open`s) and near `currentWorkDir`'s declaration (same mutable-module-state pattern, so a test run never touches the real `%LOCALAPPDATA%`):

```fsharp
open FSharpQueryStudio.Storage
```

```fsharp
/// The persistent, single per-machine workspace directory — connections,
/// trust metadata, package approvals — `%LOCALAPPDATA%\FSharpQueryStudio`
/// in production. Distinct from `currentWorkDir` above, which is a fresh
/// throwaway temp directory per *evaluation*; this one is long-lived and
/// shared across every evaluation. `setWorkspaceDirForTests` exists only
/// so a test run never reads or writes the real user profile — never
/// called from `Program.fs`.
let mutable private workspaceDirOverride: string option = None

let workspaceDir () : string =
    defaultArg
        workspaceDirOverride
        (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FSharpQueryStudio"))

let setWorkspaceDirForTests (dir: string option) : unit = workspaceDirOverride <- dir

/// `connection/register` — Section 7. Persists `connection` to
/// `workspace.json` via `Workspace.registerConnection`, replacing any
/// existing binding sharing its `Id`, and returns the updated full list.
let handleConnectionRegister (request: WorkerRequestFrame) : WorkerResponseFrame =
    let connection =
        JsonSerializer.Deserialize<ConnectionDefinition>(request.Params, Serialization.options)

    let updated = Workspace.registerConnection (workspaceDir ()) connection

    { Id = request.Id
      Result = Some(JsonSerializer.SerializeToElement(updated, Serialization.options))
      Error = None }
```

In `dispatch`, add the case alongside the others:
```fsharp
            | "connection/register" -> return handleConnectionRegister request
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests --filter "FullyQualifiedName~connection/register"`
Expected: PASS, both tests.

- [ ] **Step 5: Run the full Worker test project and commit**

Run: `dotnet build && dotnet test tests/FSharpQueryStudio.Worker.Tests`
Expected: clean build, all tests green.

```bash
git add src/FSharpQueryStudio.Worker/Handlers.fs src/FSharpQueryStudio.Worker/FSharpQueryStudio.Worker.fsproj tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs tests/FSharpQueryStudio.Worker.Tests/FSharpQueryStudio.Worker.Tests.fsproj
git commit -m "Feature: worker handles connection/register, persisting to workspace.json"
```

---

### Task 3: Worker configures `Connections` from `workspace.json` before every evaluation

**Files:**
- Modify: `src/FSharpQueryStudio.Worker/Handlers.fs`
- Modify: `src/FSharpQueryStudio.Worker/FSharpQueryStudio.Worker.fsproj` (add `ProjectReference`s to `FSharpQueryStudio.Connectors` and `FSharpQueryStudio.Security`)
- Test: `tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs`

**Interfaces:**
- Consumes: `Handlers.workspaceDir()`/`setWorkspaceDirForTests` (Task 2), `WorkspaceConnectionResolver`/`ConnectionStringSecrets` (Task 1), `FSharpQueryStudio.Security.{ISecretStore, WindowsCredentialStore, InMemorySecretStore}` (existing).
- Produces: every real `execution/evaluate` run now has `Connections.sql`/`csv`/`excel`/`rest` resolvable against whatever's currently in `workspace.json` — nothing downstream (App/UI) needs to know this happens; it's purely a Worker-internal wiring step.

- [ ] **Step 1: Write the failing test**

This is exactly the gap `MilestoneCAcceptanceTests.fs` already demonstrates by hand (each test calls `Connections.configure(SingleConnectionResolver(...))` itself, because nothing else ever does). The new test proves the Worker does that wiring on its own, from a real `workspace.json`, with no test-side `Connections.configure` call:

```fsharp
[<Fact>]
let ``execution/evaluate resolves Connections.sql against a connection registered via connection/register`` () =
    task {
        let tempDir = Path.Combine(Path.GetTempPath(), "fqs-workspace-" + Guid.NewGuid().ToString("N"))
        let dbPath = Path.Combine(tempDir, "orders.db")
        Handlers.setWorkspaceDirForTests (Some tempDir)

        try
            Directory.CreateDirectory tempDir |> ignore

            use connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}")
            connection.Open()
            use createCmd = connection.CreateCommand()
            createCmd.CommandText <- "create table Orders (Id integer primary key, Total real)"
            createCmd.ExecuteNonQuery() |> ignore
            use insertCmd = connection.CreateCommand()
            insertCmd.CommandText <- "insert into Orders (Id, Total) values (1, 100.0)"
            insertCmd.ExecuteNonQuery() |> ignore
            connection.Close()
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()

            let registerRequest =
                registerConnectionRequest
                    "reg1"
                    { Id = Guid.NewGuid()
                      Name = "Sales"
                      Kind = Sqlite
                      Configuration =
                        JsonSerializer.SerializeToElement({| FilePath = dbPath |}, Serialization.options)
                      SecretReference = None
                      IsTrusted = false }

            let! _ = Handlers.dispatch registerRequest

            let evaluateRequest = evaluateRequest "eval1" """Connections.sql "Sales" "select Total from Orders" [] |> fun r -> r.Rows |> Seq.toArray"""
            let! evalResponse = Handlers.dispatch evaluateRequest

            Assert.True(evalResponse.Error.IsNone)
            let result = JsonSerializer.Deserialize<ResultEnvelope>(evalResponse.Result.Value, Serialization.options)
            Assert.Equal(ResultKind.Error, result.Kind) |> ignore // placeholder assertion replaced in Step 3 below
        finally
            Handlers.setWorkspaceDirForTests None
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()

            if Directory.Exists tempDir then
                Directory.Delete(tempDir, true)
    }
```

Note: the `evaluateRequest` helper already exists in this file (used by every other `execution/evaluate` test) — reuse it, don't redefine it. The final assertion is intentionally a placeholder proving the test *fails for the right reason* first (RED): before Step 3's implementation, `Connections.sql` isn't configured, so the script throws `"Connections has not been configured for this session."` and `result.Kind` is `Error` with that message — assert on that exact message in RED, then flip the assertion to the real success case once Step 3 makes it pass.

- [ ] **Step 2: Run the test to verify it fails, then fails for the *expected* reason**

Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests --filter "FullyQualifiedName~resolves Connections.sql"`
Expected: the script result is `ResultKind.Error` with `DisplayText` containing `"Connections has not been configured for this session."` — confirming the gap this task closes, not a compile error or an unrelated failure. Update the test's final assertion to:
```fsharp
Assert.Equal(ResultKind.Error, result.Kind)
Assert.Contains("Connections has not been configured", result.DisplayText |> Option.defaultValue "")
```
Re-run once to lock in this RED state before implementing.

- [ ] **Step 3: Implement, then flip the assertion to GREEN**

In `src/FSharpQueryStudio.Worker/FSharpQueryStudio.Worker.fsproj`, add:
```xml
<ProjectReference Include="..\FSharpQueryStudio.Connectors\FSharpQueryStudio.Connectors.fsproj" />
<ProjectReference Include="..\FSharpQueryStudio.Security\FSharpQueryStudio.Security.fsproj" />
```

In `src/FSharpQueryStudio.Worker/Handlers.fs`, add to the `open` list:
```fsharp
open FSharpQueryStudio.Connectors
open FSharpQueryStudio.Security
```

In `runEvaluate`, immediately before the existing `let outcome = Evaluation.evaluate session evalParams.Source` line, add:
```fsharp
            let connections = Workspace.readConnections (workspaceDir ())
            Connections.configure (WorkspaceConnectionResolver(connections, WindowsCredentialStore()))

```

Then change the test's final two assertions from the RED-state error check to:
```fsharp
Assert.Equal(ResultKind.Table, result.Kind)
Assert.Single(result.Rows) |> ignore
Assert.Equal(100.0, result.Rows.[0].[0] :?> float)
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests --filter "FullyQualifiedName~resolves Connections.sql"`
Expected: PASS.

- [ ] **Step 5: Run the full Worker test project and commit**

Run: `dotnet build && dotnet test tests/FSharpQueryStudio.Worker.Tests`
Expected: clean build, all tests green (this test project doesn't need Docker — SQLite is file-based).

```bash
git add src/FSharpQueryStudio.Worker/Handlers.fs src/FSharpQueryStudio.Worker/FSharpQueryStudio.Worker.fsproj tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs
git commit -m "Feature: worker resolves Connections.* against workspace.json on every evaluation"
```

---

### Task 4: `App.ConnectionService` — register a connection (and its secret) through the worker

**Files:**
- Create: `src/FSharpQueryStudio.App/ConnectionService.fs`
- Modify: `src/FSharpQueryStudio.App/FSharpQueryStudio.App.fsproj` (add the new file to `<Compile>`, right after `AnalysisService.fs` and before `App.axaml.fs`; add a `ProjectReference` to `FSharpQueryStudio.Security`)
- Test: none at this layer — `ConnectionService` is a thin composition-root wrapper over `WorkerClient.Send`/`ISecretStore.Save`, the same untested-at-this-layer shape as `ExecutionService`/`AnalysisService` already are (their own tests live one layer down, in `Worker.Tests`/`Connectors.Tests`, which Tasks 1–3 already cover). Verified instead by Task 5's `ShellViewModelTests`, which exercise this through the hook it's wired into.

**Interfaces:**
- Consumes: `WorkerClient.Send` (existing — see `ExecutionService.fs`/`AnalysisService.fs` for the exact call shape to copy), `FSharpQueryStudio.Security.{ISecretStore, WindowsCredentialStore}` (existing).
- Produces: `ConnectionService.register: WorkerClient -> ISecretStore -> ConnectionDefinition -> string option -> Task<ConnectionDefinition list>`. Task 5 wires this into `ShellViewModel.RegisterConnectionAsync`.

- [ ] **Step 1: Write the implementation directly (no test at this layer — see rationale above)**

`src/FSharpQueryStudio.App/ConnectionService.fs`:
```fsharp
namespace FSharpQueryStudio.App

open System
open System.Text.Json
open System.Threading.Tasks
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security

/// The "host service" for `connection/register` — same shape as
/// `ExecutionService`/`AnalysisService`. `register` is the one place a
/// plaintext secret (e.g. a typed-in database password) exists outside
/// the OS credential store: it's saved via `secretStore.Save` *before*
/// the definition (carrying only the resulting `SecretReference`, never
/// the secret itself) is sent to the worker to persist to
/// `workspace.json`.
module ConnectionService =

    let register
        (client: WorkerClient)
        (secretStore: ISecretStore)
        (definition: ConnectionDefinition)
        (secret: string option)
        : Task<ConnectionDefinition list> =
        task {
            match definition.SecretReference, secret with
            | Some reference, Some value -> secretStore.Save reference value
            | _ -> ()

            let request: WorkerRequestFrame =
                { Id = Guid.NewGuid().ToString()
                  Method = "connection/register"
                  Params = JsonSerializer.SerializeToElement<ConnectionDefinition>(definition, Serialization.options) }

            let! response = client.Send(request)

            match response.Result with
            | Some resultJson -> return JsonSerializer.Deserialize<ConnectionDefinition list>(resultJson, Serialization.options)
            | None -> return []
        }
```

In `src/FSharpQueryStudio.App/FSharpQueryStudio.App.fsproj`, add the compile item (order matters — after `AnalysisService.fs`, before `App.axaml.fs`) and the project reference:
```xml
<Compile Include="ConnectionService.fs" />
```
```xml
<ProjectReference Include="..\FSharpQueryStudio.Security\FSharpQueryStudio.Security.fsproj" />
```

- [ ] **Step 2: Build to confirm it compiles**

Run: `dotnet build`
Expected: clean build, 0 warnings/errors. (No test to run yet — Task 5 exercises this.)

- [ ] **Step 3: Commit**

```bash
git add src/FSharpQueryStudio.App/ConnectionService.fs src/FSharpQueryStudio.App/FSharpQueryStudio.App.fsproj
git commit -m "Feature: ConnectionService saves a secret then registers the connection via the worker"
```

---

### Task 5: `ShellViewModel` gains `Connections` state and a `RegisterConnectionAsync` hook

**Files:**
- Modify: `src/FSharpQueryStudio.Ui/ShellViewModel.fs`
- Test: `tests/FSharpQueryStudio.Ui.Tests/ShellViewModelTests.fs`

**Interfaces:**
- Consumes: nothing new from other tasks directly (the hook is `(ConnectionDefinition * string option -> Task<unit>) option`, matching every other composition-root hook's no-op-when-unconfigured shape) — Task 6 (App) sets it to a closure over `ConnectionService.register`.
- Produces: `ShellViewModel.Connections: ConnectionDefinition list` (read), `ShellViewModel.RegisterConnectionAsync: (ConnectionDefinition * string option) option with get, set`, `ShellViewModel.RegisterConnection: ConnectionDefinition -> string option -> Task<unit>` (the method the UI calls; updates `Connections` and raises `PropertyChanged` on success). Task 7 (UI) calls `RegisterConnection` from the new dialog and binds `WorkspacePanel` to `Connections`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/FSharpQueryStudio.Ui.Tests/ShellViewModelTests.fs`:

```fsharp
let private testConnection (name: string) : ConnectionDefinition =
    { Id = Guid.NewGuid()
      Name = name
      Kind = Sqlite
      Configuration = JsonDocument.Parse("""{"FilePath":"C:\\data.db"}""").RootElement
      SecretReference = None
      IsTrusted = false }

[<Fact>]
let ``a freshly-constructed ShellViewModel has no registered connections`` () =
    let vm = ShellViewModel()
    Assert.Empty(vm.Connections)

[<Fact>]
let ``RegisterConnection calls the configured hook and stores the returned list`` () =
    task {
        let vm = ShellViewModel()
        let connection = testConnection "Sales"
        let mutable received = None

        vm.RegisterConnectionAsync <-
            Some(fun (def, secret) ->
                received <- Some(def, secret)
                Task.FromResult([ def ]))

        do! vm.RegisterConnection connection (Some "s3cret")

        Assert.Equal(Some(connection, Some "s3cret"), received)
        Assert.Equal<ConnectionDefinition list>([ connection ], vm.Connections)
    }

[<Fact>]
let ``RegisterConnection is a no-op when RegisterConnectionAsync has not been configured`` () =
    task {
        let vm = ShellViewModel()
        let connection = testConnection "Sales"

        do! vm.RegisterConnection connection None // Must not throw.

        Assert.Empty(vm.Connections)
    }

[<Fact>]
let ``PropertyChanged fires for Connections after a successful RegisterConnection`` () =
    task {
        let vm = ShellViewModel()
        let connection = testConnection "Sales"
        vm.RegisterConnectionAsync <- Some(fun (def, _secret) -> Task.FromResult([ def ]))

        let raisedFor = ResizeArray<string>()
        (vm :> INotifyPropertyChanged).PropertyChanged.Add(fun args -> raisedFor.Add(args.PropertyName))

        do! vm.RegisterConnection connection None

        Assert.Contains("Connections", raisedFor)
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FSharpQueryStudio.Ui.Tests --filter "FullyQualifiedName~RegisterConnection|FullyQualifiedName~registered connections"`
Expected: FAIL to compile — `ShellViewModel` has no `Connections`/`RegisterConnectionAsync`/`RegisterConnection` members yet.

- [ ] **Step 3: Implement**

In `src/FSharpQueryStudio.Ui/ShellViewModel.fs`, add a mutable field near the top (alongside `documents`):
```fsharp
    let mutable connections: ConnectionDefinition list = []
```

Add a `raiseConnectionsChanged` helper near `raiseProblemsChanged`/`raiseResultChanged`:
```fsharp
    let raiseConnectionsChanged () =
        propertyChanged.Trigger(self, PropertyChangedEventArgs("Connections"))
```

Add the public members (near `ExecuteAsync`/`ResetSession`'s hook declarations):
```fsharp
    member _.Connections = connections

    /// The composition root's hook into `connection/register` — set
    /// alongside `ExecuteAsync`/`ResetSession`. Takes the definition and
    /// an optional plaintext secret (saved to the OS credential store
    /// before the definition itself is ever sent anywhere) — see
    /// `ConnectionService.register`. A no-op if never configured, same
    /// rationale as every other hook here.
    member val RegisterConnectionAsync: ((ConnectionDefinition * string option) -> Task<ConnectionDefinition list>) option =
        None with get, set

    /// Registers `definition` (with `secret`, if any) and updates
    /// `Connections` to the worker's returned, up-to-date list on
    /// success. A no-op, not an error, when `RegisterConnectionAsync`
    /// isn't configured.
    member this.RegisterConnection (definition: ConnectionDefinition) (secret: string option) : Task<unit> =
        task {
            match this.RegisterConnectionAsync with
            | None -> ()
            | Some register ->
                let! updated = register (definition, secret)
                connections <- updated
                raiseConnectionsChanged ()
        }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FSharpQueryStudio.Ui.Tests --filter "FullyQualifiedName~RegisterConnection|FullyQualifiedName~registered connections"`
Expected: PASS, all 4 tests.

- [ ] **Step 5: Run the full Ui.Tests project and commit**

Run: `dotnet build && dotnet test tests/FSharpQueryStudio.Ui.Tests`
Expected: clean build, all tests green, no regressions.

```bash
git add src/FSharpQueryStudio.Ui/ShellViewModel.fs tests/FSharpQueryStudio.Ui.Tests/ShellViewModelTests.fs
git commit -m "Feature: ShellViewModel tracks registered connections and can register a new one"
```

---

### Task 6: Wire `ConnectionService` into `ShellViewModel.RegisterConnectionAsync`, load connections at startup

**Files:**
- Modify: `src/FSharpQueryStudio.App/App.axaml.fs`
- Modify: `src/FSharpQueryStudio.App/FSharpQueryStudio.App.fsproj` (add a `ProjectReference` to `FSharpQueryStudio.Storage`, needed for the startup `Workspace.readConnections` read)

**Interfaces:**
- Consumes: `ConnectionService.register` (Task 4), `ShellViewModel.RegisterConnectionAsync`/`Connections` (Task 5), `Workspace.readConnections` (existing, previously uncalled from `App`).
- Produces: a running app whose `ShellViewModel.Connections` is populated from the real `workspace.json` at startup and kept current after every registration. Task 7 (UI) is the first real consumer.

- [ ] **Step 1: Implement (composition-root wiring — no new automated test; `Task 5`'s tests already cover the hook contract this closes over)**

In `src/FSharpQueryStudio.App/FSharpQueryStudio.App.fsproj`, add:
```xml
<ProjectReference Include="..\FSharpQueryStudio.Storage\FSharpQueryStudio.Storage.fsproj" />
```
(It may already be present via a transitive reference from `ConnectionService`'s new `Security` reference — check with `dotnet build` first; add explicitly only if the build reports `Workspace`/`Storage` as unresolved.)

In `src/FSharpQueryStudio.App/App.axaml.fs`, add to the `open` list:
```fsharp
open FSharpQueryStudio.Security
```

After the existing `let checkAsync = ...` / `mainWindow.ShellViewModel.CheckAsync <- Some checkAsync` block, add:
```fsharp
            let secretStore = WindowsCredentialStore() :> ISecretStore

            mainWindow.ShellViewModel.RegisterConnectionAsync <-
                Some(fun (definition, secret) -> ConnectionService.register client secretStore definition secret)

            // Populate `Connections` from whatever's already registered —
            // reads the same `workspace.json` the worker itself reads
            // fresh before every `execution/evaluate` (see
            // `Handlers.runEvaluate`), so what the UI shows at startup and
            // what a script can actually resolve always agree.
            let workspaceDir =
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FSharpQueryStudio"
                )

            for connection in FSharpQueryStudio.Storage.Workspace.readConnections workspaceDir do
                ()

            // `RegisterConnection`'s own no-op-hook guard would silently
            // drop a direct assignment attempt, so `Connections` is seeded
            // by reflection-free direct read instead — a small
            // `ShellViewModel.SeedConnections` setter is simpler than
            // routing a startup read back through the register hook. See
            // the next code block.
```

Replace that last placeholder loop with a real seed call — add one more tiny member to `ShellViewModel` alongside `Connections`/`RegisterConnection` in `src/FSharpQueryStudio.Ui/ShellViewModel.fs`:
```fsharp
    /// Seeds `Connections` at startup from whatever's already in
    /// `workspace.json` — distinct from `RegisterConnection`, which is
    /// for adding one new connection through the configured hook and
    /// raises `PropertyChanged` for a UI that's already bound and
    /// showing something. `SeedConnections` is a plain assignment: it
    /// runs once, before `MainWindow` is even shown, so there's nothing
    /// bound yet to notify.
    member _.SeedConnections(initial: ConnectionDefinition list) = connections <- initial
```

Then in `App.axaml.fs`, replace the placeholder loop with:
```fsharp
            mainWindow.ShellViewModel.SeedConnections(FSharpQueryStudio.Storage.Workspace.readConnections workspaceDir)
```

- [ ] **Step 2: Add one direct test for `SeedConnections`**

Add to `tests/FSharpQueryStudio.Ui.Tests/ShellViewModelTests.fs`:
```fsharp
[<Fact>]
let ``SeedConnections sets Connections directly without requiring RegisterConnectionAsync to be configured`` () =
    let vm = ShellViewModel()
    let connection = testConnection "Sales"

    vm.SeedConnections([ connection ])

    Assert.Equal<ConnectionDefinition list>([ connection ], vm.Connections)
```

Run: `dotnet test tests/FSharpQueryStudio.Ui.Tests --filter "FullyQualifiedName~SeedConnections"`
Expected: FAIL first (method doesn't exist), then PASS after adding the member from Step 1.

- [ ] **Step 3: Build and run the full solution test suite**

Run: `dotnet build && dotnet test`
Expected: clean build across all projects; all tests green except the pre-existing `RequiresDocker` `Integration.Tests` failures (unrelated, present before this plan started).

- [ ] **Step 4: Commit**

```bash
git add src/FSharpQueryStudio.App/App.axaml.fs src/FSharpQueryStudio.App/FSharpQueryStudio.App.fsproj src/FSharpQueryStudio.Ui/ShellViewModel.fs tests/FSharpQueryStudio.Ui.Tests/ShellViewModelTests.fs
git commit -m "Feature: wire connection registration and startup load into the composition root"
```

---

### Task 7: "New Connection" dialog and a real Connections menu

**Files:**
- Modify: `src/FSharpQueryStudio.Ui/MainWindow.axaml` (enable the `Connections` menu item from a disabled stub to a real submenu)
- Modify: `src/FSharpQueryStudio.Ui/MainWindow.axaml.fs` (the dialog itself, hand-rolled — mirrors the existing `confirmDiscardDirty` `Window`-based dialog already in this file)

**Interfaces:**
- Consumes: `ShellViewModel.RegisterConnection`/`Connections` (Tasks 5–6), `ConnectorKind` (existing).
- Produces: a user-reachable path from the running app's menu to a registered, usable connection — the thing this whole plan exists to deliver. No further task depends on this one.

- [ ] **Step 1: Enable the menu and add a "New Connection…" item**

In `src/FSharpQueryStudio.Ui/MainWindow.axaml`, find the existing disabled stub:
```xml
<MenuItem Header="_Connections" IsEnabled="False" />
```
Replace with:
```xml
<MenuItem Header="_Connections">
  <MenuItem Header="_New Connection…" Name="NewConnectionMenuItem" />
</MenuItem>
```

- [ ] **Step 2: Build the dialog and wire the menu item, in `MainWindow.axaml.fs`**

Add near `confirmDiscardDirty` (same section of the file, same hand-rolled-`Window` approach — Avalonia ships no dialog controls, and this project's own established pattern for exactly this situation already lives a few dozen lines above):

```fsharp
        /// Collects a new connection's name/kind/configuration and
        /// registers it — the only UI path to `ShellViewModel
        /// .RegisterConnection`. Kind-specific fields are the minimum
        /// each connector's `WorkspaceConnectionResolver.build` case
        /// actually reads (a file path for Sqlite/Csv/Excel, a
        /// connection string + optional password for SqlServer/
        /// PostgreSql, a URL for RestJson) — see
        /// `WorkspaceConnectionResolver.fs`'s `FileConnectionConfig`/
        /// `SqlConnectionStringConfig` for the exact shape each one
        /// serializes into `Configuration`.
        let showNewConnectionDialog () =
            let nameBox = TextBox(Watermark = "Connection name")

            let kindCombo =
                ComboBox(
                    ItemsSource =
                        [ ConnectorKind.Sqlite
                          ConnectorKind.Csv
                          ConnectorKind.Excel
                          ConnectorKind.SqlServer
                          ConnectorKind.PostgreSql
                          ConnectorKind.RestJson ],
                    SelectedIndex = 0
                )

            let filePathBox = TextBox(Watermark = @"C:\path\to\file")
            let connectionStringBox = TextBox(Watermark = "Server=...;Database=...;User Id=...;Password={password};")
            let passwordBox = TextBox(Watermark = "Password (optional)", PasswordChar = '*')
            let urlBox = TextBox(Watermark = "https://example.com/api")

            let fieldsPanel = StackPanel(Spacing = 8.0)

            let rebuildFields () =
                fieldsPanel.Children.Clear()

                match kindCombo.SelectedItem :?> ConnectorKind with
                | ConnectorKind.Sqlite
                | ConnectorKind.Csv
                | ConnectorKind.Excel -> fieldsPanel.Children.Add(filePathBox)
                | ConnectorKind.SqlServer
                | ConnectorKind.PostgreSql ->
                    fieldsPanel.Children.Add(connectionStringBox)
                    fieldsPanel.Children.Add(passwordBox)
                | ConnectorKind.RestJson -> fieldsPanel.Children.Add(urlBox)

            kindCombo.SelectionChanged.Add(fun _ -> rebuildFields ())
            rebuildFields ()

            let saveButton = Button(Content = "Register", Width = 96.0, IsDefault = true)
            let cancelButton = Button(Content = "Cancel", Width = 96.0, IsCancel = true)

            let buttons =
                StackPanel(
                    Orientation = Orientation.Horizontal,
                    Spacing = 8.0,
                    HorizontalAlignment = HorizontalAlignment.Right
                )

            buttons.Children.Add(cancelButton)
            buttons.Children.Add(saveButton)

            let panel = StackPanel(Margin = Thickness(16.0), Spacing = 12.0)
            panel.Children.Add(TextBlock(Text = "Name"))
            panel.Children.Add(nameBox)
            panel.Children.Add(TextBlock(Text = "Kind"))
            panel.Children.Add(kindCombo)
            panel.Children.Add(fieldsPanel)
            panel.Children.Add(buttons)

            let dialog =
                Window(
                    Title = "New Connection",
                    Width = 420.0,
                    SizeToContent = SizeToContent.Height,
                    CanResize = false,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = panel
                )

            saveButton.Click.Add(fun _ ->
                let kind = kindCombo.SelectedItem :?> ConnectorKind

                let configuration, secretReference =
                    match kind with
                    | ConnectorKind.Sqlite
                    | ConnectorKind.Csv
                    | ConnectorKind.Excel ->
                        System.Text.Json.JsonSerializer.SerializeToElement(
                            {| FilePath = filePathBox.Text |},
                            Serialization.options
                        ),
                        None
                    | ConnectorKind.SqlServer
                    | ConnectorKind.PostgreSql ->
                        let hasSecret = not (String.IsNullOrWhiteSpace passwordBox.Text)

                        System.Text.Json.JsonSerializer.SerializeToElement(
                            {| ConnectionString = connectionStringBox.Text |},
                            Serialization.options
                        ),
                        (if hasSecret then Some $"fqs-conn-{Guid.NewGuid():N}" else None)
                    | ConnectorKind.RestJson ->
                        System.Text.Json.JsonSerializer.SerializeToElement(
                            {| Url = urlBox.Text
                               Method = "GET"
                               Headers = [||]
                               QueryParams = [||]
                               Body: string option = None |},
                            Serialization.options
                        ),
                        None

                let definition: ConnectionDefinition =
                    { Id = Guid.NewGuid()
                      Name = nameBox.Text
                      Kind = kind
                      Configuration = configuration
                      SecretReference = secretReference
                      IsTrusted = false }

                let secret =
                    if String.IsNullOrWhiteSpace passwordBox.Text then None else Some passwordBox.Text

                shellViewModel.RegisterConnection definition secret |> ignore
                dialog.Close())

            cancelButton.Click.Add(fun _ -> dialog.Close())

            dialog.ShowDialog(this) |> ignore

        this.FindControl<MenuItem>("NewConnectionMenuItem").Click.Add(fun _ -> showNewConnectionDialog ())
```

Place this block right after the existing `confirmDiscardDirty` function definition and its `shellViewModel.ConfirmDiscardDirtyDocument <- Some confirmDiscardDirty`-style wiring (search for that line to find the right spot — keep the new block after it, before `saveActiveDocument`).

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: clean build, 0 warnings/errors. `MainWindow.axaml.fs` isn't unit-testable in this codebase (needs a real `Window`/WebView, per every prior XAML-code-behind task in `docs/DEVLOG.md`) — this task's correctness rests on Task 5's `ShellViewModel.RegisterConnection` tests plus this manual-verify boundary, the same standing limitation the project has documented for every XAML task since Milestone A.

- [ ] **Step 3: Run the full solution test suite**

Run: `dotnet test`
Expected: all green except the pre-existing `RequiresDocker` failures.

- [ ] **Step 4: Commit**

```bash
git add src/FSharpQueryStudio.Ui/MainWindow.axaml src/FSharpQueryStudio.Ui/MainWindow.axaml.fs
git commit -m "Feature: New Connection dialog, reachable from a real Connections menu"
```

---

### Task 8: `WorkspacePanel` shows the registered connection list

**Files:**
- Modify: `src/FSharpQueryStudio.Ui/MainWindow.axaml`

**Interfaces:**
- Consumes: `ShellViewModel.Connections` (Task 5).
- Produces: the last visible piece of this plan — a user can now see what they've registered, not just register it blind.

- [ ] **Step 1: Replace the placeholder `TextBlock` with a bound list**

In `src/FSharpQueryStudio.Ui/MainWindow.axaml`, find the `WorkspacePanel` `Border` (currently just `<TextBlock Text="Workspace" .../>` per its own comment "File tree not built yet"). Replace its content with:
```xml
<DockPanel>
  <TextBlock Text="Workspace" DockPanel.Dock="Top" Margin="8,8,8,4" FontWeight="Bold" />
  <ListBox ItemsSource="{Binding Connections}" Name="ConnectionsList">
    <ListBox.ItemTemplate>
      <DataTemplate>
        <StackPanel Orientation="Horizontal" Spacing="6">
          <TextBlock Text="{Binding Kind}" Opacity="0.7" />
          <TextBlock Text="{Binding Name}" />
        </StackPanel>
      </DataTemplate>
    </ListBox.ItemTemplate>
  </ListBox>
</DockPanel>
```
Keep the placeholder `TextBlock`'s original `Margin`/styling attributes if the surrounding `Border` had any — this only replaces the inner content, not the panel's own visibility toggle (`toggleToolVisibility`, already wired to this panel by name elsewhere in `MainWindow.axaml.fs`, is untouched).

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: clean build. Same manual-verify boundary as Task 7 — XAML content isn't unit-testable in this codebase; `ShellViewModel.Connections`'s own correctness is what Task 5's tests already prove.

- [ ] **Step 3: Run the full solution test suite one final time**

Run: `dotnet test`
Expected: all green except the pre-existing `RequiresDocker` failures — confirms nothing across the whole plan regressed anything else.

- [ ] **Step 4: Commit, then write the DEVLOG entry**

```bash
git add src/FSharpQueryStudio.Ui/MainWindow.axaml
git commit -m "Feature: WorkspacePanel lists registered connections"
```

Add one `docs/DEVLOG.md` entry (per this repo's established convention — see any existing entry for the expected shape) summarizing the whole plan: what was missing (Connections menu disabled, WorkspacePanel a placeholder, `Connections.configure`/`Workspace.registerConnection` never called from `App`/`Worker`), what now exists end to end (register → `workspace.json` → worker resolves fresh before every evaluate → a script's `Connections.sql`/`csv`/`excel`/`rest` call actually resolves), test counts before/after, and the same manual-verify caveat every prior XAML task in this log carries (dialog/menu/list rendering not visually confirmable in this environment).

```bash
git add docs/DEVLOG.md
git commit -m "Docs: DEVLOG entry for the Connections UI"
```

## Self-Review Notes

- **Spec coverage:** every DEVLOG-confirmed gap from the investigation is closed: disabled `Connections` menu (Task 7), placeholder `WorkspacePanel` (Task 8), `connection/register` never dispatched (Task 2), `Connections.configure`/`Workspace.registerConnection` never called from `App`/`Worker` (Tasks 3, 6), secret store never called (Task 4/6 via `WindowsCredentialStore`).
- **Explicitly out of scope** (call out if asked, don't silently expand into it): CSV/Excel connector options (delimiter, sheet name, header row) beyond their defaults; REST connector headers/query params/body in the dialog (only URL+GET); editing or deleting an already-registered connection (only add/replace-by-`Id` via re-registering the same `Id`, which the underlying `Workspace.registerConnection` already supports — just no UI entry point to reach it); testing a connection before saving it (`IConnector.TestConnectionAsync` exists but nothing calls it from this dialog). Each is a reasonable, independently-sized follow-up, not silently dropped scope.
