module FSharpQueryStudio.App.Program

open System
open Avalonia
open Serilog
open FSharpQueryStudio.Logging

let buildAvaloniaApp () =
    AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace()

// WebView2's COM initialization requires the main thread to be STA. F#'s
// [<EntryPoint>] doesn't default to that the way C#'s [STAThread] Main
// does — omitting this throws RPC_E_CHANGED_MODE the first time the
// NativeWebView control attaches.
[<EntryPoint>]
[<STAThread>]
let main argv =
    Log.Logger <- LoggerSetup.build "host" "host" (LoggerSetup.defaultLogsDirectory ())

    try
        Log.Information("F# Query Studio host starting")
        buildAvaloniaApp().StartWithClassicDesktopLifetime(argv)
    finally
        Log.CloseAndFlush()
