module FSharpQueryStudio.App.Program

open Serilog
open FSharpQueryStudio.Logging

[<EntryPoint>]
let main _argv =
    Log.Logger <- LoggerSetup.build "host" "host" (LoggerSetup.defaultLogsDirectory ())

    try
        Log.Information("F# Query Studio host starting")
        0
    finally
        Log.CloseAndFlush()
