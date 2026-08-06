module MilestoneCAcceptanceTests

open System
open System.IO
open System.Net
open System.Text
open System.Text.Json
open System.Threading
open ClosedXML.Excel
open Microsoft.Data.Sqlite
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors
open FSharpQueryStudio.Results
open FSharpQueryStudio.Ui

type private SingleConnectionResolver(name: string, connector: IConnector) =
    interface IConnectionResolver with
        member _.Resolve(requested: string) =
            if requested = name then
                connector
            else
                failwith $"No connection named '{requested}' is configured."

/// "Displayed" the way a real script's returned table would be: bound
/// into a `ResultViewModel` via a `ResultEnvelope`, the same shape
/// `Projection` produces and `MainWindow`'s DataGrid binds to (C.12).
let private assertDisplayed (columns: string list) (rows: obj array list) =
    let envelope: ResultEnvelope =
        { Kind = ResultKind.Table
          DisplayText = None
          Schema = columns |> List.map (fun name -> { Name = name; DisplayType = "obj" })
          Rows = rows |> Array.ofList
          IsTruncated = false
          ContinuationToken = None
          ElapsedMs = 0L
          Output = [] }

    let vm = ResultViewModel()
    vm.Apply(envelope)
    Assert.True(vm.IsTable)
    Assert.Equal(rows.Length, vm.Rows.Length)
    vm

/// "Exported through bounded results" (C.14): the full row set reaches
/// the CSV, not just whatever a bounded preview would have shown.
let private assertExported (columns: string list) (rows: obj array seq) : string =
    use writer = new StringWriter()
    CsvExport.write writer columns rows
    let text = writer.ToString()
    let lines = text.Split("\r\n") |> Array.filter (fun l -> l <> "")
    Assert.Equal((rows |> Seq.length) + 1, lines.Length) // +1 header
    text

/// Milestone C's acceptance criterion (architecture spec): "Each
/// supported data target can be configured, queried through F# helpers,
/// and displayed/exported through bounded results." One test per
/// connector: a real instance is configured, queried through its
/// `Connections.*` helper (C.11 - the only sanctioned script-facing
/// API), then the result is both displayed (C.12) and exported (C.14).

[<Fact>]
let ``SQLite is configured, queried through Connections.sql, and displayed/exported`` () =
    let path = Path.Combine(Path.GetTempPath(), "fqs-milestone-c-" + Guid.NewGuid().ToString("N") + ".db")

    try
        let connector = SqliteConnector(path)

        connector.ExecuteQuery "create table Orders (Id integer primary key, Total real)" []
        |> fun r -> r.Rows |> Seq.toList |> ignore

        connector.ExecuteQuery "insert into Orders (Id, Total) values (1, 100.0), (2, 50.0)" []
        |> fun r -> r.Rows |> Seq.toList |> ignore

        Connections.configure(SingleConnectionResolver("Sales", connector))

        let result = Connections.sql "Sales" "select Id, Total from Orders order by Id" []
        let rows = result.Rows |> Seq.toList

        assertDisplayed result.Columns rows |> ignore
        let csv = assertExported result.Columns rows
        Assert.Contains("1,100", csv)
        Assert.Contains("2,50", csv)
    finally
        SqliteConnection.ClearAllPools()

        if File.Exists path then
            File.Delete path

[<Collection("SqlServer container")>]
type MilestoneCSqlServerTests(fixture: SqlServerConnectorTests.MsSqlContainerFixture) =

    [<Fact>]
    member _.``SQL Server is configured, queried through Connections.sql, and displayed/exported`` () =
        let connector = SqlServerConnector(fixture.ConnectionString)

        connector.ExecuteQuery
            "if object_id('MilestoneCOrders', 'U') is not null drop table MilestoneCOrders; create table MilestoneCOrders (Id int primary key, Total decimal(10,2))"
            []
        |> fun r -> r.Rows |> Seq.toList |> ignore

        connector.ExecuteQuery "insert into MilestoneCOrders (Id, Total) values (1, 100.0), (2, 50.0)" []
        |> fun r -> r.Rows |> Seq.toList |> ignore

        Connections.configure(SingleConnectionResolver("Sales", connector))

        let result =
            Connections.sql "Sales" "select Id, Total from MilestoneCOrders order by Id" []

        let rows = result.Rows |> Seq.toList

        assertDisplayed result.Columns rows |> ignore
        assertExported result.Columns rows |> ignore

