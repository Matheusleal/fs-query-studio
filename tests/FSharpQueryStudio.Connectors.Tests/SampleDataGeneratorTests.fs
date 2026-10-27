namespace FSharpQueryStudio.Connectors.Tests

open System
open System.IO
open System.Text.Json
open Xunit
open FSharpQueryStudio.Connectors

module SampleDataGeneratorTests =

    [<Fact>]
    let ``generateAllSamples creates multiple SQLite dbs, multiple CSVs, and both English and Portuguese notebooks with valid JSON`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), "fsqs_sample_tests_" + Guid.NewGuid().ToString("N"))
        try
            Directory.CreateDirectory tempDir |> ignore
            let res = SampleDataGenerator.generateAllSamples tempDir

            // Assert all 4 data files exist
            Assert.True(File.Exists res.SqlitePath, $"First SQLite db should exist at {res.SqlitePath}")
            Assert.True(File.Exists res.Sqlite2Path, $"Second SQLite db should exist at {res.Sqlite2Path}")
            Assert.True(File.Exists res.CsvPath, $"First CSV file should exist at {res.CsvPath}")
            Assert.True(File.Exists res.Csv2Path, $"Second CSV file should exist at {res.Csv2Path}")
            Assert.True(File.Exists res.NotebookPath, $"English notebook should exist at {res.NotebookPath}")
            Assert.True(File.Exists res.NotebookPtPath, $"Portuguese notebook should exist at {res.NotebookPtPath}")

            // Verify First SQLite database content (sample_store.db)
            do
                use conn1 = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={res.SqlitePath}")
                conn1.Open()
                use cmd1 = conn1.CreateCommand()
                cmd1.CommandText <- "SELECT COUNT(*) FROM Customers"
                let custCount = Convert.ToInt32(cmd1.ExecuteScalar())
                Assert.True(custCount > 0, "Customers table should have rows")

            // Verify Second SQLite database content (inventory.db)
            do
                use conn2 = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={res.Sqlite2Path}")
                conn2.Open()
                use cmd2 = conn2.CreateCommand()
                cmd2.CommandText <- "SELECT COUNT(*) FROM Warehouses"
                let warehouseCount = Convert.ToInt32(cmd2.ExecuteScalar())
                Assert.True(warehouseCount > 0, "Warehouses table should have rows in inventory.db")

                use cmdStock = conn2.CreateCommand()
                cmdStock.CommandText <- "SELECT COUNT(*) FROM StockLevels"
                let stockCount = Convert.ToInt32(cmdStock.ExecuteScalar())
                Assert.True(stockCount > 0, "StockLevels table should have rows in inventory.db")

            // Verify First CSV file content (sales_feed.csv)
            let csv1Lines = File.ReadAllLines res.CsvPath
            Assert.True(csv1Lines.Length > 5, "Sales CSV should contain headers and rows")
            Assert.StartsWith("Date,OrderId,Customer", csv1Lines.[0])

            // Verify Second CSV file content (customers_feed.csv)
            let csv2Lines = File.ReadAllLines res.Csv2Path
            Assert.True(csv2Lines.Length > 5, "Customers CSV should contain headers and rows")
            Assert.StartsWith("CustomerId,CustomerName,LoyaltyTier,TotalSpent,Score,ActiveSince,Feedback", csv2Lines.[0])

            // Verify English notebook validity & rich content with multi-source queries
            let enJson = File.ReadAllText res.NotebookPath
            let enDoc = JsonDocument.Parse enJson
            Assert.Equal(1, enDoc.RootElement.GetProperty("version").GetInt32())
            let enCells = enDoc.RootElement.GetProperty("cells")
            Assert.True(enCells.GetArrayLength() >= 10, "English notebook should have rich multiple cells")

            // Verify English contains references to all 4 sources and shortcuts
            Assert.Contains("Ctrl + Enter", enJson)
            Assert.Contains("SampleStore", enJson)
            Assert.Contains("InventoryStore", enJson)
            Assert.Contains("SalesFeed", enJson)
            Assert.Contains("CustomersFeed", enJson)
            Assert.Contains("CustomerName =", enJson)

            // Verify Portuguese notebook validity & rich content with multi-source queries
            let ptJson = File.ReadAllText res.NotebookPtPath
            let ptDoc = JsonDocument.Parse ptJson
            Assert.Equal(1, ptDoc.RootElement.GetProperty("version").GetInt32())
            let ptCells = ptDoc.RootElement.GetProperty("cells")
            Assert.True(ptCells.GetArrayLength() >= 10, "Portuguese notebook should have rich multiple cells")

            // Verify Portuguese contains references to all 4 sources, Portuguese aliases and guidelines
            Assert.Contains("Ctrl + Enter", ptJson)
            Assert.Contains("SampleStore", ptJson)
            Assert.Contains("InventoryStore", ptJson)
            Assert.Contains("SalesFeed", ptJson)
            Assert.Contains("CustomersFeed", ptJson)
            Assert.Contains("NomeCliente =", ptJson)
            Assert.Contains("PedidoId =", ptJson)
            Assert.Contains("TicketMedio =", ptJson)
            Assert.Contains("NivelFidelidade =", ptJson)
            Assert.Contains("QuantidadeEmEstoque =", ptJson)
        finally
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
            if Directory.Exists tempDir then
                try Directory.Delete(tempDir, true) with _ -> ()
