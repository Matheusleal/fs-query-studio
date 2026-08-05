module FSharpQueryStudio.App.Program

open Avalonia
open Serilog
open FSharpQueryStudio.Logging

let buildAvaloniaApp () =
    AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace()

[<EntryPoint>]
let main argv =
    Log.Logger <- LoggerSetup.build "host" "host" (LoggerSetup.defaultLogsDirectory ())

    try
        Log.Information("F# Query Studio host starting")
        buildAvaloniaApp().StartWithClassicDesktopLifetime(argv)
    finally
        Log.CloseAndFlush()
