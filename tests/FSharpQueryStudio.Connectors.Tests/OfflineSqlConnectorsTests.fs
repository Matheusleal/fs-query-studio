module FSharpQueryStudio.Connectors.Tests.OfflineSqlConnectorsTests

open System
open System.Data
open System.Data.Common
open System.Threading
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors
open Microsoft.Data.SqlClient
open Npgsql
open MySqlConnector
open Oracle.ManagedDataAccess.Client

// Fast-failing connection strings pointing to an unmapped local port with short 1-second timeout.
let private fastFailSqlServerConn =
    "Server=127.0.0.1,59999;Database=testdb;Connection Timeout=1;Encrypt=False;"

let private fastFailPostgreSqlConn =
    "Host=127.0.0.1;Port=59999;Database=testdb;Timeout=1;"

let private fastFailMySqlConn =
    "Server=127.0.0.1;Port=59999;Database=testdb;Connection Timeout=1;"

let private fastFailMariaDbConn =
    "Server=127.0.0.1;Port=59999;Database=testdb;Connection Timeout=1;"

let private fastFailOracleConn =
    "Data Source=127.0.0.1:59999/testdb;Connection Timeout=1;"

// -----------------------------------------------------------------------------
// 1. Instantiation and Metadata Tests
// -----------------------------------------------------------------------------

[<Fact>]
let ``SqlServerConnector initializes with correct Kind and ConnectionString`` () =
    let connStr = "Server=my-sql-server;Database=mydb;User Id=sa;Password=secret;"
    let connector = SqlServerConnector(connStr)
    let iconn = connector :> IConnector
    let iqueryable = connector :> IQueryableConnector

    Assert.Equal(ConnectorKind.SqlServer, iconn.Kind)
    Assert.Equal("SqlServer", iconn.Kind.Id)
    Assert.Equal(connStr, connector.ConnectionString)
    Assert.Equal(connStr, iqueryable.ConnectionString)

[<Fact>]
let ``PostgreSqlConnector initializes with correct Kind and ConnectionString`` () =
    let connStr = "Host=my-pg-server;Database=mydb;Username=postgres;Password=secret;"
    let connector = PostgreSqlConnector(connStr)
    let iconn = connector :> IConnector
    let iqueryable = connector :> IQueryableConnector

    Assert.Equal(ConnectorKind.PostgreSql, iconn.Kind)
    Assert.Equal("PostgreSql", iconn.Kind.Id)
    Assert.Equal(connStr, connector.ConnectionString)
    Assert.Equal(connStr, iqueryable.ConnectionString)

[<Fact>]
let ``MySqlConnector initializes with correct Kind and ConnectionString`` () =
    let connStr = "Server=my-mysql-server;Database=mydb;Uid=root;Pwd=secret;"
    let connector = MySqlConnector(connStr)
    let iconn = connector :> IConnector
    let iqueryable = connector :> IQueryableConnector

    Assert.Equal(ConnectorKind.MySql, iconn.Kind)
    Assert.Equal("MySql", iconn.Kind.Id)
    Assert.Equal(connStr, connector.ConnectionString)
    Assert.Equal(connStr, iqueryable.ConnectionString)

[<Fact>]
let ``MariaDbConnector initializes with correct Kind and ConnectionString`` () =
    let connStr = "Server=my-mariadb-server;Database=mydb;Uid=root;Pwd=secret;"
    let connector = MariaDbConnector(connStr)
    let iconn = connector :> IConnector
    let iqueryable = connector :> IQueryableConnector

    Assert.Equal(ConnectorKind.MariaDb, iconn.Kind)
    Assert.Equal("MariaDb", iconn.Kind.Id)
    Assert.Equal(connStr, connector.ConnectionString)
    Assert.Equal(connStr, iqueryable.ConnectionString)

