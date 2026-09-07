# F8 — Build & publish scripts + release documentation

**Status:** Concluído
**Started:** 2026-09-07
**Last updated:** 2026-09-07
**Related:** branch `main-vscode`; `docs/superpowers/specs/2026-09-03-vscode-extension-migration-design.md` (rollout table); `docs/vscode-extension-release.md`; `.github/workflows/vscode-extension.yml`

---

## 1. Planning

### Objective
Turn the VS Code extension build/package/publish process from inline-only
bash in the CI workflow plus a prose runbook into repeatable, cross-platform
scripts that a developer runs locally and that CI reuses verbatim, and
document the whole release flow around them.

### Requirements / Scope
- `editors/vscode/scripts/` — Node ESM scripts, no new runtime, no new deps:
  - `lib.mjs` — shared helpers (`run`, repo paths, `assertVsixContents`).
  - `build.mjs` — `dotnet publish` the worker + `npm run compile`. Flags
    `--skip-worker`, `--configuration <cfg>`.
  - `package.mjs` — build → `vsce package` → fail on `WARNING` → `vsce ls`
    → `assertVsixContents`. Flag `--out`.
  - `publish.mjs` — preflight → package → `vsce publish --packagePath`.
    Flags `--dry-run`, `--allow-dirty`.
  - `doctor.mjs` — read-only environment + repo preflight, ✓/✗ table.
- `editors/vscode/package.json` scripts: `build`, `build:worker`, `package`,
  `publish`, `doctor`, `vscode:prepublish`.
- CI: replace the inline `Package VSIX` + `Assert VSIX contents` bash steps
  in `vscode-extension.yml` with `npm run package` (single source of truth).
- Docs: `editors/vscode/scripts/README.md`; rewrite the per-release section
  of `docs/vscode-extension-release.md` around the scripts; new
  `editors/vscode/CHANGELOG.md` (seeded 0.1.0); "Building & packaging"
  section in `editors/vscode/README.md`; F8 row in the migration design
  rollout table; this task doc + a DEVLOG entry.
- **Non-goals:** version-bump / git-tag automation (scripts *validate* the
  version, they do not write it); self-contained per-platform VSIX (still
  F7 backlog); an actual Marketplace publish (no publisher account / PAT).

### Approach & Key Decisions
- **Decision — Node `.mjs` scripts driven by npm scripts.** *Reasoning:*
  cross-platform with zero new tooling (Node + npm already required and
  already used by CI), idiomatic for a VS Code extension, one code path for
  Windows-primary dev and mac/linux/CI. *Alternatives:* PowerShell `.ps1`
  (matches the primary Windows dev box but forces a second code path and
  `pwsh` onto CI + other contributors); Bash `.sh` (matches today's CI but
  second-class on Windows).
- **Decision — CI calls `npm run package`.** *Reasoning:* the warning gate
  and the VSIX-contents assertion currently live only in the workflow YAML;
  moving them into `package.mjs`/`lib.mjs` makes a local run enforce exactly
  what CI enforces. *Alternative:* leave CI's inline bash and add local
  scripts alongside — rejected, that is two copies of the same rules that
  drift.
- **Decision — `publish.mjs` validates the version, never writes it.**
  *Reasoning:* keeps the scripts side-effect-free on git history; the
  human stays in control of the semver bump and the tag. Preflight refuses
  if the tree is dirty, `package.json` version does not match a
  `vscode-v<version>` tag on HEAD, `CHANGELOG.md` has no matching heading,
  `media/icon.png` is missing, or `VSCE_PAT` is unset. *Alternative:* a
  `release.mjs` that bumps + writes CHANGELOG + commits + tags — more
  moving parts than the first real publish warrants.
- **Decision — no unit tests for the scripts.** *Reasoning:* they are thin
  orchestration over `dotnet` and `vsce`; the meaningful verification is
  running them and CI exercising `package.mjs` on every push. The one piece
  of real logic, `assertVsixContents`, is a direct port of the bash checks
  CI already had.

### Risks & Assumptions
- Assumes `@vscode/vsce` stays invocable via `npx --yes @vscode/vsce` (no
  new devDependency added; matches current CI).
- `doctor.mjs` cross-checks `publisher` in `package.json` against the
  `EXTENSION_ID` constant duplicated in the 5 integration test files — a
  regex read, not a parse; if those files change shape the check degrades
  to a warning, not a hard failure.
- `media/icon.png` does not exist yet (F6 gap). `doctor` and `publish`
  report it as a blocker for `publish` only; `build` and `package` are
  unaffected.
- CI `Package VSIX` step currently runs only on `ubuntu-latest`; the
  `npm run package` replacement keeps that `if` guard.

---

## 2. Execution

### 2026-09-07 — task doc + planning
- Files touched: `docs/tasks/f8-build-publish-scripts.md` (new).
- Explored current state: no `scripts/` dir, no root `package.json`;
  package/assert logic lives only in `.github/workflows/vscode-extension.yml`;
  `editors/vscode/package.json` has no publish-related scripts;
  `docs/vscode-extension-release.md` is manual prose.

### 2026-09-07 — scripts
- New: `editors/vscode/scripts/{lib,build,package,publish,doctor}.mjs`.
- `lib.mjs` holds `run` / `runCapture` (spawn helpers), repo paths,
  `parseArgs`, and `assertVsixContents` — the latter a direct port of the
  required-files + no-`src`/`test`/`out` bash checks from the workflow.
