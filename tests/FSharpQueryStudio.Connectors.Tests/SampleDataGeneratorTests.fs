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

    [<Fact>]
    let ``generateAdvancedSamples with all engines produces all 5 SQL scripts and English notebooks`` () =
        let tempDir =
            Path.Combine(Path.GetTempPath(), "fsqs_adv_all_en_" + Guid.NewGuid().ToString("N"))

        try
            Directory.CreateDirectory tempDir |> ignore
            let res =
                AdvancedSampleGenerator.generateAdvancedSamplesWithLanguage tempDir [ "all" ] (Some "en")

            Assert.Equal<string list>(
                [ "postgresql"; "sqlserver"; "mysql"; "mariadb"; "oracle" ],
                res.SelectedEngines
            )
            Assert.True(Option.isSome res.DockerComposePath)
            let composeContent = File.ReadAllText(Option.get res.DockerComposePath)
            Assert.Contains("postgres:16-alpine", composeContent)
            Assert.Contains("mcr.microsoft.com/mssql/server:2022-latest", composeContent)
            Assert.Contains("mysql:8.4", composeContent)
            Assert.Contains("mariadb:11.4", composeContent)
            Assert.Contains("gvenzl/oracle-free:23-slim", composeContent)

            // Assert 5 init scripts generated
            Assert.Equal(5, res.InitScripts.Length)
            let expectedScripts =
                [ "init_postgres.sql"; "init_sqlserver.sql"; "init_mysql.sql"; "init_mariadb.sql"; "init_oracle.sql" ]
            for s in expectedScripts do
                let full = Path.Combine(tempDir, "init-scripts", s)
                Assert.True(File.Exists full, $"Init script should exist at {full}")

            // Assert 5 English notebooks generated, no Portuguese notebooks
            Assert.Equal(5, res.NotebooksGenerated.Length)
            let expectedNbs =
                [ "sample_postgresql.fsqnb"; "sample_sqlserver.fsqnb"; "sample_mysql.fsqnb"; "sample_mariadb.fsqnb"; "sample_oracle.fsqnb" ]
            for nb in expectedNbs do
                let full = Path.Combine(tempDir, nb)
                Assert.True(File.Exists full, $"English notebook should exist at {full}")
                let json = File.ReadAllText full
                let doc = JsonDocument.Parse json
                Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32())

            let unexpectedNbs =
                [ "sample_postgresql_pt.fsqnb"; "sample_sqlserver_pt.fsqnb"; "sample_mysql_pt.fsqnb"; "sample_mariadb_pt.fsqnb"; "sample_oracle_pt.fsqnb" ]
            for nb in unexpectedNbs do
                let full = Path.Combine(tempDir, nb)
                Assert.False(File.Exists full, $"Portuguese notebook should not exist when lang is en at {full}")

            // Assert 5 registered connections
            Assert.Equal(5, res.RegisteredConnections.Length)
            Assert.Contains("PostgreSQL_Advanced", res.RegisteredConnections)
            Assert.Contains("SqlServer_Advanced", res.RegisteredConnections)
            Assert.Contains("MySQL_Advanced", res.RegisteredConnections)
            Assert.Contains("MariaDB_Advanced", res.RegisteredConnections)
            Assert.Contains("Oracle_Advanced", res.RegisteredConnections)
        finally
            if Directory.Exists tempDir then
                try Directory.Delete(tempDir, true) with _ -> ()

    [<Fact>]
    let ``generateAdvancedSamples with all engines produces all 5 Portuguese notebooks when language is pt`` () =
        let tempDir =
            Path.Combine(Path.GetTempPath(), "fsqs_adv_all_pt_" + Guid.NewGuid().ToString("N"))

        try
            Directory.CreateDirectory tempDir |> ignore
            let res =
                AdvancedSampleGenerator.generateAdvancedSamplesWithLanguage tempDir [ "all" ] (Some "pt")

            Assert.Equal(5, res.NotebooksGenerated.Length)
            let expectedPtNbs =
                [ "sample_postgresql_pt.fsqnb"; "sample_sqlserver_pt.fsqnb"; "sample_mysql_pt.fsqnb"; "sample_mariadb_pt.fsqnb"; "sample_oracle_pt.fsqnb" ]
            for nb in expectedPtNbs do
                let full = Path.Combine(tempDir, nb)
                Assert.True(File.Exists full, $"Portuguese notebook should exist at {full}")
                let json = File.ReadAllText full
                let doc = JsonDocument.Parse json
                Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32())

            let unexpectedEnNbs =
                [ "sample_postgresql.fsqnb"; "sample_sqlserver.fsqnb"; "sample_mysql.fsqnb"; "sample_mariadb.fsqnb"; "sample_oracle.fsqnb" ]
            for nb in unexpectedEnNbs do
                let full = Path.Combine(tempDir, nb)
                Assert.False(File.Exists full, $"English notebook should not exist at {full}")
        finally
            if Directory.Exists tempDir then
                try Directory.Delete(tempDir, true) with _ -> ()

    [<Fact>]
    let ``generateAdvancedSamples with default language produces both EN and PT notebooks (10 notebooks)`` () =
        let tempDir =
            Path.Combine(Path.GetTempPath(), "fsqs_adv_all_both_" + Guid.NewGuid().ToString("N"))

        try
            Directory.CreateDirectory tempDir |> ignore
            let res =
                AdvancedSampleGenerator.generateAdvancedSamples tempDir [ "all" ]

            Assert.Equal(10, res.NotebooksGenerated.Length)
            let allExpected =
                [
                    "sample_postgresql.fsqnb"; "sample_postgresql_pt.fsqnb"
                    "sample_sqlserver.fsqnb"; "sample_sqlserver_pt.fsqnb"
                    "sample_mysql.fsqnb"; "sample_mysql_pt.fsqnb"
                    "sample_mariadb.fsqnb"; "sample_mariadb_pt.fsqnb"
                    "sample_oracle.fsqnb"; "sample_oracle_pt.fsqnb"
                ]
            for nb in allExpected do
                let full = Path.Combine(tempDir, nb)
                Assert.True(File.Exists full, $"Notebook should exist at {full}")
        finally
            if Directory.Exists tempDir then
                try Directory.Delete(tempDir, true) with _ -> ()

    [<Fact>]
    let ``generateAdvancedSamples with MySQL and MariaDB produces dedicated scripts and notebooks`` () =
        let tempDir =
            Path.Combine(Path.GetTempPath(), "fsqs_adv_mysql_maria_" + Guid.NewGuid().ToString("N"))

        try
            Directory.CreateDirectory tempDir |> ignore
            let res =
                AdvancedSampleGenerator.generateAdvancedSamplesWithLanguage tempDir [ "mysql"; "mariadb" ] None

            Assert.Equal<string list>([ "mysql"; "mariadb" ], res.SelectedEngines)
            Assert.Equal(2, res.InitScripts.Length)
            Assert.Equal(4, res.NotebooksGenerated.Length)

            let myScript = Path.Combine(tempDir, "init-scripts", "init_mysql.sql")
            Assert.True(File.Exists myScript)
            let mySqlContent = File.ReadAllText myScript
            Assert.Contains("CREATE DATABASE IF NOT EXISTS fsqs_demo;", mySqlContent)

            let mariaScript = Path.Combine(tempDir, "init-scripts", "init_mariadb.sql")
            Assert.True(File.Exists mariaScript)
            let mariaSqlContent = File.ReadAllText mariaScript
            Assert.Contains("CREATE DATABASE IF NOT EXISTS fsqs_demo;", mariaSqlContent)

            let myNbEn = Path.Combine(tempDir, "sample_mysql.fsqnb")
            let myNbPt = Path.Combine(tempDir, "sample_mysql_pt.fsqnb")
            Assert.True(File.Exists myNbEn)
            Assert.True(File.Exists myNbPt)

            let mariaNbEn = Path.Combine(tempDir, "sample_mariadb.fsqnb")
            let mariaNbPt = Path.Combine(tempDir, "sample_mariadb_pt.fsqnb")
            Assert.True(File.Exists mariaNbEn)
            Assert.True(File.Exists mariaNbPt)
        finally
            if Directory.Exists tempDir then
                try Directory.Delete(tempDir, true) with _ -> ()

    [<Fact>]
    let ``generateAdvancedSamples with Oracle produces dedicated scripts and notebooks`` () =
        let tempDir =
            Path.Combine(Path.GetTempPath(), "fsqs_adv_oracle_" + Guid.NewGuid().ToString("N"))

        try
            Directory.CreateDirectory tempDir |> ignore
            let res =
                AdvancedSampleGenerator.generateAdvancedSamplesWithLanguage tempDir [ "oracle" ] None

            Assert.Equal<string list>([ "oracle" ], res.SelectedEngines)
            Assert.Single(res.InitScripts) |> ignore
            Assert.Equal(2, res.NotebooksGenerated.Length)

            let oraScript = Path.Combine(tempDir, "init-scripts", "init_oracle.sql")
            Assert.True(File.Exists oraScript)
            let oraContent = File.ReadAllText oraScript
            Assert.Contains("CATEGORIES", oraContent)
            Assert.Contains("PRODUCTS", oraContent)

            let oraNbEn = Path.Combine(tempDir, "sample_oracle.fsqnb")
            let oraNbPt = Path.Combine(tempDir, "sample_oracle_pt.fsqnb")
            Assert.True(File.Exists oraNbEn)
            Assert.True(File.Exists oraNbPt)
        finally
            if Directory.Exists tempDir then
                try Directory.Delete(tempDir, true) with _ -> ()

    [<Fact>]
    let ``generateAdvancedSamples handles engine aliases and whitespace normalization`` () =
        let tempDir =
            Path.Combine(Path.GetTempPath(), "fsqs_adv_aliases_" + Guid.NewGuid().ToString("N"))

        try
            Directory.CreateDirectory tempDir |> ignore
            let res =
                AdvancedSampleGenerator.generateAdvancedSamplesWithLanguage
                    tempDir
                    [ "  POSTGRES  "; "mssql"; "postgres" ]
                    (Some "en")

            // Keeps normalized engine strings, removes duplicates
            Assert.Contains("postgres", res.SelectedEngines)
            Assert.Contains("mssql", res.SelectedEngines)
            Assert.Equal(2, res.SelectedEngines.Length)
            Assert.Equal(2, res.InitScripts.Length)
            Assert.Equal(2, res.NotebooksGenerated.Length)
        finally
            if Directory.Exists tempDir then
                try Directory.Delete(tempDir, true) with _ -> ()

    [<Fact>]
    let ``generateAdvancedSamples with empty or unsupported engines produces empty result safely`` () =
        let tempDir =
            Path.Combine(Path.GetTempPath(), "fsqs_adv_empty_" + Guid.NewGuid().ToString("N"))

        try
            Directory.CreateDirectory tempDir |> ignore
            let res =
                AdvancedSampleGenerator.generateAdvancedSamplesWithLanguage
                    tempDir
                    [ "unsupported_engine"; "invalid" ]
                    None

            Assert.Empty res.SelectedEngines
            Assert.Empty res.InitScripts
            Assert.Empty res.NotebooksGenerated
            Assert.Empty res.RegisteredConnections
            Assert.True(Option.isNone res.DockerComposePath)
            Assert.False(Directory.Exists(Path.Combine(tempDir, "init-scripts")))
        finally
            if Directory.Exists tempDir then
                try Directory.Delete(tempDir, true) with _ -> ()

    [<Fact>]
    let ``tryGetKnownSampleTables returns expected schema definitions for all database connectors`` () =
        // PostgreSQL
        let pgOpt = AdvancedSampleGenerator.tryGetKnownSampleTables "PostgreSQL_Advanced"
        Assert.True(Option.isSome pgOpt)
        let pgTables = Option.get pgOpt
        Assert.Equal(5, pgTables.Length)
        Assert.Contains(pgTables, fun t -> t.Name = "fsqs_demo.categories")
        Assert.Contains(pgTables, fun t -> t.Name = "fsqs_demo.products")

        // SQL Server
        let ssOpt = AdvancedSampleGenerator.tryGetKnownSampleTables "SqlServer_Advanced"
        Assert.True(Option.isSome ssOpt)
        let ssTables = Option.get ssOpt
        Assert.Equal(5, ssTables.Length)
        Assert.Contains(ssTables, fun t -> t.Name = "master.dbo.Categories")
        Assert.Contains(ssTables, fun t -> t.Name = "master.dbo.Products")

        // MySQL
        let myOpt = AdvancedSampleGenerator.tryGetKnownSampleTables "MySQL_Advanced"
        Assert.True(Option.isSome myOpt)
        let myTables = Option.get myOpt
        Assert.Equal(5, myTables.Length)
        Assert.Contains(myTables, fun t -> t.Name = "fsqs_demo.categories")

        // MariaDB
        let mariaOpt = AdvancedSampleGenerator.tryGetKnownSampleTables "MariaDB_Advanced"
        Assert.True(Option.isSome mariaOpt)
        let mariaTables = Option.get mariaOpt
        Assert.Equal(5, mariaTables.Length)
        Assert.Contains(mariaTables, fun t -> t.Name = "fsqs_demo.categories")

        // Oracle
        let oraOpt = AdvancedSampleGenerator.tryGetKnownSampleTables "Oracle_Advanced"
        Assert.True(Option.isSome oraOpt)
        let oraTables = Option.get oraOpt
        Assert.Equal(5, oraTables.Length)
        Assert.Contains(oraTables, fun t -> t.Name = "FREEPDB1.SYSTEM.CATEGORIES")
        Assert.Contains(oraTables, fun t -> t.Name = "FREEPDB1.SYSTEM.PRODUCTS")

    [<Fact>]
    let ``tryGetKnownSampleTables returns None for unknown, empty, or file-based connections`` () =
        Assert.True(Option.isNone (AdvancedSampleGenerator.tryGetKnownSampleTables ""))
        Assert.True(Option.isNone (AdvancedSampleGenerator.tryGetKnownSampleTables "NonExistent_Db"))
        Assert.True(Option.isNone (AdvancedSampleGenerator.tryGetKnownSampleTables "SampleStore"))
        Assert.True(Option.isNone (AdvancedSampleGenerator.tryGetKnownSampleTables "SalesFeed"))
        Assert.True(Option.isNone (AdvancedSampleGenerator.tryGetKnownSampleTables null))

    [<Fact>]
    let ``createSampleDemoNotebook and createSampleDemoNotebookPt create missing parent directories`` () =
        let tempDir =
            Path.Combine(Path.GetTempPath(), "fsqs_demo_dir_" + Guid.NewGuid().ToString("N"))
        try
            let subDirEn = Path.Combine(tempDir, "nested", "en")
            let nbPathEn = Path.Combine(subDirEn, "demo_en.fsqnb")
            SampleDataGenerator.createSampleDemoNotebook nbPathEn
            Assert.True(File.Exists nbPathEn)

            let subDirPt = Path.Combine(tempDir, "nested", "pt")
            let nbPathPt = Path.Combine(subDirPt, "demo_pt.fsqnb")
            SampleDataGenerator.createSampleDemoNotebookPt nbPathPt
            Assert.True(File.Exists nbPathPt)
        finally
            if Directory.Exists tempDir then
                try Directory.Delete(tempDir, true) with _ -> ()

    [<Fact>]
    let ``AdvancedSampleNotebookCommon mkMarkdown mkCode and serializeNotebook format correctly`` () =
        let md = AdvancedSampleNotebookCommon.mkMarkdown "  # Title  "
        Assert.Equal("markdown", md.kind)
        Assert.True(Option.isNone md.language)
        Assert.Equal("# Title", md.value)

        let code = AdvancedSampleNotebookCommon.mkCode "  let x = 42  "
        Assert.Equal("code", code.kind)
        Assert.Equal(Some "fsharp", code.language)
        Assert.Equal("let x = 42", code.value)

        let serialized = AdvancedSampleNotebookCommon.serializeNotebook [ md; code ]
        Assert.Contains("\"version\": 1", serialized)
        Assert.Contains("\"cells\":", serialized)
        Assert.Contains("# Title", serialized)
        Assert.Contains("let x = 42", serialized)

    [<Fact>]
    let ``getMariaDbInitSql returns same valid DDL as MySQL init SQL`` () =
        let mariaSql = AdvancedSampleInitScripts.getMariaDbInitSql ()
        let mySql = AdvancedSampleInitScripts.getMySqlInitSql ()
        Assert.Equal(mySql, mariaSql)
        Assert.Contains("CREATE DATABASE IF NOT EXISTS fsqs_demo;", mariaSql)

    [<Fact>]
    let ``SampleDataSqlite ensureSampleStoreIndexes handles non-existent file gracefully scenario 1`` () =
        let nonExistentPath = Path.Combine(Path.GetTempPath(), $"missing_store_{Guid.NewGuid():N}.db")
        SampleDataSqlite.ensureSampleStoreIndexes nonExistentPath
        Assert.False(File.Exists nonExistentPath)

    [<Fact>]
    let ``SampleDataSqlite ensureSampleStoreIndexes handles non-existent file gracefully scenario 2`` () =
        let nonExistentPath = Path.Combine(Path.GetTempPath(), $"missing_store_2_{Guid.NewGuid():N}.db")
        SampleDataSqlite.ensureSampleStoreIndexes nonExistentPath
        Assert.False(File.Exists nonExistentPath)

    [<Fact>]
    let ``SampleDataSqlite ensureSampleStoreIndexes applies indexes to existing database scenario 1`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), $"fsqs_test_idx_1_{Guid.NewGuid():N}")
        try
            Directory.CreateDirectory tempDir |> ignore
            let dbPath = Path.Combine(tempDir, "store.db")
            SampleDataSqlite.createSampleStoreDatabase dbPath
            Assert.True(File.Exists dbPath)
            SampleDataSqlite.ensureSampleStoreIndexes dbPath
            use conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Pooling=False")
            conn.Open()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_Customers_Country'"
            let count = Convert.ToInt32(cmd.ExecuteScalar())
            Assert.Equal(1, count)
        finally
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
            if Directory.Exists tempDir then
                try Directory.Delete(tempDir, true) with _ -> ()

    [<Fact>]
    let ``SampleDataSqlite ensureSampleStoreIndexes applies indexes to existing database scenario 2`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), $"fsqs_test_idx_2_{Guid.NewGuid():N}")
        try
            Directory.CreateDirectory tempDir |> ignore
            let dbPath = Path.Combine(tempDir, "store2.db")
            SampleDataSqlite.createSampleStoreDatabase dbPath
            Assert.True(File.Exists dbPath)
            SampleDataSqlite.ensureSampleStoreIndexes dbPath
            use conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Pooling=False")
            conn.Open()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_Orders_OrderDate'"
            let count = Convert.ToInt32(cmd.ExecuteScalar())
            Assert.Equal(1, count)
        finally
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
            if Directory.Exists tempDir then
                try Directory.Delete(tempDir, true) with _ -> ()

    [<Fact>]
    let ``SampleDataSqlite ensureSampleStoreIndexes handles corrupt file gracefully scenario 1`` () =
        let tempFile = Path.Combine(Path.GetTempPath(), $"corrupt_store_1_{Guid.NewGuid():N}.db")
        try
            File.WriteAllText(tempFile, "NOT A VALID SQLITE DATABASE FILE CONTENT")
            SampleDataSqlite.ensureSampleStoreIndexes tempFile
            Assert.True(File.Exists tempFile)
        finally
            if File.Exists tempFile then
                try File.Delete tempFile with _ -> ()

    [<Fact>]
    let ``SampleDataSqlite ensureSampleStoreIndexes handles corrupt file gracefully scenario 2`` () =
        let tempFile = Path.Combine(Path.GetTempPath(), $"corrupt_store_2_{Guid.NewGuid():N}.db")
        try
            File.WriteAllBytes(tempFile, [| 0uy; 1uy; 2uy; 3uy |])
            SampleDataSqlite.ensureSampleStoreIndexes tempFile
            Assert.True(File.Exists tempFile)
        finally
            if File.Exists tempFile then
                try File.Delete tempFile with _ -> ()

    [<Fact>]
    let ``SampleDataSqlite ensureInventoryIndexes handles non-existent file gracefully scenario 1`` () =
        let nonExistentPath = Path.Combine(Path.GetTempPath(), $"missing_inv_{Guid.NewGuid():N}.db")
        SampleDataSqlite.ensureInventoryIndexes nonExistentPath
        Assert.False(File.Exists nonExistentPath)

    [<Fact>]
    let ``SampleDataSqlite ensureInventoryIndexes handles non-existent file gracefully scenario 2`` () =
        let nonExistentPath = Path.Combine(Path.GetTempPath(), $"missing_inv_2_{Guid.NewGuid():N}.db")
        SampleDataSqlite.ensureInventoryIndexes nonExistentPath
        Assert.False(File.Exists nonExistentPath)

    [<Fact>]
    let ``SampleDataSqlite ensureInventoryIndexes applies indexes to existing database scenario 1`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), $"fsqs_test_inv_idx_1_{Guid.NewGuid():N}")
        try
            Directory.CreateDirectory tempDir |> ignore
            let dbPath = Path.Combine(tempDir, "inventory.db")
            SampleDataSqlite.createInventoryDatabase dbPath
            Assert.True(File.Exists dbPath)
            SampleDataSqlite.ensureInventoryIndexes dbPath
            use conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Pooling=False")
            conn.Open()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_StockLevels_WarehouseId'"
            let count = Convert.ToInt32(cmd.ExecuteScalar())
            Assert.Equal(1, count)
        finally
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
            if Directory.Exists tempDir then
                try Directory.Delete(tempDir, true) with _ -> ()

    [<Fact>]
    let ``SampleDataSqlite ensureInventoryIndexes applies indexes to existing database scenario 2`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), $"fsqs_test_inv_idx_2_{Guid.NewGuid():N}")
        try
            Directory.CreateDirectory tempDir |> ignore
            let dbPath = Path.Combine(tempDir, "inventory2.db")
            SampleDataSqlite.createInventoryDatabase dbPath
            Assert.True(File.Exists dbPath)
            SampleDataSqlite.ensureInventoryIndexes dbPath
            use conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Pooling=False")
            conn.Open()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_Suppliers_Country'"
            let count = Convert.ToInt32(cmd.ExecuteScalar())
            Assert.Equal(1, count)
        finally
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
            if Directory.Exists tempDir then
                try Directory.Delete(tempDir, true) with _ -> ()

    [<Fact>]
    let ``SampleDataSqlite ensureInventoryIndexes handles corrupt file gracefully scenario 1`` () =
        let tempFile = Path.Combine(Path.GetTempPath(), $"corrupt_inv_1_{Guid.NewGuid():N}.db")
        try
            File.WriteAllText(tempFile, "MALFORMED SQLITE HEADER")
            SampleDataSqlite.ensureInventoryIndexes tempFile
            Assert.True(File.Exists tempFile)
        finally
            if File.Exists tempFile then
                try File.Delete tempFile with _ -> ()

    [<Fact>]
    let ``SampleDataSqlite ensureInventoryIndexes handles corrupt file gracefully scenario 2`` () =
        let tempFile = Path.Combine(Path.GetTempPath(), $"corrupt_inv_2_{Guid.NewGuid():N}.db")
        try
            File.WriteAllBytes(tempFile, [| 255uy; 254uy; 0uy; 0uy |])
            SampleDataSqlite.ensureInventoryIndexes tempFile
            Assert.True(File.Exists tempFile)
        finally
            if File.Exists tempFile then
                try File.Delete tempFile with _ -> ()

    [<Fact>]
    let ``SampleDataSqlite createSampleStoreDatabase creates missing nested directory scenario 1`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), $"fsqs_nested_store_1_{Guid.NewGuid():N}", "nested1", "nested2")
        let dbPath = Path.Combine(tempDir, "sample_store.db")
        try
            Assert.False(Directory.Exists tempDir)
            SampleDataSqlite.createSampleStoreDatabase dbPath
            Assert.True(Directory.Exists tempDir)
            Assert.True(File.Exists dbPath)
        finally
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
            let root = Path.Combine(Path.GetTempPath(), Path.GetPathRoot(tempDir))
            try Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(tempDir)), true) with _ -> ()

    [<Fact>]
    let ``SampleDataSqlite createSampleStoreDatabase creates missing nested directory scenario 2`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), $"fsqs_nested_store_2_{Guid.NewGuid():N}", "deep", "folder")
        let dbPath = Path.Combine(tempDir, "sample_store.db")
        try
            Assert.False(Directory.Exists tempDir)
            SampleDataSqlite.createSampleStoreDatabase dbPath
            Assert.True(Directory.Exists tempDir)
            Assert.True(File.Exists dbPath)
        finally
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
            try Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(tempDir)), true) with _ -> ()

    [<Fact>]
    let ``SampleDataSqlite createInventoryDatabase creates missing nested directory scenario 1`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), $"fsqs_nested_inv_1_{Guid.NewGuid():N}", "nested_inv_1")
        let dbPath = Path.Combine(tempDir, "inventory.db")
        try
            Assert.False(Directory.Exists tempDir)
            SampleDataSqlite.createInventoryDatabase dbPath
            Assert.True(Directory.Exists tempDir)
            Assert.True(File.Exists dbPath)
        finally
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
            try Directory.Delete(tempDir, true) with _ -> ()

    [<Fact>]
    let ``SampleDataSqlite createInventoryDatabase creates missing nested directory scenario 2`` () =
        let tempDir = Path.Combine(Path.GetTempPath(), $"fsqs_nested_inv_2_{Guid.NewGuid():N}", "nested_inv_2")
        let dbPath = Path.Combine(tempDir, "inventory.db")
        try
            Assert.False(Directory.Exists tempDir)
            SampleDataSqlite.createInventoryDatabase dbPath
            Assert.True(Directory.Exists tempDir)
            Assert.True(File.Exists dbPath)
        finally
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
            try Directory.Delete(tempDir, true) with _ -> ()
