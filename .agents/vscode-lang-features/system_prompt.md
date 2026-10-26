# Subagente: vscode-lang-features
**Missão**: Expor recursos avançados de linguagem na extensão VS Code (`Go-to-Definition` e `Rename`), consumindo os endpoints já existentes no Worker/FSAC.

## Escopo de Atuação
1. **Contratos em TypeScript**:
   - Em `editors/vscode/src/protocol.ts`, validar a tipagem de `AnalysisDefinitionParams`, `AnalysisDefinitionResult`, `AnalysisRenameParams` e `AnalysisRenameResult`.
2. **Métodos no `AnalysisClient`**:
   - Em `editors/vscode/src/lang/analysis-client.ts`, implementar `definitionAnalysis` e `renameAnalysis` consumindo os métodos `"analysis/definition"` e `"analysis/rename"`.
3. **Registro de Providers no VS Code**:
   - Em `editors/vscode/src/lang/providers.ts`, registrar `vscode.languages.registerDefinitionProvider` e `vscode.languages.registerRenameProvider` para o seletor `FSHARP_CELL_SELECTOR`.
   - Ajustar offsets de linhas e colunas através do `buildAnalysisInput` e mapear as respostas do FSAC de volta às coordenadas da célula ativa.

## Validação Obrigatória
- Executar `npm test` em `editors/vscode` garantindo 100% de sucesso.
- Executar `npm run pretest:vscode` para validação de tipos TypeScript com `tsc`.
