# Change Proposal: Project Auto-Upgrader & .fsqs Workspace Schema v2

## Intent Block
```text
Intent Block:
Goal:        Mover definitivamente o arquivo `workspace.json` para o diretório `.fsqs/`, atualizar seu formato para incluir versionamento explícito (versão do schema e versão da extensão) e introduzir uma ferramenta/mecanismo de migração automática para aplicar breaking changes de forma transparente.
Expectation: Projetos legados que possuam `workspace.json` (ou pasta `generated/`) na raiz, ou que utilizem o formato JSON v1 (array simples de conexões), são automaticamente detectados e migrados para `<root>/.fsqs/workspace.json` com schema v2 e versionamento sem exigir intervenção manual ou quebrar fluxos de trabalho existentes.
Action:      (1) Definir o Schema v2 para `workspace.json` contendo `schemaVersion`, `extensionVersion` e `connections`; (2) Criar o componente `ProjectUpgrader` (motor de migração sequencial por etapas) na camada de Storage; (3) Integrar a execução automática do upgrader em operações de leitura/escrita de workspace e via requisição no protocolo do Worker; (4) Atualizar a inicialização de projetos e a extensão VS Code para reconhecer e registrar migrações automáticas.
Result:      A raiz de todo projeto F# Query Studio permanece limpa; quebras de compatibilidade entre versões de schema são atualizadas automaticamente; e o sistema possui uma base extensível para futuras migrações entre versões.
```

## Contexto & Motivação

1. **Isolamento na pasta `.fsqs`**:
   - Anteriormente, o `workspace.json` e a pasta `generated/` podiam ser gerados ou mantidos na raiz do projeto. Embora a pasta `.fsqs` tenha sido introduzida recentemente, projetos existentes e fluxos sem `.fsqs` pré-existente ainda podiam criar ou manter arquivos soltos na raiz.
   - Todo projeto agora deve armazenar seus dados internos exclusivamente dentro de `.fsqs/`.

2. **Versionamento do `workspace.json` (Schema v2)**:
   - Atualmente, `workspace.json` consiste apenas em um array JSON plano (`[ { ... } ]`).
   - Esse formato não contém informações sobre:
     - Qual versão da extensão gerou o arquivo.
     - Qual versão do schema do workspace está sendo utilizada.
   - Sem versionamento, torna-se impossível detectar e orquestrar breaking changes ou migrações de dados entre versões futuras.
   - O novo formato adota um objeto estruturado:
     ```json
     {
       "schemaVersion": 2,
       "extensionVersion": "0.3.2",
       "updatedAt": "2026-10-28T20:00:00Z",
       "connections": [ ... ]
     }
     ```

3. **Ferramenta de Atualização Automática de Projetos (Project Upgrader)**:
   - Uma arquitetura modular de migrações (`IMigrationStep`) que executa passos sequenciais:
     - **Migração v1 -> v2**:
       - Move `<root>/workspace.json` -> `<root>/.fsqs/workspace.json`.
       - Move `<root>/generated/` -> `<root>/.fsqs/generated/`.
       - Converte o formato legado (array de conexões) para o Schema v2 (objeto com `schemaVersion`, `extensionVersion`, `connections`).
     - Extensível para versões futuras (`v2 -> v3`, etc.) quando novas breaking changes surgirem.
   - **Execução Automática**:
     - O upgrader roda de forma transparente e atômica ao abrir/acessar um projeto, ao ler ou escrever conexões, e via endpoint de comando/worker (`workspace/upgrade`).

---

## Escopo

### No Escopo (In Scope)
- Criação do tipo `WorkspaceConfiguration` e suporte bidirecional (leitura tolerante de v1 array e v2 objeto; escrita sempre em v2).
- Módulo `ProjectUpgrader` com pipeline extensível de migrações (`IMigrationStep`).
- Migração automática de arquivos estruturais:
  - `<root>/workspace.json` -> `<root>/.fsqs/workspace.json`
  - `<root>/generated/` -> `<root>/.fsqs/generated/`
- Execução automática do upgrader em:
  - `Workspace.readConnections` / `Workspace.writeConnections` / `Workspace.workspaceFilePath`.
  - `workspace/initEmptyProject` (já gera o template v2 com versionamento).
  - Novo método no worker `workspace/upgrade` para invocação explícita e auditoria.
- Atualização do VS Code Extension:
  - Detecção e notificação/log de projetos atualizados.
  - Atualização dos testes de integração e unitários do VS Code e .NET.

### Fora de Escopo (Out of Scope)
- Migração de credenciais no OS Keychain (as referências `SecretReference` permanecem opacas e compatíveis).
- Alteração no formato do arquivo de notebook (`.fsqnb`).

---

## Critérios de Sucesso
1. Projetos com `workspace.json` antigo na raiz são migrados automaticamente para `.fsqs/workspace.json` no novo formato com `schemaVersion: 2` e `extensionVersion`.
2. O arquivo `<root>/workspace.json` e a pasta `<root>/generated/` antigos são limpos da raiz após a migração bem-sucedida.
3. Projetos com `.fsqs/workspace.json` no formato v1 (array) são atualizados em disco para o Schema v2 mantendo 100% das conexões existentes intactas.
4. `Workspace.readConnections` lê tanto arquivos legados v1 quanto v2 sem erros.
5. Novas inicializações de projeto (`workspace/initEmptyProject`) criam `.fsqs/workspace.json` já em conformidade com o Schema v2.
6. 100% dos testes unitários e de integração (.NET e Vitest) passam com sucesso.

---

## Artefatos Impactados
- `src/FSharpQueryStudio.Storage/Workspace.fs`
- `src/FSharpQueryStudio.Storage/ProjectUpgrader.fs` (novo)
- `src/FSharpQueryStudio.Storage/FSharpQueryStudio.Storage.fsproj`
- `src/FSharpQueryStudio.Protocol/Payloads.fs`
- `src/FSharpQueryStudio.Worker/Handlers.fs`
- `tests/FSharpQueryStudio.Storage.Tests/Tests.fs`
- `tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs`
- `editors/vscode/src/worker/local-workspace.integration.test.ts`
- `editors/vscode/src/connections/commands.ts`
- `editors/vscode/src/extension.ts`