- Two bugs found by running them:
  1. `package.mjs` warning gate used `/warning/i`, which matched Node's own
     `DeprecationWarning` on stderr. CI matched `WARNING` case-sensitively;
     changed to `/\bWARNING\b/`.
  2. `spawnSync` with `shell: true` + args array trips `DEP0190`, but a
     Windows `.cmd` shim (`npm`/`npx`) *cannot* be spawned without a shell
     since Node 20.12 (`EINVAL`). Resolved: `shell: true` only for those
     shims on Windows, with whitespace-args quoted; `dotnet`/`git` spawn
     shell-free. One cosmetic `DEP0190` line per `vsce` call remains on
     Windows local runs (not in CI — Linux, no shell); documented in
     `scripts/README.md`.

### 2026-09-07 — wiring + CI
- `editors/vscode/package.json`: added `build`, `build:worker`
  (`--worker-only`), `package`, `publish`, `doctor`, `vscode:prepublish`.
- `editors/vscode/.vscodeignore`: added `scripts/**`.
- `.github/workflows/vscode-extension.yml`: replaced the `Package VSIX`
  (warning-gate) and `Assert VSIX contents` steps with a single
  `run: npm run package -- --skip-worker` (worker published by the earlier
  job step). Upload-artifact step unchanged.

### 2026-09-07 — docs
- New `editors/vscode/CHANGELOG.md` (Keep a Changelog; `0.1.0` seeded from
  F1–F7, `[Unreleased]` for F8), `editors/vscode/scripts/README.md`.
- Rewrote the per-release section of `docs/vscode-extension-release.md`
  around `npm run doctor` → bump version + CHANGELOG → tag → `npm run
  publish`.
- `editors/vscode/README.md`: dev block simplified to `npm run build`;
  added a "Building & packaging" table linking `scripts/README.md` + the
  runbook.
- Migration design doc: added an **F8** row to the rollout table and a
  status sentence.
- DEVLOG: `## 2026-09-07 — F8` close-out entry.

---

## 3. Validation

### Test Plan
- `npm run doctor` — prints the ✓/✗ table; exits non-zero because
  `media/icon.png` is missing (expected), zero otherwise.
- `npm run build` — worker published to `dist/worker`, `dist/extension.js`
  + `media/renderer/index.js` rebuilt.
- `npm run package` — produces `fs-query-studio.vsix`, no `vsce` warnings,
  `assertVsixContents` passes.
- `npm run publish -- --dry-run` — preflight runs, stops before the actual
  `vsce publish`, reports the `icon.png` / version-tag blockers.
- Existing suites unaffected: `npm test`, `npm run test:vscode`.
- CI: the refactored `vscode-extension.yml` still packages + uploads the
  VSIX artifact on `ubuntu-latest`.

### Test Results
- `npm run doctor` — prints the ✓/✗ table; exits 1 on exactly one hard
  blocker (`media/icon.png` / no `"icon"` key — the F6 gap). Every other
  check ✓ except the soft `!` on `VSCE_PAT`.
- `npm run build` — worker `dotnet publish` + `npm run compile`, both clean.
- `npm run package` / `npm run package -- --skip-worker` — produces
  `fs-query-studio.vsix`, **no `vsce` warnings**, `✓ VSIX contents OK
  (264 files)`.
- `npm run publish -- --dry-run --allow-dirty` — reports 3 blockers (icon,
  HEAD not tagged `vscode-v0.1.0`, `VSCE_PAT` unset), stops before publish.
  With the tree dirty and no `--allow-dirty` it also reports the
  unclean-tree blocker; the CHANGELOG-heading blocker cleared once
  `CHANGELOG.md` was written.
- `npm test` — **224 / 224** pass, unchanged (27 files).
- Not run: `npm run test:vscode` (unaffected — no `src/` change); an actual
  `vsce publish` (no publisher account / PAT); CI (will run on push).

### Acceptance Criteria Check
| Requirement | Verified how | Result |
|---|---|---|
| `scripts/` with build/package/publish/doctor + lib | 5 files created; each runs | ✅ |
| npm scripts wired (`build`/`build:worker`/`package`/`publish`/`doctor`/`vscode:prepublish`) | `package.json` updated; `vsce package` invoked `vscode:prepublish` in the package run | ✅ |
| CI reuses `npm run package` | `vscode-extension.yml` two steps → one `npm run package -- --skip-worker` | ✅ (runs on next push) |
| `assertVsixContents` parity with old CI bash | Ported checks; `✓ VSIX contents OK` on a real listing; same required-file set | ✅ |
| Release doc rewritten around the scripts | `docs/vscode-extension-release.md` per-release section | ✅ |
| `scripts/README.md` + `CHANGELOG.md` + README section + design F8 row | all created / edited | ✅ |

---

## 4. Notes & Follow-ups
- `media/icon.png` (128×128 PNG) + the `"icon"` key in `package.json` still
  outstanding from F6 — the one hard blocker on `npm run doctor` /
  `npm run publish`. Tracked in `docs/vscode-extension-release.md` one-time
  setup.
- Version-bump / tag automation deliberately deferred (validate-only);
  revisit if release cadence picks up.
- One cosmetic `DEP0190` deprecation line prints per `vsce` call on Windows
  local runs (`.cmd` shim needs a shell). Not in CI, does not affect the
  `WARNING` gate. If it ever needs silencing: run the entry scripts with
  `node --disable-warning=DEP0190` (Node ≥ 20.11).
- `npm run package` re-publishes the worker and re-compiles even when
  nothing changed (~30 s). `--skip-worker` skips the `dotnet publish`;
  there is no `--skip-compile`. Fine for now.
</content>
</invoke>
