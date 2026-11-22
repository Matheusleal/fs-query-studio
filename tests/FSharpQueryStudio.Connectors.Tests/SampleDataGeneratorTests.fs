namespace FSharpQueryStudio.Connectors.Tests

open System
open System.IO
open System.Text.Json
open Xunit
open FSharpQueryStudio.Connectors

module SampleDataGeneratorTests =

    [<Fact>]
    let ``generateAllSamples creates multiple SQLite dbs, multiple CSVs, and both English and Portuguese notebooks with valid JSON``
        ()
        =
        let tempDir =
            Path.Combine(Path.GetTempPath(), "fsqs_sample_tests_" + Guid.NewGuid().ToString("N"))

        try
            Directory.CreateDirectory tempDir |> ignore
            let res = SampleDataGenerator.generateAllSamples tempDir

            // Assert all data files exist
            Assert.True(File.Exists res.SqlitePath, $"First SQLite db should exist at {res.SqlitePath}")
            Assert.True(File.Exists res.Sqlite2Path, $"Second SQLite db should exist at {res.Sqlite2Path}")
            Assert.True(File.Exists res.CsvPath, $"First CSV file should exist at {res.CsvPath}")
            Assert.True(File.Exists res.Csv2Path, $"Second CSV file should exist at {res.Csv2Path}")
            Assert.True(File.Exists res.JsonPath, $"Sample JSON file should exist at {res.JsonPath}")
            Assert.True(File.Exists res.XmlPath, $"Sample XML file should exist at {res.XmlPath}")
            Assert.True(File.Exists res.NotebookPath, $"English notebook should exist at {res.NotebookPath}")
            Assert.True(File.Exists res.NotebookPtPath, $"Portuguese notebook should exist at {res.NotebookPtPath}")

            // Verify First SQLite database content (sample_store.db)
            do
                use conn1 =
                    new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={res.SqlitePath}")

                conn1.Open()
                use cmd1 = conn1.CreateCommand()
                cmd1.CommandText <- "SELECT COUNT(*) FROM Customers"
                let custCount = Convert.ToInt32(cmd1.ExecuteScalar())
                Assert.True(custCount > 0, "Customers table should have rows")

            // Verify Second SQLite database content (inventory.db)
            do
                use conn2 =
                    new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={res.Sqlite2Path}")

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

            Assert.StartsWith(
                "CustomerId,CustomerName,LoyaltyTier,TotalSpent,Score,ActiveSince,Feedback",
                csv2Lines.[0]
            )

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
                try
                    Directory.Delete(tempDir, true)
                with _ ->
                    ()

    [<Fact>]
    let ``generateAllSamplesWithLanguage respects language filter`` () =
        let tempDir =
            Path.Combine(Path.GetTempPath(), "fsqs_sample_lang_" + Guid.NewGuid().ToString("N"))

        try
            Directory.CreateDirectory tempDir |> ignore

            // English only
            let dirEn = Path.Combine(tempDir, "en")
            Directory.CreateDirectory dirEn |> ignore
            let resEn = SampleDataGenerator.generateAllSamplesWithLanguage dirEn (Some "en")
            Assert.True(File.Exists resEn.NotebookPath, "English notebook should exist")
            Assert.False(File.Exists resEn.NotebookPtPath, "Portuguese notebook should not exist when language is en")

            // Portuguese only
            let dirPt = Path.Combine(tempDir, "pt")
            Directory.CreateDirectory dirPt |> ignore
            let resPt = SampleDataGenerator.generateAllSamplesWithLanguage dirPt (Some "pt")
            Assert.False(File.Exists resPt.NotebookPath, "English notebook should not exist when language is pt")
            Assert.True(File.Exists resPt.NotebookPtPath, "Portuguese notebook should exist when language is pt")
        finally
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()

            if Directory.Exists tempDir then
                try
                    Directory.Delete(tempDir, true)
                with _ ->
                    ()

    [<Fact>]
    let ``generateAdvancedSamplesWithLanguage respects language filter and produces valid dialect notebooks`` () =
        let tempDir =
            Path.Combine(Path.GetTempPath(), "fsqs_adv_lang_" + Guid.NewGuid().ToString("N"))

        try
            Directory.CreateDirectory tempDir |> ignore

            // English only
            let dirEn = Path.Combine(tempDir, "en")
            Directory.CreateDirectory dirEn |> ignore

            let resEn =
                AdvancedSampleGenerator.generateAdvancedSamplesWithLanguage
                    dirEn
                    [ "postgresql"; "sqlserver" ]
                    (Some "en")

            Assert.Contains(Path.Combine(dirEn, "sample_postgresql.fsqnb"), resEn.NotebooksGenerated)
            Assert.Contains(Path.Combine(dirEn, "sample_sqlserver.fsqnb"), resEn.NotebooksGenerated)
            Assert.DoesNotContain(Path.Combine(dirEn, "sample_postgresql_pt.fsqnb"), resEn.NotebooksGenerated)
            Assert.DoesNotContain(Path.Combine(dirEn, "sample_sqlserver_pt.fsqnb"), resEn.NotebooksGenerated)

            // Portuguese only
            let dirPt = Path.Combine(tempDir, "pt")
            Directory.CreateDirectory dirPt |> ignore

            let resPt =
                AdvancedSampleGenerator.generateAdvancedSamplesWithLanguage
                    dirPt
                    [ "postgresql"; "sqlserver" ]
                    (Some "pt")

            Assert.Contains(Path.Combine(dirPt, "sample_postgresql_pt.fsqnb"), resPt.NotebooksGenerated)
            Assert.Contains(Path.Combine(dirPt, "sample_sqlserver_pt.fsqnb"), resPt.NotebooksGenerated)
            Assert.DoesNotContain(Path.Combine(dirPt, "sample_postgresql.fsqnb"), resPt.NotebooksGenerated)
            Assert.DoesNotContain(Path.Combine(dirPt, "sample_sqlserver.fsqnb"), resPt.NotebooksGenerated)

            // Validate Portuguese PostgreSQL notebook JSON and content
            let pgPtJson = File.ReadAllText(Path.Combine(dirPt, "sample_postgresql_pt.fsqnb"))
            let pgPtDoc = JsonDocument.Parse pgPtJson
            Assert.Equal(1, pgPtDoc.RootElement.GetProperty("version").GetInt32())
            Assert.True(pgPtDoc.RootElement.GetProperty("cells").GetArrayLength() >= 13)
            Assert.Contains("PostgreSQL", pgPtJson)
            Assert.Contains("fullOuterJoin c in Connections.sql.PostgreSQL_Advanced.categories", pgPtJson)
            Assert.Contains("withRecursiveCteSql", pgPtJson)

            // Validate Portuguese SQL Server notebook JSON and content
            let ssPtJson = File.ReadAllText(Path.Combine(dirPt, "sample_sqlserver_pt.fsqnb"))
            let ssPtDoc = JsonDocument.Parse ssPtJson
            Assert.Equal(1, ssPtDoc.RootElement.GetProperty("version").GetInt32())
            Assert.True(ssPtDoc.RootElement.GetProperty("cells").GetArrayLength() >= 13)
            Assert.Contains("SQL Server", ssPtJson)
            Assert.Contains("fullOuterJoin c in Connections.sql.SqlServer_Advanced.Categories", ssPtJson)
            Assert.Contains("withRecursiveCteSql", ssPtJson)
        finally
            if Directory.Exists tempDir then
                try
                    Directory.Delete(tempDir, true)
                with _ ->
                    ()
