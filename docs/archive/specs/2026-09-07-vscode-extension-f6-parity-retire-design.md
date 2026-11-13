# VS Code Extension F6 — Parity + Retire — Design

Final phase of the VS Code extension migration (`2026-09-03-vscode-extension-migration-design.md`). F1–F5 built the extension to feature parity with the Avalonia desktop app for the query-and-execution workflow. F6 proves that parity with a walked checklist, then removes the Avalonia tier from the tree, and makes the VSIX publish-ready — short of an actual Marketplace publish.

The F# **worker** and every reused library (`Execution`, `Connectors`, `Results`, `Protocol`, `Storage`, `Security`, `Fsac`, `Logging`) are untouched except one default-value change in `Handlers.fs`. No wire-protocol change. No extension feature work.

## Background

### What F1–F5 delivered (all on `main-vscode`, not merged to `main`)

- **F1** — `editors/vscode/` scaffold, `worker/client.ts` NDJSON transport, `dotnet` resolution, health check.
- **F2** — `.fsqnb` serializer + `NotebookController`, run a cell, stdout/stderr streaming, Persistent mode.
- **F3** — `NotebookRenderer` results grid, schema view, CSV export, paging via `results/page`.
- **F4** — in-cell completion / hover / signature help / diagnostics via language providers forwarding to `analysis/*`; cell concatenation; connection-name autocomplete.
- **F5** — Connections TreeView + Add/Edit/Remove QuickPick, `BridgeSecretStore` + reverse `secret/get` RPC (worker → host), cross-platform secrets via VS Code `SecretStorage`.

The migration spec's rollout table names F6 **Parity + retire**: *"Parity checklist vs the app. Remove `App`/`Ui`/`Editor` + `WorkerClient.fs` + themes + `WindowsCredentialStore` from the active path. Clean `.slnx`. Review `installer/`. Marketplace publish."* — with Avalonia **deleted** at the end of this phase.

### Decisions taken for F6 (from brainstorming)

| Question | Decision |
|---|---|
| How far does "Marketplace publish" go? | **Publish-ready only.** Metadata, `LICENSE`, icon, `.vscodeignore`, README, a clean warning-free `vsce package` VSIX artifact in CI, and a written release runbook. No `vsce publish`, no publisher PAT / CI secret this phase. |
| Retired F# code | **Hard delete.** `git rm` the three src projects, the two dead test projects, `WindowsCredentialStore.fs`, and `WindowsCredentialStoreTests.fs`. Git history retains them. |
| `installer/` (WiX MSI, targets `FSharpQueryStudio.App.exe`) | **Delete.** `git rm -r installer/`. The VSIX is the only distribution artifact. |
| End F6 by merging `main-vscode` → `main`? | **No.** F6 completes on `main-vscode` like F1–F5. Merging the migration to `main` is a separate later decision. |
| `LICENSE` (none exists anywhere in the repo) | **MIT.** Repo-root `LICENSE`, `"license": "MIT"` in `package.json`, shipped in the VSIX. |
| Parity verification depth | **Written checklist, walked, with evidence.** `docs/f6-parity-checklist.md`, each criterion tagged automated (cite the test) or manual (evidence note), committed as the F6 parity record. |
| Avalonia-era planning docs (`docs/fs-studio-plan.md`, `docs/fs-studio-development-plan.md`) | **Keep with a retirement banner.** Prepend a "superseded by the VS Code extension migration (F6, 2026-09); Avalonia host retired" paragraph to each. History intact. |

### Current tree state (verified 2026-09-07)

- `FSharpQueryStudio.slnx` lists 12 src projects + 12 test projects. To be removed: `src/FSharpQueryStudio.App`, `src/FSharpQueryStudio.Ui`, `src/FSharpQueryStudio.Editor`, `tests/FSharpQueryStudio.Editor.Tests`, `tests/FSharpQueryStudio.Ui.Tests`.
- `WindowsCredentialStore` references outside `App`:
  - `src/FSharpQueryStudio.Security/FSharpQueryStudio.Security.fsproj` — `<Compile Include="WindowsCredentialStore.fs" />`
  - `src/FSharpQueryStudio.Worker/Handlers.fs:80` — `let mutable private secretStore: ISecretStore = WindowsCredentialStore()` (module default) + explanatory comment at lines 74–80.
  - `tests/FSharpQueryStudio.Integration.Tests/FSharpQueryStudio.Integration.Tests.fsproj` — `<Compile Include="WindowsCredentialStoreTests.fs" />`
  - `tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs:1236` — `Handlers.configureSecretStore (FSharpQueryStudio.Security.WindowsCredentialStore())` in one test.
  - `App.axaml.fs:110` — goes away with `App`.
