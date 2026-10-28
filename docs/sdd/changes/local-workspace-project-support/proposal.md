# Proposal: Suporte a Projetos em Branco e Escopo de Conexão Local vs Global

## 1. Intent Block
```text
Intent Block:
Goal:        Permitir que usuários inicializem projetos em branco e escolham explicitamente o escopo (Local vs Global) ao adicionar novas conexões.
Expectation: Usuários com um projeto aberto no VS Code podem decidir se uma nova conexão pertence apenas àquele repositório/pasta ou ao catálogo global da máquina, mantendo arquivos workspace.json limpos e isolados.
Action:      Criar o comando 'fs-query-studio.initEmptyProject' e estender o fluxo de 'connection/register' no VS Code e no Worker para suportar 'scope' (Local ou Global) e diretório de destino.
Result:      Projetos locais possuem seu próprio workspace.json atualizado dinamicamente; conexões locais geram entidades isoladas sem poluir o catálogo global.
```

## 2. Why (Motivação)
Atualmente, a criação de conexões via interface do VS Code (`fs-query-studio.connections.add`) sempre registra no catálogo global do usuário (`%LOCALAPPDATA%\FSharpQueryStudio\workspace.json`). Embora o worker já suporte a resolução de `workspace.json` local por pasta, não existe uma forma automatizada de:
1. Criar um projeto em branco (com `workspace.json` vazio inicializado e estrutura pronta).
2. Escolher se uma nova conexão adicionada pela interface deve ser salva no projeto local ativo ou no catálogo global.

## 3. What (O Que Será Feito)
1. **Comando para Criar Projeto em Branco (`fs-query-studio.initEmptyProject`)**:
   - Cria um arquivo `workspace.json` na raiz da pasta do workspace aberto (ou solicita pasta caso nenhuma esteja aberta).
   - Cria um notebook inicial opcional (ex: `notebook.fsqnb` em branco com célula de exemplo comentada).
   - Inicializa a pasta `generated/` e atualiza a visualização de conexões.
2. **Seleção de Escopo na Criação de Conexão (Local vs Global)**:
   - No fluxo de adicionar conexão (`fs-query-studio.connections.add`), se houver um projeto aberto (pasta com `workspace.json` ou workspace folder ativo), exibir uma etapa no QuickPick/formulário perguntando:
     - 📁 **Local (Projeto Atual)**: Salva no `workspace.json` da pasta do projeto.
     - 🌐 **Global (Máquina / Todas as Janelas)**: Salva no `%LOCALAPPDATA%\FSharpQueryStudio\workspace.json`.
   - Se não houver workspace aberto, salva automaticamente no Global.
3. **Suporte no Worker (`connection/register`)**:
   - O método `connection/register` do Worker passará a aceitar um parâmetro opcional `targetDir` / `workspaceDir`.
   - Se fornecido, registra no `workspace.json` daquele diretório e aciona a geração de tipos (`ensureGenerated`) naquele diretório específico.
4. **Listagem e Árvore de Conexões**:
   - A árvore de conexões do VS Code exibirá as conexões ativas (combinando conexões locais do projeto aberto e/ou indicando o escopo).

## 4. Scope (In / Out)
- **In Scope**:
  - Novo comando de inicialização de projeto em branco no VS Code.
  - Prompt de escopo (Local vs Global) no wizard de adição de conexão.
  - Atualização do handler `connection/register` no Worker F#.
  - Testes unitários e de integração no TypeScript (Vitest) e no .NET.
- **Out of Scope**:
  - Sincronização em nuvem de conexões.
  - Gerenciamento de permissões multi-usuário.

## 5. Success Criteria
- [ ] Executar `F# Query Studio: Initialize Empty Project` cria `workspace.json` vazio e notebook inicial no diretório escolhido.
- [ ] Ao adicionar uma conexão com projeto aberto, o usuário pode escolher "Local (Current Project)" ou "Global".
- [ ] Conexão local é gravada no `workspace.json` do projeto e gera entidades em `<projeto>/generated/GeneratedEntities.fs`.
- [ ] Conexão global continua sendo gravada em `%LOCALAPPDATA%\FSharpQueryStudio\workspace.json`.
- [ ] 100% dos testes passam no Vitest e no .NET.

## 6. Impacted Artifacts
- `editors/vscode/package.json`
- `editors/vscode/src/extension.ts`
- `editors/vscode/src/connections/add.ts`
- `editors/vscode/src/connections/definition.ts`
- `src/FSharpQueryStudio.Worker/Handlers.fs`
- `src/FSharpQueryStudio.Protocol/Protocol.fs`