[<Fact>]
let ``OracleConnector initializes with correct Kind and ConnectionString`` () =
    let connStr = "Data Source=my-oracle-server:1521/XE;User Id=system;Password=secret;"
    let connector = OracleConnector(connStr)
    let iconn = connector :> IConnector
    let iqueryable = connector :> IQueryableConnector

    Assert.Equal(ConnectorKind.Oracle, iconn.Kind)
    Assert.Equal("Oracle", iconn.Kind.Id)
    Assert.Equal(connStr, connector.ConnectionString)
    Assert.Equal(connStr, iqueryable.ConnectionString)

[<Fact>]
let ``Sql connectors share the Sql ConnectorCategory definition`` () =
    Assert.Equal("sql", ConnectorCategory.Sql.Identifier)
    Assert.Equal("Db", ConnectorCategory.Sql.ModuleSuffix)
    Assert.Equal("FSharpQueryStudio.Connectors.Connections.SqlCategory", ConnectorCategory.Sql.CategoryTypeName)

[<Fact>]
let ``MySqlConnector NormalizeConnectionString replaces localhost with 127.0.0.1 and preserves others`` () =
    let norm =
        MySqlConnector.NormalizeConnectionString
            "Server=localhost;Port=3306;Database=mydb;User ID=root;Password=secret;"

    Assert.Contains("Server=127.0.0.1", norm)

    let other =
        MySqlConnector.NormalizeConnectionString
            "Server=192.168.1.100;Port=3306;Database=mydb;User ID=root;Password=secret;"

    Assert.Contains("Server=192.168.1.100", other)

[<Fact>]
let ``MySqlConnector NormalizeConnectionString handles case variations, malformed and empty strings safely`` () =
    let upper =
        MySqlConnector.NormalizeConnectionString "Server=LOCALHOST;Port=3306;Database=mydb;"

    Assert.Contains("Server=127.0.0.1", upper)

    let mixed =
        MySqlConnector.NormalizeConnectionString "server=LocalHost;port=3306;database=mydb;"

    Assert.Contains("Server=127.0.0.1", mixed)
    let malformed = MySqlConnector.NormalizeConnectionString "not a valid conn string;;"
    Assert.Equal("not a valid conn string;;", malformed)
    let empty = MySqlConnector.NormalizeConnectionString ""
    Assert.Equal("", empty)

[<Fact>]
let ``MariaDbConnector NormalizeConnectionString replaces localhost with 127.0.0.1 and preserves others`` () =
    let norm =
        MariaDbConnector.NormalizeConnectionString
            "Server=localhost;Port=3307;Database=mydb;User ID=root;Password=secret;"

    Assert.Contains("Server=127.0.0.1", norm)

    let other =
        MariaDbConnector.NormalizeConnectionString
            "Server=remote.db;Port=3307;Database=mydb;User ID=root;Password=secret;"

    Assert.Contains("Server=remote.db", other)

[<Fact>]
let ``MariaDbConnector NormalizeConnectionString handles case variations, malformed and empty strings safely`` () =
    let upper =
        MariaDbConnector.NormalizeConnectionString "Server=LOCALHOST;Port=3307;Database=mydb;"

    Assert.Contains("Server=127.0.0.1", upper)

    let mixed =
        MariaDbConnector.NormalizeConnectionString "server=LocalHost;port=3307;database=mydb;"

    Assert.Contains("Server=127.0.0.1", mixed)

    let malformed =
        MariaDbConnector.NormalizeConnectionString "invalid connection string ;==;"

    Assert.Equal("invalid connection string ;==;", malformed)
    let empty = MariaDbConnector.NormalizeConnectionString ""
    Assert.Equal("", empty)

// -----------------------------------------------------------------------------
// 2. Safe Connection Failure Handling (TestConnectionAsync)
// -----------------------------------------------------------------------------

[<Fact>]
let ``SqlServerConnector TestConnectionAsync returns false when host is unreachable`` () =
    task {
        let connector = SqlServerConnector(fastFailSqlServerConn) :> IConnector
        let! success = connector.TestConnectionAsync(CancellationToken.None)
        Assert.False(success)
    }