- `Program.fs` (`main`) **always** composes `BridgeSecretStore` via `Handlers.configureSecretStore`, so the `Handlers.fs` module default is only reached by a standalone `dotnet run` of the worker or by tests that never call `configureSecretStore`.
- `installer/` — WiX project (`FSharpQueryStudio.Installer.wixproj`, `Package.wxs` targeting `FSharpQueryStudio.App.exe`). **Not referenced** by `.slnx` or any workflow.
- `.github/workflows/ci.yml` — Windows job builds/tests the whole `.slnx`; Linux matrix job lists every test project individually, **including** `FSharpQueryStudio.Ui.Tests` and `FSharpQueryStudio.Editor.Tests`, with long comment blocks (D.8, E.9) about Avalonia / Monaco / WebView cross-platform behaviour.
- `.github/workflows/vscode-extension.yml` — publishes the worker, runs unit + extension-host tests on win/linux/mac, and **already** packages `fs-query-studio.vsix` and uploads it as an artifact (ubuntu only).
- `editors/vscode/package.json` — `version: "0.0.1"`, `publisher: "fs-query-studio"`, `categories: ["Other"]`, **no** `license`, `icon`, `keywords`, `bugs`, `homepage`, `galleryBanner`, `qna`. No `.vscodeignore` file in `editors/vscode/`.
- No `LICENSE` / `COPYING` file anywhere in the repo (node_modules excluded).

## Goals

- A committed, walked parity checklist covering the migration spec's eight F6 parity criteria.
- Avalonia gone: `App`, `Ui`, `Editor`, their test projects, `WindowsCredentialStore.fs`, and `installer/` removed from the tree; `.slnx` and CI cleaned; the .NET build and test suite green without them.
- The worker builds and its tests pass with a cross-platform default secret store; no functional change to secret resolution (the bridge is unaffected).
- The VSIX is publish-ready: `LICENSE` (MIT), complete `package.json` Marketplace metadata, an icon, a `.vscodeignore` that ships only runtime assets, a polished README, and a CI gate that fails on any `vsce package` warning.
- A written release runbook for the eventual manual `vsce publish`.
- A final DEVLOG close-out and a migration-spec status flip (`Avalonia: deleted`).

## Non-goals

- **No `vsce publish`.** No publisher account setup, no PAT, no CI publish secret. Publish-ready plumbing only.
- **No merge of `main-vscode` into `main`.** F6 lands on `main-vscode`.
- **No extension feature work.** No new commands, providers, renderer changes, or worker RPCs. F7 backlog items (`workspace/prelude`, in-cell `definition`/`rename`, self-contained VSIX, parallel workers) stay in the backlog.
- **No rewrite of the Avalonia planning docs** beyond a retirement banner.
- **No new native secret-store implementation.** `InMemorySecretStore` is the new module default; the real cross-platform path is the F5 bridge and is untouched.
- **No change to `Protocol` wire types, the NDJSON transport, FSI execution, or any reused library's behaviour.** The only `src/` edit is the `Handlers.fs` default value + its comment, plus removing one `<Compile>` line from `Security.fsproj`.
- **No removal of `Integration.Tests` itself** — only its `WindowsCredentialStore` test file and any retired-project reference.

## Approach — verify first, then demolish, then publish-ready

Rejected alternatives:

- **Retire first, fix the build after.** If a project marked for deletion turns out load-bearing, that surfaces as a red build in the middle of demolition, entangled with every other removal. Verifying parity while Avalonia is still present keeps the "does the extension stand alone?" question separate from the "did the build survive deletion?" question.
- **One big-bang commit.** Unreviewable, un-bisectable, and against the project's commit-after-every-task cadence.

Sequence: (1) prove parity against `main-vscode` as it stands, (2) remove Avalonia and get the .NET build/test green, (3) make the VSIX publish-ready. Each task is its own commit with a DEVLOG entry; one final F6 close-out entry.

## Design

### 1. Parity checklist deliverable

New file `docs/f6-parity-checklist.md`. One row per migration-spec F6 parity criterion. Each row records: the criterion, **automated** (with the exact test file / spec name that exercises it) or **manual** (with a one-to-three-line evidence note from a real run), and a pass mark.

