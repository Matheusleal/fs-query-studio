module FSharpQueryStudio.Worker.Program

open System.IO
open System.Text.Json
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

// true  -> the line was a response to a worker-initiated request and has
//          been handed to the channel; the caller must not dispatch it.
// false -> the line has a real `method`; dispatch it as a request.
//
// The reverse channel (F5) multiplexes two frame shapes onto the same
// stdin: host-minted `WorkerRequestFrame`s (always carry `method`) and
// host replies to worker-initiated `HostChannel.Request`s (a
// `WorkerResponseFrame`, no `method`). Peeking the raw JSON for `method`
// before committing to a deserialisation target keeps `readFrame<'T>`'s
// "one shape per line" guarantee intact for every real request.
let private routeResponse (host: HostChannel) (line: string) : bool =
    use doc = JsonDocument.Parse(line)

    let hasMethod =
        match doc.RootElement.TryGetProperty("method") with
        | true, m when m.ValueKind <> JsonValueKind.Null -> true
        | _ -> false

    if hasMethod then
        false
    else
        let response =
            JsonSerializer.Deserialize<WorkerResponseFrame>(line, Serialization.options)

        host.TryComplete response |> ignore
        true

let rec private loopWith (stdin: TextReader) (stdout: TextWriter) (host: HostChannel) =
    task {
        let! line = NdjsonStream.readLine stdin

        match line with
        | None -> ()
        | Some line when routeResponse host line -> do! loopWith stdin stdout host
        | Some line ->
            let request =
                JsonSerializer.Deserialize<WorkerRequestFrame>(line, Serialization.options)

            match request with
            | request when request.Method = "execution/evaluate" ->
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
                    do! loopWith stdin stdout host
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

                    do! loopWith stdin stdout host
            | request when request.Method.StartsWith("analysis/") || request.Method = "results/exportCsv" ->
                // Backgrounded like execution/evaluate, but for a different
                // reason: analysis requests are stateless (a fresh
                // AnalysisContext per call, no shared mutable state like
                // FsiSession's), so several can run concurrently and complete
                // out of order without corrupting anything - which is exactly
                // what lets a genuinely stale response arrive after a newer
                // one, for the host's version guard (Section 7) to discard.
                //
                // Matched by prefix, not by an enumerated pair of method
                // names: every analysis/* method has that same stateless
                // shape, and leaving any of them on the inline path blocks
                // the read loop for the whole duration of a full
                // AnalysisContext.build plus type-check. analysis/complete
                // fires on every keystroke, so an inline dispatch there can
                // stall an incoming execution/cancel past the grace period
                // WorkerClient.Cancel waits before killing the process.
                //
                // results/exportCsv joins this path for the same underlying
                // reason: it only ever touches a ConcurrentDictionary and the
                // filesystem (via CsvExport.write), no shared FSI state, and
                // can write an unbounded amount of data to disk — just as
                // capable of stalling the read loop past execution/cancel's
                // grace period as an inline analysis/* call. results/discard
                // is cheap/synchronous and stays on the inline path below.
                Task.Run(fun () ->
                    (task {
                        use _ = Correlation.pushCorrelationId request.Id
                        Log.Debug("Handling request {Method}", request.Method)

                        // `Handlers.dispatch` for an `analysis/*`/`results/exportCsv`
                        // method can genuinely throw here (unguarded
                        // `JsonElement.GetProperty`/`GetArrayLength` calls against a
                        // possibly-unexpected FSAC response shape, an `IOException`
                        // writing to a dead process's stdin, ...) — this whole
                        // dispatch is fire-and-forget `Task.Run(...) |> ignore`, so
                        // without this try/with an exception here vanishes
                        // silently: no response frame is ever written back, and the
                        // caller just hangs until its own client-side timeout, with
                        // nothing in the log to say why. A structured error
                        // response, same shape as the other error responses this
                        // codebase already returns (e.g. `Handlers.handleConnectionSchema`'s
                        // `connection/schemaFailed`), turns that into a fast,
                        // diagnosable failure instead.
                        try
                            let! response = Handlers.dispatch request
                            do! writeResponse stdout response
                        with ex ->
                            Log.Error(ex, "Unhandled exception dispatching {Method}", request.Method)

                            do!
                                writeResponse
                                    stdout
                                    { Id = request.Id
                                      Result = None
                                      Error = Some { Code = "worker/requestFailed"; Message = ex.Message } }
                    })
                    :> Task)
                |> ignore

                do! loopWith stdin stdout host
            | request ->
                use _ = Correlation.pushCorrelationId request.Id
                Log.Debug("Handling request {Method}", request.Method)
                let! response = Handlers.dispatch request
                do! writeResponse stdout response
                do! loopWith stdin stdout host
    }

/// Module-visible entry to the read loop — F5 tests drive it directly with
/// a `StringReader`/`StringWriter` pair. `main` and the tests both go
/// through here; `loopWith` stays `private` as the tail-recursive worker.
let loop (stdin: TextReader) (stdout: TextWriter) (host: HostChannel) : Task =
    loopWith stdin stdout host :> Task

[<EntryPoint>]
let main _argv =
    Log.Logger <- LoggerSetup.build "worker" "worker" (LoggerSetup.defaultLogsDirectory ())

    try
        Log.Information("F# Query Studio worker starting")

        let stdout = System.Console.Out

        // Generalises `writeResponse`'s lock pattern to any frame: the
        // reverse channel writes `WorkerRequestFrame`s to the same stdout,
        // through the same `writeLock`, so a worker-initiated request never
        // interleaves with a response frame.
        let writeFrameLocked (frame: WorkerRequestFrame) : Task =
            task {
                do! writeLock.WaitAsync()

                try
                    do! NdjsonStream.writeFrame stdout frame
                finally
                    writeLock.Release() |> ignore
            }
            :> Task

        let hostChannel = HostChannel(writeFrameLocked)
        Handlers.configureSecretStore (BridgeSecretStore hostChannel)

        (loop System.Console.In stdout hostChannel).GetAwaiter().GetResult()
        Log.Information("F# Query Studio worker stopping")
        0
    finally
        FSharpQueryStudio.Fsac.FsacBridge.sharedProcess.Shutdown()
        Log.CloseAndFlush()
