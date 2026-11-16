# Diretrizes para Agentes de IA / AI Agent Guidelines

## Regra Estrita: Git & Branch Protection
- **NUNCA faça commit ou push diretamente na branch `main` ou `master`**.
- Toda e qualquer alteração no código deve ser executada em uma feature/fix branch (ex: `feature/...`, `fix/...`, `chore/...`).
- O CI é acionado apenas quando um Pull Request é criado ou atualizado. Branches sem PR não executam o CI.
- **REGRA OBRIGATÓRIA PARA ABERTURA DE PR**:
  - O agente **NUNCA** deve abrir um Pull Request (`gh pr create`) antes de concluir e validar integralmente todas as alterações.
  - O Pull Request **SÓ PODE SER CRIADO** quando:
    1. A implementação estiver 100% concluída.
    2. **Validação de Build & Release completa**: O comando `dotnet fsi .\build.fsx release` foi executado e passou com 100% de sucesso (código de saída 0), validando a compilação Release do backend, a compilação TypeScript da extensão (`tsc --noEmit && node esbuild.mjs`), e o empacotamento VSIX (`vsce package`).
    3. Os testes forem executados e passarem com 100% de sucesso localmente e no container Linux (`docker run ...`).
    4. Não houver nenhum teste quebrado, travando (hang) ou pendente.
  - Commits intermediários e testes devem ocorrer exclusivamente na branch local/remota **sem PR**.
  - O PR é o último passo da entrega, servindo apenas como gate final de merge e validação do CI do GitHub.

- O fluxo obrigatório para toda alteração é:
  1. Criar e alternar para uma nova branch (`git checkout -b feature/...`).
  2. Implementar as mudanças.
  3. Comitar na branch de trabalho (`git commit`).
  4. Executar e validar todos os testes (local e container Linux).
  5. Executar o build completo e empacotamento: `dotnet fsi .\build.fsx release` (e opcionalmente testar instalação: `code --install-extension editors/vscode/fs-query-studio.vsix --force`).
  6. Se houver falhas, corrigir e recomitar na branch até que tudo passe.
  7. Enviar para o repositório remoto (`git push -u origin feature/...`).
  8. **Somente após tudo testado, construído e funcionando**: Abrir o Pull Request via GitHub CLI (`gh pr create`).
  9. Aguardar o usuário ou a aprovação/merge do PR.

## Regra Obrigatória: Validação de Build de Release & Empacotamento VSIX
- Antes de abrir qualquer Pull Request ou considerar qualquer alteração concluída, é **estritamente obrigatório** executar o build completo via:
  ```powershell
  dotnet fsi .\build.fsx release
  ```
- Este comando valida a cadeia completa de ponta a ponta:
  1. Compilação de todos os projetos .NET em modo Release.
  2. Publicação do Worker para `editors/vscode/dist/worker/`.
  3. Cópia e sincronização da documentação para a extensão.
  4. Checagem estrita de tipos do TypeScript na extensão (`tsc --noEmit`) e bundle com `esbuild`.
  5. Empacotamento do arquivo `editors/vscode/fs-query-studio.vsix` e verificação de segurança de conteúdo (`vsce ls`).
- Nunca abra um PR se `dotnet fsi .\build.fsx release` apresentar qualquer erro ou falha.

## Regra Obrigatória: Validação do `sample_demo` em Toda Alteração
- Sempre que **qualquer alteração** for feita no backend, nos conectores ou na extensão VS Code, é **obrigatório** validar ponta a ponta os notebooks `sample_demo.fsqnb` e `sample_demo_pt.fsqnb` (incluindo SQLite, CSV, Excel `.xlsx` e REST `https://` do GitHub, bem como recriação sobre um workspace existente):
  - Backend: `dotnet test tests/FSharpQueryStudio.Worker.Tests --filter "FullyQualifiedName~sample"`
  - Frontend/Worker E2E: `npx vitest run src/worker/sample-notebook.integration.test.ts` (após publicar o worker para `editors/vscode/dist/worker` ou via `./scripts/run-all-tests.ps1`).
  - Quando a extensão estiver instalada localmente em `~/.vscode/extensions/fs-query-studio.fs-query-studio-*`, sincronizar os artefatos compilados (`dist/`) para garantir que o ambiente local do VS Code nunca execute código defasado.

## Regra de Criação de Queries: Guia Canônico `fsql` para Agentes de IA
- Todo código de consulta de dados deve seguir rigorosamente as especificações em [`docs/FSQL_AI_AGENT_GUIDE.md`](file:///docs/FSQL_AI_AGENT_GUIDE.md).
- Use **exclusivamente** a computation expression `fsql { ... }` para queries e subqueries (`whereExists`, `whereNotExists`, `having`, `crossApply`, `lateral`, escalares na projeção).
- **Nunca** gere templates em strings SQL crus e **nunca** misture a sintaxe antiga `query { ... }` com `fsql`.

---

## Strict Rule: Git & Branch Protection (English)
- **NEVER commit or push directly to the `main` or `master` branch**.
- Every code change must be executed on a feature/fix branch (e.g., `feature/...`, `fix/...`, `chore/...`).
- CI is triggered only when a Pull Request is created or updated. Branches without a PR do not run CI.
- **MANDATORY RULE FOR OPENING A PR**:
  - The agent must **NEVER** open a Pull Request (`gh pr create`) before completely finishing and validating all changes.
  - A Pull Request **MAY ONLY BE CREATED** when:
    1. Implementation is 100% complete.
    2. **Full Build & Release Validation**: `dotnet fsi .\build.fsx release` has been executed and passed with 100% success (exit code 0), verifying backend Release build, extension TypeScript compilation (`tsc --noEmit && node esbuild.mjs`), and VSIX packaging (`vsce package`).
    3. All tests have been executed and pass with 100% success locally and in the Linux container (`docker run ...`).
    4. There are no broken, hanging, or pending tests.

## Mandatory Rule: Full Release Build & VSIX Packaging Validation (English)
- Before opening any Pull Request or marking any change complete, it is **strictly mandatory** to run the full release build via:
  ```powershell
  dotnet fsi .\build.fsx release
  ```
- This command verifies the entire pipeline end-to-end:
  1. Release compilation of all .NET projects.
  2. Worker publication to `editors/vscode/dist/worker/`.
  3. Documentation sync to the extension.
  4. Strict TypeScript type-checking (`tsc --noEmit`) and bundling with `esbuild`.
  5. Packaging `editors/vscode/fs-query-studio.vsix` and running package security checks (`vsce ls`).
- Never open a PR if `dotnet fsi .\build.fsx release` reports any errors or failures.

## Mandatory Rule: `sample_demo` Validation on Every Change (English)
- Whenever **any change** is made to the backend, connectors, or VS Code extension, end-to-end validation of `sample_demo.fsqnb` and `sample_demo_pt.fsqnb` (covering SQLite, CSV, Excel `.xlsx`, and secure GitHub `https://` REST endpoints, including re-creation over an existing workspace) is **mandatory**:
  - Backend: `dotnet test tests/FSharpQueryStudio.Worker.Tests --filter "FullyQualifiedName~sample"`
  - Frontend/Worker E2E: `npx vitest run src/worker/sample-notebook.integration.test.ts` (after publishing the worker to `editors/vscode/dist/worker` or via `./scripts/run-all-tests.ps1`).
  - When the extension is installed locally under `~/.vscode/extensions/fs-query-studio.fs-query-studio-*`, sync the built `dist/` artifacts so the local VS Code environment never runs stale bundles.
