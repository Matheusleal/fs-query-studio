# VS Code Extension F6 — Parity + Retire — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove the VS Code extension has reached parity with the retired Avalonia app, remove the Avalonia tier from the repo, and make the VSIX publish-ready (short of an actual Marketplace publish).

**Architecture:** Verify first (walk a parity checklist against `main-vscode` as it stands), then demolish (delete `App`/`Ui`/`Editor` + their test projects + `WindowsCredentialStore.fs` + `installer/`, surgically strip the Avalonia-dependent files out of `Integration.Tests`, clean `.slnx` and `ci.yml`), then package (MIT `LICENSE`, `package.json` Marketplace metadata, `.vscodeignore`, README, a release runbook, and a CI gate that inspects the VSIX contents). Each task is one commit with a DEVLOG entry; a final close-out flips the migration spec's status.

**Tech Stack:** F# / .NET 10 (`dotnet build`, `dotnet test`, xUnit), TypeScript / Node 20 (`vitest`, `@vscode/test-electron`), `@vscode/vsce` for packaging, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-09-07-vscode-extension-f6-parity-retire-design.md`

## Global Constraints

- **No `vsce publish`.** No publisher account, PAT, or CI publish secret this phase. Publish-ready plumbing only.
- **No merge of `main-vscode` into `main`.** All F6 work lands on `main-vscode`.
- **No extension feature work.** No new commands, providers, renderer changes, or worker RPCs.
- **The only `src/` code edit is `Handlers.fs`** (default `secretStore` value + its doc comment) plus removing one `<Compile>` line from `FSharpQueryStudio.Security.fsproj`. `Protocol` wire types, the NDJSON transport, FSI execution, and every reused library's behaviour stay byte-for-byte unchanged.
- **License:** MIT, `Copyright (c) 2026 Matheus Leal`.
- **`InMemorySecretStore` is the new module default** in `Handlers.fs` — never a new native store. The cross-platform path is the F5 `BridgeSecretStore`, untouched.
- **Commit after every task; add a DEVLOG entry in the same commit.** One final `2026-09-07 — F6 complete` entry closes the phase.
- **Verification gates are real:** paste the actual command output into the task's checkbox before ticking it. `dotnet test` filter is always `--filter "Category!=RequiresDocker&Category!=RequiresPublish"` on Windows; the Linux CI matrix runs per-project.

---

### Task 1: Parity checklist — walked against `main-vscode`

**Files:**
- Create: `docs/f6-parity-checklist.md`

**Interfaces:**
- Consumes: nothing (first task).
- Produces: `docs/f6-parity-checklist.md` — the committed F6 parity record. Later tasks do not depend on it.

This task changes no build files. It writes the checklist, then verifies each row by running the cited test or performing the manual check, recording real evidence.

- [ ] **Step 1: Create the checklist file**

Create `docs/f6-parity-checklist.md` with exactly this content:

```markdown
# F6 Parity Checklist — VS Code extension vs. the retired Avalonia app

Walked against `main-vscode` on 2026-09-07, before any Avalonia deletion.
Each criterion is from the migration design's F6 parity list
(`docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md`).

| # | Criterion | Kind | Evidence | Pass |
|---|---|---|---|---|
| 1 | Write and run a query | automated | `editors/vscode/test/integration/notebook.test.ts` — opens `sample.fsqnb`, executes a code cell, asserts cell output. | ☐ |
| 2 | All six connectors (Sqlite, Csv, Excel, SqlServer, PostgreSql, RestJson) | automated | Worker: `tests/FSharpQueryStudio.Connectors.Tests` (all kinds) + `tests/FSharpQueryStudio.Integration.Tests/{SqlServerConnectorTests,PostgreSqlConnectorTests}.fs` (Testcontainers, `RequiresDocker`). Host: `editors/vscode/test/integration/connections.test.ts` registers a SQLite and a PostgreSQL connection end to end. | ☐ |
| 3 | CSV export | automated | `editors/vscode/test/integration/renderer.test.ts` + the `results/exportCsv` path exercised by the renderer host; worker `tests/FSharpQueryStudio.Results.Tests` CSV projection. | ☐ |
| 4 | Connection-name autocomplete | automated | `editors/vscode/test/integration/intelligence.test.ts` — completion in a cell offers a symbol defined in a sibling executed cell; `analysis/complete` reaches the worker with `Connections`/`Results` in scope. | ☐ |
| 5 | Schema navigation | automated + manual | `editors/vscode/test/integration/connections.test.ts` asserts `connection/schema` returns the fixture's real table + column. Manual: expand a connection in the Connections TreeView, see tables → columns. | ☐ |
| 6 | Secrets kept out of files | automated | `connections.test.ts` — a `{password}` connection resolves through the reverse `secret/get` bridge; `connection/register` receives only the `SecretReference`; `workspace.json` on disk contains no raw secret. | ☐ |
| 7 | Session persistence | automated | `editors/vscode/test/integration/notebook.test.ts` — a `let` bound in one cell is visible in a later cell (Persistent mode); `fs-query-studio.restartKernel` clears it. | ☐ |
| 8 | Log viewer → OutputChannel + Serilog file sink | manual (accepted downgrade) | The extension registers "F# Query Studio" and "F# Query Studio (Worker)" output channels (`src/log/channel.ts`); the worker's Serilog file sink still writes under `%LOCALAPPDATA%/FSharpQueryStudio` (14-day retention). No in-app log viewer, per the migration spec. | ☐ |

## Findings

_(Record here any criterion with no automated coverage and no cheap manual path.)_
```

- [ ] **Step 2: Verify rows 1–7 by running the cited automated suites**

From `editors/vscode/`:

```bash
dotnet publish ../../src/FSharpQueryStudio.Worker/FSharpQueryStudio.Worker.fsproj -c Release -o dist/worker
npm ci
npm run compile
npm test
npm run pretest:vscode
npm run test:vscode
```