[<Collection("PostgreSql container")>]
type MilestoneCPostgreSqlTests(fixture: PostgreSqlConnectorTests.PostgreSqlContainerFixture) =

    [<Fact>]
    member _.``PostgreSQL is configured, queried through Connections.sql, and displayed/exported`` () =
        let connector = PostgreSqlConnector(fixture.ConnectionString)

        connector.ExecuteQuery
            "drop table if exists milestone_c_orders; create table milestone_c_orders (id int primary key, total numeric(10,2))"
            []
        |> fun r -> r.Rows |> Seq.toList |> ignore

        connector.ExecuteQuery "insert into milestone_c_orders (id, total) values (1, 100.0), (2, 50.0)" []
        |> fun r -> r.Rows |> Seq.toList |> ignore

        Connections.configure(SingleConnectionResolver("Sales", connector))

        let result =
            Connections.sql "Sales" "select id, total from milestone_c_orders order by id" []

        let rows = result.Rows |> Seq.toList

        assertDisplayed result.Columns rows |> ignore
        assertExported result.Columns rows |> ignore

[<Fact>]
let ``CSV is configured, queried through Connections.csv, and displayed/exported`` () =
    let path = Path.Combine(Path.GetTempPath(), "fqs-milestone-c-" + Guid.NewGuid().ToString("N") + ".csv")
    File.WriteAllText(path, "Id,Total\n1,100\n2,50\n")

    try
        let connector = CsvConnector(path)
        Connections.configure(SingleConnectionResolver("Feed", connector))

        let result = Connections.csv "Feed"
        let rows = result.Rows |> Seq.toList

        assertDisplayed result.Columns rows |> ignore
        let csv = assertExported result.Columns rows
        Assert.Contains("1,100", csv)
    finally
        if File.Exists path then
            File.Delete path

[<Fact>]
let ``Excel is configured, queried through Connections.excel, and displayed/exported`` () =
    let path = Path.Combine(Path.GetTempPath(), "fqs-milestone-c-" + Guid.NewGuid().ToString("N") + ".xlsx")

    try
        do
            use workbook = new XLWorkbook()
            let sheet = workbook.Worksheets.Add("Orders")
            sheet.Cell(1, 1).Value <- "Id"
            sheet.Cell(1, 2).Value <- "Total"
            sheet.Cell(2, 1).Value <- 1
            sheet.Cell(2, 2).Value <- 100.0
            sheet.Cell(3, 1).Value <- 2
            sheet.Cell(3, 2).Value <- 50.0
            workbook.SaveAs(path)

        let connector = ExcelConnector(path)
        Connections.configure(SingleConnectionResolver("Book", connector))

        let result = Connections.excel "Book" None
        let rows = result.Rows |> Seq.toList

        assertDisplayed result.Columns rows |> ignore
        assertExported result.Columns rows |> ignore
    finally
        if File.Exists path then
            File.Delete path

[<Fact>]
let ``REST/JSON is configured, queried through Connections.rest, and displayed/exported`` () =
    task {
        let listener = new HttpListener()
        let port = 38999
        listener.Prefixes.Add($"http://127.0.0.1:{port}/")
        listener.Start()

        let serveTask =
            task {
                let! ctx = listener.GetContextAsync()
                let bytes = Encoding.UTF8.GetBytes("""[{"id":1,"total":100},{"id":2,"total":50}]""")
                ctx.Response.ContentType <- "application/json"
                do! ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length)
                ctx.Response.Close()
            }

        try
            let options = RestRequestOptions.Get($"http://127.0.0.1:{port}/orders")
            let connector = RestConnector(options)
            Connections.configure(SingleConnectionResolver("Api", connector))

            let! json = Connections.rest "Api" options CancellationToken.None
            do! serveTask

            let items = json.EnumerateArray() |> Seq.toList
            Assert.Equal(2, items.Length)

            // A script shapes the parsed JSON into tabular rows before it
            // reaches display/export - the same way it would shape any
            // other connector's result.
            let columns = [ "id"; "total" ]

            let rows =
                items
                |> List.map (fun item ->
                    [| box (item.GetProperty("id").GetInt32()); box (item.GetProperty("total").GetInt32()) |])

            assertDisplayed columns rows |> ignore
            let csv = assertExported columns rows
            Assert.Contains("1,100", csv)
        finally
            listener.Stop()
            listener.Close()
    }
