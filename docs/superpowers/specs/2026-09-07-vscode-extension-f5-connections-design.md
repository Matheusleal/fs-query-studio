# VS Code Extension — F5 Connections — Design

Fifth phase of the Avalonia → VS Code extension migration (`docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md`). F1 Scaffold, F2 Minimal notebook, F3 Renderer, and F4 Cell intelligence are complete on `main-vscode`. This phase adds connection management: an Activity Bar tree of registered connections with lazy schema navigation, a QuickPick to add / edit / remove connections, and — the one structural change — a **reverse RPC channel** from the worker to the host so the worker can read a connection's secret from `context.secrets` at evaluation time.

## Background

The worker already answers `connection/register`, `connection/remove`, and `connection/schema` (`src/FSharpQueryStudio.Worker/Handlers.fs`), and persists non-secret `ConnectionDefinition` records to `workspace.json` via `src/FSharpQueryStudio.Storage/Workspace.fs`. There is **no** bare list method — `register` / `remove` each return the full updated list, but nothing returns it without mutating.

Secrets never touch `workspace.json`. A `ConnectionDefinition.SecretReference` is an opaque key into an `ISecretStore` (`src/FSharpQueryStudio.Security/ISecretStore.fs`); the connectors substitute a `{password}` token in the connection string from that store at resolve time (`ConnectionStringSecrets.resolve` in `src/FSharpQueryStudio.Connectors/WorkspaceConnectionResolver.fs`). `ISecretStore` is **synchronous**:

```fsharp
type ISecretStore =
    abstract Save: reference: string -> secret: string -> unit
    abstract TryRead: reference: string -> string option
    abstract Delete: reference: string -> unit
```

