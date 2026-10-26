# `editors/vscode/scripts/`

Plain Node ESM scripts that build, package, and publish the extension. They are
driven by npm scripts so the same code path runs on Windows, macOS, Linux, and
in CI — no extra runtime, no extra dependency. `@vscode/vsce` is invoked through
`npx --yes @vscode/vsce`, matching CI; it is intentionally **not** a
devDependency.

Run everything from `editors/vscode/`.

| npm script | Script | What it does |
|---|---|---|
| `npm run doctor` | `doctor.mjs` | Read-only preflight. Checks .NET ≥ 10, Node ≥ 20, that `@vscode/vsce` resolves, that `publisher.name` in `package.json` matches the `EXTENSION_ID` constant in the five integration tests, that the Marketplace icon exists, and whether `VSCE_PAT` is set. Prints a `✓ / ✗ / !` table; exits non-zero on a hard failure. |
| `npm run build:worker` | `build.mjs --worker-only` | `dotnet publish` the F# worker into `dist/worker/` only. |
| `npm run build` | `build.mjs` | `dotnet publish` the worker, then `npm run compile` (`tsc --noEmit` + esbuild). A full runnable dev build. Also runs as `vscode:prepublish`. |
| `npm run package` | `package.mjs` | `build` → `vsce package --out fs-query-studio.vsix` → fail if `vsce` printed `WARNING` → `vsce ls` → assert the VSIX contains every required file and no `src` / `test` / `out` / `node_modules` junk. Exactly what CI enforces. |
| `npm run publish` | `publish.mjs` | Preflight (below) → `package` → `vsce publish --packagePath …`. Needs `VSCE_PAT` in the environment. |

## Flags

- `build.mjs` — `--skip-worker` (reuse an existing `dist/worker/`),
  `--worker-only` (publish the worker, skip the TS build), `--configuration
  Debug` (default `Release`).
- `package.mjs` — `--skip-worker`, `--configuration <cfg>`, `--out <path>`.
- `publish.mjs` — `--dry-run` (full preflight + package, stop before the actual
  publish), `--allow-dirty` (skip the clean-tree check).

Pass flags through npm with `--`, e.g. `npm run package -- --skip-worker`.

## `publish` preflight

`npm run publish` refuses unless **all** of these hold:

1. `npm run doctor` reports no hard failure (toolchain, `EXTENSION_ID`, icon).
2. The git working tree is clean (override: `--allow-dirty`).
3. `package.json` `version` matches a `vscode-v<version>` tag on `HEAD`.
4. `CHANGELOG.md` has a `## <version>` heading.
5. `VSCE_PAT` is set in the environment.

The scripts never write the version or create the tag — that stays a manual,
reviewed step. See [`../../docs/vscode-extension-release.md`](../../docs/vscode-extension-release.md).

## Notes

- On Windows, spawning the `npm` / `npx` `.cmd` shims needs a shell, so you see
  one `DEP0190` deprecation line per `vsce` call. It is harmless and does not
  appear in CI (Linux, no shell) or affect the `WARNING` gate.
- `dotnet` and `git` are real executables and are spawned without a shell.