| # | Criterion | Verification source (expected) |
|---|---|---|
| 1 | Write and run a query | Extension-host notebook specs (F2/F3 `test/integration/*.test.ts`) — open `.fsqnb`, execute a cell, assert output. |
| 2 | All six connectors (Sqlite, Csv, Excel, SqlServer, PostgreSql, RestJson) | Worker `Connectors.Tests` (unchanged) + `test/integration/connections.test.ts` sqlite & postgres end-to-end. Excel/Csv/RestJson: cite worker connector tests; manual note if no host-level spec. |
| 3 | CSV export | F3 renderer unit tests + the `table-json` host spec (`results/exportCsv` path). |
| 4 | Connection-name autocomplete | `test/integration/intelligence.test.ts` — completion offers a `Connections.*` name. |
| 5 | Schema navigation | `test/integration/connections.test.ts` — `connection/schema` returns the fixture's real table + column; TreeView expansion (manual note). |
| 6 | Secrets kept out of files | `connections.test.ts` — `{password}` resolves through the reverse bridge; `connection/register` receives only `SecretReference`; assert `workspace.json` on disk carries no raw secret. |
| 7 | Session persistence | F2 Persistent-mode controller specs — a `let` in one cell is visible in a later cell; `Restart Kernel` clears it. |
| 8 | Log viewer → `OutputChannel` + Serilog file sink | Manual note — accepted downgrade per the migration spec. Confirm both channels exist and the file sink still writes under `LocalApplicationData`. |

Walked against `main-vscode` **before** any deletion. If any criterion has no automated coverage and no cheap manual path, that is a finding recorded in the checklist, not a silent gap.

### 2. F# retirement

**Deletions (`git rm`):**

- `src/FSharpQueryStudio.App/` (whole project)
- `src/FSharpQueryStudio.Ui/` (whole project)
- `src/FSharpQueryStudio.Editor/` (whole project)
- `tests/FSharpQueryStudio.Editor.Tests/` (whole project)
- `tests/FSharpQueryStudio.Ui.Tests/` (whole project)
- `src/FSharpQueryStudio.Security/WindowsCredentialStore.fs`
- `tests/FSharpQueryStudio.Integration.Tests/WindowsCredentialStoreTests.fs`

**Edits:**

- `src/FSharpQueryStudio.Security/FSharpQueryStudio.Security.fsproj` — remove the `<Compile Include="WindowsCredentialStore.fs" />` line. `Security` keeps `ISecretStore`, `InMemorySecretStore`, `OriginDetection`.
- `src/FSharpQueryStudio.Worker/Handlers.fs` — the module default becomes `let mutable private secretStore: ISecretStore = InMemorySecretStore()`. Rewrite the lines 74–80 comment: the default now serves standalone `dotnet run` / tests that never call `configureSecretStore`; the extension host always composes `BridgeSecretStore` in `Program.fs`'s `main`. No behavioural change on the extension path.
- `tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs:1236` — replace `FSharpQueryStudio.Security.WindowsCredentialStore()` with `FSharpQueryStudio.Security.InMemorySecretStore()`. Check the surrounding assertion (lines ~1199–1240) still makes sense — it tests that an injected store is used *instead of* the default; the swap keeps that meaning.
- `tests/FSharpQueryStudio.Integration.Tests/FSharpQueryStudio.Integration.Tests.fsproj` — remove the `<Compile Include="WindowsCredentialStoreTests.fs" />` line and any `<ProjectReference>` to `App` / `Ui` / `Editor` (grep confirmed the fsproj is in the "references a retired project" set; the plan task resolves exactly which). Keep every other file in the suite.
- `FSharpQueryStudio.slnx` — remove the five `<Project>` lines for the deleted projects (3 src, 2 test).

**Verification gate for this task:** `dotnet build FSharpQueryStudio.slnx` clean; `dotnet test FSharpQueryStudio.slnx --filter "Category!=RequiresDocker&Category!=RequiresPublish"` green.

### 3. installer/ and planning-doc cleanup

- `git rm -r installer/`.
- Grep `Directory.Build.props`, `Directory.Build.targets`, `.slnx`, and both workflows for `installer` / `Installer` / `wixproj` — confirmed none today; the plan task re-checks and removes anything that appears.
- `docs/fs-studio-plan.md` and `docs/fs-studio-development-plan.md` — prepend a short blockquote banner:

  > **Superseded (2026-09).** The Avalonia desktop host described here was retired in the VS Code extension migration, phase F6. F# Query Studio now ships as a VS Code extension (`editors/vscode/`). This document is kept for historical context; see `docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md` and the DEVLOG.

- `.slnx` `/docs/` folder listing keeps both files.

### 4. CI

