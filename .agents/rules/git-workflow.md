# Git & Branch Protection Rules

## 1. Princípio Fundamental: Proteção das Branches Principais
- O agente **NUNCA** deve fazer commit diretamente na branch `main` ou `master`.
- O agente **NUNCA** deve fazer push direto para a branch `main` ou `master`.

## 2. Gate Obrigatório: Abertura de Pull Request Apenas Após Testes Completos
- O CI do GitHub roda apenas quando um PR é aberto ou atualizado (`pull_request: branches: [main]`).
- Branches sem PR **não executam CI**, protegendo a quota do repositório.
- **Portanto, o agente NUNCA deve abrir um PR antes de garantir que tudo está funcionando perfeitamente**:
  - Todo o desenvolvimento e iteração de correções devem acontecer na branch local/remota **sem PR**.
  - O agente deve rodar os testes localmente e no container Linux (`docker run ... dotnet test ...`).
  - O comando `gh pr create` é estritamente proibido enquanto houver falhas, testes quebrados ou código incompleto.
  - O PR é o passo final de validação e entrega.

## 3. Fluxo Obrigatório de Trabalho
Sempre que for solicitado desenvolver uma funcionalidade, aplicar uma alteração, refatorar código ou corrigir um bug:
1. **Criar uma branch dedicada**:
   - Antes de realizar qualquer commit, crie e troque para uma branch apropriada:
     - `feature/<nome-descritivo>` para novas funcionalidades
     - `fix/<nome-descritivo>` para correções de bugs
     - `chore/<nome-descritivo>` para tarefas de manutenção/configuração
2. **Desenvolvimento e Testes Iterativos**:
   - Faça as alterações na branch criada.
   - Comite na branch de trabalho (`git commit`).
   - Valide no container Linux (`docker run ...`) e localmente.
   - Itere e corrija até que 100% dos testes passem com sucesso.
3. **Push e Abertura do PR (Passo Final)**:
   - Somente quando todos os testes estiverem passando e a solução estiver pronta:
     ```bash
     git push -u origin <branch-name>
     gh pr create --base main --head <branch-name> --title "..." --body "..."
     ```
4. **Merge**:
   - O merge na `main`/`master` só é feito após revisão/aprovação e via Pull Request (nunca via commit direto na branch principal).