Today the worker composes `WindowsCredentialStore()` directly in two places — `Handlers.fs:136` (the `WorkspaceConnectionResolver` for `execution/evaluate`) and `Handlers.fs:550` (the resolver for `connection/schema`). That is Windows-only and reads a store the extension host does not own. The migration design's plan: replace it with a `BridgeSecretStore : ISecretStore` that forwards `TryRead` to the host over a reverse channel, so the credential lives in VS Code's `SecretStorage` (cross-platform, per the migration design's "Worker changes" section).

The worker's `Program.fs` read loop today reads only `WorkerRequestFrame` and writes only `WorkerResponseFrame`. It has no path for a worker-initiated request. `src/FSharpQueryStudio.Worker/Program.fs:14`'s `writeLock` already serialises concurrent stdout writes (the backgrounded `execution/evaluate` task vs. the loop) — the reverse channel reuses it.

On the extension side, `editors/vscode/src/worker/client.ts`'s `WorkerClient.onFrame` ignores any inbound frame whose `id` is not in its `pending` map. There is no host-request handler. The worker session (`editors/vscode/src/worker/session.ts`, `getWorkerSession`) is a lazy memoised singleton, spawned on first `getWorker()` — today only from notebook activation (`onNotebook:fsquery-notebook`).

The Avalonia reference for the add / edit UX is `src/FSharpQueryStudio.Ui/MainWindow.axaml.fs:410` (`showConnectionDialog`): pick kind → name → per-kind fields (file path for Sqlite/Csv/Excel; connection string + password for SqlServer/PostgreSql; URL for RestJson), password stored under a minted `SecretReference` only if non-blank, blank-on-edit keeps the existing reference.

## Goals

- A `connections/` module registering a `TreeDataProvider` in a new Activity Bar view container:
  - roots ← `connection/list` (new RPC), one node per `ConnectionDefinition`
  - expand a connection → `connection/schema` (once, cached per connection `Id` for the session) → `Tables` → `Columns` (`name: dataType`)
  - a **Refresh** command clears the schema cache and re-lists
- An add / edit / remove flow driven by `QuickPick` / `InputBox`, replacing the Avalonia dialog. All six connector kinds, per-kind fields. The password is written to `context.secrets` **before** `connection/register`, which receives only the `SecretReference`.
- A **reverse RPC channel** worker → host: the worker mints a `host-`-prefixed request id, writes a request frame, and awaits a response frame routed by id. Used this phase for exactly one method, `secret/get`.
- `BridgeSecretStore : ISecretStore` in the worker, composed in place of `WindowsCredentialStore` on the extension-host worker path, so a `{password}` connection resolves its credential from VS Code `SecretStorage` during `execution/evaluate` and `connection/schema`.
- The Connections view is usable with **no notebook open** — opening it resolves the shared worker session (new `onView` activation event).

## Non-goals

- **No `secret/set` / `secret/delete` over the bridge.** The extension owns every `context.secrets` write (around `connection/register` and the remove command); nothing in the worker calls `ISecretStore.Save` / `Delete`. `BridgeSecretStore.Save` / `Delete` raise `NotSupportedException`. (The migration design lists all three host-bound methods; only `secret/get` has a caller, so only it is built. Revisit if a worker-initiated write ever appears.)
- **No indefinite wait in `BridgeSecretStore.TryRead`.** It blocks with a 5 s timeout and falls back to `None` (a missing secret already surfaces as a connection failure at use — the established convention in `ConnectionStringSecrets.resolve`).
- **No `connection/update` RPC.** Edit re-registers under the same `Id` via `connection/register` (which already replaces by `Id`).
- **No connection-string builder UI / field-by-field form.** One free-text connection-string `InputBox` with a `{password}` placeholder, matching the Avalonia dialog. No live "Test connection" button (schema discovery on tree-expand is the implicit test).
- **No `workspace/prelude`, no loose-script (`.fsx`) connection help** — F7 backlog.
- **No change to `App.axaml.fs`** or any Avalonia composition — retired at F6.
- **No change to `Fsac`, `Execution`, `Connectors`, `Results`, or the NDJSON framing / `Serialization.options`.** The reverse channel reuses the existing frame types and `writeLock`.
- **No multi-workspace / multi-`workspace.json` handling** beyond what `Workspace.workspaceDir` already does.

## Architecture

### Extension module layout — `editors/vscode/src/`

| File | Responsibility | Pure? |
|---|---|---|
| `connections/definition.ts` | `buildConnectionDefinition(input, existing?)` → `{ definition: ConnectionDefinition, secret: string \| undefined, secretReference: string \| undefined }`. Per-kind `Configuration` assembly, `SecretReference` minting (`fqs-conn-<uuid-no-dashes>`), blank-password-keeps-existing rule. | yes |
| `connections/schema-cache.ts` | `SchemaCache` — `get(id)` / `set(id, tables)` / `clear()` over a `Map<string, SchemaTable[]>`. | yes |
| `connections/tree.ts` | `ConnectionsTreeProvider implements vscode.TreeDataProvider<ConnNode>`. `getChildren` — no arg → `connection/list`; a connection node → cached-or-fetched `connection/schema`; a table node → its columns. `refresh()` fires `_onDidChangeTreeData`. Takes `{ getWorker, schemaCache, log }`. | no (holds the emitter) |
| `connections/add.ts` | `runAddConnection` / `runEditConnection` — the multi-step `QuickPick` / `InputBox` sequence, calling `buildConnectionDefinition`, then `secrets.store` then `connection/register`. Takes `{ getWorker, secrets, show... }` (all `vscode.window` entry points injected for tests). | no |
| `connections/commands.ts` | `registerConnectionFeatures({ getWorker, secrets, log }): vscode.Disposable` — registers the tree provider and the four commands, bundles their `Disposable`s. Composition root. | no |
| `secrets/bridge.ts` | `registerSecretBridge({ client, secrets }): void` — sets the `WorkerClient` host-request handler; answers `secret/get { reference }` → `{ secret: string \| null }` from `secrets.get`. Unknown method → `{ error: { code: "host/unknownMethod", message } }`. | no |

`getWorker` is the existing `() => Promise<WorkerClient>` closure from `extension.ts` (returns the memoised session client). `secrets` is `vscode.SecretStorage` (`context.secrets`), narrowed to `{ get, store, delete }` at the injection boundary.

### `WorkerClient` reverse-channel additions — `editors/vscode/src/worker/client.ts`

- New optional constructor field `onHostRequest?: (method: string, params: unknown) => Promise<{ result?: unknown; error?: WorkerErrorInfo }>`.
- `onFrame(frame)`: if `frame.method` is a non-empty string → it is a host-bound request. Call `onHostRequest(method, params)`; `writeFrame(stdin, { id: frame.id, result })` or `{ id: frame.id, error }`. If no handler is set → reply `{ id, error: { code: "host/unhandled", message } }`. Frames with no `method` keep the current id-correlation path unchanged.
- A thrown / rejected `onHostRequest` → `{ id, error: { code: "host/threw", message } }`; never an unhandled rejection.
- The handler is set once per `WorkerClient` instance (survives an internal process respawn), by `registerSecretBridge`, called from `extension.ts` right after the session resolves.

### Worker reverse channel — `src/FSharpQueryStudio.Worker/`

**`HostChannel.fs` (new).**

```fsharp
type HostChannel(writeFrame: WorkerRequestFrame -> Task) =
    let pending = ConcurrentDictionary<string, TaskCompletionSource<WorkerResponseFrame>>()
    member _.Request(method: string, params: obj) : Task<WorkerResponseFrame>   // mints "host-<guid>"
    member _.TryComplete(frame: WorkerResponseFrame) : bool                     // routes an inbound methodless frame
```

- `Request` mints `host-<Guid.NewGuid("N")>`, registers a TCS, serialises `params` with `Serialization.options`, writes the frame (through the caller-supplied writer, which goes via `Program.fs`'s `writeLock`), returns the TCS task.
- `TryComplete` looks the id up in `pending`; found → set the result, return `true`; not found → `false` (the loop then treats it as a normal — malformed — request, unchanged behaviour).

**`Program.fs` loop change.** After `NdjsonStream.readFrame<WorkerRequestFrame>`, before the method dispatch: if the frame's `Method` is null/empty, deserialise the same line as `WorkerResponseFrame` and call `hostChannel.TryComplete`; if it routed, `continue` the loop. Otherwise fall through to the existing dispatch. `main` constructs one `HostChannel` wrapping `writeResponse`-style writes and threads it into `Handlers`.

**`BridgeSecretStore.fs` (new), `FSharpQueryStudio.Worker`.**

```fsharp
type BridgeSecretStore(host: HostChannel, ?timeout: TimeSpan) =
    interface ISecretStore with
        member _.TryRead reference =
            // host.Request("secret/get", {| reference = reference |})
            //   .Wait(timeout ?? 5s); on completion → result.secret |> Option.ofObj
            //   on timeout / error frame / null result / exception → None
        member _.Save _ _ = raise (NotSupportedException "...")
        member _.Delete _ = raise (NotSupportedException "...")
```

`TryRead` runs on the backgrounded `execution/evaluate` / `connection/schema` task thread (both are already `Task.Run`-dispatched in `Program.fs`), so the blocking wait cannot stall the read loop — the loop stays free to receive the `secret/get` response and route it via `TryComplete`.

**Composition swap.** `Handlers.fs:136` and `Handlers.fs:550` take the injected `ISecretStore` instead of constructing `WindowsCredentialStore()`. `Handlers` gains one `ISecretStore` (or `HostChannel`) parameter at its composition boundary, supplied by `Program.fs`. `WindowsCredentialStore` stays in the tree (still referenced by `App.axaml.fs` until F6) but leaves the worker's active path.

**`connection/list` handler.**

```fsharp
let handleConnectionList (request: WorkerRequestFrame) : WorkerResponseFrame =
    let connections = Workspace.readConnections (workspaceDir ())
    { Id = request.Id
      Result = Some (JsonSerializer.SerializeToElement({| connections = connections |}, Serialization.options))
      Error = None }
```

Added to `dispatch` as `"connection/list"`. Synchronous, inline (like `results/discard`) — a cheap file read, no need for the background path.

### `protocol.ts` additions — `editors/vscode/src/`

- `type ConnectorKindName = "SqlServer" | "PostgreSql" | "Sqlite" | "Csv" | "Excel" | "RestJson"`. The wire form is an F# DU with the project's default `JsonFSharpConverter` encoding — **`{ "Case": "Sqlite" }`**, same as `Diagnostic.severity` in F4 (verified: `Serialization.options` adds `JsonFSharpConverter()` with no `JsonUnionEncoding` override, so fieldless tags are *not* unwrapped; `tests/FSharpQueryStudio.Protocol.Tests/Tests.fs` round-trips every kind). Decode with the existing `readUnionTag`; encode with `makeUnionTag`.
- `interface ConnectionDefinition { id: string; name: string; kind: UnionTag<ConnectorKindName>; configuration: unknown; secretReference: string | null; isTrusted: boolean }` — `configuration` is an opaque nested JSON object, shaped per kind by `buildConnectionDefinition`; `secretReference` is an F# `string option` (bare value or `null` on the wire).
- `interface SchemaColumn { name: string; dataType: string }`, `interface SchemaTable { name: string; columns: SchemaColumn[] }`.
- `interface ConnectionListResult { connections: ConnectionDefinition[] }`, `interface ConnectionSchemaResult { tables: SchemaTable[] }`, `ConnectionRemoveParams { id: string }`, `ConnectionSchemaParams { id: string }`.
- `interface HostRequestFrame { id: string; method: string; params: unknown }` — an inbound frame with `method` set. Reuses `WorkerErrorInfo` for the reply error.
- Per-kind `Configuration` shapes (used only inside `buildConnectionDefinition`): `{ filePath }` (Sqlite/Csv/Excel), `{ connectionString }` (SqlServer/PostgreSql), `{ url, method: "GET", headers: [], queryParams: [], body: null }` (RestJson) — matching `MainWindow.axaml.fs:527`.

### Add / edit flow — `connections/add.ts`

`runAddConnection`:
1. `showQuickPick` the six kinds (labels: `SQL Server`, `PostgreSQL`, `SQLite`, `CSV`, `Excel`, `REST (JSON)`).
2. `showInputBox` name (required, non-blank).
3. Per kind:
   - Sqlite / Csv / Excel → `showInputBox` file path (prompt notes an absolute path; no file picker this phase — parity with the Avalonia free-text box).
   - SqlServer / PostgreSql → `showInputBox` connection string (`value` placeholder `Server=...;Database=...;User Id=...;Password={password};`), then `showInputBox` password (`password: true`, optional).
   - RestJson → `showInputBox` URL.
4. `buildConnectionDefinition(input)` → definition + optional secret.
5. If a secret was entered: `secrets.store(secretReference, secret)` **then** `getWorker().send(connection/register)`. Order matters — a crash between the two leaves an orphan secret (harmless) rather than a connection pointing at a missing credential.
6. Tree `refresh()`.

`runEditConnection(node)`: same, but kind is fixed (not re-prompted), every field pre-filled from the definition's `configuration`, the password box empty with prompt "leave blank to keep the current password". `buildConnectionDefinition(input, existing)`:
- keeps `existing.id`, `existing.isTrusted`;
- `secretReference` = `existing.secretReference` if set, else a fresh mint iff a new password was typed;
- returns `secret: undefined` when the password box was blank → the caller skips `secrets.store`, leaving the existing credential untouched.

`runRemoveConnection(node)`: `showWarningMessage(\`Remove connection "${name}"?\`, { modal: true }, "Remove")`; on confirm → `connection/remove { id }` → `secrets.delete(secretReference)` if present → `refresh()`.

### `package.json` contributions

- `viewsContainers.activitybar`: `{ id: "fs-query-studio", title: "F# Query Studio", icon: "media/activitybar.svg" }` (a simple database glyph; `$(database)` is not valid for a container icon — an SVG asset is required).
- `views`: `{ "fs-query-studio": [ { id: "fsQueryStudioConnections", name: "Connections" } ] }`.
- `viewsWelcome`: `{ view: "fsQueryStudioConnections", contents: "No connections yet.\n[Add Connection](command:fs-query-studio.connections.add)" }`.
- `commands`: `fs-query-studio.connections.add` (`$(add)`), `.edit`, `.remove` (`$(trash)`), `.refresh` (`$(refresh)`) — titles prefixed `F# Query Studio: Connections:`.
- `menus`:
  - `view/title` → `add`, `refresh` (`when: view == fsQueryStudioConnections`, `group: navigation`).
  - `view/item/context` → `edit`, `remove` (`when: view == fsQueryStudioConnections && viewItem == fsqsConnection`).
- `activationEvents`: add `onView:fsQueryStudioConnections` (keeps `onNotebook:fsquery-notebook`, `onCommand:fs-query-studio.checkWorker`).

Tree node `contextValue`: `"fsqsConnection"` for connection roots; table / column / error nodes leave it unset.

### Wiring — `extension.ts`

- `activate` calls `registerConnectionFeatures({ getWorker, secrets: context.secrets, log })`, pushes the returned `Disposable` onto `context.subscriptions`.
- After the first `getWorker()` resolves (in `registerConnectionFeatures` and in `controller` / `rendererHost` alike — whichever runs first), `registerSecretBridge({ client, secrets: context.secrets })` is called once on that client. Simplest placement: `getWorker` wraps `getWorkerSession` and, on the session's first resolution, calls `registerSecretBridge`. `getWorkerSession` already memoises, so a `sessionRegisteredBridge` guard in `extension.ts` is enough.
- No change to `deactivate` beyond what `disposeWorkerSession` already does.

## Error handling and lifecycle

| Situation | Handling |
|---|---|
| Connections view opened, no worker session | `getWorker()` resolves (spawns) the session; if spawn fails (`dotnet` missing, etc.) the tree shows one error node with the `WorkerUnavailableError.userMessage`; `viewsWelcome` still offers Add, which surfaces the same error via `showErrorMessage`. |
| `connection/list` returns `worker/crashed` | Tree shows an error node; the next `refresh()` respawns and retries. |
| `connection/schema` → `connection/schemaFailed` / `connection/unknown` | The connection node gets a single child error node (`$(warning) <message>`); no throw, siblings unaffected. |
| `secret/get` not answered by the host | `BridgeSecretStore.TryRead` times out at 5 s → `None` → `{password}` left unsubstituted → the connector fails at connect time with its own error, shown in cell output. |
| `secretReference` points at a deleted secret | `context.secrets.get` → `undefined` → `{ secret: null }` → `None` → same as above. |
| Host-request handler throws | `WorkerClient` replies `{ error: { code: "host/threw" } }`; `BridgeSecretStore` maps any error frame to `None`. |
| Remove confirmation dismissed | No-op; nothing sent. |
| `connection/register` fails after `secrets.store` | The secret is orphaned in `SecretStorage` (unreferenced, harmless); the error is shown; the tree is not refreshed. |
| Worker process respawns mid-session | `registerSecretBridge` set the handler on the persistent `WorkerClient`, not the process — it survives. Any in-flight `host-` request rejects via the existing `onExit` crash path → `TryRead` → `None`. |

## Testing

### Unit — `vitest`, `editors/vscode/src/**/*.test.ts`

- `connections/definition.test.ts` — `buildConnectionDefinition` for each of the six kinds (config shape); secret minting when a password is supplied; edit keeps `id` / `isTrusted` / existing `secretReference`; blank password on edit → `secret: undefined`; new password on a previously-secretless connection → fresh reference.
- `connections/schema-cache.test.ts` — get miss / set / get hit / clear.
- `connections/tree.test.ts` — against a fake `getWorker`: roots from `connection/list`; expand → `connection/schema` once, second expand served from cache; `refresh` after `clear` re-fetches; `connection/schemaFailed` → one error child; `worker/crashed` on list → error node.
- `connections/add.test.ts` — a scripted fake `{ showQuickPick, showInputBox, showWarningMessage }`: full add path calls `secrets.store` before `connection/register` with the right args; edit path with blank password does not call `secrets.store`; remove path confirms then calls `connection/remove` then `secrets.delete`; cancelled remove sends nothing.
- `secrets/bridge.test.ts` — `secret/get` hit → `{ secret }`; miss → `{ secret: null }`; unknown method → `host/unknownMethod` error; `secrets.get` throws → error frame, no throw out.
- `worker/client.test.ts` (append) — an inbound frame with `method` set → `onHostRequest` called, a `{ id, result }` frame written to stdin; no handler → `host/unhandled`; handler rejects → `host/threw`; a methodless frame with an unknown id → still ignored (unchanged).
- `protocol.test.ts` (append) — `readUnionTag` decodes a `{ "Case": "Sqlite" }` kind; `makeUnionTag("Sqlite")` encodes it back; an inbound frame with `method` set is recognised as a `HostRequestFrame` (guard).

### `test/vscode-stub.ts` additions

`window.createQuickPick` / `showQuickPick` / `showInputBox` / `showWarningMessage`; `TreeItem`, `TreeItemCollapsibleState`, `ThemeIcon`, `EventEmitter`; `commands.registerCommand` already present; a `SecretStorage` fake (`get` / `store` / `delete` over a `Map`).

### F# — `src/FSharpQueryStudio.Worker.Tests` (and `.Security.Tests` if the store lands there)

- `BridgeSecretStore` — `TryRead` returns the host's `secret` on a completed reply; `None` on a timeout (a `HostChannel` fake that never completes, short injected timeout); `None` on an error frame; `None` on a `null` result; `Save` / `Delete` raise `NotSupportedException`.
- `HostChannel` — `Request` mints a `host-`-prefixed id and writes one frame; `TryComplete` with a matching id resolves the awaiting task and returns `true`; with an unknown id returns `false`.
- `Program` loop — a methodless response frame whose id matches a pending `HostChannel` request is routed and not passed to `Handlers.dispatch` (spy dispatch); a normal request frame is still dispatched.
- `handleConnectionList` — returns `{ connections }` equal to `Workspace.readConnections` for a temp `workspaceDir` (empty list; one seeded connection).

### Integration — `editors/vscode/test/integration/`, real downloaded VS Code, CI-gated (worker-backed; pending/green without a bundled worker)

- `connections.test.ts` (+ a Sqlite fixture `.db`):
  - drive a scripted QuickPick to add a SQLite connection → assert it appears in the tree / `connection/list`.
  - expand it → assert real table + column nodes from `connection/schema`.
  - register a SqlServer-style connection with `Password={password}` + a stored secret, run a cell that touches `Connections.<name>` → assert the evaluation reaches the connector with the substituted credential (proves the reverse channel end-to-end); a happy-path assertion on the error text when the secret is absent.
  - remove the connection → assert it leaves `connection/list` and the secret is gone from the stub `SecretStorage`.

### Manual release checklist (not automated)

- The Activity Bar icon renders in both light and dark themes.
- Adding a `{password}` PostgreSQL connection, then running `Connections.sql "<name>" "select 1"` in a fresh notebook, returns a row without any secret appearing in `workspace.json` or the output channel.

## Known limitations (to carry in the DEVLOG close-out entry)

- Schema cache is per session and never invalidated except by the explicit **Refresh** command — a table added to the database mid-session is not shown until Refresh.
- No file picker for file-based connectors (free-text path only), matching the Avalonia dialog.
- `BridgeSecretStore.TryRead`'s 5 s blocking wait ties up the evaluation thread for that window if the host is unresponsive; the evaluation cannot be cancelled during the wait (the wait is inside a synchronous `ISecretStore` call).
- A single global `workspace.json` — no per-notebook or per-folder connection scoping.
- `secret/set` / `secret/delete` are not bridged; a hypothetical future worker path that writes a secret would hit `NotSupportedException`.
- The reverse channel carries exactly one method; it is not a general host-service RPC surface yet.

## Rollout

Implementation proceeds via a task-by-task plan (`docs/superpowers/plans/2026-09-07-vscode-extension-f5-connections.md`, written next by the `superpowers:writing-plans` skill). Each task: failing test first, implement, full unit suite, one `## 2026-09-07 — F5.N: <title>` DEVLOG line, commit. F5-complete close-out entry with the verification counts and the CI merge gate (the worker-backed `connections.test.ts` must be green in the `vscode-extension` workflow before merge). On completion, update the migration design's Status line and the **F5 Connections** rollout row.
