# Diretrizes para Agentes de IA

## Regra Estrita: Git & Branch Protection
- **NUNCA faça commit ou push diretamente na branch `main` ou `master`**.
- Toda e qualquer alteração no código deve ser executada em uma feature/fix branch (ex: `feature/...`, `fix/...`, `chore/...`).
- O fluxo obrigatório para toda alteração é:
  1. Criar e alternar para uma nova branch (`git checkout -b feature/...`).
  2. Implementar as mudanças e rodar os testes relevantes.
  3. Comitar na branch de trabalho (`git commit`).
  4. Enviar para o repositório remoto (`git push -u origin feature/...`).
  5. Abrir um Pull Request via GitHub CLI (`gh pr create`).
  6. Aguardar o usuário ou a aprovação/merge do PR.
