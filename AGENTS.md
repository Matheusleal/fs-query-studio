# Diretrizes para Agentes de IA

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