[<Fact>]
let ``SqlServerConnector TestConnectionAsync returns false when cancellation token is pre-cancelled`` () =
    task {
        let connector = SqlServerConnector(fastFailSqlServerConn) :> IConnector
        use cts = new CancellationTokenSource()
        cts.Cancel()
        let! success = connector.TestConnectionAsync(cts.Token)
        Assert.False(success)
    }

[<Fact>]
let ``PostgreSqlConnector TestConnectionAsync returns false when host is unreachable`` () =
    task {
        let connector = PostgreSqlConnector(fastFailPostgreSqlConn) :> IConnector
        let! success = connector.TestConnectionAsync(CancellationToken.None)
        Assert.False(success)
    }

[<Fact>]
let ``PostgreSqlConnector TestConnectionAsync returns false when cancellation token is pre-cancelled`` () =
    task {
        let connector = PostgreSqlConnector(fastFailPostgreSqlConn) :> IConnector
        use cts = new CancellationTokenSource()
        cts.Cancel()
        let! success = connector.TestConnectionAsync(cts.Token)
        Assert.False(success)
    }

[<Fact>]
let ``MySqlConnector TestConnectionAsync returns false when host is unreachable`` () =
    task {
        let connector = MySqlConnector(fastFailMySqlConn) :> IConnector
        let! success = connector.TestConnectionAsync(CancellationToken.None)
        Assert.False(success)
    }

[<Fact>]
let ``MySqlConnector TestConnectionAsync returns false when cancellation token is pre-cancelled`` () =
    task {
        let connector = MySqlConnector(fastFailMySqlConn) :> IConnector
        use cts = new CancellationTokenSource()
        cts.Cancel()
        let! success = connector.TestConnectionAsync(cts.Token)
        Assert.False(success)
    }

[<Fact>]
let ``MariaDbConnector TestConnectionAsync returns false when host is unreachable`` () =
    task {
        let connector = MariaDbConnector(fastFailMariaDbConn) :> IConnector
        let! success = connector.TestConnectionAsync(CancellationToken.None)
        Assert.False(success)
    }

[<Fact>]
let ``MariaDbConnector TestConnectionAsync returns false when cancellation token is pre-cancelled`` () =
    task {
        let connector = MariaDbConnector(fastFailMariaDbConn) :> IConnector
        use cts = new CancellationTokenSource()
        cts.Cancel()
        let! success = connector.TestConnectionAsync(cts.Token)
        Assert.False(success)
    }

[<Fact>]
let ``OracleConnector TestConnectionAsync returns false when host is unreachable`` () =
    task {
        let connector = OracleConnector(fastFailOracleConn) :> IConnector
        let! success = connector.TestConnectionAsync(CancellationToken.None)
        Assert.False(success)
    }

[<Fact>]
let ``OracleConnector TestConnectionAsync returns false when cancellation token is pre-cancelled`` () =
    task {
        let connector = OracleConnector(fastFailOracleConn) :> IConnector
        use cts = new CancellationTokenSource()
        cts.Cancel()
        let! success = connector.TestConnectionAsync(cts.Token)
        Assert.False(success)
    }

// -----------------------------------------------------------------------------
// 3. Exception Handling in ExecuteQuery, DiscoverSchemaAsync, and Streaming
// -----------------------------------------------------------------------------

[<Fact>]
let ``SqlServerConnector ExecuteQuery throws SqlException when connection fails`` () =
    let connector = SqlServerConnector(fastFailSqlServerConn)

    let ex =
        Assert.Throws<SqlException>(fun () -> connector.ExecuteQuery "SELECT 1" [] |> ignore)

    Assert.NotNull(ex)
    Assert.NotEmpty(ex.Message)

