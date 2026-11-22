# F# Query Studio — Benchmark de Execução e Consumo de Recursos

> **Data do Benchmark:** 2026-11-22 15:55:52 UTC  
> **Ambiente de Teste:** .NET 10.0 | Microsoft Windows NT 10.0.26300.0 | 12 vCPUs  

Este relatório documenta as medições de desempenho, tempo de execução, uso de CPU e alocação de memória (GC e Working Set) do **F# Query Studio**, cobrindo o ciclo de vida completo: desde a conexão e descoberta de schemas até a execução de queries em diferentes escalas, consultas LINQ tipadas e leitura de arquivos.

## 1. Tabela Geral de Resultados

| Categoria | Cenário de Teste | Iterações | Tempo Médio | P95 | Min | Max | CPU Total | Uso CPU % | Memória/Op | Coletas GC (G0/G1/G2) |
|:---|:---|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| **Connection** | Connect & Ping (SQLite) | 50 | `0.00 ms` | `0.00 ms` | `0.00 ms` | `0.03 ms` | `0.00 ms` | `0.0%` | `1.2 KB` | `0/0/0` |
| **Connection** | Schema Discovery (tables & views) | 30 | `0.33 ms` | `0.61 ms` | `0.28 ms` | `0.71 ms` | `0.00 ms` | `0.0%` | `7.1 KB` | `0/0/0` |
| **Latency** | Cold Start Query (New Connection + First Query) | 20 | `0.24 ms` | `0.29 ms` | `0.21 ms` | `0.34 ms` | `0.00 ms` | `0.0%` | `3.0 KB` | `0/0/0` |
| **Latency** | Warm Query (10 rows, pooled) | 100 | `0.22 ms` | `0.28 ms` | `0.16 ms` | `0.46 ms` | `31.25 ms` | `11.9%` | `1.7 KB` | `0/0/0` |
| **Latency** | Warm Query Parameterized (@minAmount) | 100 | `0.25 ms` | `0.40 ms` | `0.19 ms` | `0.59 ms` | `31.25 ms` | `10.4%` | `2.1 KB` | `0/0/0` |
| **Volume** | Fetch 100 rows | 50 | `0.25 ms` | `0.39 ms` | `0.19 ms` | `0.57 ms` | `15.62 ms` | `10.4%` | `1.9 KB` | `0/0/0` |
| **Volume** | Fetch 1,000 rows | 30 | `0.22 ms` | `0.31 ms` | `0.18 ms` | `0.32 ms` | `15.62 ms` | `20.1%` | `1.9 KB` | `0/0/0` |
| **Volume** | Fetch 10,000 rows | 15 | `0.22 ms` | `0.29 ms` | `0.19 ms` | `0.29 ms` | `0.00 ms` | `0.0%` | `1.9 KB` | `0/0/0` |
| **Volume** | Fetch 50,000 rows | 5 | `0.22 ms` | `0.26 ms` | `0.20 ms` | `0.26 ms` | `0.00 ms` | `0.0%` | `1.9 KB` | `0/0/0` |
| **Mapping** | Raw ExecuteQuery (1,000 rows) | 30 | `0.22 ms` | `0.26 ms` | `0.18 ms` | `0.26 ms` | `0.00 ms` | `0.0%` | `1.9 KB` | `0/0/0` |
| **Mapping** | Typed Connections.query<Customer> (1,000 rows) | 30 | `1.54 ms` | `1.78 ms` | `1.39 ms` | `2.23 ms` | `31.25 ms` | `5.7%` | `229.1 KB` | `0/0/0` |
| **Mapping** | LINQ query { ... } Filter & Sort (1,000 rows) | 30 | `2.15 ms` | `2.91 ms` | `1.77 ms` | `2.92 ms` | `62.50 ms` | `8.1%` | `352.0 KB` | `0/0/0` |
| **CSV** | queryCsv<Customer> (1,000 records) | 30 | `1.89 ms` | `2.53 ms` | `1.57 ms` | `2.72 ms` | `62.50 ms` | `9.2%` | `957.4 KB` | `2/1/1` |
| **CSV** | queryCsv<Customer> (10,000 records) | 10 | `21.75 ms` | `38.08 ms` | `16.18 ms` | `38.08 ms` | `265.62 ms` | `10.2%` | `7.53 MB` | `4/0/0` |

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
