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
        let def = definition "Sales" ConnectorKind.Sqlite {| FilePath = path |} None
        let resolver = WorkspaceConnectionResolver([ def ], InMemorySecretStore())

        let connector = (resolver :> IConnectionResolver).Resolve("Sales")

        Assert.Equal(ConnectorKind.Sqlite, connector.Kind)
        Assert.IsType<SqliteConnector>(connector) |> ignore
    finally
        if File.Exists path then
            File.Delete path

[<Fact>]
let ``resolves a registered CSV connection to a real CsvConnector`` () =
    let def = definition "Feed" ConnectorKind.Csv {| FilePath = @"C:\data\feed.csv" |} None
    let resolver = WorkspaceConnectionResolver([ def ], InMemorySecretStore())

    let connector = (resolver :> IConnectionResolver).Resolve("Feed")

    Assert.Equal(ConnectorKind.Csv, connector.Kind)
    Assert.IsType<CsvConnector>(connector) |> ignore

[<Fact>]
let ``resolves a registered Excel connection to a real ExcelConnector`` () =
    let def = definition "Book" ConnectorKind.Excel {| FilePath = @"C:\data\book.xlsx" |} None
    let resolver = WorkspaceConnectionResolver([ def ], InMemorySecretStore())

    let connector = (resolver :> IConnectionResolver).Resolve("Book")

    Assert.Equal(ConnectorKind.Excel, connector.Kind)
    Assert.IsType<ExcelConnector>(connector) |> ignore

[<Fact>]
let ``resolves a registered REST connection to a real RestConnector using the stored request options`` () =
    let def =
        definition
            "Api"
            ConnectorKind.RestJson
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
            ConnectorKind.SqlServer
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


// ---------------------------------------------------------------------------
// Relative local file paths are resolved against the workspace project root
// ---------------------------------------------------------------------------

let private withTempWorkspace (f: string -> unit) =
    let root = Path.Combine(Path.GetTempPath(), "fqs-relpath-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(Path.Combine(root, ".fsqs")) |> ignore
    Directory.CreateDirectory(Path.Combine(root, "data")) |> ignore
    try
        f root
    finally
        try Directory.Delete(root, true) with _ -> ()

[<Fact>]
let ``relative CSV path is resolved against the workspace project root and is readable`` () =
    withTempWorkspace (fun root ->
        File.WriteAllText(Path.Combine(root, "data", "feed.csv"), "Id,Name\n1,Alice\n2,Bob\n")
        let def = definition "Feed" ConnectorKind.Csv {| FilePath = "data/feed.csv" |} None
        let resolver = WorkspaceConnectionResolver([ def ], InMemorySecretStore(), root)

        let connector = (resolver :> IConnectionResolver).Resolve("Feed") :?> CsvConnector

        Assert.Equal(Path.GetFullPath(Path.Combine(root, "data", "feed.csv")), connector.FilePath)
        Assert.Equal(2, connector.ReadRows().Rows |> Seq.length))

[<Fact>]
let ``relative path is resolved against the project root when the workspace dir points at the .fsqs folder`` () =
    withTempWorkspace (fun root ->
        let def = definition "Feed" ConnectorKind.Csv {| FilePath = "./data/feed.csv" |} None
        let resolver = WorkspaceConnectionResolver([ def ], InMemorySecretStore(), Path.Combine(root, ".fsqs"))

        let connector = (resolver :> IConnectionResolver).Resolve("Feed") :?> CsvConnector

        Assert.Equal(Path.GetFullPath(Path.Combine(root, "data", "feed.csv")), connector.FilePath))

[<Fact>]
let ``parent-relative Excel path (..) is resolved against the project root`` () =
    withTempWorkspace (fun root ->
        let def = definition "Book" ConnectorKind.Excel {| FilePath = "../shared/book.xlsx" |} None
        let resolver = WorkspaceConnectionResolver([ def ], InMemorySecretStore(), root)

        let connector = (resolver :> IConnectionResolver).Resolve("Book") :?> ExcelConnector

        Assert.Equal(Path.GetFullPath(Path.Combine(root, "..", "shared", "book.xlsx")), connector.FilePath))

[<Fact>]
let ``relative SQLite path creates and opens the database inside the project root, not the process working directory`` () =
    withTempWorkspace (fun root ->
        let def = definition "Local" ConnectorKind.Sqlite {| FilePath = "data/local.db" |} None
        let resolver = WorkspaceConnectionResolver([ def ], InMemorySecretStore(), root)

        let connector = (resolver :> IConnectionResolver).Resolve("Local") :?> SqliteConnector
        let result = connector.ExecuteQuery "select 42 as Answer" []
        Assert.Equal(1, result.Rows |> Seq.length)

        Assert.Contains(Path.GetFullPath(Path.Combine(root, "data", "local.db")), connector.ConnectionString)
        Assert.True(File.Exists(Path.Combine(root, "data", "local.db"))))

[<Fact>]
let ``absolute paths are left untouched when a workspace directory is provided`` () =
    withTempWorkspace (fun root ->
        let absolute = Path.Combine(Path.GetTempPath(), "elsewhere", "feed.csv")
        let def = definition "Feed" ConnectorKind.Csv {| FilePath = absolute |} None
        let resolver = WorkspaceConnectionResolver([ def ], InMemorySecretStore(), root)

        let connector = (resolver :> IConnectionResolver).Resolve("Feed") :?> CsvConnector

        Assert.Equal(absolute, connector.FilePath))

[<Fact>]
let ``non-file fields such as connection strings are never rewritten as paths`` () =
    let fields: ConnectorFormField list =
        [ { Name = "connectionString"; Label = "Conn"; Type = ConnectorFieldType.String; Required = true
            Placeholder = None; DefaultValue = None; SecretHandling = SecretHandling.None } ]
    let config = JsonSerializer.SerializeToElement({| connectionString = "Data Source=relative.db" |})

    let resolved = ConnectorDescriptor.resolveRelativeFilePaths fields config @"C:\ws"

    Assert.Equal("Data Source=relative.db", resolved.GetProperty("connectionString").GetString())

[<Fact>]
let ``resolvePath keeps :memory: and URIs unchanged`` () =
    Assert.Equal(":memory:", ConnectorDescriptor.resolvePath @"C:\ws" ":memory:")
    Assert.Equal("https://example.test/a.csv", ConnectorDescriptor.resolvePath @"C:\ws" "https://example.test/a.csv")
