# Releasing the F# Query Studio VS Code extension

The extension is **publish-ready but not published**. There is no Marketplace
publisher account, no PAT, and no CI publish secret yet. This runbook is the
manual process for the first publish and every one after it.

The mechanics live in `editors/vscode/scripts/` and run through npm scripts
(`npm run doctor` / `build` / `package` / `publish`) — see
[`editors/vscode/scripts/README.md`](../editors/vscode/scripts/README.md). Run
every command below from `editors/vscode/`.

## One-time setup

1. **Create a Marketplace publisher** at
   <https://marketplace.visualstudio.com/manage>. Note its id.

2. **If the publisher id is not `fs-query-studio`,** update `"publisher"` in
   `editors/vscode/package.json`. That changes the extension id
   (`<publisher>.<name>`), which is hard-coded as the `EXTENSION_ID` constant —
   currently `"fs-query-studio.fs-query-studio"` — in **all five**
   `editors/vscode/test/integration/*.test.ts` files (`connections`,
   `extension`, `intelligence`, `notebook`, `renderer`; each calls
   `vscode.extensions.getExtension(EXTENSION_ID)` in `before`). Update the
   constant in all five or the extension-host CI suite (`vscode-extension.yml`)
   fails on the publish PR. `npm run doctor` cross-checks this and fails if they
   disagree.

3. **Create a Personal Access Token** in the publisher's Azure DevOps
   organization with **Marketplace → Manage** scope. Keep it out of the repo;
   export it as `VSCE_PAT` when publishing.

4. **Add the Marketplace icon.** Put a 128×128 PNG at
   `editors/vscode/media/icon.png` and add `"icon": "media/icon.png"` to
   `editors/vscode/package.json` **before the first publish**. This file does
   not exist yet (no rasterizer was available when the packaging scripts
   landed). `npm run doctor` and `npm run publish` report its absence as a
   blocker; `npm run build` / `npm run package` do not care.

## Per release

1. **`npm run doctor`** — resolve every `✗`. A `!` on `VSCE_PAT` is fine until
   step 6.
2. **Bump `"version"`** in `editors/vscode/package.json` (semver).
3. **Update `editors/vscode/CHANGELOG.md`** — add a `## <version>` heading and
   move the `## [Unreleased]` items under it. `npm run publish` refuses without a
   heading that matches the new version.
4. **Keep the two `LICENSE` files identical** (`./LICENSE` and
   `editors/vscode/LICENSE`, both MIT) — only touch them if the license itself
   changes.
5. **Commit, tag, push:**
   ```bash
   git tag vscode-v<version>
   git push && git push --tags
   ```
6. **Publish from a clean checkout of the tagged commit:**
   ```bash
   cd editors/vscode
   VSCE_PAT=<token> npm run publish
   ```
   `publish` re-runs the preflight (clean tree, `package.json` version ==
   `vscode-v<version>` on `HEAD`, CHANGELOG heading, icon, `VSCE_PAT`), then
   `npm run package`, then `vsce publish --packagePath fs-query-studio.vsix`.
   Run `npm run publish -- --dry-run` first to see the preflight result without
   publishing.
7. **Smoke test:** install the Marketplace build in a clean VS Code, open a
   `.fsqnb`, add a connection, run a cell that returns a table.

## Just the VSIX, no publish

`npm run package` produces `editors/vscode/fs-query-studio.vsix` with the same
warning gate and contents assertion CI enforces. CI also uploads it as the
`fs-query-studio-vsix` artifact on every `main` / `main-vscode` push that
touches the extension or the worker (it never publishes) — download that instead
of building locally.

## Security & Anti-Leak Safeguards

The packaging and publishing pipeline enforces multi-layer defenses to ensure zero sensitive artifacts leak into the VSIX:

1. **Exhaustive Package Ignores (`.vscodeignore`)**:
   - Explicitly rejects debug symbols (`**/*.pdb`), source maps (`**/*.map`), and raw source code (`**/*.ts`, `**/*.fs`, `**/*.cs`).
   - Rejects all environment configs (`**/.env*`), certificates (`**/*.pem`, `**/*.key`, `**/*.pfx`, `**/*.p12`), and credentials (`*secret*`, `*token*`).
   - Rejects local notebooks (`**/*.fsqnb`), local databases (`**/*.db`, `**/*.sqlite*`), backups, and nested `*.vsix` packages.
   - Rejects internal VCS, IDE, and agent metadata (`.git/**`, `.github/**`, `.agents/**`, `.claude/**`, etc.).

2. **Worker Build Cleansing (`scripts/build.mjs`)**:
   - `dotnet publish` runs with `-p:DebugType=None -p:DebugSymbols=false` in Release mode.
   - `build.mjs` automatically purges any `.pdb` and documentation `.xml` files from `editors/vscode/dist/worker/`.

3. **Strict VSIX Assertion Gate (`scripts/lib.mjs: assertVsixContents`)**:
   - Every `npm run package` inspects `vsce ls` against `forbiddenVsixRules`. Any debug symbol, uncompiled source file, secret, database, or nested archive halts packaging with a fatal error.

4. **Preflight Security Audit (`scripts/doctor.mjs`)**:
   - `npm run doctor` scans the extension tree for untracked/unignored secrets or local databases and validates that `.vscodeignore` contains all critical rules. `npm run publish` refuses to proceed if any security check fails.

5. **Pre-Commit Secret Scanner (`.githooks/pre-commit`)**:
   - Rejects git commits containing staged `.env*`, `.pem`, `.key`, `secrets.json`, `.pdb`, or private key blocks.

## Notes

- The VSIX bundles the framework-dependent worker (`dist/worker/**`); the user
  still needs .NET 10 on `PATH`. A self-contained per-platform VSIX is backlog.
- CI packages the VSIX with `npm run package -- --skip-worker` (an earlier job
  step publishes the worker). The warning gate and the VSIX-contents assertion
  live in `scripts/lib.mjs` / `scripts/package.mjs`, so a local `npm run
  package` enforces exactly what CI does.
- Marketplace publish is deliberately **not** wired into CI — it is manual and
  tag-driven for v1.

