namespace FSharpQueryStudio.Connectors.Tests

open System
open System.IO
open System.Text.Json
open Xunit
open FSharpQueryStudio.Connectors

module SampleDataGeneratorTests =

    [<Fact>]
    let ``generateAllSamples creates SQLite, CSV, and both English and Portuguese notebooks with valid JSON`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), "fsqs_sample_tests_" + Guid.NewGuid().ToString("N"))
        try
            Directory.CreateDirectory tempDir |> ignore
            let res = SampleDataGenerator.generateAllSamples tempDir

            Assert.True(File.Exists res.SqlitePath, $"SQLite db should exist at {res.SqlitePath}")
            Assert.True(File.Exists res.CsvPath, $"CSV file should exist at {res.CsvPath}")
            Assert.True(File.Exists res.NotebookPath, $"English notebook should exist at {res.NotebookPath}")
            Assert.True(File.Exists res.NotebookPtPath, $"Portuguese notebook should exist at {res.NotebookPtPath}")

            // Verify English notebook validity & rich content
            let enJson = File.ReadAllText res.NotebookPath
            let enDoc = JsonDocument.Parse enJson
            Assert.Equal(1, enDoc.RootElement.GetProperty("version").GetInt32())
            let enCells = enDoc.RootElement.GetProperty("cells")
            Assert.True(enCells.GetArrayLength() >= 10, "English notebook should have rich multiple cells")

            // Verify English contains anonymous records and shortcuts
            Assert.Contains("Ctrl + Enter", enJson)
            Assert.Contains("CustomerName =", enJson)
            Assert.Contains("SalesFeed", enJson)
            Assert.Contains("SampleStore", enJson)

            // Verify Portuguese notebook validity & rich content
            let ptJson = File.ReadAllText res.NotebookPtPath
            let ptDoc = JsonDocument.Parse ptJson
            Assert.Equal(1, ptDoc.RootElement.GetProperty("version").GetInt32())
            let ptCells = ptDoc.RootElement.GetProperty("cells")
            Assert.True(ptCells.GetArrayLength() >= 10, "Portuguese notebook should have rich multiple cells")

            // Verify Portuguese contains translated anonymous records and Portuguese guidelines
            Assert.Contains("Ctrl + Enter", ptJson)
            Assert.Contains("NomeCliente =", ptJson)
            Assert.Contains("PedidoId =", ptJson)
            Assert.Contains("TicketMedio =", ptJson)
            Assert.Contains("FaturamentoConcluido =", ptJson)

            // Verify CSV file content
            let csvLines = File.ReadAllLines res.CsvPath
            Assert.True(csvLines.Length > 5, "CSV should contain headers and rows")
            Assert.StartsWith("Date,OrderId,Customer", csvLines.[0])
        finally
            if Directory.Exists tempDir then
                try Directory.Delete(tempDir, true) with _ -> ()
