# Tasks: Dependabot Security Upgrades and CI Stabilization

## Milestone 1: Dependency Upgrades & Audit
- [x] TASK-101: Upgrade `esbuild`, `brace-expansion`, `vite`, `serialize-javascript`, `tinypool` and `vitest` in `editors/vscode`, resolving vulnerabilities while maintaining Node 20 / `@types/node` compatibility.
- [x] TASK-102: Run `npm audit` and verify vulnerability resolution status in `editors/vscode`.

## Milestone 2: Frontend & Extension Validation
- [x] TASK-201: Execute `npm ci`, `npm run compile`, `npm test` and `npm run package` in `editors/vscode`.
- [x] TASK-202: Run VS Code extension integration tests (`npm run test:vscode` / `npx vitest`).

## Milestone 3: Backend & E2E Validation (Local and Linux Container)
- [x] TASK-301: Validate all backend test projects locally via `dotnet test`.
- [x] TASK-302: Validate `sample_demo` E2E notebooks (`dotnet test ... --filter "FullyQualifiedName~sample"` and `npx vitest run src/worker/sample-notebook.integration.test.ts`).
- [x] TASK-303: Validate test execution in Linux container (`docker run`).

## Milestone 4: Pull Request Readiness
- [x] TASK-401: Commit changes and verify git cleanliness.
- [x] TASK-402: Push branch and create Pull Request via `gh pr create`.
