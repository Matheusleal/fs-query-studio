namespace FSharpQueryStudio.Benchmarks

open System
open System.IO
open System.Threading
open System.Linq
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors
open FSharpQueryStudio.Results
open FSharpQueryStudio.Worker

type BenchmarkCustomer = {
    Id: int
    Name: string
    Email: string
    Age: int
    Active: bool
}

type BenchmarkOrder = {
    Id: int
    CustomerId: int
    Amount: decimal
    Status: string
    CreatedAt: string
}

type private BenchmarkResolver(map: Map<string, IConnector>) =
    interface IConnectionResolver with
        member _.Resolve(name: string) =
            match Map.tryFind name map with
            | Some c -> c
            | None -> failwith $"Connection '{name}' not found."

module QueryBenchmarks =

    let private createSampleDb (path: string) (rowCount: int) : unit =
        if File.Exists path then File.Delete path
        let connector = SqliteConnector(path)
        connector.ExecuteQuery "create table customers (id integer primary key, name text, email text, age integer, active integer)" [] |> ignore
        connector.ExecuteQuery "create table orders (id integer primary key, customer_id integer, amount real, status text, created_at text)" [] |> ignore
        connector.ExecuteQuery "create view active_customers as select id, name, email, age from customers where active = 1" [] |> ignore

        // Seed in a single transaction for speed
        use conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}")
        conn.Open()
        use tx = conn.BeginTransaction()

        use cmdCust = conn.CreateCommand()
        cmdCust.Transaction <- tx
        cmdCust.CommandText <- "insert into customers (id, name, email, age, active) values (@id, @name, @email, @age, @active)"
        let pId = cmdCust.Parameters.Add("@id", Microsoft.Data.Sqlite.SqliteType.Integer)
        let pName = cmdCust.Parameters.Add("@name", Microsoft.Data.Sqlite.SqliteType.Text)
        let pEmail = cmdCust.Parameters.Add("@email", Microsoft.Data.Sqlite.SqliteType.Text)
        let pAge = cmdCust.Parameters.Add("@age", Microsoft.Data.Sqlite.SqliteType.Integer)
        let pActive = cmdCust.Parameters.Add("@active", Microsoft.Data.Sqlite.SqliteType.Integer)

        for i in 1 .. rowCount do
            pId.Value <- i
            pName.Value <- $"Customer_{i}"
            pEmail.Value <- $"cust_{i}@example.com"
            pAge.Value <- 20 + (i % 50)
            pActive.Value <- if i % 2 = 0 then 1 else 0
            cmdCust.ExecuteNonQuery() |> ignore

        use cmdOrd = conn.CreateCommand()
        cmdOrd.Transaction <- tx
        cmdOrd.CommandText <- "insert into orders (id, customer_id, amount, status, created_at) values (@id, @cid, @amount, @status, @dt)"
        let pOId = cmdOrd.Parameters.Add("@id", Microsoft.Data.Sqlite.SqliteType.Integer)
        let pOCid = cmdOrd.Parameters.Add("@cid", Microsoft.Data.Sqlite.SqliteType.Integer)
        let pOAmount = cmdOrd.Parameters.Add("@amount", Microsoft.Data.Sqlite.SqliteType.Real)
        let pOStatus = cmdOrd.Parameters.Add("@status", Microsoft.Data.Sqlite.SqliteType.Text)
        let pODt = cmdOrd.Parameters.Add("@dt", Microsoft.Data.Sqlite.SqliteType.Text)

        for i in 1 .. rowCount do
            pOId.Value <- i
            pOCid.Value <- ((i - 1) % rowCount) + 1
            pOAmount.Value <- double (10 + (i % 500)) + 0.99
            pOStatus.Value <- if i % 3 = 0 then "Completed" elif i % 3 = 1 then "Pending" else "Shipped"
            pODt.Value <- DateTime.UtcNow.AddDays(double (-i)).ToString("yyyy-MM-dd HH:mm:ss")
            cmdOrd.ExecuteNonQuery() |> ignore

        tx.Commit()

    let private createSampleCsv (path: string) (rowCount: int) : unit =
        if File.Exists path then File.Delete path
        use sw = new StreamWriter(path)
        sw.WriteLine("Id,Name,Email,Age,Active")
        for i in 1 .. rowCount do
            sw.WriteLine($"{i},Customer_{i},cust_{i}@example.com,{20 + (i % 50)},{i % 2 = 0}")

    let runAll () : BenchmarkMetric list =
        let tempDir = Path.Combine(Path.GetTempPath(), $"fqs-bench-{Guid.NewGuid():N}")
        Directory.CreateDirectory tempDir |> ignore
        let dbFile = Path.Combine(tempDir, "benchmark.db")
        let csv1kFile = Path.Combine(tempDir, "benchmark_1k.csv")
        let csv10kFile = Path.Combine(tempDir, "benchmark_10k.csv")

        try
            printfn "Populating benchmark dataset (50,000 rows)..."
            createSampleDb dbFile 50000
            createSampleCsv csv1kFile 1000
            createSampleCsv csv10kFile 10000

            let sqlite = SqliteConnector(dbFile)
            let csv1k = CsvConnector(csv1kFile)
            let csv10k = CsvConnector(csv10kFile)

            let resolver = BenchmarkResolver(Map.ofList [
                "BenchSqlite", sqlite :> IConnector
                "BenchCsv1k", csv1k :> IConnector
                "BenchCsv10k", csv10k :> IConnector
            ])
            Connections.configure resolver

            let mutable metrics = []

            // --- 1. Connection & Schema Discovery ---
            printfn "Benchmarking: Connection & Schema Discovery..."
            let m1 = BenchmarkRunner.run "Connection" "Connect & Ping (SQLite)" 50 5 (fun () ->
                use conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbFile}")
                conn.Open()
                use cmd = conn.CreateCommand()
                cmd.CommandText <- "select 1"
                cmd.ExecuteScalar() |> ignore
            )
            metrics <- metrics @ [ m1 ]

            let m2 = BenchmarkRunner.run "Connection" "Schema Discovery (tables & views)" 30 3 (fun () ->
                (sqlite :> IConnector).DiscoverSchemaAsync(CancellationToken.None).GetAwaiter().GetResult()
            )
            metrics <- metrics @ [ m2 ]

            // --- 2. Query Latency: Cold vs Warm ---
            printfn "Benchmarking: Cold vs Warm Queries..."
            let m3 = BenchmarkRunner.run "Latency" "Cold Start Query (New Connection + First Query)" 20 2 (fun () ->
                let c = SqliteConnector(dbFile)
                c.ExecuteQuery "select id, name, email from customers limit 10" []
            )
            metrics <- metrics @ [ m3 ]

            let m4 = BenchmarkRunner.run "Latency" "Warm Query (10 rows, pooled)" 100 10 (fun () ->
                sqlite.ExecuteQuery "select id, name, email from customers limit 10" []
            )
            metrics <- metrics @ [ m4 ]

            let m5 = BenchmarkRunner.run "Latency" "Warm Query Parameterized (@minAmount)" 100 10 (fun () ->
                sqlite.ExecuteQuery "select id, customer_id, amount from orders where amount > @minAmount limit 10" [ ("@minAmount", box 100.0) ]
            )
            metrics <- metrics @ [ m5 ]

            // --- 3. Data Volume Scaling ---
            printfn "Benchmarking: Volume Scaling..."
            let m6 = BenchmarkRunner.run "Volume" "Fetch 100 rows" 50 5 (fun () ->
                sqlite.ExecuteQuery "select id, name, email, age, active from customers limit 100" []
            )
            metrics <- metrics @ [ m6 ]

            let m7 = BenchmarkRunner.run "Volume" "Fetch 1,000 rows" 30 3 (fun () ->
                sqlite.ExecuteQuery "select id, name, email, age, active from customers limit 1000" []
            )
            metrics <- metrics @ [ m7 ]

            let m8 = BenchmarkRunner.run "Volume" "Fetch 10,000 rows" 15 2 (fun () ->
                sqlite.ExecuteQuery "select id, name, email, age, active from customers limit 10000" []
            )
            metrics <- metrics @ [ m8 ]

            let m9 = BenchmarkRunner.run "Volume" "Fetch 50,000 rows" 5 1 (fun () ->
                sqlite.ExecuteQuery "select id, customer_id, amount, status, created_at from orders" []
            )
            metrics <- metrics @ [ m9 ]

            // --- 4. LINQ & Typed Mapping ---
            printfn "Benchmarking: LINQ & Dynamic Typed Mapping..."
            let m10 = BenchmarkRunner.run "Mapping" "Raw ExecuteQuery (1,000 rows)" 30 3 (fun () ->
                sqlite.ExecuteQuery "select id, name, email, age, active from customers limit 1000" []
            )
            metrics <- metrics @ [ m10 ]

            let m11 = BenchmarkRunner.run "Mapping" "Typed Connections.query<Customer> (1,000 rows)" 30 3 (fun () ->
                let res = Connections.query<BenchmarkCustomer> "BenchSqlite" "select id, name, email, age, active from customers limit 1000" []
                res |> Seq.length |> ignore
            )
            metrics <- metrics @ [ m11 ]

            let m12 = BenchmarkRunner.run "Mapping" "LINQ query { ... } Filter & Sort (1,000 rows)" 30 3 (fun () ->
                let customers = Connections.query<BenchmarkCustomer> "BenchSqlite" "select id, name, email, age, active from customers limit 1000" []
                let filtered =
                    query {
                        for c in customers do
                        where (c.Age >= 30 && c.Active)
                        sortBy c.Name
                        select c.Email
                    }
                    |> Seq.toList
                filtered |> List.length |> ignore
            )
            metrics <- metrics @ [ m12 ]

            // --- 5. File-Based Queries (CSV) ---
            printfn "Benchmarking: CSV Queries..."
            let m13 = BenchmarkRunner.run "CSV" "queryCsv<Customer> (1,000 records)" 30 3 (fun () ->
                let rows = Connections.queryCsv<BenchmarkCustomer> "BenchCsv1k"
                rows |> Seq.length |> ignore
            )
            metrics <- metrics @ [ m13 ]

            let m14 = BenchmarkRunner.run "CSV" "queryCsv<Customer> (10,000 records)" 10 2 (fun () ->
                let rows = Connections.queryCsv<BenchmarkCustomer> "BenchCsv10k"
                rows |> Seq.length |> ignore
            )
            metrics <- metrics @ [ m14 ]

            metrics
        finally
            try
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
                if Directory.Exists tempDir then
                    Directory.Delete(tempDir, true)
            with _ -> ()
