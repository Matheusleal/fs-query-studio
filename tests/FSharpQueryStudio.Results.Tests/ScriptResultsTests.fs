module ScriptResultsTests

open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors
open FSharpQueryStudio.Results

[<Fact>]
let ``currentEnvelope is None when nothing has been pushed this evaluation`` () =
    Results.reset ()

    Assert.True((Results.currentEnvelope 0L []).IsNone)

[<Fact>]
let ``raw pushes a real Table envelope with the connector's own column names, and returns its input unchanged`` () =
    Results.reset ()

    let query: ConnectorQueryResult =
        { Columns = [ "id"; "name" ]
          Rows = seq { yield [| box 1; box "Ada" |] } }

    let returned = Results.raw query
    Assert.Same(query, returned)

    let envelope = (Results.currentEnvelope 5L []).Value

    Assert.Equal(ResultKind.Table, envelope.Kind)
    Assert.Equal<string list>([ "id"; "name" ], envelope.Schema |> List.map (fun c -> c.Name))
    Assert.Single(envelope.Rows) |> ignore
    Assert.Equal<obj array>([| box 1; box "Ada" |], envelope.Rows.[0])
    Assert.False(envelope.IsTruncated)
    Assert.Equal(5L, envelope.ElapsedMs)

[<Fact>]
let ``raw fully materializes every row, even past the preview row limit, and never marks IsTruncated`` () =
    Results.reset ()

    let rowCount = Projection.PreviewRowLimit + 10
    let manyRows = seq { for i in 1..rowCount -> [| box i |] }

    let query: ConnectorQueryResult = { Columns = [ "n" ]; Rows = manyRows }

    let returned = Results.raw query
    Assert.Same(query, returned)

    let envelope = (Results.currentEnvelope 0L []).Value

    Assert.Equal(rowCount, envelope.Rows.Length)
    Assert.False(envelope.IsTruncated)

[<Fact>]
let ``text pushes the value's structured-printed form as a Text envelope, and returns its input unchanged`` () =
    Results.reset ()

    let returned = Results.text [ 1; 2; 3 ]
    Assert.Equal<int list>([ 1; 2; 3 ], returned)

    let envelope = (Results.currentEnvelope 0L []).Value

    Assert.Equal(ResultKind.Text, envelope.Kind)
    Assert.Equal(Some "[1; 2; 3]", envelope.DisplayText)

[<Fact>]
let ``reset clears whatever was previously pushed`` () =
    Results.text "leftover from a previous evaluation" |> ignore

    Results.reset ()

    Assert.True((Results.currentEnvelope 0L []).IsNone)

[<Fact>]
let ``a later push replaces an earlier one within the same evaluation`` () =
    Results.reset ()
    Results.text "first" |> ignore
    Results.text "second" |> ignore

    let envelope = (Results.currentEnvelope 0L []).Value

    Assert.Equal(Some "\"second\"", envelope.DisplayText)

[<Fact>]
let ``currentEnvelope carries the Output lines it's given, same as the generic Projection path`` () =
    Results.reset ()
    Results.text "hi" |> ignore

    let output = [ { Stream = OutputStream.Stdout; Text = "printed" } ]
    let envelope = (Results.currentEnvelope 0L output).Value

    Assert.Equal<OutputLine list>(output, envelope.Output)
