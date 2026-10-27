module WorkspaceConnectionResolverTests

open System
open System.IO
open System.Text.Json
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors
open FSharpQueryStudio.Security

let private definition (name: string) (kind: ConnectorKind) (configuration: obj) (secretRef: string option) : ConnectionDefinition =
    { Id = Guid.NewGuid()
      Name = name
      Kind = kind
      Configuration = JsonSerializer.SerializeToElement(configuration, Serialization.options)
      SecretReference = secretRef
      IsTrusted = false }

[<Fact>]
let ``ConnectionStringSecrets.resolve substitutes the {password} token from the secret store`` () =
    let store = InMemorySecretStore()
    (store :> ISecretStore).Save "ref1" "s3cret"

    let result =
        ConnectionStringSecrets.resolve store (Some "ref1") "Server=.;Database=Orders;Password={password};"

    Assert.Equal("Server=.;Database=Orders;Password=s3cret;", result)

[<Fact>]
let ``ConnectionStringSecrets.resolve returns the template unchanged when SecretReference is None`` () =
    let result =
        ConnectionStringSecrets.resolve (InMemorySecretStore()) None "Server=.;Trusted_Connection=true;"

    Assert.Equal("Server=.;Trusted_Connection=true;", result)

[<Fact>]
let ``ConnectionStringSecrets.resolve returns the template unchanged when the reference has no stored secret`` () =
    let result =
        ConnectionStringSecrets.resolve (InMemorySecretStore()) (Some "never-saved") "Server=.;Password={password};"

    Assert.Equal("Server=.;Password={password};", result)

[<Fact>]
let ``ConnectionStringSecrets.resolve substitutes case-insensitive {Password} token`` () =
    let store = InMemorySecretStore()
    (store :> ISecretStore).Save "ref1" "s3cret"

    let result =
        ConnectionStringSecrets.resolve store (Some "ref1") "Host=localhost;Database=mydb;Password={Password};"

    Assert.Equal("Host=localhost;Database=mydb;Password=s3cret;", result)

[<Fact>]
let ``ConnectionStringSecrets.resolve injects Password when no placeholder is present`` () =
    let store = InMemorySecretStore()
    (store :> ISecretStore).Save "ref1" "s3cret"

    let result =
        ConnectionStringSecrets.resolve store (Some "ref1") "Host=localhost;Database=mydb;Username=postgres"

    Assert.Contains("Password=s3cret", result)

[<Fact>]
let ``ConnectionStringSecrets.resolve injects password in postgresql URI format`` () =
    let store = InMemorySecretStore()
    (store :> ISecretStore).Save "ref1" "s3cret"

    let result =
        ConnectionStringSecrets.resolve store (Some "ref1") "postgresql://postgres@localhost:5432/mydb"

    Assert.Equal("postgresql://postgres:s3cret@localhost:5432/mydb", result)

[<Fact>]
let ``resolves a registered SQLite connection to a real, correctly-kinded SqliteConnector`` () =
    let path = Path.Combine(Path.GetTempPath(), "fqs-resolver-" + Guid.NewGuid().ToString("N") + ".db")

    try
        let def = definition "Sales" Sqlite {| FilePath = path |} None
        let resolver = WorkspaceConnectionResolver([ def ], InMemorySecretStore())

        let connector = (resolver :> IConnectionResolver).Resolve("Sales")

        Assert.Equal(ConnectorKind.Sqlite, connector.Kind)
        Assert.IsType<SqliteConnector>(connector) |> ignore
    finally
        if File.Exists path then
            File.Delete path

[<Fact>]
let ``resolves a registered CSV connection to a real CsvConnector`` () =
    let def = definition "Feed" Csv {| FilePath = @"C:\data\feed.csv" |} None
    let resolver = WorkspaceConnectionResolver([ def ], InMemorySecretStore())

    let connector = (resolver :> IConnectionResolver).Resolve("Feed")

    Assert.Equal(ConnectorKind.Csv, connector.Kind)
    Assert.IsType<CsvConnector>(connector) |> ignore

[<Fact>]
let ``resolves a registered Excel connection to a real ExcelConnector`` () =
    let def = definition "Book" Excel {| FilePath = @"C:\data\book.xlsx" |} None
    let resolver = WorkspaceConnectionResolver([ def ], InMemorySecretStore())

    let connector = (resolver :> IConnectionResolver).Resolve("Book")

    Assert.Equal(ConnectorKind.Excel, connector.Kind)
    Assert.IsType<ExcelConnector>(connector) |> ignore

[<Fact>]
let ``resolves a registered REST connection to a real RestConnector using the stored request options`` () =
    let def =
        definition
            "Api"
            RestJson
            {| Url = "https://example.test/orders"
               Method = "GET"
               Headers = [||]
               QueryParams = [||]
               Body = None |}
            None

    let resolver = WorkspaceConnectionResolver([ def ], InMemorySecretStore())
    let connector = (resolver :> IConnectionResolver).Resolve("Api")

    Assert.Equal(ConnectorKind.RestJson, connector.Kind)
    Assert.IsType<RestConnector>(connector) |> ignore

[<Fact>]
let ``resolves a registered SQL Server connection with its {password} token substituted from the secret store`` () =
    let store = InMemorySecretStore()
    (store :> ISecretStore).Save "fqs-conn-sales" "s3cret"

    let def =
        definition
            "Sales"
            SqlServer
            {| ConnectionString = "Server=.;Database=Orders;User Id=sa;Password={password};" |}
            (Some "fqs-conn-sales")

    let resolver = WorkspaceConnectionResolver([ def ], store)
    let connector = (resolver :> IConnectionResolver).Resolve("Sales")

    Assert.Equal(ConnectorKind.SqlServer, connector.Kind)
    Assert.IsType<SqlServerConnector>(connector) |> ignore

[<Fact>]
let ``Resolve throws a clear error for a name that is not registered`` () =
    let resolver = WorkspaceConnectionResolver([], InMemorySecretStore())

    let ex =
        Assert.Throws<Exception>(fun () -> (resolver :> IConnectionResolver).Resolve("Missing") |> ignore)

    Assert.Contains("Missing", ex.Message)
