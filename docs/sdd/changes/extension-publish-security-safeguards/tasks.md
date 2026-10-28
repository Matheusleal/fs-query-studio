# Task Ledger: Extension Packaging and Publish Security Safeguards

## Tasks

- [x] **TASK-01: Update `.vscodeignore` and `.gitignore` with Strict Security Exclusions**
  - **Goal**: Prevent `.pdb`, `.map`, `.env*`, `.fsqnb`, `.sqlite`, `.db`, source files (`.fs`, `.cs`, `.ts`), `.vsix`, and private keys from being packaged or tracked.
  - **Related Requirements**: SEC-PKG-01, SEC-PKG-02, SEC-PKG-03
  - **Done When**: `editors/vscode/.vscodeignore`, `editors/vscode/.gitignore`, and root `.gitignore` contain explicit ignore patterns for all sensitive files.
  - **Status**: Completed

- [x] **TASK-02: Worker Build Cleansing in `build.mjs`**
  - **Goal**: Automatically strip `.pdb` and documentation `.xml` files from `editors/vscode/dist/worker/` during worker publishing.
  - **Related Requirements**: MOD-PKG-01, SEC-PKG-01
  - **Done When**: `build.mjs` purges all `.pdb` files from `workerOutDir` after `dotnet publish`, preventing symbols with local dev paths from reaching the package directory.
  - **Status**: Completed

- [x] **TASK-03: Strict Security Assertions in `assertVsixContents` (`lib.mjs`)**
  - **Goal**: Implement multi-pattern forbidden-files assertions in `assertVsixContents`.
  - **Related Requirements**: SEC-PKG-04
  - **Done When**: Any VSIX package containing `.pdb`, source files (`.fs`, `.cs`, `.ts`), `.env*`, keys, databases (`.db`, `.sqlite`), notebooks (`.fsqnb`), or nested `.vsix` triggers an immediate fatal error during packaging.
  - **Status**: Completed

- [x] **TASK-04: Preflight Security Audit in `doctor.mjs` & `publish.mjs`**
  - **Goal**: Add a hard blocker check in `doctor.mjs` for untracked secrets and verify packaging security before allowing publish.
  - **Related Requirements**: SEC-PKG-05
  - **Done When**: `npm run doctor` executes a security preflight audit and reports `✓` when clean, blocking publish if any secret file is found in the extension workspace.
  - **Status**: Completed

- [x] **TASK-05: Pre-commit Hook Secret Scanning (`.githooks/pre-commit`)**
  - **Goal**: Prevent committing sensitive credentials, private keys, or `.env` files into git.
  - **Related Requirements**: SEC-PKG-06
  - **Done When**: Staging a `.env` file or private key causes `.githooks/pre-commit` to abort the commit with an explanatory error.
  - **Status**: Completed

- [x] **TASK-06: Automated Unit Tests for Packaging Security**
  - **Goal**: Validate `assertVsixContents` security assertions against positive and negative test cases.
  - **Related Requirements**: SEC-PKG-01, SEC-PKG-02, SEC-PKG-03, SEC-PKG-04
  - **Done When**: Vitest runs `test/packaging-security.test.ts` verifying that forbidden file categories are caught and that valid listings pass.
  - **Status**: Completed

- [x] **TASK-07: Documentation & Full Verification Suite**
  - **Goal**: Document the security guardrails in `docs/vscode-extension-release.md` and execute end-to-end verification (`npm run doctor`, `npm run package`, `npm test`).
  - **Related Requirements**: SEC-PKG-01 through SEC-PKG-06, MOD-PKG-01
  - **Done When**: Runbook is updated, `npm run doctor` is clean, `npm run package` succeeds with 0 warnings and 0 PDBs/secrets/source files, and all tests pass.
  - **Status**: Completed
