# Change Proposal: Typed Connections By Category & Entity Mapping Progress

## Intent Block
```text
Intent Block:
Goal:        Eliminar o vazamento de contexto entre conexões no F# Query Studio, organizando o acesso através de categorias tipadas (Connections.sql, Connections.csv, Connections.excel, Connections.rest), removendo os métodos Connections.connect* e .asEntities, e fornecendo feedback visual em tempo real na barra de status do VS Code durante o mapeamento de entidades.
Expectation: O usuário acessa diretamente as conexões registradas através de suas categorias (ex.: Connections.sql.SampleStore.Customers, Connections.sql.SampleStore.rawQuery, Connections.csv.CustomersFeed), sem misturar tabelas ou arquivos de conexões diferentes e sem a necessidade da chamada intermediária .asEntities. Além disso, o usuário visualiza na barra inferior do VS Code quando o mapeamento de entidades estiver em andamento.
Action:      Reestruturar o módulo `Connections` para expor objetos categorizados (`sql`, `csv`, `excel`, `rest`); atualizar o `EntityBackgroundGenerator` para gerar extensões tipadas nessas categorias e embutir `rawQuery` e tabelas isoladas por conexão; remover métodos `connect`, `connectCsv`, `connectExcel`, `connectRest` e a propriedade `.asEntities`; atualizar os geradores de demonstração (`SampleDataGenerator`) e documentação; adicionar evento reverso no worker/host para notificar o início e conclusão do mapeamento de entidades exibindo um item animado na barra de status do VS Code; garantir higienização e limpeza de arquivos de testes.
Result:      Zero vazamento de tabelas/arquivos entre conexões distintas, código de consulta mais limpo e conciso em notebooks, e visibilidade instantânea do status de mapeamento de entidades no VS Code.
```

## Contexto & Motivação
1. **Vazamento de Contexto**:
   - Atualmente, ao chamar `(Connections.connectCsv "CustomersFeed").asEntities` ou `(Connections.connect "SampleStore").asEntities`, os tipos `AllCsvEntities` e `AllDatabaseEntities` expõem tabelas e propriedades de **todas** as conexões registradas naquele tipo simultaneamente.
   - Isso desvirtua o propósito de especificar o nome da conexão no `connect*` e permite que uma consulta tente inadvertidamente misturar entidades de fontes desconexas sem erro em tempo de edição.

2. **Nova API de Conexão por Categoria**:
   - Substituição de `Connections.connect*` e `.asEntities` por propriedades tipadas sob cada categoria:
     - `Connections.sql.<ConnectionName>.<TableName>` (ex.: `Connections.sql.SampleStore.Customers`)
     - `Connections.sql.<ConnectionName>.rawQuery """SELECT ..."""` (consultas customizadas com parâmetros)
     - `Connections.csv.<ConnectionName>` (ex.: `Connections.csv.CustomersFeed`, atuando diretamente como `IQueryable<Row>` e expondo `.Rows` e `.Raw()`)
     - `Connections.excel.<ConnectionName>.<SheetName>` (ex.: `Connections.excel.Reports.Sales`)
     - `Connections.rest.<ConnectionName>.Items` (ou `.SendAsync`)
   - Mapeamento das entidades passa a ser o comportamento padrão nativo, sem exigir `.asEntities`.

3. **Feedback Visual de Mapeamento no VS Code**:
   - Ao adicionar conexões, abrir workspaces locais ou regenerar o código tipado em background, o usuário não tem retorno visual de que as entidades estão sendo inspecionadas e os tipos F# gerados.
   - O Worker notificará o VS Code (via canal bidirecional ou status event) sobre o ciclo de vida do mapeamento, e a extensão exibirá um `StatusBarItem` com spinner (`$(sync~spin) Mapping entities...`) durante a execução, finalizando com sucesso ou ocultando ao concluir.

4. **Diretiva de Limpeza de Testes**:
   - Todo teste automatizado (C# / F# / TypeScript) que gere pastas ou arquivos em disco (ex.: diretórios temporários de workspaces, arquivos `.fsqs/generated`) deve obrigatoriamente limpar seus artefatos no término (`finally` / `afterEach`), garantindo que o repositório e o sistema de arquivos permaneçam limpos.

## Escopo
- **In Scope**:
  - Remoção de `Connections.connect`, `connectCsv`, `connectExcel`, `connectRest` e de `.asEntities`.
  - Adição de instâncias de categoria em `Connections`: `Connections.sql`, `Connections.csv`, `Connections.excel`, `Connections.rest`.
  - Atualização do gerador `EntityBackgroundGenerator.fs` para gerar propriedades de extensão estendendo as categorias de conexão e isolando tabelas/consultas exclusivamente dentro da conexão a que pertencem.
  - Implementação de `rawQuery` dentro do contexto de cada conexão SQL gerada.
  - Atualização dos notebooks de exemplo gerados (`SampleDataGenerator.fs`), documentações e testes existentes.
  - Notificação de status de mapeamento do worker para o VS Code e feedback visual na barra inferior (Status Bar).
  - Atualização de completion e providers no VS Code para refletir a nova sintaxe.
  - Higienização e limpeza de diretórios temporários em todos os testes.
- **Out of Scope**:
  - Modificação do backend de execução do kernel FSI além do carregamento normal de `GeneratedEntities.fs`.
  - Suporte a joins entre conexões SQL distintas (já destacado pelo usuário como joins somente dentro da mesma connection).

## Success Criteria
- [ ] O código gerado em `GeneratedEntities.fs` disponibiliza conexões organizadas por categoria (`Connections.sql.<Conn>`, `Connections.csv.<Conn>`, etc.) sem vazamento entre conexões distintas.
- [ ] Chamadas a `.asEntities` e `Connections.connect*` são completamente eliminadas do runtime e da documentação/exemplos.
- [ ] É possível executar `fsql { for c in Connections.sql.SampleStore.Customers do ... }` e `Connections.sql.SampleStore.rawQuery """SELECT ..."""`.
- [ ] É possível consultar `Connections.csv.<Conn>` diretamente.
- [ ] Durante o mapeamento de entidades, a barra inferior do VS Code exibe feedback visual (`$(sync~spin) Mapping entities...`).
- [ ] Todos os testes locais e no container Linux passam com 100% de sucesso.
- [ ] Nenhum arquivo residual ou diretório temporário de testes é mantido após as execuções de testes.
