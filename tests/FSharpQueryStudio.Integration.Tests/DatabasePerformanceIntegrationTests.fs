namespace FSharpQueryStudio.Integration.Tests

open System
open System.IO
open System.Threading
open System.Diagnostics
open System.Linq
open Xunit
open Xunit.Abstractions
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors
open PostgreSqlConnectorTests
open SqlServerConnectorTests

type PerfCustomer = {
    id: int
    full_name: string
    tier: string
    credit_limit: decimal
}

type PerfOrder = {
    id: int
    customer_id: int
    order_date: DateTime
    total_amount: decimal
    status: string
}

type CustomerOrderSummary = {
    OrderId: int
    CustomerName: string
    TotalAmount: decimal
    Status: string
}

type private PerfResolver(map: Map<string, IConnector>) =
    interface IConnectionResolver with
        member _.Resolve(name: string) =
            match Map.tryFind name map with
            | Some c -> c
            | None -> failwith $"Connection '{name}' not found."

[<Collection("PostgreSql container")>]
[<Trait("Category", "RequiresDocker")>]
type PostgreSqlPerformanceIntegrationTests(fixture: PostgreSqlContainerFixture, output: ITestOutputHelper) =

    let seedPostgreSqlData (connector: PostgreSqlConnector) =
        task {
            // Criar schemas separados
            connector.ExecuteQuery "create schema if not exists perf_customers" [] |> ignore
            connector.ExecuteQuery "create schema if not exists perf_sales" [] |> ignore

            // Criar tabelas
            connector.ExecuteQuery "drop table if exists perf_sales.orders" [] |> ignore
            connector.ExecuteQuery "drop table if exists perf_customers.profiles" [] |> ignore

            connector.ExecuteQuery
                "create table perf_customers.profiles ( \
                    id int primary key, \
                    full_name varchar(100) not null, \
                    tier varchar(20) not null, \
                    credit_limit numeric(12,2) not null \
                )" [] |> ignore

            connector.ExecuteQuery
                "create table perf_sales.orders ( \
                    id int primary key, \
                    customer_id int not null, \
                    order_date date not null, \
                    total_amount numeric(12,2) not null, \
                    status varchar(20) not null \
                )" [] |> ignore

            // Inserir > 10.000 linhas em perf_customers.profiles (10.000) e perf_sales.orders (15.000)
            let sw = Stopwatch.StartNew()
            connector.ExecuteQuery
                "insert into perf_customers.profiles (id, full_name, tier, credit_limit) \
                 select \
                     g, \
                     'Customer ' || g, \
                     case when g % 3 = 0 then 'Premium' when g % 3 = 1 then 'Standard' else 'Basic' end, \
                     (1000 + (g % 50) * 100)::numeric \
                 from generate_series(1, 10000) as g" [] |> ignore

            connector.ExecuteQuery
                "insert into perf_sales.orders (id, customer_id, order_date, total_amount, status) \
                 select \
                     g, \
                     1 + (g % 10000), \
                     current_date - ((g % 365) || ' days')::interval, \
                     (10.5 + (g % 1000) * 1.5)::numeric \
                     , \
                     case when g % 4 = 0 then 'Completed' when g % 4 = 1 then 'Pending' when g % 4 = 2 then 'Shipped' else 'Cancelled' end \
                 from generate_series(1, 15000) as g" [] |> ignore
            sw.Stop()
            output.WriteLine($"[PostgreSQL] Seeded 10k customers and 15k orders in {sw.ElapsedMilliseconds}ms")
        }

    [<Fact>]
    member _.``PostgreSQL multi-schema tables with >10k rows: schema discovery and performance benchmark`` () =
        task {
            let connector = PostgreSqlConnector(fixture.ConnectionString)
            do! seedPostgreSqlData connector

            // 1. Validar contagem > 10.000 linhas em cada schema
            let custCountResult = connector.ExecuteQuery "select count(*) from perf_customers.profiles" []
            let custCount = Convert.ToInt32((custCountResult.Rows |> Seq.head).[0])
            Assert.True(custCount >= 10000, $"Expected >= 10000 customers, got {custCount}")

            let orderCountResult = connector.ExecuteQuery "select count(*) from perf_sales.orders" []
            let orderCount = Convert.ToInt32((orderCountResult.Rows |> Seq.head).[0])
            Assert.True(orderCount >= 10000, $"Expected >= 10000 orders, got {orderCount}")

            output.WriteLine($"[PostgreSQL] Verified row counts: perf_customers.profiles = {custCount}, perf_sales.orders = {orderCount}")

            // 2. Validar Descoberta de Esquema Multi-Schema
            let asInterface = connector :> IConnector
            let! schema = asInterface.DiscoverSchemaAsync(CancellationToken.None)
            let tableNames = schema |> List.map (fun t -> t.Name)
            Assert.Contains("perf_customers.profiles", tableNames)
            Assert.Contains("perf_sales.orders", tableNames)

            // Configurar Resolver para testes de query
            let dbName = "PgPerfDb"
            Connections.configure (PerfResolver(Map.ofList [ dbName, connector :> IConnector ]))
            let dbConn = Connections.getDatabaseConnection dbName

            // 3. BENCHMARK: Query filtrada (WHERE total_amount > 1400.0)
            // Cenário A: Execução em Memória (como o Queryable atual faz: SELECT * FROM table -> LINQ to Objects em memória)
            let memAllocBeforeA = GC.GetAllocatedBytesForCurrentThread()
            let swA = Stopwatch.StartNew()
            let inMemoryQueryable = dbConn.Queryable<PerfOrder>("select id, customer_id, order_date, total_amount, status from perf_sales.orders")
            let filteredInMemory =
                query {
                    for o in inMemoryQueryable do
                    where (o.total_amount > 1400.0M)
                    select o
                }
                |> Seq.toList
            swA.Stop()
            let memAllocAfterA = GC.GetAllocatedBytesForCurrentThread()
            let memUsedA = memAllocAfterA - memAllocBeforeA

            // Cenário B: Execução com Pushdown no Banco de Dados (filtro no WHERE da query SQL)
            let memAllocBeforeB = GC.GetAllocatedBytesForCurrentThread()
            let swB = Stopwatch.StartNew()
            let pushdownFiltered =
                dbConn.Query<PerfOrder>(
                    "select id, customer_id, order_date, total_amount, status from perf_sales.orders where total_amount > @min",
                    [ "@min", box 1400.0M ]
                )
                |> Seq.toList
            swB.Stop()
            let memAllocAfterB = GC.GetAllocatedBytesForCurrentThread()
            let memUsedB = memAllocAfterB - memAllocBeforeB

            // Validação de paridade dos resultados
            Assert.Equal(filteredInMemory.Length, pushdownFiltered.Length)
            output.WriteLine($"[PostgreSQL Benchmark - Filtered Query ({filteredInMemory.Length} rows returned out of 15k)]")
            output.WriteLine($"  - In-Memory (Full scan + LINQ to Objects): {swA.ElapsedMilliseconds}ms, Allocated: {memUsedA / 1024L} KB")
            output.WriteLine($"  - Pushdown (SQL WHERE):                    {swB.ElapsedMilliseconds}ms, Allocated: {memUsedB / 1024L} KB")
            output.WriteLine($"  - Memory Savings with Pushdown:           {double (memUsedA - memUsedB) / double memUsedA * 100.0:F1}%%")

            // 4. BENCHMARK: Agregação (SUM e COUNT)
            // In-Memory: materializa 15.000 registros para calcular a soma
            let swAggMem = Stopwatch.StartNew()
            let allocBeforeAggMem = GC.GetAllocatedBytesForCurrentThread()
            let ordersAll = dbConn.Query<PerfOrder>("select id, customer_id, order_date, total_amount, status from perf_sales.orders") |> Seq.toList
            let sumInMemory = ordersAll |> List.sumBy (fun o -> o.total_amount)
            let allocAfterAggMem = GC.GetAllocatedBytesForCurrentThread()
            swAggMem.Stop()

            // Pushdown: Agregação executada no PostgreSQL
            let swAggDb = Stopwatch.StartNew()
            let allocBeforeAggDb = GC.GetAllocatedBytesForCurrentThread()
            let sumResult = dbConn.rawQuery("select sum(total_amount) from perf_sales.orders")
            let sumInDb = Convert.ToDecimal((sumResult.Rows |> Seq.head).[0])
            let allocAfterAggDb = GC.GetAllocatedBytesForCurrentThread()
            swAggDb.Stop()

            Assert.Equal(sumInMemory, sumInDb)
            output.WriteLine($"[PostgreSQL Benchmark - Aggregation (SUM 15k rows)]")
            output.WriteLine($"  - In-Memory Sum: {swAggMem.ElapsedMilliseconds}ms, Allocated: {(allocAfterAggMem - allocBeforeAggMem) / 1024L} KB")
            output.WriteLine($"  - Pushdown Sum:  {swAggDb.ElapsedMilliseconds}ms, Allocated: {(allocAfterAggDb - allocBeforeAggDb) / 1024L} KB")

            // 5. BENCHMARK: Cross-Schema JOIN (perf_sales.orders JOIN perf_customers.profiles)
            // In-Memory JOIN: materializa 15k orders + 10k customers
            let swJoinMem = Stopwatch.StartNew()
            let allocBeforeJoinMem = GC.GetAllocatedBytesForCurrentThread()
            let customersAll = dbConn.Query<PerfCustomer>("select id, full_name, tier, credit_limit from perf_customers.profiles") |> Seq.toList
            let ordersForJoin = dbConn.Query<PerfOrder>("select id, customer_id, order_date, total_amount, status from perf_sales.orders where total_amount > 1450.0") |> Seq.toList
            let joinedInMemory =
                query {
                    for o in ordersForJoin do
                    join c in customersAll on (o.customer_id = c.id)
                    select { OrderId = o.id; CustomerName = c.full_name; TotalAmount = o.total_amount; Status = o.status }
                }
                |> Seq.toList
            let allocAfterJoinMem = GC.GetAllocatedBytesForCurrentThread()
            swJoinMem.Stop()

            // Pushdown JOIN: Banco de dados faz o JOIN e projeta apenas o necessário
            let swJoinDb = Stopwatch.StartNew()
            let allocBeforeJoinDb = GC.GetAllocatedBytesForCurrentThread()
            let joinedInDb =
                dbConn.Query<CustomerOrderSummary>(
                    "select o.id as OrderId, c.full_name as CustomerName, o.total_amount as TotalAmount, o.status as Status \
                     from perf_sales.orders o \
                     join perf_customers.profiles c on o.customer_id = c.id \
                     where o.total_amount > 1450.0"
                )
                |> Seq.toList
            let allocAfterJoinDb = GC.GetAllocatedBytesForCurrentThread()
            swJoinDb.Stop()

            // 6. BENCHMARK: Paging & Ordering (Top 10 highest orders out of 15k)
            let swPageMem = Stopwatch.StartNew()
            let allocBeforePageMem = GC.GetAllocatedBytesForCurrentThread()
            let top10InMemory =
                query {
                    for o in dbConn.Queryable<PerfOrder>("select id, customer_id, order_date, total_amount, status from perf_sales.orders") do
                    sortByDescending o.total_amount
                    thenBy o.id
                    take 10
                    select o
                }
                |> Seq.toList
            let allocAfterPageMem = GC.GetAllocatedBytesForCurrentThread()
            swPageMem.Stop()

            let swPageDb = Stopwatch.StartNew()
            let allocBeforePageDb = GC.GetAllocatedBytesForCurrentThread()
            let top10InDb =
                dbConn.Query<PerfOrder>(
                    "select id, customer_id, order_date, total_amount, status \
                     from perf_sales.orders \
                     order by total_amount desc, id asc \
                     limit 10"
                )
                |> Seq.toList
            let allocAfterPageDb = GC.GetAllocatedBytesForCurrentThread()
            swPageDb.Stop()

            Assert.Equal(top10InMemory.Length, top10InDb.Length)
            Assert.Equal(top10InMemory.[0].id, top10InDb.[0].id)
            output.WriteLine($"[PostgreSQL Benchmark - Paging / Top 10 (out of 15k rows)]")
            output.WriteLine($"  - In-Memory Top 10: {swPageMem.ElapsedMilliseconds}ms, Allocated: {(allocAfterPageMem - allocBeforePageMem) / 1024L} KB")
            output.WriteLine($"  - Pushdown Top 10:  {swPageDb.ElapsedMilliseconds}ms, Allocated: {(allocAfterPageDb - allocBeforePageDb) / 1024L} KB")

            // 7. BENCHMARK: Mapping Overhead (15k rows raw ADO.NET vs Connections.getMapper reflection)
            let swRaw = Stopwatch.StartNew()
            let allocBeforeRaw = GC.GetAllocatedBytesForCurrentThread()
            let rawResult = dbConn.rawQuery("select id, customer_id, order_date, total_amount, status from perf_sales.orders")
            let rawRows = rawResult.Rows |> Seq.toList
            let allocAfterRaw = GC.GetAllocatedBytesForCurrentThread()
            swRaw.Stop()

            let swMapped = Stopwatch.StartNew()
            let allocBeforeMapped = GC.GetAllocatedBytesForCurrentThread()
            let mappedRows = dbConn.Query<PerfOrder>("select id, customer_id, order_date, total_amount, status from perf_sales.orders") |> Seq.toList
            let allocAfterMapped = GC.GetAllocatedBytesForCurrentThread()
            swMapped.Stop()

            output.WriteLine($"[PostgreSQL Benchmark - Mapping Overhead (15k rows)]")
            output.WriteLine($"  - Raw ADO.NET obj[]:       {swRaw.ElapsedMilliseconds}ms, Allocated: {(allocAfterRaw - allocBeforeRaw) / 1024L} KB")
            output.WriteLine($"  - Mapped to F# PerfOrder:  {swMapped.ElapsedMilliseconds}ms, Allocated: {(allocAfterMapped - allocBeforeMapped) / 1024L} KB")
            output.WriteLine($"  - Pure Mapping Overhead:   {swMapped.ElapsedMilliseconds - swRaw.ElapsedMilliseconds}ms, Added Alloc: {((allocAfterMapped - allocBeforeMapped) - (allocAfterRaw - allocBeforeRaw)) / 1024L} KB")
        }