- `.github/workflows/ci.yml`:
  - Linux matrix job — delete the `tests/FSharpQueryStudio.Ui.Tests/...` and `tests/FSharpQueryStudio.Editor.Tests/...` matrix entries.
  - Trim the stale comment prose in that job: the D.8 / E.9 paragraphs about Avalonia `NativeWebView`, Monaco, WebKitGTK, and "`App` has no dedicated test project" no longer describe anything in the tree. Replace with a one-line note that the matrix runs the reused libraries + worker on Linux.
  - Windows job — no change needed; it builds/tests the (now smaller) `.slnx`.
- `.github/workflows/vscode-extension.yml`:
  - The existing "Package VSIX" step stays. Add `--no-dependencies` is **not** applicable (esbuild bundles). Instead make packaging **warning-fatal**: run `vsce package` capturing output and fail the step if it prints a `WARNING`, or use `vsce package 2>&1 | tee` + `grep -q WARNING && exit 1`. Simplest: `npx --yes @vscode/vsce package --out fs-query-studio.vsix` already exits non-zero on error; add a follow-up `npx @vscode/vsce ls` step and assert (grep) that the listing contains `LICENSE` and `package.json` and does **not** contain `src/` or `test/`.

### 5. Publish-ready

- **`LICENSE`** at repo root — MIT, `Copyright (c) 2026 Matheus Leal` (matches the git author). Not added to `.vscodeignore`, so it ships in the VSIX.
- **`editors/vscode/package.json`:**
  - `"version": "0.1.0"` (first real pre-release line; `0.0.1` was a scaffold placeholder).
  - `"license": "MIT"`.
  - `"icon": "media/icon.png"` — a new 128×128 PNG (reuse / rasterise the existing `media/activitybar.svg` glyph on a solid background).
  - `"categories": ["Notebooks", "Data Science", "Programming Languages"]` (drop `"Other"`).
  - `"keywords": ["fsharp", "f#", "sql", "query", "notebook", "database", "rest", "csv"]`.
  - `"bugs": { "url": "https://github.com/Matheusleal/fs-query-studio/issues" }`.
  - `"homepage": "https://github.com/Matheusleal/fs-query-studio#readme"`.
  - `"qna": "marketplace"`.
  - `"galleryBanner": { "color": "#4B267D", "theme": "dark" }` (F#-purple; adjustable).
  - Leave `publisher: "fs-query-studio"` as-is — a real publisher id is chosen at publish time (runbook notes this).
- **`editors/vscode/.vscodeignore`** (new):
  ```
  src/**
  test/**
  out/**
  node_modules/**
  .vscode-test/**
  tsconfig*.json
  esbuild.mjs
  vitest.config.ts
  .vscode-test.mjs
  **/*.map
  **/*.test.ts
  ```
  Ships: `dist/**`, `media/**`, `README.md`, `LICENSE`, `package.json`, `package-lock.json`. Verify `dist/worker/**` (the published worker) is **included** — it must be in the VSIX.
- **`editors/vscode/README.md`** — restructure to: one-line pitch; **Requirements** (`.NET 10 SDK on PATH`); **Quick start** (create a `.fsqnb`, run a cell, add a connection via the Activity Bar); **Features** (notebook execution, results grid + CSV export, in-cell F# intelligence, connection management with OS-native secret storage); **Limitations** (in-cell intelligence ceiling — completion/hover/signature/diagnostics only; one evaluation at a time across notebooks; `dotnet` on PATH required); **Ionide** (coexists; handles `.fs`/`.fsx`, never notebook cells).
- **`docs/vscode-extension-release.md`** (new) — the manual runbook:
  1. Choose / register a Marketplace publisher; update `package.json` `publisher` if it differs.
  2. Bump `version` in `editors/vscode/package.json`; commit; `git tag vscode-vX.Y.Z`.
  3. Let `vscode-extension.yml` build; download the `fs-query-studio-vsix` artifact.
  4. `npx @vscode/vsce publish --packagePath fs-query-studio.vsix` with `VSCE_PAT` in the environment (personal access token from the publisher's Azure DevOps org).
  5. Post-publish smoke: install the Marketplace build in a clean VS Code, open a `.fsqnb`, run a cell.
  Note explicitly: no PAT or CI secret is configured as of F6; step 4 is manual.

### 6. Verification

Run and record actual output for each:

- `dotnet build FSharpQueryStudio.slnx --configuration Release` — clean.
- `dotnet test FSharpQueryStudio.slnx --configuration Release --filter "Category!=RequiresDocker&Category!=RequiresPublish"` — green; note the count drop from the two removed suites.
- From `editors/vscode/`: `npm run compile` (tsc + esbuild clean), `npm test` (unit — unchanged pass count), `npm run test:vscode` (host — unchanged pass/pending).
- From `editors/vscode/`: `npx --yes @vscode/vsce package --out fs-query-studio.vsix` — **zero warnings**; then `npx @vscode/vsce ls` — inspect: `LICENSE` present, `dist/worker/` present, no `src/`, no `test/`.
- `docs/f6-parity-checklist.md` — every row marked pass (or an explicit recorded finding).

### 7. Task breakdown (for `writing-plans`)

1. **F6 spec** — this document; commit.
2. **Parity checklist** — write `docs/f6-parity-checklist.md`, walk all eight criteria against `main-vscode`, commit with evidence.
3. **Retire F#** — `git rm` the projects + cred-store files; edit `Security.fsproj`, `Handlers.fs` (+ comment), `HandlersTests.fs`, `Integration.Tests.fsproj`, `.slnx`; `dotnet build` + `dotnet test` green; commit; DEVLOG.
4. **Delete installer + doc banners** — `git rm -r installer/`; re-grep for stray refs; prepend banners to the two planning docs; commit; DEVLOG.
5. **CI prune** — `ci.yml` Linux matrix entries + stale comments; commit; DEVLOG.
6. **Publish metadata** — `LICENSE`, `package.json` fields, `media/icon.png`, `.vscodeignore`; local `vsce package` clean; commit; DEVLOG.
7. **README + runbook** — rewrite `editors/vscode/README.md`; add `docs/vscode-extension-release.md`; commit; DEVLOG.
8. **CI package gate** — `vscode-extension.yml` `vsce ls` assertion step; commit; DEVLOG.
9. **F6 close-out** — full verification pass with recorded output; `2026-09-07 — F6 complete` DEVLOG entry; flip the migration spec's Status line and rollout-table `Avalonia` column to `deleted`; commit.

## Testing

No new test code. F6 is deletion + configuration + a walked manual checklist. Coverage is the **existing** suites proving nothing regressed:

| Layer | Command | Asserts |
|---|---|---|
| Reused libs + worker | `dotnet test FSharpQueryStudio.slnx --filter "Category!=RequiresDocker&Category!=RequiresPublish"` | Execution / Connectors / Results / Fsac / Protocol / Storage / Security / Worker unaffected by the retirement; the `InMemorySecretStore` default swap in `HandlersTests.fs` still passes. |
| Extension unit | `cd editors/vscode && npm test` | Unchanged — no extension source touched. |
| Extension host | `cd editors/vscode && npm run test:vscode` (CI: win/linux/mac) | Unchanged — the F2–F5 integration specs still green; they are the live parity evidence for checklist rows 1–7. |
| Packaging | `npx @vscode/vsce package` + `vsce ls` (CI gate) | Warning-free; VSIX ships `dist/**` + `media/**` + `LICENSE` + `README.md` + `package.json` and nothing else. |
| Parity | `docs/f6-parity-checklist.md` | Eight criteria, each automated-with-citation or manual-with-evidence. |

## Risks

- **A retired project is referenced somewhere not yet found.** Mitigation: the retirement task's gate is a full `dotnet build` + `dotnet test` of the `.slnx`; anything dangling fails there, before the commit. `Integration.Tests` is the one suite that both references a retired project and must survive — the plan task pins down exactly which references to cut.
- **`HandlersTests.fs` line ~1236 assertion depends on `WindowsCredentialStore` semantics.** Read the full test before swapping; if it asserts a Windows-specific behaviour rather than "the injected store wins", it needs rewriting, not a one-token swap. Recorded as a plan-task check.
- **`vsce package` warns on something beyond metadata** (e.g. a missing `activationEvents` shape, a `repository` mismatch, a large bundle). Mitigation: run `vsce package` locally in task 6, not first in CI; fix whatever it flags then.
- **`.vscodeignore` excludes something the runtime needs** (worker DLLs, renderer assets). Mitigation: `vsce ls` inspection in task 6 + the CI assertion that `dist/worker/` is present; a host smoke test (`test:vscode`) already exercises a real worker spawn.
- **Icon quality.** A rasterised activity-bar glyph may look poor at 128×128 on a Marketplace card. Acceptable for publish-ready; the runbook notes the icon can be replaced before an actual publish.
- **CI comment edits in `ci.yml` lose institutional context** (the D.8 / E.9 notes explain historical cross-platform decisions). Mitigation: trim to a one-line pointer rather than deleting outright; the full reasoning stays in git history and the DEVLOG.
