module FsacBridgeCompletionTests

open System
open Xunit
open FSharpQueryStudio.Fsac

[<Fact>]
let ``complete after 's.Sub' on a string returns String's members, not a top-level dump`` () =
    task {
        let uri = $"file:///complete-test-{Guid.NewGuid()}.fsx"
        let source = "let s = \"hello\"\ns.Sub"
        // Caret right after "Sub" on line 2 - column 5 (0-based, exclusive end).
        let! items = FsacBridge.complete uri 1 source 2 5

        let labels = items |> List.map (fun i -> i.Label)
        Assert.Contains("Substring", labels)
        let substringItem = items |> List.find (fun i -> i.Label = "Substring")
        Assert.Equal(Some "method", substringItem.Detail)
    }

[<Fact>]
let ``complete on an empty document returns a non-empty top-level scope`` () =
    task {
        let uri = $"file:///complete-test-{Guid.NewGuid()}.fsx"
        let! items = FsacBridge.complete uri 1 "" 1 0
        Assert.NotEmpty(items)
    }

[<Fact>]
let ``complete after record dot returns fields with their types`` () =
    task {
        let uri = $"file:///complete-test-{Guid.NewGuid()}.fsx"
        let source = "type Person = { Name: string; Age: int }\nlet p = { Name = \"Alice\"; Age = 30 }\np."
        let! items = FsacBridge.complete uri 1 source 3 2
        let nameItem = items |> List.find (fun i -> i.Label = "Name")
        let ageItem = items |> List.find (fun i -> i.Label = "Age")
        Assert.Equal(Some "string", nameItem.Detail)
        Assert.Equal(Some "int", ageItem.Detail)
    }
