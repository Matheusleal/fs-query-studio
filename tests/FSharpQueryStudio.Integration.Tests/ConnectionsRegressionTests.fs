module ConnectionsRegressionTests

open Xunit
open FSharpQueryStudio.Connectors

type private SingleConnectionResolver(name: string, connector: IConnector) =
    interface IConnectionResolver with
        member _.Resolve(requested: string) =
            if requested = name then
                connector
            else
                failwith $"No connection named '{requested}' is configured."

[<Collection("SqlServer container")>]
type ConnectionsSqlServerRegressionTests(fixture: SqlServerConnectorTests.MsSqlContainerFixture) =

    [<Fact>]
    member _.``Connections.sql binds an OR-1-equals-1-shaped parameter as literal data against SQL Server`` () =
        let connector = SqlServerConnector(fixture.ConnectionString)

        connector.ExecuteQuery
            "if object_id('People', 'U') is not null drop table People; create table People (Id int primary key, Name nvarchar(100))"
            []
        |> ignore

        connector.ExecuteQuery "insert into People (Id, Name) values (1, 'Alice'), (2, 'Bob')" []
        |> ignore

        Connections.configure(SingleConnectionResolver("Sales", connector))

        let result =
            Connections.sql "Sales" "select * from People where Name = @name" [ "@name", box "x' OR '1'='1" ]

        Assert.Empty(result.Rows)

[<Collection("PostgreSql container")>]
type ConnectionsPostgreSqlRegressionTests(fixture: PostgreSqlConnectorTests.PostgreSqlContainerFixture) =

    [<Fact>]
    member _.``Connections.sql binds an OR-1-equals-1-shaped parameter as literal data against PostgreSQL`` () =
        let connector = PostgreSqlConnector(fixture.ConnectionString)

        connector.ExecuteQuery "drop table if exists people; create table people (id int primary key, name text)" []
        |> ignore

        connector.ExecuteQuery "insert into people (id, name) values (1, 'Alice'), (2, 'Bob')" []
        |> ignore

        Connections.configure(SingleConnectionResolver("Sales", connector))

        let result =
            Connections.sql "Sales" "select * from people where name = @name" [ "@name", box "x' OR '1'='1" ]

        Assert.Empty(result.Rows)
