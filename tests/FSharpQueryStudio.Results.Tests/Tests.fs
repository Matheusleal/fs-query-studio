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
let ``projectWithCursor returns no continuation cursor for a sequence within the preview-row limit`` () =
    let values = [ 1; 2; 3 ] :> obj

    let envelope, cursor = Projection.projectWithCursor values 0L

    Assert.Equal(3, envelope.Rows.Length)
    Assert.False(envelope.IsTruncated)
    Assert.True(cursor.IsNone)

[<Fact>]
let ``projectWithCursor returns a continuation cursor that resumes past the first page for a longer sequence`` () =
    let values = [ 1 .. Projection.PreviewRowLimit + 10 ] :> obj

    let envelope, cursor = Projection.projectWithCursor values 0L

    Assert.Equal(Projection.PreviewRowLimit, envelope.Rows.Length)
    Assert.True(envelope.IsTruncated)

    match cursor with
    | None -> failwith "expected a continuation cursor"
    | Some c ->
        let nextPage, hasMore = c.TakePage(20)
        let expected = [| for i in Projection.PreviewRowLimit + 1 .. Projection.PreviewRowLimit + 10 -> box i |]
        Assert.Equal<obj array>(expected, nextPage)
        Assert.False(hasMore)

[<Fact>]
let ``an exception projects to a structured Error with a non-empty message`` () =
    let ex = System.Exception("boom")

    let envelope = Projection.projectError ex 7L

    Assert.Equal(ResultKind.Error, envelope.Kind)
    Assert.Equal(Some "boom", envelope.DisplayText)
    Assert.Equal(7L, envelope.ElapsedMs)

[<Fact>]
let ``a worker-level error (crash, busy) projects to a structured Error with its message`` () =
    let workerError: WorkerErrorInfo =
        { Code = "worker/crashed"
          Message = "The worker process exited before responding." }

    let envelope = Projection.projectWorkerError workerError

    Assert.Equal(ResultKind.Error, envelope.Kind)
    Assert.Equal(Some "The worker process exited before responding.", envelope.DisplayText)

[<Fact>]
let ``toOutputLines tags stdout and stderr lines separately`` () =
    let lines = Projection.toOutputLines "hi\nthere" "warn1"

    Assert.Equal<OutputLine list>(
        [ { Stream = OutputStream.Stdout; Text = "hi" }
          { Stream = OutputStream.Stdout; Text = "there" }
          { Stream = OutputStream.Stderr; Text = "warn1" } ],
        lines
    )

[<Fact>]
let ``toOutputLines drops a single trailing empty line from a trailing newline`` () =
    let lines = Projection.toOutputLines "hi\n" ""

    Assert.Equal<OutputLine list>([ { Stream = OutputStream.Stdout; Text = "hi" } ], lines)

[<Fact>]
let ``toOutputLines returns an empty list for empty captured output`` () =
    let lines = Projection.toOutputLines "" ""
    Assert.Empty(lines)
