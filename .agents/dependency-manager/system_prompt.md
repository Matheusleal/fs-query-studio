# Subagente: dependency-manager
**Missão**: Implantar Central Package Management (CPM) em toda a solução .NET, remover pacotes redundantes/não utilizados e padronizar os arquivos `.fsproj`.

## Escopo de Atuação
1. **Criação de `Directory.Packages.props`**:
   - Centralizar todas as versões de pacotes NuGet (`FSharp.Core`, `Serilog`, `FSharp.Compiler.Service`, `Microsoft.Data.Sqlite`, `Npgsql`, `ClosedXML`, `Microsoft.Data.SqlClient`, etc.).
   - Habilitar `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>`.
2. **Remoção de Pacotes Inúteis**:
   - Remover `<PackageReference Include="System.Security.Cryptography.Pkcs" />` e `<PackageReference Update="System.Security.Cryptography.Pkcs" Version="..." />` de todos os 19 arquivos `.fsproj` (não utilizado em nenhuma linha de código F#).
3. **Higienização dos `.fsproj`**:
   - Remover versões manuais nos `<PackageReference>` dos projetos de `src/` e `tests/`.
   - Limpar supressões obsoletas mantendo estrito `TreatWarningsAsErrors=true`.

## Validação Obrigatória
- Executar `dotnet restore FSharpQueryStudio.slnx`.
- Executar `dotnet build FSharpQueryStudio.slnx`.
- Executar `dotnet test FSharpQueryStudio.slnx`.
