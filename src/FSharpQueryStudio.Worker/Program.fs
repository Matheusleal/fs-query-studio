module FSharpQueryStudio.Worker.Program

open System.IO
open Serilog
open FSharpQueryStudio.Logging
open FSharpQueryStudio.Protocol

let rec private loop (stdin: TextReader) (stdout: TextWriter) =
    task {
        let! frame = NdjsonStream.readFrame<WorkerRequestFrame> stdin

        match frame with
        | None -> ()
        | Some request ->
            use _ = Correlation.pushCorrelationId request.Id
            Log.Debug("Handling request {Method}", request.Method)
            let response = Handlers.dispatch request
            do! NdjsonStream.writeFrame stdout response
            do! loop stdin stdout
    }

[<EntryPoint>]
let main _argv =
    Log.Logger <- LoggerSetup.build "worker" "worker" (LoggerSetup.defaultLogsDirectory ())

    try
        Log.Information("F# Query Studio worker starting")
        (loop System.Console.In System.Console.Out).GetAwaiter().GetResult()
        Log.Information("F# Query Studio worker stopping")
        0
    finally
        Log.CloseAndFlush()
