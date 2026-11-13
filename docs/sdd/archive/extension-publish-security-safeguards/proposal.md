# Proposal: Medidas de Segurança para Empacotamento e Publicação da Extensão

## 1. Intent Block
```text
Intent Block:
Goal:        Garantir que a extensão VS Code nunca empacote ou publique segredos, código-fonte, símbolos com caminhos locais de desenvolvimento ou arquivos críticos/sensíveis.
Expectation: O artefato distribuível (VSIX) e o pipeline de publicação contenham exclusivamente os binários e assets essenciais de execução em produção, rejeitando preventivamente qualquer vazamento com falha imediata no empacotamento, no preflight e no CI.
Action:      Implementar defesas em múltiplas camadas: higienização do .vscodeignore e .gitignore, remoção de PDBs e metadados no build do worker, validação estrita em assertVsixContents, auditoria de segurança no doctor.mjs/publish.mjs, hook de pre-commit contra secrets e testes automatizados de segurança.
Result:      Risco zero de vazamento de segredos (.env, chaves, tokens), arquivos de código-fonte (.fs, .cs, .ts), símbolos de depuração (.pdb com caminhos locais), bancos locais (.db, .sqlite, .fsqnb) ou pacotes aninhados (.vsix) na extensão publicada.
```

## 2. Why (Motivação)
Durante a inspeção do processo de empacotamento (`npx @vscode/vsce ls` e `package.mjs`), foram identificados pontos de risco e lacunas de segurança:
1. **Símbolos de Depuração (.pdb)**: O `dotnet publish` em Release estava incluindo 12 arquivos `.pdb` em `dist/worker/`, expondo caminhos absolutos locais do sistema de arquivos do desenvolvedor e símbolos internos.
2. **Falta de Bloqueio Explícito no .vscodeignore**: Arquivos de segredos (`.env*`, `.pem`, `.key`, `.pfx`, `.token*`, `.secret*`), bancos de dados locais (`*.db`, `*.sqlite`, `*.sqlite3`), cadernos locais (`*.fsqnb`), código-fonte solto (`*.fs`, `*.cs`, `*.ts`) e artefatos de pacotes anteriores (`*.vsix`) não estavam explicitamente ignorados.
3. **Validação Superficial em `assertVsixContents`**: O script `assertVsixContents` validava apenas pastas `src/`, `test/`, `out/`, `node_modules/` e extensões `.test.ts`/`tsconfig*.json`, deixando de checar PDBs, arquivos de ambiente, chaves, código F#/C# solto e artefatos de bancos ou notebooks.
4. **Ausência de Scanner Preventivo no Preflight**: O script `doctor.mjs` checava dependências e ícone, mas não auditava a presença de segredos ou riscos de segurança no diretório antes de autorizar a publicação.

## 3. What (O Que Será Feito)
1. **Camada 1 - Bloqueio de Empacotamento (.vscodeignore e .gitignore)**:
   - Bloquear formalmente: `**/*.pdb`, `**/*.map`, `**/*.env*`, `**/*.{key,pem,cert,pfx,p12,secret,token,credentials}`, `**/*.{fs,cs,ts,tsx}`, `**/*.{fsproj,csproj,sln,slnx}`, `**/*.{db,sqlite,sqlite3,fsqnb,dump,bak}`, `**/*.vsix`, `**/*.{log,tmp}`, `.git*`, `.github/**`, `.githooks/**`, `.agents/**`, `.claude/**`, `.superpowers/**`.
   - Adicionar regras preventivas equivalentes no `editors/vscode/.gitignore` e no `.gitignore` raiz.

2. **Camada 2 - Limpeza de PDBs e Símbolos no Build do Worker (`scripts/build.mjs`)**:
   - Configurar o `dotnet publish` para desativar símbolos de debug em produção (`-p:DebugType=None -p:DebugSymbols=false`) e/ou expurgar arquivos `.pdb` e `.xml` de documentação residual em `dist/worker/`.

3. **Camada 3 - Assertions Rigorosas no VSIX (`scripts/lib.mjs: assertVsixContents`)**:
   - Adicionar rejeição fatal com lista de problemas para:
     - Qualquer arquivo `.pdb` ou `.map`.
     - Qualquer arquivo de código-fonte (`.fs`, `.cs`, `.ts`, exceto `dist/extension.js` e `media/renderer/index.js`).
     - Qualquer arquivo de segredo ou ambiente (`.env*`, `.key`, `.pem`, etc.).
     - Qualquer arquivo de banco de dados, notebook ou dump (`.db`, `.sqlite`, `.fsqnb`, etc.).
     - Qualquer pacote `.vsix` embutido recursivamente.
     - Qualquer arquivo de projeto (`.fsproj`, `.csproj`, `.sln*`).
     - Metadados de versionamento e agentes (`.git`, `.github`, `.agents`, etc.).

4. **Camada 4 - Checagem de Segurança no Doctor (`scripts/doctor.mjs` e `scripts/publish.mjs`)**:
   - Adicionar checagem bloqueante (hard check) no `doctor.mjs`: `Security: Secret and Sensitive Artifact Audit`, verificando a ausência de arquivos sensíveis soltos em `editors/vscode/`.

5. **Camada 5 - Hook Git Preventivo (`.githooks/pre-commit`)**:
   - Escanear arquivos staged contra padrões típicos de segredos (chaves privadas, `.env`, tokens expostos) antes de permitir commits.

6. **Camada 6 - Testes Automatizados de Segurança (`editors/vscode/test/packaging-security.test.ts`)**:
   - Testar unitariamente que `assertVsixContents` bloqueia cada classe de artefato proibido e aceita apenas a lista de arquivos legítimos de distribuição.

## 4. Scope (In / Out)
- **In Scope**:
  - Regras do `.vscodeignore` e `.gitignore`.
  - Scripts de build e empacotamento (`build.mjs`, `lib.mjs`, `doctor.mjs`, `publish.mjs`).
  - Hook de pré-commit `.githooks/pre-commit`.
  - Testes unitários de validação de empacotamento seguro.
  - Documentação de release (`docs/vscode-extension-release.md`).
- **Out of Scope**:
  - Alterações na lógica de execução de queries em tempo de execução no Worker F#.
  - Configurações de provedores externos de nuvem (Marketplace Azure PAT rotation).

## 5. Success Criteria
- [ ] `vsce ls` lista 0 arquivos `.pdb`, 0 arquivos `.env`, 0 arquivos `.fs`/`.cs`/`.ts` e 0 artefatos de dados/notebooks.
- [ ] `assertVsixContents` valida e rejeita ativamente qualquer inclusão acidental de PDBs, código-fonte ou segredos.
- [ ] `npm run doctor` inclui e aprova a checagem de integridade de segurança.
- [ ] `npm run package` gera o VSIX limpo e sem warnings.
- [ ] 100% dos testes unitários e de integração passam.

## 6. Impacted Artifacts
- `editors/vscode/.vscodeignore`
- `editors/vscode/.gitignore`
- `.gitignore`
- `editors/vscode/scripts/lib.mjs`
- `editors/vscode/scripts/build.mjs`
- `editors/vscode/scripts/doctor.mjs`
- `editors/vscode/scripts/publish.mjs`
- `.githooks/pre-commit`
- `docs/vscode-extension-release.md`
- `editors/vscode/test/packaging-security.test.ts`
