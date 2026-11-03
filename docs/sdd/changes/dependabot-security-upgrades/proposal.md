# Proposal: Dependabot Security Upgrades and CI Stabilization

## Intent Block
**Goal:** Atualizar dependências com vulnerabilidades apontadas pelo Dependabot (`brace-expansion`, `esbuild`, `serialize-javascript`, etc.) e assegurar que o pipeline de CI (tanto Node 20 para a extensão quanto .NET 10 para o backend no Linux e Windows) passe com 100% de sucesso.
**Expectation:** `npm ci`, `npm test`, `npm run compile`, `npm run package`, `dotnet test` e os testes E2E do `sample_demo` executem sem falhas ou conflitos de peer dependency, mitigando vulnerabilidades reportadas.
**Action:** Atualizar `brace-expansion`, `esbuild` e dependências transitivas compatíveis em `editors/vscode`, garantindo compatibilidade estrita com Node 20 e evitando incompatibilidades do Vitest 5.
**Result:** Vulnerabilidades sanadas, build e suites de testes 100% verdes localmente e no container Linux.