[<Fact>]
let ``SqlServerConnector DiscoverSchemaAsync throws SqlException when connection fails`` () =
    task {
        let connector = SqlServerConnector(fastFailSqlServerConn) :> IConnector

        let! ex =
            Assert.ThrowsAsync<SqlException>(fun () -> connector.DiscoverSchemaAsync(CancellationToken.None) :> Task)

        Assert.NotNull(ex)
    }

[<Fact>]
let ``SqlServerConnector ExecuteReaderStream throws SqlException upon enumeration when connection fails`` () =
    let connector = SqlServerConnector(fastFailSqlServerConn)
    let stream = connector.ExecuteReaderStream "SELECT 1" [] (fun r -> r.GetInt32(0))
    let ex = Assert.Throws<SqlException>(fun () -> stream |> Seq.toList |> ignore)
    Assert.NotNull(ex)

[<Fact>]
let ``PostgreSqlConnector ExecuteQuery throws NpgsqlException when connection fails`` () =
    let connector = PostgreSqlConnector(fastFailPostgreSqlConn)

    let ex =
        Assert.Throws<NpgsqlException>(fun () -> connector.ExecuteQuery "SELECT 1" [] |> ignore)

    Assert.NotNull(ex)
    Assert.NotEmpty(ex.Message)

[<Fact>]
let ``PostgreSqlConnector DiscoverSchemaAsync throws NpgsqlException when connection fails`` () =
    task {
        let connector = PostgreSqlConnector(fastFailPostgreSqlConn) :> IConnector

        let! ex =
            Assert.ThrowsAsync<NpgsqlException>(fun () -> connector.DiscoverSchemaAsync(CancellationToken.None) :> Task)

        Assert.NotNull(ex)
    }

[<Fact>]
let ``PostgreSqlConnector ExecuteReaderStream throws NpgsqlException upon enumeration when connection fails`` () =
    let connector = PostgreSqlConnector(fastFailPostgreSqlConn)
    let stream = connector.ExecuteReaderStream "SELECT 1" [] (fun r -> r.GetInt32(0))
    let ex = Assert.Throws<NpgsqlException>(fun () -> stream |> Seq.toList |> ignore)
    Assert.NotNull(ex)

[<Fact>]
let ``MySqlConnector ExecuteQuery throws MySqlException when connection fails`` () =
    let connector = MySqlConnector(fastFailMySqlConn)

    let ex =
        Assert.Throws<MySqlException>(fun () -> connector.ExecuteQuery "SELECT 1" [] |> ignore)

    Assert.NotNull(ex)
    Assert.NotEmpty(ex.Message)

[<Fact>]
let ``MySqlConnector DiscoverSchemaAsync throws MySqlException when connection fails`` () =
    task {
        let connector = MySqlConnector(fastFailMySqlConn) :> IConnector

        let! ex =
            Assert.ThrowsAsync<MySqlException>(fun () -> connector.DiscoverSchemaAsync(CancellationToken.None) :> Task)

        Assert.NotNull(ex)
    }

[<Fact>]
let ``MySqlConnector ExecuteReaderStream throws MySqlException upon enumeration when connection fails`` () =
    let connector = MySqlConnector(fastFailMySqlConn)
    let stream = connector.ExecuteReaderStream "SELECT 1" [] (fun r -> r.GetInt32(0))
    let ex = Assert.Throws<MySqlException>(fun () -> stream |> Seq.toList |> ignore)
    Assert.NotNull(ex)

[<Fact>]
let ``MariaDbConnector ExecuteQuery throws MySqlException when connection fails`` () =
    let connector = MariaDbConnector(fastFailMariaDbConn)

    let ex =
        Assert.Throws<MySqlException>(fun () -> connector.ExecuteQuery "SELECT 1" [] |> ignore)

    Assert.NotNull(ex)
    Assert.NotEmpty(ex.Message)

[<Fact>]
let ``MariaDbConnector DiscoverSchemaAsync throws MySqlException when connection fails`` () =
    task {
        let connector = MariaDbConnector(fastFailMariaDbConn) :> IConnector

        let! ex =
            Assert.ThrowsAsync<MySqlException>(fun () -> connector.DiscoverSchemaAsync(CancellationToken.None) :> Task)

        Assert.NotNull(ex)
    }

