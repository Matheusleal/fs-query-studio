module SqliteConnectorTests

open System
open System.IO
open System.Threading
open Microsoft.Data.Sqlite
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors

[<Fact>]
let ``BindParameters adds every parameter as a real ADO.NET parameter, never touching the SQL text`` () =
    use command = new SqliteCommand()
    let originalSql = "select * from Orders where CustomerId = @customerId and Total > @minTotal"
    command.CommandText <- originalSql

    SqliteConnector.BindParameters(command, [ "@customerId", box 42; "@minTotal", box 100.0 ])

    Assert.Equal(2, command.Parameters.Count)
    Assert.Equal(box 42, command.Parameters.["@customerId"].Value)
    Assert.Equal(box 100.0, command.Parameters.["@minTotal"].Value)
    Assert.Equal(originalSql, command.CommandText)

[<Fact>]
let ``BindParameters binds a null value as DBNull rather than a literal null`` () =
    use command = new SqliteCommand()

    SqliteConnector.BindParameters(command, [ "@note", null ])

    Assert.Equal(box DBNull.Value, command.Parameters.["@note"].Value)

/// A fresh temp SQLite file with one `Orders` table and two rows, deleted
/// when the caller's `use` scope ends.
type private TempSqliteFile() =
    let path = Path.Combine(Path.GetTempPath(), "fqs-sqlite-" + Guid.NewGuid().ToString("N") + ".db")

    do
        use connection = new SqliteConnection(SqliteConnectionStringBuilder(DataSource = path).ToString())
        connection.Open()
        use create = connection.CreateCommand()
        create.CommandText <- "create table Orders (Id integer primary key, CustomerId integer, Total real)"
        create.ExecuteNonQuery() |> ignore
        use insert = connection.CreateCommand()
        insert.CommandText <- "insert into Orders (Id, CustomerId, Total) values (1, 42, 100.0), (2, 43, 50.0)"
        insert.ExecuteNonQuery() |> ignore

    member _.Path = path

    interface IDisposable with
        member _.Dispose() =
            // Microsoft.Data.Sqlite pools connections by default, which
            // keeps a native handle open on `path` past `connection`'s
            // own Dispose - clear the pool first or deleting the file
            // races a still-open handle.
            SqliteConnection.ClearAllPools()

            if File.Exists path then
                File.Delete path

[<Fact>]
let ``ExecuteQuery returns only the rows matching a bound parameter, reading through a live SqliteDataReader`` () =
    use file = new TempSqliteFile()
    let connector = SqliteConnector(file.Path)

    let result =
        connector.ExecuteQuery "select Id, CustomerId, Total from Orders where CustomerId = @customerId" [
            "@customerId", box 42
        ]

    Assert.Equal<string list>([ "Id"; "CustomerId"; "Total" ], result.Columns)
    let rows = result.Rows |> Seq.toList
    Assert.Equal<obj list>([ box 1L; box 42L; box 100.0 ], rows |> List.exactlyOne |> Array.toList)

[<Fact>]
let ``TestConnectionAsync returns true for a reachable SQLite file`` () =
    task {
        use file = new TempSqliteFile()
        let connector = SqliteConnector(file.Path) :> IConnector

        let! connected = connector.TestConnectionAsync(CancellationToken.None)

        Assert.True(connected)
    }

[<Fact>]
let ``DiscoverSchemaAsync reports the Orders table and its columns`` () =
    task {
        use file = new TempSqliteFile()
        let connector = SqliteConnector(file.Path) :> IConnector

        let! tables = connector.DiscoverSchemaAsync(CancellationToken.None)

        let orders = tables |> List.find (fun t -> t.Name = "Orders")
        let columnNames = orders.Columns |> List.map (fun c -> c.Name) |> List.sort
        Assert.Equal<string list>([ "CustomerId"; "Id"; "Total" ], columnNames)
    }

[<Fact>]
let ``SqliteConnector reports its Kind as Sqlite`` () =
    use file = new TempSqliteFile()
    let connector = SqliteConnector(file.Path) :> IConnector

    Assert.Equal(ConnectorKind.Sqlite, connector.Kind)

[<Fact>]
let ``ExecuteQuery rows sequence can be enumerated multiple times without throwing ObjectDisposedException`` () =
    use file = new TempSqliteFile()
    let connector = SqliteConnector(file.Path)

    let result = connector.ExecuteQuery "select Id, CustomerId, Total from Orders" []
    
    // Primeira enumeração: contar elementos
    let count = result.Rows |> Seq.length
    Assert.Equal(2, count)

    // Segunda enumeração: iterar e mapear
    let ids = result.Rows |> Seq.map (fun row -> Convert.ToInt32(row.[0])) |> Seq.toList
    Assert.Equal<int list>([ 1; 2 ], ids)

    // Terceira enumeração com ExecuteReaderStream
    let streaming = connector :> IStreamingQueryableConnector
    let streamResult =
        streaming.ExecuteReaderStream
            "select Id, CustomerId, Total from Orders order by Id"
            []
            (fun record -> record.GetInt32(0))
        |> Seq.toList
    Assert.Equal<int list>([ 1; 2 ], streamResult)

