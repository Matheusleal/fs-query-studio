module ConnectionNameCompletionTests

open Xunit
open FSharpQueryStudio.Ui

[<Fact>]
let ``cursor right after the opening quote of Connections.sql's first argument is inside a connection-name position`` () =
    let source = "Connections.sql \""
    Assert.True(ConnectionNameCompletion.isInsideConnectionNameArgument source 1 source.Length)

[<Fact>]
let ``cursor mid-way through an already-typed partial connection name is still inside a connection-name position`` () =
    let source = "Connections.csv \"Sa"
    Assert.True(ConnectionNameCompletion.isInsideConnectionNameArgument source 1 source.Length)

[<Fact>]
let ``a call written with parentheses is still recognized`` () =
    let source = "Connections.excel(\""
    Assert.True(ConnectionNameCompletion.isInsideConnectionNameArgument source 1 source.Length)

[<Fact>]
let ``cursor after the closing quote is no longer inside the connection-name argument`` () =
    let source = "Connections.sql \"Sales\""
    Assert.False(ConnectionNameCompletion.isInsideConnectionNameArgument source 1 source.Length)

[<Fact>]
let ``an unrelated string literal does not match`` () =
    let source = "printfn \"hello"
    Assert.False(ConnectionNameCompletion.isInsideConnectionNameArgument source 1 source.Length)

[<Fact>]
let ``cursor still inside the function name, before any quote, does not match`` () =
    let source = "Connections.sql"
    Assert.False(ConnectionNameCompletion.isInsideConnectionNameArgument source 1 source.Length)

[<Fact>]
let ``matches on the requested line of a multi-line document, not just the first line`` () =
    let source = "let x = 1\nConnections.rest \""
    Assert.True(ConnectionNameCompletion.isInsideConnectionNameArgument source 2 18)

[<Fact>]
let ``an out-of-range line number is safely rejected rather than throwing`` () =
    let source = "Connections.sql \""
    Assert.False(ConnectionNameCompletion.isInsideConnectionNameArgument source 5 0)