[<Fact>]
let ``MariaDbConnector ExecuteReaderStream throws MySqlException upon enumeration when connection fails`` () =
    let connector = MariaDbConnector(fastFailMariaDbConn)
    let stream = connector.ExecuteReaderStream "SELECT 1" [] (fun r -> r.GetInt32(0))
    let ex = Assert.Throws<MySqlException>(fun () -> stream |> Seq.toList |> ignore)
    Assert.NotNull(ex)

[<Fact>]
let ``OracleConnector ExecuteQuery throws OracleException when connection fails`` () =
    let connector = OracleConnector(fastFailOracleConn)

    let ex =
        Assert.Throws<OracleException>(fun () -> connector.ExecuteQuery "SELECT 1 FROM DUAL" [] |> ignore)

    Assert.NotNull(ex)
    Assert.NotEmpty(ex.Message)

[<Fact>]
let ``OracleConnector DiscoverSchemaAsync throws OracleException when connection fails`` () =
    task {
        let connector = OracleConnector(fastFailOracleConn) :> IConnector

        let! ex =
            Assert.ThrowsAsync<OracleException>(fun () -> connector.DiscoverSchemaAsync(CancellationToken.None) :> Task)

        Assert.NotNull(ex)
    }

[<Fact>]
let ``OracleConnector ExecuteReaderStream throws OracleException upon enumeration when connection fails`` () =
    let connector = OracleConnector(fastFailOracleConn)

    let stream =
        connector.ExecuteReaderStream "SELECT 1 FROM DUAL" [] (fun r -> r.GetInt32(0))

    let ex = Assert.Throws<OracleException>(fun () -> stream |> Seq.toList |> ignore)
    Assert.NotNull(ex)

// -----------------------------------------------------------------------------
// 4. Parameter Handling and SQL Formatting Tests
// -----------------------------------------------------------------------------

[<Fact>]
let ``SqlServerConnector BindParameters binds typed values and maps nulls to DBNull`` () =
    use command = new SqlCommand()
    let now = DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc)
    let guid = Guid.NewGuid()

    SqlServerConnector.BindParameters(
        command,
        [
            "@id", box 42
            "@name", box "Alice"
            "@price", box 19.99M
            "@active", box true
            "@created", box now
            "@token", box guid
            "@nullVal", null
            "@dbNullVal", box DBNull.Value
        ]
    )

    Assert.Equal(8, command.Parameters.Count)
    Assert.Equal(box 42, command.Parameters.["@id"].Value)
    Assert.Equal(box "Alice", command.Parameters.["@name"].Value)
    Assert.Equal(box 19.99M, command.Parameters.["@price"].Value)
    Assert.Equal(box true, command.Parameters.["@active"].Value)
    Assert.Equal(box now, command.Parameters.["@created"].Value)
    Assert.Equal(box guid, command.Parameters.["@token"].Value)
    Assert.Equal(box DBNull.Value, command.Parameters.["@nullVal"].Value)
    Assert.Equal(box DBNull.Value, command.Parameters.["@dbNullVal"].Value)

[<Fact>]
let ``PostgreSqlConnector BindParameters binds typed values and maps nulls to DBNull`` () =
    use command = new NpgsqlCommand()
    let now = DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc)

    PostgreSqlConnector.BindParameters(
        command,
        [
            "id", box 100L
            "name", box "Postgres"
            "score", box 98.5
            "timestamp", box now
            "nullVal", null
            "dbNullVal", box DBNull.Value
        ]
    )

    Assert.Equal(6, command.Parameters.Count)
    Assert.Equal(box 100L, command.Parameters.["id"].Value)
    Assert.Equal(box "Postgres", command.Parameters.["name"].Value)
    Assert.Equal(box 98.5, command.Parameters.["score"].Value)
    Assert.Equal(box now, command.Parameters.["timestamp"].Value)
    Assert.Equal(box DBNull.Value, command.Parameters.["nullVal"].Value)
    Assert.Equal(box DBNull.Value, command.Parameters.["dbNullVal"].Value)

