namespace FSharpQueryStudio.Connectors

open System
open System.IO
open FSharpQueryStudio.Protocol
open AdvancedSampleInitScripts
open AdvancedSampleNotebooksPostgres
open AdvancedSampleNotebooksSqlServer
open AdvancedSampleNotebooksEngines

module AdvancedSampleGenerator =

    type AdvancedSampleResult =
        {
            TargetDir: string
            SelectedEngines: string list
            DockerComposePath: string option
            InitScripts: string list
            NotebooksGenerated: string list
            RegisteredConnections: string list
        }

    /// Returns pre-defined schemas for containerized sample databases when offline.
    let tryGetKnownSampleTables (connName: string) : ConnectorTable list option =
        let mkCol (n: string) (dt: string) : ConnectorColumn = { Name = n; DataType = dt }
        let mkTable (n: string) (cols: ConnectorColumn list) : ConnectorTable = { Name = n; Columns = cols }

        let pgCols =
            [
                mkTable
                    "categories"
                    [
                        mkCol "id" "int"
                        mkCol "name" "string"
                        mkCol "description" "string"
                        mkCol "code" "string"
                        mkCol "is_active" "bool"
                    ]
                mkTable
                    "products"
                    [
                        mkCol "id" "int"
                        mkCol "category_id" "int"
                        mkCol "name" "string"
                        mkCol "sku" "string"
                        mkCol "price" "decimal"
                        mkCol "cost_price" "decimal"
                        mkCol "stock_quantity" "int"
                        mkCol "specs" "string"
                    ]
                mkTable
                    "customers"
                    [
                        mkCol "id" "int"
                        mkCol "first_name" "string"
                        mkCol "last_name" "string"
                        mkCol "email" "string"
                        mkCol "city" "string"
                        mkCol "country" "string"
                        mkCol "profile" "string"
                    ]
                mkTable
                    "orders"
                    [
                        mkCol "id" "int"
                        mkCol "customer_id" "int"
                        mkCol "order_date" "System.DateTime"
                        mkCol "status" "string"
                        mkCol "total_amount" "decimal"
                    ]
                mkTable
                    "order_items"
                    [
                        mkCol "id" "int"
                        mkCol "order_id" "int"
                        mkCol "product_id" "int"
                        mkCol "quantity" "int"
                        mkCol "unit_price" "decimal"
                        mkCol "line_total" "decimal"
                    ]
            ]

        let sqlServerCols =
            [
                mkTable
                    "Categories"
                    [
                        mkCol "Id" "int"
                        mkCol "Name" "string"
                        mkCol "Description" "string"
                        mkCol "Code" "string"
                        mkCol "IsActive" "bool"
                    ]
                mkTable
                    "Products"
                    [
                        mkCol "Id" "int"
                        mkCol "CategoryId" "int"
                        mkCol "Name" "string"
                        mkCol "Sku" "string"
                        mkCol "Price" "decimal"
                        mkCol "CostPrice" "decimal"
                        mkCol "StockQuantity" "int"
                        mkCol "Specs" "string"
                    ]
                mkTable
                    "Customers"
                    [
                        mkCol "Id" "int"
                        mkCol "FirstName" "string"
                        mkCol "LastName" "string"
                        mkCol "Email" "string"
                        mkCol "City" "string"
                        mkCol "Country" "string"
                        mkCol "Profile" "string"
                    ]
                mkTable
                    "Orders"
                    [
                        mkCol "Id" "int"
                        mkCol "CustomerId" "int"
                        mkCol "OrderDate" "System.DateTime"
                        mkCol "Status" "string"
                        mkCol "TotalAmount" "decimal"
                    ]
                mkTable
                    "OrderItems"
                    [
                        mkCol "Id" "int"
                        mkCol "OrderId" "int"
                        mkCol "ProductId" "int"
                        mkCol "Quantity" "int"
                        mkCol "UnitPrice" "decimal"
                        mkCol "LineTotal" "decimal"
                    ]
            ]

        let oracleCols =
            [
                mkTable
                    "CATEGORIES"
                    [
                        mkCol "ID" "int"
                        mkCol "NAME" "string"
                        mkCol "DESCRIPTION" "string"
                        mkCol "CODE" "string"
                        mkCol "IS_ACTIVE" "bool"
                    ]
                mkTable
                    "PRODUCTS"
                    [
                        mkCol "ID" "int"
                        mkCol "CATEGORY_ID" "int"
                        mkCol "NAME" "string"
                        mkCol "SKU" "string"
                        mkCol "PRICE" "decimal"
                        mkCol "COST_PRICE" "decimal"
                        mkCol "STOCK_QUANTITY" "int"
                        mkCol "SPECS" "string"
                    ]
                mkTable
                    "CUSTOMERS"
                    [
                        mkCol "ID" "int"
                        mkCol "FIRST_NAME" "string"
                        mkCol "LAST_NAME" "string"
                        mkCol "EMAIL" "string"
                        mkCol "CITY" "string"
                        mkCol "COUNTRY" "string"
                        mkCol "PROFILE" "string"
                    ]
                mkTable
                    "ORDERS"
                    [
                        mkCol "ID" "int"
                        mkCol "CUSTOMER_ID" "int"
                        mkCol "ORDER_DATE" "System.DateTime"
                        mkCol "STATUS" "string"
                        mkCol "TOTAL_AMOUNT" "decimal"
                    ]
                mkTable
                    "ORDER_ITEMS"
                    [
                        mkCol "ID" "int"
                        mkCol "ORDER_ID" "int"
                        mkCol "PRODUCT_ID" "int"
                        mkCol "QUANTITY" "int"
                        mkCol "UNIT_PRICE" "decimal"
                        mkCol "LINE_TOTAL" "decimal"
                    ]
            ]

        match connName with
        | "PostgreSQL_Advanced" -> Some [ for t in pgCols -> mkTable $"fsqs_demo.{t.Name}" t.Columns ]
        | "MySQL_Advanced"
        | "MariaDB_Advanced" -> Some [ for t in pgCols -> mkTable $"fsqs_demo.{t.Name}" t.Columns ]
        | "SqlServer_Advanced" -> Some [ for t in sqlServerCols -> mkTable $"master.dbo.{t.Name}" t.Columns ]
        | "Oracle_Advanced" -> Some [ for t in oracleCols -> mkTable $"FREEPDB1.SYSTEM.{t.Name}" t.Columns ]
        | _ -> None


    let generateAdvancedSamplesWithLanguage
        (targetDir: string)
        (selectedEngines: string list)
        (language: string option)
        : AdvancedSampleResult =
        let normalizedEngines =
            let raw = selectedEngines |> List.map (fun e -> e.ToLowerInvariant().Trim())

            if raw |> List.exists (fun e -> e = "all") then
                [ "postgresql"; "sqlserver"; "mysql"; "mariadb"; "oracle" ]
            else
                raw
                |> List.filter (fun e ->
                    e = "postgresql"
                    || e = "sqlserver"
                    || e = "postgres"
                    || e = "mssql"
                    || e = "mysql"
                    || e = "mariadb"
                    || e = "oracle")
                |> List.distinct

        let hasPostgres =
            normalizedEngines |> List.exists (fun e -> e = "postgresql" || e = "postgres")

        let hasSqlServer =
            normalizedEngines |> List.exists (fun e -> e = "sqlserver" || e = "mssql")

        let hasMySql = normalizedEngines |> List.exists (fun e -> e = "mysql")
        let hasMariaDb = normalizedEngines |> List.exists (fun e -> e = "mariadb")
        let hasOracle = normalizedEngines |> List.exists (fun e -> e = "oracle")

        let hasAnyContainer =
            hasPostgres || hasSqlServer || hasMySql || hasMariaDb || hasOracle

        let mutable initScripts = []
        let mutable dockerComposePath = None
        let mutable notebooksGenerated = []
        let mutable registeredConns = []

        let initScriptsDir = Path.Combine(targetDir, "init-scripts")

        if hasAnyContainer && not (Directory.Exists initScriptsDir) then
            Directory.CreateDirectory initScriptsDir |> ignore

        let isPtOnly = language = Some "pt"
        let isEnOnly = language = Some "en"

        if hasPostgres then
            let pgScriptPath = Path.Combine(initScriptsDir, "init_postgres.sql")
            File.WriteAllText(pgScriptPath, getPostgresInitSql ())
            initScripts <- pgScriptPath :: initScripts

            if not isPtOnly then
                let nbPath = Path.Combine(targetDir, "sample_postgresql.fsqnb")
                File.WriteAllText(nbPath, getPostgresNotebook ())
                notebooksGenerated <- nbPath :: notebooksGenerated

            if not isEnOnly then
                let nbPtPath = Path.Combine(targetDir, "sample_postgresql_pt.fsqnb")
                File.WriteAllText(nbPtPath, getPostgresNotebookPt ())
                notebooksGenerated <- nbPtPath :: notebooksGenerated

            registeredConns <- "PostgreSQL_Advanced" :: registeredConns

        if hasSqlServer then
            let sqlServerScriptPath = Path.Combine(initScriptsDir, "init_sqlserver.sql")
            File.WriteAllText(sqlServerScriptPath, getSqlServerInitSql ())
            initScripts <- sqlServerScriptPath :: initScripts

            if not isPtOnly then
                let nbPath = Path.Combine(targetDir, "sample_sqlserver.fsqnb")
                File.WriteAllText(nbPath, getSqlServerNotebook ())
                notebooksGenerated <- nbPath :: notebooksGenerated

            if not isEnOnly then
                let nbPtPath = Path.Combine(targetDir, "sample_sqlserver_pt.fsqnb")
                File.WriteAllText(nbPtPath, getSqlServerNotebookPt ())
                notebooksGenerated <- nbPtPath :: notebooksGenerated

            registeredConns <- "SqlServer_Advanced" :: registeredConns

        if hasMySql then
            let myScriptPath = Path.Combine(initScriptsDir, "init_mysql.sql")
            File.WriteAllText(myScriptPath, getMySqlInitSql ())
            initScripts <- myScriptPath :: initScripts

            if not isPtOnly then
                let nbPath = Path.Combine(targetDir, "sample_mysql.fsqnb")
                File.WriteAllText(nbPath, getMySqlNotebook ())
                notebooksGenerated <- nbPath :: notebooksGenerated

            if not isEnOnly then
                let nbPtPath = Path.Combine(targetDir, "sample_mysql_pt.fsqnb")
                File.WriteAllText(nbPtPath, getMySqlNotebookPt ())
                notebooksGenerated <- nbPtPath :: notebooksGenerated

            registeredConns <- "MySQL_Advanced" :: registeredConns

        if hasMariaDb then
            let mariaScriptPath = Path.Combine(initScriptsDir, "init_mariadb.sql")
            File.WriteAllText(mariaScriptPath, getMariaDbInitSql ())
            initScripts <- mariaScriptPath :: initScripts

            if not isPtOnly then
                let nbPath = Path.Combine(targetDir, "sample_mariadb.fsqnb")
                File.WriteAllText(nbPath, getMariaDbNotebook ())
                notebooksGenerated <- nbPath :: notebooksGenerated

            if not isEnOnly then
                let nbPtPath = Path.Combine(targetDir, "sample_mariadb_pt.fsqnb")
                File.WriteAllText(nbPtPath, getMariaDbNotebookPt ())
                notebooksGenerated <- nbPtPath :: notebooksGenerated

            registeredConns <- "MariaDB_Advanced" :: registeredConns

        if hasOracle then
            let oraScriptPath = Path.Combine(initScriptsDir, "init_oracle.sql")
            File.WriteAllText(oraScriptPath, getOracleInitSql ())
            initScripts <- oraScriptPath :: initScripts

            if not isPtOnly then
                let nbPath = Path.Combine(targetDir, "sample_oracle.fsqnb")
                File.WriteAllText(nbPath, getOracleNotebook ())
                notebooksGenerated <- nbPath :: notebooksGenerated

            if not isEnOnly then
                let nbPtPath = Path.Combine(targetDir, "sample_oracle_pt.fsqnb")
                File.WriteAllText(nbPtPath, getOracleNotebookPt ())
                notebooksGenerated <- nbPtPath :: notebooksGenerated

            registeredConns <- "Oracle_Advanced" :: registeredConns

        if hasAnyContainer then
            let composePath = Path.Combine(targetDir, "docker-compose.fsqs.yml")

            File.WriteAllText(
                composePath,
                getDockerComposeYamlWithEngines hasPostgres hasSqlServer hasMySql hasMariaDb hasOracle
            )

            dockerComposePath <- Some composePath

        {
            TargetDir = targetDir
            SelectedEngines = normalizedEngines
            DockerComposePath = dockerComposePath
            InitScripts = List.rev initScripts
            NotebooksGenerated = List.rev notebooksGenerated
            RegisteredConnections = List.rev registeredConns
        }

    let generateAdvancedSamples (targetDir: string) (selectedEngines: string list) : AdvancedSampleResult =
        generateAdvancedSamplesWithLanguage targetDir selectedEngines None
