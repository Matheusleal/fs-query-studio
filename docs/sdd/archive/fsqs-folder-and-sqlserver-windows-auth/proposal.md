# Change Proposal: .fsqs Internal Storage Folder & SQL Server Windows Authentication

## Intent Block
```text
Intent Block:
Goal:        Isolar dados internos e arquivos gerados em uma pasta oculta `.fsqs` na raiz do projeto e adicionar suporte direto à autenticação do Windows (Integrated Security) para conexões SQL Server.
Expectation: Usuários de projetos locais não verão arquivos de configuração e código gerado poluindo a raiz do repositório, e usuários em ambientes corporativos Windows poderão conectar-se ao SQL Server sem necessidade de cadastrar usuário e senha.
Action:      Centralizar `workspace.json`, a pasta `generated/` e caches na subpasta `.fsqs` do diretório raiz do projeto; incluir seletor de autenticação Windows vs SQL Server no fluxo de adição de conexão do SQL Server.
Result:      A raiz do projeto permanece limpa com apenas notebooks e arquivos do usuário; conexões SQL Server com autenticação Windows funcionam nativamente com Integrated Security=True sem solicitar senha.
```

## Contexto & Motivação
1. **Pasta `.fsqs`**:
   - Atualmente, a criação de projetos em branco coloca `workspace.json` e a pasta `generated/` diretamente na raiz do diretório.
   - O usuário solicitou que tanto `workspace.json` quanto todos os dados internos e código gerado residam dentro de uma pasta chamada `.fsqs` na raiz escolhida.
2. **Autenticação Windows no SQL Server**:
   - Em muitos ambientes corporativos com SQL Server (Active Directory / Windows Domain / instâncias locais do desenvolvedor), o acesso é concedido via Windows Authentication (`Integrated Security=True` / `Trusted_Connection=True`).
   - O fluxo de cadastro de conexão deve permitir selecionar explicitamente essa opção, gerando a connection string adequada e dispensando o prompt de senha.
