module NotebookCellsTests

open Xunit
open FSharpQueryStudio.Execution
open FSharpQueryStudio.Execution.NotebookCells

[<Fact>]
let ``a script with no boundary markers is a single cell holding everything`` () =
    let cells = NotebookCells.parse "let x = 1\nlet y = 2"

    Assert.Equal<NotebookCells.Cell list>([ { Text = "let x = 1\nlet y = 2"; StartLine = 0 } ], cells)

[<Fact>]
let ``a single bare marker splits the script into two cells`` () =
    let cells = NotebookCells.parse "let x = 1\n// %%\nlet y = 2"

    Assert.Equal<NotebookCells.Cell list>(
        [ { Text = "let x = 1"; StartLine = 0 }
          { Text = "let y = 2"; StartLine = 2 } ],
        cells
    )

[<Fact>]
let ``a titled marker ("// %% Cell 2") is recognized the same as a bare one`` () =
    let cells = NotebookCells.parse "let x = 1\n// %% Cell 2\nlet y = 2"

    Assert.Equal(2, cells.Length)
    Assert.Equal("let x = 1", cells.[0].Text)
    Assert.Equal("let y = 2", cells.[1].Text)

[<Fact>]
let ``three cells split by two markers preserve document order and StartLine`` () =
    let cells = NotebookCells.parse "let a = 1\n// %%\nlet b = 2\n// %%\nlet c = 3"

    Assert.Equal<NotebookCells.Cell list>(
        [ { Text = "let a = 1"; StartLine = 0 }
          { Text = "let b = 2"; StartLine = 2 }
          { Text = "let c = 3"; StartLine = 4 } ],
        cells
    )

[<Fact>]
let ``a leading marker on the document's first line produces no empty first cell`` () =
    let cells = NotebookCells.parse "// %%\nlet x = 1"

    Assert.Equal<NotebookCells.Cell list>([ { Text = "let x = 1"; StartLine = 1 } ], cells)

[<Fact>]
let ``a trailing marker with nothing after it produces no empty trailing cell`` () =
    let cells = NotebookCells.parse "let x = 1\n// %%"

    Assert.Equal<NotebookCells.Cell list>([ { Text = "let x = 1"; StartLine = 0 } ], cells)

[<Fact>]
let ``two consecutive markers with nothing between them produce no empty cell for the gap`` () =
    let cells = NotebookCells.parse "let x = 1\n// %%\n// %%\nlet y = 2"

    Assert.Equal<NotebookCells.Cell list>(
        [ { Text = "let x = 1"; StartLine = 0 }
          { Text = "let y = 2"; StartLine = 3 } ],
        cells
    )

[<Fact>]
let ``an empty source produces no cells`` () =
    Assert.Empty(NotebookCells.parse "")

[<Fact>]
let ``a source that is only a marker produces no cells`` () =
    Assert.Empty(NotebookCells.parse "// %%")

[<Fact>]
let ``a multi-line cell keeps its internal blank lines and indentation intact`` () =
    let cells = NotebookCells.parse "let x = 1\n\n  let y = 2\n// %%\nx + y"

    Assert.Equal("let x = 1\n\n  let y = 2", cells.[0].Text)

[<Fact>]
let ``Windows-style CRLF line endings split cells the same as LF`` () =
    let cells = NotebookCells.parse "let x = 1\r\n// %%\r\nlet y = 2"

    Assert.Equal<NotebookCells.Cell list>(
        [ { Text = "let x = 1"; StartLine = 0 }
          { Text = "let y = 2"; StartLine = 2 } ],
        cells
    )

[<Fact>]
let ``a marker with leading indentation is still recognized as a boundary`` () =
    let cells = NotebookCells.parse "let x = 1\n    // %%\nlet y = 2"

    Assert.Equal(2, cells.Length)
