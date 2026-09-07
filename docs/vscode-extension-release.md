# Releasing the F# Query Studio VS Code extension

As of phase F6 the extension is **publish-ready but not published**. There is
no Marketplace publisher account, no PAT, and no CI publish secret. This
runbook is the manual process for the first and subsequent publishes.

Since F8 the mechanics live in `editors/vscode/scripts/` and are run through
npm scripts (`npm run doctor` / `build` / `package` / `publish`) — see
`editors/vscode/scripts/README.md`. Run every command below from
`editors/vscode/`.

## One-time setup

1. Create a Visual Studio Marketplace **publisher** at
   <https://marketplace.visualstudio.com/manage>. Note its id.
2. If it differs from `fs-query-studio`, update `"publisher"` in
   `editors/vscode/package.json`. This changes the extension id
   (`<publisher>.<name>`), which is hardcoded as the `EXTENSION_ID`
   constant (currently `"fs-query-studio.fs-query-studio"`) in **all five**
   `editors/vscode/test/integration/*.test.ts` files (`connections`,
   `extension`, `intelligence`, `notebook`, `renderer` — each calls
   `vscode.extensions.getExtension(EXTENSION_ID)` in `before`). Update that
   constant in all five, or the extension-host CI suite
   (`vscode-extension.yml`) fails on the publish PR. `npm run doctor`
   cross-checks this and fails if they disagree.
3. In the publisher's Azure DevOps organization, create a **Personal Access
   Token** with **Marketplace → Manage** scope. Keep it out of the repo;
   export it as `VSCE_PAT` when publishing.
4. Add a 128×128 PNG at `editors/vscode/media/icon.png` and add the
   `"icon": "media/icon.png"` key to `editors/vscode/package.json` **before
   the first publish**. F6 did not produce this icon — no rasterizer
   (ImageMagick / rsvg-convert / inkscape) was available on the build
   machine — so the file does not exist and `package.json` has no `icon`
   key yet. `npm run doctor` and `npm run publish` report this as a
   blocker; `npm run build` / `npm run package` do not care.

## Per release

1. `npm run doctor` — resolve every ✗ (a `!` on `VSCE_PAT` is fine until
   step 6).
2. Bump `"version"` in `editors/vscode/package.json` (semver).
3. Add a `## <version>` section to `editors/vscode/CHANGELOG.md` describing
   the release (move items out of `## [Unreleased]`). `npm run publish`
   refuses without a heading matching the new version.
4. Keep `LICENSE` and `editors/vscode/LICENSE` identical (both MIT — only
   touch if the license itself changes).
5. Commit, then tag and push:
   ```bash
   git tag vscode-v<version>
   git push && git push --tags
   ```
6. Publish from a clean checkout of the tagged commit:
   ```bash
   cd editors/vscode
   VSCE_PAT=<token> npm run publish
   ```
   `publish` re-runs the preflight (clean tree, `package.json` version ==
   `vscode-v<version>` on HEAD, CHANGELOG heading, icon, `VSCE_PAT`), then
   `npm run package`, then `vsce publish --packagePath fs-query-studio.vsix`.
   Use `npm run publish -- --dry-run` first to see the preflight result
   without publishing.
7. Smoke test: install the Marketplace build in a clean VS Code, open a
   `.fsqnb`, add a connection, run a cell that returns a table.

### If you only want the VSIX (no publish)

`npm run package` produces `editors/vscode/fs-query-studio.vsix` with the
same warning gate and contents assertion CI uses. CI also uploads this as
the `fs-query-studio-vsix` artifact on every `main` / `main-vscode` push
touching the extension or the worker (it never publishes) — you can
download that instead of building locally.

## Notes

- The VSIX bundles the framework-dependent worker (`dist/worker/**`); the
  user still needs .NET 10 on `PATH`. A self-contained per-platform VSIX is
  F7 backlog.
- CI packages the VSIX by calling `npm run package -- --skip-worker` (the
  worker is published by an earlier job step). The warning gate and the
  VSIX-contents assertion are in `scripts/lib.mjs` / `scripts/package.mjs`,
  so a local `npm run package` enforces exactly what CI does.
- Marketplace publish is not wired into CI — it is deliberately manual /
  tag-driven for v1.
</content>
