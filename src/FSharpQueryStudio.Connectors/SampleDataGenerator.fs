namespace FSharpQueryStudio.Connectors

open System
open System.IO
open SampleDataSqlite
open SampleDataFiles
open SampleDemoNotebookEn
open SampleDemoNotebookPt

module SampleDataGenerator =

    let DefaultGitHubRestUrl = "https://api.github.com/orgs/fsprojects/repos?per_page=8"

    type SampleFilesResult =
        {
            SqlitePath: string
            Sqlite2Path: string
            CsvPath: string
            Csv2Path: string
            ExcelPath: string
            Excel2Path: string
            JsonPath: string
            XmlPath: string
            RestUrl: string
            NotebookPath: string
            NotebookPtPath: string
        }


    let createSampleStoreDatabase = SampleDataSqlite.createSampleStoreDatabase
    let createInventoryDatabase = SampleDataSqlite.createInventoryDatabase
    let ensureSampleStoreIndexes = SampleDataSqlite.ensureSampleStoreIndexes
    let ensureInventoryIndexes = SampleDataSqlite.ensureInventoryIndexes

    let createSampleCsv = SampleDataFiles.createSampleCsv
    let createSampleCustomersCsv = SampleDataFiles.createSampleCustomersCsv
    let createSampleExcelWorkbook = SampleDataFiles.createSampleExcelWorkbook

    let createSampleCommerceExcelWorkbook =
        SampleDataFiles.createSampleCommerceExcelWorkbook

    let createSampleJsonFile = SampleDataFiles.createSampleJsonFile
    let createSampleXmlFile = SampleDataFiles.createSampleXmlFile

    let getSampleDemoEnglishNotebook = SampleDemoNotebookEn.getSampleDemoEnglishNotebook

    let getSampleDemoPortugueseNotebook =
        SampleDemoNotebookPt.getSampleDemoPortugueseNotebook

    let createSampleDemoNotebook (filePath: string) : unit =
        let dir = Path.GetDirectoryName(filePath)

        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        File.WriteAllText(filePath, getSampleDemoEnglishNotebook ())

    let createSampleDemoNotebookPt (filePath: string) : unit =
        let dir = Path.GetDirectoryName(filePath)

        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory(dir) |> ignore

        File.WriteAllText(filePath, getSampleDemoPortugueseNotebook ())

    let generateAllSamplesWithLanguage (targetDir: string) (language: string option) : SampleFilesResult =
        let sampleDir = Path.Combine(targetDir, "data_sources")

        if not (Directory.Exists sampleDir) then
            Directory.CreateDirectory sampleDir |> ignore

        let readmePath = Path.Combine(sampleDir, "README.txt")

        if not (File.Exists readmePath) then
            File.WriteAllText(
                readmePath,
                "Place your .csv, .xlsx, .xls, .json, .xml, or .sqlite/.db files in this directory.\nF# Query Studio will automatically detect and register them as queryable data sources.\n"
            )

        let dbPath = Path.Combine(sampleDir, "sample_store.db")
        let db2Path = Path.Combine(sampleDir, "inventory_store.db")
        let csvPath = Path.Combine(sampleDir, "sales_feed.csv")
        let csv2Path = Path.Combine(sampleDir, "customers_feed.csv")
        let excelPath = Path.Combine(sampleDir, "regional_targets.xlsx")
        let excel2Path = Path.Combine(sampleDir, "commerce_ops.xlsx")
        let jsonPath = Path.Combine(sampleDir, "product_catalog.json")
        let xmlPath = Path.Combine(sampleDir, "logistics_hub.xml")
        let notebookPath = Path.Combine(targetDir, "sample_demo.fsqnb")
        let notebookPtPath = Path.Combine(targetDir, "sample_demo_pt.fsqnb")

        let isFileValid (path: string) (minBytes: int64) =
            try
                File.Exists path && (FileInfo path).Length >= minBytes
            with _ ->
                false

        if not (isFileValid dbPath 1000000L) then
            createSampleStoreDatabase dbPath

        if not (isFileValid db2Path 1000000L) then
            createInventoryDatabase db2Path

        if not (isFileValid csvPath 1000000L) then
            createSampleCsv csvPath

        if not (isFileValid csv2Path 1000000L) then
            createSampleCustomersCsv csv2Path

        if not (isFileValid excelPath 100000L) then
            createSampleExcelWorkbook excelPath

        if not (isFileValid excel2Path 100000L) then
            createSampleCommerceExcelWorkbook excel2Path

        if not (isFileValid jsonPath 1000000L) then
            createSampleJsonFile jsonPath

        if not (isFileValid xmlPath 1000000L) then
            createSampleXmlFile xmlPath

        let isPtOnly = language = Some "pt"
        let isEnOnly = language = Some "en"

        if not isPtOnly then
            createSampleDemoNotebook notebookPath

        if not isEnOnly then
            createSampleDemoNotebookPt notebookPtPath

        {
            SqlitePath = dbPath
            Sqlite2Path = db2Path
            CsvPath = csvPath
            Csv2Path = csv2Path
            ExcelPath = excelPath
            Excel2Path = excel2Path
            JsonPath = jsonPath
            XmlPath = xmlPath
            RestUrl = DefaultGitHubRestUrl
            NotebookPath = notebookPath
            NotebookPtPath = notebookPtPath
        }

    let generateAllSamples (targetDir: string) : SampleFilesResult =
        generateAllSamplesWithLanguage targetDir None
