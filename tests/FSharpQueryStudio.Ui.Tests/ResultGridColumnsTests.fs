module ResultGridColumnsTests

open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Ui

[<Fact>]
let ``build returns no columns for an empty schema`` () =
    Assert.Empty(ResultGridColumns.build [])

[<Fact>]
let ``build maps each schema column to a header and an indexed binding path, in order`` () =
    let schema =
        [ { Name = "Id"; DisplayType = "int" }
          { Name = "Name"; DisplayType = "string" }
          { Name = "Total"; DisplayType = "decimal" } ]

    let columns = ResultGridColumns.build schema

    Assert.Equal<ResultColumn list>(
        [ { Header = "Id"; BindingPath = "[0]" }
          { Header = "Name"; BindingPath = "[1]" }
          { Header = "Total"; BindingPath = "[2]" } ],
        columns
    )
