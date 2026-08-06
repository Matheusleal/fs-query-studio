module ConnectionsTests

open System
open System.IO
open System.Text
open System.Threading
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors

type private FakeResolver(connections: Map<string, IConnector>) =
    interface IConnectionResolver with
        member _.Resolve(name: string) =
            match connections.TryFind name with
            | Some connector -> connector
            | None -> failwith $"No connection named '{name}' is configured."

let private tempSqliteWithOrders () : SqliteConnector =
    let path = Path.Combine(Path.GetTempPath(), "fqs-connections-" + Guid.NewGuid().ToString("N") + ".db")
    let connector = SqliteConnector(path)

    connector.ExecuteQuery "create table Orders (Id integer primary key, Name text)" []
    |> ignore

    connector.ExecuteQuery "insert into Orders (Id, Name) values (1, 'Alice'), (2, 'Bob')" []
    |> ignore

    connector

[<Fact>]
let ``Connections.sql binds an OR-1-equals-1-shaped parameter as literal data, never as SQL syntax`` () =
    let sqlite = tempSqliteWithOrders ()
    Connections.configure(FakeResolver(Map.ofList [ "Sales", sqlite :> IConnector ]))

    // If this parameter were ever concatenated into the SQL text instead
    // of bound, this classic injection payload would make the WHERE
    // clause always true and return every row.
    let result =
        Connections.sql "Sales" "select * from Orders where Name = @name" [ "@name", box "x' OR '1'='1" ]

    Assert.Empty(result.Rows)

[<Fact>]
let ``Connections.sql binds a statement-terminator-shaped parameter as literal data, never executing it`` () =
    let sqlite = tempSqliteWithOrders ()
    Connections.configure(FakeResolver(Map.ofList [ "Sales", sqlite :> IConnector ]))

    Connections.sql "Sales" "select * from Orders where Name = @name" [
        "@name", box "x'; DROP TABLE Orders; --"
    ]
    |> ignore

    // The table must still exist and still hold its original two rows -
    // a real DROP TABLE would have removed it.
    let survivingRows = sqlite.ExecuteQuery "select * from Orders" [] |> fun r -> r.Rows |> Seq.toList
    Assert.Equal(2, survivingRows.Length)

[<Fact>]
let ``Connections.sql routes to the resolved connector and returns its rows for a normal query`` () =
    let sqlite = tempSqliteWithOrders ()
    Connections.configure(FakeResolver(Map.ofList [ "Sales", sqlite :> IConnector ]))

    let result = Connections.sql "Sales" "select Name from Orders where Id = @id" [ "@id", box 1 ]

    Assert.Equal(box "Alice", (result.Rows |> Seq.exactlyOne).[0])

[<Fact>]
let ``Connections.sql fails clearly when the named connection isn't a SQL-capable connector`` () =
    let csvPath = Path.Combine(Path.GetTempPath(), "fqs-connections-" + Guid.NewGuid().ToString("N") + ".csv")
    File.WriteAllText(csvPath, "Id\n1\n")
    let csv = CsvConnector(csvPath)
    Connections.configure(FakeResolver(Map.ofList [ "Feed", csv :> IConnector ]))

    Assert.Throws<Exception>(fun () -> Connections.sql "Feed" "select 1" [] |> ignore)
    |> ignore

[<Fact>]
let ``Connections.csv routes to the resolved CSV connector and reads its rows`` () =
    let path = Path.Combine(Path.GetTempPath(), "fqs-connections-" + Guid.NewGuid().ToString("N") + ".csv")
    File.WriteAllText(path, "Id,Name\n1,Sales\n")
    let csv = CsvConnector(path)
    Connections.configure(FakeResolver(Map.ofList [ "Feed", csv :> IConnector ]))

    let result = Connections.csv "Feed"

    Assert.Equal<string list>([ "Id"; "Name" ], result.Columns)

[<Fact>]
let ``Connections.excel routes to the resolved Excel connector and reads its rows`` () =
    let path = Path.Combine(Path.GetTempPath(), "fqs-connections-" + Guid.NewGuid().ToString("N") + ".xlsx")

    do
        use workbook = new ClosedXML.Excel.XLWorkbook()
        let sheet = workbook.Worksheets.Add("Sheet1")
        sheet.Cell(1, 1).Value <- "Id"
        sheet.Cell(2, 1).Value <- 1
        workbook.SaveAs(path)

    let excel = ExcelConnector(path)
    Connections.configure(FakeResolver(Map.ofList [ "Book", excel :> IConnector ]))

    let result = Connections.excel "Book" None

    Assert.Equal<string list>([ "Id" ], result.Columns)

[<Fact>]
let ``Connections.rest routes to the resolved REST connector and sends the request`` () =
    task {
        let listener = new System.Net.HttpListener()
        let port = 38471
        listener.Prefixes.Add($"http://127.0.0.1:{port}/")
        listener.Start()

        let serveTask =
            task {
                let! ctx = listener.GetContextAsync()
                let bytes = Encoding.UTF8.GetBytes("""{"ok":true}""")
                ctx.Response.ContentType <- "application/json"
                do! ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length)
                ctx.Response.Close()
            }

        let rest = RestConnector(RestRequestOptions.Get($"http://127.0.0.1:{port}/health"))
        Connections.configure(FakeResolver(Map.ofList [ "Api", rest :> IConnector ]))

        let! result = Connections.rest "Api" (RestRequestOptions.Get($"http://127.0.0.1:{port}/health")) CancellationToken.None
        do! serveTask
        listener.Stop()
        listener.Close()

        Assert.True(result.GetProperty("ok").GetBoolean())
    }