[<Fact>]
let ``MySqlConnector BindParameters ensures at prefix, binds typed values, and maps nulls to DBNull`` () =
    use command = new MySqlCommand()

    MySqlConnector.BindParameters(
        command,
        [
            "unprefixed", box 10
            "@prefixed", box "MySQL"
            "rate", box 3.14f
            "nullVal", null
            "@dbNullVal", box DBNull.Value
        ]
    )

    Assert.Equal(5, command.Parameters.Count)
    Assert.Equal(box 10, command.Parameters.["@unprefixed"].Value)
    Assert.Equal(box "MySQL", command.Parameters.["@prefixed"].Value)
    Assert.Equal(box 3.14f, command.Parameters.["@rate"].Value)
    Assert.Equal(box DBNull.Value, command.Parameters.["@nullVal"].Value)
    Assert.Equal(box DBNull.Value, command.Parameters.["@dbNullVal"].Value)

[<Fact>]
let ``MariaDbConnector BindParameters ensures at prefix, binds typed values, and maps nulls to DBNull`` () =
    use command = new MySqlCommand()

    MariaDbConnector.BindParameters(
        command,
        [
            "unprefixed", box 20
            "@prefixed", box "MariaDB"
            "rate", box 2.718
            "nullVal", null
            "@dbNullVal", box DBNull.Value
        ]
    )

    Assert.Equal(5, command.Parameters.Count)
    Assert.Equal(box 20, command.Parameters.["@unprefixed"].Value)
    Assert.Equal(box "MariaDB", command.Parameters.["@prefixed"].Value)
    Assert.Equal(box 2.718, command.Parameters.["@rate"].Value)
    Assert.Equal(box DBNull.Value, command.Parameters.["@nullVal"].Value)
    Assert.Equal(box DBNull.Value, command.Parameters.["@dbNullVal"].Value)

[<Fact>]
let ``OracleConnector BindParameters sets BindByName, trims at-sign and colon prefixes, and maps nulls to DBNull`` () =
    use command = new OracleCommand()

    OracleConnector.BindParameters(
        command,
        [
            "@atPrefix", box 1
            ":colonPrefix", box 2
            "noPrefix", box "Oracle"
            "@nullVal", null
            ":dbNullVal", box DBNull.Value
        ]
    )

    Assert.True(command.BindByName)
    Assert.Equal(5, command.Parameters.Count)
    Assert.Equal(box 1, command.Parameters.["atPrefix"].Value)
    Assert.Equal(box 2, command.Parameters.["colonPrefix"].Value)
    Assert.Equal(box "Oracle", command.Parameters.["noPrefix"].Value)
    // ODP.NET OracleParameter may represent null/DBNull as null or DBNull.Value depending on driver internals
    let nullVal = command.Parameters.["nullVal"].Value
    let dbNullVal = command.Parameters.["dbNullVal"].Value
    Assert.True(isNull nullVal || obj.Equals(nullVal, DBNull.Value))
    Assert.True(isNull dbNullVal || obj.Equals(dbNullVal, DBNull.Value))

[<Fact>]
let ``OracleConnector PrepareSql transforms at-param to colon param syntax`` () =
    let inputSql =
        "SELECT * FROM Employees WHERE DepartmentId = @deptId AND Salary > @minSalary"

    let expectedSql =
        "SELECT * FROM Employees WHERE DepartmentId = :deptId AND Salary > :minSalary"

    let result = OracleConnector.PrepareSql(inputSql)

    Assert.Equal(expectedSql, result)

