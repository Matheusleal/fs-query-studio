# Diretrizes para Agentes de IA / AI Agent Guidelines

## Regra Estrita: Git & Branch Protection
- **NUNCA faça commit ou push diretamente na branch `main` ou `master`**.
- Toda e qualquer alteração no código deve ser executada em uma feature/fix branch (ex: `feature/...`, `fix/...`, `chore/...`).
- O CI é acionado apenas quando um Pull Request é criado ou atualizado. Branches sem PR não executam o CI.
- **REGRA OBRIGATÓRIA PARA ABERTURA DE PR**:
  - O agente **NUNCA** deve abrir um Pull Request (`gh pr create`) antes de concluir e validar integralmente todas as alterações.
  - O Pull Request **SÓ PODE SER CRIADO** quando:
    1. A implementação estiver 100% concluída.
    2. Os testes forem executados e passarem com 100% de sucesso localmente e no container Linux (`docker run ...`).
    3. Não houver nenhum teste quebrado, travando (hang) ou pendente.
  - Commits intermediários e testes devem ocorrer exclusivamente na branch local/remota **sem PR**.
  - O PR é o último passo da entrega, servindo apenas como gate final de merge e validação do CI do GitHub.

- O fluxo obrigatório para toda alteração é:
  1. Criar e alternar para uma nova branch (`git checkout -b feature/...`).
  2. Implementar as mudanças.
  3. Comitar na branch de trabalho (`git commit`).
  4. Executar e validar todos os testes (local e container Linux).
  5. Se houver falhas, corrigir e recomitar na branch até que tudo passe.
  6. Enviar para o repositório remoto (`git push -u origin feature/...`).
  7. **Somente após tudo testado e funcionando**: Abrir o Pull Request via GitHub CLI (`gh pr create`).
  8. Aguardar o usuário ou a aprovação/merge do PR.

## Regra Obrigatória: Validação do `sample_demo` em Toda Alteração
- Sempre que **qualquer alteração** for feita no backend, nos conectores ou na extensão VS Code, é **obrigatório** validar ponta a ponta os notebooks `sample_demo.fsqnb` e `sample_demo_pt.fsqnb` (incluindo SQLite, CSV, Excel `.xlsx` e REST `https://` do GitHub, bem como recriação sobre um workspace existente):
  - Backend: `dotnet test tests/FSharpQueryStudio.Worker.Tests --filter "FullyQualifiedName~sample"`
  - Frontend/Worker E2E: `npx vitest run src/worker/sample-notebook.integration.test.ts` (após publicar o worker para `editors/vscode/dist/worker` ou via `./scripts/run-all-tests.ps1`).
  - Quando a extensão estiver instalada localmente em `~/.vscode/extensions/fs-query-studio.fs-query-studio-*`, sincronizar os artefatos compilados (`dist/`) para garantir que o ambiente local do VS Code nunca execute código defasado.

---

## Strict Rule: Git & Branch Protection (English)
- **NEVER commit or push directly to the `main` or `master` branch**.
- Every code change must be executed on a feature/fix branch (e.g., `feature/...`, `fix/...`, `chore/...`).
- CI is triggered only when a Pull Request is created or updated. Branches without a PR do not run CI.
- **MANDATORY RULE FOR OPENING A PR**:
  - The agent must **NEVER** open a Pull Request (`gh pr create`) before completely finishing and validating all changes.
  - A Pull Request **MAY ONLY BE CREATED** when:
    1. Implementation is 100% complete.
    2. All tests have been executed and pass with 100% success locally and in the Linux container (`docker run ...`).
    3. There are no broken, hanging, or pending tests.

## Mandatory Rule: `sample_demo` Validation on Every Change (English)
- Whenever **any change** is made to the backend, connectors, or VS Code extension, end-to-end validation of `sample_demo.fsqnb` and `sample_demo_pt.fsqnb` (covering SQLite, CSV, Excel `.xlsx`, and secure GitHub `https://` REST endpoints, including re-creation over an existing workspace) is **mandatory**:
  - Backend: `dotnet test tests/FSharpQueryStudio.Worker.Tests --filter "FullyQualifiedName~sample"`
  - Frontend/Worker E2E: `npx vitest run src/worker/sample-notebook.integration.test.ts` (after publishing the worker to `editors/vscode/dist/worker` or via `./scripts/run-all-tests.ps1`).
  - When the extension is installed locally under `~/.vscode/extensions/fs-query-studio.fs-query-studio-*`, sync the built `dist/` artifacts so the local VS Code environment never runs stale bundles.
