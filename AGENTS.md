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

## Regra Obrigatória: Validação E2E Real dos Samples em Toda Entrega (Notebooks EN e PT)
- Sempre que **qualquer alteração** for finalizada no backend, conectores ou na extensão VS Code (que não seja estritamente alteração de documentação), é **estritamente obrigatório**:
  1. Inicializar e recriar os projetos de amostra básicos e avançados (`workspace/createSampleData` e `workspace/createAdvancedSamples` com `language: "all"`, `engines: ["all"]`).
  2. Executar e validar ponta a ponta **100% das células de todos os cadernos** (tanto em Inglês quanto em Português) sem nenhum erro:
     - Cadernos Básicos: `sample_demo.fsqnb` e `sample_demo_pt.fsqnb` (SQLite, CSV, Excel, XML, JSON, REST).
     - Cadernos de Bancos de Dados: `sample_mysql.fsqnb`, `sample_mariadb.fsqnb`, `sample_oracle.fsqnb`, `sample_postgresql.fsqnb`, `sample_sqlserver.fsqnb` (e seus equivalentes `*_pt.fsqnb`).
  3. Validação automatizada via comando:
     ```powershell
     node editors/vscode/test_all_samples_e2e.mjs
     # ou no diretório editors/vscode:
     npm run test:samples
     ```
     O script executa os testes em um diretório temporário isolado e, no fim da execução, remove compulsoriamente os containers Docker dos bancos de dados e o diretório temporário.
  4. Backend: `dotnet test tests/FSharpQueryStudio.Worker.Tests --filter "FullyQualifiedName~sample"`
  5. Frontend/Worker E2E: `npx vitest run src/worker/sample-notebook.integration.test.ts`
  6. Nenhuma entrega ou PR pode ser concluído se houver qualquer célula falhando ou quebrando.
  7. Quando a extensão estiver instalada localmente em `~/.vscode/extensions/fs-query-studio.fs-query-studio-*`, sincronizar os artefatos compilados (`dist/`) e/ou reinstalar o VSIX compilado via `code --install-extension editors/vscode/fs-query-studio.vsix --force`.

## Regra Obrigatória: 100% de Cobertura de Testes e Testes em Tudo que For Feito
- **Pré-requisito estrito para toda nova funcionalidade, alteração, bugfix ou refatoração**:
  1. **Testes obrigatórios em tudo que for feito**: Nenhum código novo ou alterado pode ser introduzido sem testes automatizados que exercitem integralmente o comportamento implementado. Código não testado é estritamente proibido.
  2. **Mínimo de 2 testes distintos para cada cenário possível**: Para cada cenário de uso da funcionalidade (caminho feliz, variações de entrada, casos de borda/limites, entradas vazias/nulas e cenários de erro/exceção), devem ser escritos **no mínimo 2 testes automatizados distintos** validando o comportamento esperado.
  3. **Manutenção de 100.00% de cobertura por classe/arquivo (`100.00%`)**: Todas as classes e arquivos de código-fonte modificados ou criados devem manter ou atingir cobertura de linhas de **100.00%**. Nenhuma nova funcionalidade ou PR pode ser concluído se qualquer arquivo modificado ficar abaixo de 100% de cobertura.
  4. **Eliminação de código inalcançável**: Qualquer ramificação condicional ou código defensivo deve ser estritamente testável e testado; código morto ou inatingível não é permitido e deve ser refatorado.

## Regra de Criação de Queries: Guia Canônico `fsql` para Agentes de IA
- Todo código de consulta de dados deve seguir rigorosamente as especificações em [`docs/FSQL_AI_AGENT_GUIDE.md`](file:///docs/FSQL_AI_AGENT_GUIDE.md).
- Use **exclusivamente** a computation expression `fsql { ... }` para queries e subqueries (`whereExists`, `whereNotExists`, `having`, `crossApply`, `lateral`, escalares na projeção).
- **Nunca** gere templates em strings SQL crus e **nunca** misture a sintaxe antiga `query { ... }` com `fsql`.

---

## Mandatory Rule: 100% Coverage (100.00%) & Mandatory Tests for Everything Done (English)
- **Strict prerequisite for every new feature, behavioral change, bugfix, or refactoring**:
  1. **Mandatory tests on everything done**: No new or changed code may be introduced without automated tests covering 100% of the implemented behavior. Untested code is strictly prohibited.
  2. **At least 2 distinct tests for every possible scenario**: Every scenario of a new feature or change (happy path, input variations, boundary/edge cases, empty/null inputs, and error/exception paths) **must** be covered by **at least 2 distinct automated tests**.
  3. **Strict 100.00% line coverage per class/file (`100.00%`)**: Every class and source file modified or introduced in the repository must maintain or reach **100.00%** line coverage. No delivery or Pull Request may be opened if any modified file drops below 100.00% coverage.
  4. **Elimination of unreachable code**: Any conditional branch or defensive check introduced must be strictly testable and tested; dead or unreachable code is not permitted and must be refactored.

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

## Mandatory Rule: Real E2E Sample Validation on Every Delivery (EN and PT Notebooks)
- Whenever **any change** is finalized in the backend, connectors, or VS Code extension (that is not strictly documentation-only), it is **strictly mandatory** to:
  1. Initialize and recreate basic and advanced sample projects (`workspace/createSampleData` and `workspace/createAdvancedSamples` with `language: "all"`, `engines: ["all"]`).
  2. Execute and validate end-to-end **100% of all cells in all notebooks** (both English and Portuguese) with zero errors:
     - Standard Notebooks: `sample_demo.fsqnb` and `sample_demo_pt.fsqnb` (SQLite, CSV, Excel, XML, JSON, REST).
     - Database Engine Notebooks: `sample_mysql.fsqnb`, `sample_mariadb.fsqnb`, `sample_oracle.fsqnb`, `sample_postgresql.fsqnb`, `sample_sqlserver.fsqnb` (and their `*_pt.fsqnb` counterparts).
  3. Automated validation command:
     ```powershell
     node editors/vscode/test_all_samples_e2e.mjs
     # or inside editors/vscode:
     npm run test:samples
     ```
     The script executes all tests in an isolated temporary directory and unconditionally removes all database Docker containers and the temporary directory upon completion.
  4. Backend: `dotnet test tests/FSharpQueryStudio.Worker.Tests --filter "FullyQualifiedName~sample"`
  5. Frontend/Worker E2E: `npx vitest run src/worker/sample-notebook.integration.test.ts`
  6. No delivery or PR may be completed if any notebook cell fails or produces errors.
  7. When the extension is installed locally under `~/.vscode/extensions/fs-query-studio.fs-query-studio-*`, sync the built artifacts (`dist/`) and/or reinstall the built VSIX via `code --install-extension editors/vscode/fs-query-studio.vsix --force`.
