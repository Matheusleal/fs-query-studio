module FsacBridgeUriTests

open System
open Xunit
open FSharpQueryStudio.Fsac

// `fsacUriFor` turns a non-`file://` caller identity (the VS Code
// extension's `vscode-notebook-cell:` cell uri, this app's own
// `untitled:ScriptN.fsx`) into a synthetic `file://` uri this module
// controls end to end. These are pure - no FSAC process - so they don't
// perturb the shared-process timing the real-FSAC test classes depend on.

let private notebookCellUri =
    // What the extension actually sends: `analysisDocumentUri(cellUri)` -
    // a `vscode-notebook-cell:` uri with a `#<base64 cell handle>`
    // fragment and `%`-encoded path segments, plus a `.fsx` suffix.
    "vscode-notebook-cell:/d%3A/repo/notebooks/sample.fsqnb#W2sZmlsZQ%3D%3D.fsx"

[<Fact>]
let ``fsacUriFor strips the fragment/percent chars that would break file:// parsing`` () =
    let result = FsacBridge.fsacUriFor notebookCellUri

    Assert.StartsWith("file:///", result)
    // A literal `#` makes a uri parser (FSAC's included) treat everything
    // after it as a fragment - which drops the `.fsx` that marks the
    // buffer as an F# script. A literal `%` is an incomplete escape.
    Assert.DoesNotContain("#", result)
    Assert.DoesNotContain("%", result)

[<Fact>]
let ``fsacUriFor always yields a .fsx extension so FSAC runs script analysis`` () =
    Assert.EndsWith(".fsx", FsacBridge.fsacUriFor notebookCellUri)
    Assert.EndsWith(".fsx", FsacBridge.fsacUriFor "untitled:Script1.fsx")
    // No extension of its own -> still lands as a script.
    Assert.EndsWith(".fsx", FsacBridge.fsacUriFor "untitled:Script1")

[<Fact>]
let ``fsacUriFor is deterministic for the same caller uri`` () =
    Assert.Equal(FsacBridge.fsacUriFor notebookCellUri, FsacBridge.fsacUriFor notebookCellUri)

[<Fact>]
let ``fsacUriFor keeps two different cell handles distinct`` () =
    let a = "vscode-notebook-cell:/d%3A/repo/n.fsqnb#W1sZmlsZQ%3D%3D.fsx"
    let b = "vscode-notebook-cell:/d%3A/repo/n.fsqnb#W2sZmlsZQ%3D%3D.fsx"
    Assert.NotEqual<string>(FsacBridge.fsacUriFor a, FsacBridge.fsacUriFor b)

[<Fact>]
let ``fsacUriFor passes a file:// caller uri through unchanged`` () =
    let uri = "file:///d%3A/repo/loose-script.fsx"
    Assert.Equal(uri, FsacBridge.fsacUriFor uri)
