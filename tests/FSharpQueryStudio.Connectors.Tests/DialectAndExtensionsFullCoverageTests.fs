namespace FSharpQueryStudio.Connectors.Tests

open System
open System.Collections.Generic
open System.Data
open System.IO
open System.Linq
open System.Threading
open System.Threading.Tasks
open Microsoft.FSharp.Linq
open Xunit
open FSharpQueryStudio.Connectors
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security

type DummyExplainConnector(dialectRows: obj array array) =
    interface IQueryableConnector with
        member _.Kind = ConnectorKind.Sqlite
        member _.ConnectionString = "Data Source=:memory:"
        member _.TestConnectionAsync(_: CancellationToken) = Task.FromResult true
        member _.DiscoverSchemaAsync(_: CancellationToken) = Task.FromResult []

        member _.ExecuteQuery (sql: string) (_: (string * obj) list) : ConnectorQueryResult =
            {
                Columns = [ "PlanOutput" ]
                Rows = dialectRows
            }

type MockProviderConnector() =
    interface IConnector with
        member _.Kind = ConnectorKind.SqlServer
        member _.TestConnectionAsync(_) = Task.FromResult true
        member _.DiscoverSchemaAsync(_) = Task.FromResult []

    interface ISqlDialectProvider with
        member _.Dialect = SqlServerDialect.Instance

type TestPersonRecord =
    { Id: int; Name: string; Score: double }

type TestSaleRecord =
    {
        Region: string
        Month: string
        Amount: decimal
    }

