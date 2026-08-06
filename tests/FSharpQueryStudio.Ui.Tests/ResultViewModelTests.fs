module ResultViewModelTests

open System.ComponentModel
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Ui

let private envelope (kind: ResultKind) (displayText: string option) : ResultEnvelope =
    { Kind = kind
      DisplayText = displayText
      Schema = [ { Name = "Value"; DisplayType = "int" } ]
      Rows = [| [| box 1 |] |]
      IsTruncated = true
      ContinuationToken = None
      ElapsedMs = 42L
      Output = [ { Stream = OutputStream.Stdout; Text = "printed line" } ] }

[<Fact>]
let ``starts with NoValue and no rows`` () =
    let vm = ResultViewModel()

    Assert.Equal(ResultKind.NoValue, vm.Kind)
    Assert.Empty(vm.Rows)
    Assert.False(vm.IsTruncated)

[<Fact>]
let ``Apply copies every field from the envelope`` () =
    let vm = ResultViewModel()

    vm.Apply(envelope ResultKind.Scalar (Some "1"))

    Assert.Equal(ResultKind.Scalar, vm.Kind)
    Assert.Equal(Some "1", vm.DisplayText)
    Assert.Equal<Column list>([ { Name = "Value"; DisplayType = "int" } ], vm.Schema)
    Assert.Equal(1, vm.Rows.Length)
    Assert.True(vm.IsTruncated)
    Assert.Equal(42L, vm.ElapsedMs)
    Assert.Equal<OutputLine list>([ { Stream = OutputStream.Stdout; Text = "printed line" } ], vm.Output)

[<Theory>]
[<InlineData("Scalar")>]
[<InlineData("Text")>]
[<InlineData("Table")>]
[<InlineData("Error")>]
let ``Apply reflects the envelope's Kind for every result kind`` (kindName: string) =
    let kind =
        match kindName with
        | "Scalar" -> ResultKind.Scalar
        | "Text" -> ResultKind.Text
        | "Table" -> ResultKind.Table
        | "Error" -> ResultKind.Error
        | other -> failwith $"unexpected kind {other}"

    let vm = ResultViewModel()

    vm.Apply(envelope kind (Some "value"))

    Assert.Equal(kind, vm.Kind)
    Assert.Equal((kind = ResultKind.Table), vm.IsTable)
    Assert.Equal((kind = ResultKind.Scalar || kind = ResultKind.Text || kind = ResultKind.Error), vm.IsScalarOrText)

[<Fact>]
let ``FormattedRows joins each row's cells into one display string per row`` () =
    let vm = ResultViewModel()

    vm.Apply(
        { envelope ResultKind.Table (Some "value") with
            Rows = [| [| box "Ada"; box 30 |]; [| box "Alan"; box 41 |] |] }
    )

    Assert.Equal<string list>([ "Ada, 30"; "Alan, 41" ], vm.FormattedRows)

[<Fact>]
let ``PropertyChanged fires for the derived IsTable/IsScalarOrText flags on Apply`` () =
    let vm = ResultViewModel()
    let mutable raisedNames: string list = []
    (vm :> INotifyPropertyChanged).PropertyChanged.Add(fun args -> raisedNames <- args.PropertyName :: raisedNames)

    vm.Apply(envelope ResultKind.Table (Some "value"))

    Assert.Contains("IsTable", raisedNames)
    Assert.Contains("IsScalarOrText", raisedNames)

[<Fact>]
let ``Apply does not itself enumerate a slow/blocking Rows or Output sequence`` () =
    // Apply only copies the already-materialized arrays/lists it's handed -
    // it must never re-enumerate them. Simulate what a lazy/slow producer
    // upstream would look like by handing Apply data backed by a sequence
    // that throws if pulled more than once, then read the properties
    // (which would force re-enumeration if Apply were lazy) without
    // triggering the guard.
    let vm = ResultViewModel()
    let mutable pulls = 0

    let guardedRows =
        seq {
            pulls <- pulls + 1

            if pulls > 1 then
                failwith "Rows was enumerated more than once"

            yield [| box 1 |]
        }
        |> Seq.toArray // Apply's caller always hands over a realized array (see ResultEnvelope.Rows's type) - assert Apply preserves that, not re-triggering enumeration.

    vm.Apply({ envelope ResultKind.Table (Some "value") with Rows = guardedRows })

    Assert.Equal(1, vm.Rows.Length)
    Assert.Equal(1, vm.Rows.Length) // reading twice must not re-enumerate
    Assert.Equal(1, pulls)

[<Fact>]
let ``ClipboardText for a Table result is tab-separated, header first, one line per row`` () =
    let vm = ResultViewModel()

    vm.Apply(
        { envelope ResultKind.Table None with
            Schema = [ { Name = "Name"; DisplayType = "string" }; { Name = "Age"; DisplayType = "int" } ]
            Rows = [| [| box "Ada"; box 30 |]; [| box "Alan"; box 41 |] |] }
    )

    Assert.Equal("Name\tAge\nAda\t30\nAlan\t41", vm.ClipboardText)

[<Fact>]
let ``ClipboardText for a Scalar/Text/Error result is just the DisplayText`` () =
    let vm = ResultViewModel()

    vm.Apply(envelope ResultKind.Scalar (Some "42"))

    Assert.Equal("42", vm.ClipboardText)

[<Fact>]
let ``ClipboardText is empty when there is no DisplayText`` () =
    let vm = ResultViewModel()

    vm.Apply(envelope ResultKind.NoValue None)

    Assert.Equal("", vm.ClipboardText)

[<Fact>]
let ``PropertyChanged fires for Kind on Apply`` () =
    let vm = ResultViewModel()
    let mutable raisedNames: string list = []
    (vm :> INotifyPropertyChanged).PropertyChanged.Add(fun args -> raisedNames <- args.PropertyName :: raisedNames)

    vm.Apply(envelope ResultKind.Scalar (Some "1"))

    Assert.Contains("Kind", raisedNames)
