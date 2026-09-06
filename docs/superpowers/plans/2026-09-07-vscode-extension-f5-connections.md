# VS Code Extension — F5 Connections Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add connection management to the VS Code extension — an Activity Bar tree of registered connections with lazy schema navigation, an add/edit/remove QuickPick flow, and a reverse RPC channel so the worker reads a connection's secret from VS Code `SecretStorage` at evaluation time.

**Architecture:** Two halves. **Worker (F#):** a new `connection/list` RPC; a `HostChannel` that lets the worker *initiate* a request; a `BridgeSecretStore : ISecretStore` whose `TryRead` forwards `secret/get` over that channel (blocking, 5 s timeout, `None` fallback); `Program.fs`'s read loop learns to route a response frame (no `method`) back to the channel; the two hard-coded `WindowsCredentialStore()` sites in `Handlers.fs` take an injected store. **Extension (TS):** `connections/` (`definition.ts` pure builder, `schema-cache.ts`, `tree.ts` `TreeDataProvider`, `add.ts` orchestrators, `commands.ts` composition root) and `secrets/bridge.ts` answering `secret/get` from `context.secrets`; `WorkerClient` gains a host-request handler hook; `package.json` gets a view container; `extension.ts` wires both and returns a small test API.

**Tech Stack:** F# / .NET 10 (`FSharpQueryStudio.Worker`, xunit tests); TypeScript 5, Node 24, `esbuild` (unchanged — single node/cjs bundle), `vitest` (unit; `vscode` aliased to `test/vscode-stub.ts`), `@vscode/test-cli` + `@vscode/test-electron` (extension-host integration).

**Spec:** `docs/superpowers/specs/2026-09-07-vscode-extension-f5-connections-design.md` (which implements the **F5 Connections** rollout row of `docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md` and that spec's "Worker changes — additive, small" items 1–2 and the `connections/tree.ts` / `connections/add.ts` / `secrets/bridge.ts` component rows).

## Global Constraints

- **.NET floor:** worker requires .NET **10** on `PATH`. Reuse the existing `resolveDotnet` / `worker/session.ts`; never re-implement worker startup.
- **Node engine:** development on Node 24; `package.json` `engines.node` = `">=20"` (unchanged). **VS Code engine:** `"^1.90.0"` (unchanged).
- **Extension identity (placeholder until F6):** `name` = `fs-query-studio`, `publisher` = `fs-query-studio`, `displayName` = `F# Query Studio`. Extension id used by integration specs: `fs-query-studio.fs-query-studio`.
- **Worker frames are camelCase on the wire** (`{ id, method, params }` request / `{ id, result?, error? }` response, error `{ code, message }`). FSharp.SystemTextJson writes an absent `result`/`error` as explicit JSON `null` — treat `null` and `undefined` the same. `Serialization.options` (F#) = `CamelCase` + `PropertyNameCaseInsensitive` + `JsonFSharpConverter()` with **no** `JsonUnionEncoding` override.
- **F# discriminated unions on the wire** use `{ "Case": "<Name>" }` (AdjacentTag, fieldless tags not unwrapped). Applies here to `ConnectionDefinition.kind` (`{ "Case": "Sqlite" | "Csv" | "Excel" | "SqlServer" | "PostgreSql" | "RestJson" }`). Decode with `readUnionTag`, encode with `makeUnionTag` (both already in `editors/vscode/src/protocol.ts`).
- **F# `option` on the wire** is the bare value or JSON `null`. Applies to `ConnectionDefinition.secretReference` (`string option`).
- **Reverse-channel id namespace:** worker-initiated request ids are `host-<32 hex>` (`"host-" + Guid.NewGuid().ToString("N")`). Host-initiated ids are `randomUUID()` (hyphenated) / xunit test literals — they never start with `host-`, so the two spaces cannot collide.
- **Secret RPC surface this phase:** `secret/get` only. `BridgeSecretStore.Save` / `Delete` raise `NotSupportedException`. The extension owns every `context.secrets` write.
- **`BridgeSecretStore.TryRead` never blocks forever:** `task.Wait(timeout)` with a default `TimeSpan.FromSeconds 5.0` (constructor-overridable for tests); timeout, an error frame, a `null` result, or any exception → `None`.
- **New wire method strings:** `connection/list`, `secret/get`. Existing, unchanged: `connection/register`, `connection/remove`, `connection/schema`.
- **New `SecretReference` format (matches the Avalonia dialog `MainWindow.axaml.fs:525`):** `fqs-conn-<uuid with dashes stripped>`.
- **TreeView id:** `fsQueryStudioConnections`. **View container id:** `fs-query-studio`. **Connection node `contextValue`:** `fsqsConnection` (table / column / error nodes leave it unset).
- **Command ids:** `fs-query-studio.connections.add`, `.edit`, `.remove`, `.refresh`. Titles prefixed `F# Query Studio: Connections: `.
- **DiagnosticCollection / diagnostics are F4's** — F5 adds none.
- **TDD:** every task writes the failing test first, runs it red, then implements. **Commit after every task.** Add a one-line `docs/DEVLOG.md` entry (`## 2026-09-07 — F5.N: <title>`) as the penultimate step of every task.
- **F# work lives under `src/FSharpQueryStudio.Worker/` + `tests/FSharpQueryStudio.Worker.Tests/`.** TS work lives under `editors/vscode/`. The Avalonia projects (`FSharpQueryStudio.App`, `.Ui`, `.Editor`) are **not touched** — retired at F6. `WindowsCredentialStore` stays in the tree (still referenced by `App.axaml.fs`); F5 only removes it from the worker's *active* path.
- **Run the whole relevant suite at the end of each task:** F# tasks → `dotnet test tests/FSharpQueryStudio.Worker.Tests` (and `.Protocol.Tests` if touched); TS tasks → `npm test` in `editors/vscode` (shared `test/vscode-stub.ts` — a regression there breaks earlier suites silently).

---

## File Structure

**Worker (F#) — `src/FSharpQueryStudio.Worker/`:**

| File | Responsibility |
|---|---|
| `HostChannel.fs` (new) | `HostChannel(send)` — mints a `host-` id, writes a `WorkerRequestFrame`, returns a `Task<WorkerResponseFrame>`; `TryComplete(frame)` routes an inbound response back to the awaiting caller. Depends on `Protocol` only. |
| `BridgeSecretStore.fs` (new) | `BridgeSecretStore(host, ?timeout) : ISecretStore` — `TryRead` → `secret/get` over the channel, blocking with timeout, `None` on any failure; `Save`/`Delete` throw `NotSupportedException`. |
| `Handlers.fs` (modify) | Add a module-level injected `ISecretStore` (`configureSecretStore`), default `WindowsCredentialStore()`; use it at the two `WorkspaceConnectionResolver` sites (`:136`, `:550`). Add `handleConnectionList` + a `"connection/list"` dispatch arm. |
| `NdjsonStream.fs` (modify) | Add `readLine : TextReader -> Task<string option>` (raw line, so the loop can decide request-vs-response before deserialising). |
| `Program.fs` (modify) | Rewrite `loop` to parse the raw line, route a `method`-less frame to `HostChannel.TryComplete`, otherwise dispatch as today. `main` builds one `HostChannel` (writing through `writeLock`), calls `Handlers.configureSecretStore (BridgeSecretStore(hostChannel))`, threads the channel into `loop`. |
| `FSharpQueryStudio.Worker.fsproj` (modify) | `<Compile>` `HostChannel.fs` (after `NdjsonStream.fs`), `BridgeSecretStore.fs` (after `Handlers.fs`), both before `Program.fs`. |

**Worker tests — `tests/FSharpQueryStudio.Worker.Tests/`:** new `HostChannelTests.fs`, `BridgeSecretStoreTests.fs`, `ReverseChannelLoopTests.fs`; append to `HandlersTests.fs`. Register each new file in `FSharpQueryStudio.Worker.Tests.fsproj`.

**Extension (TS) — `editors/vscode/src/`:**

| File | Responsibility |
|---|---|
| `protocol.ts` (modify) | `ConnectorKindName`, `ConnectionDefinition`, `SchemaColumn`, `SchemaTable`, `ConnectionListResult`, `ConnectionSchemaResult`, `ConnectionRemoveParams`, `ConnectionSchemaParams`, `HostRequestFrame`, `isHostRequestFrame`. |
| `worker/client.ts` (modify) | `setHostRequestHandler(fn)`; `onFrame` routes a frame with a non-empty `method` to it and writes the reply through `writeChain`. |
| `secrets/bridge.ts` (new) | `registerSecretBridge({ client, secrets })` — installs a handler answering `secret/get` from `secrets.get`; unknown method / bad params / read failure → an error reply. |
| `connections/definition.ts` (new) | `buildConnectionDefinition(input, existing?) : BuiltConnection` — pure; per-kind `configuration`, `SecretReference` minting, keep-existing-secret rule. |
| `connections/schema-cache.ts` (new) | `SchemaCache` — `get` / `set` / `clear` over `Map<string, SchemaTable[]>`. |
| `connections/tree.ts` (new) | `ConnectionsTreeProvider implements vscode.TreeDataProvider<ConnNode>` — `getChildren` fans `connection/list` → `connection/schema` (cached) → columns; `getTreeItem`; `refresh()`. |
| `connections/add.ts` (new) | `runAddConnection` / `runEditConnection` / `runRemoveConnection` — orchestrators taking injected `ConnectionUi` + `SecretWriter` + `getWorker` + `refresh`. |
| `connections/commands.ts` (new) | `registerConnectionFeatures({ getWorker, secrets, log })` — builds the cache, tree, real `ConnectionUi` from `vscode.window`, registers the view + four commands, returns `{ disposable, api }`. |
| `extension.ts` (modify) | Wrap `getWorker` so the first resolution calls `registerSecretBridge`; call `registerConnectionFeatures`; `activate` returns `FsQueryStudioApi`. |
| `package.json` (modify) | `contributes.viewsContainers` + `views` + `viewsWelcome` + 4 `commands` + `menus`; `activationEvents` += `onView:fsQueryStudioConnections`. |
| `media/activitybar.svg` (new) | Monochrome database glyph for the container icon. |

**Extension tests — `editors/vscode/`:** new `src/secrets/bridge.test.ts`, `src/connections/{definition,schema-cache,tree,add}.test.ts`; append to `src/worker/client.test.ts`, `src/protocol.test.ts`, `src/extension.test.ts`; new `test/integration/connections.test.ts` + `test/integration/fixtures/sample-connections.db`; extend `test/vscode-stub.ts`.

---

## Task 1: `connection/list` RPC (F#)

**Files:**
- Modify: `src/FSharpQueryStudio.Worker/Handlers.fs` (add `handleConnectionList` near `handleConnectionRegister` ~`:490`; add a dispatch arm ~`:591`)
- Test: `tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs` (append)

**Interfaces:**
- Consumes: `Workspace.readConnections : string -> ConnectionDefinition list`; `Handlers.setWorkspaceDirForTests : string option -> unit`; `Handlers.dispatch : WorkerRequestFrame -> Task<WorkerResponseFrame>`.
- Produces: wire method `"connection/list"` → `WorkerResponseFrame` whose `Result` is `{ "connections": ConnectionDefinition[] }`.

- [ ] **Step 1: Write the failing test** — append to `HandlersTests.fs`:

```fsharp
[<Fact>]
let ``dispatch on connection/list returns the connections persisted in workspace.json`` () =
    task {
        let tmp = Path.Combine(Path.GetTempPath(), "fqs-list-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory tmp |> ignore
        try
            Handlers.setWorkspaceDirForTests (Some tmp)
            let seeded: ConnectionDefinition =
                { Id = Guid.NewGuid()
                  Name = "Sales"
                  Kind = ConnectorKind.Sqlite
                  Configuration = JsonDocument.Parse("""{"filePath":"C:/data/sales.db"}""").RootElement
                  SecretReference = None
                  IsTrusted = true }
            Workspace.writeConnections tmp [ seeded ]

            let! response = Handlers.dispatch (emptyParamsRequest "l1" "connection/list")

            Assert.True response.Error.IsNone
            let payload =
                JsonSerializer.Deserialize<{| connections: ConnectionDefinition list |}>(
                    response.Result.Value, Serialization.options)
            Assert.Equal(1, payload.connections.Length)
            Assert.Equal("Sales", payload.connections.Head.Name)
        finally
            Handlers.setWorkspaceDirForTests None
            Directory.Delete(tmp, true)
    }

[<Fact>]
let ``dispatch on connection/list returns an empty list for a fresh workspace`` () =
    task {
        let tmp = Path.Combine(Path.GetTempPath(), "fqs-list-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory tmp |> ignore
        try
            Handlers.setWorkspaceDirForTests (Some tmp)
            let! response = Handlers.dispatch (emptyParamsRequest "l2" "connection/list")
            let payload =
                JsonSerializer.Deserialize<{| connections: ConnectionDefinition list |}>(
                    response.Result.Value, Serialization.options)
            Assert.Empty payload.connections
        finally
            Handlers.setWorkspaceDirForTests None
            Directory.Delete(tmp, true)
    }
```

- [ ] **Step 2: Run — verify it fails**

Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests --filter "connection/list"`
Expected: FAIL — `dispatch` returns a `method/unknown` error, so `response.Error.IsNone` is false / `response.Result.Value` throws.

- [ ] **Step 3: Implement** — in `Handlers.fs`, after `handleConnectionRegister`:

```fsharp
/// `connection/list` — the read-only companion to `connection/register` /
/// `connection/remove` (F5). Neither of those returns the registry without
/// mutating it, and the extension's connections TreeView needs the list on
/// notebook/view open. A thin projection of `Workspace.readConnections`.
let handleConnectionList (request: WorkerRequestFrame) : WorkerResponseFrame =
    let connections = Workspace.readConnections (workspaceDir ())

    { Id = request.Id
      Result = Some(JsonSerializer.SerializeToElement({| connections = connections |}, Serialization.options))
      Error = None }
```

In `dispatch`, next to the other `connection/*` arms:

```fsharp
            | "connection/list" -> return handleConnectionList request
```

- [ ] **Step 4: Run — verify pass**

Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests --filter "connection/list"`
Expected: PASS (both).

- [ ] **Step 5: Full suite**

Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests`
Expected: all green (no regression).

- [ ] **Step 6: DEVLOG** — append to `docs/DEVLOG.md`:

```markdown
## 2026-09-07 — F5.1: connection/list RPC

`Handlers.handleConnectionList` + a `"connection/list"` dispatch arm — a read-only `{ connections }` projection of `Workspace.readConnections (workspaceDir ())`, the list the connections TreeView needs on open (`connection/register` / `connection/remove` only ever return it as a side effect of mutating).
```

- [ ] **Step 7: Commit**

```bash
git add src/FSharpQueryStudio.Worker/Handlers.fs tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs docs/DEVLOG.md
git commit -m "feat(worker): connection/list RPC

Read-only { connections } projection of Workspace.readConnections. The
connections TreeView needs the registry on open; register/remove only
return it as a mutation side effect.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8"
```

---

## Task 2: `HostChannel` — worker-initiated request path (F#)

**Files:**
- Create: `src/FSharpQueryStudio.Worker/HostChannel.fs`
- Modify: `src/FSharpQueryStudio.Worker/FSharpQueryStudio.Worker.fsproj` (`<Compile Include="HostChannel.fs" />` immediately after `NdjsonStream.fs`)
- Create: `tests/FSharpQueryStudio.Worker.Tests/HostChannelTests.fs`
- Modify: `tests/FSharpQueryStudio.Worker.Tests/FSharpQueryStudio.Worker.Tests.fsproj` (`<Compile Include="HostChannelTests.fs" />` after `HandlersLoggingTests.fs`)

**Interfaces:**
- Consumes: `FSharpQueryStudio.Protocol.WorkerRequestFrame` / `WorkerResponseFrame` / `Serialization.options`.
- Produces:
  - `type HostChannel(send: WorkerRequestFrame -> System.Threading.Tasks.Task)`
  - `member Request : method_: string * paramsValue: obj -> Task<WorkerResponseFrame>` — mints `host-<32hex>`, calls `send`, returns a task that completes when `TryComplete` is called with the matching id (or faults if `send` faults).
  - `member TryComplete : WorkerResponseFrame -> bool` — `true` if the frame's `Id` matched a pending request.

- [ ] **Step 1: Write the failing test** — `HostChannelTests.fs`:

```fsharp
module HostChannelTests

open System.Collections.Generic
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Worker

[<Fact>]
let ``Request writes one frame with a host- id and its method`` () =
    let sent = List<WorkerRequestFrame>()
    let channel = HostChannel(fun frame -> sent.Add frame; Task.CompletedTask)

    channel.Request("secret/get", {| reference = "fqs-conn-abc" |}) |> ignore

    Assert.Single sent |> ignore
    Assert.StartsWith("host-", sent.[0].Id)
    Assert.Equal("secret/get", sent.[0].Method)
    Assert.Equal("fqs-conn-abc", sent.[0].Params.GetProperty("reference").GetString())

[<Fact>]
let ``TryComplete resolves the matching pending Request and returns true`` () =
    let sent = List<WorkerRequestFrame>()
    let channel = HostChannel(fun frame -> sent.Add frame; Task.CompletedTask)

    let pending = channel.Request("secret/get", {| reference = "r" |})
    let reply: WorkerResponseFrame =
        { Id = sent.[0].Id
          Result = Some(System.Text.Json.JsonSerializer.SerializeToElement({| secret = "hunter2" |}, Serialization.options))
          Error = None }

    Assert.True(channel.TryComplete reply)
    Assert.True(pending.Wait 1000)
    Assert.Equal("hunter2", pending.Result.Result.Value.GetProperty("secret").GetString())

[<Fact>]
let ``TryComplete returns false for an unknown id`` () =
    let channel = HostChannel(fun _ -> Task.CompletedTask)
    let reply: WorkerResponseFrame = { Id = "host-nope"; Result = None; Error = None }
    Assert.False(channel.TryComplete reply)
```

- [ ] **Step 2: Run — verify it fails**

Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests --filter HostChannelTests`
Expected: FAIL — `HostChannel` does not exist (build error).

- [ ] **Step 3: Implement** — `HostChannel.fs`:

```fsharp
namespace FSharpQueryStudio.Worker

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open System.Text.Json
open FSharpQueryStudio.Protocol

/// The worker-initiated request path (F5). The worker normally only
/// answers frames; `BridgeSecretStore` needs it to *ask* the host for a
/// secret. `Request` writes a `WorkerRequestFrame` with a `host-`-prefixed
/// id (distinct from every host-minted id) and hands back a task the read
/// loop completes via `TryComplete` when the matching response frame
/// arrives. `send` is supplied by `Program.fs` and writes through the same
/// stdout `writeLock` every response uses.
type HostChannel(send: WorkerRequestFrame -> Task) =
    let pending = ConcurrentDictionary<string, TaskCompletionSource<WorkerResponseFrame>>()

    member _.Request(method_: string, paramsValue: obj) : Task<WorkerResponseFrame> =
        let id = "host-" + Guid.NewGuid().ToString("N")

        let tcs =
            TaskCompletionSource<WorkerResponseFrame>(TaskCreationOptions.RunContinuationsAsynchronously)

        pending.[id] <- tcs

        let frame: WorkerRequestFrame =
            { Id = id
              Method = method_
              Params = JsonSerializer.SerializeToElement(paramsValue, Serialization.options) }

        (send frame)
            .ContinueWith(fun (t: Task) ->
                if t.IsFaulted then
                    match pending.TryRemove id with
                    | true, waiter -> waiter.TrySetException(t.Exception.InnerExceptions :> seq<exn>) |> ignore
                    | _ -> ())
        |> ignore

        tcs.Task

    member _.TryComplete(frame: WorkerResponseFrame) : bool =
        match pending.TryRemove frame.Id with
        | true, tcs -> tcs.TrySetResult frame
        | _ -> false
```

Add `<Compile Include="HostChannel.fs" />` after `NdjsonStream.fs` in the worker `.fsproj`, and `<Compile Include="HostChannelTests.fs" />` after `HandlersLoggingTests.fs` in the test `.fsproj`.

- [ ] **Step 4: Run — verify pass**

Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests --filter HostChannelTests`
Expected: PASS (3).

- [ ] **Step 5: Full suite**

Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests`
Expected: green.

- [ ] **Step 6: DEVLOG**

```markdown
## 2026-09-07 — F5.2: HostChannel (worker-initiated request path)

`src/FSharpQueryStudio.Worker/HostChannel.fs`: `HostChannel(send)` — `Request(method, params)` mints a `host-<32hex>` id (disjoint from every host-minted id), writes a `WorkerRequestFrame` via the injected `send`, and returns a `Task<WorkerResponseFrame>` the read loop completes through `TryComplete` when the matching response arrives. A faulted `send` faults the pending task. Nothing wires it yet.
```

- [ ] **Step 7: Commit**

```bash
git add src/FSharpQueryStudio.Worker/HostChannel.fs src/FSharpQueryStudio.Worker/FSharpQueryStudio.Worker.fsproj tests/FSharpQueryStudio.Worker.Tests/HostChannelTests.fs tests/FSharpQueryStudio.Worker.Tests/FSharpQueryStudio.Worker.Tests.fsproj docs/DEVLOG.md
git commit -m "feat(worker): HostChannel for worker-initiated requests

host-<32hex> request ids, ContinueWith fault propagation, TryComplete
routing. Unwired — Program.fs and BridgeSecretStore consume it next.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8"
```

---

## Task 3: `Handlers` secret-store injection seam (F#)

**Files:**
- Modify: `src/FSharpQueryStudio.Worker/Handlers.fs` (add the injected store near `workspaceDirOverride` ~`:64`; swap `:136` and `:550`)
- Test: `tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs` (append)

**Interfaces:**
- Consumes: `FSharpQueryStudio.Security.ISecretStore`, `WindowsCredentialStore`, `InMemorySecretStore`.
- Produces: `Handlers.configureSecretStore : ISecretStore -> unit` (module-level; default before any call is `WindowsCredentialStore()`).

**Context:** `Handlers.fs:136` (`runEvaluate`) and `:550` (`handleConnectionSchema`) each construct `WorkspaceConnectionResolver(<connections>, WindowsCredentialStore())`. This task replaces the literal with a module-level `mutable` set at startup — mirrors the existing `workspaceDirOverride` / `setWorkspaceDirForTests` pattern. No behaviour change yet (default store is still `WindowsCredentialStore`).

- [ ] **Step 1: Write the failing test** — append to `HandlersTests.fs`:

```fsharp
[<Fact>]
let ``configureSecretStore makes connection/schema resolve {password} from the injected store`` () =
    task {
        let tmp = Path.Combine(Path.GetTempPath(), "fqs-seam-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory tmp |> ignore
        try
            Handlers.setWorkspaceDirForTests (Some tmp)
            let store = FSharpQueryStudio.Security.InMemorySecretStore()
            (store :> FSharpQueryStudio.Security.ISecretStore).Save "fqs-conn-seam" "s3cr3t"
            Handlers.configureSecretStore store

            let definition: ConnectionDefinition =
                { Id = Guid.NewGuid()
                  Name = "pg"
                  Kind = ConnectorKind.PostgreSql
                  Configuration =
                    JsonDocument.Parse("""{"connectionString":"Host={password}-sentinel;Port=5432"}""").RootElement
                  SecretReference = Some "fqs-conn-seam"
                  IsTrusted = false }
            Workspace.writeConnections tmp [ definition ]

            let req: WorkerRequestFrame =
                { Id = "sc1"
                  Method = "connection/schema"
                  Params = JsonSerializer.SerializeToElement({ Id = definition.Id } : ConnectionSchemaParams, Serialization.options) }
            let! response = Handlers.dispatch req

            // No server at "s3cr3t-sentinel" → a schemaFailed error whose message
            // proves the injected secret (not the literal {password}) was substituted.
            Assert.Equal(Some "connection/schemaFailed", response.Error |> Option.map (fun e -> e.Code))
            Assert.Contains("s3cr3t-sentinel", response.Error.Value.Message)
        finally
            Handlers.setWorkspaceDirForTests None
            Handlers.configureSecretStore (FSharpQueryStudio.Security.WindowsCredentialStore())
            Directory.Delete(tmp, true)
    }
```

*(If Npgsql's host-resolution error text on the CI OS proves not to echo the host verbatim, weaken the final assertion to `Assert.DoesNotContain("{password}", response.Error.Value.Message)` — the point is that substitution used the injected store.)*

- [ ] **Step 2: Run — verify it fails**

Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests --filter "configureSecretStore"`
Expected: FAIL — `Handlers.configureSecretStore` does not exist (build error).

- [ ] **Step 3: Implement** — in `Handlers.fs`, after the `workspaceDir` / `setWorkspaceDirForTests` block (~`:71`):

```fsharp
    /// The secret store the worker's connection resolver reads `{password}`
    /// tokens from. Default `WindowsCredentialStore` keeps the standalone
    /// worker and every existing test unchanged; `Program.fs` swaps in a
    /// `BridgeSecretStore` at startup so the extension host's credential
    /// store (VS Code `SecretStorage`, cross-platform) is the source of
    /// truth instead. One process-wide store — the worker handles one
    /// evaluation at a time.
    let mutable private secretStore: ISecretStore = WindowsCredentialStore()

    let configureSecretStore (store: ISecretStore) : unit = secretStore <- store
```

Then change `:136`:

```fsharp
            Connections.configure (WorkspaceConnectionResolver(connections, secretStore))
```

and `:550`:

```fsharp
                    let resolver =
                        WorkspaceConnectionResolver([ definition ], secretStore) :> IConnectionResolver
```

- [ ] **Step 4: Run — verify pass**

Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests --filter "configureSecretStore"`
Expected: PASS.

- [ ] **Step 5: Full suite**

Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests`
Expected: green — existing evaluate/schema tests still pass on the default `WindowsCredentialStore`.

- [ ] **Step 6: DEVLOG**

```markdown
## 2026-09-07 — F5.3: Handlers secret-store injection seam

`Handlers.configureSecretStore : ISecretStore -> unit` + a module-level `mutable secretStore` (default `WindowsCredentialStore()`), consumed at the two `WorkspaceConnectionResolver` sites (`runEvaluate`, `handleConnectionSchema`) in place of the hard-coded `WindowsCredentialStore()`. No behaviour change yet — same default store; `Program.fs` swaps in the bridge next. Mirrors the existing `workspaceDirOverride` pattern.
```

- [ ] **Step 7: Commit**

```bash
git add src/FSharpQueryStudio.Worker/Handlers.fs tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs docs/DEVLOG.md
git commit -m "refactor(worker): inject ISecretStore into Handlers

Module-level configureSecretStore seam (default WindowsCredentialStore),
used at both WorkspaceConnectionResolver sites. No behaviour change;
Program.fs swaps in BridgeSecretStore next.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8"
```

---

## Task 4: `Program.fs` reverse-channel routing + `main` wiring (F#)

**Files:**
- Modify: `src/FSharpQueryStudio.Worker/NdjsonStream.fs` (add `readLine`)
- Modify: `src/FSharpQueryStudio.Worker/Program.fs` (rewrite `loop`; wire `HostChannel` in `main`)
- Create: `tests/FSharpQueryStudio.Worker.Tests/ReverseChannelLoopTests.fs`
- Modify: `tests/FSharpQueryStudio.Worker.Tests/FSharpQueryStudio.Worker.Tests.fsproj` (`<Compile Include="ReverseChannelLoopTests.fs" />`)

**Interfaces:**
- Consumes: `HostChannel` (Task 2); `NdjsonStream.writeFrame`.
- Produces:
  - `NdjsonStream.readLine : System.IO.TextReader -> Task<string option>`
  - `Program.loop : TextReader -> TextWriter -> HostChannel -> Task` (arity +1; the loop now needs the channel to route responses). Exposed for the test — keep it `module`-visible (it is `private` today; drop `private` on `loop`, or add an `internal`-visible `runLoop` wrapper and keep `private loop`). **Chosen:** rename the recursive worker to `loopWith` and expose `let loop (stdin) (stdout) (host) = loopWith stdin stdout host` without `private`.
  - Behaviour: a raw line whose parsed JSON has no `"method"` property (or `"method": null`) is deserialised as `WorkerResponseFrame` and passed to `host.TryComplete`; the loop continues without calling `Handlers.dispatch`. Every other line keeps today's exact dispatch (evaluate / `analysis/*`+`results/exportCsv` background / inline default).

- [ ] **Step 1: Write the failing test** — `ReverseChannelLoopTests.fs`:

```fsharp
module ReverseChannelLoopTests

open System.IO
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Worker

/// Feeds the loop two lines: a host-bound response frame (must be routed to
/// the channel, never dispatched) followed by EOF.
[<Fact>]
let ``loop routes a method-less frame to HostChannel.TryComplete and does not dispatch it`` () =
    task {
        let sent = System.Collections.Generic.List<WorkerRequestFrame>()
        let channel = HostChannel(fun f -> sent.Add f; Task.CompletedTask)
        let pending = channel.Request("secret/get", {| reference = "r" |})
        let hostId = sent.[0].Id

        let responseLine =
            System.Text.Json.JsonSerializer.Serialize<WorkerResponseFrame>(
                { Id = hostId
                  Result = Some(System.Text.Json.JsonSerializer.SerializeToElement({| secret = "ok" |}, Serialization.options))
                  Error = None },
                Serialization.options)

        use stdin = new StringReader(responseLine + "\n")
        use stdout = new StringWriter()

        do! Program.loop stdin stdout channel

        Assert.True(pending.Wait 1000)
        Assert.Equal("ok", pending.Result.Result.Value.GetProperty("secret").GetString())
        Assert.Equal("", stdout.ToString())   // nothing written back for a routed response
    }

[<Fact>]
let ``loop still answers a normal request frame`` () =
    task {
        let channel = HostChannel(fun _ -> Task.CompletedTask)
        let healthLine =
            System.Text.Json.JsonSerializer.Serialize<WorkerRequestFrame>(
                { Id = "h1"; Method = "worker/health"; Params = System.Text.Json.JsonDocument.Parse("{}").RootElement },
                Serialization.options)

        use stdin = new StringReader(healthLine + "\n")
        use stdout = new StringWriter()

        do! Program.loop stdin stdout channel

        let written = stdout.ToString().Trim()
        Assert.Contains("\"id\":\"h1\"", written)
        Assert.Contains("version", written)
    }
```

- [ ] **Step 2: Run — verify it fails**

Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests --filter ReverseChannelLoopTests`
Expected: FAIL — `Program.loop` is `private` / wrong arity (build error).

- [ ] **Step 3a: Implement `NdjsonStream.readLine`** — in `NdjsonStream.fs`:

```fsharp
    /// The raw next line, or `None` at EOF. The reverse channel (F5) needs
    /// to peek at whether an inbound line is a request (`method` present)
    /// or a response to a worker-initiated request *before* choosing which
    /// type to deserialise it as — `readFrame<'T>` commits to one shape.
    let readLine (reader: TextReader) : System.Threading.Tasks.Task<string option> =
        task {
            let! line = reader.ReadLineAsync()
            return (match line with | null -> None | l -> Some l)
        }
```

- [ ] **Step 3b: Rewrite `loop` in `Program.fs`** — replace the current `loop` (lines ~26–125) with:

```fsharp
let private routeResponse (host: HostChannel) (line: string) : bool =
    // true  -> the line was a response to a worker-initiated request and
    //          has been handed to the channel; the caller must not dispatch.
    // false -> the line has a real `method`; dispatch it as a request.
    use doc = JsonDocument.Parse(line)

    let hasMethod =
        match doc.RootElement.TryGetProperty("method") with
        | true, m when m.ValueKind <> JsonValueKind.Null -> true
        | _ -> false

    if hasMethod then
        false
    else
        let response =
            JsonSerializer.Deserialize<WorkerResponseFrame>(line, Serialization.options)

        host.TryComplete response |> ignore
        true

let rec private loopWith (stdin: TextReader) (stdout: TextWriter) (host: HostChannel) =
    task {
        let! line = NdjsonStream.readLine stdin

        match line with
        | None -> ()
        | Some line when routeResponse host line -> do! loopWith stdin stdout host
        | Some line ->
            let request =
                JsonSerializer.Deserialize<WorkerRequestFrame>(line, Serialization.options)

            match request with
            | request when request.Method = "execution/evaluate" ->
                // (body identical to today's `Some request when ... "execution/evaluate"` arm)
                match Handlers.beginEvaluate request with
                | Choice2Of2 busy ->
                    do! writeResponse stdout busy
                    do! loopWith stdin stdout host
                | Choice1Of2 workDir ->
                    Task.Run(fun () ->
                        (task {
                            use _ = Correlation.pushCorrelationId request.Id
                            Log.Debug("Handling request {Method}", request.Method)
                            let response = Handlers.runEvaluate request workDir
                            do! writeResponse stdout response
                        })
                        :> Task)
                    |> ignore

                    do! loopWith stdin stdout host
            | request when request.Method.StartsWith("analysis/") || request.Method = "results/exportCsv" ->
                // (body identical to today's analysis/* + results/exportCsv arm)
                Task.Run(fun () ->
                    (task {
                        use _ = Correlation.pushCorrelationId request.Id
                        Log.Debug("Handling request {Method}", request.Method)

                        try
                            let! response = Handlers.dispatch request
                            do! writeResponse stdout response
                        with ex ->
                            Log.Error(ex, "Unhandled exception dispatching {Method}", request.Method)

                            do!
                                writeResponse
                                    stdout
                                    { Id = request.Id
                                      Result = None
                                      Error = Some { Code = "worker/requestFailed"; Message = ex.Message } }
                    })
                    :> Task)
                |> ignore

                do! loopWith stdin stdout host
            | request ->
                use _ = Correlation.pushCorrelationId request.Id
                Log.Debug("Handling request {Method}", request.Method)
                let! response = Handlers.dispatch request
                do! writeResponse stdout response
                do! loopWith stdin stdout host
    }

let loop (stdin: TextReader) (stdout: TextWriter) (host: HostChannel) : Task =
    loopWith stdin stdout host :> Task
```

Add `open System.Text.Json` to `Program.fs` (for `JsonDocument` / `JsonSerializer` / `JsonValueKind`).

- [ ] **Step 3c: Wire `main`** — replace the `try` block body in `main`:

```fsharp
        Log.Information("F# Query Studio worker starting")

        let stdout = System.Console.Out

        let writeFrameLocked (frame: WorkerRequestFrame) : Task =
            task {
                do! writeLock.WaitAsync()
                try
                    do! NdjsonStream.writeFrame stdout frame
                finally
                    writeLock.Release() |> ignore
            }
            :> Task

        let hostChannel = HostChannel(writeFrameLocked)
        Handlers.configureSecretStore (BridgeSecretStore(hostChannel))

        (loop System.Console.In stdout hostChannel).GetAwaiter().GetResult()
        Log.Information("F# Query Studio worker stopping")
        0
```

*(`BridgeSecretStore` lands in Task 5; until then this line will not compile — that is expected and Task 5's Step 2 depends on it. If executing strictly one task at a time with a green build between each, defer the `Handlers.configureSecretStore (BridgeSecretStore …)` line to Task 5 and land only the `HostChannel` + `loop` wiring here, passing `hostChannel` through with the default `WindowsCredentialStore` still active.)* **Chosen:** land only the `HostChannel` + `loop` wiring in this task; add the `configureSecretStore` line in Task 5.

- [ ] **Step 4: Run — verify pass**

Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests --filter ReverseChannelLoopTests`
Expected: PASS (2).

- [ ] **Step 5: Full suite + build the exe**

Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests && dotnet build src/FSharpQueryStudio.Worker`
Expected: green; exe builds.

- [ ] **Step 6: DEVLOG**

```markdown
## 2026-09-07 — F5.4: Program.fs reverse-channel routing

`NdjsonStream.readLine` (raw line) + a rewritten `loop` that peeks each inbound line: no `"method"` property → deserialise as `WorkerResponseFrame` and hand to `HostChannel.TryComplete` (no dispatch, no reply); otherwise the exact request dispatch as before (evaluate / `analysis/*`+`results/exportCsv` backgrounded / inline default). `main` builds one `HostChannel` writing through the stdout `writeLock` and threads it into `loop`. `loop` is now module-visible (arity +1) for the loop tests. Secret store still `WindowsCredentialStore` — the bridge composes next.
```

- [ ] **Step 7: Commit**

```bash
git add src/FSharpQueryStudio.Worker/NdjsonStream.fs src/FSharpQueryStudio.Worker/Program.fs tests/FSharpQueryStudio.Worker.Tests/ReverseChannelLoopTests.fs tests/FSharpQueryStudio.Worker.Tests/FSharpQueryStudio.Worker.Tests.fsproj docs/DEVLOG.md
git commit -m "feat(worker): route response frames to HostChannel in the read loop

readLine + a method-peek: a frame with no method is a reply to a
worker-initiated request -> TryComplete, no dispatch. main threads one
HostChannel (writing through writeLock) into loop.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8"
```

---

## Task 5: `BridgeSecretStore` + compose it in `main` (F#)

**Files:**
- Create: `src/FSharpQueryStudio.Worker/BridgeSecretStore.fs`
- Modify: `src/FSharpQueryStudio.Worker/FSharpQueryStudio.Worker.fsproj` (`<Compile Include="BridgeSecretStore.fs" />` after `Handlers.fs`, before `Program.fs`)
- Modify: `src/FSharpQueryStudio.Worker/Program.fs` (add the `Handlers.configureSecretStore (BridgeSecretStore hostChannel)` line)
- Create: `tests/FSharpQueryStudio.Worker.Tests/BridgeSecretStoreTests.fs`
- Modify: `tests/FSharpQueryStudio.Worker.Tests/FSharpQueryStudio.Worker.Tests.fsproj`

**Interfaces:**
- Consumes: `HostChannel` (Task 2); `FSharpQueryStudio.Security.ISecretStore`.
- Produces: `type BridgeSecretStore(host: HostChannel, ?timeout: System.TimeSpan)` implementing `ISecretStore`:
  - `TryRead reference` → blocks on `host.Request("secret/get", {| reference = reference |})` up to `timeout` (default 5 s); returns `Some s` when the reply's `result.secret` is a JSON string, else `None` (timeout / error frame / `null` / non-string / exception).
  - `Save _ _` and `Delete _` → `raise (NotSupportedException …)`.

- [ ] **Step 1: Write the failing test** — `BridgeSecretStoreTests.fs`:

```fsharp
module BridgeSecretStoreTests

open System
open System.Threading.Tasks
open System.Text.Json
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security
open FSharpQueryStudio.Worker

let private replyWith (result: obj option) (error: WorkerErrorInfo option) (channel: HostChannel) (id: string) =
    channel.TryComplete
        { Id = id
          Result = result |> Option.map (fun r -> JsonSerializer.SerializeToElement(r, Serialization.options))
          Error = error }
    |> ignore

[<Fact>]
let ``TryRead returns the host's secret`` () =
    let sent = System.Collections.Generic.List<WorkerRequestFrame>()
    let channel = HostChannel(fun f -> sent.Add f; Task.CompletedTask)
    let store = BridgeSecretStore(channel) :> ISecretStore

    let read = Task.Run(fun () -> store.TryRead "fqs-conn-1")
    // let the Request frame be written, then answer it
    SpinWait.SpinUntil((fun () -> sent.Count = 1), 1000) |> ignore
    replyWith (Some(box {| secret = "hunter2" |})) None channel sent.[0].Id

    Assert.Equal(Some "hunter2", read.Result)

[<Fact>]
let ``TryRead returns None on an error frame`` () =
    let sent = System.Collections.Generic.List<WorkerRequestFrame>()
    let channel = HostChannel(fun f -> sent.Add f; Task.CompletedTask)
    let store = BridgeSecretStore(channel) :> ISecretStore

    let read = Task.Run(fun () -> store.TryRead "x")
    SpinWait.SpinUntil((fun () -> sent.Count = 1), 1000) |> ignore
    replyWith None (Some { Code = "host/unknownMethod"; Message = "no" }) channel sent.[0].Id

    Assert.Equal(None, read.Result)

[<Fact>]
let ``TryRead returns None on a null secret`` () =
    let sent = System.Collections.Generic.List<WorkerRequestFrame>()
    let channel = HostChannel(fun f -> sent.Add f; Task.CompletedTask)
    let store = BridgeSecretStore(channel) :> ISecretStore

    let read = Task.Run(fun () -> store.TryRead "x")
    SpinWait.SpinUntil((fun () -> sent.Count = 1), 1000) |> ignore
    replyWith (Some(box {| secret = (null: string) |})) None channel sent.[0].Id

    Assert.Equal(None, read.Result)

[<Fact>]
let ``TryRead returns None when the host never replies (timeout)`` () =
    let channel = HostChannel(fun _ -> Task.CompletedTask)
    let store = BridgeSecretStore(channel, TimeSpan.FromMilliseconds 150.0) :> ISecretStore
    Assert.Equal(None, store.TryRead "x")

[<Fact>]
let ``Save and Delete are not supported`` () =
    let store = BridgeSecretStore(HostChannel(fun _ -> Task.CompletedTask)) :> ISecretStore
    Assert.Throws<NotSupportedException>(fun () -> store.Save "r" "s") |> ignore
    Assert.Throws<NotSupportedException>(fun () -> store.Delete "r") |> ignore
```

- [ ] **Step 2: Run — verify it fails**

Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests --filter BridgeSecretStoreTests`
Expected: FAIL — `BridgeSecretStore` does not exist.

- [ ] **Step 3: Implement** — `BridgeSecretStore.fs`:

```fsharp
namespace FSharpQueryStudio.Worker

open System
open System.Text.Json
open FSharpQueryStudio.Security

/// `ISecretStore` backed by the extension host over `HostChannel` (F5).
/// The connection resolver reads `{password}` synchronously mid-evaluation
/// (`ConnectionStringSecrets.resolve`), so `TryRead` blocks on the
/// host-bound `secret/get` — safe because `runEvaluate` /
/// `handleConnectionSchema` already run on a background task, leaving the
/// read loop free to deliver the reply. Any failure (timeout, error frame,
/// null/non-string result, exception) degrades to `None`, which surfaces
/// downstream as an ordinary connection failure — the same contract
/// `ConnectionStringSecrets.resolve` already documents for a missing
/// secret. Read-only: the extension owns every credential write.
type BridgeSecretStore(host: HostChannel, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout (TimeSpan.FromSeconds 5.0)

    let notSupported () : 'a =
        raise (NotSupportedException "BridgeSecretStore is read-only; the extension host owns secret writes.")

    interface ISecretStore with
        member _.TryRead(reference: string) : string option =
            try
                let request = host.Request("secret/get", {| reference = reference |})

                if request.Wait timeout then
                    let frame = request.Result

                    match frame.Error, frame.Result with
                    | Some _, _
                    | None, None -> None
                    | None, Some element ->
                        match element.TryGetProperty "secret" with
                        | true, secret when secret.ValueKind = JsonValueKind.String -> Some(secret.GetString())
                        | _ -> None
                else
                    None
            with _ ->
                None

        member _.Save (_: string) (_: string) : unit = notSupported ()
        member _.Delete(_: string) : unit = notSupported ()
```

Add `<Compile Include="BridgeSecretStore.fs" />` after `Handlers.fs` in the worker `.fsproj`; `<Compile Include="BridgeSecretStoreTests.fs" />` in the test `.fsproj`.

- [ ] **Step 4: Wire `main`** — in `Program.fs`, right after `let hostChannel = HostChannel(writeFrameLocked)`:

```fsharp
        Handlers.configureSecretStore (BridgeSecretStore hostChannel)
```

- [ ] **Step 5: Run — verify pass + full suite + build**

Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests --filter BridgeSecretStoreTests`
Expected: PASS (5).
Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests && dotnet build src/FSharpQueryStudio.Worker`
Expected: green; exe builds.

- [ ] **Step 6: DEVLOG**

```markdown
## 2026-09-07 — F5.5: BridgeSecretStore + composed on the worker path

`src/FSharpQueryStudio.Worker/BridgeSecretStore.fs`: `ISecretStore` whose `TryRead` blocks on a host-bound `secret/get` over `HostChannel` (5 s default timeout, constructor-overridable) and degrades to `None` on timeout / error frame / null / non-string / exception — the same "missing secret → connection failure" contract `ConnectionStringSecrets.resolve` already documents. `Save` / `Delete` raise `NotSupportedException` (the extension owns credential writes). `Program.fs` `main` now calls `Handlers.configureSecretStore (BridgeSecretStore hostChannel)`, so a `{password}` connection in the extension host resolves from VS Code `SecretStorage`. Standalone `dotnet run` of the worker keeps `WindowsCredentialStore` only if `main` is bypassed — in practice the worker always runs through `main`, so the bridge is always active; a host that never answers `secret/get` just yields `None`.
```

- [ ] **Step 7: Commit**

```bash
git add src/FSharpQueryStudio.Worker/BridgeSecretStore.fs src/FSharpQueryStudio.Worker/FSharpQueryStudio.Worker.fsproj src/FSharpQueryStudio.Worker/Program.fs tests/FSharpQueryStudio.Worker.Tests/BridgeSecretStoreTests.fs tests/FSharpQueryStudio.Worker.Tests/FSharpQueryStudio.Worker.Tests.fsproj docs/DEVLOG.md
git commit -m "feat(worker): BridgeSecretStore over HostChannel, composed in main

TryRead -> host-bound secret/get, blocking with a 5s timeout, None on any
failure. Save/Delete unsupported. main swaps it in for
WindowsCredentialStore on the worker's active path.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8"
```

---

## Task 6: `protocol.ts` — connection + host-frame types (TS)

**Files:**
- Modify: `editors/vscode/src/protocol.ts` (append a new `// --- F5: connections + reverse channel ---` section)
- Test: `editors/vscode/src/protocol.test.ts` (append)

**Interfaces:**
- Consumes: existing `UnionTag`, `readUnionTag`, `makeUnionTag`, `WorkerErrorInfo`, `WorkerRequestFrame` from `protocol.ts`.
- Produces:
  - `type ConnectorKindName = "SqlServer" | "PostgreSql" | "Sqlite" | "Csv" | "Excel" | "RestJson"`
  - `interface ConnectionDefinition { id: string; name: string; kind: UnionTag<ConnectorKindName>; configuration: unknown; secretReference: string | null; isTrusted: boolean }`
  - `interface SchemaColumn { name: string; dataType: string }`
  - `interface SchemaTable { name: string; columns: SchemaColumn[] }`
  - `interface ConnectionListResult { connections: ConnectionDefinition[] }`
  - `interface ConnectionSchemaResult { tables: SchemaTable[] }`
  - `interface ConnectionRemoveParams { id: string }` / `interface ConnectionSchemaParams { id: string }`
  - `interface HostRequestFrame { id: string; method: string; params: unknown }`
  - `function isHostRequestFrame(frame: unknown): frame is HostRequestFrame`

- [ ] **Step 1: Write the failing test** — append to `protocol.test.ts`:

```ts
import {
  readUnionTag,
  makeUnionTag,
  isHostRequestFrame,
  type ConnectionDefinition,
} from "./protocol";

describe("F5 connection wire types", () => {
  it("decodes a { Case } connector kind and re-encodes it", () => {
    const def = {
      id: "1",
      name: "Sales",
      kind: { Case: "Sqlite" },
      configuration: { filePath: "/x.db" },
      secretReference: null,
      isTrusted: false,
    } as unknown as ConnectionDefinition;
    expect(readUnionTag(def.kind)).toBe("Sqlite");
    expect(makeUnionTag("PostgreSql")).toEqual({ Case: "PostgreSql" });
  });

  it("recognises an inbound host-bound request frame", () => {
    expect(isHostRequestFrame({ id: "host-abc", method: "secret/get", params: {} })).toBe(true);
    expect(isHostRequestFrame({ id: "x", result: { secret: "y" } })).toBe(false);
    expect(isHostRequestFrame({ id: "x", method: "" })).toBe(false);
    expect(isHostRequestFrame(null)).toBe(false);
  });
});
```

- [ ] **Step 2: Run — verify it fails**

Run: `cd editors/vscode && npx vitest run src/protocol.test.ts`
Expected: FAIL — `isHostRequestFrame` / `ConnectionDefinition` not exported.

- [ ] **Step 3: Implement** — append to `protocol.ts`:

```ts
// --- F5: connections + reverse channel ---

export type ConnectorKindName =
  | "SqlServer"
  | "PostgreSql"
  | "Sqlite"
  | "Csv"
  | "Excel"
  | "RestJson";

/** A registered, non-secret connection binding — mirrors F#
 *  `FSharpQueryStudio.Protocol.ConnectionDefinition`. `kind` is an F# DU on
 *  the wire (`{ "Case": "Sqlite" }`); `configuration` is an opaque nested
 *  object shaped per kind; `secretReference` is an F# `string option`. */
export interface ConnectionDefinition {
  id: string;
  name: string;
  kind: UnionTag<ConnectorKindName>;
  configuration: unknown;
  secretReference: string | null;
  isTrusted: boolean;
}

export interface SchemaColumn {
  name: string;
  dataType: string;
}

export interface SchemaTable {
  name: string;
  columns: SchemaColumn[];
}

export interface ConnectionListResult {
  connections: ConnectionDefinition[];
}

export interface ConnectionSchemaResult {
  tables: SchemaTable[];
}

export interface ConnectionRemoveParams {
  id: string;
}

export interface ConnectionSchemaParams {
  id: string;
}

/** An inbound frame the worker *sent* — a host-bound request (F5 reverse
 *  channel). Distinguished from a response frame by a non-empty `method`. */
export interface HostRequestFrame {
  id: string;
  method: string;
  params: unknown;
}

export function isHostRequestFrame(frame: unknown): frame is HostRequestFrame {
  return (
    frame !== null &&
    typeof frame === "object" &&
    typeof (frame as { id?: unknown }).id === "string" &&
    typeof (frame as { method?: unknown }).method === "string" &&
    (frame as { method: string }).method.length > 0
  );
}
```

- [ ] **Step 4: Run — verify pass**

Run: `cd editors/vscode && npx vitest run src/protocol.test.ts`
Expected: PASS.

- [ ] **Step 5: Full unit suite**

Run: `cd editors/vscode && npm test`
Expected: green.

- [ ] **Step 6: DEVLOG**

```markdown
## 2026-09-07 — F5.6: connection + host-frame wire types (protocol.ts)

`src/protocol.ts`: `ConnectorKindName`, `ConnectionDefinition` (`kind` as `UnionTag<ConnectorKindName>`, `secretReference: string | null`), `SchemaTable` / `SchemaColumn`, the `connection/list` + `connection/schema` result shapes, `ConnectionRemoveParams` / `ConnectionSchemaParams`, and `HostRequestFrame` + `isHostRequestFrame` (non-empty `method` distinguishes a worker-sent request from a response).
```

- [ ] **Step 7: Commit**

```bash
git add editors/vscode/src/protocol.ts editors/vscode/src/protocol.test.ts docs/DEVLOG.md
git commit -m "feat(vscode): F5 connection + host-frame wire types

ConnectionDefinition (kind as {Case} DU), SchemaTable/Column, list/schema
results, HostRequestFrame + isHostRequestFrame guard.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8"
```

---

## Task 7: `WorkerClient` reverse dispatch (TS)

**Files:**
- Modify: `editors/vscode/src/worker/client.ts`
- Test: `editors/vscode/src/worker/client.test.ts` (append)

**Interfaces:**
- Consumes: `isHostRequestFrame` (Task 6); existing `writeFrame`, `WorkerResponseFrame`, `WorkerErrorInfo`.
- Produces:
  - `type HostRequestHandler = (method: string, params: unknown) => Promise<{ result?: unknown; error?: WorkerErrorInfo }>`
  - `WorkerClient.setHostRequestHandler(fn: HostRequestHandler): void`
  - `WorkerClient.onFrame` behaviour: a frame passing `isHostRequestFrame` is answered — `{ id, result }` or `{ id, error }` written back through `writeChain`; no handler → `{ id, error: { code: "host/unhandled", … } }`; handler throws/rejects → `{ id, error: { code: "host/threw", message } }`. Response frames (no `method`) keep today's `pending`-map path.

- [ ] **Step 1: Write the failing test** — append to `client.test.ts` (reuse the file's existing `FakeChild` / harness; the sketch below assumes helpers `makeClient()` returning `{ client, child }` and `child.emit(obj)` pushing a stdout frame, `child.stdinLines` capturing writes — match the names already in the file):

```ts
describe("reverse channel (host-bound requests)", () => {
  it("answers a host request with the handler result", async () => {
    const { client, child } = makeClient();
    client.setHostRequestHandler(async (method, params) => {
      expect(method).toBe("secret/get");
      expect(params).toEqual({ reference: "fqs-conn-1" });
      return { result: { secret: "hunter2" } };
    });
    client.send({ id: "warm", method: "worker/health", params: {} }); // force ensureProcess

    child.emit({ id: "host-1", method: "secret/get", params: { reference: "fqs-conn-1" } });
    await flush();

    const written = child.stdinLines.map((l) => JSON.parse(l));
    expect(written).toContainEqual({ id: "host-1", result: { secret: "hunter2" } });
  });

  it("replies host/unhandled when no handler is set", async () => {
    const { client, child } = makeClient();
    client.send({ id: "warm", method: "worker/health", params: {} });
    child.emit({ id: "host-2", method: "secret/get", params: {} });
    await flush();
    const written = child.stdinLines.map((l) => JSON.parse(l));
    expect(written.find((f) => f.id === "host-2")?.error?.code).toBe("host/unhandled");
  });

  it("replies host/threw when the handler rejects", async () => {
    const { client, child } = makeClient();
    client.setHostRequestHandler(async () => {
      throw new Error("boom");
    });
    client.send({ id: "warm", method: "worker/health", params: {} });
    child.emit({ id: "host-3", method: "secret/get", params: {} });
    await flush();
    const written = child.stdinLines.map((l) => JSON.parse(l));
    const f = written.find((x) => x.id === "host-3");
    expect(f?.error).toEqual({ code: "host/threw", message: "boom" });
  });

  it("still correlates a normal response frame by id", async () => {
    const { client, child } = makeClient();
    const p = client.send({ id: "r1", method: "worker/health", params: {} });
    child.emit({ id: "r1", result: { version: "0.1.0" } });
    expect((await p).result).toEqual({ version: "0.1.0" });
  });
});
```

*(If `client.test.ts` has no such harness, add a minimal one alongside these tests using the existing `WorkerProcess` fake shape from the file — a writable that records `write(line)` into an array, a readable you push newline JSON into.)*

- [ ] **Step 2: Run — verify it fails**

Run: `cd editors/vscode && npx vitest run src/worker/client.test.ts`
Expected: FAIL — `setHostRequestHandler` is not a function.

- [ ] **Step 3: Implement** — in `client.ts`:

Add near the top-level types:

```ts
export type HostRequestHandler = (
  method: string,
  params: unknown,
) => Promise<{ result?: unknown; error?: WorkerErrorInfo }>;
```

Add the import: `import { isHostRequestFrame } from "../protocol";` (and `WorkerErrorInfo` to the existing `../protocol` import).

In the class body:

```ts
  private hostRequestHandler?: HostRequestHandler;

  setHostRequestHandler(handler: HostRequestHandler): void {
    this.hostRequestHandler = handler;
  }
```

Replace `onFrame`:

```ts
  private onFrame(frame: unknown): void {
    if (isHostRequestFrame(frame)) {
      void this.answerHostRequest(frame.id, frame.method, frame.params);
      return;
    }
    const response = frame as WorkerResponseFrame;
    if (!response || typeof response.id !== "string") {
      return;
    }
    const waiter = this.pending.get(response.id);
    if (waiter) {
      this.pending.delete(response.id);
      waiter.resolve(response);
    }
  }

  private async answerHostRequest(id: string, method: string, params: unknown): Promise<void> {
    const proc = this.current;
    if (!proc) {
      return;
    }
    let reply: WorkerResponseFrame;
    if (!this.hostRequestHandler) {
      reply = { id, error: { code: "host/unhandled", message: `No host handler for ${method}` } };
    } else {
      try {
        const outcome = await this.hostRequestHandler(method, params);
        reply = outcome.error ? { id, error: outcome.error } : { id, result: outcome.result };
      } catch (err) {
        reply = { id, error: { code: "host/threw", message: (err as Error).message } };
      }
    }
    this.writeChain = this.writeChain.then(() => writeFrame(proc.stdin, reply)).catch(() => {
      /* a failed write surfaces as an exit via readFrames onEnd */
    });
  }
```

- [ ] **Step 4: Run — verify pass**

Run: `cd editors/vscode && npx vitest run src/worker/client.test.ts`
Expected: PASS.

- [ ] **Step 5: Full unit suite**

Run: `cd editors/vscode && npm test`
Expected: green.

- [ ] **Step 6: DEVLOG**

```markdown
## 2026-09-07 — F5.7: WorkerClient reverse dispatch

`src/worker/client.ts`: `setHostRequestHandler(fn)` + an `onFrame` branch — a frame passing `isHostRequestFrame` (non-empty `method`) is answered via the handler and the reply written back through `writeChain` (`{ id, result }` / `{ id, error }`); no handler → `host/unhandled`; a throwing/rejecting handler → `host/threw`. Response frames keep the existing id-correlation path untouched.
```

- [ ] **Step 7: Commit**

```bash
git add editors/vscode/src/worker/client.ts editors/vscode/src/worker/client.test.ts docs/DEVLOG.md
git commit -m "feat(vscode): WorkerClient answers host-bound requests

setHostRequestHandler + onFrame routing; reply written through writeChain.
host/unhandled and host/threw fallbacks.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8"
```

---

## Task 8: `secrets/bridge.ts` (TS)

**Files:**
- Create: `editors/vscode/src/secrets/bridge.ts`
- Test: `editors/vscode/src/secrets/bridge.test.ts`

**Interfaces:**
- Consumes: `HostRequestHandler` shape from `worker/client.ts` (structurally — do not import `vscode`).
- Produces:
  - `interface SecretReader { get(key: string): Thenable<string | undefined> }`
  - `interface HostRequestClient { setHostRequestHandler(fn: (method: string, params: unknown) => Promise<{ result?: unknown; error?: { code: string; message: string } }>): void }`
  - `function registerSecretBridge(deps: { client: HostRequestClient; secrets: SecretReader }): void`
  - Handler behaviour: `method !== "secret/get"` → `{ error: { code: "host/unknownMethod", … } }`; `params.reference` not a string → `{ error: { code: "host/badParams", … } }`; ok → `{ result: { secret: (await secrets.get(reference)) ?? null } }`; `secrets.get` throws → `{ error: { code: "host/secretReadFailed", message } }`.

- [ ] **Step 1: Write the failing test** — `bridge.test.ts`:

```ts
import { describe, it, expect, vi } from "vitest";
import { registerSecretBridge } from "./bridge";

function harness(secretsImpl: { get: (k: string) => Promise<string | undefined> }) {
  let handler!: (m: string, p: unknown) => Promise<{ result?: unknown; error?: { code: string; message: string } }>;
  registerSecretBridge({
    client: { setHostRequestHandler: (fn) => (handler = fn) },
    secrets: secretsImpl,
  });
  return (m: string, p: unknown) => handler(m, p);
}

describe("registerSecretBridge", () => {
  it("answers secret/get from the store", async () => {
    const call = harness({ get: async (k) => (k === "fqs-conn-1" ? "hunter2" : undefined) });
    expect(await call("secret/get", { reference: "fqs-conn-1" })).toEqual({ result: { secret: "hunter2" } });
  });

  it("returns { secret: null } for a missing key", async () => {
    const call = harness({ get: async () => undefined });
    expect(await call("secret/get", { reference: "nope" })).toEqual({ result: { secret: null } });
  });

  it("rejects an unknown method", async () => {
    const call = harness({ get: async () => undefined });
    expect((await call("secret/set", {})).error?.code).toBe("host/unknownMethod");
  });

  it("rejects a non-string reference", async () => {
    const call = harness({ get: async () => undefined });
    expect((await call("secret/get", { reference: 5 })).error?.code).toBe("host/badParams");
  });

  it("maps a throwing store to an error reply", async () => {
    const call = harness({
      get: async () => {
        throw new Error("keychain locked");
      },
    });
    expect(await call("secret/get", { reference: "x" })).toEqual({
      error: { code: "host/secretReadFailed", message: "keychain locked" },
    });
  });
});
```

- [ ] **Step 2: Run — verify it fails**

Run: `cd editors/vscode && npx vitest run src/secrets/bridge.test.ts`
Expected: FAIL — module not found.

- [ ] **Step 3: Implement** — `bridge.ts`:

```ts
/** Answers the worker's host-bound `secret/get` requests from VS Code
 *  `SecretStorage` (F5 reverse channel). No `vscode` import — `secrets` and
 *  `client` are injected structurally so this is unit-testable. Only
 *  `secret/get` is supported: the extension performs every credential
 *  write itself around `connection/register` / the remove command. */

export interface SecretReader {
  get(key: string): Thenable<string | undefined>;
}

export interface HostRequestClient {
  setHostRequestHandler(
    fn: (
      method: string,
      params: unknown,
    ) => Promise<{ result?: unknown; error?: { code: string; message: string } }>,
  ): void;
}

export function registerSecretBridge(deps: { client: HostRequestClient; secrets: SecretReader }): void {
  deps.client.setHostRequestHandler(async (method, params) => {
    if (method !== "secret/get") {
      return { error: { code: "host/unknownMethod", message: `Unsupported host method: ${method}` } };
    }
    const reference = (params as { reference?: unknown } | null)?.reference;
    if (typeof reference !== "string") {
      return { error: { code: "host/badParams", message: "secret/get requires a string 'reference'" } };
    }
    try {
      const secret = await deps.secrets.get(reference);
      return { result: { secret: secret ?? null } };
    } catch (err) {
      return { error: { code: "host/secretReadFailed", message: (err as Error).message } };
    }
  });
}
```

- [ ] **Step 4: Run — verify pass**

Run: `cd editors/vscode && npx vitest run src/secrets/bridge.test.ts`
Expected: PASS (5).

- [ ] **Step 5: Full unit suite**

Run: `cd editors/vscode && npm test`
Expected: green.

- [ ] **Step 6: DEVLOG**

```markdown
## 2026-09-07 — F5.8: secrets/bridge.ts

`src/secrets/bridge.ts`: `registerSecretBridge({ client, secrets })` installs the `WorkerClient` host-request handler — `secret/get { reference }` → `{ secret: (context.secrets.get(reference)) ?? null }`; unknown method → `host/unknownMethod`, non-string reference → `host/badParams`, a throwing store → `host/secretReadFailed`. No `vscode` import (deps injected structurally).
```

- [ ] **Step 7: Commit**

```bash
git add editors/vscode/src/secrets/bridge.ts editors/vscode/src/secrets/bridge.test.ts docs/DEVLOG.md
git commit -m "feat(vscode): secrets/bridge answers secret/get from SecretStorage

secret/get only; unknown-method / bad-params / read-failure error replies.
Dependency-injected, no vscode import.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8"
```

---

## Task 9: `connections/definition.ts` — pure builder (TS)

**Files:**
- Create: `editors/vscode/src/connections/definition.ts`
- Test: `editors/vscode/src/connections/definition.test.ts`

**Interfaces:**
- Consumes: `ConnectorKindName`, `ConnectionDefinition`, `makeUnionTag` from `protocol.ts`.
- Produces:
  - `interface ConnectionFormInput { kind: ConnectorKindName; name: string; filePath?: string; connectionString?: string; password?: string; url?: string }`
  - `interface BuiltConnection { definition: ConnectionDefinition; secret?: string; secretReference?: string }`
  - `function buildConnectionDefinition(input: ConnectionFormInput, existing?: ConnectionDefinition): BuiltConnection`
  - Rules: `id` = `existing?.id ?? randomUUID()`; `isTrusted` = `existing?.isTrusted ?? false`. `hasNewSecret` = `!!input.password && input.password.trim().length > 0`. `secretReference` = `existing?.secretReference ?? (hasNewSecret ? "fqs-conn-" + randomUUID().replace(/-/g, "") : null)`. `secret` = `hasNewSecret ? input.password : undefined`. `configuration` per kind: `{ filePath }` for `Sqlite`/`Csv`/`Excel`; `{ connectionString }` for `SqlServer`/`PostgreSql`; `{ url, method: "GET", headers: [], queryParams: [], body: null }` for `RestJson`.

- [ ] **Step 1: Write the failing test** — `definition.test.ts`:

```ts
import { describe, it, expect } from "vitest";
import { buildConnectionDefinition } from "./definition";
import type { ConnectionDefinition } from "../protocol";

describe("buildConnectionDefinition", () => {
  it("builds a Sqlite file connection with no secret", () => {
    const { definition, secret, secretReference } = buildConnectionDefinition({
      kind: "Sqlite",
      name: "Sales",
      filePath: "/data/sales.db",
    });
    expect(definition.kind).toEqual({ Case: "Sqlite" });
    expect(definition.configuration).toEqual({ filePath: "/data/sales.db" });
    expect(definition.secretReference).toBeNull();
    expect(definition.isTrusted).toBe(false);
    expect(typeof definition.id).toBe("string");
    expect(secret).toBeUndefined();
    expect(secretReference).toBeUndefined();
  });

  it("mints a fqs-conn- reference when a SQL connection gets a password", () => {
    const { definition, secret, secretReference } = buildConnectionDefinition({
      kind: "PostgreSql",
      name: "pg",
      connectionString: "Host=h;Port=5432;Password={password}",
      password: "hunter2",
    });
    expect(definition.configuration).toEqual({ connectionString: "Host=h;Port=5432;Password={password}" });
    expect(definition.secretReference).toMatch(/^fqs-conn-[0-9a-f]{32}$/);
    expect(secretReference).toBe(definition.secretReference);
    expect(secret).toBe("hunter2");
  });

  it("builds a RestJson configuration matching the Avalonia shape", () => {
    const { definition } = buildConnectionDefinition({ kind: "RestJson", name: "api", url: "https://x/api" });
    expect(definition.configuration).toEqual({
      url: "https://x/api",
      method: "GET",
      headers: [],
      queryParams: [],
      body: null,
    });
  });

  it("edit keeps id, isTrusted and the existing secretReference when password is blank", () => {
    const existing: ConnectionDefinition = {
      id: "keep-me",
      name: "old",
      kind: { Case: "SqlServer" },
      configuration: { connectionString: "old" },
      secretReference: "fqs-conn-existing",
      isTrusted: true,
    };
    const { definition, secret } = buildConnectionDefinition(
      { kind: "SqlServer", name: "new", connectionString: "Server=.;Password={password}", password: "  " },
      existing,
    );
    expect(definition.id).toBe("keep-me");
    expect(definition.isTrusted).toBe(true);
    expect(definition.secretReference).toBe("fqs-conn-existing");
    expect(secret).toBeUndefined();
  });

  it("edit with a new password on a previously secretless connection mints a reference", () => {
    const existing: ConnectionDefinition = {
      id: "c1",
      name: "n",
      kind: { Case: "PostgreSql" },
      configuration: { connectionString: "x" },
      secretReference: null,
      isTrusted: false,
    };
    const { definition, secret } = buildConnectionDefinition(
      { kind: "PostgreSql", name: "n", connectionString: "Host=h;Password={password}", password: "new" },
      existing,
    );
    expect(definition.secretReference).toMatch(/^fqs-conn-[0-9a-f]{32}$/);
    expect(secret).toBe("new");
  });
});
```

- [ ] **Step 2: Run — verify it fails**

Run: `cd editors/vscode && npx vitest run src/connections/definition.test.ts`
Expected: FAIL — module not found.

- [ ] **Step 3: Implement** — `definition.ts`:

```ts
import { randomUUID } from "node:crypto";
import { makeUnionTag, type ConnectorKindName, type ConnectionDefinition } from "../protocol";

export interface ConnectionFormInput {
  kind: ConnectorKindName;
  name: string;
  filePath?: string;
  connectionString?: string;
  password?: string;
  url?: string;
}

export interface BuiltConnection {
  definition: ConnectionDefinition;
  /** Present only when a new password was entered — the caller stores it. */
  secret?: string;
  /** Present when the definition carries a SecretReference (new or kept). */
  secretReference?: string;
}

function configurationFor(input: ConnectionFormInput): unknown {
  switch (input.kind) {
    case "Sqlite":
    case "Csv":
    case "Excel":
      return { filePath: input.filePath ?? "" };
    case "SqlServer":
    case "PostgreSql":
      return { connectionString: input.connectionString ?? "" };
    case "RestJson":
      return { url: input.url ?? "", method: "GET", headers: [], queryParams: [], body: null };
  }
}

/** Pure. Assembles a `ConnectionDefinition` from form input, applying the
 *  Avalonia dialog's secret rules: a blank password on edit keeps the
 *  existing `SecretReference` (and the credential it points at) untouched;
 *  a typed password reuses an existing reference or mints `fqs-conn-<hex>`. */
export function buildConnectionDefinition(
  input: ConnectionFormInput,
  existing?: ConnectionDefinition,
): BuiltConnection {
  const hasNewSecret = !!input.password && input.password.trim().length > 0;

  const secretReference =
    existing?.secretReference ??
    (hasNewSecret ? `fqs-conn-${randomUUID().replace(/-/g, "")}` : null);

  const definition: ConnectionDefinition = {
    id: existing?.id ?? randomUUID(),
    name: input.name,
    kind: makeUnionTag(input.kind),
    configuration: configurationFor(input),
    secretReference,
    isTrusted: existing?.isTrusted ?? false,
  };

  return {
    definition,
    secret: hasNewSecret ? input.password : undefined,
    secretReference: secretReference ?? undefined,
  };
}
```

- [ ] **Step 4: Run — verify pass**

Run: `cd editors/vscode && npx vitest run src/connections/definition.test.ts`
Expected: PASS (5).

- [ ] **Step 5: Full unit suite**

Run: `cd editors/vscode && npm test`
Expected: green.

- [ ] **Step 6: DEVLOG**

```markdown
## 2026-09-07 — F5.9: connections/definition.ts (pure builder)

`src/connections/definition.ts`: `buildConnectionDefinition(input, existing?)` → `{ definition, secret?, secretReference? }`. Per-kind `configuration` (`{ filePath }` / `{ connectionString }` / `{ url, method:"GET", headers:[], queryParams:[], body:null }`), `kind` as `makeUnionTag`. Secret rules mirror `MainWindow.axaml.fs`: blank password on edit keeps the existing `SecretReference`; a typed password reuses it or mints `fqs-conn-<32 hex>`. `secret` is returned only when newly entered.
```

- [ ] **Step 7: Commit**

```bash
git add editors/vscode/src/connections/definition.ts editors/vscode/src/connections/definition.test.ts docs/DEVLOG.md
git commit -m "feat(vscode): connections/definition.ts pure builder

Per-kind configuration + Avalonia secret rules (blank-keeps-existing,
typed-mints fqs-conn-<hex>).

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8"
```

---

## Task 10: `connections/schema-cache.ts` (TS)

**Files:**
- Create: `editors/vscode/src/connections/schema-cache.ts`
- Test: `editors/vscode/src/connections/schema-cache.test.ts`

**Interfaces:**
- Consumes: `SchemaTable` from `protocol.ts`.
- Produces: `class SchemaCache { get(id: string): SchemaTable[] | undefined; set(id: string, tables: SchemaTable[]): void; clear(): void }`.

- [ ] **Step 1: Write the failing test** — `schema-cache.test.ts`:

```ts
import { describe, it, expect } from "vitest";
import { SchemaCache } from "./schema-cache";

describe("SchemaCache", () => {
  it("returns undefined for an unknown id", () => {
    expect(new SchemaCache().get("x")).toBeUndefined();
  });
  it("returns what was set", () => {
    const c = new SchemaCache();
    const tables = [{ name: "t", columns: [{ name: "c", dataType: "int" }] }];
    c.set("id1", tables);
    expect(c.get("id1")).toBe(tables);
  });
  it("clear drops every entry", () => {
    const c = new SchemaCache();
    c.set("id1", []);
    c.clear();
    expect(c.get("id1")).toBeUndefined();
  });
});
```

- [ ] **Step 2: Run — verify it fails**

Run: `cd editors/vscode && npx vitest run src/connections/schema-cache.test.ts`
Expected: FAIL — module not found.

- [ ] **Step 3: Implement** — `schema-cache.ts`:

```ts
import type { SchemaTable } from "../protocol";

/** Per-session cache of `connection/schema` results, keyed by connection
 *  id. Invalidated only by the explicit Refresh command (F5 spec). */
export class SchemaCache {
  private readonly entries = new Map<string, SchemaTable[]>();

  get(id: string): SchemaTable[] | undefined {
    return this.entries.get(id);
  }

  set(id: string, tables: SchemaTable[]): void {
    this.entries.set(id, tables);
  }

  clear(): void {
    this.entries.clear();
  }
}
```

- [ ] **Step 4: Run — verify pass**

Run: `cd editors/vscode && npx vitest run src/connections/schema-cache.test.ts`
Expected: PASS (3).

- [ ] **Step 5: Full unit suite**

Run: `cd editors/vscode && npm test`
Expected: green.

- [ ] **Step 6: DEVLOG**

```markdown
## 2026-09-07 — F5.10: connections/schema-cache.ts

`src/connections/schema-cache.ts`: `SchemaCache` — `get` / `set` / `clear` over a `Map<connectionId, SchemaTable[]>`. Session-lived, invalidated only by the Refresh command.
```

- [ ] **Step 7: Commit**

```bash
git add editors/vscode/src/connections/schema-cache.ts editors/vscode/src/connections/schema-cache.test.ts docs/DEVLOG.md
git commit -m "feat(vscode): connections/schema-cache.ts

Per-session Map<connectionId, SchemaTable[]>; cleared only by Refresh.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8"
```

---

## Task 11: `connections/tree.ts` — `TreeDataProvider` (TS)

**Files:**
- Create: `editors/vscode/src/connections/tree.ts`
- Modify: `editors/vscode/test/vscode-stub.ts` (add `TreeItem`, `TreeItemCollapsibleState`, `ThemeIcon`, `EventEmitter`; export from the default object)
- Test: `editors/vscode/src/connections/tree.test.ts`

**Interfaces:**
- Consumes: `SchemaCache` (Task 10); `newRequestId`, `ConnectionDefinition`, `ConnectionListResult`, `ConnectionSchemaResult`, `SchemaTable`, `SchemaColumn`, `readUnionTag`, `WorkerResponseFrame` from `protocol.ts`; `vscode` (`TreeDataProvider`, `TreeItem`, `TreeItemCollapsibleState`, `ThemeIcon`, `EventEmitter`, `Event`).
- Produces:
  - `type WorkerLike = { send(req: { id: string; method: string; params: unknown }): Promise<WorkerResponseFrame> }`
  - `type ConnNode = { t: "connection"; def: ConnectionDefinition } | { t: "table"; connId: string; table: SchemaTable } | { t: "column"; column: SchemaColumn } | { t: "error"; message: string }`
  - `class ConnectionsTreeProvider implements vscode.TreeDataProvider<ConnNode>` with ctor `(deps: { getWorker: () => Promise<WorkerLike>; schemaCache: SchemaCache; log: (l: string) => void })`, `onDidChangeTreeData`, `getTreeItem`, `getChildren`, `refresh(): void`.
  - `getChildren` contract: no arg → `connection/list` → `connection` nodes (error frame / thrown / `getWorker` reject → a single `error` node); `connection` node → `schemaCache.get(id)` else `connection/schema` (cache on success; error → single `error` node child); `table` node → its `column` nodes; `column` / `error` → `[]`.

- [ ] **Step 1: Write the failing test** — `tree.test.ts`:

```ts
import { describe, it, expect, vi } from "vitest";
import { ConnectionsTreeProvider, type WorkerLike } from "./tree";
import { SchemaCache } from "./schema-cache";

const conn = (id: string, name: string) => ({
  id,
  name,
  kind: { Case: "Sqlite" as const },
  configuration: {},
  secretReference: null,
  isTrusted: false,
});

function provider(send: WorkerLike["send"]) {
  const schemaCache = new SchemaCache();
  const p = new ConnectionsTreeProvider({
    getWorker: async () => ({ send }),
    schemaCache,
    log: () => {},
  });
  return { p, schemaCache };
}

describe("ConnectionsTreeProvider", () => {
  it("lists connections from connection/list", async () => {
    const { p } = provider(async (req) => {
      expect(req.method).toBe("connection/list");
      return { id: req.id, result: { connections: [conn("1", "Sales"), conn("2", "Ops")] } };
    });
    const roots = await p.getChildren();
    expect(roots.map((n) => (n.t === "connection" ? n.def.name : n.t))).toEqual(["Sales", "Ops"]);
  });

  it("shows one error node when connection/list fails", async () => {
    const { p } = provider(async (req) => ({ id: req.id, error: { code: "worker/crashed", message: "gone" } }));
    const roots = await p.getChildren();
    expect(roots).toEqual([{ t: "error", message: expect.stringContaining("gone") }]);
  });

  it("fetches schema once and serves the second expand from cache", async () => {
    const send = vi.fn(async (req: { id: string; method: string; params: unknown }) => {
      if (req.method === "connection/schema") {
        return { id: req.id, result: { tables: [{ name: "orders", columns: [{ name: "id", dataType: "int" }] }] } };
      }
      return { id: req.id, result: { connections: [] } };
    });
    const { p } = provider(send);
    const node = { t: "connection" as const, def: conn("1", "Sales") };
    const first = await p.getChildren(node);
    const second = await p.getChildren(node);
    expect(first.map((n) => (n.t === "table" ? n.table.name : n.t))).toEqual(["orders"]);
    expect(second).toEqual(first);
    expect(send.mock.calls.filter((c) => c[0].method === "connection/schema")).toHaveLength(1);
  });

  it("shows an error child when connection/schema fails", async () => {
    const { p } = provider(async (req) => ({ id: req.id, error: { code: "connection/schemaFailed", message: "bad creds" } }));
    const kids = await p.getChildren({ t: "connection", def: conn("1", "Sales") });
    expect(kids).toEqual([{ t: "error", message: expect.stringContaining("bad creds") }]);
  });

  it("expands a table into columns", async () => {
    const { p } = provider(async (req) => ({ id: req.id, result: { connections: [] } }));
    const kids = await p.getChildren({
      t: "table",
      connId: "1",
      table: { name: "orders", columns: [{ name: "id", dataType: "int" }, { name: "total", dataType: "money" }] },
    });
    expect(kids.map((n) => (n.t === "column" ? `${n.column.name}:${n.column.dataType}` : n.t))).toEqual([
      "id:int",
      "total:money",
    ]);
  });

  it("refresh fires onDidChangeTreeData", () => {
    const { p } = provider(async (req) => ({ id: req.id, result: { connections: [] } }));
    const fn = vi.fn();
    p.onDidChangeTreeData(fn);
    p.refresh();
    expect(fn).toHaveBeenCalledOnce();
  });
});
```

- [ ] **Step 2: Extend `test/vscode-stub.ts`** — add before the `default` export and include each in the default object:

```ts
export enum TreeItemCollapsibleState {
  None = 0,
  Collapsed = 1,
  Expanded = 2,
}

export class TreeItem {
  description?: string;
  contextValue?: string;
  iconPath?: unknown;
  constructor(
    public label: string,
    public collapsibleState: TreeItemCollapsibleState = TreeItemCollapsibleState.None,
  ) {}
}

export class ThemeIcon {
  constructor(public id: string) {}
}

export class EventEmitter<T> {
  private listeners: Array<(e: T) => unknown> = [];
  event = (listener: (e: T) => unknown) => {
    this.listeners.push(listener);
    return { dispose: () => (this.listeners = this.listeners.filter((l) => l !== listener)) };
  };
  fire(data: T) {
    for (const l of this.listeners) l(data);
  }
  dispose() {
    this.listeners = [];
  }
}
```

Add `registerTreeDataProvider` to the exported `window` object:

```ts
  registerTreeDataProvider(_id: string, _provider: unknown): Disposable {
    return { dispose: () => {} };
  },
```

- [ ] **Step 3: Run — verify it fails**

Run: `cd editors/vscode && npx vitest run src/connections/tree.test.ts`
Expected: FAIL — module not found.

- [ ] **Step 4: Implement** — `tree.ts`:

```ts
import * as vscode from "vscode";
import {
  newRequestId,
  readUnionTag,
  type ConnectionDefinition,
  type ConnectionListResult,
  type ConnectionSchemaResult,
  type SchemaColumn,
  type SchemaTable,
  type WorkerResponseFrame,
} from "../protocol";
import type { SchemaCache } from "./schema-cache";

export type WorkerLike = {
  send(req: { id: string; method: string; params: unknown }): Promise<WorkerResponseFrame>;
};

export type ConnNode =
  | { t: "connection"; def: ConnectionDefinition }
  | { t: "table"; connId: string; table: SchemaTable }
  | { t: "column"; column: SchemaColumn }
  | { t: "error"; message: string };

export interface ConnectionsTreeDeps {
  getWorker: () => Promise<WorkerLike>;
  schemaCache: SchemaCache;
  log: (line: string) => void;
}

export class ConnectionsTreeProvider implements vscode.TreeDataProvider<ConnNode> {
  private readonly changed = new vscode.EventEmitter<void>();
  readonly onDidChangeTreeData = this.changed.event;

  constructor(private readonly deps: ConnectionsTreeDeps) {}

  refresh(): void {
    this.changed.fire();
  }

  getTreeItem(node: ConnNode): vscode.TreeItem {
    switch (node.t) {
      case "connection": {
        const item = new vscode.TreeItem(node.def.name, vscode.TreeItemCollapsibleState.Collapsed);
        item.description = readUnionTag(node.def.kind) ?? "";
        item.contextValue = "fsqsConnection";
        item.iconPath = new vscode.ThemeIcon("database");
        return item;
      }
      case "table": {
        const item = new vscode.TreeItem(node.table.name, vscode.TreeItemCollapsibleState.Collapsed);
        item.iconPath = new vscode.ThemeIcon("table");
        return item;
      }
      case "column": {
        const item = new vscode.TreeItem(node.column.name, vscode.TreeItemCollapsibleState.None);
        item.description = node.column.dataType;
        item.iconPath = new vscode.ThemeIcon("symbol-field");
        return item;
      }
      case "error": {
        const item = new vscode.TreeItem(node.message, vscode.TreeItemCollapsibleState.None);
        item.iconPath = new vscode.ThemeIcon("warning");
        return item;
      }
    }
  }

  async getChildren(node?: ConnNode): Promise<ConnNode[]> {
    if (!node) {
      return this.listConnections();
    }
    if (node.t === "connection") {
      return this.tablesOf(node.def);
    }
    if (node.t === "table") {
      return node.table.columns.map((column) => ({ t: "column", column }));
    }
    return [];
  }

  private async listConnections(): Promise<ConnNode[]> {
    try {
      const worker = await this.deps.getWorker();
      const frame = await worker.send({ id: newRequestId(), method: "connection/list", params: {} });
      if (frame.error) {
        return [{ t: "error", message: `Could not list connections: ${frame.error.message}` }];
      }
      const result = frame.result as ConnectionListResult;
      return (result.connections ?? []).map((def) => ({ t: "connection", def }));
    } catch (err) {
      this.deps.log(`connection/list failed: ${(err as Error).message}`);
      return [{ t: "error", message: `Connections unavailable: ${(err as Error).message}` }];
    }
  }

  private async tablesOf(def: ConnectionDefinition): Promise<ConnNode[]> {
    const cached = this.deps.schemaCache.get(def.id);
    const tables = cached ?? (await this.fetchSchema(def));
    if (tables === undefined) {
      return [{ t: "error", message: this.lastSchemaError ?? "Schema discovery failed." }];
    }
    return tables.map((table) => ({ t: "table", connId: def.id, table }));
  }

  private lastSchemaError: string | undefined;

  private async fetchSchema(def: ConnectionDefinition): Promise<SchemaTable[] | undefined> {
    try {
      const worker = await this.deps.getWorker();
      const frame = await worker.send({
        id: newRequestId(),
        method: "connection/schema",
        params: { id: def.id },
      });
      if (frame.error) {
        this.lastSchemaError = `${def.name}: ${frame.error.message}`;
        return undefined;
      }
      const tables = (frame.result as ConnectionSchemaResult).tables ?? [];
      this.deps.schemaCache.set(def.id, tables);
      return tables;
    } catch (err) {
      this.lastSchemaError = `${def.name}: ${(err as Error).message}`;
      this.deps.log(`connection/schema failed: ${(err as Error).message}`);
      return undefined;
    }
  }
}
```

- [ ] **Step 5: Run — verify pass**

Run: `cd editors/vscode && npx vitest run src/connections/tree.test.ts`
Expected: PASS (6).

- [ ] **Step 6: Full unit suite + typecheck**

Run: `cd editors/vscode && npm test && npx tsc --noEmit`
Expected: green; no type errors (the stub additions compile).

- [ ] **Step 7: DEVLOG**

```markdown
## 2026-09-07 — F5.11: connections/tree.ts (TreeDataProvider)

`src/connections/tree.ts`: `ConnectionsTreeProvider` — roots from `connection/list`, a connection expands to `connection/schema` tables (cached in `SchemaCache`, fetched once), a table expands to `name: dataType` columns. An error frame / thrown send / failed `getWorker` collapses to a single `error` node (root level) or error child (schema level); nothing throws out of `getChildren`. `refresh()` fires `onDidChangeTreeData`. Icons: `database` / `table` / `symbol-field` / `warning`; connection nodes carry `contextValue="fsqsConnection"`. `test/vscode-stub.ts` gained `TreeItem`, `TreeItemCollapsibleState`, `ThemeIcon`, `EventEmitter`, `window.registerTreeDataProvider`.
```

- [ ] **Step 8: Commit**

```bash
git add editors/vscode/src/connections/tree.ts editors/vscode/src/connections/tree.test.ts editors/vscode/test/vscode-stub.ts docs/DEVLOG.md
git commit -m "feat(vscode): connections/tree.ts TreeDataProvider

connection/list roots -> cached connection/schema tables -> columns.
Error frames/throws collapse to an error node. vscode-stub gains
TreeItem/ThemeIcon/EventEmitter/registerTreeDataProvider.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8"
```

---

## Task 12: `connections/add.ts` — add / edit / remove orchestrators (TS)

**Files:**
- Create: `editors/vscode/src/connections/add.ts`
- Modify: `editors/vscode/test/vscode-stub.ts` (add `window.showQuickPick`, `window.showInputBox`, `window.showWarningMessage`)
- Test: `editors/vscode/src/connections/add.test.ts`

**Interfaces:**
- Consumes: `buildConnectionDefinition` (Task 9); `WorkerLike` (Task 11); `newRequestId`, `ConnectionDefinition`, `ConnectorKindName`, `readUnionTag` from `protocol.ts`.
- Produces:
  - `interface ConnectionUi { pickKind(): Promise<ConnectorKindName | undefined>; input(opts: { prompt: string; value?: string; password?: boolean; placeHolder?: string }): Promise<string | undefined>; confirmRemove(name: string): Promise<boolean> }`
  - `interface SecretWriter { store(key: string, value: string): Thenable<void>; delete(key: string): Thenable<void> }`
  - `interface ConnectionFlowDeps { getWorker: () => Promise<WorkerLike>; ui: ConnectionUi; secrets: SecretWriter; refresh: () => void; log: (l: string) => void }`
  - `function runAddConnection(deps: ConnectionFlowDeps): Promise<void>`
  - `function runEditConnection(deps: ConnectionFlowDeps, existing: ConnectionDefinition): Promise<void>`
  - `function runRemoveConnection(deps: ConnectionFlowDeps, existing: ConnectionDefinition): Promise<void>`
  - Contract:
    - **add**: `pickKind` → cancel aborts. `input` name (required; empty/cancel aborts). Per kind: file path (`Sqlite`/`Csv`/`Excel`), connection string then password (`SqlServer`/`PostgreSql`), url (`RestJson`). `buildConnectionDefinition`. If `secret` present → `secrets.store(secretReference!, secret)` **then** `connection/register`. On any register error → `log` + return (no refresh). Success → `refresh()`.
    - **edit**: kind fixed to `readUnionTag(existing.kind)`; every `input` pre-filled from `existing.configuration`; password prompt "leave blank to keep the current password". `buildConnectionDefinition(input, existing)`. `secret` present → `secrets.store`. Then `connection/register`. `refresh()` on success.
    - **remove**: `ui.confirmRemove(existing.name)` false → return. `connection/remove { id }`; on success, if `existing.secretReference` → `secrets.delete(existing.secretReference)`; `refresh()`. Register/remove errors → `log`, no refresh.

- [ ] **Step 1: Extend `test/vscode-stub.ts`** — add to the exported `window` object:

```ts
  showQuickPick: async (_items: unknown, _opts?: unknown) => undefined as unknown,
  showInputBox: async (_opts?: unknown) => undefined as string | undefined,
  showWarningMessage: async (_message: string, _opts?: unknown, ..._items: string[]) =>
    undefined as string | undefined,
```

- [ ] **Step 2: Write the failing test** — `add.test.ts`:

```ts
import { describe, it, expect, vi } from "vitest";
import { runAddConnection, runEditConnection, runRemoveConnection, type ConnectionUi } from "./add";
import type { ConnectionDefinition } from "../protocol";

function deps(overrides: Partial<Parameters<typeof runAddConnection>[0]> = {}) {
  const sent: Array<{ method: string; params: unknown }> = [];
  const stored: Array<[string, string]> = [];
  const deleted: string[] = [];
  const refresh = vi.fn();
  const base = {
    getWorker: async () => ({
      send: async (req: { id: string; method: string; params: unknown }) => {
        sent.push({ method: req.method, params: req.params });
        return { id: req.id, result: { connections: [] } };
      },
    }),
    ui: {
      pickKind: async () => "PostgreSql" as const,
      input: async (o: { prompt: string }) =>
        o.prompt.includes("name") ? "pg" : o.prompt.includes("assword") ? "hunter2" : "Host=h;Password={password}",
      confirmRemove: async () => true,
    } satisfies ConnectionUi,
    secrets: { store: async (k: string, v: string) => void stored.push([k, v]), delete: async (k: string) => void deleted.push(k) },
    refresh,
    log: () => {},
    ...overrides,
  };
  return { deps: base, sent, stored, deleted, refresh };
}

const existing: ConnectionDefinition = {
  id: "c1",
  name: "pg",
  kind: { Case: "PostgreSql" },
  configuration: { connectionString: "Host=h;Password={password}" },
  secretReference: "fqs-conn-old",
  isTrusted: false,
};

describe("runAddConnection", () => {
  it("stores the secret before connection/register and refreshes", async () => {
    const { deps: d, sent, stored, refresh } = deps();
    await runAddConnection(d);
    expect(stored).toHaveLength(1);
    expect(stored[0][1]).toBe("hunter2");
    const reg = sent.find((s) => s.method === "connection/register")!;
    expect((reg.params as ConnectionDefinition).secretReference).toBe(stored[0][0]);
    expect((reg.params as ConnectionDefinition).secretReference).not.toBeNull();
    expect(refresh).toHaveBeenCalledOnce();
  });

  it("aborts silently when the kind pick is cancelled", async () => {
    const { deps: d, sent, refresh } = deps({ ui: { ...deps().deps.ui, pickKind: async () => undefined } });
    await runAddConnection(d);
    expect(sent).toHaveLength(0);
    expect(refresh).not.toHaveBeenCalled();
  });
});

describe("runEditConnection", () => {
  it("does not store a secret when the password box is left blank", async () => {
    const d = deps({
      ui: {
        pickKind: async () => "PostgreSql" as const,
        input: async (o: { prompt: string }) => (o.prompt.includes("assword") ? "" : "pg"),
        confirmRemove: async () => true,
      },
    });
    await runEditConnection(d.deps, existing);
    expect(d.stored).toHaveLength(0);
    const reg = d.sent.find((s) => s.method === "connection/register")!;
    expect((reg.params as ConnectionDefinition).id).toBe("c1");
    expect((reg.params as ConnectionDefinition).secretReference).toBe("fqs-conn-old");
  });
});

describe("runRemoveConnection", () => {
  it("removes then deletes the secret and refreshes", async () => {
    const d = deps();
    await runRemoveConnection(d.deps, existing);
    expect(d.sent.find((s) => s.method === "connection/remove")?.params).toEqual({ id: "c1" });
    expect(d.deleted).toEqual(["fqs-conn-old"]);
    expect(d.refresh).toHaveBeenCalledOnce();
  });

  it("sends nothing when the confirm is declined", async () => {
    const d = deps({ ui: { ...deps().deps.ui, confirmRemove: async () => false } });
    await runRemoveConnection(d.deps, existing);
    expect(d.sent).toHaveLength(0);
    expect(d.deleted).toHaveLength(0);
  });
});
```

- [ ] **Step 3: Run — verify it fails**

Run: `cd editors/vscode && npx vitest run src/connections/add.test.ts`
Expected: FAIL — module not found.

- [ ] **Step 4: Implement** — `add.ts`:

```ts
import {
  newRequestId,
  readUnionTag,
  type ConnectionDefinition,
  type ConnectorKindName,
} from "../protocol";
import {
  buildConnectionDefinition,
  type ConnectionFormInput,
} from "./definition";
import type { WorkerLike } from "./tree";

export interface ConnectionUi {
  pickKind(): Promise<ConnectorKindName | undefined>;
  input(opts: {
    prompt: string;
    value?: string;
    password?: boolean;
    placeHolder?: string;
  }): Promise<string | undefined>;
  confirmRemove(name: string): Promise<boolean>;
}

export interface SecretWriter {
  store(key: string, value: string): Thenable<void>;
  delete(key: string): Thenable<void>;
}

export interface ConnectionFlowDeps {
  getWorker: () => Promise<WorkerLike>;
  ui: ConnectionUi;
  secrets: SecretWriter;
  refresh: () => void;
  log: (line: string) => void;
}

function stringProp(configuration: unknown, key: string): string {
  const value = (configuration as Record<string, unknown> | null | undefined)?.[key];
  return typeof value === "string" ? value : "";
}

async function collectFields(
  ui: ConnectionUi,
  kind: ConnectorKindName,
  existing?: ConnectionDefinition,
): Promise<ConnectionFormInput | undefined> {
  const name = await ui.input({
    prompt: "Connection name",
    value: existing?.name,
  });
  if (!name || name.trim().length === 0) {
    return undefined;
  }

  const input: ConnectionFormInput = { kind, name: name.trim() };

  if (kind === "Sqlite" || kind === "Csv" || kind === "Excel") {
    const filePath = await ui.input({
      prompt: "Absolute file path",
      value: existing ? stringProp(existing.configuration, "filePath") : undefined,
      placeHolder: "/path/to/file",
    });
    if (filePath === undefined) return undefined;
    input.filePath = filePath;
  } else if (kind === "SqlServer" || kind === "PostgreSql") {
    const connectionString = await ui.input({
      prompt: "Connection string",
      value: existing ? stringProp(existing.configuration, "connectionString") : undefined,
      placeHolder: "Server=...;Database=...;User Id=...;Password={password};",
    });
    if (connectionString === undefined) return undefined;
    input.connectionString = connectionString;
    const password = await ui.input({
      prompt: existing?.secretReference
        ? "Password (leave blank to keep the current one)"
        : "Password (optional)",
      password: true,
    });
    if (password === undefined) return undefined;
    input.password = password;
  } else {
    const url = await ui.input({
      prompt: "Base URL",
      value: existing ? stringProp(existing.configuration, "url") : undefined,
      placeHolder: "https://example.com/api",
    });
    if (url === undefined) return undefined;
    input.url = url;
  }

  return input;
}

async function register(
  deps: ConnectionFlowDeps,
  built: ReturnType<typeof buildConnectionDefinition>,
): Promise<boolean> {
  if (built.secret && built.secretReference) {
    await deps.secrets.store(built.secretReference, built.secret);
  }
  const worker = await deps.getWorker();
  const frame = await worker.send({
    id: newRequestId(),
    method: "connection/register",
    params: built.definition,
  });
  if (frame.error) {
    deps.log(`connection/register failed: ${frame.error.message}`);
    return false;
  }
  return true;
}

export async function runAddConnection(deps: ConnectionFlowDeps): Promise<void> {
  const kind = await deps.ui.pickKind();
  if (!kind) return;
  const input = await collectFields(deps.ui, kind);
  if (!input) return;
  if (await register(deps, buildConnectionDefinition(input))) {
    deps.refresh();
  }
}

export async function runEditConnection(
  deps: ConnectionFlowDeps,
  existing: ConnectionDefinition,
): Promise<void> {
  const kind = readUnionTag(existing.kind) as ConnectorKindName | undefined;
  if (!kind) {
    deps.log(`connection ${existing.id} has an unrecognised kind; cannot edit`);
    return;
  }
  const input = await collectFields(deps.ui, kind, existing);
  if (!input) return;
  if (await register(deps, buildConnectionDefinition(input, existing))) {
    deps.refresh();
  }
}

export async function runRemoveConnection(
  deps: ConnectionFlowDeps,
  existing: ConnectionDefinition,
): Promise<void> {
  if (!(await deps.ui.confirmRemove(existing.name))) return;
  const worker = await deps.getWorker();
  const frame = await worker.send({
    id: newRequestId(),
    method: "connection/remove",
    params: { id: existing.id },
  });
  if (frame.error) {
    deps.log(`connection/remove failed: ${frame.error.message}`);
    return;
  }
  if (existing.secretReference) {
    await deps.secrets.delete(existing.secretReference);
  }
  deps.refresh();
}
```

- [ ] **Step 5: Run — verify pass**

Run: `cd editors/vscode && npx vitest run src/connections/add.test.ts`
Expected: PASS (6).

- [ ] **Step 6: Full unit suite + typecheck**

Run: `cd editors/vscode && npm test && npx tsc --noEmit`
Expected: green.

- [ ] **Step 7: DEVLOG**

```markdown
## 2026-09-07 — F5.12: connections/add.ts (add / edit / remove orchestrators)

`src/connections/add.ts`: `runAddConnection` / `runEditConnection` / `runRemoveConnection` over an injected `ConnectionUi` (`pickKind` / `input` / `confirmRemove`) + `SecretWriter` + `getWorker` + `refresh`. Add: kind → name → per-kind fields (file path / connection string + password / url) → `buildConnectionDefinition` → `secrets.store` (only when a new secret) **before** `connection/register` → `refresh`. Edit: kind fixed, fields pre-filled, blank password keeps the existing `SecretReference`. Remove: `confirmRemove` → `connection/remove` → `secrets.delete` (if any) → `refresh`. A cancelled step or an error frame aborts without refreshing. `test/vscode-stub.ts` gained `showQuickPick` / `showInputBox` / `showWarningMessage`.
```

- [ ] **Step 8: Commit**

```bash
git add editors/vscode/src/connections/add.ts editors/vscode/src/connections/add.test.ts editors/vscode/test/vscode-stub.ts docs/DEVLOG.md
git commit -m "feat(vscode): connections/add.ts add/edit/remove flows

Injected ConnectionUi + SecretWriter. store-before-register ordering;
blank-password-keeps-secret on edit; confirm + secret cleanup on remove.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8"
```

---

## Task 13: `connections/commands.ts` + `package.json` + `extension.ts` wiring (TS)

**Files:**
- Create: `editors/vscode/src/connections/commands.ts`
- Create: `editors/vscode/media/activitybar.svg`
- Modify: `editors/vscode/package.json` (`contributes`, `activationEvents`)
- Modify: `editors/vscode/src/extension.ts` (wire the bridge on first session; register connection features; return an API)
- Test: `editors/vscode/src/extension.test.ts` (append)

**Interfaces:**
- Consumes: `ConnectionsTreeProvider` (Task 11), `SchemaCache` (Task 10), `runAddConnection` / `runEditConnection` / `runRemoveConnection` / `ConnectionUi` (Task 12), `registerSecretBridge` (Task 8); `getWorkerSession` (existing).
- Produces:
  - `interface ConnectionFeatureDeps { getWorker: () => Promise<WorkerLike>; secrets: vscode.SecretStorage; log: (l: string) => void }`
  - `function registerConnectionFeatures(deps: ConnectionFeatureDeps): { disposable: vscode.Disposable; api: ConnectionsApi }`
  - `interface ConnectionsApi { list(): Promise<ConnectionDefinition[]>; schema(id: string): Promise<SchemaTable[]>; addFromInput(input: ConnectionFormInput, existing?: ConnectionDefinition): Promise<void>; remove(id: string): Promise<void> }` — the integration-test seam; `addFromInput` runs `buildConnectionDefinition` + `secrets.store` + `connection/register` with no UI.
  - `extension.ts` `activate` returns `interface FsQueryStudioApi { connections: ConnectionsApi }`.

- [ ] **Step 1: Write the failing test** — append to `extension.test.ts` (this file already imports `activate` / `deactivate` and the `vscode` stub; follow its existing style):

```ts
describe("F5 activation wiring", () => {
  it("activate returns a connections API and registers the four commands", async () => {
    const context = makeFakeContext(); // reuse the helper already in this file
    const api = await activate(context);
    expect(api).toBeDefined();
    expect(typeof api.connections.list).toBe("function");
    for (const id of [
      "fs-query-studio.connections.add",
      "fs-query-studio.connections.edit",
      "fs-query-studio.connections.remove",
      "fs-query-studio.connections.refresh",
    ]) {
      expect(vscodeStub.commands._handlers.has(id)).toBe(true);
    }
    await deactivate();
  });
});
```

*(If `extension.test.ts` has no `makeFakeContext` / `vscodeStub` handles, add the smallest fake `ExtensionContext` — `{ subscriptions: [], extensionPath: ".", secrets: { get: async () => undefined, store: async () => {}, delete: async () => {} } }` — and import the stub module namespace for `_handlers`.)*

- [ ] **Step 2: Run — verify it fails**

Run: `cd editors/vscode && npx vitest run src/extension.test.ts`
Expected: FAIL — `activate` returns `void`; connection commands not registered.

- [ ] **Step 3a: Implement `connections/commands.ts`:**

```ts
import * as vscode from "vscode";
import {
  newRequestId,
  type ConnectionDefinition,
  type ConnectionListResult,
  type ConnectionSchemaResult,
  type SchemaTable,
} from "../protocol";
import { SchemaCache } from "./schema-cache";
import { ConnectionsTreeProvider, type WorkerLike } from "./tree";
import {
  buildConnectionDefinition,
  type ConnectionFormInput,
} from "./definition";
import {
  runAddConnection,
  runEditConnection,
  runRemoveConnection,
  type ConnectionUi,
} from "./add";

const KIND_PICKS: Array<{ label: string; kind: import("../protocol").ConnectorKindName }> = [
  { label: "SQL Server", kind: "SqlServer" },
  { label: "PostgreSQL", kind: "PostgreSql" },
  { label: "SQLite", kind: "Sqlite" },
  { label: "CSV", kind: "Csv" },
  { label: "Excel", kind: "Excel" },
  { label: "REST (JSON)", kind: "RestJson" },
];

export interface ConnectionFeatureDeps {
  getWorker: () => Promise<WorkerLike>;
  secrets: vscode.SecretStorage;
  log: (line: string) => void;
}

export interface ConnectionsApi {
  list(): Promise<ConnectionDefinition[]>;
  schema(id: string): Promise<SchemaTable[]>;
  addFromInput(input: ConnectionFormInput, existing?: ConnectionDefinition): Promise<void>;
  remove(id: string): Promise<void>;
}

export function registerConnectionFeatures(deps: ConnectionFeatureDeps): {
  disposable: vscode.Disposable;
  api: ConnectionsApi;
} {
  const schemaCache = new SchemaCache();
  const tree = new ConnectionsTreeProvider({
    getWorker: deps.getWorker,
    schemaCache,
    log: deps.log,
  });
  const refresh = () => {
    schemaCache.clear();
    tree.refresh();
  };

  const ui: ConnectionUi = {
    async pickKind() {
      const picked = await vscode.window.showQuickPick(KIND_PICKS, {
        placeHolder: "Connector type",
      });
      return picked?.kind;
    },
    async input(opts) {
      return vscode.window.showInputBox({
        prompt: opts.prompt,
        value: opts.value,
        password: opts.password ?? false,
        placeHolder: opts.placeHolder,
        ignoreFocusOut: true,
      });
    },
    async confirmRemove(name) {
      const answer = await vscode.window.showWarningMessage(
        `Remove connection "${name}"? This deletes its stored credential.`,
        { modal: true },
        "Remove",
      );
      return answer === "Remove";
    },
  };

  const flowDeps = { getWorker: deps.getWorker, ui, secrets: deps.secrets, refresh, log: deps.log };

  const view = vscode.window.registerTreeDataProvider("fsQueryStudioConnections", tree);
  const add = vscode.commands.registerCommand("fs-query-studio.connections.add", () =>
    runAddConnection(flowDeps),
  );
  const edit = vscode.commands.registerCommand(
    "fs-query-studio.connections.edit",
    (node?: { t?: string; def?: ConnectionDefinition }) =>
      node?.def ? runEditConnection(flowDeps, node.def) : undefined,
  );
  const remove = vscode.commands.registerCommand(
    "fs-query-studio.connections.remove",
    (node?: { t?: string; def?: ConnectionDefinition }) =>
      node?.def ? runRemoveConnection(flowDeps, node.def) : undefined,
  );
  const refreshCmd = vscode.commands.registerCommand("fs-query-studio.connections.refresh", refresh);

  const send = async (method: string, params: unknown) => {
    const worker = await deps.getWorker();
    return worker.send({ id: newRequestId(), method, params });
  };

  const api: ConnectionsApi = {
    async list() {
      const frame = await send("connection/list", {});
      if (frame.error) throw new Error(frame.error.message);
      return (frame.result as ConnectionListResult).connections ?? [];
    },
    async schema(id) {
      const frame = await send("connection/schema", { id });
      if (frame.error) throw new Error(frame.error.message);
      return (frame.result as ConnectionSchemaResult).tables ?? [];
    },
    async addFromInput(input, existing) {
      const built = buildConnectionDefinition(input, existing);
      if (built.secret && built.secretReference) {
        await deps.secrets.store(built.secretReference, built.secret);
      }
      const frame = await send("connection/register", built.definition);
      if (frame.error) throw new Error(frame.error.message);
      refresh();
    },
    async remove(id) {
      const frame = await send("connection/remove", { id });
      if (frame.error) throw new Error(frame.error.message);
      refresh();
    },
  };

  return {
    disposable: vscode.Disposable.from(view, add, edit, remove, refreshCmd),
    api,
  };
}
```

- [ ] **Step 3b: Implement `media/activitybar.svg`** (monochrome, `currentColor`, 24×24):

```xml
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
  <ellipse cx="12" cy="5" rx="8" ry="3" />
  <path d="M4 5v6c0 1.66 3.58 3 8 3s8-1.34 8-3V5" />
  <path d="M4 11v6c0 1.66 3.58 3 8 3s8-1.34 8-3v-6" />
</svg>
```

- [ ] **Step 3c: `package.json`** — add to `contributes` (alongside `commands` / `notebooks` / `notebookRenderer`):

```jsonc
"viewsContainers": {
  "activitybar": [
    { "id": "fs-query-studio", "title": "F# Query Studio", "icon": "media/activitybar.svg" }
  ]
},
"views": {
  "fs-query-studio": [
    { "id": "fsQueryStudioConnections", "name": "Connections" }
  ]
},
"viewsWelcome": [
  {
    "view": "fsQueryStudioConnections",
    "contents": "No connections registered yet.\n[Add Connection](command:fs-query-studio.connections.add)"
  }
],
"menus": {
  "view/title": [
    { "command": "fs-query-studio.connections.add", "when": "view == fsQueryStudioConnections", "group": "navigation" },
    { "command": "fs-query-studio.connections.refresh", "when": "view == fsQueryStudioConnections", "group": "navigation" }
  ],
  "view/item/context": [
    { "command": "fs-query-studio.connections.edit", "when": "view == fsQueryStudioConnections && viewItem == fsqsConnection", "group": "inline" },
    { "command": "fs-query-studio.connections.remove", "when": "view == fsQueryStudioConnections && viewItem == fsqsConnection" }
  ]
}
```

Extend `commands` with:

```jsonc
{ "command": "fs-query-studio.connections.add", "title": "F# Query Studio: Connections: Add Connection", "icon": "$(add)" },
{ "command": "fs-query-studio.connections.edit", "title": "F# Query Studio: Connections: Edit Connection", "icon": "$(edit)" },
{ "command": "fs-query-studio.connections.remove", "title": "F# Query Studio: Connections: Remove Connection", "icon": "$(trash)" },
{ "command": "fs-query-studio.connections.refresh", "title": "F# Query Studio: Connections: Refresh", "icon": "$(refresh)" }
```

Add to `activationEvents`: `"onView:fsQueryStudioConnections"`.

- [ ] **Step 3d: `extension.ts`** — imports:

```ts
import { registerConnectionFeatures, type ConnectionsApi } from "./connections/commands";
import { registerSecretBridge } from "./secrets/bridge";
```

Wrap `getWorker` so the first resolution installs the bridge, and export an API type:

```ts
export interface FsQueryStudioApi {
  connections: ConnectionsApi;
}
```

Inside `activate`, replace the `getWorker` definition:

```ts
  let bridgeRegistered = false;
  const getWorker = async () => {
    const session = await getWorkerSession({
      extensionRoot: context.extensionPath,
      env: process.env,
      log: (line) => outputChannel!.appendLine(line),
    });
    if (!bridgeRegistered) {
      bridgeRegistered = true;
      registerSecretBridge({ client: session.client, secrets: context.secrets });
    }
    return session.client;
  };
```

After `languageFeatures`:

```ts
  const connections = registerConnectionFeatures({
    getWorker,
    secrets: context.secrets,
    log: (line) => outputChannel!.appendLine(line),
  });
```

Add `connections.disposable` to the `context.subscriptions.push(...)` list. Change the signature to `export async function activate(context: vscode.ExtensionContext): Promise<FsQueryStudioApi>` and `return { connections: connections.api };` at the end.

- [ ] **Step 4: Run — verify pass**

Run: `cd editors/vscode && npx vitest run src/extension.test.ts`
Expected: PASS.

- [ ] **Step 5: Full suite + compile**

Run: `cd editors/vscode && npm test && npm run compile`
Expected: `npm test` green; `tsc --noEmit` clean; esbuild bundle written.

- [ ] **Step 6: DEVLOG**

```markdown
## 2026-09-07 — F5.13: connection features wired + Activity Bar contribution

`src/connections/commands.ts`: `registerConnectionFeatures({ getWorker, secrets, log })` — builds the `SchemaCache` + `ConnectionsTreeProvider`, the real `ConnectionUi` (`showQuickPick` six kinds / `showInputBox` / modal `showWarningMessage`), registers the `fsQueryStudioConnections` tree and the four `fs-query-studio.connections.*` commands, and returns `{ disposable, api }`. The `api` (`list` / `schema` / `addFromInput` / `remove`) is the integration-test seam — same operations, no UI. `package.json` gains the `fs-query-studio` Activity Bar container + view + `viewsWelcome` + menus + `onView` activation; `media/activitybar.svg` is the container glyph. `extension.ts`: `getWorker` now installs `registerSecretBridge` on the session client the first time it resolves; `activate` registers the connection features and returns `FsQueryStudioApi`.
```

- [ ] **Step 7: Commit**

```bash
git add editors/vscode/src/connections/commands.ts editors/vscode/media/activitybar.svg editors/vscode/package.json editors/vscode/src/extension.ts editors/vscode/src/extension.test.ts docs/DEVLOG.md
git commit -m "feat(vscode): wire connections tree, commands, secret bridge

Activity Bar container + view + welcome + menus; registerConnectionFeatures
builds the real QuickPick UI and a UI-less api seam; activate installs the
secret bridge on first session resolve and returns FsQueryStudioApi.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8"
```

---

## Task 14: extension-host integration spec (TS)

**Files:**
- Create: `editors/vscode/test/integration/connections.test.ts`
- Create: `editors/vscode/test/integration/fixtures/sample-connections.db` (a tiny SQLite file — 1 table `widgets(id INTEGER, label TEXT)`; generate it in the test's `before` if committing a binary is undesirable, using `fs` + the bytes of an empty-then-`CREATE TABLE` DB, or via the worker itself — simplest: commit the ~12 KB file)
- Modify: `editors/vscode/tsconfig.integration.json` only if a new path glob is needed (it already globs `test/integration/**`)

**Interfaces:**
- Consumes: `FsQueryStudioApi` returned by `activate` (Task 13); the bundled worker at `dist/worker/FSharpQueryStudio.Worker.dll` (same gate as `intelligence.test.ts`).

- [ ] **Step 1: Write the spec** — `connections.test.ts` (mirrors `intelligence.test.ts`'s gating + activation):

```ts
import * as assert from "node:assert";
import * as fs from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";

const EXTENSION_ID = "fs-query-studio.fs-query-studio";
const extensionRoot = path.resolve(__dirname, "..", "..", "..");
const dbFixture = path.join(extensionRoot, "test", "integration", "fixtures", "sample-connections.db");
const bundledWorker = path.join(extensionRoot, "dist", "worker", "FSharpQueryStudio.Worker.dll");
const hasWorker = fs.existsSync(bundledWorker);

describe("fsquery connections", () => {
  let api: { connections: {
    list(): Promise<Array<{ id: string; name: string }>>;
    schema(id: string): Promise<Array<{ name: string; columns: Array<{ name: string }> }>>;
    addFromInput(input: unknown): Promise<void>;
    remove(id: string): Promise<void>;
  } } };

  before(async () => {
    const ext = vscode.extensions.getExtension(EXTENSION_ID);
    assert.ok(ext, `extension ${EXTENSION_ID} not found`);
    api = (await ext!.activate()) as typeof api;
  });

  (hasWorker ? it : it.skip)("registers a SQLite connection and reads its schema", async function () {
    this.timeout(120000);
    await api.connections.addFromInput({ kind: "Sqlite", name: "widgets-db", filePath: dbFixture });

    const list = await api.connections.list();
    const created = list.find((c) => c.name === "widgets-db");
    assert.ok(created, "widgets-db not in connection/list");

    const tables = await api.connections.schema(created!.id);
    const widgets = tables.find((t) => t.name.toLowerCase() === "widgets");
    assert.ok(widgets, `expected a 'widgets' table, got: ${tables.map((t) => t.name).join(", ")}`);
    assert.ok(widgets!.columns.some((c) => c.name.toLowerCase() === "label"));

    await api.connections.remove(created!.id);
    assert.ok(!(await api.connections.list()).some((c) => c.id === created!.id));
  });

  (hasWorker ? it : it.skip)(
    "resolves a {password} token from SecretStorage over the reverse channel",
    async function () {
      this.timeout(120000);
      // Substitute the secret into the *host* portion of a PostgreSQL string,
      // so the connection error text echoes it back — proving the worker
      // fetched it from context.secrets via secret/get, not the literal token.
      const sentinel = `bridge-sentinel-${Date.now()}`;
      // addFromInput stores the password under a minted reference:
      await api.connections.addFromInput({
        kind: "PostgreSql",
        name: "bridge-check",
        connectionString: "Host={password};Port=5432;Username=u;Database=d",
        password: sentinel,
      });
      const created = (await api.connections.list()).find((c) => c.name === "bridge-check")!;

      let message = "";
      try {
        await api.connections.schema(created.id);
      } catch (err) {
        message = (err as Error).message;
      }
      assert.ok(
        message.includes(sentinel),
        `expected the resolved host '${sentinel}' in the failure, got: ${message}`,
      );
      assert.ok(!message.includes("{password}"), "the {password} token was not substituted");

      await api.connections.remove(created.id);
    },
  );
});
```

- [ ] **Step 2: Run locally without a worker — verify it skips green**

Run: `cd editors/vscode && npm run pretest:vscode && npm run test:vscode`
Expected: the two `connections` specs report **pending** (no bundled worker), suite green. (If a worker is bundled locally, they run and must pass.)

- [ ] **Step 3: Verify the CI gate** — confirm `.github/workflows` `vscode-extension` job publishes `dist/worker` before `npm run test:vscode` (it already does for `intelligence.test.ts`). No workflow edit expected; if the job filters test files, add `connections.test.ts` to its glob.

- [ ] **Step 4: DEVLOG**

```markdown
## 2026-09-07 — F5.14: connections extension-host integration spec

`test/integration/connections.test.ts` (+ `fixtures/sample-connections.db`): via the `activate` API seam, registers a SQLite connection and asserts it appears in `connection/list` and that `connection/schema` returns the fixture's real `widgets` table + `label` column, then removes it. A second spec registers a PostgreSQL connection whose `Host={password}` resolves a `SecretStorage`-stored sentinel and asserts the `connection/schemaFailed` message echoes the *resolved* host (not `{password}`) — an end-to-end proof of the reverse `secret/get` channel. Both gated on a bundled worker (pending/green without one, run in the `vscode-extension` CI workflow).
```

- [ ] **Step 5: Commit**

```bash
git add editors/vscode/test/integration/connections.test.ts editors/vscode/test/integration/fixtures/sample-connections.db docs/DEVLOG.md
git commit -m "test(vscode): extension-host spec for F5 connections + secret bridge

connection/list + connection/schema over a real worker via the activate
API seam; a {password} PostgreSQL connection proves secret/get end to end.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8"
```

---

## Task 15: F5 close-out — spec status + rollout + DEVLOG

**Files:**
- Modify: `docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md` (Status line ~`:249`; the **F5 Connections** rollout row ~`:245`)
- Modify: `docs/DEVLOG.md` (close-out entry)

- [ ] **Step 1: Run every suite and record counts**

```bash
dotnet test tests/FSharpQueryStudio.Worker.Tests
cd editors/vscode && npm test && npm run compile && npm run pretest:vscode && npm run test:vscode
```
Record: Worker.Tests pass count; `npm test` pass/skip; `test:vscode` passing/pending.

- [ ] **Step 2: Update the migration design** — Status line: add `F5 Connections is complete on \`main-vscode\` (not yet merged) — see the DEVLOG close-out entry (\`2026-09-07 — F5 complete\`). F6 Parity + retire is next.` Update the **F5 Connections** rollout-table row's status marker to match how F1–F4 rows read once done (`intact` → whatever the completed-phase convention is; mirror the F4 row).

- [ ] **Step 3: Write the DEVLOG close-out** — append to `docs/DEVLOG.md`:

```markdown
## 2026-09-07 — F5 complete: connections

The extension manages connections. A new **`fs-query-studio` Activity Bar container** hosts a **Connections** tree: roots from a new `connection/list` RPC, each connection lazily expands to `connection/schema` tables → columns (cached per session, cleared by the **Refresh** command). An **Add Connection** QuickPick (`connections/add.ts`, driven by an injected `ConnectionUi`) covers all six connector kinds — file path for Sqlite/Csv/Excel, connection string + password for SqlServer/PostgreSql, URL for RestJson — writing the password to `context.secrets` **before** `connection/register`, which only ever receives the `SecretReference`. Edit re-registers under the same `Id` (blank password keeps the existing credential); Remove confirms modally, then `connection/remove` + `context.secrets.delete`.

**Reverse RPC channel (worker → host).** The worker can now *initiate* a request: `HostChannel` mints a `host-<32hex>` id, writes a `WorkerRequestFrame` through the stdout `writeLock`, and awaits a response the read loop routes back via `TryComplete`. `Program.fs`'s loop peeks each inbound line — no `method` → it's a reply, hand to `HostChannel`; otherwise dispatch unchanged. `BridgeSecretStore : ISecretStore` (composed in `main` in place of `WindowsCredentialStore` on the worker's active path) answers a synchronous `TryRead` by blocking on a host-bound `secret/get` (5 s timeout, `None` on any failure — the "missing secret → connection failure" contract `ConnectionStringSecrets.resolve` already documents). `WorkerClient.setHostRequestHandler` + `secrets/bridge.ts` answer `secret/get` from VS Code `SecretStorage`; unknown method / bad params / read failure → an error reply. Secrets are now cross-platform (no `WindowsCredentialStore` in the worker path).

Scope, per the F5 spec: **`secret/get` only** — `BridgeSecretStore.Save` / `Delete` raise `NotSupportedException` (the extension owns every credential write); `secret/set` / `secret/delete` over the bridge are unbuilt. No `connection/update` RPC (edit = re-register). No connection-string builder UI or "Test connection" button (schema-on-expand is the implicit test). No file picker for file connectors (free-text path, matching the Avalonia dialog). The Connections view is usable with no notebook open — `onView:fsQueryStudioConnections` resolves the shared worker session.

Known limitations (carried): the schema cache is never invalidated except by **Refresh** (a table added mid-session is invisible until then); `BridgeSecretStore.TryRead`'s 5 s blocking wait ties up the evaluation thread and cannot be cancelled during that window; a single global `workspace.json`, no per-folder scoping; the reverse channel carries exactly one method — it is not yet a general host-service surface.

New `src/` F# files: `HostChannel.fs`, `BridgeSecretStore.fs`. `Handlers.fs` gained `connection/list` + a `configureSecretStore` seam; `Program.fs`'s loop routes response frames; `NdjsonStream.readLine` added. Avalonia untouched (`WindowsCredentialStore` stays in the tree for `App.axaml.fs`, retired at F6). New extension modules: `src/connections/{definition,schema-cache,tree,add,commands}.ts`, `src/secrets/bridge.ts`; `WorkerClient` reverse dispatch; `activate` returns `FsQueryStudioApi`.

Verification (record actual numbers at close-out): `dotnet test tests/FSharpQueryStudio.Worker.Tests` — N passed; `npm test` (editors/vscode) — N passed / M skipped; `npm run compile` — tsc clean, bundle written; `npm run test:vscode` — P passing / Q pending. **Merge gate:** the worker-backed `test/integration/connections.test.ts` (and the F2–F4 integration specs) run only in the `vscode-extension` CI workflow, which publishes the worker — that run must be green before merge.
```

- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md docs/DEVLOG.md
git commit -m "docs: F5 Connections phase complete

Reverse secret/get channel, BridgeSecretStore, connection/list, Activity
Bar connections tree + add/edit/remove QuickPick. Migration design status
and rollout row updated.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8"
```

---

## Self-Review

**Spec coverage:**

| Spec section | Task(s) |
|---|---|
| Goal: connections tree + lazy schema + Refresh | 11, 13 |
| Goal: add/edit/remove all six kinds, secret before register | 9, 12, 13 |
| Goal: reverse RPC channel worker→host | 2, 4, 7 |
| Goal: `BridgeSecretStore` composed on the worker path | 3, 5 |
| Goal: view usable with no notebook (`onView`) | 13 |
| Non-goal: `secret/get` only, Save/Delete throw | 5 (F#), 8 (TS) |
| Non-goal: 5 s timeout → None | 5 |
| Non-goal: no `connection/update` (edit re-registers) | 9, 12 |
| Architecture: `connections/definition.ts` | 9 |
| Architecture: `connections/schema-cache.ts` | 10 |
| Architecture: `connections/tree.ts` | 11 |
| Architecture: `connections/add.ts` | 12 |
| Architecture: `connections/commands.ts` | 13 |
| Architecture: `secrets/bridge.ts` | 8 |
| `WorkerClient` reverse-channel additions | 7 |
| Worker: `HostChannel.fs` | 2 |
| Worker: `BridgeSecretStore.fs` | 5 |
| Worker: `Program.fs` loop + composition swap | 3, 4, 5 |
| Worker: `connection/list` handler | 1 |
| `protocol.ts` additions | 6 |
| `package.json` contributions | 13 |
| `extension.ts` wiring | 13 |
| Error handling table (schema failure → error node, crash, timeout, dismissed confirm) | 5, 11, 12 |
| Testing: unit (all `connections/*`, `secrets/bridge`, `client`, `protocol`) | 6–13 |
| Testing: F# (`BridgeSecretStore`, `HostChannel`, loop routing, `handleConnectionList`) | 1, 2, 4, 5 |
| Testing: integration (`connections.test.ts`) | 14 |
| Rollout: migration design status/row + DEVLOG close-out | 15 |

No uncovered spec requirement.

**Placeholder scan:** the two integration-test helper caveats (`makeFakeContext` / `makeClient` "if the file has no such harness") point at concrete fallback shapes, not "TODO". The Npgsql error-text assertion in Task 3 carries an explicit weaker fallback. No bare TODO/TBD.

**Type consistency:** `WorkerLike` is defined once (Task 11) and imported by Tasks 12–13. `ConnectionFormInput` — Task 9, reused Tasks 12–13. `BuiltConnection` — Task 9. `HostRequestHandler` — Task 7, consumed structurally by Task 8's `HostRequestClient`. `HostChannel` members `Request` / `TryComplete` — Task 2, consumed Tasks 4–5. `Handlers.configureSecretStore` — Task 3, called Task 5. `ConnectionsApi` — Task 13, consumed Task 14. `refresh` clears the cache **and** fires the tree in Tasks 12/13 (`runAdd/Edit/Remove` call the injected `refresh`, which Task 13 defines as `schemaCache.clear(); tree.refresh()`); Task 11's `ConnectionsTreeProvider.refresh()` only fires the emitter — the cache clear is the caller's, consistent across tasks.

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-09-07-vscode-extension-f5-connections.md`. Two execution options:**

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.

**Which approach?**
