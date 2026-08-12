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

type private Person = { Id: int; Name: string }

[<Fact>]
let ``table on a record sequence infers columns from reflected field names in declaration order`` () =
    Results.reset ()

    let people = [ { Id = 1; Name = "Ada" }; { Id = 2; Name = "Grace" } ]

    let returned = Results.table (box people) None
    Assert.Same(box people, returned)

    let envelope = (Results.currentEnvelope 0L []).Value

    Assert.Equal(ResultKind.Table, envelope.Kind)
    Assert.Equal<string list>([ "Id"; "Name" ], envelope.Schema |> List.map (fun c -> c.Name))
    Assert.Equal(2, envelope.Rows.Length)
    Assert.Equal<obj array>([| box 1; box "Ada" |], envelope.Rows.[0])
    Assert.Equal<obj array>([| box 2; box "Grace" |], envelope.Rows.[1])

[<Fact>]
let ``table on a record sequence with columnNames overrides the headers positionally`` () =
    Results.reset ()

    let people = [ { Id = 1; Name = "Ada" } ]

    Results.table (box people) (Some [ "identifier"; "full name" ]) |> ignore

    let envelope = (Results.currentEnvelope 0L []).Value
    Assert.Equal<string list>([ "identifier"; "full name" ], envelope.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box 1; box "Ada" |], envelope.Rows.[0])

[<Fact>]
let ``table throws naming both counts when columnNames count does not match inferred column count`` () =
    Results.reset ()

    let people = [ { Id = 1; Name = "Ada" } ]

    let ex =
        Assert.Throws<exn>(fun () -> Results.table (box people) (Some [ "only-one" ]) |> ignore)

    Assert.Contains("1", ex.Message)
    Assert.Contains("2", ex.Message)

[<Fact>]
let ``table throws referencing Results.raw when passed a ConnectorQueryResult directly`` () =
    Results.reset ()

    let query: ConnectorQueryResult = { Columns = [ "id" ]; Rows = Seq.empty }

    let ex = Assert.Throws<exn>(fun () -> Results.table (box query) None |> ignore)

    Assert.Contains("Results.raw", ex.Message)

[<Fact>]
let ``table throws a wrap-it error when data is not a collection at all`` () =
    Results.reset ()

    let ex = Assert.Throws<exn>(fun () -> Results.table (box 42) None |> ignore)

    Assert.Contains("wrap it", ex.Message)

[<Fact>]
let ``table on an empty sequence produces zero rows and columns from columnNames if given`` () =
    Results.reset ()

    Results.table (box (List.empty<Person>)) (Some [ "a"; "b" ]) |> ignore
    let withNames = (Results.currentEnvelope 0L []).Value
    Assert.Equal<string list>([ "a"; "b" ], withNames.Schema |> List.map (fun c -> c.Name))
    Assert.Empty(withNames.Rows)

    Results.reset ()
    Results.table (box (List.empty<Person>)) None |> ignore
    let noNames = (Results.currentEnvelope 0L []).Value
    Assert.Empty(noNames.Schema)
    Assert.Empty(noNames.Rows)

[<Fact>]
let ``table returns its input unchanged (pass-through)`` () =
    Results.reset ()

    let people = [ { Id = 1; Name = "Ada" } ]
    let input = box people

    let returned = Results.table input None

    Assert.Same(input, returned)

[<Fact>]
let ``table on a tuple sequence with columnNames maps each element to its own column positionally`` () =
    Results.reset ()

    let pairs = [ (1, "Ada"); (2, "Grace") ]

    Results.table (box pairs) (Some [ "id"; "name" ]) |> ignore

    let envelope = (Results.currentEnvelope 0L []).Value
    Assert.Equal<string list>([ "id"; "name" ], envelope.Schema |> List.map (fun c -> c.Name))
    Assert.Equal<obj array>([| box 1; box "Ada" |], envelope.Rows.[0])
    Assert.Equal<obj array>([| box 2; box "Grace" |], envelope.Rows.[1])

[<Fact>]
let ``table on a tuple sequence without columnNames throws the tuple-specific error`` () =
    Results.reset ()

    let pairs = [ (1, "Ada") ]

    let ex = Assert.Throws<exn>(fun () -> Results.table (box pairs) None |> ignore)

    Assert.Contains("columnNames", ex.Message)