[<Collection("SqlServer container")>]
[<Trait("Category", "RequiresDocker")>]
type SqlServerPerformanceIntegrationTests(fixture: MsSqlContainerFixture, output: ITestOutputHelper) =

    let seedSqlServerData (connector: SqlServerConnector) =
        task {
            // Criar schemas separados
            connector.ExecuteQuery "IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'perf_customers') EXEC('CREATE SCHEMA perf_customers')" [] |> ignore
            connector.ExecuteQuery "IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'perf_sales') EXEC('CREATE SCHEMA perf_sales')" [] |> ignore

            // Criar tabelas
            connector.ExecuteQuery "IF OBJECT_ID('perf_sales.orders', 'U') IS NOT NULL DROP TABLE perf_sales.orders" [] |> ignore
            connector.ExecuteQuery "IF OBJECT_ID('perf_customers.profiles', 'U') IS NOT NULL DROP TABLE perf_customers.profiles" [] |> ignore

            connector.ExecuteQuery
                "CREATE TABLE perf_customers.profiles ( \
                    id int primary key, \
                    full_name nvarchar(100) not null, \
                    tier nvarchar(20) not null, \
                    credit_limit decimal(12,2) not null \
                )" [] |> ignore

            connector.ExecuteQuery
                "CREATE TABLE perf_sales.orders ( \
                    id int primary key, \
                    customer_id int not null, \
                    order_date date not null, \
                    total_amount decimal(12,2) not null, \
                    status nvarchar(20) not null \
                )" [] |> ignore

            // Inserir > 10.000 linhas em perf_customers.profiles (10.000) e perf_sales.orders (15.000)
            let sw = Stopwatch.StartNew()
            connector.ExecuteQuery
                "WITH E1(N) AS (SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1), \
                      E2(N) AS (SELECT 1 FROM E1 a CROSS JOIN E1 b), \
                      E4(N) AS (SELECT 1 FROM E2 a CROSS JOIN E2 b), \
                      Tally(N) AS (SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM E4) \
                 INSERT INTO perf_customers.profiles (id, full_name, tier, credit_limit) \
                 SELECT \
                     N, \
                     CONCAT('Customer ', N), \
                     CASE WHEN N % 3 = 0 THEN 'Premium' WHEN N % 3 = 1 THEN 'Standard' ELSE 'Basic' END, \
                     1000 + (N % 50) * 100 \
                 FROM Tally \
                 WHERE N <= 10000" [] |> ignore

            connector.ExecuteQuery
                "WITH E1(N) AS (SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1 UNION ALL SELECT 1), \
                      E2(N) AS (SELECT 1 FROM E1 a CROSS JOIN E1 b), \
                      E3(N) AS (SELECT 1 FROM E2 a CROSS JOIN E1 b), \
                      E5(N) AS (SELECT 1 FROM E3 a CROSS JOIN E2 b), \
                      Tally(N) AS (SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM E5) \
                 INSERT INTO perf_sales.orders (id, customer_id, order_date, total_amount, status) \
                 SELECT \
                     N, \
                     1 + (N % 10000), \
                     DATEADD(day, -(N % 365), CAST(GETDATE() AS DATE)), \
                     CAST(10.5 + (N % 1000) * 1.5 AS DECIMAL(12,2)), \
                     CASE WHEN N % 4 = 0 THEN 'Completed' WHEN N % 4 = 1 THEN 'Pending' WHEN N % 4 = 2 THEN 'Shipped' ELSE 'Cancelled' END \
                 FROM Tally \
                 WHERE N <= 15000" [] |> ignore
            sw.Stop()
            output.WriteLine($"[SQL Server] Seeded 10k customers and 15k orders in {sw.ElapsedMilliseconds}ms")
        }

    [<Fact>]
    member _.``SQL Server multi-schema tables with >10k rows: schema discovery and performance benchmark`` () =
        task {
            let connector = SqlServerConnector(fixture.ConnectionString)
            do! seedSqlServerData connector

            // 1. Validar contagem > 10.000 linhas em cada schema
            let custCountResult = connector.ExecuteQuery "select count(*) from perf_customers.profiles" []
            let custCount = Convert.ToInt32((custCountResult.Rows |> Seq.head).[0])
            Assert.True(custCount >= 10000, $"Expected >= 10000 customers, got {custCount}")

            let orderCountResult = connector.ExecuteQuery "select count(*) from perf_sales.orders" []
            let orderCount = Convert.ToInt32((orderCountResult.Rows |> Seq.head).[0])
            Assert.True(orderCount >= 10000, $"Expected >= 10000 orders, got {orderCount}")

            output.WriteLine($"[SQL Server] Verified row counts: perf_customers.profiles = {custCount}, perf_sales.orders = {orderCount}")

            // 2. Validar Descoberta de Esquema Multi-Schema
            let asInterface = connector :> IConnector
            let! schema = asInterface.DiscoverSchemaAsync(CancellationToken.None)
            let tableNames = schema |> List.map (fun t -> t.Name)
            Assert.Contains("perf_customers.profiles", tableNames)
            Assert.Contains("perf_sales.orders", tableNames)

            // Configurar Resolver para testes de query
            let dbName = "SqlPerfDb"
            Connections.configure (PerfResolver(Map.ofList [ dbName, connector :> IConnector ]))
            let dbConn = Connections.getDatabaseConnection dbName

            // 3. BENCHMARK: Query filtrada (WHERE total_amount > 1400.0)
            // Cenário A: Execução em Memória (como o Queryable atual faz: SELECT * FROM table -> LINQ to Objects em memória)
            let memAllocBeforeA = GC.GetAllocatedBytesForCurrentThread()
            let swA = Stopwatch.StartNew()
            let inMemoryQueryable = dbConn.Queryable<PerfOrder>("select id, customer_id, order_date, total_amount, status from perf_sales.orders")
            let filteredInMemory =
                query {
                    for o in inMemoryQueryable do
                    where (o.total_amount > 1400.0M)
                    select o
                }
                |> Seq.toList
            swA.Stop()
            let memAllocAfterA = GC.GetAllocatedBytesForCurrentThread()
            let memUsedA = memAllocAfterA - memAllocBeforeA

            // Cenário B: Execução com Pushdown no Banco de Dados (filtro no WHERE da query SQL)
            let memAllocBeforeB = GC.GetAllocatedBytesForCurrentThread()
            let swB = Stopwatch.StartNew()
            let pushdownFiltered =
                dbConn.Query<PerfOrder>(
                    "select id, customer_id, order_date, total_amount, status from perf_sales.orders where total_amount > @min",
                    [ "@min", box 1400.0M ]
                )
                |> Seq.toList
            swB.Stop()
            let memAllocAfterB = GC.GetAllocatedBytesForCurrentThread()
            let memUsedB = memAllocAfterB - memAllocBeforeB

            // Validação de paridade dos resultados
            Assert.Equal(filteredInMemory.Length, pushdownFiltered.Length)
            output.WriteLine($"[SQL Server Benchmark - Filtered Query ({filteredInMemory.Length} rows returned out of 15k)]")
            output.WriteLine($"  - In-Memory (Full scan + LINQ to Objects): {swA.ElapsedMilliseconds}ms, Allocated: {memUsedA / 1024L} KB")
            output.WriteLine($"  - Pushdown (SQL WHERE):                    {swB.ElapsedMilliseconds}ms, Allocated: {memUsedB / 1024L} KB")
            output.WriteLine($"  - Memory Savings with Pushdown:           {double (memUsedA - memUsedB) / double memUsedA * 100.0:F1}%%")

            // 4. BENCHMARK: Agregação (SUM e COUNT)
            // In-Memory: materializa 15.000 registros para calcular a soma
            let swAggMem = Stopwatch.StartNew()
            let allocBeforeAggMem = GC.GetAllocatedBytesForCurrentThread()
            let ordersAll = dbConn.Query<PerfOrder>("select id, customer_id, order_date, total_amount, status from perf_sales.orders") |> Seq.toList
            let sumInMemory = ordersAll |> List.sumBy (fun o -> o.total_amount)
            let allocAfterAggMem = GC.GetAllocatedBytesForCurrentThread()
            swAggMem.Stop()

            // Pushdown: Agregação executada no SQL Server
            let swAggDb = Stopwatch.StartNew()
            let allocBeforeAggDb = GC.GetAllocatedBytesForCurrentThread()
            let sumResult = dbConn.rawQuery("select sum(total_amount) from perf_sales.orders")
            let sumInDb = Convert.ToDecimal((sumResult.Rows |> Seq.head).[0])
            let allocAfterAggDb = GC.GetAllocatedBytesForCurrentThread()
            swAggDb.Stop()

            Assert.Equal(sumInMemory, sumInDb)
            output.WriteLine($"[SQL Server Benchmark - Aggregation (SUM 15k rows)]")
            output.WriteLine($"  - In-Memory Sum: {swAggMem.ElapsedMilliseconds}ms, Allocated: {(allocAfterAggMem - allocBeforeAggMem) / 1024L} KB")
            output.WriteLine($"  - Pushdown Sum:  {swAggDb.ElapsedMilliseconds}ms, Allocated: {(allocAfterAggDb - allocBeforeAggDb) / 1024L} KB")

            // 5. BENCHMARK: Cross-Schema JOIN (perf_sales.orders JOIN perf_customers.profiles)
            // In-Memory JOIN: materializa 15k orders + 10k customers
            let swJoinMem = Stopwatch.StartNew()
            let allocBeforeJoinMem = GC.GetAllocatedBytesForCurrentThread()
            let customersAll = dbConn.Query<PerfCustomer>("select id, full_name, tier, credit_limit from perf_customers.profiles") |> Seq.toList
            let ordersForJoin = dbConn.Query<PerfOrder>("select id, customer_id, order_date, total_amount, status from perf_sales.orders where total_amount > 1450.0") |> Seq.toList
            let joinedInMemory =
                query {
                    for o in ordersForJoin do
                    join c in customersAll on (o.customer_id = c.id)
                    select { OrderId = o.id; CustomerName = c.full_name; TotalAmount = o.total_amount; Status = o.status }
                }
                |> Seq.toList
            let allocAfterJoinMem = GC.GetAllocatedBytesForCurrentThread()
            swJoinMem.Stop()

            // Pushdown JOIN: Banco de dados faz o JOIN e projeta apenas o necessário
            let swJoinDb = Stopwatch.StartNew()
            let allocBeforeJoinDb = GC.GetAllocatedBytesForCurrentThread()
            let joinedInDb =
                dbConn.Query<CustomerOrderSummary>(
                    "select o.id as OrderId, c.full_name as CustomerName, o.total_amount as TotalAmount, o.status as Status \
                     from perf_sales.orders o \
                     join perf_customers.profiles c on o.customer_id = c.id \
                     where o.total_amount > 1450.0"
                )
                |> Seq.toList
            let allocAfterJoinDb = GC.GetAllocatedBytesForCurrentThread()
            swJoinDb.Stop()

            Assert.Equal(joinedInMemory.Length, joinedInDb.Length)
            output.WriteLine($"[SQL Server Benchmark - Cross-Schema JOIN (orders JOIN customers)]")
            output.WriteLine($"  - In-Memory JOIN: {swJoinMem.ElapsedMilliseconds}ms, Allocated: {(allocAfterJoinMem - allocBeforeJoinMem) / 1024L} KB")
            output.WriteLine($"  - Pushdown JOIN:  {swJoinDb.ElapsedMilliseconds}ms, Allocated: {(allocAfterJoinDb - allocBeforeJoinDb) / 1024L} KB")

            // 6. BENCHMARK: Paging & Ordering (Top 10 highest orders out of 15k)
            let swPageMem = Stopwatch.StartNew()
            let allocBeforePageMem = GC.GetAllocatedBytesForCurrentThread()
            let top10InMemory =
                query {
                    for o in dbConn.Queryable<PerfOrder>("select id, customer_id, order_date, total_amount, status from perf_sales.orders") do
                    sortByDescending o.total_amount
                    thenBy o.id
                    take 10
                    select o
                }
                |> Seq.toList
            let allocAfterPageMem = GC.GetAllocatedBytesForCurrentThread()
            swPageMem.Stop()

            let swPageDb = Stopwatch.StartNew()
            let allocBeforePageDb = GC.GetAllocatedBytesForCurrentThread()
            let top10InDb =
                dbConn.Query<PerfOrder>(
                    "select top 10 id, customer_id, order_date, total_amount, status \
                     from perf_sales.orders \
                     order by total_amount desc, id asc"
                )
                |> Seq.toList
            let allocAfterPageDb = GC.GetAllocatedBytesForCurrentThread()
            swPageDb.Stop()

            Assert.Equal(top10InMemory.Length, top10InDb.Length)
            Assert.Equal(top10InMemory.[0].id, top10InDb.[0].id)
            output.WriteLine($"[SQL Server Benchmark - Paging / Top 10 (out of 15k rows)]")
            output.WriteLine($"  - In-Memory Top 10: {swPageMem.ElapsedMilliseconds}ms, Allocated: {(allocAfterPageMem - allocBeforePageMem) / 1024L} KB")
            output.WriteLine($"  - Pushdown Top 10:  {swPageDb.ElapsedMilliseconds}ms, Allocated: {(allocAfterPageDb - allocBeforePageDb) / 1024L} KB")

            // 7. BENCHMARK: Mapping Overhead (15k rows raw ADO.NET vs Connections.getMapper reflection)
            let swRaw = Stopwatch.StartNew()
            let allocBeforeRaw = GC.GetAllocatedBytesForCurrentThread()
            let rawResult = dbConn.rawQuery("select id, customer_id, order_date, total_amount, status from perf_sales.orders")
            let rawRows = rawResult.Rows |> Seq.toList
            let allocAfterRaw = GC.GetAllocatedBytesForCurrentThread()
            swRaw.Stop()

            let swMapped = Stopwatch.StartNew()
            let allocBeforeMapped = GC.GetAllocatedBytesForCurrentThread()
            let mappedRows = dbConn.Query<PerfOrder>("select id, customer_id, order_date, total_amount, status from perf_sales.orders") |> Seq.toList
            let allocAfterMapped = GC.GetAllocatedBytesForCurrentThread()
            swMapped.Stop()

            output.WriteLine($"[SQL Server Benchmark - Mapping Overhead (15k rows)]")
            output.WriteLine($"  - Raw ADO.NET obj[]:       {swRaw.ElapsedMilliseconds}ms, Allocated: {(allocAfterRaw - allocBeforeRaw) / 1024L} KB")
            output.WriteLine($"  - Mapped to F# PerfOrder:  {swMapped.ElapsedMilliseconds}ms, Allocated: {(allocAfterMapped - allocBeforeMapped) / 1024L} KB")
            output.WriteLine($"  - Pure Mapping Overhead:   {swMapped.ElapsedMilliseconds - swRaw.ElapsedMilliseconds}ms, Added Alloc: {((allocAfterMapped - allocBeforeMapped) - (allocAfterRaw - allocBeforeRaw)) / 1024L} KB")
        }
