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
      Output = [] }

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

[<Fact>]
let ``PropertyChanged fires for Kind on Apply`` () =
    let vm = ResultViewModel()
    let mutable raisedNames: string list = []
    (vm :> INotifyPropertyChanged).PropertyChanged.Add(fun args -> raisedNames <- args.PropertyName :: raisedNames)

    vm.Apply(envelope ResultKind.Scalar (Some "1"))

    Assert.Contains("Kind", raisedNames)
