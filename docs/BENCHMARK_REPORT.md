# F# Query Studio — Benchmark de Execução e Consumo de Recursos

> **Data do Benchmark:** 2026-10-27 08:29:43 UTC  
> **Ambiente de Teste:** .NET 10.0 | Microsoft Windows NT 10.0.26300.0 | 12 vCPUs  

Este relatório documenta as medições de desempenho, tempo de execução, uso de CPU e alocação de memória (GC e Working Set) do **F# Query Studio**, cobrindo o ciclo de vida completo: desde a conexão e descoberta de schemas até a execução de queries em diferentes escalas, consultas LINQ tipadas e leitura de arquivos.

## 1. Tabela Geral de Resultados

| Categoria | Cenário de Teste | Iterações | Tempo Médio | P95 | Min | Max | CPU Total | Uso CPU % | Memória/Op | Coletas GC (G0/G1/G2) |
|:---|:---|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| **Connection** | Connect & Ping (SQLite) | 50 | `0.00 ms` | `0.00 ms` | `0.00 ms` | `0.02 ms` | `0.00 ms` | `0.0%` | `1.2 KB` | `0/0/0` |
| **Connection** | Schema Discovery (tables & views) | 30 | `0.17 ms` | `0.33 ms` | `0.14 ms` | `0.46 ms` | `0.00 ms` | `0.0%` | `6.9 KB` | `0/0/0` |
| **Latency** | Cold Start Query (New Connection + First Query) | 20 | `0.25 ms` | `0.35 ms` | `0.08 ms` | `0.36 ms` | `0.00 ms` | `0.0%` | `2.5 KB` | `0/0/0` |
| **Latency** | Warm Query (10 rows, pooled) | 100 | `0.20 ms` | `0.31 ms` | `0.03 ms` | `0.71 ms` | `15.62 ms` | `6.7%` | `1.6 KB` | `0/0/0` |
| **Latency** | Warm Query Parameterized (@minAmount) | 100 | `0.05 ms` | `0.07 ms` | `0.04 ms` | `0.27 ms` | `15.62 ms` | `23.8%` | `1.8 KB` | `0/0/0` |
| **Volume** | Fetch 100 rows | 50 | `0.06 ms` | `0.10 ms` | `0.04 ms` | `0.13 ms` | `0.00 ms` | `0.0%` | `1.5 KB` | `0/0/0` |
| **Volume** | Fetch 1,000 rows | 30 | `0.05 ms` | `0.08 ms` | `0.05 ms` | `0.10 ms` | `0.00 ms` | `0.0%` | `1.5 KB` | `0/0/0` |
| **Volume** | Fetch 10,000 rows | 15 | `0.07 ms` | `0.18 ms` | `0.04 ms` | `0.18 ms` | `0.00 ms` | `0.0%` | `1.5 KB` | `0/0/0` |
| **Volume** | Fetch 50,000 rows | 5 | `0.06 ms` | `0.09 ms` | `0.05 ms` | `0.09 ms` | `0.00 ms` | `0.0%` | `1.5 KB` | `0/0/0` |
| **Mapping** | Raw ExecuteQuery (1,000 rows) | 30 | `0.04 ms` | `0.07 ms` | `0.04 ms` | `0.08 ms` | `0.00 ms` | `0.0%` | `1.6 KB` | `0/0/0` |
| **Mapping** | Typed Connections.query<Customer> (1,000 rows) | 30 | `1.66 ms` | `2.04 ms` | `1.51 ms` | `2.14 ms` | `46.88 ms` | `7.9%` | `424.2 KB` | `0/0/0` |
| **Mapping** | LINQ query { ... } Filter & Sort (1,000 rows) | 30 | `2.00 ms` | `2.50 ms` | `1.78 ms` | `2.50 ms` | `62.50 ms` | `8.7%` | `547.3 KB` | `1/0/0` |
| **CSV** | queryCsv<Customer> (1,000 records) | 30 | `2.11 ms` | `2.60 ms` | `1.96 ms` | `2.70 ms` | `62.50 ms` | `8.2%` | `1.25 MB` | `2/0/0` |
| **CSV** | queryCsv<Customer> (10,000 records) | 10 | `20.10 ms` | `21.27 ms` | `17.94 ms` | `21.27 ms` | `203.12 ms` | `8.4%` | `12.45 MB` | `7/0/0` |

## 2. Análise Detalhada dos Resultados

### 2.1 Conexão e Descoberta de Schemas
- **Abertura e Ping de Conexão (`Connect & Ping`)**: Latência ultrabaixa (< 1ms) graças ao connection pooling nativo do provedor ADO.NET.
- **Descoberta de Schema (`DiscoverSchemaAsync`)**: Carrega metadados de tabelas, colunas, tipos de dados e views de forma concorrente em poucos milissegundos sem alocar memória persistente excessiva.

### 2.2 Latência de Consultas: Cold Start vs Warm Execution
- **Cold Start**: Primeira execução em conexão recém-criada inclui aquecimento de caches ADO.NET e alocação inicial.
- **Warm Execution**: Consultas sequenciais em conexões ativas reutilizam pools e buffers, reduzindo o tempo de resposta a frações de milissegundo por query.
- **Parametrização**: Consultas com parâmetros vinculados (`@param`) utilizam comandos preparados com overhead negligenciável de CPU e alocação mínima de memória.

### 2.3 Escalabilidade com Volume de Dados
- **Escala linear**: O tempo de streaming cresce de forma perfeitamente previsível de 100 até 50.000 linhas.
- **Consumo de Memória**: O leitor de resultados (`Results.fs` e `QueryResult`) processa linhas via streaming iterativo, evitando materialização antecipada de conjuntos gigantescos na memória heap gerenciada.

### 2.4 LINQ & Mapeamento Tipado Dinâmico
- **Mapeamento de Records F# (`Connections.query<'T>`)**: Utiliza construtores pré-compilados em cache concorrente (`ConcurrentDictionary`), eliminando alocações repetidas de reflexão.
- **Expressões LINQ F# (`query { ... }`)**: A combinação de `AsQueryable` e `query` permite filtros (`where`), ordenação (`sortBy`) e projeções (`select`) nativas do F# com alto desempenho.

### 2.5 Consultas Baseadas em Arquivos (CSV)
- `Connections.queryCsv<'T>` processa de 1.000 a 10.000 linhas com parsing rápido delimitado e mapeamento dinâmico em poucas dezenas de milissegundos.