Expected: `npm test` all green (~200 passing / 1 skipped per the F5 DEVLOG); `npm run test:vscode` — the `notebook`, `renderer`, `intelligence`, `connections` specs pass (they are pending only without a bundled worker, which Step 1's `dotnet publish` provides).

From the repo root:

```bash
dotnet test tests/FSharpQueryStudio.Connectors.Tests --configuration Release
dotnet test tests/FSharpQueryStudio.Results.Tests --configuration Release
```

Expected: green.

Tick each row's checkbox in `docs/f6-parity-checklist.md`, replacing `☐` with `☑` and appending ` — verified <date>` to the Evidence cell where a run confirmed it. If any spec fails, that is a real parity gap: record it under `## Findings` and stop — do not proceed to Task 2.

- [ ] **Step 3: Verify row 8 (manual)**

Confirm `editors/vscode/src/log/channel.ts` exists and registers both output channels; confirm the worker still configures a Serilog file sink (grep `src/FSharpQueryStudio.Logging` / `src/FSharpQueryStudio.Worker` for the file sink). Mark row 8 `☑` with a one-line note.

- [ ] **Step 4: Commit**

```bash
git add docs/f6-parity-checklist.md
git commit -m "$(cat <<'EOF'
docs(vscode): F6 parity checklist walked against main-vscode

Eight migration-spec parity criteria, each verified by a cited automated
suite or a recorded manual check, before the Avalonia tier is removed.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8
EOF
)"
```

---

### Task 2: Strip Avalonia-dependent files out of `Integration.Tests`

**Files:**
- Delete: `tests/FSharpQueryStudio.Integration.Tests/WorkerClientTests.fs`
- Delete: `tests/FSharpQueryStudio.Integration.Tests/WorkerClientLoggingTests.fs`
- Delete: `tests/FSharpQueryStudio.Integration.Tests/ExecutionServiceTests.fs`
- Delete: `tests/FSharpQueryStudio.Integration.Tests/MilestoneAAcceptanceTests.fs`
- Delete: `tests/FSharpQueryStudio.Integration.Tests/MilestoneBAcceptanceTests.fs`
- Delete: `tests/FSharpQueryStudio.Integration.Tests/MilestoneCAcceptanceTests.fs`
- Delete: `tests/FSharpQueryStudio.Integration.Tests/MilestoneDAcceptanceTests.fs`
- Delete: `tests/FSharpQueryStudio.Integration.Tests/MilestoneEAcceptanceTests.fs`
- Delete: `tests/FSharpQueryStudio.Integration.Tests/PublishedAppTests.fs`
- Delete: `tests/FSharpQueryStudio.Integration.Tests/WindowsCredentialStoreTests.fs`
- Modify: `tests/FSharpQueryStudio.Integration.Tests/TestHelpers.fs`
- Modify: `tests/FSharpQueryStudio.Integration.Tests/FSharpQueryStudio.Integration.Tests.fsproj`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: an `Integration.Tests` project that no longer references `FSharpQueryStudio.App` or `FSharpQueryStudio.Ui`. Keeper files after this task: `AssemblyInfo.fs`, `TestHelpers.fs` (trimmed), `Tests.fs`, `NotebookExecutionTests.fs`, `SqlServerConnectorTests.fs`, `PostgreSqlConnectorTests.fs`, `ConnectionsRegressionTests.fs`, `PackageApprovalTests.fs`. `TestHelpers` still exports `workerDllPath : unit -> string`, `withTimeout : float -> Task<'T> -> Task<'T>`, `request : string -> string -> string -> WorkerRequestFrame`.

This task runs while `App`/`Ui`/`Editor` still exist in the tree, so the only change to the build graph is `Integration.Tests` dropping two project references. Task 3 then deletes the projects themselves.

- [ ] **Step 1: Baseline — confirm the suite builds today**

```bash
dotnet build tests/FSharpQueryStudio.Integration.Tests --configuration Release
```
Expected: PASS (baseline before edits).

- [ ] **Step 2: Delete the ten Avalonia-dependent / obsolete test files**

```bash
git rm tests/FSharpQueryStudio.Integration.Tests/WorkerClientTests.fs \
       tests/FSharpQueryStudio.Integration.Tests/WorkerClientLoggingTests.fs \
       tests/FSharpQueryStudio.Integration.Tests/ExecutionServiceTests.fs \
       tests/FSharpQueryStudio.Integration.Tests/MilestoneAAcceptanceTests.fs \
       tests/FSharpQueryStudio.Integration.Tests/MilestoneBAcceptanceTests.fs \
       tests/FSharpQueryStudio.Integration.Tests/MilestoneCAcceptanceTests.fs \
       tests/FSharpQueryStudio.Integration.Tests/MilestoneDAcceptanceTests.fs \
       tests/FSharpQueryStudio.Integration.Tests/MilestoneEAcceptanceTests.fs \
       tests/FSharpQueryStudio.Integration.Tests/PublishedAppTests.fs \
       tests/FSharpQueryStudio.Integration.Tests/WindowsCredentialStoreTests.fs
```

Rationale per file: `WorkerClient*` test `App/WorkerClient.fs` (retired); `ExecutionServiceTests` tests `App/ExecutionService.fs`; `Milestone{A..E}AcceptanceTests` drive `App`/`Ui` view-models; `PublishedAppTests` publishes and launches `FSharpQueryStudio.App.exe` (`RequiresPublish`); `WindowsCredentialStoreTests` tests the store being deleted in Task 3.

- [ ] **Step 3: Trim `TestHelpers.fs` to its Avalonia-free core**

Replace the entire contents of `tests/FSharpQueryStudio.Integration.Tests/TestHelpers.fs` with:

```fsharp
module TestHelpers

open System
open System.IO
open System.Threading.Tasks
open FSharpQueryStudio.Protocol

let workerDllPath () =
    Path.Combine(AppContext.BaseDirectory, "FSharpQueryStudio.Worker.dll")

let withTimeout (seconds: float) (work: Task<'T>) : Task<'T> =
    task {
        let! winner = Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds seconds))

        if winner = (work :> Task) then
            return! work
        else
            return failwith $"timed out after {seconds}s waiting for the worker process"
    }

let request (id: string) (method_: string) (paramsJson: string) : WorkerRequestFrame =
    { Id = id
      Method = method_
      Params = Text.Json.JsonDocument.Parse(paramsJson).RootElement }
```

This drops `open FSharpQueryStudio.App`, `open FSharpQueryStudio.Ui`, and the `wireShell` helper (used only by the deleted `ExecutionServiceTests` / `MilestoneA` / `MilestoneE`). `workerDllPath` is used by `Tests.fs`; `withTimeout` by `Tests.fs` and `NotebookExecutionTests.fs`; `request` is kept as a generic helper.

- [ ] **Step 4: Edit `FSharpQueryStudio.Integration.Tests.fsproj`**

Remove these ten `<Compile Include=... />` lines (the deleted files):
`WorkerClientTests.fs`, `WorkerClientLoggingTests.fs`, `ExecutionServiceTests.fs`, `MilestoneAAcceptanceTests.fs`, `MilestoneBAcceptanceTests.fs`, `MilestoneCAcceptanceTests.fs`, `MilestoneDAcceptanceTests.fs`, `MilestoneEAcceptanceTests.fs`, `PublishedAppTests.fs`, `WindowsCredentialStoreTests.fs`.

The `<ItemGroup>` should end up:

```xml
  <ItemGroup>
    <Compile Include="AssemblyInfo.fs" />
    <Compile Include="TestHelpers.fs" />
    <Compile Include="Tests.fs" />
    <Compile Include="SqlServerConnectorTests.fs" />
    <Compile Include="PostgreSqlConnectorTests.fs" />
    <Compile Include="ConnectionsRegressionTests.fs" />
    <Compile Include="PackageApprovalTests.fs" />
    <Compile Include="NotebookExecutionTests.fs" />
  </ItemGroup>
```

Remove these two `<ProjectReference>` lines:

```xml
    <ProjectReference Include="..\..\src\FSharpQueryStudio.App\FSharpQueryStudio.App.fsproj" />
    <ProjectReference Include="..\..\src\FSharpQueryStudio.Ui\FSharpQueryStudio.Ui.fsproj" />
```

Leave the `Worker`, `Protocol`, `Security`, `Connectors`, `Storage` project references and every `<PackageReference>` as-is. (`SSH.NET` is now unreferenced by any source file but an unused `PackageReference` produces no warning — leave it; a follow-up cleanup can drop it.)

- [ ] **Step 5: Build and test the pruned suite**

```bash
dotnet build tests/FSharpQueryStudio.Integration.Tests --configuration Release
dotnet test tests/FSharpQueryStudio.Integration.Tests --configuration Release --filter "Category!=RequiresDocker&Category!=RequiresPublish"
```
Expected: build PASS; tests green (the remaining non-Docker tests: `Tests.fs` worker-stdio smoke, `NotebookExecutionTests.fs`, `PackageApprovalTests.fs`). Record the passing count.

- [ ] **Step 6: Full-solution sanity (Avalonia still present)**

```bash
dotnet build FSharpQueryStudio.slnx --configuration Release
```
Expected: PASS — nothing else referenced the deleted files.

- [ ] **Step 7: Commit + DEVLOG**

Add a DEVLOG entry (top of `docs/DEVLOG.md`, below the title):

```markdown
## 2026-09-07 — F6.1: Integration.Tests loses its Avalonia coupling

`tests/FSharpQueryStudio.Integration.Tests` no longer references `App` or
`Ui`. Deleted the ten files that drove the Avalonia tier: `WorkerClientTests`
/ `WorkerClientLoggingTests` (tested `App/WorkerClient.fs`),
`ExecutionServiceTests`, `Milestone{A..E}AcceptanceTests` (view-model
acceptance), `PublishedAppTests` (`RequiresPublish` — launched
`FSharpQueryStudio.App.exe`), and `WindowsCredentialStoreTests` (store
removed in F6.2). `TestHelpers.fs` trimmed to `workerDllPath` / `withTimeout`
/ `request` — `wireShell` and the `App`/`Ui` opens are gone. Kept: the
worker-stdio smoke (`Tests.fs`), `NotebookExecutionTests`, the Testcontainers
SQL Server / PostgreSQL connector suites, `ConnectionsRegressionTests`,
`PackageApprovalTests`. No production code touched.
```

```bash
git add tests/FSharpQueryStudio.Integration.Tests docs/DEVLOG.md
git commit -m "$(cat <<'EOF'
test: decouple Integration.Tests from the Avalonia App/Ui projects

Delete the ten App/Ui-bound test files and trim TestHelpers.fs to its
worker-only core, so the suite builds without FSharpQueryStudio.App /
FSharpQueryStudio.Ui. Those projects are removed in the next task.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8
EOF
)"
```

---

### Task 3: Delete `App` / `Ui` / `Editor` + their test projects + `WindowsCredentialStore`

**Files:**
- Delete: `src/FSharpQueryStudio.App/` (whole directory)
- Delete: `src/FSharpQueryStudio.Ui/` (whole directory)
- Delete: `src/FSharpQueryStudio.Editor/` (whole directory)
- Delete: `tests/FSharpQueryStudio.Editor.Tests/` (whole directory)
- Delete: `tests/FSharpQueryStudio.Ui.Tests/` (whole directory)
- Delete: `src/FSharpQueryStudio.Security/WindowsCredentialStore.fs`
- Modify: `src/FSharpQueryStudio.Security/FSharpQueryStudio.Security.fsproj`
- Modify: `src/FSharpQueryStudio.Worker/Handlers.fs:73-80`
- Modify: `tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs:1236`
- Modify: `FSharpQueryStudio.slnx`

**Interfaces:**
- Consumes: the App/Ui-free `Integration.Tests` from Task 2.
- Produces: a solution with 9 src projects + 10 test projects, no Avalonia, no `WindowsCredentialStore`. `Handlers` module default `secretStore` is `InMemorySecretStore()`.

- [ ] **Step 1: Delete the five projects**

```bash
git rm -r src/FSharpQueryStudio.App \
          src/FSharpQueryStudio.Ui \
          src/FSharpQueryStudio.Editor \
          tests/FSharpQueryStudio.Editor.Tests \
          tests/FSharpQueryStudio.Ui.Tests
```

- [ ] **Step 2: Delete `WindowsCredentialStore.fs` and drop it from the `Security` project**

```bash
git rm src/FSharpQueryStudio.Security/WindowsCredentialStore.fs
```

In `src/FSharpQueryStudio.Security/FSharpQueryStudio.Security.fsproj`, remove the line:

```xml
    <Compile Include="WindowsCredentialStore.fs" />
```

The `<ItemGroup>` becomes `ISecretStore.fs`, `InMemorySecretStore.fs`, `OriginDetection.fs`.

- [ ] **Step 3: Change the `Handlers.fs` default secret store**

In `src/FSharpQueryStudio.Worker/Handlers.fs`, replace lines 73–80 (the doc comment + the `let mutable private secretStore` binding) with:

```fsharp
    /// The secret store the worker's connection resolver reads `{password}`
    /// tokens from. The default `InMemorySecretStore` serves a standalone
    /// `dotnet run` of the worker and any test that never calls
    /// `configureSecretStore`; being empty, it resolves nothing, so a
    /// `{password}` connection simply fails as a missing secret. `Program.fs`
    /// swaps in a `BridgeSecretStore` at startup so the extension host's
    /// credential store (VS Code `SecretStorage`, cross-platform) is the
    /// source of truth. One process-wide store — the worker handles one
    /// evaluation at a time.
    let mutable private secretStore: ISecretStore = InMemorySecretStore()
```

`InMemorySecretStore` is in the same `FSharpQueryStudio.Security` namespace already `open`ed in this file, so no new `open` is needed. `configureSecretStore` on the next line is unchanged.

- [ ] **Step 4: Fix the one `HandlersTests.fs` reference**

In `tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs` line ~1236 (the `finally` block of `` `connection/schema reads {password} from the store configured via configureSecretStore` ``), change:

```fsharp
            Handlers.configureSecretStore (FSharpQueryStudio.Security.WindowsCredentialStore())
```
to:
```fsharp
            Handlers.configureSecretStore (FSharpQueryStudio.Security.InMemorySecretStore())
```

The test body uses an inline spy `ISecretStore`; only this reset line referenced the deleted type. The assertion (`askedFor = Some "fqs-conn-seam"`, `schemaFailed`) is unaffected — it checks the injected spy was consulted, not any Windows behaviour.

- [ ] **Step 5: Remove the five projects from `FSharpQueryStudio.slnx`**

Delete these `<Project>` lines:

```xml
    <Project Path="src/FSharpQueryStudio.App/FSharpQueryStudio.App.fsproj" />
    <Project Path="src/FSharpQueryStudio.Editor/FSharpQueryStudio.Editor.fsproj" />
    <Project Path="src/FSharpQueryStudio.Ui/FSharpQueryStudio.Ui.fsproj" />
    <Project Path="tests/FSharpQueryStudio.Editor.Tests/FSharpQueryStudio.Editor.Tests.fsproj" />
    <Project Path="tests/FSharpQueryStudio.Ui.Tests/FSharpQueryStudio.Ui.Tests.fsproj" />
```

`src/` folder keeps 9 projects; `tests/` folder keeps 10.

- [ ] **Step 6: Grep for dangling references**

```bash
grep -rn "FSharpQueryStudio\.\(App\|Ui\|Editor\)\|WindowsCredentialStore" \
  --include=*.fs --include=*.fsproj --include=*.slnx --include=*.props --include=*.targets \
  src/ tests/ *.slnx Directory.Build.*
```
Expected: **no matches** (bin/obj excluded by `--include`). If anything appears, resolve it before continuing.

- [ ] **Step 7: Build and test the whole solution**

```bash
dotnet build FSharpQueryStudio.slnx --configuration Release
dotnet test FSharpQueryStudio.slnx --no-build --configuration Release --filter "Category!=RequiresDocker&Category!=RequiresPublish"
```
Expected: build clean; all tests green. Record the count and note the drop vs. the pre-F6 baseline (the two removed test projects + the ten removed `Integration.Tests` files).

- [ ] **Step 8: Delete now-empty `bin`/`obj` leftovers (optional, not committed)**

```bash
git status --porcelain   # confirm only intended deletions + the 4 edits are staged
```

- [ ] **Step 9: Commit + DEVLOG**

DEVLOG entry:

```markdown
## 2026-09-07 — F6.2: Avalonia tier deleted

`git rm` of `src/FSharpQueryStudio.{App,Ui,Editor}`,
`tests/FSharpQueryStudio.{Ui,Editor}.Tests`, and
`src/FSharpQueryStudio.Security/WindowsCredentialStore.fs`.
`FSharpQueryStudio.slnx` drops the five projects (now 9 src + 10 test).
`Handlers.fs`'s module-default `secretStore` is now `InMemorySecretStore()`
(was `WindowsCredentialStore()`) — only reached by a standalone `dotnet run`
or a test that never calls `configureSecretStore`; `Program.fs` still
composes `BridgeSecretStore` for the extension host, so secret resolution is
unchanged there. One `HandlersTests.fs` reset line swapped to
`InMemorySecretStore()`. `Security` keeps `ISecretStore` /
`InMemorySecretStore` / `OriginDetection`. Build + non-Docker test suite
green (<count> passing).
```

```bash
git add -A
git commit -m "$(cat <<'EOF'
chore: delete the Avalonia app, UI, editor, and Windows credential store

Removes src/FSharpQueryStudio.{App,Ui,Editor}, their two test projects, and
Security/WindowsCredentialStore.fs. Handlers.fs's default secret store
becomes InMemorySecretStore; Program.fs still composes BridgeSecretStore on
the extension path. .slnx trimmed to 9 src + 10 test projects.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8
EOF
)"
```

---

### Task 4: Delete `installer/` and add retirement banners to the planning docs

**Files:**
- Delete: `installer/` (whole directory)
- Modify: `docs/fs-studio-plan.md` (top blockquote)
- Modify: `docs/fs-studio-development-plan.md` (add a top blockquote)

**Interfaces:**
- Consumes / Produces: nothing other tasks depend on.

- [ ] **Step 1: Confirm nothing references the installer**

```bash
grep -rn "installer\|Installer\|wixproj\|\.wxs" \
  --include=*.yml --include=*.yaml --include=*.slnx --include=*.props --include=*.targets --include=*.md \
  .github/ *.slnx Directory.Build.* docs/
```
Expected: matches only inside `docs/` prose (if any) — not in workflows, `.slnx`, or MSBuild files. (`FSharpQueryStudio.slnx` does not list the installer; `.github/workflows/*` do not mention it.)

- [ ] **Step 2: Delete the installer project**

```bash
git rm -r installer
```

- [ ] **Step 3: Update the `fs-studio-plan.md` banner**

`docs/fs-studio-plan.md` already opens with a migration blockquote. Replace that existing blockquote (the `> **Delivery is migrating to a VS Code extension.** ...` paragraph, however many lines it spans) with:

```markdown
> **Superseded (2026-09).** The Avalonia desktop host described here was
> retired in phase **F6** of the VS Code extension migration. F# Query Studio
> now ships as a VS Code extension under `editors/vscode/`, reusing the F#
> worker and every `src/` library except the deleted `App`/`Ui`/`Editor`.
> This document is kept for historical context — architecture, protocol
> shapes, milestone history. See
> `docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md`
> and `docs/DEVLOG.md` for the current system.
```

- [ ] **Step 4: Add a banner to `fs-studio-development-plan.md`**

Insert, immediately after the `# F# Query Studio — Development Plan` title line and before the next paragraph:

```markdown

> **Superseded (2026-09).** This milestone task list covered the Avalonia
> desktop build, retired in phase **F6** of the VS Code extension migration.
> Kept for historical context. Current work is tracked in `docs/DEVLOG.md`
> and the `docs/superpowers/` specs and plans.
```

- [ ] **Step 5: Verify the `.slnx` docs folder still resolves**

The `/docs/` solution folder in `FSharpQueryStudio.slnx` lists `DEVLOG.md`, `fs-studio-development-plan.md`, `fs-studio-plan.md` — all still present. No `.slnx` edit needed. Confirm:

```bash
dotnet build FSharpQueryStudio.slnx --configuration Release
```
Expected: PASS (unchanged; docs-folder entries are not build inputs).

- [ ] **Step 6: Commit + DEVLOG**

DEVLOG entry:

```markdown
## 2026-09-07 — F6.3: installer removed, planning docs marked superseded

`git rm -r installer/` (the WiX MSI project — it only packaged
`FSharpQueryStudio.App.exe`, now gone; nothing in CI or `.slnx` referenced
it). `docs/fs-studio-plan.md` and `docs/fs-studio-development-plan.md` gain a
"Superseded (2026-09) — Avalonia host retired at F6" banner; their bodies
are untouched.
```

```bash
git add -A
git commit -m "$(cat <<'EOF'
chore: remove the WiX installer and mark the Avalonia planning docs superseded

The MSI packaged FSharpQueryStudio.App.exe, deleted in the previous task.
The VSIX is now the only distribution artifact. Planning docs keep their
history behind a superseded banner.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8
EOF
)"
```

---

### Task 5: Prune `ci.yml` — drop the Ui/Editor matrix rows and stale Avalonia comments

**Files:**
- Modify: `.github/workflows/ci.yml`

**Interfaces:**
- Consumes / Produces: nothing other tasks depend on. The `build-and-test` (Windows) job builds `FSharpQueryStudio.slnx` and needs no change beyond passing.

- [ ] **Step 1: Remove the two deleted test projects from the Linux matrix**

In `.github/workflows/ci.yml`, `build-and-test-linux` job, delete these two `matrix.project` entries:

```yaml
          - tests/FSharpQueryStudio.Ui.Tests/FSharpQueryStudio.Ui.Tests.fsproj
          - tests/FSharpQueryStudio.Editor.Tests/FSharpQueryStudio.Editor.Tests.fsproj
```

The matrix keeps: Protocol, Fsac, Execution, Results, Connectors, Storage, Security, Worker, Integration test projects.

- [ ] **Step 2: Replace the stale D.8 / E.9 comment block**

In the same job, replace the long comment block (the lines from `# D.8: Section 14's ...` through `# ... in the same container.` that sit between `runs-on: ubuntu-latest` and `strategy:`) with:

```yaml
    # Runs the reused libraries + the F# worker on Linux, each project
    # individually so one failure doesn't mask the others. The VS Code
    # extension itself is built and tested cross-OS in vscode-extension.yml.
    # Docker-backed SQL Server / PostgreSQL tests are NOT excluded here:
    # ubuntu-latest has native Linux container support.
```

Leave the `build-and-test` (Windows) job's comment about `RequiresDocker` / `RequiresPublish` as-is — that filter is still in use.

- [ ] **Step 3: Lint the workflow**

```bash
python -c "import yaml,sys; yaml.safe_load(open('.github/workflows/ci.yml')); print('ok')"
```
Expected: `ok`. (No local runner executes the workflow; the real check is the next push to `main` — out of scope for F6, which stays on `main-vscode`. `vscode-extension.yml` already fires on `main-vscode` and covers the extension.)

- [ ] **Step 4: Commit + DEVLOG**

DEVLOG entry:

```markdown
## 2026-09-07 — F6.4: ci.yml drops the retired Avalonia test projects

`.github/workflows/ci.yml` Linux matrix loses the `Ui.Tests` and
`Editor.Tests` entries (projects deleted in F6.2); the D.8/E.9 comment block
about Avalonia `NativeWebView` / Monaco / WebKitGTK is replaced by a
three-line note. The Windows job is unchanged — it builds the now-smaller
`.slnx`.
```

```bash
git add .github/workflows/ci.yml docs/DEVLOG.md
git commit -m "$(cat <<'EOF'
ci: drop the retired Ui.Tests/Editor.Tests projects from the Linux matrix

Also trims the stale Avalonia/Monaco/WebView commentary in that job to a
short note pointing at vscode-extension.yml for the extension build.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8
EOF
)"
```

---

### Task 6: Publish-ready metadata — LICENSE, `package.json`, `.vscodeignore`, icon

**Files:**
- Create: `LICENSE` (repo root)
- Create: `editors/vscode/LICENSE`
- Create: `editors/vscode/.vscodeignore`
- Create (best-effort): `editors/vscode/media/icon.png`
- Modify: `editors/vscode/package.json`

**Interfaces:**
- Consumes: the pruned tree from Tasks 2–5.
- Produces: an `editors/vscode/` folder that `vsce package` processes with zero warnings. Task 8 asserts the VSIX contents in CI.

- [ ] **Step 1: Create the root `LICENSE` (MIT)**

Create `LICENSE` at the repo root with the standard MIT text:

```
MIT License

Copyright (c) 2026 Matheus Leal

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

- [ ] **Step 2: Copy it into the extension package root**

```bash
cp LICENSE editors/vscode/LICENSE
```

`vsce` looks for `LICENSE` in the package root (`editors/vscode/`), not the repo root; without this it prints `WARNING: LICENSE ... not found`. The two files are identical static MIT text — a one-line note in the release runbook (Task 7) records that they must stay in sync.

- [ ] **Step 3: Create `editors/vscode/.vscodeignore`**

```
.vscode/**
.vscode-test/**
.vscode-test.mjs
src/**
test/**
out/**
node_modules/**
tsconfig.json
tsconfig.integration.json
esbuild.mjs
vitest.config.ts
**/*.map
**/*.test.ts
**/tsconfig.*
.gitignore
```

What ships: `dist/**` (bundled `extension.js` + the published `dist/worker/**`), `media/**`, `package.json`, `package-lock.json`, `README.md`, `LICENSE`, `CHANGELOG.md` if present.

- [ ] **Step 4: Update `editors/vscode/package.json`**

Apply these changes to the top-level object:

- `"version": "0.0.1"` → `"version": "0.1.0"`
- Add `"license": "MIT"` (next to `"description"`).
- Add:
  ```json
  "icon": "media/icon.png",
  ```
  **only if Step 5 actually produces the file.** If it does not, omit this key (a missing icon is not a `vsce` warning; a `package.json` `icon` pointing at a missing file is a hard `vsce` error).
- Replace `"categories": ["Other"]` with `"categories": ["Notebooks", "Data Science", "Programming Languages"]`.
- Add:
  ```json
  "keywords": ["fsharp", "f#", "sql", "query", "notebook", "database", "rest", "csv"],
  "bugs": { "url": "https://github.com/Matheusleal/fs-query-studio/issues" },
  "homepage": "https://github.com/Matheusleal/fs-query-studio/tree/main-vscode/editors/vscode#readme",
  "qna": "marketplace",
  "galleryBanner": { "color": "#4B267D", "theme": "dark" },
  ```
- In `"activationEvents"`, remove `"onCommand:fs-query-studio.checkWorker"` (VS Code ≥ 1.74 auto-generates command activation; `vsce` flags the redundancy). Keep `"onNotebook:fsquery-notebook"` and `"onView:fsQueryStudioConnections"`.
- Leave `"publisher": "fs-query-studio"` unchanged — the real publisher id is chosen at publish time (runbook, Task 7).

- [ ] **Step 5: Generate the icon (best-effort)**

Try, in order, from `editors/vscode/`:

```bash
# ImageMagick v7
magick -background "#4B267D" -density 384 media/activitybar.svg -resize 128x128 -gravity center -extent 128x128 media/icon.png
# or ImageMagick v6
convert -background "#4B267D" -density 384 media/activitybar.svg -resize 128x128 -gravity center -extent 128x128 media/icon.png
# or rsvg-convert
rsvg-convert -w 128 -h 128 -b "#4B267D" media/activitybar.svg -o media/icon.png
```

If one succeeds, verify it is a 128×128 PNG (`file media/icon.png`) and keep the `"icon"` key from Step 4. If none is available, skip the file, omit the `"icon"` key, and note in the runbook that a 128×128 PNG must be added before an actual Marketplace publish. **Do not block the task on the icon.**

- [ ] **Step 6: Compile and package locally**

From `editors/vscode/`:

```bash
dotnet publish ../../src/FSharpQueryStudio.Worker/FSharpQueryStudio.Worker.fsproj -c Release -o dist/worker
npm run compile
npx --yes @vscode/vsce package --out fs-query-studio.vsix
```
Expected: `vsce` prints **no line containing `WARNING`** and produces `fs-query-studio.vsix`. If a warning appears, fix its cause (do not suppress it) and re-run.

- [ ] **Step 7: Inspect the VSIX contents**

```bash
npx --yes @vscode/vsce ls
```
Expected listing **includes**: `package.json`, `README.md`, `LICENSE`, `dist/extension.js`, `dist/worker/FSharpQueryStudio.Worker.dll` (+ the rest of `dist/worker/`), `media/renderer/index.js`. Expected listing **excludes**: anything under `src/`, `test/`, `out/`, `node_modules/`, and any `*.test.ts` or `tsconfig*.json`.

Then delete the local artifact so it is not committed:

```bash
rm fs-query-studio.vsix
```

- [ ] **Step 8: Confirm `.gitignore` covers the artifact**

```bash
grep -q "fs-query-studio.vsix\|\*.vsix" editors/vscode/.gitignore || echo "*.vsix" >> editors/vscode/.gitignore
```

- [ ] **Step 9: Commit + DEVLOG**

DEVLOG entry:

```markdown
## 2026-09-07 — F6.5: VSIX publish metadata

Repo-root `LICENSE` (MIT) + a copy at `editors/vscode/LICENSE` (where `vsce`
looks). New `editors/vscode/.vscodeignore` ships only `dist/**` (bundle +
published worker), `media/**`, `package.json`, `README.md`, `LICENSE`.
`package.json`: `version` 0.1.0, `license` MIT, real `categories`
(Notebooks / Data Science / Programming Languages), `keywords`, `bugs`,
`homepage`, `qna`, `galleryBanner`; the redundant
`onCommand:` activation event dropped. Icon: <generated media/icon.png |
deferred — see release runbook>. `vsce package` runs warning-free; `vsce ls`
confirms no source or test files in the VSIX.
```

```bash
git add LICENSE editors/vscode/LICENSE editors/vscode/.vscodeignore editors/vscode/package.json editors/vscode/.gitignore docs/DEVLOG.md
git add editors/vscode/media/icon.png 2>/dev/null || true
git commit -m "$(cat <<'EOF'
chore(vscode): make the extension publish-ready

Add MIT LICENSE (root + package copy), a .vscodeignore that ships only
runtime assets, and full Marketplace metadata in package.json. vsce package
is warning-free and the VSIX carries no source or test files. No vsce
publish this phase.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8
EOF
)"
```

---

### Task 7: Rewrite the extension README and add the release runbook

**Files:**
- Modify: `editors/vscode/README.md`
- Create: `docs/vscode-extension-release.md`

**Interfaces:**
- Consumes: the metadata from Task 6.
- Produces: user-facing docs. Nothing depends on them.

- [ ] **Step 1: Replace `editors/vscode/README.md`**

Full new content:

```markdown
# F# Query Studio

Write, analyze, and run F# query scripts against SQL databases, local files, and REST APIs — as VS Code notebooks.

## Requirements

- **.NET 10 SDK** on your `PATH` (`dotnet --version` ≥ 10). The extension runs a bundled F# worker process with it.
- VS Code 1.90 or newer.

## Quick start

1. Create a file ending in `.fsqnb` and open it — it opens as an **F# Query Notebook**.
2. Type F# into a code cell and run it. `let` bindings persist to later cells in the same notebook (a persistent FSI session). **F# Query Studio: Restart Notebook Session** clears it.
3. Open the **F# Query Studio** view in the Activity Bar and **Add Connection** — SQLite / CSV / Excel (file path), SQL Server / PostgreSQL (connection string; use `{password}` where the secret goes), or REST JSON (URL). Credentials are stored in the OS keychain via VS Code `SecretStorage`, never written to `workspace.json` or the notebook.
4. Reference a connection from a cell (`Connections.sql "my-conn" "select ..."`), run it, and the result renders as a sortable grid with a schema tab and **Export CSV**.

## Features

- **Notebook execution** — each `.fsqnb` code cell runs against a persistent, per-notebook FSI session; stdout/stderr stream live; cancel a running cell from the toolbar.
- **Results grid** — virtualized table, schema view, CSV export, paged loading for large results.
- **In-cell F# intelligence** — completion, hover, signature help, and diagnostics, including connection-name completion inside `Connections.*`. Provided by this extension.
- **Connection management** — a tree of connections → tables → columns, lazily loaded; add / edit / remove via guided pickers.
- **Cross-platform** — Windows, macOS, Linux. One F# worker per VS Code window.

## Limitations

- In-cell intelligence is completion / hover / signature help / diagnostics only — no find-all-references, formatting, code actions, or cross-cell rename.
- One evaluation runs at a time, including across multiple open `.fsqnb` files in the same window.
- Requires `dotnet` on `PATH`; there is no self-contained build yet.

## Relationship to Ionide

[Ionide](https://ionide.io) handles F# in ordinary `.fs` / `.fsx` files. It never sees notebook cells, and this extension never touches `.fs` / `.fsx`. Install both; they do not conflict.

## Contributing / development

See [`CONTRIBUTING`-style notes in the repo](https://github.com/Matheusleal/fs-query-studio):

```bash
cd editors/vscode
npm install
dotnet publish ../../src/FSharpQueryStudio.Worker/FSharpQueryStudio.Worker.fsproj -c Release -o dist/worker
npm run compile      # type-check + esbuild bundle
npm test             # unit tests (vitest)
npm run test:vscode  # extension-host tests in a downloaded VS Code
```

Press F5 in VS Code to launch an Extension Development Host. Set `FSQS_WORKER_DLL` to use a worker built elsewhere instead of `dist/worker`.

Design: `docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md`. Release process: `docs/vscode-extension-release.md`.
```

- [ ] **Step 2: Create `docs/vscode-extension-release.md`**

```markdown
# Releasing the F# Query Studio VS Code extension

As of phase F6 the extension is **publish-ready but not published**. There is
no Marketplace publisher account, no PAT, and no CI publish secret. This
runbook is the manual process for the first and subsequent publishes.

## One-time setup

1. Create a Visual Studio Marketplace **publisher** at
   <https://marketplace.visualstudio.com/manage>. Note its id.
2. If it differs from `fs-query-studio`, update `"publisher"` in
   `editors/vscode/package.json`.
3. In the publisher's Azure DevOps organization, create a **Personal Access
   Token** with **Marketplace → Manage** scope. Keep it out of the repo.
4. Add a 128×128 PNG at `editors/vscode/media/icon.png` and the
   `"icon": "media/icon.png"` key to `package.json` if not already present
   (F6 left this optional).

## Per release

1. Bump `"version"` in `editors/vscode/package.json` (semver).
2. Keep `LICENSE` and `editors/vscode/LICENSE` identical (both MIT — only
   touch if the license itself changes).
3. Commit; tag: `git tag vscode-v<version> && git push --tags`.
4. Let `.github/workflows/vscode-extension.yml` run on the pushed commit.
   Download the `fs-query-studio-vsix` artifact from that run.
5. Publish the downloaded VSIX:
   ```bash
   VSCE_PAT=<token> npx --yes @vscode/vsce publish --packagePath fs-query-studio.vsix
   ```
6. Smoke test: install the Marketplace build in a clean VS Code, open a
   `.fsqnb`, add a connection, run a cell that returns a table.

## Notes

- The VSIX bundles the framework-dependent worker (`dist/worker/**`); the
  user still needs .NET 10 on `PATH`. A self-contained per-platform VSIX is
  F7 backlog.
- CI packages the VSIX on every `main` / `main-vscode` push touching the
  extension or the worker; it never publishes.
```

- [ ] **Step 3: Sanity-check links**

```bash
grep -n "](\./" editors/vscode/README.md || echo "no repo-relative links (good — Marketplace renders README standalone)"
```
Expected: `no repo-relative links ...`. Marketplace does not resolve relative paths; the README uses absolute GitHub URLs or plain text.

- [ ] **Step 4: Commit + DEVLOG**

DEVLOG entry:

```markdown
## 2026-09-07 — F6.6: extension README + release runbook

`editors/vscode/README.md` rewritten for end users: requirements, quick
start, feature list, the stated limitations, and the Ionide-coexistence
note (dev instructions kept at the bottom). New
`docs/vscode-extension-release.md` — the manual `vsce publish` process,
publisher/PAT setup, and the "F6 is publish-ready, not published" statement.
```

```bash
git add editors/vscode/README.md docs/vscode-extension-release.md docs/DEVLOG.md
git commit -m "$(cat <<'EOF'
docs(vscode): user-facing README + manual release runbook

README is now a Marketplace-facing overview (quick start, features,
limitations, Ionide coexistence); dev setup moves to the bottom.
docs/vscode-extension-release.md documents the manual publish flow.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8
EOF
)"
```

---

### Task 8: CI — assert VSIX contents in `vscode-extension.yml`

**Files:**
- Modify: `.github/workflows/vscode-extension.yml`

**Interfaces:**
- Consumes: the `.vscodeignore` + metadata from Task 6.
- Produces: a CI step that fails if the VSIX gains source/test files or loses a runtime asset.

- [ ] **Step 1: Add a content-assertion step after "Package VSIX"**

In `.github/workflows/vscode-extension.yml`, the `Package VSIX` step (ubuntu only) currently is:

```yaml
      - name: Package VSIX
        if: matrix.os == 'ubuntu-latest'
        working-directory: editors/vscode
        run: npx --yes @vscode/vsce package --out fs-query-studio.vsix
```

Immediately after it, add:

```yaml
      - name: Assert VSIX contents
        if: matrix.os == 'ubuntu-latest'
        working-directory: editors/vscode
        run: |
          set -euo pipefail
          listing="$(npx --yes @vscode/vsce ls)"
          echo "$listing"
          fail=0
          for required in package.json README.md LICENSE dist/extension.js dist/worker/FSharpQueryStudio.Worker.dll media/renderer/index.js; do
            if ! grep -qxF "$required" <<<"$listing"; then
              echo "::error::VSIX is missing required file: $required"; fail=1
            fi
          done
          for forbidden in 'src/' 'test/' 'out/' 'node_modules/'; do
            if grep -q "$forbidden" <<<"$listing"; then
              echo "::error::VSIX contains files under $forbidden"; fail=1
            fi
          done
          if grep -qE '\.test\.ts$|tsconfig.*\.json$' <<<"$listing"; then
            echo "::error::VSIX contains test or tsconfig files"; fail=1
          fi
          exit $fail
```

(`vsce ls` prints one repo-relative path per line. `grep -qxF` matches a whole line literally.)

- [ ] **Step 2: Make packaging warning-fatal**

Change the `Package VSIX` step's `run` to fail on any `vsce` warning:

```yaml
        run: |
          set -euo pipefail
          out="$(npx --yes @vscode/vsce package --out fs-query-studio.vsix 2>&1)"
          echo "$out"
          if grep -q 'WARNING' <<<"$out"; then
            echo "::error::vsce package emitted warnings"; exit 1
          fi
```

- [ ] **Step 3: Lint the workflow**

```bash
python -c "import yaml; yaml.safe_load(open('.github/workflows/vscode-extension.yml')); print('ok')"
```
Expected: `ok`.

- [ ] **Step 4: Commit + DEVLOG**

DEVLOG entry:

```markdown
## 2026-09-07 — F6.7: CI asserts the VSIX is clean

`vscode-extension.yml` — `vsce package` now fails on any `WARNING`, and a new
"Assert VSIX contents" step (ubuntu) checks `vsce ls`: `package.json`,
`README.md`, `LICENSE`, `dist/extension.js`, `dist/worker/*.dll`, and the
renderer bundle must be present; nothing under `src/`, `test/`, `out/`,
`node_modules/`, and no `*.test.ts` / `tsconfig*.json` may be.
```

```bash
git add .github/workflows/vscode-extension.yml docs/DEVLOG.md
git commit -m "$(cat <<'EOF'
ci(vscode): fail packaging on vsce warnings and assert VSIX contents

New step inspects `vsce ls`: runtime assets required, source/test files
forbidden. Guards the .vscodeignore against regressions.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8
EOF
)"
```

---

### Task 9: F6 close-out — full verification, DEVLOG, migration-spec status flip

**Files:**
- Modify: `docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md` (Status line + rollout table)
- Modify: `docs/DEVLOG.md` (close-out entry)
- Modify: `docs/f6-parity-checklist.md` (final re-walk marks, if any changed)

**Interfaces:**
- Consumes: everything from Tasks 1–8.
- Produces: the phase's terminal record.

- [ ] **Step 1: Full .NET verification, recorded**

```bash
dotnet build FSharpQueryStudio.slnx --configuration Release
dotnet test FSharpQueryStudio.slnx --no-build --configuration Release --filter "Category!=RequiresDocker&Category!=RequiresPublish"
```
Expected: build clean; all green. Paste the final test count.

- [ ] **Step 2: Full extension verification, recorded**

From `editors/vscode/`:

```bash
dotnet publish ../../src/FSharpQueryStudio.Worker/FSharpQueryStudio.Worker.fsproj -c Release -o dist/worker
npm ci
npm run compile
npm test
npm run pretest:vscode
npm run test:vscode
npx --yes @vscode/vsce package --out fs-query-studio.vsix && npx --yes @vscode/vsce ls && rm fs-query-studio.vsix
```
Expected: `npm test` unchanged pass count vs. F5 (no extension source changed); `npm run test:vscode` — worker-backed specs pass; `vsce package` warning-free; `vsce ls` clean. Paste counts.

- [ ] **Step 3: Re-walk the parity checklist**

Re-run the Task 1 verification commands now that Avalonia is gone. Every row must still be `☑`. If any regressed, that is a release blocker — fix before closing F6.

- [ ] **Step 4: Flip the migration spec status**

In `docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md`:

- In the **Rollout — phased** table, the `F6 Parity + retire` row's `Avalonia` column: `**deleted**` (it already reads `**deleted**` in that cell — confirm it stays) and change nothing else in the table.
- Replace the trailing **Status:** paragraph's last two sentences so it reads: `... F5 Connections is complete on \`main-vscode\` (not yet merged) — see the DEVLOG close-out entry (\`2026-09-07 — F5 complete\`). F6 Parity + retire is **complete** on \`main-vscode\` (not yet merged) — see \`2026-09-07 — F6 complete\`; \`App\`/\`Ui\`/\`Editor\`, their test projects, \`WindowsCredentialStore.fs\`, and \`installer/\` are deleted, and the VSIX is publish-ready (no Marketplace publish, no merge to \`main\` yet). F7 Extras remains backlog.`

- [ ] **Step 5: Write the F6 close-out DEVLOG entry**

```markdown
## 2026-09-07 — F6 complete: parity + retire

The VS Code extension has reached parity with the retired Avalonia app and
Avalonia is gone from the tree.

**Parity.** `docs/f6-parity-checklist.md` walks the migration spec's eight
F6 criteria — write+run a query, all six connectors, CSV export,
connection-name autocomplete, schema navigation, secrets-out-of-files,
session persistence, and the log-viewer→OutputChannel downgrade — each tied
to a passing automated suite or a recorded manual check.

**Retired.** `git rm` of `src/FSharpQueryStudio.{App,Ui,Editor}`,
`tests/FSharpQueryStudio.{Ui,Editor}.Tests`,
`src/FSharpQueryStudio.Security/WindowsCredentialStore.fs`, and `installer/`
(the WiX MSI). `FSharpQueryStudio.slnx` is down to 9 src + 10 test projects.
`Integration.Tests` lost its ten Avalonia-coupled files (WorkerClient,
ExecutionService, Milestone A–E acceptance, PublishedApp) and its
`TestHelpers.fs` is trimmed to `workerDllPath` / `withTimeout` / `request`;
it keeps the worker-stdio smoke, `NotebookExecutionTests`, the Testcontainers
connector suites, `ConnectionsRegressionTests`, and `PackageApprovalTests`.
The only production change: `Handlers.fs`'s module-default `secretStore` is
now `InMemorySecretStore()` — `Program.fs` still composes `BridgeSecretStore`
for the extension host, so secret resolution there is unchanged. `ci.yml`
drops the two deleted test projects from its Linux matrix. The Avalonia
planning docs carry a "superseded" banner.

**Publish-ready (not published).** MIT `LICENSE` at the repo root and in
`editors/vscode/`; a `.vscodeignore` that ships only `dist/**` (bundle +
worker), `media/**`, `package.json`, `README.md`, `LICENSE`; full
`package.json` Marketplace metadata (`version` 0.1.0, `license`,
`categories`, `keywords`, `bugs`, `homepage`, `qna`, `galleryBanner`; the
redundant `onCommand:` activation event removed). `README.md` is now a
user-facing overview. `docs/vscode-extension-release.md` is the manual
`vsce publish` runbook. `vscode-extension.yml` fails packaging on any `vsce`
warning and asserts the VSIX carries no source/test files. No publisher
account, PAT, or CI publish secret was created. `main-vscode` is **not**
merged to `main` — that is a separate decision.

Verification: `dotnet build FSharpQueryStudio.slnx` clean; `dotnet test`
(non-Docker, non-Publish) <count> passing; `editors/vscode` `npm test`
<count> passing / 1 skipped; `npm run test:vscode` <n> passing; `vsce
package` warning-free; `vsce ls` clean.
```

- [ ] **Step 6: Commit**

```bash
git add docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md docs/DEVLOG.md docs/f6-parity-checklist.md
git commit -m "$(cat <<'EOF'
docs: F6 (parity + retire) phase complete

Avalonia deleted, parity checklist walked, VSIX publish-ready. Migration
spec status flipped to F6 complete. main-vscode not merged to main.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01B3K2ZuY97tetSNkrv8gcQ8
EOF
)"
```

---

## Self-Review

**1. Spec coverage:**

| Spec section | Task |
|---|---|
| §1 Parity checklist deliverable (`docs/f6-parity-checklist.md`, 8 criteria) | Task 1, re-walked Task 9 |
| §2 F# retirement — delete App/Ui/Editor + test projects | Task 3 |
| §2 F# retirement — `WindowsCredentialStore.fs` + `Security.fsproj` | Task 3 |
| §2 F# retirement — `Handlers.fs` default + comment | Task 3 |
| §2 F# retirement — `HandlersTests.fs` swap | Task 3 |
| §2 F# retirement — `Integration.Tests` prune (expanded per brainstorming) | Task 2 |
| §2 F# retirement — `.slnx` | Task 3 |
| §3 installer/ removal | Task 4 |
| §3 planning-doc banners | Task 4 |
| §4 CI — `ci.yml` matrix + comments | Task 5 |
| §4 CI — `vscode-extension.yml` package gate | Task 8 |
| §5 LICENSE (MIT) | Task 6 |
| §5 package.json metadata | Task 6 |
| §5 `.vscodeignore` | Task 6 |
| §5 icon | Task 6 (best-effort, documented fallback) |
| §5 README polish | Task 7 |
| §5 release runbook | Task 7 |
| §6 Verification | Task 9 |
| §7 status flip + DEVLOG close-out | Task 9 |

No gaps.

**2. Placeholder scan:** No "TBD"/"handle edge cases"/"similar to Task N". The `<count>` / `<n>` / `<generated … | deferred>` markers in DEVLOG-entry text are deliberate fill-in-the-actual-number slots at execution time, not skipped work — each has an explicit verification command producing the number.

**3. Type consistency:** `TestHelpers` exports are named identically everywhere they appear: `workerDllPath`, `withTimeout`, `request` (Tasks 2, 9). `InMemorySecretStore()` — the exact type used in `Handlers.fs` and `HandlersTests.fs` (Task 3), matching its definition (`FSharpQueryStudio.Security.InMemorySecretStore`, verified in the source). `secretStore` / `configureSecretStore` names unchanged from the existing file. Workflow job names (`build-and-test-linux`, `Package VSIX`) quoted verbatim from the current files.

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-09-07-vscode-extension-f6-parity-retire.md`. Two execution options:

1. **Subagent-Driven (recommended)** — a fresh subagent per task, review between tasks, fast iteration.
2. **Inline Execution** — tasks run in this session via executing-plans, batch execution with checkpoints.

Which approach?
