module PackagingTests

open System
open System.IO
open Xunit

[<Fact>]
let ``the bundled fsautocomplete tool is present in the build output`` () =
    let dllPath = Path.Combine(AppContext.BaseDirectory, "FsacTool", "fsautocomplete.dll")
    Assert.True(File.Exists(dllPath), $"expected {dllPath} to exist — the RestoreFsautocomplete build target should have copied it here")
