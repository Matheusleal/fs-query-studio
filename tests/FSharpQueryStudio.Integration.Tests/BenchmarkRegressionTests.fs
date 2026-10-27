namespace FSharpQueryStudio.Integration.Tests

open System
open System.IO
open System.Diagnostics
open Xunit
open FSharpQueryStudio.Connectors

type BenchRow = { Id: int; Name: string; Value: double }

module BenchmarkRegressionTests =

    [<Fact>]
    [<Trait("Category", "LocalOnly")>]
    let ``SQLite connection and query performance metrics remain within expected thresholds`` () =
        let tempDb = Path.Combine(Path.GetTempPath(), $"fqs-bench-test-{Guid.NewGuid():N}.db")
        try
            let connector = SqliteConnector(tempDb)
            connector.ExecuteQuery "create table metrics (id integer primary key, name text, value real)" [] |> ignore

            use conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={tempDb}")
            conn.Open()
            use tx = conn.BeginTransaction()
            use cmd = conn.CreateCommand()
            cmd.Transaction <- tx
            cmd.CommandText <- "insert into metrics values (@id, @name, @val)"
            let pId = cmd.Parameters.Add("@id", Microsoft.Data.Sqlite.SqliteType.Integer)
            let pName = cmd.Parameters.Add("@name", Microsoft.Data.Sqlite.SqliteType.Text)
            let pVal = cmd.Parameters.Add("@val", Microsoft.Data.Sqlite.SqliteType.Real)

            for i in 1 .. 500 do
                pId.Value <- i
                pName.Value <- $"Row_{i}"
                pVal.Value <- double i * 1.5
                cmd.ExecuteNonQuery() |> ignore

            tx.Commit()

            // 1. Connection ping latency < 50ms
            let sw = Stopwatch.StartNew()
            use pingConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={tempDb}")
            pingConn.Open()
            use pingCmd = pingConn.CreateCommand()
            pingCmd.CommandText <- "select 1"
            let scalar = pingCmd.ExecuteScalar()
            sw.Stop()
            Assert.Equal(1L, Convert.ToInt64(scalar))
            Assert.True(sw.ElapsedMilliseconds < 50L, $"Ping exceeded threshold: {sw.ElapsedMilliseconds}ms")

            // 2. Query execution latency < 100ms for 500 rows
            let swQuery = Stopwatch.StartNew()
            let result = connector.ExecuteQuery "select id, name, value from metrics" []
            swQuery.Stop()
            Assert.Equal(500, Seq.length result.Rows)
            Assert.True(swQuery.ElapsedMilliseconds < 100L, $"Query latency exceeded threshold: {swQuery.ElapsedMilliseconds}ms")

            // 3. Memory allocation per op check
            let startAlloc = GC.GetAllocatedBytesForCurrentThread()
            for _ in 1 .. 10 do
                connector.ExecuteQuery "select id, name, value from metrics limit 10" [] |> ignore
            let totalAlloc = GC.GetAllocatedBytesForCurrentThread() - startAlloc
            let allocPerOpKb = (double totalAlloc / 10.0) / 1024.0
            Assert.True(allocPerOpKb < 100.0, $"Alloc per op exceeded threshold: {allocPerOpKb} KB")
        finally
            try
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
                if File.Exists tempDb then File.Delete tempDb
            with _ -> ()
