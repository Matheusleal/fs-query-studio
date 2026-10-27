# F# Query Studio

[🇺🇸 English](README.md) | [🇧🇷 Português](README_PT.md)

Escreva, analise e execute scripts de consulta F# contra bancos de dados SQL, arquivos locais e APIs REST — diretamente em notebooks do VS Code.

O F# Query Studio é uma **extensão para o VS Code**. Você abre arquivos `.fsqnb` como *F# Query Notebooks*; cada célula de código executa F# em uma sessão FSI persistente hospedada em um processo worker .NET nativo. Uma API unificada e segura (`Connections.*`) conecta-se aos bancos de dados e arquivos registrados na barra lateral, renderizando os resultados em um grid interativo virtualizado com visualização de esquema e exportação para CSV.

> **Status:** `0.2.0`, pronto para distribuição. Estúdio completo de exploração e consultas com descoberta multi-schema, consultas LINQ fortemente tipadas, geração de entidades em background, benchmarks de execução sub-milissegundo comprovados e 100% de cobertura de testes de elementos de UI.
> Veja o [Release v0.2.0](https://github.com/Matheusleal/fs-query-studio/releases/tag/v0.2.0) e o guia de publicação em [`docs/vscode-extension-release.md`](docs/vscode-extension-release.md).

---

## Requisitos

- **.NET 10 SDK** no seu `PATH` — `dotnet --version` deve reportar `10` ou superior. A extensão inicializa um processo worker F# com ele.
- **VS Code 1.90** ou superior.

Suporte nativo a Windows, macOS e Linux. Um único processo worker é executado por janela do VS Code.

## Instalação

Disponível via build local VSIX ou download direto dos artifacts do GitHub Releases:

```bash
cd editors/vscode
npm install
npm run package          # gera editors/vscode/fs-query-studio.vsix
code --install-extension fs-query-studio.vsix --force
```

O pipeline de CI também publica o pacote VSIX automaticamente a cada push na branch principal.

## Início Rápido

1. **Novo notebook** — execute o comando **F# Query Studio: New F# Query Notebook** na Command Palette (`Ctrl+Shift+P`), ou crie qualquer arquivo com extensão `.fsqnb`.
2. **Execute F#** — digite código F# em uma célula e execute-a. Declarações `let` persistem para as células seguintes (uma sessão FSI por notebook). O comando **F# Query Studio: Restart Notebook Session** reinicializa a sessão quando desejado.
3. **Adicione uma conexão** — abra o painel **F# Query Studio** na Activity Bar e clique em **Add Connection**: SQLite / CSV / Excel / JSON (caminho de arquivo), SQL Server / PostgreSQL / MySQL (connection string), ou REST JSON (URL). Credenciais e senhas são salvas no chaveiro seguro do sistema operacional via VS Code `SecretStorage`, nunca em disco.
4. **Consulte com LINQ ou SQL**:
   - Consultas tipadas com LINQ:
     ```fsharp
     let db = (Connections.connect "minha-conexao").asEntities
     query { for u in db.Users do where (u.IsActive) select u }
     ```
   - Ou com SQL tradicional parametrizado:
     ```fsharp
     Connections.sql "minha-conexao" "select * from Orders where Total > @min" [ "@min", box 100.0 ]
     ```
   Ao executar a célula, o resultado é renderizado automaticamente em um grid interativo com aba de esquema e botão **Export CSV**.

Guia prático passo a passo: **[`docs/TUTORIAL.md`](docs/TUTORIAL.md)**.  
Guia Completo do Usuário: **[`editors/vscode/USER_GUIDE_PT.md`](editors/vscode/USER_GUIDE_PT.md)** (ou clique no ícone `$(book)` no VS Code).

## O que a Extensão Oferece

- **Execução em Notebooks** — cada célula `.fsqnb` executa em uma sessão FSI isolada por notebook; saída ao vivo de stdout / stderr; cancelamento de células demoradas pela barra de ferramentas.
- **Consultas LINQ Fortemente Tipadas** — geração automática de tipos record e contextos LINQ em background para bancos de dados (PostgreSQL, SQL Server, MySQL, SQLite) e arquivos (CSV, Excel, JSON).
- **API `Connections` Unificada** — `Connections.connect` / `sql` / `csv` / `excel` / `json` / `rest` acessam conexões pelo nome de forma protegida contra injeção SQL. O valor da última expressão é renderizado automaticamente — coleções geram o grid, conectores adicionam **Export CSV**, valores escalares são exibidos como texto formatado.
- **Grid de Resultados Interativo** — tabela virtualizada de alta performance, alternância para visualização de metadados/esquema, exportação para CSV e paginação contínua ("Load More") para grandes volumes.
- **Inteligência de Linguagem no Editor** — autocompletação, hover/tooltips, signature help, diagnósticos em tempo real e navegação **Go to Definition (`F12`)** entre células, tipos gerados e modelos de dados, além de autocomplete contextual de conexões filtrado por tipo.
- **Explorador de Conexões** — visualização em árvore na Activity Bar organizada por conexões → schemas → tabelas/views → colunas, com carregamento sob demanda e wizards de criação/edição.
- **Documentação Embutida** — clique no ícone de livro `$(book)` na barra de conexões ou no título do editor para acessar o Guia do Usuário traduzido sem sair do editor.

## Benchmarks de Execução e Confiabilidade

O F# Query Studio foi projetado com foco em latência mínima, consumo previsível de memória e ausência de travamentos de interface:

- **Consultas Sub-milissegundo**: Conexões reutilizadas via pool ADO.NET executam queries entre `0.05 ms` e `0.20 ms` com uso negligenciável de CPU e zero coletas Gen 1/2 no GC.
- **Escalabilidade com Streaming**: Processa conjuntos de 100 até 50.000+ linhas via streaming iterativo de cursores (`Results.fs`), mantendo uma linha de base estável de `1.5 KB` sem materializar tabelas gigantescas na memória heap.
- **LINQ Compilado com Cache de Reflexão**: Mapeamento de records F# (`Connections.query<'T>`) e expressões `query { ... }` processam 1.000 registros tipados em `~2.0 ms` usando delegates construtores pré-compilados.
- **Alto Desempenho com Arquivos CSV**: Taxa de transferência contínua de `~500.000 linhas/segundo`.
- **100% de Verificação de Elementos de UI**: Todos os componentes visuais (grid virtualizado, cabeçalhos fixos, destaque de nulos e numéricos, aba de esquema, paginação progressiva, mensagens de aviso e itens do explorador de conexões) possuem testes automatizados dedicados.

Relatório detalhado com tabelas completas: **[`docs/BENCHMARK_REPORT.md`](docs/BENCHMARK_REPORT.md)**.

## Estrutura do Repositório

| Caminho | Descrição |
|---|---|
| `src/FSharpQueryStudio.*` | O worker F# e suas bibliotecas (.NET 10) — hosting FSI, conectores, projeção de resultados, análise FSAC, protocolo e cofre de senhas. |
| `editors/vscode/` | A extensão do VS Code (TypeScript) — controlador de notebooks, renderer do grid de resultados, painel de conexões e cliente do worker. |
| `tests/` | Projetos de testes em .NET e benchmarks de performance. |
| `editors/vscode/test/` | Testes unitários (vitest) e testes no host do VS Code (`@vscode/test-electron`). |
| `docs/` | Documentação do projeto — veja [`docs/README.md`](docs/README.md) para o índice. |

## Desenvolvimento Local

```bash
cd editors/vscode
npm install
npm run build          # compila o worker em .NET + validação de tipos TypeScript + bundle com esbuild
npm test               # executa todos os testes unitários (vitest)
npm run test:vscode    # executa testes de integração dentro do VS Code
```

Pressione **F5** no VS Code para abrir o Extension Development Host.

Para compilar e testar os projetos .NET:

```bash
dotnet build FSharpQueryStudio.slnx
dotnet test  FSharpQueryStudio.slnx
```

## Relação com a Extensão Ionide

O [Ionide](https://ionide.io) é a extensão para edição de arquivos normais `.fs` e `.fsx`. Ele não interage com células de notebooks `.fsqnb`, e o F# Query Studio não interfere em arquivos `.fs` / `.fsx`. Você pode instalar ambas extensões simultaneamente sem qualquer conflito.

## Licença

[MIT](LICENSE) © 2026 Matheus Leal.
