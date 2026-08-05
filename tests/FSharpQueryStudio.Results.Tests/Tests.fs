module Tests

open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Results

type private Person = { Name: string; Age: int }

[<Fact>]
let ``a primitive value projects to Scalar`` () =
    let envelope = Projection.project (box 42) 5L

    Assert.Equal(ResultKind.Scalar, envelope.Kind)
    Assert.Equal(Some "42", envelope.DisplayText)
    Assert.Equal(5L, envelope.ElapsedMs)

[<Fact>]
let ``a string value projects to Text`` () =
    let envelope = Projection.project (box "hello world") 3L

    Assert.Equal(ResultKind.Text, envelope.Kind)
    Assert.Equal(Some "hello world", envelope.DisplayText)

[<Fact>]
let ``a record projects to a one-row Table with columns matching its fields`` () =
    let value = { Name = "Ada"; Age = 30 }

    let envelope = Projection.project (box value) 0L

    Assert.Equal(ResultKind.Table, envelope.Kind)
    Assert.Equal<string list>([ "Name"; "Age" ], envelope.Schema |> List.map (fun c -> c.Name))
    Assert.Single(envelope.Rows) |> ignore
    Assert.Equal<obj array>([| box "Ada"; box 30 |], envelope.Rows.[0])

[<Fact>]
let ``a sequence projects to a bounded Table preview`` () =
    let values = [ 1; 2; 3 ] :> obj

    let envelope = Projection.project values 0L

    Assert.Equal(ResultKind.Table, envelope.Kind)
    Assert.Equal(3, envelope.Rows.Length)
    Assert.False(envelope.IsTruncated)

[<Fact>]
let ``a sequence longer than the preview-row limit is truncated`` () =
    let values = [ 1 .. Projection.PreviewRowLimit + 1 ] :> obj

    let envelope = Projection.project values 0L

    Assert.Equal(Projection.PreviewRowLimit, envelope.Rows.Length)
    Assert.True(envelope.IsTruncated)

[<Fact>]
let ``an exception projects to a structured Error with a non-empty message`` () =
    let ex = System.Exception("boom")

    let envelope = Projection.projectError ex 7L

    Assert.Equal(ResultKind.Error, envelope.Kind)
    Assert.Equal(Some "boom", envelope.DisplayText)
    Assert.Equal(7L, envelope.ElapsedMs)
