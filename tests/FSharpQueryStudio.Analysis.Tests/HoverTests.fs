module HoverTests

open System.IO
open Xunit
open FSharpQueryStudio.Analysis

let private hoverAt (source: string) (line: int) (column: int) =
    let dir = TestHelpers.tempWorkingDirectory ()

    try
        let context = AnalysisContext.build dir
        Hover.hover context source line column |> Async.RunSynchronously
    finally
        Directory.Delete(dir, true)

let private source =
    "/// Adds two numbers together.\nlet add (x: int) (y: int) : int = x + y\n\nlet total = 42\n"

[<Fact>]
let ``hover on a typed let binding returns its display name and inferred type`` () =
    // "total" ends at column 9 (0-based) on line 4: "let total = 42".
    match hoverAt source 4 9 with
    | None -> failwith "expected hover info"
    | Some info ->
        Assert.Equal("total", info.DisplayName)
        Assert.Equal("int", info.Type)

[<Fact>]
let ``hover on a function returns its full signature`` () =
    // "add" ends at column 6 (0-based) on line 2: "let add (x: int) (y: int) : int = x + y".
    match hoverAt source 2 6 with
    | None -> failwith "expected hover info"
    | Some info ->
        Assert.Equal("add", info.DisplayName)
        Assert.Equal(Some "int -> int -> int", info.Signature)

[<Fact>]
let ``hover on a documented symbol returns its XML doc text`` () =
    match hoverAt source 2 6 with
    | None -> failwith "expected hover info"
    | Some info ->
        match info.XmlDoc with
        | None -> failwith "expected XmlDoc to be present"
        | Some text -> Assert.Contains("Adds two numbers together.", text)

[<Fact>]
let ``hover on a symbol with no XML doc gracefully returns None, not an error`` () =
    // "total" has no preceding doc comment.
    match hoverAt source 4 9 with
    | None -> failwith "expected hover info"
    | Some info -> Assert.Equal(None, info.XmlDoc)

[<Fact>]
let ``hover on Connections.sql resolves with no #r or open of its own`` () =
    // Real execution (FsiSession.create) auto-references and opens
    // FSharpQueryStudio.Connectors; live analysis must agree, or hovering
    // a script that runs fine shows nothing. "Connections.sql" ends at
    // column 15 (0-based).
    match hoverAt "Connections.sql" 1 15 with
    | None -> failwith "expected hover info"
    | Some info -> Assert.Equal("sql", info.DisplayName)

[<Fact>]
let ``hover at a position with no resolvable symbol returns None`` () =
    Assert.Equal(None, hoverAt source 3 0)
