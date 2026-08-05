module TestHelpers

open System
open System.IO

let tempWorkingDirectory () =
    let dir = Path.Combine(Path.GetTempPath(), "fqs-analysis-tests-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    dir
