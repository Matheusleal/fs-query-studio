module CsvExportTests

open System.IO
open Xunit
open FSharpQueryStudio.Results

[<Fact>]
let ``write emits a header row followed by one CSV row per source row`` () =
    use writer = new StringWriter()
    let rows = seq { yield [| box 1; box "Ada" |]; yield [| box 2; box "Alan" |] }

    CsvExport.write writer [ "Id"; "Name" ] rows

    Assert.Equal("Id,Name\r\n1,Ada\r\n2,Alan\r\n", writer.ToString())

[<Fact>]
let ``write quotes a field containing a comma, a quote, or a newline, doubling internal quotes`` () =
    use writer = new StringWriter()

    let rows =
        seq {
            yield [| box "Sales, EMEA"; box "She said \"hi\""; box "line one\nline two" |]
        }

    CsvExport.write writer [ "A"; "B"; "C" ] rows

    Assert.Equal(
        "A,B,C\r\n\"Sales, EMEA\",\"She said \"\"hi\"\"\",\"line one\nline two\"\r\n",
        writer.ToString()
    )

[<Fact>]
let ``write streams every row from a source larger than the preview-row limit, not truncating it`` () =
    use writer = new StringWriter()
    let rowCount = Projection.PreviewRowLimit + 250
    let rows = seq { for i in 1..rowCount -> [| box i |] }

    CsvExport.write writer [ "Value" ] rows

    let lines = writer.ToString().Split("\r\n") |> Array.filter (fun l -> l <> "")
    Assert.Equal(rowCount + 1, lines.Length) // +1 for the header

/// Counts how many times its `GetEnumerator` is called — proves `write`
/// pulls from its source exactly once, the way a connector's live-reader-
/// backed sequence requires (a second enumeration would mean re-running
/// the underlying query).
type private CountingEnumerable(items: obj array list) =
    let mutable enumerations = 0
    member _.Enumerations = enumerations

    interface System.Collections.Generic.IEnumerable<obj array> with
        member _.GetEnumerator() =
            enumerations <- enumerations + 1
            (items :> System.Collections.Generic.IEnumerable<obj array>).GetEnumerator()

    interface System.Collections.IEnumerable with
        member this.GetEnumerator() =
            (this :> System.Collections.Generic.IEnumerable<obj array>).GetEnumerator() :> System.Collections.IEnumerator

[<Fact>]
let ``write enumerates its source exactly once`` () =
    use writer = new StringWriter()
    let source = CountingEnumerable([ [| box 1 |]; [| box 2 |] ])

    CsvExport.write writer [ "Value" ] source

    Assert.Equal(1, source.Enumerations)
    Assert.Equal("Value\r\n1\r\n2\r\n", writer.ToString())
