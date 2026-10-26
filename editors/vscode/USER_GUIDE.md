# F# Query Studio — Guia Completo do Usuário (User Guide)

O **F# Query Studio** é uma extensão profissional para o Visual Studio Code que transforma o editor em um estúdio completo e interativo para análise, consulta e engenharia de dados utilizando a linguagem **F#**. 

Com ele, você executa consultas com segurança, alta performance e tipagem estática forte através de **F# Query Notebooks (`.fsqnb`)**, integrando bancos de dados relacionais (**PostgreSQL, SQL Server, MySQL, SQLite**), arquivos locais (**CSV, Excel/XLSX, JSON**) e **APIs REST**.

---

## Sumário

1. [Requisitos e Instalação](#1-requisitos-e-instalação)
2. [Interface e Acesso Rápido](#2-interface-e-acesso-rápido)
3. [Gerenciamento de Conexões (Sidebar Explorer)](#3-gerenciamento-de-conexões-sidebar-explorer)
   - [Conexões Suportadas](#conexões-suportadas)
   - [Segurança e Armazenamento de Credenciais](#segurança-e-armazenamento-de-credenciais)
4. [Auto-complete Inteligente de Conexões](#4-auto-complete-inteligente-de-conexões)
5. [Consultas com LINQ e Type Provider em Background](#5-consultas-com-linq-e-type-provider-em-background)
   - [Como Funciona](#como-funciona-o-mapeamento-automático)
   - [Consultas em Bancos de Dados (Multi-Schema)](#consultas-linq-em-bancos-de-dados)
   - [Consultas em Arquivos Planos (CSV, Excel, JSON)](#consultas-linq-em-arquivos-csv-excel-e-json)
6. [Consultas SQL Tradicionais (Raw SQL)](#6-consultas-sql-tradicionais-raw-sql)
7. [Grid Interativo de Resultados](#7-grid-interativo-de-resultados)
   - [Ordenação, Alinhamento e Paginação](#recursos-do-grid)
   - [Aba de Esquema (Schema View)](#aba-de-esquema-schema-view)
   - [Exportação para CSV](#exportação-para-csv)
8. [Recursos de Linguagem e Go to Definition (F12)](#8-recursos-de-linguagem-e-go-to-definition-f12)
9. [Ciclo de Vida do Notebook e Sessão FSI](#9-ciclo-de-vida-do-notebook-e-sessão-fsi)
10. [Diagnóstico e Solução de Problemas](#10-diagnóstico-e-solução-de-problemas)

---

## 1. Requisitos e Instalação

- **.NET 10 SDK** instalado e acessível no seu `PATH` (`dotnet --version` ≥ 10.0). O F# Query Studio executa um processo worker nativo empacotado que gerencia a sessão interativa FSI e o servidor de linguagem (FSAC).
- **VS Code 1.90** ou superior.
- Suporte multiplataforma nativo: **Windows, Linux e macOS**.

### Instalação

Caso instale a partir de um pacote `.vsix` local:
```bash
code --install-extension fs-query-studio.vsix --force
```

---

## 2. Interface e Acesso Rápido

O F# Query Studio se integra perfeitamente ao VS Code em três áreas principais:

1. **Barra de Atividades (Activity Bar)**: Ícone dedicado com o logo do F# Query Studio na barra lateral esquerda. Nele você visualiza todas as conexões registradas, esquemas, tabelas e colunas.
2. **Ícone de Documentação no Menu (`$(book)`)**:
   - Localizado no topo da barra de ferramentas do painel **Connections**.
   - Localizado no canto superior direito do editor de qualquer notebook `.fsqnb`.
   - Permite abrir este guia completo lado a lado com seu código a qualquer momento.
3. **Command Palette (`Ctrl+Shift+P` ou `Cmd+Shift+P`)**:
   - `F# Query Studio: New F# Query Notebook`: Cria um novo notebook interativo.
   - `F# Query Studio: Open Documentation`: Abre o guia do usuário.
   - `F# Query Studio: Restart Notebook Session`: Reinicia o kernel e a sessão FSI do notebook atual.
   - `F# Query Studio: Check Worker`: Verifica a saúde do processo worker e a versão do .NET.

---

## 3. Gerenciamento de Conexões (Sidebar Explorer)

Na barra lateral **F# Query Studio > Connections**, você pode gerenciar suas fontes de dados.

### Conexões Suportadas

| Tipo | Descrição | Exemplo de Configuração |
|---|---|---|
| **PostgreSQL** | Suporte completo a múltiplos schemas (`public`, schemas customizados, views) | `Host=localhost;Database=mydb;Username=postgres;Password={password}` |
| **SQL Server** | Suporte a schemas (`dbo`, etc.), tabelas e views | `Server=localhost;Database=master;User Id=sa;Password={password};TrustServerCertificate=True` |
| **MySQL** | Suporte a bancos/catálogos, tabelas e colunas | `Server=localhost;Database=loja;Uid=root;Pwd={password};` |
| **SQLite** | Banco de dados local em arquivo | Caminho absoluto ou relativo, ex: `C:\dados\app.db` |
| **CSV** | Arquivos delimitados (vírgula, ponto-e-vírgula, tabulação) | Caminho do arquivo `.csv` |
| **Excel (XLSX)** | Planilhas Excel com múltiplas abas | Caminho do arquivo `.xlsx` |
| **JSON** | Arquivos JSON locais (objeto ou array de objetos) | Caminho do arquivo `.json` |
| **REST API** | Endpoints HTTP/HTTPS retornando JSON | URL base da API REST |

### Segurança e Armazenamento de Credenciais

As senhas **nunca** são salvas em texto puro no disco ou nos arquivos de notebook. Quando você cadastra uma conexão com `{password}`, a credencial é armazenada com criptografia nativa no chaveiro do seu sistema operacional via `SecretStorage` do VS Code.

---

## 4. Auto-complete Inteligente de Conexões

Ao digitar código em uma célula do notebook, o F# Query Studio oferece **IntelliSense contextual específico para cada tipo de conexão**:

- `Connections.connect "..."` ➔ Sugere **apenas bancos de dados relacionais e fontes que geram entidades** (PostgreSQL, SQL Server, MySQL, SQLite, CSV, Excel, JSON).
- `Connections.sql "..."` ➔ Sugere **apenas bancos de dados relacionais SQL** (PostgreSQL, SQL Server, MySQL, SQLite).
- `Connections.csv "..."` ➔ Sugere **apenas conexões do tipo CSV**.
- `Connections.excel "..."` ➔ Sugere **apenas conexões do tipo Excel/XLSX**.
- `Connections.json "..."` ➔ Sugere **apenas conexões do tipo JSON**.
- `Connections.rest "..."` ➔ Sugere **apenas conexões do tipo REST API**.

Isso evita erros de digitação e garante que você não tente executar SQL em uma API REST ou CSV em um driver PostgreSQL.

---

## 5. Consultas com LINQ e Type Provider em Background

### Como Funciona o Mapeamento Automático

O F# Query Studio analisa as conexões cadastradas no seu workspace e **gera automaticamente em segundo plano tipos F# fortemente tipados** para todas as tabelas, schemas e colunas. 

Você não precisa configurar Type Providers manuais nem compilar projetos auxiliares: basta chamar `.asEntities`!

### Consultas LINQ em Bancos de Dados

Para consultar um banco de dados (PostgreSQL, SQL Server, SQLite ou MySQL):

```fsharp
// 1. Obtenha a conexão registrada
let conn = Connections.connect "Backbone"

// 2. Acesse o contexto com todos os tipos tipados
let db = conn.asEntities

// 3. Escreva consultas LINQ utilizando a sintaxe nativa de F#
query {
    for u in db.Users do
    where (u.Email.Contains("@") && u.IsActive)
    sortBy u.CreatedAt
    select (u.Id, u.Name, u.Email)
}
```

#### Suporte a Múltiplos Schemas (PostgreSQL e SQL Server)
Se o seu banco de dados possuir schemas além do padrão:

```fsharp
query {
    for order in db.Sales.Orders do
    where (order.TotalAmount > 1000.0M)
    select order
}
```

### Consultas LINQ em Arquivos (CSV, Excel e JSON)

Arquivos CSV, planilhas Excel e arquivos JSON também são tipados automaticamente:

```fsharp
// Consulta LINQ em arquivo CSV
let csv = (Connections.connect "ClientesCSV").asEntities

query {
    for c in csv.Rows do
    where (c.Estado = "SP")
    select (c.Nome, c.Cidade, c.Telefone)
}
```

```fsharp
// Consulta LINQ em arquivo JSON
let produtos = (Connections.connect "ProdutosJSON").asEntities

query {
    for p in produtos.Items do
    where (p.Preco > 50.0)
    sortByDescending p.Preco
    select (p.Titulo, p.Preco)
}
```

---

## 6. Consultas SQL Tradicionais (Raw SQL)

Se preferir executar instruções SQL manuais com comandos complexos, CTEs ou agregações específicas:

```fsharp
// Consulta parametrizada com proteção nativa contra SQL Injection
Connections.sql
    "Backbone"
    "SELECT id, name, email, created_at FROM users WHERE status = @status AND created_at >= @desde"
    [ "status", box "ativo"
      "desde", box (System.DateTime(2026, 1, 1)) ]
```

---

## 7. Grid Interativo de Resultados

Toda expressão que retornar coleções ou dados tabulares é exibida automaticamente em um **grid visual interativo**, sem necessidade de formatações manuais.

```fsharp
// O último valor da célula determina o que é renderizado
Connections.sql "Backbone" "SELECT * FROM orders LIMIT 100" []
```

### Recursos do Grid:
- **Ordenação**: Clique no cabeçalho de qualquer coluna para ordenar de forma crescente ou decrescente.
- **Tipagem Visual**: Colunas numéricas são alinhadas à direita; textos à esquerda; valores nulos recebem identificadores visuais claros.
- **Paginação / Load More**: Resultados volumosos são carregados em blocos fluidos para manter alta performance de memória.
- **Aba de Esquema (Schema View)**: Clique na aba **Schema** no topo do grid para inspecionar os tipos de dados originais (.NET e banco de dados) de cada coluna retornada.
- **Exportação para CSV**: Clique no botão **Export CSV** para salvar o resultado exibido diretamente em um arquivo CSV local com diálogo nativo do sistema.

---

## 8. Recursos de Linguagem e Go to Definition (F12)

O F# Query Studio inclui um backend de linguagem F# completo rodando em conjunto com o F# Compiler Service:

- **Go to Definition (`F12`)**:
  - Pressione `F12` em qualquer símbolo (`Connections.connect`, `asEntities`, propriedades de tabelas como `u.Email`, funções ou variáveis).
  - O editor navega instantaneamente para a definição exata — seja ela em uma célula anterior do mesmo notebook ou no código gerado de entidades.
- **Hover (`Tooltip`)**: Passe o mouse sobre variáveis, métodos ou propriedades para inspecionar tipos e assinaturas completas.
- **Diagnósticos em Tempo Real**: Erros de sintaxe ou tipo são sublinhados imediatamente em vermelho/amarelo antes mesmo da execução da célula.
- **Rename (`F2`)**: Renomeie variáveis e símbolos de forma segura em todo o notebook.

---

## 9. Ciclo de Vida do Notebook e Sessão FSI

- **Persistência de Estado**: Células compartilham a mesma sessão interativa FSI dentro do arquivo `.fsqnb`. Variáveis definidas em uma célula ficam disponíveis nas células seguintes.
- **Execução Concorrente e Cancelamento**: Uma célula longa pode ser interrompida a qualquer momento pelo botão de parada (⏹) na barra de ferramentas da célula.
- **Reinício Limpo da Sessão**: Quando quiser limpar variáveis e módulos carregados, clique no ícone **Restart Notebook Session** na barra de ferramentas do notebook ou utilize o comando no `Ctrl+Shift+P`.

---

## 10. Diagnóstico e Solução de Problemas

- **O worker não inicializa?**
  Execute `F# Query Studio: Check Worker` no Command Palette (`Ctrl+Shift+P`). Ele verifica se o .NET 10 SDK está presente e emite mensagens claras de correção.
- **Adicionei uma tabela nova ao banco de dados e ela não aparece no autocomplete?**
  Na barra lateral **Connections**, clique no ícone de atualização (`$(refresh)`) para forçar o recarregamento do esquema e atualização dos tipos gerados.
- **Dúvidas ou Relato de Problemas?**
  Abra uma issue no repositório oficial: [https://github.com/Matheusleal/fs-query-studio/issues](https://github.com/Matheusleal/fs-query-studio/issues).
