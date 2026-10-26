# Git & Branch Protection Rules

## 1. Princípio Fundamental: Proteção das Branches Principais
- O agente **NUNCA** deve fazer commit diretamente na branch `main` ou `master`.
- O agente **NUNCA** deve fazer push direto para a branch `main` ou `master`.

## 2. Fluxo Obrigatório de Trabalho (Feature Branch + Pull Request)
Sempre que for solicitado desenvolver uma funcionalidade, aplicar uma alteração, refatorar código ou corrigir um bug:
1. **Criar uma branch dedicada**:
   - Antes de realizar qualquer commit, crie e troque para uma branch apropriada:
     - `feature/<nome-descritivo>` para novas funcionalidades
     - `fix/<nome-descritivo>` para correções de bugs
     - `chore/<nome-descritivo>` para tarefas de manutenção/configuração
2. **Desenvolvimento e Testes**:
   - Faça as alterações na branch criada.
   - Valide localmente e em container Linux (`docker run ...`) conforme as regras do projeto.
3. **Commit na Feature Branch**:
   - Realize commits claros e objetivos na branch de trabalho.
4. **Push e Abertura de PR**:
   - Faça o push da branch para o remote: `git push -u origin <branch-name>`.
   - Crie o Pull Request direcionado para a `main`:
     ```bash
     gh pr create --base main --head <branch-name> --title "..." --body "..."
     ```
5. **Merge**:
   - O merge na `main`/`master` só é feito após revisão/aprovação e via Pull Request (nunca via commit direto na branch principal).
