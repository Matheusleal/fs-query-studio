module CsvExportTests

open System
open System.IO
open Microsoft.Data.Sqlite
open Xunit
open FSharpQueryStudio.Connectors
open FSharpQueryStudio.Results

/// A temp SQLite file seeded with more rows than the UI's preview bound,
/// deleted (pool cleared first — see SqliteConnectorTests) when the
/// caller's `use` scope ends.
type private TempSqliteFile(rowCount: int) =
    let path = Path.Combine(Path.GetTempPath(), "fqs-csv-export-" + Guid.NewGuid().ToString("N") + ".db")

    do
        let connector = SqliteConnector(path)

        // ExecuteQuery's Rows seq is what actually opens/closes the
        // connection (see SqliteConnector) - it must be enumerated even
        // for a DDL/DML call with no rows, or the connection leaks until
        // finalized and later file deletion races a still-open handle.
        connector.ExecuteQuery "create table Orders (Id integer primary key, Total real)" []
        |> fun r -> r.Rows |> Seq.toList |> ignore

        let values =
            [ 1..rowCount ] |> List.map (fun i -> $"({i}, {float i})") |> String.concat ", "

        connector.ExecuteQuery $"insert into Orders (Id, Total) values {values}" []
        |> fun r -> r.Rows |> Seq.toList |> ignore

    member _.Path = path

    interface IDisposable with
        member _.Dispose() =
            SqliteConnection.ClearAllPools()

            if File.Exists path then
                File.Delete path

[<Fact>]
let ``CsvExport.write streams a full connector query result to CSV, beyond the preview-row limit`` () =
    let rowCount = Projection.PreviewRowLimit + 300
    use file = new TempSqliteFile(rowCount)
    let connector = SqliteConnector(file.Path)

    let result = connector.ExecuteQuery "select Id, Total from Orders order by Id" []
    use writer = new StringWriter()

    CsvExport.write writer result.Columns result.Rows

    let lines = writer.ToString().Split("\r\n") |> Array.filter (fun l -> l <> "")
    Assert.Equal(rowCount + 1, lines.Length) // +1 header
    Assert.Equal("Id,Total", lines.[0])
    Assert.StartsWith("1,", lines.[1])
    Assert.StartsWith($"{rowCount},", lines.[lines.Length - 1])
