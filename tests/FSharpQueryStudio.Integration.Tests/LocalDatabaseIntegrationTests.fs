namespace FSharpQueryStudio.Integration.Tests

open System
open System.IO
open System.Threading
open System.Linq
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors
open PostgreSqlConnectorTests
open SqlServerConnectorTests

type LocalUser = { Id: int; Name: string; Age: int }
type LocalPgConnection = { Id: int; Name: string; Active: bool }
type LocalSalesOrder = { Id: int; Amount: decimal; Description: string }

type private InMemoryResolver(map: Map<string, IConnector>) =
    interface IConnectionResolver with
        member _.Resolve(name: string) =
            match Map.tryFind name map with
            | Some c -> c
            | None -> failwith $"Connection '{name}' not found."

module SqliteLocalIntegrationTests =

    [<Fact>]
    [<Trait("Category", "LocalOnly")>]
    let ``SQLite on local disk discovers tables and views and executes LINQ queries`` () =
        let tempDbFile = Path.Combine(Path.GetTempPath(), $"fqs-test-{Guid.NewGuid():N}.db")
        try
            let connector = SqliteConnector(tempDbFile)
            connector.ExecuteQuery "create table users (id integer primary key, name text, age integer)" [] |> ignore
            connector.ExecuteQuery "create view active_users as select id, name, age from users where age >= 18" [] |> ignore
            connector.ExecuteQuery "insert into users values (1, 'Alice', 25), (2, 'Bob', 17), (3, 'Charlie', 30)" [] |> ignore

            // 1. Validar descoberta de tabelas e views
            let asInterface = connector :> IConnector
            let schema = asInterface.DiscoverSchemaAsync(CancellationToken.None).Result

            let tableNames = schema |> List.map (fun t -> t.Name) |> List.sort
            Assert.Contains("users", tableNames)
            Assert.Contains("active_users", tableNames)

            // 2. Validar consulta LINQ tipada
            Connections.configure (InMemoryResolver(Map.ofList [ "LocalSqlite", connector :> IConnector ]))
            let users = Connections.query<LocalUser> "LocalSqlite" "select id, name, age from users" []

            let adults =
                query {
                    for u in users do
                    where (u.Age >= 18)
                    sortBy u.Name
                    select u.Name
                }
                |> Seq.toList

            Assert.Equal<string list>([ "Alice"; "Charlie" ], adults)

            // 3. Validar obtenção de connection string
            let connStr = Connections.getConnectionString "LocalSqlite"
            Assert.Contains(tempDbFile, connStr)
        finally
            if File.Exists(tempDbFile) then
                try File.Delete(tempDbFile) with _ -> ()

[<Collection("PostgreSql container")>]
[<Trait("Category", "RequiresDocker")>]
type PostgreSqlLocalIntegrationTests(fixture: PostgreSqlContainerFixture) =

    [<Fact>]
    member _.``PostgreSQL in Docker discovers multi-schema tables (dbo and public) and executes LINQ queries`` () =
        task {
            let connector = PostgreSqlConnector(fixture.ConnectionString)

            // Criar schemas e tabelas adicionais
            connector.ExecuteQuery "create schema if not exists dbo" [] |> ignore
            connector.ExecuteQuery "drop table if exists dbo.connections; create table dbo.connections (id int primary key, name varchar(100), active boolean)" [] |> ignore
            connector.ExecuteQuery "insert into dbo.connections values (1, 'Backbone_dev', true), (2, 'Legacy_sql', false)" [] |> ignore

            // 1. Validar descoberta multi-schema
            let asInterface = connector :> IConnector
            let! schema = asInterface.DiscoverSchemaAsync(CancellationToken.None)

            let tableNames = schema |> List.map (fun t -> t.Name)
            Assert.Contains("dbo.connections", tableNames)

            let dboConnTable = schema |> List.find (fun t -> t.Name = "dbo.connections")
            let colNames = dboConnTable.Columns |> List.map (fun c -> c.Name) |> List.sort
            Assert.Equal<string list>([ "active"; "id"; "name" ], colNames)

            // 2. Validar consulta LINQ tipada com parâmetro seguro
            Connections.configure (InMemoryResolver(Map.ofList [ "Backbone_dev", connector :> IConnector ]))
            let conns =
                Connections.query<LocalPgConnection>
                    "Backbone_dev"
                    "select id, name, active from dbo.connections where active = @active"
                    [ "@active", box true ]

            let activeNames =
                query {
                    for c in conns do
                    where (c.Id = 1)
                    select c.Name
                }
                |> Seq.toList

            Assert.Equal<string list>([ "Backbone_dev" ], activeNames)

            // 3. Validar obtenção de connection string e objeto de conexão com autocomplete
            let connStr = Connections.getConnectionString "Backbone_dev"
            Assert.Contains("Host=", connStr)

            let dbConn = Connections.getDatabaseConnection "Backbone_dev"
            Assert.Contains("dbo.connections", dbConn.Tables)
            let typedItems = dbConn.Query<LocalPgConnection>("select id, name, active from dbo.connections where id = 1")
            let namesFromConn =
                query {
                    for x in typedItems do
                    select x.Name
                }
                |> Seq.toList
            Assert.Equal<string list>([ "Backbone_dev" ], namesFromConn)
        }

[<Collection("SqlServer container")>]
[<Trait("Category", "RequiresDocker")>]
type SqlServerLocalIntegrationTests(fixture: MsSqlContainerFixture) =

    [<Fact>]
    member _.``SQL Server in Docker discovers non-dbo schema tables and executes LINQ queries`` () =
        task {
            let connector = SqlServerConnector(fixture.ConnectionString)

            // Criar schema 'sales' e tabela 'orders'
            connector.ExecuteQuery "IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'sales') EXEC('CREATE SCHEMA sales')" [] |> ignore
            connector.ExecuteQuery "IF OBJECT_ID('sales.orders', 'U') IS NOT NULL DROP TABLE sales.orders" [] |> ignore
            connector.ExecuteQuery "CREATE TABLE sales.orders (id int primary key, amount decimal(10,2), description nvarchar(100))" [] |> ignore
            connector.ExecuteQuery "INSERT INTO sales.orders VALUES (1, 150.50, 'Laptop'), (2, 25.00, 'Mouse')" [] |> ignore

            // 1. Validar descoberta de tabelas em schema customizado
            let asInterface = connector :> IConnector
            let! schema = asInterface.DiscoverSchemaAsync(CancellationToken.None)

            let tableNames = schema |> List.map (fun t -> t.Name)
            Assert.Contains("sales.orders", tableNames)

            // 2. Validar consulta LINQ tipada
            Connections.configure (InMemoryResolver(Map.ofList [ "SalesDb", connector :> IConnector ]))
            let orders = Connections.query<LocalSalesOrder> "SalesDb" "select id, amount, description from sales.orders" []

            let expensive =
                query {
                    for o in orders do
                    where (o.Amount > 50.0M)
                    select o.Description
                }
                |> Seq.toList

            Assert.Equal<string list>([ "Laptop" ], expensive)

            // 3. Validar obtenção de connection string
            let connStr = Connections.getConnectionString "SalesDb"
            Assert.Contains("Server=", connStr)
        }
