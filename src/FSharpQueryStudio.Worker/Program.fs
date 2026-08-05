module FSharpQueryStudio.Worker.Program

open Serilog
open FSharpQueryStudio.Logging

[<EntryPoint>]
let main _argv =
    Log.Logger <- LoggerSetup.build "worker" "worker" (LoggerSetup.defaultLogsDirectory ())

    try
        Log.Information("F# Query Studio worker starting")
        0
    finally
        Log.CloseAndFlush()
