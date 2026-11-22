namespace FSharpQueryStudio.Connectors.Tests

open System
open System.IO
open Xunit
open FSharpQueryStudio.Connectors
open FSharpQueryStudio.Protocol

type ConnectorDialectDecouplingTests() =

    [<Fact>]
    member _.``ConnectorClassifier classifies SQL connectors as Sql``() =
        let sqliteConn = SqliteConnector(":memory:") :> IConnector
        let classified = ConnectorClassifier.classify sqliteConn

        match classified with
        | ConnectorInstance.Sql qc -> Assert.Equal(ConnectorKind.Sqlite, qc.Kind)
        | _ -> Assert.Fail("Expected ConnectorInstance.Sql")

    [<Fact>]
    member _.``ConnectorClassifier classifies SqlServer connector as Sql``() =
        let sqlServerConn =
            SqlServerConnector("Server=127.0.0.1;Database=Test;") :> IConnector

        let classified = ConnectorClassifier.classify sqlServerConn

        match classified with
        | ConnectorInstance.Sql qc -> Assert.Equal(ConnectorKind.SqlServer, qc.Kind)
        | _ -> Assert.Fail("Expected ConnectorInstance.Sql")

    [<Fact>]
    member _.``ConnectorClassifier classifies CSV connector as Stagable``() =
        let csvConn = CsvConnector("dummy.csv") :> IConnector
        let classified = ConnectorClassifier.classify csvConn

        match classified with
        | ConnectorInstance.Stagable sc -> Assert.Equal("dummy.csv", sc.FilePath)
        | _ -> Assert.Fail("Expected ConnectorInstance.Stagable")

    [<Fact>]
    member _.``ConnectorClassifier classifies Excel connector as Stagable``() =
        let excelConn = ExcelConnector("dummy.xlsx") :> IConnector
        let classified = ConnectorClassifier.classify excelConn

        match classified with
        | ConnectorInstance.Stagable sc -> Assert.Equal("dummy.xlsx", sc.FilePath)
        | _ -> Assert.Fail("Expected ConnectorInstance.Stagable")

    [<Fact>]
    member _.``ConnectorClassifier classifies JSON connector as Stagable``() =
        let jsonConn = JsonConnector("dummy.json") :> IConnector
        let classified = ConnectorClassifier.classify jsonConn

        match classified with
        | ConnectorInstance.Stagable sc -> Assert.Equal("dummy.json", sc.FilePath)
        | _ -> Assert.Fail("Expected ConnectorInstance.Stagable")

    [<Fact>]
    member _.``ConnectorClassifier classifies XML connector as Stagable``() =
        let xmlConn = XmlConnector("dummy.xml") :> IConnector
        let classified = ConnectorClassifier.classify xmlConn

        match classified with
        | ConnectorInstance.Stagable sc -> Assert.Equal("dummy.xml", sc.FilePath)
        | _ -> Assert.Fail("Expected ConnectorInstance.Stagable")

    [<Fact>]
    member _.``ConnectorClassifier classifies REST connector as Other``() =
        let restConn =
            RestConnector(RestRequestOptions.Get("https://api.example.com")) :> IConnector

        let classified = ConnectorClassifier.classify restConn

        match classified with
        | ConnectorInstance.Other c -> Assert.Equal(ConnectorKind("RestJson"), c.Kind)
        | _ -> Assert.Fail("Expected ConnectorInstance.Other")

    [<Fact>]
    member _.``ConnectorClassifier classifies custom dummy connector as Other``() =
        let dummy =
            { new IConnector with
                member _.Kind = ConnectorKind("Custom")

                member _.TestConnectionAsync _ =
                    System.Threading.Tasks.Task.FromResult true

                member _.DiscoverSchemaAsync _ =
                    System.Threading.Tasks.Task.FromResult []
            }

        let classified = ConnectorClassifier.classify dummy

        match classified with
        | ConnectorInstance.Other c -> Assert.Equal(ConnectorKind("Custom"), c.Kind)
        | _ -> Assert.Fail("Expected ConnectorInstance.Other")

    // --- Dialect QuoteIdentifier Tests (>= 2 per dialect) ---

    [<Fact>]
    member _.``SqliteDialect QuoteIdentifier quotes keywords and spaces correctly``() =
        let dialect = SqliteDialect.Instance
        Assert.Equal("\"SELECT\"", dialect.QuoteIdentifier("SELECT"))
        Assert.Equal("\"User Name\"", dialect.QuoteIdentifier("User Name"))
        Assert.Equal("NormalCol", dialect.QuoteIdentifier("NormalCol"))

    [<Fact>]
    member _.``SqliteDialect QuoteIdentifier preserves already quoted identifiers``() =
        let dialect = SqliteDialect.Instance
        Assert.Equal("\"already\"", dialect.QuoteIdentifier("\"already\""))
        Assert.Equal("[bracket]", dialect.QuoteIdentifier("[bracket]"))
        Assert.Equal("`backtick`", dialect.QuoteIdentifier("`backtick`"))

    [<Fact>]
    member _.``SqlServerDialect QuoteIdentifier quotes with square brackets``() =
        let dialect = SqlServerDialect.Instance
        Assert.Equal("[ORDER]", dialect.QuoteIdentifier("ORDER"))
        Assert.Equal("[Order Details]", dialect.QuoteIdentifier("Order Details"))
        Assert.Equal("OrderId", dialect.QuoteIdentifier("OrderId"))

    [<Fact>]
    member _.``SqlServerDialect QuoteIdentifier preserves brackets and backticks``() =
        let dialect = SqlServerDialect.Instance
        Assert.Equal("[already]", dialect.QuoteIdentifier("[already]"))
        Assert.Equal("\"quoted\"", dialect.QuoteIdentifier("\"quoted\""))

    [<Fact>]
    member _.``PostgreSqlDialect QuoteIdentifier quotes with double quotes``() =
        let dialect = PostgreSqlDialect.Instance
        Assert.Equal("\"GROUP\"", dialect.QuoteIdentifier("GROUP"))
        Assert.Equal("\"First Name\"", dialect.QuoteIdentifier("First Name"))
        Assert.Equal("user_id", dialect.QuoteIdentifier("user_id"))

    [<Fact>]
    member _.``PostgreSqlDialect QuoteIdentifier preserves double quotes and brackets``() =
        let dialect = PostgreSqlDialect.Instance
        Assert.Equal("\"quoted\"", dialect.QuoteIdentifier("\"quoted\""))
        Assert.Equal("[bracket]", dialect.QuoteIdentifier("[bracket]"))

    [<Fact>]
    member _.``MySqlDialect QuoteIdentifier quotes with backticks``() =
        let dialect = MySqlDialect.Instance
        Assert.Equal("`WHERE`", dialect.QuoteIdentifier("WHERE"))
        Assert.Equal("`Full Name`", dialect.QuoteIdentifier("Full Name"))
        Assert.Equal("created_at", dialect.QuoteIdentifier("created_at"))

    [<Fact>]
    member _.``MariaDbDialect QuoteIdentifier quotes with backticks``() =
        let dialect = MariaDbDialect.Instance
        Assert.Equal("`TABLE`", dialect.QuoteIdentifier("TABLE"))
        Assert.Equal("`Last Name`", dialect.QuoteIdentifier("Last Name"))
        Assert.Equal("is_active", dialect.QuoteIdentifier("is_active"))

    [<Fact>]
    member _.``OracleDialect QuoteIdentifier quotes with double quotes``() =
        let dialect = OracleDialect.Instance
        Assert.Equal("\"FROM\"", dialect.QuoteIdentifier("FROM"))
        Assert.Equal("\"Hire Date\"", dialect.QuoteIdentifier("Hire Date"))
        Assert.Equal("SALARY", dialect.QuoteIdentifier("SALARY"))

    [<Fact>]
    member _.``OracleDialect QuoteIdentifier preserves quoted strings``() =
        let dialect = OracleDialect.Instance
        Assert.Equal("\"EXISTING\"", dialect.QuoteIdentifier("\"EXISTING\""))
        Assert.Equal("SIMPLE_IDENT", dialect.QuoteIdentifier("SIMPLE_IDENT"))

    // --- Dialect FormatLimitOffset Tests (>= 2 per dialect) ---

    [<Fact>]
    member _.``SqliteDialect FormatLimitOffset produces LIMIT and OFFSET``() =
        let dialect = SqliteDialect.Instance
        Assert.Equal(" LIMIT 10 OFFSET 5", dialect.FormatLimitOffset(Some 10, Some 5, false))
        Assert.Equal(" LIMIT 25", dialect.FormatLimitOffset(Some 25, None, false))
        Assert.Equal(" LIMIT -1 OFFSET 10", dialect.FormatLimitOffset(None, Some 10, false))
        Assert.Equal("", dialect.FormatLimitOffset(None, None, false))

    [<Fact>]
    member _.``SqlServerDialect FormatLimitOffset produces OFFSET and FETCH with order fallback``() =
        let dialect = SqlServerDialect.Instance
        Assert.Equal(" OFFSET 5 ROWS FETCH NEXT 10 ROWS ONLY", dialect.FormatLimitOffset(Some 10, Some 5, true))

        Assert.Equal(
            " ORDER BY (SELECT 1) OFFSET 5 ROWS FETCH NEXT 10 ROWS ONLY",
            dialect.FormatLimitOffset(Some 10, Some 5, false)
        )

        Assert.Equal(" OFFSET 0 ROWS FETCH NEXT 20 ROWS ONLY", dialect.FormatLimitOffset(Some 20, None, true))
        Assert.Equal(" OFFSET 15 ROWS", dialect.FormatLimitOffset(None, Some 15, true))

    [<Fact>]
    member _.``PostgreSqlDialect FormatLimitOffset produces LIMIT and OFFSET clauses``() =
        let dialect = PostgreSqlDialect.Instance
        Assert.Equal(" LIMIT 10 OFFSET 5", dialect.FormatLimitOffset(Some 10, Some 5, true))
        Assert.Equal(" LIMIT 50", dialect.FormatLimitOffset(Some 50, None, false))
        Assert.Equal(" OFFSET 100", dialect.FormatLimitOffset(None, Some 100, true))

    [<Fact>]
    member _.``MySql and MariaDb Dialect FormatLimitOffset handles large integer offset fallback``() =
        let mySql = MySqlDialect.Instance
        let maria = MariaDbDialect.Instance
        Assert.Equal(" LIMIT 10 OFFSET 5", mySql.FormatLimitOffset(Some 10, Some 5, true))
        Assert.Equal(" LIMIT 18446744073709551615 OFFSET 20", mySql.FormatLimitOffset(None, Some 20, true))
        Assert.Equal(" LIMIT 10 OFFSET 5", maria.FormatLimitOffset(Some 10, Some 5, true))
        Assert.Equal(" LIMIT 18446744073709551615 OFFSET 20", maria.FormatLimitOffset(None, Some 20, true))

    [<Fact>]
    member _.``OracleDialect FormatLimitOffset produces OFFSET and FETCH clauses``() =
        let dialect = OracleDialect.Instance
        Assert.Equal(" OFFSET 5 ROWS FETCH NEXT 10 ROWS ONLY", dialect.FormatLimitOffset(Some 10, Some 5, true))
        Assert.Equal(" FETCH NEXT 25 ROWS ONLY", dialect.FormatLimitOffset(Some 25, None, true))
        Assert.Equal(" OFFSET 50 ROWS", dialect.FormatLimitOffset(None, Some 50, true))

    // --- Dialect FormatApplyJoin Tests (>= 2 per dialect) ---

    [<Fact>]
    member _.``SqlDialects FormatApplyJoin generates correct cross and outer apply syntax``() =
        let sqlServer = SqlServerDialect.Instance
        Assert.Equal("CROSS APPLY Users", sqlServer.FormatApplyJoin(false, "Users"))
        Assert.Equal("OUTER APPLY Orders", sqlServer.FormatApplyJoin(true, "Orders"))

        let oracle = OracleDialect.Instance
        Assert.Equal("CROSS APPLY Users", oracle.FormatApplyJoin(false, "Users"))
        Assert.Equal("OUTER APPLY Orders", oracle.FormatApplyJoin(true, "Orders"))

        let postgres = PostgreSqlDialect.Instance
        Assert.Equal("CROSS JOIN LATERAL Users", postgres.FormatApplyJoin(false, "Users"))
        Assert.Equal("LEFT JOIN LATERAL Orders ON true", postgres.FormatApplyJoin(true, "Orders"))

        let mysql = MySqlDialect.Instance
        Assert.Equal("CROSS JOIN LATERAL Users", mysql.FormatApplyJoin(false, "Users"))
        Assert.Equal("LEFT JOIN LATERAL Orders ON true", mysql.FormatApplyJoin(true, "Orders"))

        let sqlite = SqliteDialect.Instance
        Assert.Equal("CROSS JOIN Users", sqlite.FormatApplyJoin(false, "Users"))

    // --- Dialect SupportsRecursiveCteKeyword and DualTable Tests ---

    [<Fact>]
    member _.``SqlDialects SupportsRecursiveCteKeyword returns true for postgres, sqlite, mysql, mariadb``() =
        Assert.True(SqliteDialect.Instance.SupportsRecursiveCteKeyword)
        Assert.True(PostgreSqlDialect.Instance.SupportsRecursiveCteKeyword)
        Assert.True(MySqlDialect.Instance.SupportsRecursiveCteKeyword)
        Assert.True(MariaDbDialect.Instance.SupportsRecursiveCteKeyword)
        Assert.False(SqlServerDialect.Instance.SupportsRecursiveCteKeyword)
        Assert.False(OracleDialect.Instance.SupportsRecursiveCteKeyword)

    [<Fact>]
    member _.``SqlDialects DualTable returns Some DUAL for Oracle and None for others``() =
        Assert.Equal(Some "DUAL", OracleDialect.Instance.DualTable)
        Assert.Equal(None, SqliteDialect.Instance.DualTable)
        Assert.Equal(None, SqlServerDialect.Instance.DualTable)
        Assert.Equal(None, PostgreSqlDialect.Instance.DualTable)
        Assert.Equal(None, MySqlDialect.Instance.DualTable)
        Assert.Equal(None, MariaDbDialect.Instance.DualTable)

    // --- Dialect Date & JSON Translation Tests (>= 2 per dialect) ---

    [<Fact>]
    member _.``SqlDialects TranslateDatePart and TranslateDateTrunc translate properly``() =
        let sqlite = SqliteDialect.Instance
        Assert.Equal(Some "CAST(strftime('%Y', col) AS INTEGER)", sqlite.TranslateDatePart("YEAR", "col"))
        Assert.Equal(Some "date(col)", sqlite.TranslateDateTrunc("col"))

        let sqlServer = SqlServerDialect.Instance
        Assert.Equal(Some "YEAR(col)", sqlServer.TranslateDatePart("YEAR", "col"))
        Assert.Equal(Some "CAST(col AS DATE)", sqlServer.TranslateDateTrunc("col"))

        let postgres = PostgreSqlDialect.Instance
        Assert.Equal(Some "EXTRACT(YEAR FROM col)", postgres.TranslateDatePart("YEAR", "col"))
        Assert.Equal(Some "CAST(col AS DATE)", postgres.TranslateDateTrunc("col"))

        let oracle = OracleDialect.Instance
        Assert.Equal(Some "EXTRACT(YEAR FROM col)", oracle.TranslateDatePart("YEAR", "col"))
        Assert.Equal(Some "TRUNC(col)", oracle.TranslateDateTrunc("col"))

    [<Fact>]
    member _.``SqlDialects TranslateJsonExtract formats correct json extraction functions``() =
        Assert.Equal(Some "json_extract(data, '$.name')", SqliteDialect.Instance.TranslateJsonExtract("data", "$.name"))

        Assert.Equal(
            Some "JSON_VALUE(data, '$.name')",
            SqlServerDialect.Instance.TranslateJsonExtract("data", "$.name")
        )

        Assert.Equal(Some "(data ->> 'name')", PostgreSqlDialect.Instance.TranslateJsonExtract("data", "$.name"))

        Assert.Equal(
            Some "JSON_UNQUOTE(JSON_EXTRACT(data, '$.name'))",
            MySqlDialect.Instance.TranslateJsonExtract("data", "$.name")
        )

        Assert.Equal(Some "JSON_VALUE(data, '$.name')", OracleDialect.Instance.TranslateJsonExtract("data", "$.name"))

    // --- Dialect SqlProviderType and SchemaTarget Tests ---

    [<Fact>]
    member _.``SqlDialects SqlProviderType returns official provider strings``() =
        Assert.Equal("Common.DatabaseProviderTypes.SQLITE", SqliteDialect.Instance.SqlProviderType)
        Assert.Equal("Common.DatabaseProviderTypes.MSSQLSERVER", SqlServerDialect.Instance.SqlProviderType)
        Assert.Equal("Common.DatabaseProviderTypes.POSTGRESQL", PostgreSqlDialect.Instance.SqlProviderType)
        Assert.Equal("Common.DatabaseProviderTypes.POSTGRESQL", MySqlDialect.Instance.SqlProviderType)
        Assert.Equal("Common.DatabaseProviderTypes.POSTGRESQL", OracleDialect.Instance.SqlProviderType)

    [<Fact>]
    member _.``SqlServerDialect FormatSqlTarget targets 3-part schema correctly``() =
        let dialect = SqlServerDialect.Instance

        Assert.Equal(
            "Northwind.dbo.Customers",
            dialect.FormatSqlTarget("Northwind", None, "Customers", "Northwind.Customers", true)
        )

        Assert.Equal("Customers", dialect.FormatSqlTarget("Northwind", None, "Customers", "Customers", true))

        Assert.Equal(
            "Northwind.sales.Orders",
            dialect.FormatSqlTarget("Northwind", Some "sales", "Orders", "Orders", false)
        )

    [<Fact>]
    member _.``OracleDialect FormatSqlTarget flattens default SYSTEM schema``() =
        let dialect = OracleDialect.Instance
        Assert.Equal("CUSTOMERS", dialect.FormatSqlTarget("FREEPDB1", Some "SYSTEM", "CUSTOMERS", "CUSTOMERS", true))
        Assert.Equal("HR.EMPLOYEES", dialect.FormatSqlTarget("FREEPDB1", Some "HR", "EMPLOYEES", "EMPLOYEES", false))

    // --- IStagableConnector Staging Tests (>= 2 per connector) ---

    [<Fact>]
    member _.``CsvConnector implements IStagableConnector and stages into FileSqliteEngine``() =
        let tempDb = Path.Combine(Path.GetTempPath(), $"stagable_csv_{Guid.NewGuid():N}.db")

        let tempCsv =
            Path.Combine(Path.GetTempPath(), $"stagable_csv_{Guid.NewGuid():N}.csv")

        File.WriteAllText(tempCsv, "Id,Name\n1,Alice\n2,Bob\n")

        try
            let engine = FileSqliteEngine(tempDb)
            let connector = CsvConnector(tempCsv)
            let stagable = connector :> IStagableConnector

            let tableName = stagable.StagePrimaryTable(engine, "Users")
            Assert.False(String.IsNullOrWhiteSpace(tableName))
            Assert.True(engine.IsStagedAndCurrent("Users", tempCsv))

            let res = stagable.StageConnection(engine, "Users", 1, 1, None)
            Assert.True(engine.IsStagedAndCurrent("Users", tempCsv))
            Assert.True(res.StagedCount >= 0)
        finally
            if File.Exists tempCsv then
                File.Delete tempCsv

            if File.Exists tempDb then
                File.Delete tempDb

    [<Fact>]
    member _.``JsonConnector implements IStagableConnector and stages into FileSqliteEngine``() =
        let tempDb =
            Path.Combine(Path.GetTempPath(), $"stagable_json_{Guid.NewGuid():N}.db")

        let tempJson =
            Path.Combine(Path.GetTempPath(), $"stagable_json_{Guid.NewGuid():N}.json")

        File.WriteAllText(tempJson, "[{\"Id\":1,\"Title\":\"Hello\"},{\"Id\":2,\"Title\":\"World\"}]")

        try
            let engine = FileSqliteEngine(tempDb)
            let connector = JsonConnector(tempJson)
            let stagable = connector :> IStagableConnector

            let tableName = stagable.StagePrimaryTable(engine, "Posts")
            Assert.False(String.IsNullOrWhiteSpace(tableName))
            Assert.True(engine.IsStagedAndCurrent("Posts_Items", tempJson))

            let res = stagable.StageConnection(engine, "Posts", 1, 1, None)
            Assert.True(engine.IsStagedAndCurrent("Posts_Items", tempJson))
            Assert.True(res.StagedCount >= 0)
        finally
            if File.Exists tempJson then
                File.Delete tempJson

            if File.Exists tempDb then
                File.Delete tempDb

    [<Fact>]
    member _.``XmlConnector implements IStagableConnector and stages into FileSqliteEngine``() =
        let tempDb = Path.Combine(Path.GetTempPath(), $"stagable_xml_{Guid.NewGuid():N}.db")

        let tempXml =
            Path.Combine(Path.GetTempPath(), $"stagable_xml_{Guid.NewGuid():N}.xml")

        File.WriteAllText(
            tempXml,
            "<Items><Item><Id>1</Id><Val>A</Val></Item><Item><Id>2</Id><Val>B</Val></Item></Items>"
        )

        try
            let engine = FileSqliteEngine(tempDb)
            let connector = XmlConnector(tempXml)
            let stagable = connector :> IStagableConnector

            let tableName = stagable.StagePrimaryTable(engine, "Items")
            Assert.False(String.IsNullOrWhiteSpace(tableName))

            let res = stagable.StageConnection(engine, "Items", 1, 1, None)
            Assert.True(engine.IsStagedAndCurrent("Items_Item", tempXml))
            Assert.True(res.StagedCount >= 0)
        finally
            if File.Exists tempXml then
                File.Delete tempXml

            if File.Exists tempDb then
                File.Delete tempDb

    // --- FormatSelect Dialect Tests (>= 2 per dialect) ---

    [<Fact>]
    member _.``SqliteDialect FormatSelect generates valid SELECT with LIMIT and OFFSET``() =
        let dialect = SqliteDialect.Instance

        let sql1 =
            dialect.FormatSelect(
                false,
                "Id, Name",
                "Users",
                " WHERE Active = 1",
                "",
                "",
                " ORDER BY Id ASC",
                Some 10,
                Some 20
            )

        Assert.Equal("SELECT Id, Name FROM Users WHERE Active = 1 ORDER BY Id ASC LIMIT 10 OFFSET 20", sql1)

        let sql2 = dialect.FormatSelect(true, "Id", "Users", "", "", "", "", Some 5, None)
        Assert.Equal("SELECT DISTINCT Id FROM Users LIMIT 5", sql2)

        let sql3 = dialect.FormatSelect(false, "*", "Users", "", "", "", "", None, None)
        Assert.Equal("SELECT * FROM Users", sql3)

    [<Fact>]
    member _.``SqlServerDialect FormatSelect handles TOP, OFFSET and FETCH NEXT correctly``() =
        let dialect = SqlServerDialect.Instance
        // Take only without order -> TOP (5)
        let sqlTop =
            dialect.FormatSelect(false, "Id, Name", "Users", " WHERE Active = 1", "", "", "", Some 5, None)

        Assert.Equal("SELECT TOP (5) Id, Name FROM Users WHERE Active = 1", sqlTop)

        // Take only with order -> OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY
        let sqlTakeOrd =
            dialect.FormatSelect(false, "Id, Name", "Users", "", "", "", " ORDER BY Id", Some 5, None)

        Assert.Equal("SELECT Id, Name FROM Users ORDER BY Id OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY", sqlTakeOrd)

        // Take and Skip -> OFFSET and FETCH NEXT with fallback order
        let sqlPage =
            dialect.FormatSelect(true, "Id", "Users", "", "", "", "", Some 10, Some 20)

        Assert.Equal(
            "SELECT DISTINCT Id FROM Users ORDER BY (SELECT NULL) OFFSET 20 ROWS FETCH NEXT 10 ROWS ONLY",
            sqlPage
        )

        // Skip only -> OFFSET without FETCH NEXT
        let sqlSkip =
            dialect.FormatSelect(false, "*", "Users", "", "", "", " ORDER BY Id", None, Some 15)

        Assert.Equal("SELECT * FROM Users ORDER BY Id OFFSET 15 ROWS", sqlSkip)

    [<Fact>]
    member _.``OracleDialect FormatSelect handles OFFSET and FETCH NEXT correctly``() =
        let dialect = OracleDialect.Instance

        let sql1 =
            dialect.FormatSelect(false, "Id, Name", "Users", "", "", "", " ORDER BY Id ASC", Some 10, Some 20)

        Assert.Equal("SELECT Id, Name FROM Users ORDER BY Id ASC OFFSET 20 ROWS FETCH NEXT 10 ROWS ONLY", sql1)

        let sql2 = dialect.FormatSelect(true, "Id", "Users", "", "", "", "", Some 5, None)
        Assert.Equal("SELECT DISTINCT Id FROM Users OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY", sql2)

        let sql3 = dialect.FormatSelect(false, "*", "Users", "", "", "", "", None, Some 10)
        Assert.Equal("SELECT * FROM Users OFFSET 10 ROWS", sql3)

    [<Fact>]
    member _.``PostgreSqlDialect FormatSelect handles LIMIT and OFFSET correctly``() =
        let dialect = PostgreSqlDialect.Instance

        let sql1 =
            dialect.FormatSelect(false, "Id, Name", "Users", "", "", "", " ORDER BY Id", Some 10, Some 5)

        Assert.Equal("SELECT Id, Name FROM Users ORDER BY Id LIMIT 10 OFFSET 5", sql1)

        let sql2 =
            dialect.FormatSelect(true, "Email", "Users", " WHERE Active = 1", "", "", "", None, Some 10)

        Assert.Equal("SELECT DISTINCT Email FROM Users WHERE Active = 1 OFFSET 10", sql2)

    [<Fact>]
    member _.``MySqlDialect and MariaDbDialect FormatSelect handle LIMIT and OFFSET correctly``() =
        let mysql = MySqlDialect.Instance
        let maria = MariaDbDialect.Instance

        let sqlMy = mysql.FormatSelect(false, "Id", "Users", "", "", "", "", Some 10, None)
        Assert.Equal("SELECT Id FROM Users LIMIT 10", sqlMy)

        let sqlMaria =
            maria.FormatSelect(true, "Id", "Users", "", "", "", " ORDER BY Id", Some 10, Some 20)

        Assert.Equal("SELECT DISTINCT Id FROM Users ORDER BY Id LIMIT 10 OFFSET 20", sqlMaria)

    // --- FormatOuterApplyJoin Tests ---

    [<Fact>]
    member _.``SqliteDialect FormatOuterApplyJoin emulates outer apply with LEFT JOIN``() =
        let dialect = SqliteDialect.Instance
        let resWithOn = dialect.FormatOuterApplyJoin("Orders o", Some "u.Id = o.UserId")
        Assert.Equal("LEFT JOIN Orders o ON u.Id = o.UserId", resWithOn)

        let resWithoutOn = dialect.FormatOuterApplyJoin("Orders o", None)
        Assert.Equal("LEFT JOIN Orders o ON 1=1", resWithoutOn)

    [<Fact>]
    member _.``SqlServer, Postgres, Oracle FormatOuterApplyJoin generates expected outer apply or lateral join``() =
        let sqlServer = SqlServerDialect.Instance
        let pg = PostgreSqlDialect.Instance
        let ora = OracleDialect.Instance

        Assert.Equal("OUTER APPLY Orders o", sqlServer.FormatOuterApplyJoin("Orders o", None))
        Assert.Equal("LEFT JOIN LATERAL Orders o ON true", pg.FormatOuterApplyJoin("Orders o", None))

        Assert.Equal(
            "LEFT JOIN LATERAL Orders o ON o.Id = u.Id",
            pg.FormatOuterApplyJoin("Orders o", Some "o.Id = u.Id")
        )

        Assert.Equal("OUTER APPLY Orders o", ora.FormatOuterApplyJoin("Orders o", None))

    // --- PostProcessSql Tests ---

    [<Fact>]
    member _.``OracleDialect PostProcessSql strips invalid leading underscores from unquoted identifiers``() =
        let dialect = OracleDialect.Instance

        let sql =
            "SELECT _t0.Id, _t0._Name, t1._Value FROM Users _t0 JOIN Orders t1 ON _t0.Id = t1.UserId"

        let processed = dialect.PostProcessSql(sql)
        Assert.Equal("SELECT t0.Id, t0.Name, t1.Value FROM Users t0 JOIN Orders t1 ON t0.Id = t1.UserId", processed)

    [<Fact>]
    member _.``Sqlite and SqlServer PostProcessSql keep SQL intact``() =
        let sql = "SELECT _t0.Id FROM Users _t0"
        Assert.Equal(sql, SqliteDialect.Instance.PostProcessSql(sql))
        Assert.Equal(sql, SqlServerDialect.Instance.PostProcessSql(sql))

    // --- Scalar Translation Tests (>= 2 per scenario) ---

    [<Fact>]
    member _.``Dialects FormatBooleanLiteral formats correctly for all engines``() =
        Assert.Equal("1", SqliteDialect.Instance.FormatBooleanLiteral(true))
        Assert.Equal("0", SqliteDialect.Instance.FormatBooleanLiteral(false))
        Assert.Equal("1", SqlServerDialect.Instance.FormatBooleanLiteral(true))
        Assert.Equal("0", SqlServerDialect.Instance.FormatBooleanLiteral(false))
        Assert.Equal("TRUE", PostgreSqlDialect.Instance.FormatBooleanLiteral(true))
        Assert.Equal("FALSE", PostgreSqlDialect.Instance.FormatBooleanLiteral(false))
        Assert.Equal("1", MySqlDialect.Instance.FormatBooleanLiteral(true))
        Assert.Equal("0", MariaDbDialect.Instance.FormatBooleanLiteral(false))
        Assert.Equal("1", OracleDialect.Instance.FormatBooleanLiteral(true))

    [<Fact>]
    member _.``Dialects FormatIsEmpty and FormatIsNotEmpty translate correctly``() =
        // Oracle uses LENGTH
        Assert.Equal("(name IS NULL OR LENGTH(name) = 0)", OracleDialect.Instance.FormatIsEmpty("name"))
        Assert.Equal("(name IS NOT NULL AND LENGTH(name) > 0)", OracleDialect.Instance.FormatIsNotEmpty("name"))

        // Others use empty string check
        Assert.Equal("(name IS NULL OR name = '')", SqliteDialect.Instance.FormatIsEmpty("name"))
        Assert.Equal("(name IS NOT NULL AND name <> '')", SqliteDialect.Instance.FormatIsNotEmpty("name"))
        Assert.Equal("(name IS NULL OR name = '')", SqlServerDialect.Instance.FormatIsEmpty("name"))
        Assert.Equal("(name IS NOT NULL AND name <> '')", PostgreSqlDialect.Instance.FormatIsNotEmpty("name"))

    [<Fact>]
    member _.``Dialects TranslateStringLength and FormatConcat translate correctly``() =
        // String Length: LEN in SqlServer, LENGTH in others
        Assert.Equal("LEN(col)", SqlServerDialect.Instance.TranslateStringLength("col"))
        Assert.Equal("LENGTH(col)", SqliteDialect.Instance.TranslateStringLength("col"))
        Assert.Equal("LENGTH(col)", PostgreSqlDialect.Instance.TranslateStringLength("col"))

        // Concat: + in SqlServer, CONCAT in MySql/MariaDb, || in others
        Assert.Equal("(a + b + c)", SqlServerDialect.Instance.FormatConcat([ "a"; "b"; "c" ]))
        Assert.Equal("CONCAT(a, b, c)", MySqlDialect.Instance.FormatConcat([ "a"; "b"; "c" ]))
        Assert.Equal("CONCAT(a, b)", MariaDbDialect.Instance.FormatConcat([ "a"; "b" ]))
        Assert.Equal("(a || b || c)", SqliteDialect.Instance.FormatConcat([ "a"; "b"; "c" ]))
        Assert.Equal("(a || b)", PostgreSqlDialect.Instance.FormatConcat([ "a"; "b" ]))

    [<Fact>]
    member _.``Dialects TranslateDateFormat translates patterns correctly per engine``() =
        let dt = "dt"
        let fmt = "yyyy-MM-dd"

        Assert.Equal(Some "strftime('%Y-%m-%d', dt)", SqliteDialect.Instance.TranslateDateFormat(dt, fmt))
        Assert.Equal(Some "FORMAT(dt, 'yyyy-MM-dd')", SqlServerDialect.Instance.TranslateDateFormat(dt, fmt))
        Assert.Equal(Some "to_char(dt, 'YYYY-MM-DD')", PostgreSqlDialect.Instance.TranslateDateFormat(dt, fmt))
        Assert.Equal(Some "DATE_FORMAT(dt, '%Y-%m-%d')", MySqlDialect.Instance.TranslateDateFormat(dt, fmt))
        Assert.Equal(Some "DATE_FORMAT(dt, '%Y-%m-%d')", MariaDbDialect.Instance.TranslateDateFormat(dt, fmt))
        Assert.Equal(Some "TO_CHAR(dt, 'YYYY-MM-DD')", OracleDialect.Instance.TranslateDateFormat(dt, fmt))

    [<Fact>]
    member _.``Dialects TranslateCastToString, TranslateSubstring, TranslateCeiling, TranslateIndexOf translate correctly``
        ()
        =
        // CastToString
        Assert.Equal("CAST(x AS TEXT)", SqliteDialect.Instance.TranslateCastToString("x"))
        Assert.Equal("CAST(x AS NVARCHAR(MAX))", SqlServerDialect.Instance.TranslateCastToString("x"))
        Assert.Equal("CAST(x AS TEXT)", PostgreSqlDialect.Instance.TranslateCastToString("x"))
        Assert.Equal("CAST(x AS CHAR)", MySqlDialect.Instance.TranslateCastToString("x"))
        Assert.Equal("TO_CHAR(x)", OracleDialect.Instance.TranslateCastToString("x"))

        // Substring
        Assert.Equal("SUBSTRING(s, (0) + 1, 5)", SqlServerDialect.Instance.TranslateSubstring("s", "0", Some "5"))
        Assert.Equal("SUBSTRING(s, (0) + 1, LEN(s))", SqlServerDialect.Instance.TranslateSubstring("s", "0", None))
        Assert.Equal("SUBSTR(s, (0) + 1, 5)", SqliteDialect.Instance.TranslateSubstring("s", "0", Some "5"))
        Assert.Equal("SUBSTR(s, (0) + 1)", SqliteDialect.Instance.TranslateSubstring("s", "0", None))
        Assert.Equal("SUBSTR(s, (0) + 1, 5)", PostgreSqlDialect.Instance.TranslateSubstring("s", "0", Some "5"))

        // Ceiling
        Assert.Equal("CEIL(v)", SqliteDialect.Instance.TranslateCeiling("v"))
        Assert.Equal("CEIL(v)", PostgreSqlDialect.Instance.TranslateCeiling("v"))
        Assert.Equal("CEIL(v)", OracleDialect.Instance.TranslateCeiling("v"))
        Assert.Equal("CEILING(v)", SqlServerDialect.Instance.TranslateCeiling("v"))
        Assert.Equal("CEILING(v)", MySqlDialect.Instance.TranslateCeiling("v"))

        // IndexOf
        Assert.Equal("(CHARINDEX(sub, str) - 1)", SqlServerDialect.Instance.TranslateIndexOf("str", "sub"))
        Assert.Equal("(POSITION(sub IN str) - 1)", PostgreSqlDialect.Instance.TranslateIndexOf("str", "sub"))
        Assert.Equal("(INSTR(str, sub) - 1)", SqliteDialect.Instance.TranslateIndexOf("str", "sub"))
        Assert.Equal("(INSTR(str, sub) - 1)", MySqlDialect.Instance.TranslateIndexOf("str", "sub"))

    // --- IStagableConnector ReadRaw Tests ---

    [<Fact>]
    member _.``IStagableConnector ReadRaw reads CSV correctly with and without maxRows``() =
        let tempCsv = Path.Combine(Path.GetTempPath(), $"readraw_{Guid.NewGuid():N}.csv")
        File.WriteAllText(tempCsv, "Id,Name\n1,Alice\n2,Bob\n3,Charlie")

        try
            let connector = CsvConnector(tempCsv) :> IStagableConnector
            let allRows = connector.ReadRaw None
            Assert.Equal(3, allRows.Rows |> Seq.length)
            Assert.Equal<string seq>([ "Id"; "Name" ], allRows.Columns)

            let limitedRows = connector.ReadRaw(Some 2)
            Assert.Equal(2, limitedRows.Rows |> Seq.length)
        finally
            if File.Exists tempCsv then
                File.Delete tempCsv

    [<Fact>]
    member _.``IStagableConnector ReadRaw reads JSON correctly with and without maxRows``() =
        let tempJson = Path.Combine(Path.GetTempPath(), $"readraw_{Guid.NewGuid():N}.json")
        File.WriteAllText(tempJson, "[{\"Id\":1,\"Val\":\"A\"},{\"Id\":2,\"Val\":\"B\"},{\"Id\":3,\"Val\":\"C\"}]")

        try
            let connector = JsonConnector(tempJson) :> IStagableConnector
            let allRows = connector.ReadRaw None
            Assert.Equal(3, allRows.Rows |> Seq.length)

            let limitedRows = connector.ReadRaw(Some 1)
            Assert.Equal(1, limitedRows.Rows |> Seq.length)
        finally
            if File.Exists tempJson then
                File.Delete tempJson

    [<Fact>]
    member _.``IStagableConnector ReadRaw reads XML correctly with and without maxRows``() =
        let tempXml = Path.Combine(Path.GetTempPath(), $"readraw_{Guid.NewGuid():N}.xml")

        File.WriteAllText(
            tempXml,
            "<Items><Item><Id>1</Id><Val>A</Val></Item><Item><Id>2</Id><Val>B</Val></Item></Items>"
        )

        try
            let connector = XmlConnector(tempXml) :> IStagableConnector
            let allRows = connector.ReadRaw None
            Assert.Equal(2, allRows.Rows |> Seq.length)

            let limitedRows = connector.ReadRaw(Some 1)
            Assert.Equal(1, limitedRows.Rows |> Seq.length)
        finally
            if File.Exists tempXml then
                File.Delete tempXml
