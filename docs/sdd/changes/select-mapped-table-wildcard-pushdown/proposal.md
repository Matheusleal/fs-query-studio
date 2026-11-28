# Proposal: Suporte a Projeção de Tabelas Mapeadas Inteiras no `select` (`alias.*`)

## 1. Contexto e Problema
No **F# Query Studio**, as consultas relacionais e pushdown com `fsql { ... }` suportam projeções tabulares via registros anônimos (`{| ... |}`), registros F# tipados (`{ ... }`) e tuplas.

Atualmente, o tradutor de expressões (`ExpressionPushdownTranslator.fs`) assume que cada argumento em uma `NewExpression` de projeção é uma expressão escalar (coluna, literal, cálculo ou subquery escalar), mapeando cada campo para `col AS [Alias]`. Quando o usuário informa a referência de uma tabela mapeada inteira no `select` (por exemplo, `Tabela = t0` ou `Tabela = t1`), o tradutor tenta resolver `t0` como uma coluna escalar ou falha na resolução, gerando SQL inválido (como `_t0.Item1 AS [Tabela]`) ou desconsiderando a projeção.

Quando consultas envolvem joins entre múltiplas tabelas e o usuário deseja retornar todos os dados da tabela principal ou de uma tabela associada juntamente com algumas colunas específicas de outra tabela, ele é forçado a listar manualmente cada uma das dezenas de colunas daquela tabela (ex: `Id = t0.Id; Name = t0.Name; ...`), tornando as queries verbosas, propensas a erro e de difícil manutenção.

## 2. Intent Block

```text
Intent Block:
Goal:        Permitir informar a referência de uma tabela mapeada no `select` de consultas `fsql { ... }`, traduzindo automaticamente para a seleção de todas as colunas daquela tabela via curinga (`alias.*`), combinada com as demais propriedades projetadas.
Expectation: Ao escrever `select {| Tabela = t0; PropTabela2_1 = t1.prop1; ... |}` (ou registros F# equivalentes), o SQL gerado deve conter `t0.*` (ou `_t0.*`) para a tabela mapeada sem alias de coluna inválido, e manter `t1.prop1 AS [PropTabela2_1]` para propriedades escalares, funcionando com qualquer posição na projeção, qualquer número de tabelas, e suportando também projeções diretas de tabela (`select t0` / `select t1`).
Action:      Criar resolvedor de referências a tabelas mapeadas (`tryResolveTableAlias`) no tradutor de pushdown (`ExpressionPushdownTranslator.fs`); emitir `<alias>.*` no `plan.Projection` para argumentos correspondentes a tabelas mapeadas; suportar hidratação em tempo de execução de records aninhados no `CompiledRecordMapper` (`RecordReaders.fs`) e `SqlPushdownQuery.fs`; e documentar o padrão no guia do FSQL (`docs/FSQL_AI_AGENT_GUIDE.md` e `docs/FSQL_AI_AGENT_GUIDE_PT.md`).
Result:      Ergonomia aprimorada nas consultas FSQL, permitindo misturar tabelas completas e colunas pontuais sem código boilerplate.
```

## 3. Escopo

### No Escopo (In Scope)
- **Tradutor de Pushdown (`ExpressionPushdownTranslator.fs`)**:
  - Detecção e resolução de argumentos de `NewExpression` que representam tabelas mapeadas (tabela base `_t0` ou tabelas de join `_t1`, `_t2`, etc., originadas de tuplas de join, parâmetros de range variable ou transparent identifiers).
  - Emissão de `${tblAlias}.*` na lista de colunas projetadas sem alias `AS [Campo]`.
  - Emissão de `${sqlExpr} AS [Alias]` para as demais propriedades escalares.
  - Suporte a múltiplas tabelas com curinga na mesma projeção (ex: `T0 = t0; T1 = t1; Prop = t2.prop`).
  - Suporte a projeções de tabela em qualquer posição (primeira, intermediária, última).
  - Suporte a projeção direta de tabela no `select` em queries com join (ex: `select t0` ou `select t1`).
- **Materialização em Tempo de Execução (`RecordReaders.fs` e `SqlPushdownQuery.fs`)**:
  - Suporte a records aninhados em `CompiledRecordMapper` quando o tipo da propriedade for um record F# representando uma entidade de tabela.
- **Documentação**:
  - Atualização dos guias canônicos `docs/FSQL_AI_AGENT_GUIDE.md` e `docs/FSQL_AI_AGENT_GUIDE_PT.md`.
- **Testes & Qualidade**:
  - Mínimo de 2 testes distintos por cenário (base table wildcard, join table wildcard, multi-table wildcard, posições arbitrárias, direct `select t0`/`select t1`, execução/hidratação em memória).
  - Manutenção de 100.00% de cobertura nos arquivos modificados.
  - Formatação F# obrigatória com Fantomas (`dotnet fantomas src tests build.fsx`).
  - Validação completa com `dotnet fsi .\build.fsx release`.

### Fora de Escopo (Out of Scope)
- Modificações em dialetos SQL de engines não suportadas.