[<Fact>]
let ``OracleConnector PrepareSql leaves SQL without at-sign unaltered`` () =
    let inputSql =
        "SELECT * FROM Employees WHERE DepartmentId = :deptId AND Status = 'ACTIVE'"

    let result = OracleConnector.PrepareSql(inputSql)

    Assert.Equal(inputSql, result)

[<Fact>]
let ``OracleConnector PrepareSql handles empty or null strings safely`` () =
    Assert.Equal("", OracleConnector.PrepareSql(""))
    Assert.Null(OracleConnector.PrepareSql(null))

// -----------------------------------------------------------------------------
// 6. Relational Descriptors and Factories Tests
// -----------------------------------------------------------------------------

let private testSqlDescriptor
    (descriptor: IConnectorDescriptor)
    (expectedKind: string)
    (expectedName: string)
    (validConnStr: string)
    =
    Assert.Equal(expectedKind, descriptor.KindId)
    Assert.Equal(expectedName, descriptor.DisplayName)
    Assert.Equal(ConnectorCategory.Sql, descriptor.Category)
    Assert.Equal("database", descriptor.Icon)
    Assert.NotEmpty(descriptor.FormFields)
    Assert.True(descriptor.EntityGenerator.IsNone)

    let validJson =
        System.Text.Json.JsonDocument.Parse($$"""{"connectionString": "{{validConnStr}}"}""").RootElement

    Assert.True(descriptor.ValidateConfiguration(validJson).IsOk)

    let invalidJson = System.Text.Json.JsonDocument.Parse("""{}""").RootElement
    Assert.True(descriptor.ValidateConfiguration(invalidJson).IsError)

    let dummyStore =
        { new FSharpQueryStudio.Security.ISecretStore with
            member _.TryRead(_) = Some "resolvedPass"

            member _.TryReadAsync(_, _) =
                System.Threading.Tasks.Task.FromResult(Some "resolvedPass")

            member _.Save _ _ = ()
            member _.Delete _ = ()
        }

    // Factory from string
    let strDoc = System.Text.Json.JsonDocument.Parse($"\"{validConnStr}\"")
    let conn1 = descriptor.Factory.Create(strDoc.RootElement, dummyStore, None)
    Assert.Equal(expectedKind, conn1.Kind.Id)

    // Factory from object with secret reference
    let secretConnStr = validConnStr.Replace("secret", "{password}")

    let objDoc =
        System.Text.Json.JsonDocument.Parse($$"""{"connectionString": "{{secretConnStr}}"}""")

    let conn2 = descriptor.Factory.Create(objDoc.RootElement, dummyStore, Some "ref1")
    Assert.Equal(expectedKind, conn2.Kind.Id)

[<Fact>]
let ``SqlServerDescriptor and SqlServerConnectorFactory coverage`` () =
    testSqlDescriptor
        SqlServerDescriptor.Instance
        "SqlServer"
        "SQL Server"
        "Server=127.0.0.1;Database=db;Password=secret;"

[<Fact>]
let ``PostgreSqlDescriptor and PostgreSqlConnectorFactory coverage`` () =
    testSqlDescriptor
        PostgreSqlDescriptor.Instance
        "PostgreSql"
        "PostgreSQL"
        "Host=127.0.0.1;Database=db;Password=secret;"

[<Fact>]
let ``MySqlDescriptor and MySqlConnectorFactory coverage`` () =
    testSqlDescriptor MySqlDescriptor.Instance "MySql" "MySQL" "Server=127.0.0.1;Database=db;Password=secret;"

[<Fact>]
let ``MariaDbDescriptor and MariaDbConnectorFactory coverage`` () =
    testSqlDescriptor MariaDbDescriptor.Instance "MariaDb" "MariaDB" "Server=127.0.0.1;Database=db;Password=secret;"

[<Fact>]
let ``OracleDescriptor and OracleConnectorFactory coverage`` () =
    testSqlDescriptor OracleDescriptor.Instance "Oracle" "Oracle" "Data Source=127.0.0.1:1521/XE;Password=secret;"
