module FSharpQueryStudio.Worker.Program

open System.IO
open System.Threading
open System.Threading.Tasks
open Serilog
open FSharpQueryStudio.Logging
open FSharpQueryStudio.Protocol

// Serializes writes to stdout: execution/evaluate runs on a background
// task (see below) so the read loop can keep accepting a concurrent
// execution/cancel, and that background task's eventual response must not
// interleave with a response the loop itself is writing at the same time.
let private writeLock = new SemaphoreSlim(1, 1)

let private writeResponse (stdout: TextWriter) (response: WorkerResponseFrame) =
    task {
        do! writeLock.WaitAsync()

        try
            do! NdjsonStream.writeFrame stdout response
        finally
            writeLock.Release() |> ignore
    }

let rec private loop (stdin: TextReader) (stdout: TextWriter) =
    task {
        let! frame = NdjsonStream.readFrame<WorkerRequestFrame> stdin

        match frame with
        | None -> ()
        | Some request when request.Method = "execution/evaluate" ->
            // Reserving the slot happens synchronously, right here, before
            // this loop iteration ends — not inside the backgrounded task
            // below. A concurrent execution/cancel can only be read on the
            // *next* iteration, so by construction it always sees the
            // reservation; if it were made inside the Task.Run instead, a
            // freshly started process's threadpool could still be
            // injecting worker threads and the cancel could arrive before
            // that callback had even started, silently no-op'ing against
            // Handlers' still-unset current-working-directory state.
            match Handlers.beginEvaluate request with
            | Choice2Of2 busy ->
                do! writeResponse stdout busy
                do! loop stdin stdout
            | Choice1Of2 workDir ->
                Task.Run(fun () ->
                    (task {
                        use _ = Correlation.pushCorrelationId request.Id
                        Log.Debug("Handling request {Method}", request.Method)
                        let response = Handlers.runEvaluate request workDir
                        do! writeResponse stdout response
                    })
                    :> Task)
                |> ignore

                do! loop stdin stdout
        | Some request when request.Method = "analysis/check" || request.Method = "analysis/hover" ->
            // Backgrounded like execution/evaluate, but for a different
            // reason: analysis requests are stateless (a fresh
            // AnalysisContext per call, no shared mutable state like
            // FsiSession's), so several can run concurrently and complete
            // out of order without corrupting anything - which is exactly
            // what lets a genuinely stale response arrive after a newer
            // one, for the host's version guard (Section 7) to discard.
            Task.Run(fun () ->
                (task {
                    use _ = Correlation.pushCorrelationId request.Id
                    Log.Debug("Handling request {Method}", request.Method)
                    let! response = Handlers.dispatch request
                    do! writeResponse stdout response
                })
                :> Task)
            |> ignore

            do! loop stdin stdout
        | Some request ->
            use _ = Correlation.pushCorrelationId request.Id
            Log.Debug("Handling request {Method}", request.Method)
            let! response = Handlers.dispatch request
            do! writeResponse stdout response
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
