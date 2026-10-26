# Subagente: dead-code-cleaner
**Missão**: Eliminar código morto, arquivos órfãos, tipos de mensagens e testes legados que sobraram da interface desktop Avalonia UI.

## Escopo de Atuação
1. **Remoção de Arquivos e Tipos Órfãos**:
   - `src/FSharpQueryStudio.Protocol/UiState.fs` e `src/FSharpQueryStudio.Storage/UiState.fs`
   - `src/FSharpQueryStudio.Protocol/EditorRequest.fs`, `src/FSharpQueryStudio.Protocol/HostNotification.fs`
   - `EditorEnvelope` em `src/FSharpQueryStudio.Protocol/Frames.fs`
   - `src/FSharpQueryStudio.Logging/LogFileReader.fs` e `src/FSharpQueryStudio.Logging/DiagnosticBundle.fs`
   - `src/FSharpQueryStudio.Security/OriginDetection.fs`
   - `src/FSharpQueryStudio.Storage/FsProj.fs`
   - `src/FSharpQueryStudio.Storage/PackageApproval.fs`, `PackageLock.fs`, `Packages.fs` e `src/FSharpQueryStudio.Protocol/Packages.fs`
   - `src/FSharpQueryStudio.Protocol/Trust.fs` e `src/FSharpQueryStudio.Storage/Trust.fs`
2. **Remoção de Testes Órfãos**:
   - `tests/FSharpQueryStudio.Storage.Tests/UiStateTests.fs`, `FsProjTests.fs`, `TrustTests.fs`, `PackageLockTests.fs`, `PackagesTests.fs`
   - `tests/FSharpQueryStudio.Integration.Tests/PackageApprovalTests.fs`
   - `tests/FSharpQueryStudio.Security.Tests/OriginDetectionTests.fs`
   - `tests/FSharpQueryStudio.Logging.Tests/DiagnosticBundleTests.fs`, `LogFileReaderTests.fs`
   - Testes pontuais de `EditorEnvelope` e `ApprovedPackage` em `tests/FSharpQueryStudio.Protocol.Tests/Tests.fs`
3. **Limpeza de Diretórios Fantasmas**:
   - Excluir diretórios vazios `src/FSharpQueryStudio.Analysis` e `tests/FSharpQueryStudio.Analysis.Tests`.
4. **Ajuste de Referências em `.fsproj`**:
   - Atualizar os itens `<Compile Include="..." />` nos respectivos projetos para remover os arquivos deletados.

## Validação Obrigatória
- Executar `dotnet build FSharpQueryStudio.slnx` garantindo zero erros (`TreatWarningsAsErrors=true`).
- Executar `dotnet test FSharpQueryStudio.slnx` garantindo que todos os testes ativos continuem passando.