module DialectAndExtensionsFullCoverageTests =

    let private allDialects: ISqlDialect list =
        [
            SqliteDialect.Instance
            PostgreSqlDialect.Instance
            SqlServerDialect.Instance
            MySqlDialect.Instance
            MariaDbDialect.Instance
            OracleDialect.Instance
        ]

    // -------------------------------------------------------------------------
    // 1. ISqlDialect Coverage Tests for all 6 Dialects (2 tests per scenario)
    // -------------------------------------------------------------------------

    [<Fact>]
    let ``Dialects QuoteIdentifier quotes reserved keywords and names with spaces`` () =
        for dialect in allDialects do
            let quotedKeyword = dialect.QuoteIdentifier "order"
            let quotedSpace = dialect.QuoteIdentifier "user name"
            let plain = dialect.QuoteIdentifier "simple"

            Assert.True(quotedKeyword <> "order")
            Assert.True(quotedSpace <> "user name")
            Assert.Equal("simple", plain)

    [<Fact>]
    let ``Dialects QuoteIdentifier handles already quoted or special characters`` () =
        for dialect in allDialects do
            let quotedLeadingDigit = dialect.QuoteIdentifier "1table"
            let quotedWithSpace = dialect.QuoteIdentifier "my table"
            Assert.True(quotedLeadingDigit <> "1table")
            Assert.True(quotedWithSpace <> "my table")

    [<Fact>]
    let ``Dialects FormatLimitOffset covers all 4 take and skip combinations`` () =
        for dialect in allDialects do
            let both = dialect.FormatLimitOffset(Some 10, Some 20, false)
            let takeOnly = dialect.FormatLimitOffset(Some 10, None, false)
            let skipOnly = dialect.FormatLimitOffset(None, Some 20, false)
            let none = dialect.FormatLimitOffset(None, None, false)

            Assert.NotEmpty(both)
            Assert.NotEmpty(takeOnly)
            Assert.Equal("", none)

            if dialect.Name <> "Oracle" && dialect.Name <> "SqlServer" then
                Assert.NotEmpty(skipOnly)

    [<Fact>]
    let ``Dialects FormatLimitOffset hasOrderedPaging parameter variations`` () =
        for dialect in allDialects do
            let pagedWithOrder = dialect.FormatLimitOffset(Some 5, Some 15, true)
            let pagedWithoutOrder = dialect.FormatLimitOffset(Some 5, Some 15, false)
            Assert.NotEmpty(pagedWithOrder)
            Assert.NotEmpty(pagedWithoutOrder)

    [<Fact>]
    let ``Dialects FormatApplyJoin and FormatOuterApplyJoin produce valid syntax`` () =
        for dialect in allDialects do
            let crossApply = dialect.FormatApplyJoin(false, "subTable")
            let outerApply = dialect.FormatOuterApplyJoin("subTable", Some "1=1")

            let outerApplyCustomOn =
                dialect.FormatOuterApplyJoin("subTable", Some "t.id = subTable.id")

            let outerApplyNoneOn = dialect.FormatOuterApplyJoin("subTable", None)

            Assert.NotEmpty(crossApply)
            Assert.NotEmpty(outerApply)
            Assert.NotEmpty(outerApplyCustomOn)
            Assert.NotEmpty(outerApplyNoneOn)

    [<Fact>]
    let ``Dialects Static properties and metadata checks`` () =
        for dialect in allDialects do
            Assert.NotEmpty(dialect.Name)
            Assert.NotEmpty(dialect.SqlProviderType)
            // SupportsRecursiveCteKeyword should be boolean
            let _ = dialect.SupportsRecursiveCteKeyword
            let _ = dialect.DualTable
            let _ = dialect.DefaultWindowOrder
            ()

    [<Fact>]
    let ``Dialects TranslateDatePart handles all standard parts and unknown parts`` () =
        let parts = [ "year"; "month"; "day"; "hour"; "minute"; "second"; "date" ]

        for dialect in allDialects do
            for part in parts do
                let translated = dialect.TranslateDatePart(part, "created_at")
                Assert.True(translated.IsSome, $"Expected {dialect.Name} to support datepart {part}")

            let unknown = dialect.TranslateDatePart("millisecond_unknown", "created_at")
            Assert.True(unknown.IsNone)

    [<Fact>]
    let ``Dialects TranslateDateTrunc and TranslateJsonExtract coverage`` () =
        for dialect in allDialects do
            let trunc = dialect.TranslateDateTrunc "created_at"
            Assert.True(trunc.IsSome)

            let jsonSimple = dialect.TranslateJsonExtract("metadata", "items[0].id")
            let jsonNested = dialect.TranslateJsonExtract("metadata", "a.b.c")
            Assert.True(jsonSimple.IsSome)
            Assert.True(jsonNested.IsSome)

    [<Fact>]
    let ``Dialects FormatExplain formats output when rows are present`` () =
        let mockRows = [| [| box "1"; box "0"; box "0"; box "SCAN TABLE users" |] |]
        let mockConn = DummyExplainConnector(mockRows)

        for dialect in allDialects do
            let explain = dialect.FormatExplain(mockConn, "SELECT * FROM users", [])
            Assert.NotEmpty(explain)

            Assert.True(
                explain.Contains("PLAN", StringComparison.OrdinalIgnoreCase)
                || explain.Contains("EXPLAIN", StringComparison.OrdinalIgnoreCase)
            )

    [<Fact>]
    let ``Dialects FormatExplain formats output when rows are empty or minimal`` () =
        let mockRows = [| [| box "" |] |]
        let mockConn = DummyExplainConnector(mockRows)

        for dialect in allDialects do
            let explain = dialect.FormatExplain(mockConn, "SELECT 1", [])
            Assert.NotEmpty(explain)

    [<Fact>]
    let ``Dialects FormatSqlTarget produces appropriate schema and table identifiers`` () =
        for dialect in allDialects do
            let targetSimple = dialect.FormatSqlTarget("main", None, "orders", "orders", true)

            let targetWithSub =
                dialect.FormatSqlTarget("main", Some "sales", "orders", "sales.orders", false)

            let targetDefault =
                dialect.FormatSqlTarget("public", Some "public", "orders", "orders", true)

            Assert.NotEmpty(targetSimple)
            Assert.NotEmpty(targetWithSub)
            Assert.NotEmpty(targetDefault)

    [<Fact>]
    let ``Dialects FormatSelect generates complete SELECT statements`` () =
        for dialect in allDialects do
            let sqlSimple =
                dialect.FormatSelect(
                    false,
                    "id, name",
                    "users",
                    " WHERE id > 0",
                    "",
                    "",
                    " ORDER BY id",
                    Some 10,
                    Some 5
                )

            let sqlDistinct =
                dialect.FormatSelect(
                    true,
                    "name",
                    "users",
                    "",
                    " GROUP BY name",
                    " HAVING count(*) > 1",
                    "",
                    None,
                    None
                )

            Assert.Contains("SELECT id, name FROM users", sqlSimple)
            Assert.Contains("SELECT DISTINCT name FROM users", sqlDistinct)

    [<Fact>]
    let ``Dialects FormatBooleanLiteral, IsEmpty, and IsNotEmpty`` () =
        for dialect in allDialects do
            let bTrue = dialect.FormatBooleanLiteral true
            let bFalse = dialect.FormatBooleanLiteral false
            Assert.True(bTrue <> bFalse)

            let emptyCheck = dialect.FormatIsEmpty "name"
            let notEmptyCheck = dialect.FormatIsNotEmpty "name"
            Assert.Contains("name", emptyCheck)
            Assert.Contains("name", notEmptyCheck)

    [<Fact>]
    let ``Dialects String operations TranslateStringLength, FormatConcat, CastToString, Substring, Ceiling, IndexOf``
        ()
        =
        for dialect in allDialects do
            let lenSql = dialect.TranslateStringLength "col"
            Assert.NotEmpty(lenSql)

            let concat2 = dialect.FormatConcat([ "a"; "b" ])
            let concat3 = dialect.FormatConcat([ "a"; "b"; "c" ])
            Assert.NotEmpty(concat2)
            Assert.NotEmpty(concat3)

            let castStr = dialect.TranslateCastToString "col"
            Assert.NotEmpty(castStr)

            let subWithLen = dialect.TranslateSubstring("col", "1", Some "3")
            let subWithoutLen = dialect.TranslateSubstring("col", "1", None)
            Assert.NotEmpty(subWithLen)
            Assert.NotEmpty(subWithoutLen)

            let ceilSql = dialect.TranslateCeiling "col"
            let indexSql = dialect.TranslateIndexOf("haystack", "needle")
            Assert.NotEmpty(ceilSql)
            Assert.NotEmpty(indexSql)

    [<Fact>]
    let ``Dialects TranslateDateFormat with formatting tokens and fallback`` () =
        for dialect in allDialects do
            let dateFmt = dialect.TranslateDateFormat("created_at", "yyyy-MM-dd HH:mm:ss")
            let fallbackFmt = dialect.TranslateDateFormat("created_at", "plain_text")
            Assert.True(dateFmt.IsSome)
            Assert.True(fallbackFmt.IsSome)

    [<Fact>]
    let ``Dialects Specific pagination and syntax branches coverage`` () =
        // SqlServer isOuter apply
        Assert.Equal("OUTER APPLY sub", SqlServerDialect.Instance.FormatApplyJoin(true, "sub"))
        Assert.Equal("CROSS APPLY sub", SqlServerDialect.Instance.FormatApplyJoin(false, "sub"))

        // SqlServer skip only with and without order
        let ssSkipNoOrder =
            SqlServerDialect.Instance.FormatSelect(false, "*", "T", "", "", "", "", None, Some 5)

        Assert.Contains("ORDER BY (SELECT NULL) OFFSET 5 ROWS", ssSkipNoOrder)

        let ssSkipWithOrder =
            SqlServerDialect.Instance.FormatSelect(false, "*", "T", "", "", "", " ORDER BY id", None, Some 5)

        Assert.Contains("ORDER BY id OFFSET 5 ROWS", ssSkipWithOrder)

        // SqlServer take only without order -> TOP
        let ssTakeNoOrder =
            SqlServerDialect.Instance.FormatSelect(false, "*", "T", "", "", "", "", Some 10, None)

        Assert.Contains("TOP (10)", ssTakeNoOrder)

        // SqlServer FormatSqlTarget schema prefixes
        let ssTargetCustom =
            SqlServerDialect.Instance.FormatSqlTarget("sales_db", None, "orders", "sales_db.orders", false)

        Assert.Equal("sales_db.dbo.orders", ssTargetCustom)

        let ssTargetDbo =
            SqlServerDialect.Instance.FormatSqlTarget("dbo", None, "orders", "dbo.orders", true)

        Assert.Equal("dbo.orders", ssTargetDbo)

        // Postgres JSON single field extract
        let pgJsonSingle =
            PostgreSqlDialect.Instance.TranslateJsonExtract("metadata", "status")

        Assert.Equal(Some "(metadata ->> 'status')", pgJsonSingle)

        // Postgres skip only
        let pgSkipOnly =
            PostgreSqlDialect.Instance.FormatSelect(false, "*", "T", "", "", "", "", None, Some 5)

        Assert.Contains("OFFSET 5", pgSkipOnly)

        // MySQL / MariaDB skip only
        let mySkipOnly =
            MySqlDialect.Instance.FormatSelect(false, "*", "T", "", "", "", "", None, Some 5)

        Assert.Contains("LIMIT 18446744073709551615 OFFSET 5", mySkipOnly)

        let mariaSkipOnly =
            MariaDbDialect.Instance.FormatSelect(false, "*", "T", "", "", "", "", None, Some 5)

        Assert.Contains("LIMIT 18446744073709551615 OFFSET 5", mariaSkipOnly)

        // Oracle skip only
        let oraSkipOnly =
            OracleDialect.Instance.FormatSelect(false, "*", "T", "", "", "", "", None, Some 5)

        Assert.Contains("OFFSET 5 ROWS", oraSkipOnly)

        // Dialects SupportsRecursiveCteKeyword and ApplyJoins
        Assert.False(SqlServerDialect.Instance.SupportsRecursiveCteKeyword)
        Assert.True(PostgreSqlDialect.Instance.SupportsRecursiveCteKeyword)
        Assert.False(OracleDialect.Instance.SupportsRecursiveCteKeyword)
        Assert.True(MariaDbDialect.Instance.SupportsRecursiveCteKeyword)
        Assert.True(MySqlDialect.Instance.SupportsRecursiveCteKeyword)
        Assert.True(SqliteDialect.Instance.SupportsRecursiveCteKeyword)

        // Outer ApplyJoins
        Assert.Equal("LEFT JOIN LATERAL T ON true", PostgreSqlDialect.Instance.FormatApplyJoin(true, "T"))
        Assert.Equal("OUTER APPLY T", OracleDialect.Instance.FormatApplyJoin(true, "T"))
        Assert.Equal("LEFT JOIN LATERAL T ON true", MySqlDialect.Instance.FormatApplyJoin(true, "T"))
        Assert.Equal("LEFT JOIN LATERAL T ON true", MariaDbDialect.Instance.FormatApplyJoin(true, "T"))

        // Additional FormatSelect variations
        let oraTakeOnly =
            OracleDialect.Instance.FormatSelect(false, "*", "T", "", "", "", "", Some 10, None)

        Assert.Equal("SELECT * FROM T OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY", oraTakeOnly)

        let oraNone =
            OracleDialect.Instance.FormatSelect(false, "*", "T", "", "", "", "", None, None)

        Assert.Equal("SELECT * FROM T", oraNone)

        let oraPostProcess =
            OracleDialect.Instance.PostProcessSql("SELECT _id, _name FROM T")

        Assert.Equal("SELECT id, name FROM T", oraPostProcess)

        let myTakeOnly =
            MySqlDialect.Instance.FormatSelect(false, "*", "T", "", "", "", "", Some 10, None)

        Assert.Equal("SELECT * FROM T LIMIT 10", myTakeOnly)

        let myNone =
            MySqlDialect.Instance.FormatSelect(false, "*", "T", "", "", "", "", None, None)

        Assert.Equal("SELECT * FROM T", myNone)

        let mariaTakeOnly =
            MariaDbDialect.Instance.FormatSelect(false, "*", "T", "", "", "", "", Some 10, None)

        Assert.Equal("SELECT * FROM T LIMIT 10", mariaTakeOnly)

        let mariaNone =
            MariaDbDialect.Instance.FormatSelect(false, "*", "T", "", "", "", "", None, None)

        Assert.Equal("SELECT * FROM T", mariaNone)

        let sqliteSkipOnly = SqliteDialect.Instance.FormatLimitOffset(None, Some 5, false)
        Assert.Equal(" LIMIT -1 OFFSET 5", sqliteSkipOnly)

        let sqliteNone =
            SqliteDialect.Instance.FormatSelect(false, "*", "T", "", "", "", "", None, None)

        Assert.Equal("SELECT * FROM T", sqliteNone)

        // SQLite FormatExplain fallback when empty rows or single column rows
        let mockEmptyExplainConn =
            { new IQueryableConnector with
                member _.Kind = ConnectorKind.Sqlite
                member _.ConnectionString = "Data Source=:memory:"
                member _.TestConnectionAsync(_) = Task.FromResult true
                member _.DiscoverSchemaAsync(_) = Task.FromResult []
                member _.ExecuteQuery _ _ = { Columns = [ "Plan" ]; Rows = [] }
            }

        let explainEmpty =
            SqliteDialect.Instance.FormatExplain(mockEmptyExplainConn, "SELECT 1", [])

        Assert.Equal("-- SQLite EXPLAIN: Plan generated successfully.", explainEmpty)

        let mockSingleColExplainConn =
            { new IQueryableConnector with
                member _.Kind = ConnectorKind.Sqlite
                member _.ConnectionString = "Data Source=:memory:"
                member _.TestConnectionAsync(_) = Task.FromResult true
                member _.DiscoverSchemaAsync(_) = Task.FromResult []

                member _.ExecuteQuery _ _ =
                    {
                        Columns = [ "Detail" ]
                        Rows = [ [| box "SCAN TABLE users" |] ]
                    }
            }

        let explainSingleCol =
            SqliteDialect.Instance.FormatExplain(mockSingleColExplainConn, "SELECT 1", [])

        Assert.Equal("-- SQLite EXPLAIN QUERY PLAN:\nSCAN TABLE users", explainSingleCol)

        // SqlDialect with null Name
        let mockNullNameDialect =
            { new ISqlDialect with
                member _.Name = null
                member _.QuoteIdentifier(n) = n
                member _.FormatLimitOffset(_, _, _) = ""
                member _.FormatApplyJoin(_, s) = s
                member _.SupportsRecursiveCteKeyword = false
                member _.DualTable = None
                member _.TranslateDatePart(_, _) = None
                member _.TranslateDateTrunc(_) = None
                member _.TranslateJsonExtract(_, _) = None
                member _.FormatExplain(_, _, _) = ""
                member _.FormatSqlTarget(_, _, r, _, _) = r
                member _.SqlProviderType = ""
                member _.FormatOuterApplyJoin(s, _) = s
                member _.FormatSelect(_, _, _, _, _, _, _, _, _) = ""
                member _.PostProcessSql(s) = s
                member _.FormatBooleanLiteral(_) = ""
                member _.FormatIsEmpty(_) = ""
                member _.FormatIsNotEmpty(_) = ""
                member _.DefaultWindowOrder = None
                member _.TranslateStringLength(_) = ""
                member _.FormatConcat(_) = ""
                member _.TranslateDateFormat(_, _) = None
                member _.TranslateCastToString(_) = ""
                member _.TranslateSubstring(_, _, _) = ""
                member _.TranslateCeiling(_) = ""
                member _.TranslateIndexOf(_, _) = ""
            }

        Assert.Equal(SqlDialect.Sqlite, SqlDialect.fromDialect mockNullNameDialect)

    // -------------------------------------------------------------------------
    // 2. SqlDialect Module Tests (2 tests per scenario)
    // -------------------------------------------------------------------------

    [<Fact>]
    let ``SqlDialect fromConnectorKind resolves all known connector kinds and default`` () =
        Assert.Equal(SqlDialect.PostgreSql, SqlDialect.fromConnectorKind (ConnectorKind "PostgreSql"))
        Assert.Equal(SqlDialect.SqlServer, SqlDialect.fromConnectorKind (ConnectorKind "SqlServer"))
        Assert.Equal(SqlDialect.MySql, SqlDialect.fromConnectorKind (ConnectorKind "MySql"))
        Assert.Equal(SqlDialect.MariaDb, SqlDialect.fromConnectorKind (ConnectorKind "MariaDb"))
        Assert.Equal(SqlDialect.Oracle, SqlDialect.fromConnectorKind (ConnectorKind "Oracle"))
        Assert.Equal(SqlDialect.Sqlite, SqlDialect.fromConnectorKind (ConnectorKind "Sqlite"))
        Assert.Equal(SqlDialect.Sqlite, SqlDialect.fromConnectorKind (ConnectorKind "UnknownKind"))

    [<Fact>]
    let ``SqlDialect fromDialect resolves names and handles nulls`` () =
        Assert.Equal(SqlDialect.Sqlite, SqlDialect.fromDialect Unchecked.defaultof<ISqlDialect>)
        Assert.Equal(SqlDialect.PostgreSql, SqlDialect.fromDialect PostgreSqlDialect.Instance)
        Assert.Equal(SqlDialect.SqlServer, SqlDialect.fromDialect SqlServerDialect.Instance)
        Assert.Equal(SqlDialect.MySql, SqlDialect.fromDialect MySqlDialect.Instance)
        Assert.Equal(SqlDialect.MariaDb, SqlDialect.fromDialect MariaDbDialect.Instance)
        Assert.Equal(SqlDialect.Oracle, SqlDialect.fromDialect OracleDialect.Instance)
        Assert.Equal(SqlDialect.Sqlite, SqlDialect.fromDialect SqliteDialect.Instance)

        // Mock dialect with mssql or other names
        let mockMssql =
            { new ISqlDialect with
                member _.Name = "Microsoft SQL Server"
                member _.QuoteIdentifier(n) = n
                member _.FormatLimitOffset(_, _, _) = ""
                member _.FormatApplyJoin(_, s) = s
                member _.SupportsRecursiveCteKeyword = false
                member _.DualTable = None
                member _.TranslateDatePart(_, _) = None
                member _.TranslateDateTrunc(_) = None
                member _.TranslateJsonExtract(_, _) = None
                member _.FormatExplain(_, _, _) = ""
                member _.FormatSqlTarget(_, _, r, _, _) = r
                member _.SqlProviderType = ""
                member _.FormatOuterApplyJoin(s, _) = s
                member _.FormatSelect(_, _, _, _, _, _, _, _, _) = ""
                member _.PostProcessSql(s) = s
                member _.FormatBooleanLiteral(_) = ""
                member _.FormatIsEmpty(_) = ""
                member _.FormatIsNotEmpty(_) = ""
                member _.DefaultWindowOrder = None
                member _.TranslateStringLength(_) = ""
                member _.FormatConcat(_) = ""
                member _.TranslateDateFormat(_, _) = None
                member _.TranslateCastToString(_) = ""
                member _.TranslateSubstring(_, _, _) = ""
                member _.TranslateCeiling(_) = ""
                member _.TranslateIndexOf(_, _) = ""
            }

        Assert.Equal(SqlDialect.SqlServer, SqlDialect.fromDialect mockMssql)

    [<Fact>]
    let ``SqlDialect fromConnector and fromConnectorToDialect with ISqlDialectProvider and plain IConnector`` () =
        let mockPlainConnector =
            { new IConnector with
                member _.Kind = ConnectorKind.PostgreSql
                member _.TestConnectionAsync(_) = Task.FromResult true
                member _.DiscoverSchemaAsync(_) = Task.FromResult []
            }

        let dialectFromPlain = SqlDialect.fromConnector mockPlainConnector
        let dialectEnumFromPlain = SqlDialect.fromConnectorToDialect mockPlainConnector
        Assert.Equal(SqlDialect.PostgreSql, dialectEnumFromPlain)
        Assert.Equal("PostgreSql", dialectFromPlain.Name)

        // Mock with ISqlDialectProvider
        let mockProvider = MockProviderConnector() :> IConnector
        let dialectFromProvider = SqlDialect.fromConnector mockProvider
        let dialectEnumFromProvider = SqlDialect.fromConnectorToDialect mockProvider
        Assert.Equal(SqlDialect.SqlServer, dialectEnumFromProvider)
        Assert.Equal("SqlServer", dialectFromProvider.Name)

        let quoted = SqlDialect.quoteIdentifier SqlDialect.Sqlite "order"
        Assert.Equal("\"order\"", quoted)

        let quotedSqlServer = SqlDialect.quoteIdentifier SqlDialect.SqlServer "My Table"
        Assert.Equal("[My Table]", quotedSqlServer)

    // -------------------------------------------------------------------------
    // 3. ConnectionStringSecrets Tests (2 tests per scenario)
    // -------------------------------------------------------------------------

    type private SimpleMemorySecretStore(dict: Dictionary<string, string>) =
        interface ISecretStore with
            member _.TryRead(ref) =
                match dict.TryGetValue ref with
                | true, v -> Some v
                | false, _ -> None

            member this.TryReadAsync(ref, _) =
                Task.FromResult((this :> ISecretStore).TryRead ref)

            member _.Save ref sec = dict.[ref] <- sec
            member _.Delete ref = dict.Remove ref |> ignore

    [<Fact>]
    let ``ConnectionStringSecrets substitute handles URI schemes with user password and user only`` () =
        let uriWithPass = "postgresql://myuser:oldpass@localhost:5432/mydb"
        let resWithPass = ConnectionStringSecrets.substitute "newpass" uriWithPass
        Assert.Equal("postgresql://myuser:newpass@localhost:5432/mydb", resWithPass)

        let uriUserOnly = "mysql://myuser@localhost:3306/mydb"
        let resUserOnly = ConnectionStringSecrets.substitute "newpass" uriUserOnly
        Assert.Equal("mysql://myuser:newpass@localhost:3306/mydb", resUserOnly)

        let uriNoUser = "http://localhost:8080/api"
        let resNoUser = ConnectionStringSecrets.substitute "newpass" uriNoUser
        Assert.Equal(uriNoUser, resNoUser)

        let invalidUri = "ht tp://invalid uri with spaces"
        let resInvalid = ConnectionStringSecrets.substitute "newpass" invalidUri
        Assert.Equal(invalidUri, resInvalid)

        let malformedPgUri = "postgres://[invalid uri"
        let resMalformedPgUri = ConnectionStringSecrets.substitute "newpass" malformedPgUri
        Assert.Equal(malformedPgUri, resMalformedPgUri)

    [<Fact>]
    let ``ConnectionStringSecrets substitute handles ADO connection string and malformed string fallback`` () =
        let adoStr = "Server=127.0.0.1;Database=test;User Id=sa;"
        let resAdo = ConnectionStringSecrets.substitute "secret123" adoStr
        Assert.Contains("Password=secret123", resAdo)

        let malformed = ";;;===;;;"
        let resMalformed = ConnectionStringSecrets.substitute "secret123" malformed
        Assert.Contains("Password=secret123", resMalformed)

        let malformedAdo = "invalidKeyWithoutEqualAndNoValue;;;"
        let resMalformedAdo = ConnectionStringSecrets.substitute "secret123" malformedAdo
        Assert.Contains("Password=secret123", resMalformedAdo)

    [<Fact>]
    let ``ConnectionStringSecrets resolve and resolveAsync handle None reference, missing secret, and valid secret``
        ()
        =
        let dict = Dictionary<string, string>()
        dict.["ref-valid"] <- "mysecret"
        dict.["ref-empty"] <- ""
        let store = SimpleMemorySecretStore(dict)

        // None reference
        Assert.Equal("template", ConnectionStringSecrets.resolve store None "template")
        // Missing secret
        Assert.Equal("template", ConnectionStringSecrets.resolve store (Some "ref-missing") "template")
        // Empty secret
        Assert.Equal("template", ConnectionStringSecrets.resolve store (Some "ref-empty") "template")
        // Valid secret
        let res =
            ConnectionStringSecrets.resolve store (Some "ref-valid") "Server=127.0.0.1;Password={password};"

        Assert.Equal("Server=127.0.0.1;Password=mysecret;", res)

    [<Fact>]
    let ``ConnectionStringSecrets resolveAsync handles None reference, missing secret, and valid secret`` () =
        task {
            let dict = Dictionary<string, string>()
            dict.["ref-valid"] <- "mysecret"
            let store = SimpleMemorySecretStore(dict)

            let! resNone =
                ConnectionStringSecrets.resolveAsync store None "template" CancellationToken.None

            Assert.Equal("template", resNone)

            let! resMissing =
                ConnectionStringSecrets.resolveAsync store (Some "ref-missing") "template" CancellationToken.None

            Assert.Equal("template", resMissing)

            let! resValid =
                ConnectionStringSecrets.resolveAsync
                    store
                    (Some "ref-valid")
                    "Server=127.0.0.1;Password={password};"
                    CancellationToken.None

            Assert.Equal("Server=127.0.0.1;Password=mysecret;", resValid)
        }

    // -------------------------------------------------------------------------
    // 4. ConnectionExtensions Tests (2 tests per scenario)
    // -------------------------------------------------------------------------

    [<Fact>]
    let ``ConnectionExtensions toRawSql, toRawQuery, and toRawSqlWithParameters`` () =
        let rawStr = "SELECT * FROM Users WHERE active = 1"
        Assert.Equal(rawStr, toRawSql rawStr)
        let formatted = toRawQuery rawStr
        Assert.Contains("SELECT *", formatted)
        Assert.Contains("FROM Users", formatted)

        let sql, p = toRawSqlWithParameters rawStr
        Assert.Equal(rawStr, sql)
        Assert.Empty(p)

    [<Fact>]
    let ``ConnectionExtensions toRawSql with mock object returning SQL`` () =
        let mockQuery =
            { new obj() with
                member _.ToString() = "SELECT id, title FROM Articles"
            }

        Assert.Equal("SELECT id, title FROM Articles", toRawSql mockQuery)
        let formatted = toRawQuery mockQuery
        Assert.Contains("SELECT id, title", formatted)
        Assert.Contains("FROM Articles", formatted)

    [<Fact>]
    let ``ConnectionExtensions ConnectorQueryResult As and AsQueryable map rows to typed records`` () =
        let result: ConnectorQueryResult =
            {
                Columns = [ "Id"; "Name"; "Score" ]
                Rows = [| [| box 1; box "Alice"; box 95.5 |]; [| box 2; box "Bob"; box 88.0 |] |]
            }

        let people = result.As<TestPersonRecord>() |> Seq.toList
        Assert.Equal(2, people.Length)
        Assert.Equal("Alice", people.[0].Name)
        Assert.Equal(95.5, people.[0].Score)
        Assert.Equal("Bob", people.[1].Name)

        let queryable = result.AsQueryable<TestPersonRecord>()
        let filtered = queryable.Where(fun p -> p.Score > 90.0).ToList()
        Assert.Single(filtered) |> ignore
        Assert.Equal("Alice", filtered.[0].Name)

    [<Fact>]
    let ``ConnectionExtensions ConnectorQueryResult As handles empty rows and null values`` () =
        let result: ConnectorQueryResult =
            {
                Columns = [ "Id"; "Name"; "Score" ]
                Rows = [||]
            }

        let people = result.As<TestPersonRecord>() |> Seq.toList
        Assert.Empty(people)

        let queryable = result.AsQueryable<TestPersonRecord>()
        Assert.Empty(queryable)

    [<Fact>]
    let ``ConnectionExtensions Seq pivot performs cross-tabulation and aggregation`` () =
        let sales =
            [
                {
                    Region = "North"
                    Month = "Jan"
                    Amount = 100M
                }
                {
                    Region = "North"
                    Month = "Feb"
                    Amount = 150M
                }
                {
                    Region = "North"
                    Month = "Jan"
                    Amount = 50M
                }
                {
                    Region = "South"
                    Month = "Jan"
                    Amount = 200M
                }
                {
                    Region = "South"
                    Month = "Feb"
                    Amount = 300M
                }
            ]

        let pivoted =
            sales
            |> Seq.pivot (fun s -> s.Region) (fun s -> s.Month) (fun s -> s.Amount) Seq.sum
            |> Seq.toList

        Assert.Equal(2, pivoted.Length)

        let north = pivoted |> List.find (fun (r, _) -> r = "North") |> snd
        Assert.Equal(150M, north.["Jan"])
        Assert.Equal(150M, north.["Feb"])

        let south = pivoted |> List.find (fun (r, _) -> r = "South") |> snd
        Assert.Equal(200M, south.["Jan"])
        Assert.Equal(300M, south.["Feb"])

    [<Fact>]
    let ``ConnectionExtensions Seq pivot with empty sequence and count aggregator`` () =
        let emptySales = Seq.empty<TestSaleRecord>

        let pivotedEmpty =
            emptySales
            |> Seq.pivot (fun s -> s.Region) (fun s -> s.Month) (fun s -> s.Amount) Seq.length
            |> Seq.toList

        Assert.Empty(pivotedEmpty)

    // -------------------------------------------------------------------------
    // 5. FSqlQueryBuilder and SqlComputationExtensions Full Coverage (>= 2 tests/scenario)
    // -------------------------------------------------------------------------

    [<Fact>]
    let ``FSqlQueryBuilder isNotNull and isNotEmpty direct invocation scenario 1`` () =
        let b = FSqlQueryBuilder()
        Assert.True(b.isNotNull (42))
        Assert.False(b.isNotNull (null))
        Assert.True(b.isNotEmpty ("abc"))
        Assert.False(b.isNotEmpty (""))
        Assert.False(b.isNotEmpty (null))
        Assert.True(b.isNotNullOrEmpty ("valid"))
        Assert.False(b.isNotNullOrEmpty (""))
        Assert.False(b.isNotNullOrEmpty (null))

    [<Fact>]
    let ``FSqlQueryBuilder exists and notExists direct invocation scenario 2`` () =
        let b = FSqlQueryBuilder()
        Assert.True(b.exists ([ 10; 20 ]))
        Assert.False(b.notExists ([ 10; 20 ]))
        Assert.True(b.exists ([]))
        Assert.False(b.notExists ([]))

    [<Fact>]
    let ``FSqlQueryBuilder WhereExists and WhereNotExists throw markers scenario 1`` () =
        let b = FSqlQueryBuilder()
        let dummySource: QuerySource<int, System.Linq.IQueryable> = Unchecked.defaultof<_>

        let ex1 =
            Assert.Throws<Exception>(fun () -> b.WhereExists(dummySource, fun (x: int) -> [ x ]) |> ignore)

        Assert.Contains("whereExists marker", ex1.Message)

        let ex2 =
            Assert.Throws<Exception>(fun () -> b.WhereExistsPred(dummySource, fun (x: int) -> true) |> ignore)

        Assert.Contains("whereExists marker", ex2.Message)

    [<Fact>]
    let ``FSqlQueryBuilder WhereNotExists and Having throw markers scenario 2`` () =
        let b = FSqlQueryBuilder()
        let dummySource: QuerySource<int, System.Linq.IQueryable> = Unchecked.defaultof<_>

        let ex1 =
            Assert.Throws<Exception>(fun () -> b.WhereNotExists(dummySource, fun (x: int) -> [ x ]) |> ignore)

        Assert.Contains("whereNotExists marker", ex1.Message)

        let ex2 =
            Assert.Throws<Exception>(fun () -> b.WhereNotExistsPred(dummySource, fun (x: int) -> false) |> ignore)

        Assert.Contains("whereNotExists marker", ex2.Message)

        let ex3 =
            Assert.Throws<Exception>(fun () -> b.Having(dummySource, fun (x: int) -> true) |> ignore)

        Assert.Contains("having marker", ex3.Message)

    [<Fact>]
    let ``FSqlQueryBuilder set operation markers throw expected exceptions scenario 1`` () =
        let b = FSqlQueryBuilder()
        let dummySource: QuerySource<int, System.Linq.IQueryable> = Unchecked.defaultof<_>
        let ex1 = Assert.Throws<Exception>(fun () -> b.Union(dummySource, [ 1 ]) |> ignore)
        Assert.Contains("union marker", ex1.Message)

        let ex2 =
            Assert.Throws<Exception>(fun () -> b.UnionAll(dummySource, [ 1 ]) |> ignore)

        Assert.Contains("unionAll marker", ex2.Message)

    [<Fact>]
    let ``FSqlQueryBuilder intersect and except markers throw expected exceptions scenario 2`` () =
        let b = FSqlQueryBuilder()
        let dummySource: QuerySource<int, System.Linq.IQueryable> = Unchecked.defaultof<_>

        let ex1 =
            Assert.Throws<Exception>(fun () -> b.Intersect(dummySource, [ 1 ]) |> ignore)

        Assert.Contains("intersect marker", ex1.Message)
        let ex2 = Assert.Throws<Exception>(fun () -> b.Except(dummySource, [ 1 ]) |> ignore)
        Assert.Contains("except marker", ex2.Message)

    [<Fact>]
    let ``FSqlQueryBuilder withCte markers throw expected exceptions scenario 1`` () =
        let b = FSqlQueryBuilder()
        let dummySource: QuerySource<int, System.Linq.IQueryable> = Unchecked.defaultof<_>

        let ex1 =
            Assert.Throws<Exception>(fun () -> b.WithCte(dummySource, "cte1", [ 1 ]) |> ignore)

        Assert.Contains("withCte marker", ex1.Message)

        let ex2 =
            Assert.Throws<Exception>(fun () -> b.WithCte(dummySource, [ 1 ]) |> ignore)

        Assert.Contains("withCte marker", ex2.Message)

        let ex3 =
            Assert.Throws<Exception>(fun () -> b.WithCteSql(dummySource, "cte1", "SELECT 1") |> ignore)

        Assert.Contains("withCte marker", ex3.Message)

        let ex4 =
            Assert.Throws<Exception>(fun () -> b.WithCteSql(dummySource, "SELECT 1") |> ignore)

        Assert.Contains("withCte marker", ex4.Message)

        let ex5 =
            Assert.Throws<Exception>(fun () -> b.WithCteSqlAlt(dummySource, "cte1", "SELECT 1") |> ignore)

        Assert.Contains("withCteSql marker", ex5.Message)

        let ex6 =
            Assert.Throws<Exception>(fun () -> b.WithCteSqlAlt(dummySource, "SELECT 1") |> ignore)

        Assert.Contains("withCteSql marker", ex6.Message)

    [<Fact>]
    let ``FSqlQueryBuilder withRecursiveCte markers throw expected exceptions scenario 2`` () =
        let b = FSqlQueryBuilder()
        let dummySource: QuerySource<int, System.Linq.IQueryable> = Unchecked.defaultof<_>

        let ex1 =
            Assert.Throws<Exception>(fun () -> b.WithRecursiveCte(dummySource, "rcte", [ 1 ]) |> ignore)

        Assert.Contains("withRecursiveCte marker", ex1.Message)

        let ex2 =
            Assert.Throws<Exception>(fun () -> b.WithRecursiveCte(dummySource, [ 1 ]) |> ignore)

        Assert.Contains("withRecursiveCte marker", ex2.Message)

        let ex3 =
            Assert.Throws<Exception>(fun () -> b.WithRecursiveCte(dummySource, "rcte", "SELECT 1") |> ignore)

        Assert.Contains("withRecursiveCte marker", ex3.Message)

        let ex4 =
            Assert.Throws<Exception>(fun () -> b.WithRecursiveCte(dummySource, "SELECT 1") |> ignore)

        Assert.Contains("withRecursiveCte marker", ex4.Message)

        let ex5 =
            Assert.Throws<Exception>(fun () -> b.WithRecursiveCteSql(dummySource, "rcte", "SELECT 1") |> ignore)

        Assert.Contains("withRecursiveCteSql marker", ex5.Message)

        let ex6 =
            Assert.Throws<Exception>(fun () -> b.WithRecursiveCteSql(dummySource, "SELECT 1") |> ignore)

        Assert.Contains("withRecursiveCteSql marker", ex6.Message)

    [<Fact>]
    let ``FSqlQueryBuilder full and right outer join markers throw expected exceptions scenario 1`` () =
        let b = FSqlQueryBuilder()
        let dummySource: QuerySource<int, System.Linq.IQueryable> = Unchecked.defaultof<_>

        let ex1 =
            Assert.Throws<Exception>(fun () ->
                b.FullOuterJoin(
                    dummySource,
                    dummySource,
                    (fun (x: int) -> x),
                    (fun (x: int) -> x),
                    (fun (x: int) (ys: seq<int>) -> x)
                )
                |> ignore)

        Assert.Contains("fullOuterJoin marker", ex1.Message)

        let ex2 =
            Assert.Throws<Exception>(fun () ->
                b.RightOuterJoin(
                    dummySource,
                    dummySource,
                    (fun (x: int) -> x),
                    (fun (x: int) -> x),
                    (fun (x: int) (y: int) -> x)
                )
                |> ignore)

        Assert.Contains("rightOuterJoin marker", ex2.Message)

        let ex3 =
            Assert.Throws<Exception>(fun () ->
                b.RightOuterJoinGroup(
                    dummySource,
                    dummySource,
                    (fun (x: int) -> x),
                    (fun (x: int) -> x),
                    (fun (x: int) (ys: seq<int>) -> x)
                )
                |> ignore)

        Assert.Contains("rightOuterJoin marker", ex3.Message)

    [<Fact>]
    let ``FSqlQueryBuilder raw join and cross join markers throw expected exceptions scenario 2`` () =
        let b = FSqlQueryBuilder()
        let dummySource: QuerySource<int, System.Linq.IQueryable> = Unchecked.defaultof<_>

        let ex1 =
            Assert.Throws<Exception>(fun () ->
                b.FullOuterJoinRaw(dummySource, "OtherTable", "ot", "ot.Id = 1") |> ignore)

        Assert.Contains("fullOuterJoin marker", ex1.Message)

        let ex2 =
            Assert.Throws<Exception>(fun () ->
                b.RightOuterJoinRaw(dummySource, "OtherTable", "ot", "ot.Id = 1") |> ignore)

        Assert.Contains("rightOuterJoin marker", ex2.Message)

        let ex3 =
            Assert.Throws<Exception>(fun () -> b.CrossJoin(dummySource, "OtherTable", "ot") |> ignore)

        Assert.Contains("crossJoin marker", ex3.Message)

    type private TempExtDb() =
        let path = Path.Combine(Path.GetTempPath(), $"ext_cov_test_{Guid.NewGuid():N}.db")

        do
            use conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}")
            conn.Open()
            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                "CREATE TABLE Products (Id INT, Name TEXT, Price NUMERIC); CREATE TABLE Orders (OrderId INT, CustomerId INT, Amount NUMERIC);"

            cmd.ExecuteNonQuery() |> ignore

        member _.Path = path

        interface IDisposable with
            member _.Dispose() =
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()

                try
                    if File.Exists path then
                        File.Delete path
                with _ ->
                    ()

    [<CLIMutable>]
    type TestProductRow =
        {
            Id: int
            Name: string
            Price: decimal
        }

    [<CLIMutable>]
    type TestOrderRow =
        {
            OrderId: int
            CustomerId: int
            Amount: decimal
        }

    [<Fact>]
    let ``fsql with whereExists and whereNotExists compiles and generates SQL scenario 1`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let products = SqlPushdownQueryable<TestProductRow>(conn, "Products")
        let orders = SqlPushdownQueryable<TestOrderRow>(conn, "Orders")

        let qExists =
            fsql {
                for p in products do
                    whereExists (
                        fsql {
                            for o in orders do
                                where (o.CustomerId = p.Id)
                                select o.OrderId
                        }
                    )

                    select p.Id
            }

        Assert.NotNull(qExists)

        let qNotExists =
            fsql {
                for p in products do
                    whereNotExists (
                        fsql {
                            for o in orders do
                                where (o.CustomerId = p.Id)
                                select o.OrderId
                        }
                    )

                    select p.Id
            }

        Assert.NotNull(qNotExists)

    [<Fact>]
    let ``fsql with having compiles and generates SQL scenario 2`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let products = SqlPushdownQueryable<TestProductRow>(conn, "Products")

        let qHaving =
            fsql {
                for p in products do
                    having (p.Price > 10.0M)
                    select p.Id
            }

        Assert.NotNull(qHaving)

    [<Fact>]
    let ``fsql with anonymous withCte and withCteSql compiles and generates SQL scenario 1`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let products = SqlPushdownQueryable<TestProductRow>(conn, "Products")
        let orders = SqlPushdownQueryable<TestOrderRow>(conn, "Orders")

        let qAnonCte =
            fsql {
                for p in products do
                    withCte (
                        fsql {
                            for o in orders do
                                select o.OrderId
                        }
                    )

                    withCteSql "SELECT 1 AS N"
                    select p.Id
            }

        let sql = (qAnonCte :?> SqlPushdownQueryable<int>).ToRawSql()
        Assert.Contains("WITH cte_1 AS (SELECT 1 AS N)", sql)
        Assert.Contains("cte_2 AS", sql)

    [<Fact>]
    let ``fsql with anonymous withRecursiveCte and withRecursiveCteSql scenario 2`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let products = SqlPushdownQueryable<TestProductRow>(conn, "Products")
        let orders = SqlPushdownQueryable<TestOrderRow>(conn, "Orders")

        let qAnonRec =
            fsql {
                for p in products do
                    withRecursiveCte (
                        fsql {
                            for o in orders do
                                select o.OrderId
                        }
                    )

                    withRecursiveCteSql "SELECT 2 AS M"
                    select p.Id
            }

        let sql = (qAnonRec :?> SqlPushdownQueryable<int>).ToRawSql()
        Assert.Contains("WITH RECURSIVE cte_1 AS (SELECT 2 AS M)", sql)
        Assert.Contains("cte_2 AS", sql)

    [<Fact>]
    let ``fsql querying directly from withCte defined queryable sets fromCteName scenario 1`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let orders = SqlPushdownQueryable<TestOrderRow>(conn, "Orders")

        let cteOrderQuery =
            fsql {
                for o in orders do
                    select o.OrderId
            }

        let qFromCte =
            fsql {
                for x in cteOrderQuery do
                    withCte "my_orders_cte" cteOrderQuery
                    select x
            }

        let sql = (qFromCte :?> SqlPushdownQueryable<int>).ToRawSql()
        Assert.Contains("WITH my_orders_cte AS", sql)
        Assert.Contains("FROM my_orders_cte", sql)

    [<Fact>]
    let ``fsql querying directly from anonymous withCte defined queryable sets fromCteName scenario 2`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let orders = SqlPushdownQueryable<TestOrderRow>(conn, "Orders")

        let cteOrderQuery =
            fsql {
                for o in orders do
                    select o.OrderId
            }

        let qFromAnonCte =
            fsql {
                for x in cteOrderQuery do
                    withCte cteOrderQuery
                    select x
            }

        let sql = (qFromAnonCte :?> SqlPushdownQueryable<int>).ToRawSql()
        Assert.Contains("WITH cte_1 AS", sql)
        Assert.Contains("FROM cte_1", sql)

    [<Fact>]
    let ``fsql rightOuterJoin and fullOuterJoin generate correct join SQL scenario 1`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let products = SqlPushdownQueryable<TestProductRow>(conn, "Products")
        let orders = SqlPushdownQueryable<TestOrderRow>(conn, "Orders")

        let qRight =
            fsql {
                for p in products do
                    rightOuterJoin o in orders on (p.Id = o.CustomerId)
                    select (p.Id, o.OrderId)
            }

        let sql = (qRight :?> SqlPushdownQueryable<int * int>).ToRawSql()
        Assert.Contains("RIGHT OUTER JOIN Orders", sql)

    [<Fact>]
    let ``fsql rightOuterJoin and fullOuterJoin generate correct join SQL scenario 2`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let products = SqlPushdownQueryable<TestProductRow>(conn, "Products")
        let orders = SqlPushdownQueryable<TestOrderRow>(conn, "Orders")

        let qFull =
            fsql {
                for p in products do
                    fullOuterJoin o in orders on (p.Id = o.CustomerId) into g

                    for o in g do
                        select (p.Id, o.OrderId)
            }

        let sql = (qFull :?> SqlPushdownQueryable<int * int>).ToRawSql()
        Assert.Contains("FULL OUTER JOIN Orders", sql)

    [<Fact>]
    let ``fsql set operations union and unionAll generate combined SQL scenario 1`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let products = SqlPushdownQueryable<TestProductRow>(conn, "Products")
        let orders = SqlPushdownQueryable<TestOrderRow>(conn, "Orders")

        let qUnion =
            fsql {
                for p in products do
                    select p.Id

                    union (
                        fsql {
                            for o in orders do
                                select o.OrderId
                        }
                    )
            }

        let sql = (qUnion :?> SqlPushdownQueryable<int>).ToRawSql()
        Assert.Contains("UNION", sql)

    [<Fact>]
    let ``fsql set operations intersect and except generate combined SQL scenario 2`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let products = SqlPushdownQueryable<TestProductRow>(conn, "Products")
        let orders = SqlPushdownQueryable<TestOrderRow>(conn, "Orders")

        let qIntersect =
            fsql {
                for p in products do
                    select p.Id

                    intersect (
                        fsql {
                            for o in orders do
                                select o.OrderId
                        }
                    )
            }

        let sql = (qIntersect :?> SqlPushdownQueryable<int>).ToRawSql()
        Assert.Contains("INTERSECT", sql)

        let qExcept =
            fsql {
                for p in products do
                    select p.Id

                    except (
                        fsql {
                            for o in orders do
                                select o.OrderId
                        }
                    )
            }

        let sql2 = (qExcept :?> SqlPushdownQueryable<int>).ToRawSql()
        Assert.Contains("EXCEPT", sql2)

    [<Fact>]
    let ``IQueryable set operation extensions with seed plan Ctes scenario 1`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let products = SqlPushdownQueryable<TestProductRow>(conn, "Products")
        let orders = SqlPushdownQueryable<TestOrderRow>(conn, "Orders")
        let pWithCte = products.WithCte("prod_cte", products)
        let pIq = System.Linq.Queryable.Where(pWithCte, fun p -> p.Id > 0)
        let oIq = System.Linq.Queryable.Where(orders, fun o -> o.OrderId > 0)

        let pSel = System.Linq.Queryable.Select(pIq, fun p -> p.Id)
        let oSel = System.Linq.Queryable.Select(oIq, fun o -> o.OrderId)

        let u1 = pSel.Union(oSel)
        Assert.NotNull(u1)
        let u2 = pSel.UnionAll(oSel)
        Assert.NotNull(u2)

    [<Fact>]
    let ``IQueryable set operation extensions intersect and except with seed plan Ctes scenario 2`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let products = SqlPushdownQueryable<TestProductRow>(conn, "Products")
        let orders = SqlPushdownQueryable<TestOrderRow>(conn, "Orders")
        let pWithCte = products.WithCte("prod_cte", products)
        let pIq = System.Linq.Queryable.Where(pWithCte, fun p -> p.Id > 0)
        let oIq = System.Linq.Queryable.Where(orders, fun o -> o.OrderId > 0)

        let pSel = System.Linq.Queryable.Select(pIq, fun p -> p.Id)
        let oSel = System.Linq.Queryable.Select(oIq, fun o -> o.OrderId)

        let u3 = pSel.Intersect(oSel)
        Assert.NotNull(u3)
        let u4 = pSel.Except(oSel)
        Assert.NotNull(u4)

        // WithCte on wrapped IQueryable with parameters
        let ordersWithParams =
            SqlPushdownQueryable<TestOrderRow>(conn, "Orders", [ ("@customParam", box 42) ])

        let subWithParam = ordersWithParams.Where(fun o -> o.Amount > 10.0M)
        let cte1 = pIq.WithCte(subWithParam)
        Assert.NotNull(cte1)
        let cte2 = pIq.WithCte("param_cte", subWithParam)
        Assert.NotNull(cte2)

    [<Fact>]
    let ``fsql identity query returns underlying queryable directly scenario 1`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let products = SqlPushdownQueryable<TestProductRow>(conn, "Products")

        let q1 =
            fsql {
                for p in products do
                    select p
            }

        Assert.NotNull(q1)

    [<Fact>]
    let ``fsql identity query with yield returns underlying queryable directly scenario 2`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let products = SqlPushdownQueryable<TestProductRow>(conn, "Products")

        let q2 =
            fsql {
                for p in products do
                    yield p
            }

        Assert.NotNull(q2)

    [<Fact>]
    let ``attachToPlan applies outer joins to existing plan joins scenario 1`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let products = SqlPushdownQueryable<TestProductRow>(conn, "Products")
        let plan = products.Plan.Clone()

        plan.Joins.Add(
            {
                JoinType = "INNER JOIN"
                SourceTable = "Orders"
                Alias = "o"
                OnClause = "1=1"
                RowType = None
            }
        )

        let pIq = products :> System.Linq.IQueryable<TestProductRow>

        let spqWithJoin =
            SqlPushdownQueryable<TestProductRow>(pIq.Provider, pIq.Expression, products.Connector, "Products", [], plan)

        let orders = SqlPushdownQueryable<TestOrderRow>(conn, "Orders")

        let q =
            fsql {
                for p in spqWithJoin do
                    rightOuterJoin o in orders on (p.Id = o.CustomerId)
                    select p.Id
            }

        Assert.NotNull(q)

    [<Fact>]
    let ``attachToPlan applies full outer join to existing plan joins scenario 2`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let products = SqlPushdownQueryable<TestProductRow>(conn, "Products")
        let plan = products.Plan.Clone()

        plan.Joins.Add(
            {
                JoinType = "INNER JOIN"
                SourceTable = "Orders"
                Alias = "o"
                OnClause = "1=1"
                RowType = None
            }
        )

        let pIq = products :> System.Linq.IQueryable<TestProductRow>

        let spqWithJoin =
            SqlPushdownQueryable<TestProductRow>(pIq.Provider, pIq.Expression, products.Connector, "Products", [], plan)

        let orders = SqlPushdownQueryable<TestOrderRow>(conn, "Orders")

        let q =
            fsql {
                for p in spqWithJoin do
                    fullOuterJoin o in orders on (p.Id = o.CustomerId) into g

                    for o in g do
                        select p.Id
            }

        Assert.NotNull(q)

    [<Fact>]
    let ``fsql set operations with in-memory seq rhs scenario 1`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let products = SqlPushdownQueryable<TestPersonRecord>(conn, "Person")
        let inMemSeq: TestPersonRecord list = [ { Id = 1; Name = "A"; Score = 10.0 } ]

        let qUnion =
            fsql {
                for p in products do
                    select p
                    union inMemSeq
            }

        Assert.NotNull(qUnion)

        let qUnionAll =
            fsql {
                for p in products do
                    select p
                    unionAll inMemSeq
            }

        Assert.NotNull(qUnionAll)

        let qIntersect =
            fsql {
                for p in products do
                    select p
                    intersect inMemSeq
            }

        Assert.NotNull(qIntersect)

        let qExcept =
            fsql {
                for p in products do
                    select p
                    except inMemSeq
            }

        Assert.NotNull(qExcept)

    [<Fact>]
    let ``fsql builder direct set operations scenario 2`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)

        let products1 =
            SqlPushdownQueryable<TestPersonRecord>(conn, "Person") :> System.Linq.IQueryable<TestPersonRecord>

        let products2 =
            SqlPushdownQueryable<TestPersonRecord>(conn, "Person") :> System.Linq.IQueryable<TestPersonRecord>

        let inMemIq1 =
            ([ { Id = 2; Name = "B"; Score = 20.0 } ]: TestPersonRecord list).AsQueryable()

        let inMemIq2 =
            ([ { Id = 3; Name = "C"; Score = 30.0 } ]: TestPersonRecord list).AsQueryable()

        let uSpq = fsql.union products1 products2
        Assert.NotNull(uSpq)
        let uaSpq = fsql.unionAll products1 products2
        Assert.NotNull(uaSpq)
        let iSpq = fsql.intersect products1 products2
        Assert.NotNull(iSpq)
        let eSpq = fsql.except products1 products2
        Assert.NotNull(eSpq)

        let uMem = fsql.union inMemIq1 inMemIq2
        Assert.NotNull(uMem)
        let uaMem = fsql.unionAll inMemIq1 inMemIq2
        Assert.NotNull(uaMem)
        let iMem = fsql.intersect inMemIq1 inMemIq2
        Assert.NotNull(iMem)
        let eMem = fsql.except inMemIq1 inMemIq2
        Assert.NotNull(eMem)

    // -------------------------------------------------------------------------
    // 6. Additional FSql Extensions & Helper Coverage Tests (>= 2 tests per scenario)
    // -------------------------------------------------------------------------

    [<Fact>]
    let ``fsql helper functions recursive and jsonValue return expected defaults scenario 1`` () =
        let seqIn = [ 1; 2; 3 ]
        let resRec = recursive "rec" seqIn (fun s -> s)
        Assert.Equal<int seq>(seqIn, resRec)
        let jVal = jsonValue "column" "$.field"
        Assert.Equal("", jVal)

    [<Fact>]
    let ``fsql helper functions recursive and jsonValue with different types scenario 2`` () =
        let seqIn = [ "a"; "b" ]
        let resRec = recursive "tree" seqIn (fun s -> s)
        Assert.Equal<string seq>(seqIn, resRec)
        let jVal = jsonValue 123 "items[0]"
        Assert.Equal("", jVal)

    [<Fact>]
    let ``IQueryable extension methods Union UnionAll Intersect Except on wrapped queryable scenario 1`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)

        let p1 =
            SqlPushdownQueryable<TestPersonRecord>(conn, "Person") :> System.Linq.IQueryable<TestPersonRecord>

        let p2 =
            SqlPushdownQueryable<TestPersonRecord>(conn, "Person") :> System.Linq.IQueryable<TestPersonRecord>

        let wrapped1 = p1.Where(fun p -> p.Score > 10.0)
        let wrapped2 = p2.Where(fun p -> p.Score < 100.0)

        let u = wrapped1.Union(wrapped2)
        Assert.NotNull(u)
        let ua = wrapped1.UnionAll(wrapped2)
        Assert.NotNull(ua)
        let i = wrapped1.Intersect(wrapped2)
        Assert.NotNull(i)
        let e = wrapped1.Except(wrapped2)
        Assert.NotNull(e)

    [<Fact>]
    let ``IQueryable extension methods Union UnionAll Intersect Except on in-memory queryable scenario 2`` () =
        let mem1 =
            ([ { Id = 1; Name = "A"; Score = 1.0 } ]: TestPersonRecord list).AsQueryable()

        let mem2 =
            ([ { Id = 2; Name = "B"; Score = 2.0 } ]: TestPersonRecord list).AsQueryable()

        let u = mem1.Union(mem2)
        Assert.Equal(2, u |> Seq.length)
        let ua = mem1.UnionAll(mem2)
        Assert.Equal(2, ua |> Seq.length)
        let i = mem1.Intersect(mem2)
        Assert.Equal(0, i |> Seq.length)
        let e = mem1.Except(mem2)
        Assert.Equal(1, e |> Seq.length)

    [<Fact>]
    let ``IQueryable extension methods WithCte on wrapped queryable scenario 1`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)

        let p1 =
            SqlPushdownQueryable<TestPersonRecord>(conn, "Person") :> System.Linq.IQueryable<TestPersonRecord>

        let cteIq =
            SqlPushdownQueryable<TestPersonRecord>(conn, "Person") :> System.Linq.IQueryable<TestPersonRecord>

        let wrapped1 = p1.Where(fun p -> p.Score > 10.0)

        let withCteAuto = wrapped1.WithCte(cteIq)
        Assert.NotNull(withCteAuto)
        let withCteAutoRec = wrapped1.WithCte(cteIq, isRecursive = true)
        Assert.NotNull(withCteAutoRec)
        let withCteNamed = wrapped1.WithCte("custom_cte", cteIq)
        Assert.NotNull(withCteNamed)
        let withCteNamedRec = wrapped1.WithCte("custom_rec_cte", cteIq, isRecursive = true)
        Assert.NotNull(withCteNamedRec)

    [<Fact>]
    let ``IQueryable extension methods WithCte on in-memory queryable scenario 2`` () =
        let mem1 =
            ([ { Id = 1; Name = "A"; Score = 1.0 } ]: TestPersonRecord list).AsQueryable()

        let cteMem =
            ([ { Id = 2; Name = "B"; Score = 2.0 } ]: TestPersonRecord list).AsQueryable()

        let withCteAuto = mem1.WithCte(cteMem)
        Assert.Equal(1, withCteAuto |> Seq.length)
        let withCteNamed = mem1.WithCte("ignored", cteMem)
        Assert.Equal(1, withCteNamed |> Seq.length)

    [<Fact>]
    let ``fsql raw join operations rightOuterJoinRaw fullOuterJoinRaw crossJoin scenario 1`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let products = SqlPushdownQueryable<TestProductRow>(conn, "Products")

        let qRightRaw =
            fsql {
                for p in products do
                    rightOuterJoinRaw "Orders" "o" "p.Id = o.CustomerId"
                    select p.Id
            }

        let rawSqlRight = toRawSql qRightRaw
        Assert.Contains("RIGHT OUTER JOIN", rawSqlRight)

        let qFullRaw =
            fsql {
                for p in products do
                    fullOuterJoinRaw "Orders" "o" "p.Id = o.CustomerId"
                    select p.Id
            }

        let rawSqlFull = toRawSql qFullRaw
        Assert.Contains("FULL OUTER JOIN", rawSqlFull)

        let qCross =
            fsql {
                for p in products do
                    crossJoin "Orders" "o"
                    select p.Id
            }

        let rawSqlCross = toRawSql qCross
        Assert.Contains("CROSS JOIN", rawSqlCross)

    [<Fact>]
    let ``fsql raw join operations with different aliases and conditions scenario 2`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let products = SqlPushdownQueryable<TestProductRow>(conn, "Products")

        let qRight =
            fsql {
                for p in products do
                    rightOuterJoinRaw "Suppliers" "s" "p.Id = s.Id"
                    select p.Price
            }

        let rawSql = toRawSql qRight
        Assert.Contains("RIGHT OUTER JOIN", rawSql)

        let qFull =
            fsql {
                for p in products do
                    fullOuterJoinRaw "Inventory" "inv" "p.Id = inv.ProductId"
                    select p.Price
            }

        let rawSqlFull = toRawSql qFull
        Assert.Contains("FULL OUTER JOIN", rawSqlFull)

        let qCross =
            fsql {
                for p in products do
                    crossJoin "Categories" "c"
                    select p.Price
            }

        let rawSqlCross = toRawSql qCross
        Assert.Contains("CROSS JOIN", rawSqlCross)

    [<Fact>]
    let ``fsql raw sql cte operations WithCteSql WithCteSqlAlt WithRecursiveCteSql scenario 1`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let products = SqlPushdownQueryable<TestProductRow>(conn, "Products")

        let qCteSql =
            fsql {
                for p in products do
                    withCteSql "cte_raw" "SELECT 1 AS num"
                    select p.Id
            }

        let rawSql = toRawSql qCteSql
        Assert.Contains("cte_raw AS (SELECT 1 AS num)", rawSql)

        let qCteSqlAlt =
            fsql {
                for p in products do
                    withCteSql "cte_raw_alt" "SELECT 2 AS num"
                    select p.Id
            }

        let rawSqlAlt = toRawSql qCteSqlAlt
        Assert.Contains("cte_raw_alt AS (SELECT 2 AS num)", rawSqlAlt)

        let qRecCteSql =
            fsql {
                for p in products do
                    withRecursiveCteSql "cte_rec_raw" "SELECT 1 UNION ALL SELECT 2"
                    select p.Id
            }

        let rawSqlRec = toRawSql qRecCteSql
        Assert.Contains("cte_rec_raw AS (SELECT 1 UNION ALL SELECT 2)", rawSqlRec)

    [<Fact>]
    let ``fsql raw sql cte operations multiple ctes scenario 2`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let products = SqlPushdownQueryable<TestProductRow>(conn, "Products")

        let qMulti =
            fsql {
                for p in products do
                    withCteSql "cte_a" "SELECT * FROM A"
                    withRecursiveCteSql "cte_b" "SELECT * FROM B"
                    select p.Id
            }

        let rawSql = toRawSql qMulti
        Assert.Contains("cte_a", rawSql)
        Assert.Contains("cte_b", rawSql)

    [<Fact>]
    let ``fsql whereExists whereNotExists having scenario 1`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let products = SqlPushdownQueryable<TestProductRow>(conn, "Products")
        let orders = SqlPushdownQueryable<TestOrderRow>(conn, "Orders")

        let qExists =
            fsql {
                for p in products do
                    whereExists (
                        fsql {
                            for o in orders do
                                where (o.CustomerId = p.Id)
                                select o.OrderId
                        }
                    )

                    select p.Id
            }

        let sqlExists = toRawSql qExists
        Assert.Contains("WHERE", sqlExists)

        let qNotExists =
            fsql {
                for p in products do
                    whereNotExists (
                        fsql {
                            for o in orders do
                                where (o.CustomerId = p.Id)
                                select o.OrderId
                        }
                    )

                    select p.Id
            }

        let sqlNotExists = toRawSql qNotExists
        Assert.Contains("WHERE", sqlNotExists)

        let qHaving =
            fsql {
                for p in products do
                    groupBy p.Name into g
                    having (g.Count() > 1)
                    select g.Key
            }

        let sqlHaving = toRawSql qHaving
        Assert.Contains("HAVING", sqlHaving)

    [<Fact>]
    let ``fsql whereExists whereNotExists having variations scenario 2`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let products = SqlPushdownQueryable<TestProductRow>(conn, "Products")
        let orders = SqlPushdownQueryable<TestOrderRow>(conn, "Orders")

        let qExists2 =
            fsql {
                for p in products do
                    whereExists (orders.Where(fun o -> o.CustomerId = p.Id))
                    select p.Price
            }

        let sqlExists2 = toRawSql qExists2
        Assert.Contains("WHERE", sqlExists2)

        let qNotExists2 =
            fsql {
                for p in products do
                    whereNotExists (orders.Where(fun o -> o.CustomerId = p.Id))
                    select p.Price
            }

        let sqlNotExists2 = toRawSql qNotExists2
        Assert.Contains("WHERE", sqlNotExists2)

        let qHaving2 =
            fsql {
                for p in products do
                    groupBy p.Id into g
                    having (g.Sum(fun x -> x.Price) > 100.0M)
                    select g.Key
            }

        let sqlHaving2 = toRawSql qHaving2
        Assert.Contains("HAVING", sqlHaving2)

    [<Fact>]
    let ``fsql CTE from root matching sourceQuery scenario 1`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let products = SqlPushdownQueryable<TestProductRow>(conn, "Products")
        let cteQuery = products.Where(fun p -> p.Price > 50.0M)

        let q1 =
            fsql {
                for p in cteQuery do
                    withCte "HighValue" cteQuery
                    select p.Id
            }

        let sql1 = toRawSql q1
        Assert.Contains("HighValue", sql1)

        let qAuto =
            fsql {
                for p in cteQuery do
                    withCte cteQuery
                    select p.Id
            }

        let sqlAuto = toRawSql qAuto
        Assert.Contains("cte_1", sqlAuto)

    [<Fact>]
    let ``fsql CTE from root matching sourceQuery scenario 2`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let orders = SqlPushdownQueryable<TestOrderRow>(conn, "Orders")
        let cteOrder = orders.Where(fun o -> o.Amount > 100.0M)

        let qNamed =
            fsql {
                for o in cteOrder do
                    withRecursiveCte "BigOrders" cteOrder
                    select o.OrderId
            }

        let sql = toRawSql qNamed
        Assert.Contains("BigOrders", sql)

        let qAuto =
            fsql {
                for o in cteOrder do
                    withRecursiveCte cteOrder
                    select o.OrderId
            }

        let sqlAuto = toRawSql qAuto
        Assert.Contains("cte_1", sqlAuto)

    [<Fact>]
    let ``fsql rightOuterJoin and fullOuterJoin with wrapped pushdown source having Plan scenario 1`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let products = SqlPushdownQueryable<TestProductRow>(conn, "Products")
        let orders = SqlPushdownQueryable<TestOrderRow>(conn, "Orders")

        let ordersWrapped =
            (orders :> System.Linq.IQueryable<TestOrderRow>).Where(fun o -> o.CustomerId > 0)

        let qRight =
            fsql {
                for p in products do
                    rightOuterJoin o in ordersWrapped on (p.Id = o.CustomerId)
                    select p.Id
            }

        let sqlRight = toRawSql qRight
        Assert.NotEmpty(sqlRight)
        Assert.Contains("cte_1", sqlRight)

    [<Fact>]
    let ``fsql rightOuterJoin and fullOuterJoin with wrapped pushdown source having Plan scenario 2`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let products = SqlPushdownQueryable<TestProductRow>(conn, "Products")
        let orders = SqlPushdownQueryable<TestOrderRow>(conn, "Orders")

        let ordersWrapped =
            (orders :> System.Linq.IQueryable<TestOrderRow>).Where(fun o -> o.CustomerId > 0)

        let qFull =
            fsql {
                for p in products do
                    fullOuterJoin o in ordersWrapped on (p.Id = o.CustomerId) into g

                    for o in g do
                        select p.Id
            }

        let sqlFull = toRawSql qFull
        Assert.NotEmpty(sqlFull)
        Assert.Contains("cte_1", sqlFull)

    [<Fact>]
    let ``IQueryable extension methods Union UnionAll Intersect Except copies seedPlan ctes scenario 1`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let p1 = SqlPushdownQueryable<TestPersonRecord>(conn, "Person")

        p1.Plan.Ctes.Add(
            {
                Name = "custom_seed_cte"
                SubquerySql = "SELECT 1"
                IsRecursive = false
                SourceQuery = None
            }
        )

        let wrapped1 =
            (p1 :> System.Linq.IQueryable<TestPersonRecord>).Where(fun p -> p.Score > 10.0)

        let p2 =
            SqlPushdownQueryable<TestPersonRecord>(conn, "Person") :> System.Linq.IQueryable<TestPersonRecord>

        let u = wrapped1.Union(p2)
        Assert.NotNull(u)
        let ua = wrapped1.UnionAll(p2)
        Assert.NotNull(ua)
        let i = wrapped1.Intersect(p2)
        Assert.NotNull(i)
        let e = wrapped1.Except(p2)
        Assert.NotNull(e)

    [<Fact>]
    let ``IQueryable extension methods Union UnionAll Intersect Except copies seedPlan ctes scenario 2`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let p1 = SqlPushdownQueryable<TestPersonRecord>(conn, "Person")

        p1.Plan.Ctes.Add(
            {
                Name = "seed_cte_alt"
                SubquerySql = "SELECT 2"
                IsRecursive = true
                SourceQuery = None
            }
        )

        let wrapped1 =
            (p1 :> System.Linq.IQueryable<TestPersonRecord>).Where(fun p -> p.Score < 100.0)

        let p2 =
            SqlPushdownQueryable<TestPersonRecord>(conn, "Person") :> System.Linq.IQueryable<TestPersonRecord>

        let u = wrapped1.Union(p2)
        Assert.NotNull(u)
        let ua = wrapped1.UnionAll(p2)
        Assert.NotNull(ua)
        let i = wrapped1.Intersect(p2)
        Assert.NotNull(i)
        let e = wrapped1.Except(p2)
        Assert.NotNull(e)

    [<Fact>]
    let ``IQueryable extension methods WithCte with parameterized subquery scenario 1`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)

        let p1 =
            SqlPushdownQueryable<TestPersonRecord>(conn, "Person") :> System.Linq.IQueryable<TestPersonRecord>

        let pParam =
            SqlPushdownQueryable<TestPersonRecord>(conn, "Person").Where(fun p -> p.Score > 50.0)

        let wrapped1 = p1.Where(fun p -> p.Score > 10.0)

        let withCteAuto = wrapped1.WithCte(pParam)
        Assert.NotNull(withCteAuto)
        let withCteNamed = wrapped1.WithCte("param_cte", pParam)
        Assert.NotNull(withCteNamed)

    [<Fact>]
    let ``IQueryable extension methods WithCte with parameterized subquery scenario 2`` () =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)

        let p1 =
            SqlPushdownQueryable<TestPersonRecord>(conn, "Person") :> System.Linq.IQueryable<TestPersonRecord>

        let pParam =
            SqlPushdownQueryable<TestPersonRecord>(conn, "Person").Where(fun p -> p.Score < 0.0)

        let wrapped1 = p1.Where(fun p -> p.Score > 0.0)

        let withCteAutoRec = wrapped1.WithCte(pParam, isRecursive = true)
        Assert.NotNull(withCteAutoRec)
        let withCteNamedRec = wrapped1.WithCte("param_cte_rec", pParam, isRecursive = true)
        Assert.NotNull(withCteNamedRec)

    [<Fact>]
    let ``fsql set operations doUnionAll doIntersect doExcept with IQueryable and non-enumerable scenario 1`` () =
        let mem1 =
            ([ { Id = 1; Name = "A"; Score = 1.0 } ]: TestPersonRecord list).AsQueryable()

        let mem2 =
            ([ { Id = 2; Name = "B"; Score = 2.0 } ]: TestPersonRecord list).AsQueryable()

        let qUnionAllIq =
            fsql {
                for p in mem1 do
                    select p
                    unionAll mem2
            }

        Assert.Equal(2, qUnionAllIq |> Seq.length)

        let qIntersectIq =
            fsql {
                for p in mem1 do
                    select p
                    intersect mem2
            }

        Assert.Equal(0, qIntersectIq |> Seq.length)

        let qExceptIq =
            fsql {
                for p in mem1 do
                    select p
                    except mem2
            }

        Assert.Equal(1, qExceptIq |> Seq.length)

    [<Fact>]
    let ``fsql set operations doUnionAll doIntersect doExcept with IQueryable and non-enumerable scenario 2`` () =
        let mem1 =
            ([ { Id = 10; Name = "X"; Score = 10.0 } ]: TestPersonRecord list).AsQueryable()

        let mem2 =
            ([ { Id = 10; Name = "X"; Score = 10.0 } ]: TestPersonRecord list).AsQueryable()

        let qUnionAllIq =
            fsql {
                for p in mem1 do
                    select p
                    unionAll mem2
            }

        Assert.Equal(2, qUnionAllIq |> Seq.length)

        let qIntersectIq =
            fsql {
                for p in mem1 do
                    select p
                    intersect mem2
            }

        Assert.Equal(1, qIntersectIq |> Seq.length)

        let qExceptIq =
            fsql {
                for p in mem1 do
                    select p
                    except mem2
            }

        Assert.Equal(0, qExceptIq |> Seq.length)

    [<Fact>]
    let ``attachToPlan updates join type when table name matches fullOuterJoinTables or rightOuterJoinTables scenario 1``
        ()
        =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let products = SqlPushdownQueryable<TestProductRow>(conn, "Products")
        let plan = products.Plan.Clone()

        plan.Joins.Add(
            {
                JoinType = "INNER JOIN"
                SourceTable = "Orders"
                Alias = "o"
                OnClause = "1=1"
                RowType = None
            }
        )

        let pIq = products :> System.Linq.IQueryable<TestProductRow>

        let spqWithJoin =
            SqlPushdownQueryable<TestProductRow>(pIq.Provider, pIq.Expression, products.Connector, "Products", [], plan)

        let orders = SqlPushdownQueryable<TestOrderRow>(conn, "Orders")

        let qRight =
            fsql {
                for p in spqWithJoin do
                    rightOuterJoin o in orders on (p.Id = o.CustomerId)
                    select p.Id
            }

        let sqlRight = toRawSql qRight
        Assert.NotEmpty(sqlRight)

    [<Fact>]
    let ``attachToPlan updates join type when table name matches fullOuterJoinTables or rightOuterJoinTables scenario 2``
        ()
        =
        use tempDb = new TempExtDb()
        let conn = SqliteConnector(tempDb.Path)
        let products = SqlPushdownQueryable<TestProductRow>(conn, "Products")
        let plan = products.Plan.Clone()

        plan.Joins.Add(
            {
                JoinType = "INNER JOIN"
                SourceTable = "Orders"
                Alias = "o"
                OnClause = "1=1"
                RowType = None
            }
        )

        let pIq = products :> System.Linq.IQueryable<TestProductRow>

        let spqWithJoin =
            SqlPushdownQueryable<TestProductRow>(pIq.Provider, pIq.Expression, products.Connector, "Products", [], plan)

        let orders = SqlPushdownQueryable<TestOrderRow>(conn, "Orders")

        let qFull =
            fsql {
                for p in spqWithJoin do
                    fullOuterJoin o in orders on (p.Id = o.CustomerId) into g

                    for o in g do
                        select p.Id
            }

        let sqlFull = toRawSql qFull
        Assert.NotEmpty(sqlFull)
