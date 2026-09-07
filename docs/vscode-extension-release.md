# Releasing the F# Query Studio VS Code extension

As of phase F6 the extension is **publish-ready but not published**. There is
no Marketplace publisher account, no PAT, and no CI publish secret. This
runbook is the manual process for the first and subsequent publishes.

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
   constant in all five (or derive it from `package.json`), or the
   extension-host CI suite (`vscode-extension.yml`) fails on the publish PR.
3. In the publisher's Azure DevOps organization, create a **Personal Access
   Token** with **Marketplace → Manage** scope. Keep it out of the repo.
4. Add a 128×128 PNG at `editors/vscode/media/icon.png` and add the
   `"icon": "media/icon.png"` key to `editors/vscode/package.json` **before
   the first publish**. F6 did not produce this icon — no rasterizer
   (ImageMagick / rsvg-convert / inkscape) was available on the build
   machine — so the file does not exist and `package.json` has no `icon`
   key yet.

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
