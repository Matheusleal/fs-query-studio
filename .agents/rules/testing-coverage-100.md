# Regra Obrigatória: 100% de Cobertura de Testes e Testes em Tudo que For Feito
# Mandatory Rule: 100% Test Coverage & Mandatory Tests on Everything Done

---

## 🇧🇷 Português

### 1. Princípio Inegociável: Testes em Tudo que For Feito
- **Nenhum código entra sem testes**: Qualquer alteração no repositório — seja uma nova funcionalidade, correção de bug, refatoração, ajuste arquitetural, conector ou manipulador do worker — **deve obrigatoriamente ser acompanhada por testes automatizados**.
- **Proibido código não testado**: Nenhuma entrega ou Pull Request pode conter arquivos, classes, funções, métodos ou branches que não sejam exercitados e validados por testes automatizados.
- **Eliminação de Código Inacessível / Morto**: Qualquer ramificação condicional ou código defensivo introduzido deve ser estritamente testável e testado. Se uma linha ou branch de código não puder ser exercitada por testes, o código deve ser simplificado e refatorado para remover o caminho inalcançável.

### 2. Meta Estrita de Cobertura: 100.00%
- **Cobertura de 100% por arquivo modificado ou criado**: Todos os arquivos de código-fonte (.fs, .ts, etc.) criados ou modificados devem atingir e manter **100.00% de cobertura de linhas**.
- Nenhuma entrega é considerada concluída se a cobertura em qualquer arquivo modificado for inferior a 100.00%.

### 3. Mínimo de 2 Testes Distintos por Cenário Possível
Para cada funcionalidade, método ou fluxo de alteração, devem ser escritos **no mínimo 2 testes automatizados distintos** para cada um dos seguintes cenários:
1. **Caminho Feliz (Happy Path)**: No mínimo 2 testes validando execuções com entradas nominais e esperadas.
2. **Variações de Entrada (Input Variations)**: No mínimo 2 testes cobrindo diferentes tipos de dados, formatos, encodings ou opções configuradas.
3. **Casos de Borda e Limites (Boundary & Edge Cases)**: No mínimo 2 testes cobrindo valores máximos, mínimos, coleções de um único elemento, paginação inicial/final, etc.
4. **Entradas Vazias e Nulas (Empty & Null Inputs)**: No mínimo 2 testes cobrindo strings vazias, sequências vazias, `None`, `null`, `DBNull.Value` ou `ValueOption.None`.
5. **Cenários de Erro e Exceções (Error & Exception Paths)**: No mínimo 2 testes validando tratamento gracioso de falhas, mensagens de erro informativas, cancelamento e timeouts.

### 4. Gates Obrigatórios de Validação Antes de Qualquer PR
Antes de abrir qualquer Pull Request (`gh pr create`), o agente deve executar e validar com 100% de sucesso (código de saída 0):
1. **Build de Release Completo**:
   ```powershell
   dotnet fsi .\build.fsx release
   ```
   Valida compilação Release do backend, compilação estrita TypeScript (`tsc --noEmit && node esbuild.mjs`) e empacotamento VSIX (`vsce package`).
2. **Testes em Container Linux (Docker)**:
   ```bash
   docker run --rm -v "${PWD}:/app" -w /app mcr.microsoft.com/dotnet/sdk:10.0 dotnet test tests/FSharpQueryStudio.Connectors.Tests/FSharpQueryStudio.Connectors.Tests.fsproj -c Release
   ```
   Garante compatibilidade total multiplataforma e zero falhas no Linux.
3. **Validação E2E Real de 100% dos Cadernos de Amostra (EN e PT)**:
   ```powershell
   node editors/vscode/test_all_samples_e2e.mjs
   ```
   Executa e valida ponta a ponta 100% das células de todos os 14 cadernos de exemplo (SQLite, CSV, Excel, XML, JSON, REST, MySQL, MariaDB, Oracle, PostgreSQL, SQL Server) com zero erros.
4. **Zero Testes Falhando, Travando ou Pendentes**: Todos os testes da suíte devem passar sem pendências.

---

## 🇺🇸 English

### 1. Inviolable Principle: Mandatory Tests on Everything Done
- **No code without tests**: Any change in the repository — whether a new feature, bug fix, refactoring, architectural adjustment, connector, or worker handler — **must strictly be accompanied by automated tests**.
- **Untested code is prohibited**: No delivery or Pull Request may contain files, classes, functions, methods, or branches that are not exercised and validated by automated tests.
- **Dead / Unreachable Code Elimination**: Any conditional branch or defensive check introduced must be strictly testable and tested. If a line or branch cannot be reached by tests, the code must be refactored and simplified to eliminate the unreachable path.

### 2. Strict Coverage Target: 100.00%
- **100% coverage per created or modified file**: Every source file (.fs, .ts, etc.) modified or created must achieve and maintain **100.00% line coverage**.
- No task or PR is considered complete if coverage on any modified file drops below 100.00%.

### 3. Minimum 2 Distinct Tests per Possible Scenario
For every feature, method, or changed behavior, **at least 2 distinct automated tests** must be written for each of the following scenarios:
1. **Happy Path**: At least 2 tests verifying standard, expected execution with nominal inputs.
2. **Input Variations**: At least 2 tests covering diverse data types, formats, encodings, or configurations.
3. **Boundary & Edge Cases**: At least 2 tests covering maximums, minimums, single-element collections, start/end pagination, etc.
4. **Empty & Null Inputs**: At least 2 tests covering empty strings, empty collections, `None`, `null`, `DBNull.Value`, or `ValueOption.None`.
5. **Error & Exception Paths**: At least 2 tests verifying graceful error handling, informative messages, cancellation, and timeouts.

### 4. Mandatory Validation Gates Before Opening Any PR
Before opening any Pull Request (`gh pr create`), the agent must execute and pass with 100% success (exit code 0):
1. **Full Release Build & Packaging**:
   ```powershell
   dotnet fsi .\build.fsx release
   ```
2. **Linux Container Tests (Docker)**:
   ```bash
   docker run --rm -v "${PWD}:/app" -w /app mcr.microsoft.com/dotnet/sdk:10.0 dotnet test tests/FSharpQueryStudio.Connectors.Tests/FSharpQueryStudio.Connectors.Tests.fsproj -c Release
   ```
3. **Real E2E Sample Validation Across 100% of Notebooks (EN and PT)**:
   ```powershell
   node editors/vscode/test_all_samples_e2e.mjs
   ```
4. **Zero Broken, Hanging, or Pending Tests**: All tests across the test suite must pass cleanly.
